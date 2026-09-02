using System;
using System.Linq;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// Remembers the look on the character, and puts it back on when they next log in.
/// </summary>
/// <remarks>
/// The gap this fills is one nothing else can: Glamourer keeps no state across a restart, so closing
/// the game has always left the wardrobe's own record — <see cref="Configuration.WornItems"/> —
/// describing a character that no longer looks anything like it, which is why that record is cleared
/// on load. The mods, meanwhile, are still enabled, because Penumbra remembers everything. Getting
/// dressed again meant finding the same items in the grid and clicking every one of them a second
/// time.
/// <para>
/// So the look is written down while the game runs rather than reconstructed afterwards. Reading it
/// at load would be too late for half of it: the plain gear filling the slots the wardrobe has
/// nothing in, the dyes, the hat — none of that survives anywhere but in Glamourer, and Glamourer is
/// gone by then.
/// </para>
/// <para>
/// Nothing here is on the critical path of anything. A capture that fails leaves the previous one
/// standing, and a restore that never becomes possible simply never happens.
/// </para>
/// </remarks>
public sealed class LastWornService : IDisposable
{
    private readonly Configuration            _config;
    private readonly WardrobeService          _wardrobe;
    private readonly ScreenshotSessionService _session;
    private readonly IObjectTable             _objects;
    private readonly IClientState             _clientState;
    private readonly IFramework               _framework;
    private readonly IPluginLog               _log;

    public LastWornService(Configuration config, WardrobeService wardrobe,
        ScreenshotSessionService session, IObjectTable objects, IClientState clientState,
        IFramework framework, IPluginLog log)
    {
        _config      = config;
        _wardrobe    = wardrobe;
        _session     = session;
        _objects     = objects;
        _clientState = clientState;
        _framework   = framework;
        _log         = log;

        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;

        // The last word before the plugin goes away, which on a normal shutdown is the most recent
        // thing that will ever be written down. Only useful while the character is still loaded — on
        // the way out of the game they are already gone, and the capture answers null rather than
        // overwriting a good record with an empty one.
        Capture();
    }

    // ── Making the offer ──────────────────────────────────────────────────────

    /// <summary>
    /// The look waiting to be put back on, when it is being offered rather than applied.
    /// </summary>
    /// <remarks>
    /// Read by the wardrobe window, which draws the offer. Held here rather than there because
    /// whether there is anything to offer belongs with the thing that knows what was remembered, and
    /// because the window is usually shut at the moment it becomes true.
    /// </remarks>
    public WornSnapshot? Offer { get; private set; }

    /// <summary>Puts the offered look on and takes the offer down.</summary>
    public void AcceptOffer()
    {
        if (Offer is not { } snapshot) return;
        Offer = null;
        Restore(snapshot);
    }

    /// <summary>Takes the offer down for this login, leaving the record alone.</summary>
    /// <remarks>
    /// Not the same as turning the setting off, and deliberately not remembered: "not right now" is
    /// an answer about this login, and the next one is a different question. The button that means
    /// never is beside it and sets the setting.
    /// </remarks>
    public void DismissOffer() => Offer = null;

    // ── Restoring ─────────────────────────────────────────────────────────────

    /// <summary>Puts a remembered look back on, by hand or on the offer being accepted.</summary>
    public int Restore(WornSnapshot snapshot)
    {
        _restored = true;
        _seenWorn = true;
        return _wardrobe.RestoreLastWorn(snapshot);
    }

    /// <summary>The remembered look, when there is one worth putting back on.</summary>
    public WornSnapshot? Remembered =>
        _config.LastWorn is { IsEmpty: false } snapshot && StillExists(snapshot) ? snapshot : null;

    /// <summary>Forgets what was remembered, so nothing is offered or restored until it is worn again.</summary>
    public void Forget()
    {
        Offer            = null;
        _signature       = string.Empty;
        _config.LastWorn = null;
        _config.Save();
    }

    /// <summary>
    /// Whether anything the snapshot names is still in the wardrobe.
    /// </summary>
    /// <remarks>
    /// A record whose every item has been deleted since would otherwise be offered as a look and
    /// then put nothing on. Vanilla pieces and a design count: those are a look too, and neither
    /// depends on a wardrobe item existing.
    /// </remarks>
    private bool StillExists(WornSnapshot snapshot) =>
        snapshot.Look.VanillaItems.Count > 0 || snapshot.Look.DesignId is not null ||
        snapshot.Look.ItemIds.Any(id => _config.WardrobeItems.Any(i => i.Id == id));

