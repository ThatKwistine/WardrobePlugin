using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// Keeps the wardrobe in force pointed at whoever is logged in.
/// </summary>
/// <remarks>
/// Only when <see cref="Configuration.PerCharacterWardrobes"/> is on. Off, this watches and does
/// nothing, so a wardrobe that was never split stays exactly as it was.
/// <para>
/// A character it recognises switches the wardrobe outright. One it does not is never guessed at:
/// it raises <see cref="Pending"/> and waits to be told, because the thing being switched is what
/// Strip and Unequip act on, and swapping that under somebody without asking is how a wardrobe
/// ends up looking empty and a character ends up half-dressed.
/// </para>
/// </remarks>
public class WardrobeProfileService
{
    private readonly Configuration   _config;
    private readonly IObjectTable    _objects;
    private readonly IClientState    _clientState;
    private readonly IPluginLog      _log;

    /// <summary>A character with no wardrobe of their own, waiting on an answer.</summary>
    public readonly record struct Unknown(string Name, uint World);

    /// <summary>The character being asked about, or null when there is nothing to ask.</summary>
    public Unknown? Pending { get; private set; }

    /// <summary>Raised when the wardrobe in force changes, so the UI can drop what it cached.</summary>
    public event Action? ProfileChanged;

    /// <summary>
    /// When the character on screen is considered settled enough to act on.
    /// </summary>
    /// <remarks>
    /// The same guard <see cref="LastWornService"/> keeps, and for the same reason: logged in with
    /// no character object is a zone change or a cutscene, and the moments after one appears are
    /// spent with Penumbra still working and the draw object about to be rebuilt.
    /// </remarks>
    private DateTime? _settledAt;

    /// <summary>Who this last acted on, so a settled frame is not re-answered sixty times a second.</summary>
    private string _lastSeen = string.Empty;

    public WardrobeProfileService(Configuration config, IObjectTable objects,
        IClientState clientState, IFramework framework, IPluginLog log)
    {
        _config      = config;
        _objects     = objects;
        _clientState = clientState;
        _log         = log;

        framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework _)
    {
        if (!_config.PerCharacterWardrobes)
        {
            // Turning it off drops any half-asked question rather than leaving it on screen with
            // nothing behind it
            Pending    = null;
            _lastSeen  = string.Empty;
            _settledAt = null;
            return;
        }

        if (!_clientState.IsLoggedIn || _objects.LocalPlayer is not { } player)
        {
            _settledAt = null;
            _lastSeen  = string.Empty;
            return;
        }

        _settledAt ??= DateTime.UtcNow.AddSeconds(2);
        if (DateTime.UtcNow < _settledAt) return;

        var name  = player.Name.TextValue;
        var world = player.HomeWorld.RowId;
        var key   = WardrobeProfile.KeyFor(name, world);

        if (key == _lastSeen) return;
        _lastSeen = key;

        if (_config.ProfileFor(name, world) is { } bound)
        {
            Pending = null;
            SwitchTo(bound, $"logged in as {name}");
            return;
        }

        // Nobody's wardrobe. Ask rather than assume — see the class remarks.
        Pending = new Unknown(name, world);
    }

    /// <summary>Makes a wardrobe the one in force, taking the worn record of the old one with it.</summary>
    /// <remarks>
    /// The outgoing wardrobe's worn list is cleared rather than kept. It described what was on a
    /// character who is no longer the one on screen, so keeping it would have Unequip All walking a
    /// list of items belonging to somebody else's body.
    /// </remarks>
    public void SwitchTo(WardrobeProfile profile, string why)
    {
        if (_config.ActiveProfileId == profile.Id) return;

        _config.ActiveProfile.WornItems.Clear();
        _config.ActiveProfileId = profile.Id;
        _config.Save();

        _log.Information($"[Wardrobe] Wardrobe is now '{profile.Name}' — {why}");
        ProfileChanged?.Invoke();
    }

    /// <summary>Binds the character being asked about to the wardrobe already in force.</summary>
    public void BindPendingToActive()
    {
        if (Pending is not { } who) return;

        _config.ActiveProfile.Bind(who.Name, who.World);
        _config.Save();

        _log.Information($"[Wardrobe] '{_config.ActiveProfile.Name}' is now {who.Name}'s wardrobe");
        Pending = null;
    }

    /// <summary>Starts a wardrobe of their own for the character being asked about.</summary>
    public void CreateForPending()
    {
        if (Pending is not { } who) return;

        var profile = _config.AddProfile(who.Name);
        profile.Bind(who.Name, who.World);

        // Save before the switch, so the new wardrobe exists on disk even if the switch is the
        // thing that goes wrong
        _config.Save();
        SwitchTo(profile, $"new wardrobe for {who.Name}");

        Pending = null;
    }

    /// <summary>Stops asking about this character for now. Asked again on the next login.</summary>
    public void DismissPending() => Pending = null;

    // ── Copying between wardrobes ─────────────────────────────────────────────

    /// <summary>What a copy did, for saying so afterwards.</summary>
    public readonly record struct CopyResult(int Copied, int Skipped);

