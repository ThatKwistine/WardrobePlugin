using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// Writes and reads <c>.wardrobe</c> share bundles — a wardrobe, or part of one, in a single file
/// that can be handed to somebody else.
/// </summary>
/// <remarks>
/// A bundle is a zip holding one <see cref="WardrobeShare.ManifestName"/> and an
/// <see cref="WardrobeShare.ImageFolder"/> of pictures. Nothing else, and in particular no mod files:
/// see <see cref="WardrobeShare"/> for why that line is where it is.
/// <para>
/// Everything read here came from another person, so it is treated as hostile input rather than as
/// data this plugin wrote. Archive entry names are rewritten rather than trusted, sizes are capped,
/// and a manifest that does not parse fails the load instead of half-populating a wardrobe. The
/// threat is not really malice — it is that a corrupt file handed round a Discord should say so
/// plainly rather than producing a wardrobe full of wrong entries.
/// </para>
/// </remarks>
public class WardrobeShareService
{
    /// <summary>Largest single picture accepted out of a bundle.</summary>
    /// <remarks>
    /// A wardrobe screenshot is a cropped PNG of a few hundred kilobytes; 16MB is far past anything
    /// legitimate and well short of what would matter on disk. The cap exists so an archive claiming
    /// a 40GB entry is refused at the entry rather than discovered when the drive fills.
    /// </remarks>
    private const long MaxImageBytes = 16L * 1024 * 1024;

    /// <summary>Largest manifest accepted. Generous for a wardrobe of thousands; not unbounded.</summary>
    private const long MaxManifestBytes = 64L * 1024 * 1024;

    /// <summary>Largest number of pictures taken out of one bundle.</summary>
    private const int MaxImages = 20_000;

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    private static readonly string[] AllowedImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    private readonly PenumbraIpc _penumbra;
    private readonly IPluginLog  _log;

    public WardrobeShareService(PenumbraIpc penumbra, IPluginLog log)
    {
        _penumbra = penumbra;
        _log      = log;
    }

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Writes the given items to a bundle at <paramref name="path"/>, returning what happened for
    /// the UI to report.
    /// </summary>
    /// <param name="includeImages">
    /// Whether pictures travel with the bundle. Off makes a file of a few dozen kilobytes that still
    /// carries every item and every mod requirement — the wardrobe is fully browsable, just without
    /// the photographs — which is the difference between something that fits in a Discord message
    /// and something that does not.
    /// </param>
    /// <param name="outfits">
    /// Outfits to send. Every item they name is added to the export whether or not it was ticked —
    /// an outfit is a list of references, and one arriving without its pieces is a list of nothing.
    /// </param>
    public ExportResult Export(
        IReadOnlyList<WardrobeItem>   items,
        IReadOnlyList<Outfit>         outfits,
        IReadOnlyList<WardrobeItem>   allItems,
        string   path,
        string   author,
        string   description,
        bool     includeImages)
    {
        var (toSend, pulledIn) = WithOutfitPieces(items, outfits, allItems);

        if (toSend.Count == 0 && outfits.Count == 0)
            return ExportResult.Failed("Nothing selected to share.");

        try
        {
            var share = new WardrobeShare
            {
                PluginVersion = typeof(WardrobeShareService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                ExportedBy    = author.Trim(),
                Description   = description.Trim(),
                ExportedUtc   = DateTime.UtcNow,
            };

            // Only links between items that are both in this export survive — see
            // SharedItem.LinkedSourceIds
            var exported = toSend.Select(i => i.Id).ToHashSet();

            // One archive entry per distinct picture on disk, however many items point at it: a
            // wardrobe where twenty variants share one cover should not carry twenty copies of it
            var imageEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missingImages = 0;

            foreach (var item in toSend)
                share.Items.Add(ToShared(item, exported, includeImages, imageEntries, ref missingImages));

            foreach (var outfit in outfits)
                share.Outfits.Add(ToShared(outfit, exported, includeImages, imageEntries, ref missingImages));

            // Written to a temporary file and moved into place, so a failure halfway through leaves
            // whatever was already at that path alone rather than truncated
            var temp = path + ".tmp";

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = zip.CreateEntry(WardrobeShare.ManifestName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                    writer.Write(JsonSerializer.Serialize(share, WriteOpts));

                foreach (var (source, entryName) in imageEntries)
                {
                    try
                    {
                        // Already compressed formats gain nothing from being deflated again and cost
                        // real time on a wardrobe of hundreds
                        var level = IsAlreadyCompressed(source) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                        var entry = zip.CreateEntry($"{WardrobeShare.ImageFolder}/{entryName}", level);

                        using var src = File.OpenRead(source);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                    }
                    catch (Exception ex)
                    {
                        // One unreadable picture is not worth losing the whole export over. The item
                        // still travels; it arrives without that photograph.
                        _log.Warning(ex, $"[Wardrobe] Share export skipped picture '{source}'");
                        missingImages++;
                    }
                }
            }

            File.Move(temp, path, true);

            var size = new FileInfo(path).Length;
            _log.Information($"[Wardrobe] Shared {share.Items.Count} item(s) and {share.Outfits.Count} " +
                             $"outfit(s) to {path} ({Describe(size)}).");

            return ExportResult.Succeeded(share.Items.Count, share.Outfits.Count,
                imageEntries.Count, missingImages, size, pulledIn);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Wardrobe] Share export failed");
            return ExportResult.Failed($"Could not write the file: {ex.Message}");
        }
    }

