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
        _config.ActiveProfile.WornModsOnly.Clear();
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

    /// <summary>How a copy is to be made, beyond the copying.</summary>
    /// <param name="Tag">A tag to put on every copy, or null for none.</param>
    /// <param name="Pictures">
    /// Whether to copy the picture files into the target wardrobe's own folder, so the copy has
    /// pictures of its own rather than pointing at the source's files. See <see cref="RelocatePictures"/>.
    /// </param>
    public readonly record struct CopyOptions(string? Tag = null, bool Pictures = false);

    /// <summary>
    /// What one wardrobe already holds, indexed so a whole list can be checked against it.
    /// </summary>
    /// <remarks>
    /// Four ways a wardrobe can already have a piece, and the copy offer used to see only the
    /// first:
    /// <list type="bullet">
    /// <item>It holds a copy of the item — <see cref="WardrobeItem.CopiedFromId"/> points at it.</item>
    /// <item>It holds the original the item was copied from. Copy a piece A→B, then pull from B
    /// into A, and the piece in B is A's own item coming home; it must not arrive as a second one.</item>
    /// <item>Both are copies of the same item in a third wardrobe.</item>
    /// <item>It holds the same mod at the same options in the same slot, imported on its own, with
    /// nothing on either item pointing at the other — see <see cref="WardrobeItem.PieceFingerprint"/>.</item>
    /// </list>
    /// The one thing this never does is match by name: two characters can each have a card called
    /// "Summer dress" that are different dresses, and a name is the first thing anybody changes on
    /// a copy.
    /// <para>
    /// An index rather than a scan because the import panel asks about every row of the source
    /// wardrobe, and a fingerprint is a string built from every option of every mod: five hundred
    /// rows against five hundred cards, rebuilt each frame, was the wrong shape entirely. Built
    /// once, the lookups are dictionary reads.
    /// </para>
    /// </remarks>
    public sealed class Presence
    {
        private readonly Dictionary<Guid, WardrobeItem>   _byId         = new();
        private readonly Dictionary<Guid, WardrobeItem>   _byCopiedFrom = new();
        private readonly Dictionary<string, WardrobeItem> _byPrint      = new(StringComparer.Ordinal);

        public Presence(WardrobeProfile target)
        {
            foreach (var held in target.Items) Add(held);
        }

        /// <summary>Counts <paramref name="held"/> as present from now on.</summary>
        /// <remarks>
        /// First one in wins for each key: a wardrobe that already has two of something should
        /// point new arrivals at the one that was there first, rather than at whichever was
        /// indexed last.
        /// </remarks>
        public void Add(WardrobeItem held)
        {
            _byId.TryAdd(held.Id, held);

            if (held.CopiedFromId is { } from) _byCopiedFrom.TryAdd(from, held);
            if (held.PieceFingerprint() is { } print) _byPrint.TryAdd(print, held);
        }

        /// <summary>The item already held that is this piece, or null.</summary>
        public WardrobeItem? Find(WardrobeItem item)
        {
            if (_byId.TryGetValue(item.Id, out var held))         return held;
            if (_byCopiedFrom.TryGetValue(item.Id, out held))     return held;

            if (item.CopiedFromId is { } from)
            {
                if (_byId.TryGetValue(from, out held))            return held;
                if (_byCopiedFrom.TryGetValue(from, out held))    return held;
            }

            return item.PieceFingerprint() is { } print && _byPrint.TryGetValue(print, out held)
                ? held
                : null;
        }
    }

    /// <summary>
    /// The batch, plus whatever in <paramref name="source"/> travels with it: the variants of each
    /// original, and each item's linked partners.
    /// </summary>
    /// <remarks>
    /// One hop, and in the same order the items were given so the caller's first pick stays first.
    /// A variant ticked on its own does not drag its original in: the original is a different
    /// colour of the same mod, not a prerequisite. Links are different — a linked piece is worn
    /// alongside, so a copy that arrives without it is a copy that dresses differently.
    /// </remarks>
    public static List<WardrobeItem> WithCompanions(WardrobeProfile source,
        IReadOnlyList<WardrobeItem> items)
    {
        var seen   = new HashSet<Guid>(items.Select(i => i.Id));
        var result = new List<WardrobeItem>(items);

        foreach (var item in items)
        {
            foreach (var variant in source.Items)
                if (variant.VariantOfId == item.Id && seen.Add(variant.Id))
                    result.Add(variant);

            foreach (var id in item.LinkedItemIds)
                if (source.Items.Find(i => i.Id == id) is { } linked && seen.Add(linked.Id))
                    result.Add(linked);
        }

        return result;
    }

    /// <summary>
    /// Copies items into another wardrobe as templates to be edited there.
    /// </summary>
    /// <remarks>
    /// A copy rather than a share, because the reason to put a piece in a second character's
    /// wardrobe is usually that it needs to be different there — another size option, another
    /// collection, another material. Two wardrobes pointing at one item would make every such edit
    /// land on both, which is the opposite of what is wanted.
    /// <para>
    /// Links and variant groupings are remapped onto the target: to the copy made in this batch
    /// when the other item came too, and to the piece the target already had when it did — so a
    /// variant brought over a week after its original still folds under it. A reference to
    /// something the target has never seen is dropped rather than left pointing at an id it
    /// cannot resolve. That is why the whole batch is copied first and the references fixed up
    /// afterwards.
    /// </para>
    /// <para>
    /// Anything the target already holds — by any of the readings <see cref="Presence"/> lists —
    /// is skipped, so using the menu twice does not build a wardrobe of duplicates.
    /// </para>
    /// </remarks>
    public CopyResult CopyTo(WardrobeProfile target, IReadOnlyList<WardrobeItem> items,
        CopyOptions options = default)
    {
        // Old id to the id it means in the target: a fresh copy, or the piece that was already
        // there. Both are somewhere a link or a variant grouping can legitimately point.
        var present = new Presence(target);
        var map     = new Dictionary<Guid, Guid>();
        var copies  = new List<WardrobeItem>();
        var skipped = 0;

        foreach (var item in items)
        {
            if (map.ContainsKey(item.Id)) continue;

            if (present.Find(item) is { } held)
            {
                map[item.Id] = held.Id;
                skipped++;
                continue;
            }

            var copy = item.CopyForWardrobe();
            map[item.Id] = copy.Id;
            copies.Add(copy);

            // Indexed as it is made rather than at the end, so a batch holding the same piece
            // twice — an item and a variant that has come to hold the same options — is caught
            // by the fingerprint on the second rather than copied twice
            target.Items.Add(copy);
            present.Add(copy);

            Finish(copy, copy.Tags, target, options);
        }

        // Second pass: now that every copy has an id, the references between them can be pointed at
        // the copies rather than at the originals
        foreach (var copy in copies)
        {
            var source = items.First(i => i.Id == copy.CopiedFromId);

            if (source.VariantOfId is { } parent && map.TryGetValue(parent, out var newParent))
                copy.VariantOfId = newParent;

            foreach (var link in source.LinkedItemIds)
                if (map.TryGetValue(link, out var newLink) && !copy.LinkedItemIds.Contains(newLink))
                    copy.LinkedItemIds.Add(newLink);
        }

        // A link is a pair: the piece that was already there should know about the newcomer too,
        // or the newcomer drags it in while it never drags the newcomer
        foreach (var copy in copies)
            foreach (var partner in copy.LinkedItemIds)
                if (target.Items.Find(i => i.Id == partner) is { } held &&
                    !held.LinkedItemIds.Contains(copy.Id))
                    held.LinkedItemIds.Add(copy.Id);

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
    /// arrive as a name with an empty look behind it. Anything the target already has — by any of
    /// the readings <see cref="Presence"/> lists — is reused rather than duplicated: copy the
    /// items first and then the outfits, and the outfits find the items that are already there.
    /// </remarks>
    /// <returns>What happened to the outfits. Items brought along are reported separately.</returns>
    public CopyResult CopyOutfitsTo(WardrobeProfile source, WardrobeProfile target,
        IReadOnlyList<Outfit> outfits, out CopyResult items, CopyOptions options = default)
    {
        // Every item these outfits are made of, in the source wardrobe
        var needed = outfits
            .SelectMany(o => o.ItemIds)
            .Distinct()
            .Select(id => source.Items.Find(i => i.Id == id))
            .Where(i => i != null)
            .Select(i => i!)
            .ToList();

        items = CopyTo(target, needed, options);

        // Old item id to new, resolved the same way the copy resolved it — so an outfit whose
        // pieces arrived last week, or were imported there on their own, still finds them
        var present = new Presence(target);
        var map     = new Dictionary<Guid, Guid>();
        foreach (var item in needed)
            if (present.Find(item) is { } held)
                map[item.Id] = held.Id;

        var copies  = new List<Outfit>();
        var skipped = 0;

        foreach (var outfit in outfits)
        {
            if (OutfitAlreadyIn(target, outfit)) { skipped++; continue; }

            var copy = outfit.CopyForWardrobe(map);
            Finish(copy, copy.Tags, target, options);
            copies.Add(copy);
        }

        target.Outfits.AddRange(copies);
        if (copies.Count > 0 || items.Copied > 0) _config.Save();

        _log.Information($"[Wardrobe] Copied {copies.Count} outfit(s) and {items.Copied} item(s) " +
                         $"into '{target.Name}'");

        return new CopyResult(copies.Count, skipped);
    }

    /// <summary>The tag and the pictures, which item and outfit copies want alike.</summary>
    private void Finish(IImageOwner copy, List<string> tags, WardrobeProfile target, CopyOptions options)
    {
        if (options.Tag is { } tag && !string.IsNullOrWhiteSpace(tag) &&
            !tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase))
            tags.Add(tag.Trim());

        if (options.Pictures) RelocatePictures(copy, target);
    }

    /// <summary>
    /// Gives a copy pictures of its own, in the target wardrobe's folder.
    /// </summary>
    /// <remarks>
    /// By default a copy points at the same files as its original — the picture is of the piece,
    /// and the piece is the same piece. That stops being true the moment the copy is re-shot for
    /// the other character, and it was never true of the folder: each wardrobe has one so that a
    /// character's pictures sit together, and a copy whose pictures live in somebody else's folder
    /// is a copy that vanishes from the image browser and breaks when that folder is tidied.
    /// <para>
    /// Only when the target has a folder of its own that exists; without one there is nowhere of
    /// its own to put them, and the shared path is the honest answer. A file that cannot be copied
    /// keeps its old path rather than losing the picture — a card with the source's picture beats
    /// a card with none.
    /// </para>
    /// <para>
    /// Only the copy's paths change. The originals, and their files, are not touched.
    /// </para>
    /// </remarks>
    public void RelocatePictures(IImageOwner copy, WardrobeProfile target)
    {
        var folder = target.ImagesFolder;
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return;

        if (copy.ImagePath is { } cover) copy.ImagePath = Relocate(cover, folder);

        for (var i = 0; i < copy.ExtraImages.Count; i++)
            copy.ExtraImages[i] = Relocate(copy.ExtraImages[i], folder);
    }

    /// <summary>One file into the folder, under a name nothing there already has; the old path when it cannot.</summary>
    private string Relocate(string path, string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return path;

            // Already in the folder — its own picture, or one relocated by an earlier copy
            var here = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (string.Equals(here, System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'),
                              StringComparison.OrdinalIgnoreCase))
                return path;

            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var ext  = System.IO.Path.GetExtension(path);
            var dest = System.IO.Path.Combine(folder, name + ext);

            for (var n = 2; System.IO.File.Exists(dest); n++)
                dest = System.IO.Path.Combine(folder, $"{name}_{n}{ext}");

            System.IO.File.Copy(path, dest);
            return dest;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"[Wardrobe] Could not copy picture '{path}' into '{folder}'; the copy keeps the original's path");
            return path;
        }
    }

    /// <summary>
    /// Whether <paramref name="target"/> already holds this outfit — a copy of it, the original it
    /// was copied from, or another copy of the same original.
    /// </summary>
    public static bool OutfitAlreadyIn(WardrobeProfile target, Outfit outfit) =>
        target.Outfits.Any(o => o.Id == outfit.Id
                             || o.CopiedFromId == outfit.Id
                             || (outfit.CopiedFromId is { } from &&
                                 (o.Id == from || o.CopiedFromId == from)));

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