    /// <summary>
    /// Copies items into another wardrobe as templates to be edited there.
    /// </summary>
    /// <remarks>
    /// A copy rather than a share, because the reason to put a piece in a second character's
    /// wardrobe is usually that it needs to be different there — another size option, another
    /// collection, another material. Two wardrobes pointing at one item would make every such edit
    /// land on both, which is the opposite of what is wanted.
    /// <para>
    /// Links and variant groupings are remapped <i>within the batch</i>: copy an item and its
    /// variants together and they arrive still grouped, while a link to something left behind is
    /// dropped rather than left pointing at an id the target wardrobe has never heard of. That is
    /// why the whole batch is copied first and the references fixed up afterwards.
    /// </para>
    /// <para>
    /// Anything the target already holds a copy of is skipped, so using the menu twice does not
    /// build a wardrobe of duplicates.
    /// </para>
    /// </remarks>
    public CopyResult CopyTo(WardrobeProfile target, IReadOnlyList<WardrobeItem> items)
    {
        // Whatever the target already has a copy of, and whatever it literally already holds — the
        // second catches a wardrobe being copied into itself, which is what a pull from the panel
        // would be if somebody picked the wardrobe they were standing in
        var already = new HashSet<Guid>(
            target.Items.Where(i => i.CopiedFromId.HasValue).Select(i => i.CopiedFromId!.Value));

        foreach (var item in target.Items) already.Add(item.Id);

        var map     = new Dictionary<Guid, Guid>();
        var copies  = new List<WardrobeItem>();
        var skipped = 0;

        foreach (var item in items)
        {
            if (already.Contains(item.Id)) { skipped++; continue; }

            var copy = item.CopyForWardrobe();
            map[item.Id] = copy.Id;
            copies.Add(copy);
        }

        // Second pass: now that every copy has an id, the references between them can be pointed at
        // the copies rather than at the originals
        foreach (var copy in copies)
        {
            var source = items.First(i => i.Id == copy.CopiedFromId);

            if (source.VariantOfId is { } parent && map.TryGetValue(parent, out var newParent))
                copy.VariantOfId = newParent;

            foreach (var link in source.LinkedItemIds)
                if (map.TryGetValue(link, out var newLink))
                    copy.LinkedItemIds.Add(newLink);
        }

        target.Items.AddRange(copies);
        if (copies.Count > 0) _config.Save();

        _log.Information($"[Wardrobe] Copied {copies.Count} item(s) into '{target.Name}'" +
                         (skipped > 0 ? $", skipped {skipped} it already had" : string.Empty));

        return new CopyResult(copies.Count, skipped);
    }

    /// <summary>
    /// Copies outfits into another wardrobe, bringing whatever items they are made of.
    /// </summary>
    /// <remarks>
    /// An outfit is a list of item ids and nothing else, so copying one without its items would
    /// arrive as a name with an empty look behind it. Anything the target already has a copy of is
    /// reused rather than duplicated — copy the items first and then the outfits, and the outfits
    /// find the items that are already there.
    /// </remarks>
    /// <returns>What happened to the outfits. Items brought along are reported separately.</returns>
    public CopyResult CopyOutfitsTo(WardrobeProfile source, WardrobeProfile target,
        IReadOnlyList<Outfit> outfits, out CopyResult items)
    {
        // Every item these outfits are made of, in the source wardrobe
        var needed = outfits
            .SelectMany(o => o.ItemIds)
            .Distinct()
            .Select(id => source.Items.Find(i => i.Id == id))
            .Where(i => i != null)
            .Select(i => i!)
            .ToList();

        items = CopyTo(target, needed);

        // Old item id to new, covering both what was just copied and what was already there from a
        // previous copy — an outfit whose pieces arrived last week must still find them
        var map = new Dictionary<Guid, Guid>();
        foreach (var item in target.Items)
            if (item.CopiedFromId is { } from)
                map[from] = item.Id;

        var already = new HashSet<Guid>(
            target.Outfits.Where(o => o.CopiedFromId.HasValue).Select(o => o.CopiedFromId!.Value));

        foreach (var outfit in target.Outfits) already.Add(outfit.Id);

        var copies  = new List<Outfit>();
        var skipped = 0;

        foreach (var outfit in outfits)
        {
            if (already.Contains(outfit.Id)) { skipped++; continue; }
            copies.Add(outfit.CopyForWardrobe(map));
        }

        target.Outfits.AddRange(copies);
        if (copies.Count > 0 || items.Copied > 0) _config.Save();

        _log.Information($"[Wardrobe] Copied {copies.Count} outfit(s) and {items.Copied} item(s) " +
                         $"into '{target.Name}'");

        return new CopyResult(copies.Count, skipped);
    }


    /// <summary>The character on screen, for the settings panel to offer a binding.</summary>
    /// <remarks>
    /// The player object alone, with no <c>IsLoggedIn</c> beside it. A character object that exists
    /// is a character that is logged in, so the second test could only ever subtract — and what it
    /// subtracted was the binding button, which vanished rather than explaining itself.
    /// </remarks>
    public (string Name, uint World)? CurrentCharacter
    {
        get
        {
            var player = _objects.LocalPlayer;

            return player is null
                ? null
                : (player.Name.TextValue, player.HomeWorld.RowId);
        }
    }
}