    /// <summary>
    /// The ticked items plus every item the ticked outfits need, deduplicated, with a count of how
    /// many were pulled in that nobody ticked.
    /// </summary>
    /// <remarks>
    /// Done here rather than in the UI so it cannot be forgotten by a second caller. An outfit is a
    /// list of item ids; sending one without the items behind it produces a bundle where the outfit
    /// arrives empty and there is nothing on screen to explain why.
    /// <para>
    /// The count is reported back so the export can say what it did. Quietly sending items somebody
    /// did not tick would be a worse surprise than the alternative, given the whole point of the
    /// ticks is to control what leaves.
    /// </para>
    /// </remarks>
    private static (List<WardrobeItem> ToSend, int PulledIn) WithOutfitPieces(
        IReadOnlyList<WardrobeItem> items,
        IReadOnlyList<Outfit>       outfits,
        IReadOnlyList<WardrobeItem> allItems)
    {
        var seen   = new HashSet<Guid>();
        var toSend = new List<WardrobeItem>();

        foreach (var item in items)
            if (seen.Add(item.Id)) toSend.Add(item);

        var ticked   = seen.Count;
        var byId     = allItems.ToDictionary(i => i.Id);

        foreach (var outfit in outfits)
        {
            foreach (var id in outfit.ItemIds)
            {
                // An outfit can name an item deleted since it was saved — the wear path already
                // skips those, and so does this
                if (!byId.TryGetValue(id, out var piece)) continue;
                if (seen.Add(id)) toSend.Add(piece);
            }
        }

        return (toSend, seen.Count - ticked);
    }

    private SharedOutfit ToShared(
        Outfit outfit,
        HashSet<Guid> exported,
        bool includeImages,
        Dictionary<string, string> imageEntries,
        ref int missingImages)
    {
        var shared = new SharedOutfit
        {
            SourceId      = outfit.Id,
            Name          = outfit.Name,
            Tags          = new List<string>(outfit.Tags),
            HatVisible    = outfit.HatVisible,
            WeaponVisible = outfit.WeaponVisible,
            Origin        = outfit.IsGlamourPlate ? SharedOutfitOrigin.GlamourPlate
                          : outfit.IsDesign       ? SharedOutfitOrigin.DesignCard
                          :                         SharedOutfitOrigin.Normal,
        };

        // Only pieces actually travelling. WithOutfitPieces has already made sure that is all of
        // them bar any deleted since, so this drops nothing a sender would miss.
        foreach (var id in outfit.ItemIds)
            if (exported.Contains(id)) shared.ItemSourceIds.Add(id);

        // The sender's local item ids are the dye keys, and they are also the source ids — the same
        // guid means both things until it lands on the other side, where the import rewrites them
        foreach (var (key, dye) in outfit.Dyes)
        {
            if (!Guid.TryParse(key, out var itemId) || !exported.Contains(itemId)) continue;

            shared.Dyes[key] = new OutfitDye
            {
                Stain1   = dye.Stain1,
                Stain2   = dye.Stain2,
                Advanced = new Dictionary<string, string>(dye.Advanced),
            };
        }

        foreach (var (slot, piece) in outfit.VanillaItems)
        {
            shared.VanillaItems[slot] = new VanillaPiece
            {
                ItemId = piece.ItemId,
                Name   = piece.Name,
                Stain1 = piece.Stain1,
                Stain2 = piece.Stain2,
            };
        }

        if (includeImages)
        {
            var (cover, extras) = AttachImages(outfit, outfit.Id, imageEntries, ref missingImages);
            shared.ImageFile       = cover;
            shared.ExtraImageFiles = extras;
        }

        return shared;
    }