    /// <summary>Set once the look has been put back on, or once it has been decided not to.</summary>
    /// <remarks>
    /// Per login rather than per session: logging out to the title screen and back in leaves a
    /// character in their own clothes again, so the question is genuinely asked afresh.
    /// </remarks>
    private bool _restored;

    /// <summary>When the character will have been loaded long enough to dress, or null if not yet.</summary>
    private DateTime? _settledAt;

    /// <summary>
    /// Whether anything of the wardrobe's has been seen on the character since this login.
    /// </summary>
    /// <remarks>
    /// What separates "they took everything off" from "they have not put anything on yet", which look
    /// identical to a capture and mean opposite things. Only the first is worth forgetting a look
    /// over; the second is every login where the offer is still sat there waiting to be answered, and
    /// forgetting there would quietly throw the look away half a minute after offering it.
    /// </remarks>
    private bool _seenWorn;

    private void OnFrameworkUpdate(IFramework framework)
    {
        // A genuine logout, which is the one thing that starts the question again: the next character
        // to appear comes back in their own clothes, including the same one logging straight back in.
        if (!_clientState.IsLoggedIn)
        {
            _settledAt = null;
            _restored  = false;
            _seenWorn  = false;
            Offer      = null;
            return;
        }

        // Logged in with no character object is a zone change, a cutscene, or the moment before the
        // player appears. Emphatically not a new login — treating it as one would put the whole look
        // back on again after every teleport, which is a Penumbra reload and a visible stutter for
        // something already worn.
        if (_objects.LocalPlayer is not { } player)
        {
            _settledAt = null;
            return;
        }

        // A character is on screen, but nothing else is ready yet: Penumbra is still working through
        // the collection, Glamourer has an actor it has not finished with, and the draw object is
        // about to be rebuilt anyway. Anything applied into that is applied to a body that is
        // replaced a moment later.
        _settledAt ??= DateTime.UtcNow.AddSeconds(Math.Clamp(_config.RestoreLastWornDelay, 0, 60));
        if (DateTime.UtcNow < _settledAt) return;

        var name  = player.Name.TextValue;
        var world = player.HomeWorld.RowId;

        TryRestore(name, world);

        // Not every frame. Capturing reads the character's whole state back out of Glamourer — an
        // IPC call returning JSON that then has to be parsed — and doing that sixty times a second
        // occupies the framework thread the game is drawing on. It is what made scrolling the
        // wardrobe stutter and its pictures slow to appear: the textures load on that same thread.
        //
        // The record only has to survive a crash or a logout, so seconds of granularity cost
        // nothing. Anything worn through the wardrobe itself is written down as it happens by the
        // paths that wear it; this poll is for gear changed in Glamourer directly, where there is
        // nothing to tell us it happened.
        if (_captureTimer.ElapsedMilliseconds < CaptureIntervalMs) return;

        _captureTimer.Restart();
        TryCapture(name, world);
    }

    /// <summary>How often the character's look is read back out of Glamourer.</summary>
    private const int CaptureIntervalMs = 3000;

    private readonly System.Diagnostics.Stopwatch _captureTimer =
        System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Applies or offers the remembered look, once, on the login it belongs to.</summary>
    private void TryRestore(string name, uint world)
    {
        if (_restored || _config.RestoreLastWorn == LastWornRestore.Off) return;

        // Nothing to put back on is a settled question, not one to ask again every frame
        if (Remembered is not { } snapshot)
        {
            _restored = true;
            return;
        }

        if (!snapshot.Matches(name, world))
        {
            // Another character's clothes. Said once rather than acted on, and the record is not
            // thrown away: the character it belongs to may well be the next one to log in.
            _restored = true;
            _log.Information($"[Wardrobe] The remembered look belongs to {snapshot.Character}, " +
                             $"not {name} — leaving it alone.");
            return;
        }

        if (_config.RestoreLastWorn == LastWornRestore.Automatic)
        {
            Restore(snapshot);
            return;
        }

        _restored = true;
        Offer     = snapshot;
        _log.Debug($"[Wardrobe] Offering to put back what {name} was last wearing " +
                   $"({snapshot.Look.ItemIds.Count} item(s))");
    }

