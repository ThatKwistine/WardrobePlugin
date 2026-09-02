using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace WardrobePlugin.Ui;

/// <summary>
/// The settings panel: eight categories, opened one at a time, instead of nineteen sections
/// stacked in a single scroll.
/// </summary>
/// <remarks>
/// The old panel put every setting the plugin has into one 360px column, so finding the tag
/// colours meant scrolling past the base character's five sub-panels to get to them. Worse, the
/// grouping had stopped describing anything: "Screenshots" held the folder, the image size, three
/// session checkboxes, the camera presets, the presets file, the diagnostics and the crop guide,
/// while tags were split across three separate sections nowhere near each other.
/// <para>
/// So the categories are by subject, and the sections inside them are collapsing headers — a
/// category is a short list you can read at a glance, and only the section you opened is on
/// screen. Search cuts across all of it for anyone who knows the name of what they want.
/// </para>
/// <para>
/// Experimental is no longer a category. A feature that is not finished belongs beside the ones
/// it resembles, wearing a badge that says so, rather than in a drawer at the bottom where it is
/// filed by how finished it is instead of by what it does.
/// </para>
/// </remarks>
public partial class PluginUi
{
    /// <summary>One collapsing section within a category.</summary>
    /// <param name="Title">Header text, and what search matches on first.</param>
    /// <param name="Keywords">Extra words search should find this by, none of them drawn.</param>
    /// <param name="Draw">Draws the body. Must not draw its own title — the header is above it.</param>
    /// <param name="Experimental">Whether to badge it as not fully tested.</param>
    private sealed record SettingsSection(
        string Title, string Keywords, Action Draw, bool Experimental = false);

    /// <param name="Blurb">Shown on hover, so the list itself stays a list of names.</param>
    private sealed record SettingsCategory(string Name, string Blurb, SettingsSection[] Sections);

    /// <summary>Which category is open, or null for the list of them.</summary>
    private int? _settingsCategory;

    private string _settingsSearch = string.Empty;

    /// <summary>
    /// Built once. The delegates are instance methods, so the array is good for the plugin's life.
    /// </summary>
    private SettingsCategory[]? _settingsCategories;