    /// <summary>
    /// Files an owner's pictures into the archive's entry map and returns the names it should
    /// reference them by.
    /// </summary>
    /// <remarks>
    /// Shared by items and outfits, which hold pictures identically — both are
    /// <see cref="IImageOwner"/>, and the whole point of that interface is that this only needs
    /// writing once.
    /// </remarks>
    private (string? Cover, List<string> Extras) AttachImages(
        IImageOwner owner,
        Guid        ownerId,
        Dictionary<string, string> imageEntries,
        ref int     missingImages)
    {
        string?     cover  = null;
        var         extras = new List<string>();
        var         index  = 0;

        foreach (var picture in owner.AllImages())
        {
            if (imageEntries.Count >= MaxImages) break;

            if (!File.Exists(picture)) { missingImages++; index++; continue; }

            if (!imageEntries.TryGetValue(picture, out var entryName))
            {
                var ext = Path.GetExtension(picture);
                if (!AllowedImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) { index++; continue; }

                entryName = $"{ownerId:N}-{index}{ext.ToLowerInvariant()}";
                imageEntries[picture] = entryName;
            }

            if (cover == null && index == 0)      cover = entryName;
            else if (!extras.Contains(entryName)) extras.Add(entryName);

            index++;
        }

        // AllImages puts the cover first, but one whose file is missing while the extras survive
        // would otherwise arrive with no cover at all and pictures in the gallery
        if (cover == null && extras.Count > 0)
        {
            cover = extras[0];
            extras.RemoveAt(0);
        }

        return (cover, extras);
    }

    private SharedItem ToShared(
        WardrobeItem item,
        HashSet<Guid> exported,
        bool includeImages,
        Dictionary<string, string> imageEntries,
        ref int missingImages)
    {
        var shared = new SharedItem
        {
            SourceId          = item.Id,
            Name              = item.Name,
            Slot              = item.Slot,
            Notes             = item.Notes,
            Tags              = new List<string>(item.Tags),
            GlamourerItemId   = item.GlamourerItemId,
            GlamourerItemName = item.GlamourerItemName,
            ModelSetId        = item.ModelSetId,
            HairIdByRace      = new Dictionary<string, ushort>(item.HairIdByRace),
            Replaces          = item.Replaces,
            Layer             = item.Layer,
            ForceRedraw       = item.ForceRedraw,
            LinkedSourceIds   = item.LinkedItemIds.Where(exported.Contains).ToList(),
        };

        foreach (var (race, ids) in item.CustomizeIdsByRace)
            shared.CustomizeIdsByRace[race] = new List<ushort>(ids);

        foreach (var mod in item.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.ModDirectory) && string.IsNullOrWhiteSpace(mod.ModName))
                continue;

            var copy = new SharedMod
            {
                Label        = mod.Label,
                ModDirectory = mod.ModDirectory,
                ModName      = mod.ModName,
                Options      = new Dictionary<string, string>(mod.Options),
            };

            foreach (var (group, picks) in mod.MultiOptions)
                copy.MultiOptions[group] = new List<string>(picks);

            foreach (var (group, states) in mod.OptionStates)
                copy.OptionStates[group] = new Dictionary<string, bool>(states);

