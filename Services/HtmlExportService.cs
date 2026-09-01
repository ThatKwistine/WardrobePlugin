using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;
using WardrobePlugin.Ui;

namespace WardrobePlugin.Services;

/// <summary>
/// Writes your own wardrobe out as a web page.
/// </summary>
/// <remarks>
/// A wardrobe is a catalogue somebody built by hand, and until now the only way to show one to
/// another person was a screenshot of a screenshot. This writes the catalogue itself — searchable,
/// filterable, and readable by anyone with a browser and no plugin, no game and no account.
/// <para>
/// Nothing is uploaded and nothing is fetched. The page is written to a folder you pick, it
/// references no script, font, stylesheet or image from the internet, and it contains exactly what
/// the settings say it may. Sharing it is a file you send, deliberately, at a moment of your
/// choosing — which is the only sense in which this is a sharing feature at all.
/// </para>
/// <para>
/// What this class is, precisely, is the <em>builder</em>: it turns <see cref="Configuration"/> into
/// a <see cref="PageModel"/> and hands it to <see cref="WardrobePageWriter"/>. The layout is not
/// here and is not its property — see <see cref="WardrobePage"/> for why, and for what else builds
/// one of these.
/// </para>
/// <para>
/// Experimental because the shape of a good lookbook is not settled: what belongs on a card, what
/// belongs behind it, and how much of a wardrobe of several hundred a browser will hold at once are
/// all questions this currently answers by guessing.
/// </para>
/// </remarks>
public sealed class HtmlExportService
{
    /// <summary>The picture sizes offered in settings. Lives on the writer, which enforces them.</summary>
    public static int[] ImageSizes => WardrobePageWriter.ImageSizes;

    private readonly Configuration      _config;
    private readonly ItemLookupService  _items;
    private readonly EmoteLookupService _emotes;
    private readonly IPluginLog         _log;

    private volatile bool _running;

    /// <summary>Whether an export is in flight, so the button says so rather than starting a second.</summary>
    public bool Running => _running;

    /// <summary>What the export is doing right now, for the settings panel to show while it runs.</summary>
    public string Progress { get; private set; } = string.Empty;

    /// <summary>Outcome of the last export, kept until the next one replaces it.</summary>
    public string LastResult { get; private set; } = string.Empty;

    /// <summary>Where the last successful export went, for the button that opens it.</summary>
    public string LastPath { get; private set; } = string.Empty;

    public HtmlExportService(Configuration config, ItemLookupService items, EmoteLookupService emotes,
                             IPluginLog log)
    {
        _config = config;
        _items  = items;
        _emotes = emotes;
        _log    = log;
    }

    /// <summary>
    /// Reads the wardrobe, then writes the page on a background thread.
    /// </summary>
    /// <remarks>
    /// Call from the framework thread. The reading happens inline and on purpose: the wardrobe lists
    /// belong to the UI, and handing them to a task that will spend a minute resizing pictures is how
    /// an export ends up describing a wardrobe halfway through being edited. What crosses to the
    /// background thread is a model nothing else can touch.
    /// </remarks>
    public void Run()
    {
        if (_running) return;

        var folder = _config.HtmlExportFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            LastResult = "No export folder set.";
            return;
        }

        PageModel model;
        try
        {
            model = Build();
        }
        catch (Exception ex)
        {
            LastResult = $"Export failed while reading the wardrobe: {ex.Message}";
            _log.Error(ex, "[Wardrobe] HTML export failed to read the wardrobe");
            return;
        }

        if (model.Items.Count == 0 && model.Outfits.Count == 0)
        {
            LastResult = "Nothing to export — the wardrobe is empty.";
            return;
        }

        var options = new PageWriteOptions
        {
            Folder        = folder,
            Layout        = _config.HtmlExportLayout,
            ImageSize     = _config.HtmlExportImageSize,
            Stem          = "Wardrobe",
            Progress      = text => Progress = text,
            PictureFailed = (path, why) =>
                _log.Warning($"[Wardrobe] HTML export: skipped {path} — {why}"),
        };

        _running = true;
        Progress = "Starting...";