    // ── Writing it down ───────────────────────────────────────────────────────

    /// <summary>How often the look is written down.</summary>
    /// <remarks>
    /// Slow, because it is the fallback rather than the main path: most changes to the look are made
    /// through the wardrobe, and this is here to catch the ones that are not — gear swapped in
    /// Glamourer, a dye changed, a hat toggled. Half a minute of that lost to a crash is a fair trade
    /// against several Glamourer state reads a second.
    /// </remarks>
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromSeconds(30);

    private DateTime _lastCapture = DateTime.MinValue;

    /// <summary>What the last capture found, so an unchanged look is not written to disk again.</summary>
    private string _signature = string.Empty;

    private void TryCapture(string name, uint world)
    {
        if (DateTime.UtcNow - _lastCapture < CaptureInterval) return;
        _lastCapture = DateTime.UtcNow;

        Capture(name, world);
    }

    /// <summary>Writes the look down, if it has changed since the last time.</summary>
    /// <remarks>
    /// The comparison is what keeps this off the disk. A config save writes the whole wardrobe out,
    /// which for a large one is not free, and standing in a city wearing the same thing for an hour
    /// should cost nothing at all.
    /// </remarks>
    private void Capture(string? name = null, uint world = 0)
    {
        // A session strips the character between shots and dresses them one item at a time. Every one
        // of those is a look, and none of them is the one anybody was wearing.
        if (_session.State != SessionState.Idle) return;

        if (name == null)
        {
            if (_objects.LocalPlayer is not { } player) return;
            name  = player.Name.TextValue;
            world = player.HomeWorld.RowId;
        }

        // Asked here rather than left to the capture, which cannot tell "nothing is worn" from
        // "Glamourer could not answer" — it reports both as nothing, and only one of them is a
        // reason to throw a good record away.
        if (_config.WornItems.Count == 0 && _wardrobe.ActiveOutfitId == null)
        {
            // Taking everything off is a look too, and remembering it as such is what stops the next
            // login putting back clothes that were deliberately removed before the game was closed.
            // Only once something has actually been seen on the character this login, though — see
            // _seenWorn.
            if (!_seenWorn || _config.LastWorn == null) return;

            Forget();
            _log.Debug("[Wardrobe] Nothing of the wardrobe's is worn any more — forgetting the last look.");
            return;
        }

        _seenWorn = true;

        var snapshot = _wardrobe.CaptureLastWorn(name, world);

        // Could not read the character. Whatever is remembered is older but true, which beats
        // replacing it with what a half-loaded actor had to say.
        if (snapshot == null) return;

        var signature = Signature(snapshot);
        if (signature == _signature) return;

        _signature       = signature;
        _config.LastWorn = snapshot;
        _config.Save();
        _log.Debug($"[Wardrobe] Wrote down what {name} is wearing: {snapshot.Look.ItemIds.Count} item(s), " +
                   $"{snapshot.Look.VanillaItems.Count} vanilla piece(s)");
    }

    /// <summary>
    /// A short string that changes exactly when the look does.
    /// </summary>
    /// <remarks>
    /// Everything a restore would put back on is in it, and nothing else is: the time it was taken
    /// and the character it was taken on are not part of what the look is, and folding them in would
    /// make every capture look like a change and write the config out every half minute for ever.
    /// </remarks>
    private static string Signature(WornSnapshot snapshot)
    {
        var look = snapshot.Look;

        var items   = string.Join(",", look.ItemIds);
        var dyes    = string.Join(",", look.Dyes.OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => $"{d.Key}:{d.Value.Stain1}/{d.Value.Stain2}/{d.Value.Advanced.Count}"));
        var vanilla = string.Join(",", look.VanillaItems.OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => $"{v.Key}:{v.Value.ItemId}/{v.Value.Stain1}/{v.Value.Stain2}"));

        return $"{snapshot.OutfitId}|{look.DesignId}|{look.HatVisible}|{look.WeaponVisible}|" +
               $"{items}|{dyes}|{vanilla}";
    }
}