            shared.Mods.Add(copy);
        }

        if (!includeImages) return shared;

        var (cover, extras) = AttachImages(item, item.Id, imageEntries, ref missingImages);
        shared.ImageFile       = cover;
        shared.ExtraImageFiles = extras;

        return shared;
    }

    // ---------------------------------------------------------------- read

    /// <summary>
    /// Opens a bundle, extracting its pictures alongside the plugin config so imported items keep
    /// working after the original file is deleted.
    /// </summary>
    /// <remarks>
    /// The extraction folder is named from a hash of the file's contents, which makes re-opening the
    /// same bundle free and keeps two bundles that happen to share a file name apart. It also means
    /// an edited bundle extracts afresh rather than being served the previous version's pictures.
    /// </remarks>
    public LoadResult Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return LoadResult.Failed("That file no longer exists.");

            string hash;
            using (var stream = File.OpenRead(path))
                hash = Convert.ToHexString(SHA256.HashData(stream))[..16];

            using var zip = ZipFile.OpenRead(path);

            var manifest = zip.GetEntry(WardrobeShare.ManifestName);
            if (manifest == null)
                return LoadResult.Failed("This is not a wardrobe share file — no manifest inside it.");

            if (manifest.Length > MaxManifestBytes)
                return LoadResult.Failed("The manifest in this file is implausibly large; refusing to read it.");

            WardrobeShare? share;
            using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8))
                share = JsonSerializer.Deserialize<WardrobeShare>(reader.ReadToEnd());

            if (share == null)
                return LoadResult.Failed("The manifest in this file could not be read.");

            // A newer format is attempted rather than refused: the fields this version knows are
            // still where it expects them, and telling somebody their friend's wardrobe is
            // unreadable when most of it would have loaded is the worse failure
            var newer = share.FormatVersion > WardrobeShare.CurrentFormat;
            if (newer)
                _log.Warning($"[Wardrobe] Share file is format {share.FormatVersion}, this build knows " +
                             $"{WardrobeShare.CurrentFormat} — reading what it can.");

            var imageDir = Path.Combine(
                Plugin.PluginInterface.ConfigDirectory.FullName, "SharedWardrobes", hash);

            var extracted = ExtractImages(zip, imageDir);

            _log.Information($"[Wardrobe] Read share '{Path.GetFileName(path)}': " +
                             $"{share.Items.Count} item(s), {extracted} picture(s).");

            return LoadResult.Succeeded(share, imageDir, path, newer);
        }
        catch (InvalidDataException)
        {
            return LoadResult.Failed("This file is not a valid wardrobe share — it may be corrupt or incomplete.");
        }
        catch (JsonException ex)
        {
            _log.Warning(ex, "[Wardrobe] Share manifest did not parse");
            return LoadResult.Failed("The manifest in this file is damaged and could not be read.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Wardrobe] Share read failed");
            return LoadResult.Failed($"Could not open the file: {ex.Message}");
        }
    }

    /// <summary>
    /// Unpacks the picture entries, ignoring anything that is not one.
    /// </summary>
    /// <remarks>
    /// Every entry name is reduced to its bare file name and rebuilt under the destination, which is
    /// what stops an archive with <c>../../</c> or a rooted path in its entry names writing outside
    /// the folder it was given. The rebuilt path is then checked against the destination anyway,
    /// because a defence that is only argued for is one nobody notices breaking.
    /// </remarks>
    private int ExtractImages(ZipArchive zip, string destination)
    {
        Directory.CreateDirectory(destination);
        var full = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var count = 0;

        foreach (var entry in zip.Entries)
        {
            if (count >= MaxImages) break;

            // Directory entries have an empty name
            if (string.IsNullOrEmpty(entry.Name)) continue;

            if (!entry.FullName.StartsWith(WardrobeShare.ImageFolder + "/", StringComparison.OrdinalIgnoreCase))
                continue;

            var ext = Path.GetExtension(entry.Name);
            if (!AllowedImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

            if (entry.Length > MaxImageBytes)
            {
                _log.Warning($"[Wardrobe] Share picture '{entry.Name}' is {Describe(entry.Length)} — skipped.");
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, Path.GetFileName(entry.Name)));
            if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning($"[Wardrobe] Share entry '{entry.FullName}' tried to escape the extraction folder — skipped.");
                continue;
            }

            // Already extracted by a previous open of the same bundle, which the content hash in the
            // folder name makes safe to trust
            if (File.Exists(target)) { count++; continue; }

            try
            {
                entry.ExtractToFile(target, false);
                count++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"[Wardrobe] Could not extract share picture '{entry.Name}'");
            }
        }

        return count;
    }

    // ---------------------------------------------------------------- availability

    /// <summary>
    /// Works out what the local Penumbra can and cannot supply for every item in a share, in one
    /// pass over the installed mod list.
    /// </summary>
    /// <remarks>
    /// Built once when a bundle is opened rather than per item per frame: <see cref="PenumbraIpc.GetMods"/>
    /// is an IPC round trip over the whole mod library, which is fine once and not fine sixty times a
    /// second for three hundred cards.
    /// </remarks>
    public Dictionary<Guid, ItemAvailability> ResolveAvailability(WardrobeShare share)
    {
        var index  = BuildModIndex();
        var result = new Dictionary<Guid, ItemAvailability>();

        foreach (var item in share.Items)
        {
            var missing = new List<SharedMod>();
            var present = new List<SharedMod>();

            foreach (var mod in item.Mods)
            {
                if (index.Locate(mod) != null) present.Add(mod);
                else                           missing.Add(mod);
            }

            // The primary mod is index 0, by the same convention WardrobeItem.Mods uses. An item is
            // wearable when that one is present — a missing upscale or compatibility patch degrades
            // the look rather than preventing it — or when the item never needed a mod at all,
            // which is a plain game item somebody filed in their wardrobe.
            var primaryMissing = item.Mods.Count > 0 && index.Locate(item.Mods[0]) == null;
            var canWear = item.Mods.Count == 0
                ? item.GlamourerItemId.HasValue
                : !primaryMissing;

            result[item.SourceId] = new ItemAvailability(canWear, primaryMissing, missing, present);
        }

        return result;
    }

    /// <summary>
    /// How much of each outfit the local install can put together, given which of its pieces are
    /// backed by mods that are present.
    /// </summary>
    /// <remarks>
    /// Takes the item availability rather than recomputing it, so the two can never disagree about
    /// the same piece.
    /// </remarks>
    public Dictionary<Guid, OutfitAvailability> ResolveOutfitAvailability(
        WardrobeShare share, Dictionary<Guid, ItemAvailability> items)
    {
        var result = new Dictionary<Guid, OutfitAvailability>();

        foreach (var outfit in share.Outfits)
        {
            var available = outfit.ItemSourceIds.Count(
                id => items.TryGetValue(id, out var a) && a.CanWear);

            // Vanilla pieces need nothing installed, so an outfit made entirely of plain gear — a
            // shared glamour plate, most often — is always fully available
            result[outfit.SourceId] = new OutfitAvailability(
                available, outfit.ItemSourceIds.Count, outfit.VanillaItems.Count);
        }

        return result;
    }

    /// <summary>
    /// The installed mod list in the two forms a shared reference is matched against.
    /// </summary>
    /// <remarks>
    /// Built once and passed around rather than rebuilt per item. <see cref="PenumbraIpc.GetMods"/>
    /// is an IPC round trip returning the whole mod library, and importing three hundred items in one
    /// press had been doing that three hundred times.
    /// </remarks>
    private ModIndex BuildModIndex()
    {
        var byDirectory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName      = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, name) in _penumbra.GetMods())
        {
            if (!string.IsNullOrEmpty(directory)) byDirectory.Add(directory);

            // Name is the fallback key, so a mod whose folder somebody renamed is still recognised.
            // First writer wins: duplicate display names exist, and picking one arbitrarily beats
            // the last one read silently replacing an earlier match.
            if (!string.IsNullOrEmpty(name)) byName.TryAdd(name, directory);
        }

        return new ModIndex(byDirectory, byName);
    }

    private readonly record struct ModIndex(
        HashSet<string> ByDirectory,
        Dictionary<string, string> ByName)
    {
        /// <summary>The installed mod directory backing a shared reference, or null if there is none.</summary>
        public string? Locate(SharedMod mod)
        {
            if (!string.IsNullOrEmpty(mod.ModDirectory) && ByDirectory.Contains(mod.ModDirectory))
                return mod.ModDirectory;

            if (!string.IsNullOrEmpty(mod.ModName) && ByName.TryGetValue(mod.ModName, out var directory))
                return directory;

            return null;
        }
    }

    // ---------------------------------------------------------------- conversion

    /// <summary>
    /// Turns a shared item into a local one, resolving its mods against what is installed here and
    /// filing them in <paramref name="collection"/>.
    /// </summary>
    /// <remarks>
    /// The result is an ordinary <see cref="WardrobeItem"/> and is treated as one everywhere
    /// afterwards — the same wear path, the same editor, the same everything. Only two things mark
    /// it as having come from somewhere: <see cref="WardrobeItem.SharedFromId"/>, and the fact that
    /// mods the recipient does not have are dropped rather than carried as references to nothing.
    /// <para>
    /// A fresh <see cref="WardrobeItem.Id"/> every time, deliberately. See
    /// <see cref="SharedItem.SourceId"/>.
    /// </para>
    /// </remarks>
    public WardrobeItem ToLocalItem(SharedItem shared, string collection, string imageDir) =>
        ToLocalItem(shared, collection, imageDir, BuildModIndex());

    /// <summary>
    /// <see cref="ToLocalItem(SharedItem, string, string)"/> against an index built once for a batch.
    /// </summary>
    public IReadOnlyList<WardrobeItem> ToLocalItems(
        IEnumerable<SharedItem> shared, string collection, string imageDir)
    {
        var index = BuildModIndex();
        return shared.Select(s => ToLocalItem(s, collection, imageDir, index)).ToList();
    }

    private WardrobeItem ToLocalItem(SharedItem shared, string collection, string imageDir, ModIndex index)
    {
        var item = new WardrobeItem
        {
            Name              = shared.Name,
            Slot              = shared.Slot,
            Notes             = shared.Notes,
            Tags              = new List<string>(shared.Tags),
            GlamourerItemId   = shared.GlamourerItemId,
            GlamourerItemName = shared.GlamourerItemName,
            ModelSetId        = shared.ModelSetId,
            HairIdByRace      = new Dictionary<string, ushort>(shared.HairIdByRace),
            Replaces          = shared.Replaces,
            Layer             = shared.Layer,
            ForceRedraw       = shared.ForceRedraw,
            SharedFromId      = shared.SourceId,
            DateAdded         = DateTime.UtcNow,
        };

        foreach (var (race, ids) in shared.CustomizeIdsByRace)
            item.CustomizeIdsByRace[race] = new List<ushort>(ids);

        foreach (var mod in shared.Mods)
        {
            var directory = index.Locate(mod);
            if (directory == null) continue;

            var copy = new ModReference
            {
                Label        = mod.Label,
                Collection   = collection,
                ModDirectory = directory,
                ModName      = mod.ModName,
                Options      = new Dictionary<string, string>(mod.Options),
            };

            foreach (var (group, picks) in mod.MultiOptions)
                copy.MultiOptions[group] = new List<string>(picks);

            foreach (var (group, states) in mod.OptionStates)
                copy.OptionStates[group] = new Dictionary<string, bool>(states);

            item.Mods.Add(copy);
        }

        if (!string.IsNullOrEmpty(shared.ImageFile))
        {
            var cover = Path.Combine(imageDir, shared.ImageFile);
            if (File.Exists(cover)) item.ImagePath = cover;
        }

        foreach (var extra in shared.ExtraImageFiles)
        {
            var picture = Path.Combine(imageDir, extra);
            if (File.Exists(picture)) item.ExtraImages.Add(picture);
        }

        return item;
    }

    /// <summary>
    /// Turns a shared outfit into a local one, resolving its pieces through
    /// <paramref name="sourceToLocal"/>.
    /// </summary>
    /// <param name="sourceToLocal">
    /// <see cref="SharedItem.SourceId"/> to the id of the local item standing in for it. Pieces
    /// absent from the map are dropped: they are the ones whose mods this install does not have, and
    /// a reference to nothing is worse than a gap — the wear path would skip it anyway, silently.
    /// </param>
    /// <remarks>
    /// What comes out is an ordinary outfit. It is not a plate and not a design card whatever it was
    /// on the sender's side, both for the reasons in <see cref="SharedOutfit"/> — so it can be edited,
    /// worn and deleted like any outfit somebody built themselves.
    /// </remarks>
    public Outfit ToLocalOutfit(SharedOutfit shared, IReadOnlyDictionary<Guid, Guid> sourceToLocal, string imageDir)
    {
        var outfit = new Outfit
        {
            Name          = shared.Name,
            Tags          = new List<string>(shared.Tags),
            HatVisible    = shared.HatVisible,
            WeaponVisible = shared.WeaponVisible,
            DateAdded     = DateTime.UtcNow,
        };

        foreach (var sourceId in shared.ItemSourceIds)
            if (sourceToLocal.TryGetValue(sourceId, out var localId))
                outfit.ItemIds.Add(localId);

        foreach (var (key, dye) in shared.Dyes)
        {
            if (!Guid.TryParse(key, out var sourceId)) continue;
            if (!sourceToLocal.TryGetValue(sourceId, out var localId)) continue;

            outfit.Dyes[localId.ToString()] = new OutfitDye
            {
                Stain1   = dye.Stain1,
                Stain2   = dye.Stain2,
                Advanced = new Dictionary<string, string>(dye.Advanced),
            };
        }

        foreach (var (slot, piece) in shared.VanillaItems)
        {
            outfit.VanillaItems[slot] = new VanillaPiece
            {
                ItemId = piece.ItemId,
                Name   = piece.Name,
                Stain1 = piece.Stain1,
                Stain2 = piece.Stain2,
            };
        }

        if (!string.IsNullOrEmpty(shared.ImageFile))
        {
            var cover = Path.Combine(imageDir, shared.ImageFile);
            if (File.Exists(cover)) outfit.ImagePath = cover;
        }

        foreach (var extra in shared.ExtraImageFiles)
        {
            var picture = Path.Combine(imageDir, extra);
            if (File.Exists(picture)) outfit.ExtraImages.Add(picture);
        }

        return outfit;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A byte count in the units a person would use for it.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        < 1024                => $"{bytes} B",
        < 1024 * 1024         => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _                     => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private static bool IsAlreadyCompressed(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What the local install can supply for one shared item.</summary>
/// <param name="CanWear">
/// Whether pressing Wear would produce the item as its sender saw it, near enough to be worth
/// offering. False means the mod that <em>is</em> the item is not installed here.
/// </param>
/// <param name="PrimaryMissing">Whether the mod at index 0 — the item itself — is the missing one.</param>
/// <param name="Missing">Every mod the item names that is not installed, primary or supplementary.</param>
/// <param name="Present">Every mod the item names that is installed.</param>
public readonly record struct ItemAvailability(
    bool CanWear,
    bool PrimaryMissing,
    IReadOnlyList<SharedMod> Missing,
    IReadOnlyList<SharedMod> Present);

/// <summary>How much of one shared outfit this install could actually put together.</summary>
/// <param name="AvailablePieces">Wardrobe items in the outfit whose mods are installed here.</param>
/// <param name="TotalPieces">Wardrobe items the outfit names.</param>
/// <param name="VanillaPieces">
/// Plain game items in it, which need nothing installed and so always arrive intact.
/// </param>
public readonly record struct OutfitAvailability(
    int AvailablePieces,
    int TotalPieces,
    int VanillaPieces)
{
    /// <summary>Whether anything at all would land — some pieces, or some plain gear.</summary>
    public bool CanAdd => AvailablePieces > 0 || VanillaPieces > 0;

    /// <summary>Whether it would arrive exactly as its sender has it.</summary>
    public bool Complete => AvailablePieces == TotalPieces;

    public int MissingPieces => TotalPieces - AvailablePieces;
}

/// <summary>Outcome of writing a bundle, in the terms the UI reports it in.</summary>
public readonly record struct ExportResult(
    bool   Success,
    string Message,
    int    Items,
    int    Outfits,
    int    Images,
    int    SkippedImages,
    long   Bytes)
{
    public static ExportResult Failed(string message) => new(false, message, 0, 0, 0, 0, 0);

    /// <param name="pulledIn">
    /// Items added to the export because an outfit needed them, which nobody ticked. Reported rather
    /// than passed over — see <c>WithOutfitPieces</c>.
    /// </param>
    public static ExportResult Succeeded(int items, int outfits, int images, int skipped, long bytes, int pulledIn)
    {
        var what = outfits > 0
            ? $"{items} item(s) and {outfits} outfit(s)"
            : $"{items} item(s)";

        var message = $"Shared {what} with {images} picture(s) — {WardrobeShareService.Describe(bytes)}.";

        if (pulledIn > 0)
            message += $" {pulledIn} of the items came along because an outfit needed them.";

        if (skipped > 0)
            message += $" {skipped} picture(s) could not be read and were left out.";

        return new ExportResult(true, message, items, outfits, images, skipped, bytes);
    }
}

/// <summary>Outcome of reading a bundle.</summary>
public readonly record struct LoadResult(
    bool           Success,
    string         Message,
    WardrobeShare? Share,
    string         ImageDir,
    string         Path,
    bool           NewerFormat)
{
    public static LoadResult Failed(string message) =>
        new(false, message, null, string.Empty, string.Empty, false);

    public static LoadResult Succeeded(WardrobeShare share, string imageDir, string path, bool newer) =>
        new(true, string.Empty, share, imageDir, path, newer);
}