        _ = Task.Run(() =>
        {
            try
            {
                var written = WardrobePageWriter.Write(model, options);

                LastPath   = written.Path;
                LastResult = $"Exported {model.Items.Count} item(s) and {model.Outfits.Count} " +
                             $"outfit(s) with {written.Pictures} picture(s), {written.Size}.";
                _log.Information($"[Wardrobe] HTML export written to {written.Path} ({written.Size}).");
            }
            catch (Exception ex)
            {
                LastResult = $"Export failed: {ex.Message}";
                _log.Error(ex, "[Wardrobe] HTML export failed");
            }
            finally
            {
                Progress = string.Empty;
                _running = false;
            }
        });
    }

    // ── Building the model from the local wardrobe ────────────────────────────

    /// <summary>Turns the configuration into a page model, on the thread that owns it.</summary>
    private PageModel Build()
    {
        var model = new PageModel
        {
            Title           = string.IsNullOrWhiteSpace(_config.HtmlExportTitle)
                ? "My Wardrobe"
                : _config.HtmlExportTitle.Trim(),
            PortraitOutfits = _config.PortraitOutfitPreviews,
        };

        var byId = new Dictionary<Guid, WardrobeItem>();
        foreach (var item in _config.WardrobeItems) byId[item.Id] = item;

        foreach (var item in _config.WardrobeItems.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Mod categories are kept but hidden from the grid while the setting is off, and an
            // export is a picture of the wardrobe as its owner sees it rather than of the whole file
            if (item.Slot.IsModCategory() && !_config.ModCategoriesEnabled) continue;

            var card = new PageCard
            {
                Name         = item.Name,
                Slot         = item.Slot.DisplayName(),
                Favourite    = item.IsFavorite,
                ImageSources = item.AllImages(),
                Added        = item.DateAdded.ToLocalTime().ToString("d MMMM yyyy"),
                Notes        = _config.HtmlExportIncludeNotes ? item.Notes : null,
            };

            SplitTags(item.Tags, card);

            if (!string.IsNullOrWhiteSpace(item.GlamourerItemName))
                card.Fields.Add(new PageField { Label = "Game item", Value = item.GlamourerItemName! });

            if (!string.IsNullOrWhiteSpace(item.DesignName))
                card.Fields.Add(new PageField { Label = "Design", Value = item.DesignName });

            if (!string.IsNullOrWhiteSpace(item.Layer))
                card.Fields.Add(new PageField { Label = "Layer", Value = item.Layer! });

            if (!string.IsNullOrWhiteSpace(item.Replaces))
            {
                var emote = item.Slot == EquipSlot.Animation ? _emotes.Describe(item.Replaces) : null;
                card.Fields.Add(new PageField
                {
                    Label = "Replaces",
                    Value = string.IsNullOrWhiteSpace(emote) ? item.Replaces! : $"{item.Replaces} ({emote})",
                });
            }

            if (item.VariantOfId is { } originalId && byId.TryGetValue(originalId, out var original))
                card.Badge = $"Variant of {original.Name}";

            var linked = item.LinkedItemIds
                .Select(id => byId.TryGetValue(id, out var other) ? other.Name : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (linked.Count > 0)
                card.Fields.Add(new PageField { Label = "Worn with", Value = string.Join(", ", linked) });

            if (_config.HtmlExportIncludeMods)
                card.Mods.AddRange(item.Mods.Select(DescribeMod).Where(s => s.Length > 0));

            if (!string.IsNullOrWhiteSpace(card.Notes)) card.Links = NoteText.Links(card.Notes);

            model.Items.Add(card);
        }

        var stains = new Dictionary<byte, string>();
        foreach (var (id, name, _) in _items.GetStains()) stains[id] = name;

        foreach (var outfit in _config.Outfits.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Hidden means "keep it, but not in front of me". A page handed to somebody else is the
            // one place that reading is least in doubt.
            if (outfit.Hidden) continue;

            // Design cards are kept but hidden while the feature is off, as mod categories are. A
            // page carrying two hundred cards from a feature its owner turned off is not a page of
            // their wardrobe.
            if (outfit.IsDesign && !_config.ShowGlamourerDesigns) continue;

            var card = new PageCard
            {
                Name         = outfit.Name,
                ImageSources = outfit.AllImages(),
                Added        = outfit.DateAdded.ToLocalTime().ToString("d MMMM yyyy"),
                Badge        = outfit.GlamourPlateId is { } plate ? $"Glamour plate {plate}"
                             : outfit.IsDesign                    ? "Glamourer design"
                             : null,
            };

            SplitTags(outfit.Tags, card);

            if (outfit.IsDesign && !string.IsNullOrWhiteSpace(outfit.DesignName))
                card.Fields.Add(new PageField { Label = "Design", Value = outfit.DesignName });

            foreach (var id in outfit.ItemIds)
            {
                if (!byId.TryGetValue(id, out var piece)) continue;

                var dye = outfit.Dyes.TryGetValue(id.ToString(), out var d) ? DescribeDye(d, stains) : null;
                card.Pieces.Add(PieceLine(piece.Slot.DisplayName(), piece.Name, dye));
            }

            foreach (var pair in outfit.VanillaItems.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                var label = Enum.TryParse<EquipSlot>(pair.Key, out var parsed)
                    ? parsed.DisplayName()
                    : pair.Key;

                var dye = DescribeDye(
                    new OutfitDye { Stain1 = pair.Value.Stain1, Stain2 = pair.Value.Stain2 }, stains);

                card.Pieces.Add(PieceLine(label, pair.Value.Name, dye));
            }

            model.Outfits.Add(card);
        }

        return model;
    }

    // ── Shared with the builders that are not the local wardrobe ──────────────

    /// <summary>One line of an outfit's contents: where it goes, what it is, how it is dyed.</summary>
    /// <remarks>
    /// Internal rather than private because a page built from a bundle somebody sent describes an
    /// outfit the same way, and two spellings of the same line is exactly the drift the shared
    /// renderer exists to avoid.
    /// </remarks>
    internal static string PieceLine(string slot, string name, string? dye) =>
        dye is null ? $"{slot} — {name}" : $"{slot} — {name} ({dye})";

    /// <summary>Splits a tag list into styles and ordinary tags, dropping the reserved prefix.</summary>
    internal static void SplitTags(IEnumerable<string> tags, PageCard card)
    {
        foreach (var tag in tags)
        {
            if (TagTree.IsStyle(tag))
                card.Styles.Add(tag[(TagTree.StyleRoot.Length + 1)..]);
            else
                card.Tags.Add(tag);
        }

        card.Styles.Sort(StringComparer.OrdinalIgnoreCase);
        card.Tags.Sort(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One mod and the options chosen in it, as a single line.</summary>
    /// <remarks>
    /// Takes the fields rather than a <see cref="ModReference"/> so that a shared item's
    /// <c>SharedMod</c> — the same four things under different type — describes itself identically.
    /// </remarks>
    internal static string DescribeMod(string label, string modName, string modDirectory,
                                       IReadOnlyDictionary<string, string> single,
                                       IReadOnlyDictionary<string, List<string>> multi,
                                       IReadOnlyDictionary<string, Dictionary<string, bool>> states)
    {
        var name = string.IsNullOrWhiteSpace(modName) ? modDirectory : modName;
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var options = new List<string>();

        foreach (var one in single)
            options.Add($"{one.Key}: {one.Value}");

        foreach (var group in states)
        {
            var on = group.Value.Where(s => s.Value).Select(s => s.Key).ToList();
            if (on.Count > 0) options.Add($"{group.Key}: {string.Join(", ", on)}");
        }

        // Read only where tri-states have nothing to say for that group, matching how they are applied
        foreach (var group in multi)
        {
            if (states.ContainsKey(group.Key) || group.Value.Count == 0) continue;
            options.Add($"{group.Key}: {string.Join(", ", group.Value)}");
        }

        options.Sort(StringComparer.OrdinalIgnoreCase);

        var line = !string.IsNullOrWhiteSpace(label) && label != "Main Mod"
            ? $"{name} — {label}"
            : name;

        return options.Count == 0 ? line : $"{line} ({string.Join("; ", options)})";
    }

    private static string DescribeMod(ModReference mod) =>
        DescribeMod(mod.Label, mod.ModName, mod.ModDirectory, mod.Options, mod.MultiOptions, mod.OptionStates);

    /// <summary>The dye channels as names, or null when the piece carries none.</summary>
    internal static string? DescribeDye(OutfitDye dye, IReadOnlyDictionary<byte, string> stains)
    {
        var names = new List<string>();
        if (dye.Stain1 != 0) names.Add(stains.TryGetValue(dye.Stain1, out var a) ? a : $"Dye {dye.Stain1}");
        if (dye.Stain2 != 0) names.Add(stains.TryGetValue(dye.Stain2, out var b) ? b : $"Dye {dye.Stain2}");
        if (dye.Advanced.Count > 0) names.Add("advanced dyes");

        return names.Count == 0 ? null : string.Join(" + ", names);
    }
}