    private SettingsCategory[] Categories => _settingsCategories ??= new[]
    {
        new SettingsCategory("Penumbra & Mods",
            "Which collection items go to, what the import lists show, and the mod types beyond gear.",
            new[]
            {
                new SettingsSection("Collection", "penumbra collection character active default",
                    DrawCollectionSettings),
                new SettingsSection("Importing", "import hide support already imported mods",
                    DrawImportSettings),
                new SettingsSection("Other Mod Types", "animation vfx mount minion emote category",
                    DrawModCategorySettings),
                new SettingsSection("Uncompressed Textures", "texture size compression bc7 memory vram",
                    DrawTextureFlagSettings, Experimental: true),
            }),

        new SettingsCategory("Glamourer & Outfits",
            "What wearing an item changes, and how Glamourer's own designs appear here.",
            new[]
            {
                new SettingsSection("Wearing", "hair hairstyle apply equip wear",
                    DrawWearingSettings),
                new SettingsSection("What You Were Last Wearing",
                    "last worn remember restore login session automatic again reapply",
                    DrawLastWornSettings),
                new SettingsSection("Advanced Dyes", "dye colour material advanced outfit",
                    DrawAdvancedDyeSettings),
                new SettingsSection("Glamourer Designs", "design cards folder filter excluded",
                    DrawGlamourerDesignSettings),
            }),

        new SettingsCategory("Characters",
            "The look everything else is worn on top of, and whether each character gets a wardrobe.",
            new[]
            {
                new SettingsSection("Base Character", "base body skin strip keep slots design",
                    DrawBaseCharacterSettings),
                new SettingsSection("Per-Character Wardrobes",
                    "wardrobe profile character alt separate switch bind multiple",
                    DrawProfileSettings),
            }),

        new SettingsCategory("Grid & Cards",
            "How the wardrobe itself looks — card size, slot icons, and grouped variants.",
            new[]
            {
                new SettingsSection("Toolbar", "toolbar menu bar buttons classic old layout",
                    DrawToolbarSettings),
                new SettingsSection("Card Size", "card size scale grid zoom bigger smaller",
                    DrawCardSizeSettings),
                new SettingsSection("Slot Icons", "icon pack slot custom images size",
                    DrawSlotIconSettings),
                new SettingsSection("Variants", "variant group fold original recolour",
                    DrawVariantSettings),
            }),

        new SettingsCategory("Tags & Styles",
            "How tags are picked, coloured, and taken from Glamourer.",
            new[]
            {
                new SettingsSection("Tag Picker", "tag tree picker edit flat list",
                    DrawTagPickerSettings),
                new SettingsSection("Tag Colours", "tag colour color style highlight",
                    DrawTagColourSettings),
                new SettingsSection("Tags From Glamourer", "design folder tag glamourer automatic",
                    DrawDesignTagSettings),
            }),

        new SettingsCategory("Images",
            "Where pictures are kept, where the game leaves them, and what size they are saved at.",
            new[]
            {
                new SettingsSection("Images Folder", "images folder path pictures where saved",
                    DrawImageFolderSettings),
                new SettingsSection("FFXIV Screenshots Folder", "screenshot folder game ffxiv watch",
                    DrawScreenshotsFolderSettings),
                new SettingsSection("Captured Image Size", "size resolution square crop portrait 9:16",
                    DrawCapturedImageSettings),
            }),

        new SettingsCategory("Screenshot Sessions",
            "Photographing the wardrobe: what a session does, and the angles it shoots from.",
            new[]
            {
                new SettingsSection("During A Session", "manual strip compact session shot next",
                    DrawSessionBehaviourSettings),
                new SettingsSection("Fully Automatic Sessions", "auto super unattended delay countdown",
                    DrawAutoSessionSettings, Experimental: true),
                new SettingsSection("Crop Guide", "crop guide overlay square framing dim",
                    DrawCropGuideSetting),
                new SettingsSection("Camera Angles", "camera preset angle slot gpose view",
                    DrawCameraAngleSettings),
                new SettingsSection("Camera Presets File", "preset file json path load save import",
                    DrawCameraPresetsFileSettings),
                new SettingsSection("Camera Diagnostics", "camera log debug dump bug report",
                    DrawCameraDiagnosticsSettings),
            }),

        new SettingsCategory("Backups & Data",
            "Keeping the wardrobe safe, getting it out as a web page, and starting over.",
            new[]
            {
                new SettingsSection("Backups", "backup hourly folder keep restore copies",
                    DrawBackupSettings),
                new SettingsSection("Export As A Web Page", "html export web page share browser",
                    DrawHtmlExportSettings, Experimental: true),
                new SettingsSection("Changelog", "changelog what changed version update",
                    DrawChangelogSettings),
                new SettingsSection("First-Time Setup", "setup walkthrough onboarding again reset",
                    DrawSetupSettings),
            }),
    };

    // ── The panel ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole settings panel, drawn into whichever window is hosting it.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="SettingsWindow"/>. Internal rather than private because the window is
    /// a separate class; everything it draws still reads this one's state.
    /// </remarks>
    internal void DrawSettingsBody()
    {
        DrawSettingsSearch();

        if (!string.IsNullOrEmpty(_settingsSearch))
        {
            DrawSettingsSearchResults();
            return;
        }

        if (_settingsCategory is not { } index || index < 0 || index >= Categories.Length)
        {
            _settingsCategory = null;
            DrawSettingsCategoryList();
            return;
        }

        DrawSettingsCategory(Categories[index]);
    }

