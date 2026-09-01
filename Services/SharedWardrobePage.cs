using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// Turns a wardrobe somebody sent into the same page your own wardrobe exports as.
/// </summary>
/// <remarks>
/// The second builder, and the one that settles what <see cref="WardrobePage"/> is for. A bundle and
/// a local wardrobe have almost nothing in common as types — different classes, different fields,
/// different ideas of what a mod or an outfit even is — and yet a person looking at either wants the
/// same grid, the same filters and the same panel behind a card. Both reduce to a
/// <see cref="PageModel"/>, and from there they are one page with one layout to maintain.
/// <para>
/// It is also the shape the remote view takes when there is one. Viewing a friend's wardrobe over a
/// connection is this, with the bundle arriving down a socket rather than out of a file: the payload
/// was written so the same records travel either way, so what is here is the whole of the work
/// between "the wardrobe arrived" and "there is a page of it".
/// </para>
/// <para>
/// Everything on the page is text somebody else wrote. None of it is trusted: names, tags, notes and
/// mod names are escaped by the renderer, links are shown as their own address, and the file names
/// the bundle gave its pictures are never used to build a path — see <see cref="Picture"/>.
/// </para>
/// </remarks>
public static class SharedWardrobePage
{
    /// <summary>
    /// Builds the page model for a bundle that has already been read and unpacked.
    /// </summary>
    /// <param name="share">The manifest, as <see cref="WardrobeShareService.Read"/> returned it.</param>
    /// <param name="imageDir">Where that read unpacked the bundle's pictures.</param>
    /// <param name="stains">Dye names by id, for describing an outfit's colours.</param>
    /// <param name="includeMods">
    /// Whether the mods behind an item are written out. The sender already decided what to put in the
    /// bundle; this is the reader's own choice about what to put in a page they may pass on again.
    /// </param>
    public static PageModel Build(WardrobeShare share, string imageDir,
                                  IReadOnlyDictionary<byte, string> stains, bool includeMods = true)
    {
        var byName = string.IsNullOrWhiteSpace(share.ExportedBy) ? null : share.ExportedBy.Trim();

        var model = new PageModel
        {
            Title       = byName is null ? "Shared wardrobe" : $"{byName}'s wardrobe",
            Byline      = byName is null ? null : $"Shared by {byName}",
            Description = string.IsNullOrWhiteSpace(share.Description) ? null : share.Description.Trim(),

            // The moment the bundle was made, not the moment it was opened. The page describes
            // somebody else's wardrobe as it was when they sent it, and dating it today would say
            // otherwise about a file that has been sitting in a chat log for a month.
            When = share.ExportedUtc == default
                ? DateTime.Now
                : share.ExportedUtc.ToLocalTime(),
        };

        var names = new Dictionary<Guid, SharedItem>();
        foreach (var item in share.Items) names[item.SourceId] = item;

        foreach (var item in share.Items.OrderBy(i => i.Name, NaturalOrder.Comparer))
        {
            var card = new PageCard
            {
                Name         = Trim(item.Name, "Untitled"),
                Slot         = item.Slot.DisplayName(),
                Notes        = item.Notes,
                ImageSources = Pictures(imageDir, item.ImageFile, item.ExtraImageFiles),

                // No favourites and no date. Whose favourite would it be, and a bundle carries
                // neither — the sender's import date is not a fact about the reader's wardrobe.
            };

            PageText.SplitTags(item.Tags, card);

            if (!string.IsNullOrWhiteSpace(item.GlamourerItemName))
                card.Fields.Add(new PageField { Label = "Game item", Value = item.GlamourerItemName! });

            if (!string.IsNullOrWhiteSpace(item.Layer))
                card.Fields.Add(new PageField { Label = "Layer", Value = item.Layer! });

            if (!string.IsNullOrWhiteSpace(item.Replaces))
                card.Fields.Add(new PageField { Label = "Replaces", Value = item.Replaces! });

            var linked = item.LinkedSourceIds
                .Select(id => names.TryGetValue(id, out var other) ? other.Name : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (linked.Count > 0)
                card.Fields.Add(new PageField { Label = "Worn with", Value = string.Join(", ", linked) });

            if (includeMods)
            {
                foreach (var mod in item.Mods)
                {
                    var line = PageText.DescribeMod(
                        mod.Label, mod.ModName, mod.ModDirectory,
                        mod.Options, mod.MultiOptions, mod.OptionStates);

                    if (line.Length > 0) card.Mods.Add(line);
                }
            }

            if (!string.IsNullOrWhiteSpace(card.Notes)) card.Links = PageText.Links(card.Notes);

            model.Items.Add(card);
        }

        foreach (var outfit in share.Outfits.OrderBy(o => o.Name, NaturalOrder.Comparer))
        {
            var card = new PageCard
            {
                Name         = Trim(outfit.Name, "Untitled outfit"),
                ImageSources = Pictures(imageDir, outfit.ImageFile, outfit.ExtraImageFiles),
                Badge        = OriginBadge(outfit.Origin),
            };

            PageText.SplitTags(outfit.Tags, card);

            foreach (var id in outfit.ItemSourceIds)
            {
                if (!names.TryGetValue(id, out var piece)) continue;

                var dye = outfit.Dyes.TryGetValue(id.ToString(), out var d)
                    ? PageText.DescribeDye(d, stains)
                    : null;

                card.Pieces.Add(PageText.PieceLine(piece.Slot.DisplayName(), piece.Name, dye));
            }

            foreach (var pair in outfit.VanillaItems.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                var label = Enum.TryParse<EquipSlot>(pair.Key, out var parsed)
                    ? parsed.DisplayName()
                    : pair.Key;

                var dye = PageText.DescribeDye(
                    new OutfitDye { Stain1 = pair.Value.Stain1, Stain2 = pair.Value.Stain2 }, stains);

                card.Pieces.Add(PageText.PieceLine(label, Trim(pair.Value.Name, "?"), dye));
            }

            model.Outfits.Add(card);
        }

        return model;
    }

    /// <summary>What a plate or a design card became when it was shared, or null for an ordinary outfit.</summary>
    /// <remarks>
    /// Worth saying on the page for the same reason it is worth saying in the receive panel: a design
    /// card arrives holding its attached items and none of the design's own gear or colouring, so it
    /// will look thin, and the honest answer is to say why rather than let it be discovered.
    /// </remarks>
    private static string? OriginBadge(SharedOutfitOrigin origin) => origin switch
    {
        SharedOutfitOrigin.GlamourPlate => "Was a glamour plate",
        SharedOutfitOrigin.DesignCard   => "Was a Glamourer design",
        _                               => null,
    };

    private static string Trim(string? text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    private static List<string> Pictures(string imageDir, string? cover, List<string> extras)
    {
        var all = new List<string>();

        if (Picture(imageDir, cover) is { } first) all.Add(first);

        foreach (var name in extras)
        {
            if (Picture(imageDir, name) is not { } path) continue;
            if (all.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            all.Add(path);
        }

        return all;
    }

    /// <summary>
    /// Resolves one of the bundle's picture names to a path inside the folder it was unpacked to.
    /// </summary>
    /// <remarks>
    /// The name is reduced to its bare file name and the rebuilt path is then checked against the
    /// folder anyway, exactly as the unpacking does it — a manifest is a file somebody else wrote, and
    /// a name in it saying <c>../../</c> must not become a path this reads and copies into an export
    /// somewhere else. The check is repeated rather than assumed because the two run at different
    /// times and nothing guarantees the same manifest was the one unpacked.
    /// </remarks>
    private static string? Picture(string imageDir, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(imageDir)) return null;

        try
        {
            var root  = Path.GetFullPath(imageDir) + Path.DirectorySeparatorChar;
            var path  = Path.GetFullPath(Path.Combine(imageDir, Path.GetFileName(name)));

            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch
        {
            // A name with characters no path can hold. Nothing to show, and nothing worth failing over.
            return null;
        }
    }
}