    private void DrawSettingsSearch()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##settingssearch", "Search settings…", ref _settingsSearch, 64);
        ImGui.Spacing();
    }

    /// <summary>
    /// The eight categories, as a list of names and nothing else.
    /// </summary>
    /// <remarks>
    /// Each one's description is on hover rather than under it. A list of eight names is read in a
    /// glance; the same list with a line of explanation under every entry is a wall of text you
    /// have to read to find out it was a list of eight names.
    /// </remarks>
    private void DrawSettingsCategoryList()
    {
        for (var i = 0; i < Categories.Length; i++)
        {
            var category = Categories[i];

            if (ImGui.Button($"{category.Name}##cat{i}", new Vector2(-1, 0)))
                _settingsCategory = i;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(category.Blurb);
        }
    }

    private void DrawSettingsCategory(SettingsCategory category)
    {
        if (GlyphButton(FontAwesomeIcon.ChevronLeft, "settingsback", "Back to all settings."))
        {
            _settingsCategory = null;
            return;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(category.Name);

        ImGui.Separator();
        ImGui.Spacing();

        for (var i = 0; i < category.Sections.Length; i++)
            // The first section open, so choosing a category shows settings rather than another
            // list of closed bars to click through
            DrawSettingsSection(category.Sections[i], $"{category.Name}##{i}", defaultOpen: i == 0);
    }

    /// <summary>
    /// Everything matching the search box, from every category at once.
    /// </summary>
    /// <remarks>
    /// The reason the categories can be strict about what belongs where: anyone who knows the name
    /// of the setting they want does not have to agree with the filing to find it. Each result says
    /// which category it came from, so the next visit can go straight there.
    /// </remarks>
    private void DrawSettingsSearchResults()
    {
        var needle = _settingsSearch.Trim();

        var hits = Categories
            .SelectMany(c => c.Sections.Select(s => (Category: c, Section: s)))
            .Where(x => Contains(x.Section.Title, needle)
                     || Contains(x.Section.Keywords, needle)
                     || Contains(x.Category.Name, needle))
            .ToList();

        if (hits.Count == 0)
        {
            ImGui.TextDisabled($"Nothing matches '{needle}'.");
            return;
        }

        foreach (var (category, section) in hits)
        {
            ImGui.TextDisabled(category.Name);
            DrawSettingsSection(section, $"search##{category.Name}", defaultOpen: hits.Count <= 3);
        }

        static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One collapsing section, badged if the feature inside it is not finished.
    /// </summary>
    /// <remarks>
    /// The badge is what Experimental-as-a-category used to be. Saying it here, on the section
    /// itself, is both more honest and more useful: you see it at the moment you are about to turn
    /// the thing on, rather than having inferred it from which drawer you opened.
    /// </remarks>
    private static void DrawSettingsSection(SettingsSection section, string scope, bool defaultOpen)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

        if (!ImGui.CollapsingHeader($"{section.Title}##{scope}", flags)) return;

        ImGui.Spacing();

        if (section.Experimental)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f),
                "Experimental — not fully tested.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Written against another plugin's or the game's own internals, and " +
                                 "not yet proven over a large wardrobe.\n\n" +
                                 "Turn it on if you want to help find out how well it works.");
            ImGui.Spacing();
        }

        section.Draw();

        ImGui.Spacing();
    }

    // ── Prose ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One dim line of explanation, with the long version on hover.
    /// </summary>
    /// <remarks>
    /// The panel used to carry four paragraphs of prose for every checkbox, most of it repeating
    /// the tooltip already attached to that checkbox. Nothing has been thrown away — the long text
    /// is still a hover from where it was — but the panel now reads as a list of settings rather
    /// than as an essay with controls in it.
    /// <para>
    /// The ellipsis is the only affordance there is for hidden text on a line that is not a
    /// control, so it is drawn whenever there is more and never when there is not.
    /// </para>
    /// </remarks>
    private static void Hint(string line, string? more = null)
    {
        ImGui.TextDisabled(more == null ? line : line + " …");

        if (more != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(more);
    }

    /// <summary>
    /// One item of a short checklist, with the reason it is on the list on hover.
    /// </summary>
    /// <remarks>
    /// For the few places where the list itself has to stay on screen — a pre-flight check whose
    /// failure mode is silent — while the paragraph explaining each entry does not.
    /// </remarks>
    private static void BulletHint(string line, string more)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        Hint(line, more);
    }

    /// <summary>A hint that needs to be read rather than found, in the warning colour.</summary>
    private static void WarnHint(string line, string? more = null)
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), more == null ? line : line + " …");

        if (more != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(more);
    }
}
