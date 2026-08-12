using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Configuration;
using WardrobePlugin.Models;

namespace WardrobePlugin;

/// <summary>
/// Whether the one-off mod ownership explanation is still owed. Values are persisted, so do not
/// renumber. <c>Unevaluated</c> is the default a config written before the field deserialises to,
/// which is what distinguishes an upgrade from a fresh install.
/// </summary>
public enum OwnershipNoticeState
{
    Unevaluated = 0,
    Pending     = 1,
    Done        = 2,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Saved outfits — named sets of wardrobe items worn and removed together.</summary>
    public List<Outfit> Outfits { get; set; } = new();

    // ── Wardrobe items ────────────────────────────────────────────────────────
    public List<WardrobeItem>         WardrobeItems { get; set; } = new();

    /// <summary>Slot name (EquipSlot.ToString()) → currently worn WardrobeItem ID.</summary>
    public Dictionary<string, Guid>   WornItems     { get; set; } = new();

    /// <summary>
    /// Tags created before anything has them, so a scheme can be laid out ahead of tagging.
    /// </summary>
    /// <remarks>
    /// Only a registry of names. Tags on items are the real record and need no entry here — this
    /// exists purely so a tag with no items yet still appears in the pickers and the filter tree,
    /// which are otherwise built by scanning what items already carry. Deleting an entry removes the
    /// name from that list and nothing else; a tag any item holds survives it.
    /// </remarks>
    public List<string> DefinedTags { get; set; } = new();

    /// <summary>
    /// Whether the offer of a starter set of styles has been answered, either by taking it or by
    /// turning it down.
    /// </summary>
    /// <remarks>
    /// Styles are only worth as much as the scheme behind them, and anyone who already has one in
    /// mind should not have to delete ten guesses first. So the set is offered rather than seeded,
    /// once, and never asked about again — declining is as final as accepting.
    /// </remarks>
    public bool StarterStylesOffered { get; set; }

    /// <summary>
    /// Variant groups the user has expanded, keyed on the original item's id. Everything else is
    /// drawn folded.
    /// </summary>
    /// <remarks>
    /// Expanded rather than collapsed is the stored state so a group that has never been touched
    /// starts folded, which is the point of grouping — a wardrobe of colour variants opens as one
    /// card each. An id whose item is gone is simply never matched, so stale entries are harmless.
    /// </remarks>
    public HashSet<string> ExpandedVariantGroups { get; set; } = new();

    /// <summary>How <c>Create variant of this item</c> names the copy it makes.</summary>
    /// <remarks>
    /// Defaults to the style variants have always used, so an existing wardrobe carries on naming
    /// them the way it already has. Only ever a starting point — the copy opens for editing, and the
    /// name is the first field in it.
    /// </remarks>
    public VariantNameStyle VariantNameStyle { get; set; } = VariantNameStyle.Suffix;

    /// <summary>Whether variants are folded into their original at all.</summary>
    /// <remarks>
    /// The way out for anyone who does not want the grouping, and the way to see everything at once
    /// without expanding each group in turn. Off means every item gets its own card, exactly as
    /// before variants were grouped.
    /// </remarks>
    public bool GroupVariants { get; set; } = true;

    /// <summary>Every tag the wardrobe knows: those on items, plus those created ahead of use.</summary>
    public List<string> AllTags() =>
        WardrobeItems.SelectMany(i => i.Tags)
            .Concat(DefinedTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── Base character ────────────────────────────────────────────────────────

    /// <summary>Saved base characters — what a strip leaves on. See <see cref="BaseCharacter"/>.</summary>
    public List<BaseCharacter> BaseCharacters { get; set; } = new();

    /// <summary>
    /// The base character currently in force, or null when stripping takes everything.
    /// </summary>
    /// <remarks>
    /// One at a time, and the same one everywhere: what is held back from a strip by hand is also
    /// what a screenshot session keeps between shots. Two settings for that would only ever be a way
    /// to have the session photograph a character that is not the one you set up.
    /// </remarks>
    public Guid? ActiveBaseCharacterId { get; set; }

    /// <summary>The active base character, or null when none is set or the saved one is gone.</summary>
    public BaseCharacter? ActiveBaseCharacter =>
        ActiveBaseCharacterId is { } id ? BaseCharacters.Find(b => b.Id == id) : null;

    /// <summary>
    /// Drops repeated entries from every base character's slots and items. Returns true if anything
    /// changed, so the caller can save.
    /// </summary>
    /// <remarks>
    /// Run on every load rather than once, because it is a repair and not a migration: a duplicate
    /// is never meaningful here — the same slot kept twice is the same slot, and the same item
    /// applied twice is one item and one wasted Penumbra reload — so there is no state this can
    /// destroy and no reason to trust that it has already been done.
    /// <para>
    /// One user's head item appeared four times over in a base character (11 August 2026). No path
    /// through the code was found that could add it more than once, and the state was gone before it
    /// could be captured, so this exists because the cause is unknown rather than because it is
    /// understood. If duplicates reappear in a config that has been through this, the fault is
    /// upstream of the list and worth chasing properly.
    /// </para>
    /// </remarks>
    public bool NormaliseBaseCharacters()
    {
        var changed = false;

        foreach (var baseChar in BaseCharacters)
        {
            var slots = baseChar.KeepSlots.Distinct(StringComparer.Ordinal).ToList();
            if (slots.Count != baseChar.KeepSlots.Count)
            {
                baseChar.KeepSlots = slots;
                changed = true;
            }

            var items = baseChar.ItemIds.Distinct().ToList();
            if (items.Count != baseChar.ItemIds.Count)
            {
                baseChar.ItemIds = items;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Whether the base character goes back on after the wardrobe drops to the in-game look.
    /// </summary>
    /// <remarks>
    /// Showing what the game actually has on means clearing every Glamourer override the wardrobe
    /// put there, and the character's own hair, skin and tail are among them. For most people those
    /// are not clothes to be taken off — they are the character — so they go back on by default.
    /// Turned off, the revert is absolute and shows the unmodded character the server sees.
    /// </remarks>
    public bool KeepBaseCharacterOnRevert { get; set; } = true;

    /// <summary>Folder on disk containing wardrobe item images, shown in the image browser.</summary>
    public string ImagesFolder { get; set; } = string.Empty;

    /// <summary>Folder where FFXIV saves screenshots, watched during screenshot sessions.</summary>
    public string ScreenshotsFolder { get; set; } = string.Empty;

    /// <summary>
    /// Strip every other worn item before equipping each item for its shot, so only the target
    /// item appears. Persisted so it applies to the first item of a session rather than only
    /// after the in-session checkbox is touched.
    /// </summary>
    public bool StripOthersDuringSession { get; set; }

    /// <summary>
    /// Shrink the main wardrobe window to a compact session view while a screenshot session is
    /// running, so it stays out of the way of the shot.
    /// </summary>
    public bool CompactDuringSession { get; set; }

    /// <summary>
    /// Collection pre-selected when importing an item. Empty means "use the first collection Penumbra reports".
    /// Set this to the collection your character actually uses — mods enabled in any other collection
    /// have no visible effect.
    /// </summary>
    public string DefaultCollection { get; set; } = string.Empty;

    /// <summary>
    /// Camera presets per slot, in the order they are shown. Key = EquipSlot.ToString().
    /// </summary>
    /// <remarks>
    /// Which one a screenshot session loads is marked on the preset itself
    /// (<see cref="CameraPreset.IsDefault"/>), not decided by its position, so the list can be kept
    /// in whatever order suits and the default chosen independently of it.
    /// </remarks>
    public Dictionary<string, List<CameraPreset>> SlotCameraPresetLists { get; set; } = new();

    /// <summary>
    /// The single preset per slot that presets used to be, kept only so it can be migrated.
    /// </summary>
    /// <remarks>
    /// Left in place rather than renamed away because the property name is the JSON key: an existing
    /// config still holds its presets under this name, and dropping it would silently lose them on
    /// the first launch after updating. Emptied by <see cref="MigrateCameraPresets"/>, which being
    /// empty is then what stops the migration running twice.
    /// </remarks>
    public Dictionary<string, CameraPreset> SlotCameraPresets { get; set; } = new();

    /// <summary>
    /// Key the outfit grid's camera presets are stored under, alongside the per-slot ones.
    /// </summary>
    /// <remarks>
    /// A reserved name rather than a second dictionary: the lists are keyed by string and no
    /// <see cref="Models.EquipSlot"/> is called this, so outfits get everything slots already have —
    /// several presets, a chosen default, export and import — for the price of a key.
    /// <para>
    /// One set for all outfits, not one per outfit. An outfit is a whole look, and the angle that
    /// frames one frames the next; per-outfit angles would be a preset list per card to maintain.
    /// </para>
    /// </remarks>
    public const string OutfitPresetKey = "Outfit";

    /// <summary>The presets saved for a slot, or an empty list. Read-only — mutate the dictionary.</summary>
    public IReadOnlyList<CameraPreset> PresetsFor(string slotKey) =>
        SlotCameraPresetLists.TryGetValue(slotKey, out var list)
            ? list
            : Array.Empty<CameraPreset>();

    /// <summary>The preset a screenshot session applies for a slot, if it has one.</summary>
    /// <remarks>
    /// Falls back to the first in the list when nothing is marked, so a slot whose default was
    /// deleted, or one migrated from before defaults could be chosen, still loads an angle instead
    /// of silently loading none.
    /// </remarks>
    public CameraPreset? DefaultPresetFor(string slotKey)
    {
        var list = PresetsFor(slotKey);
        if (list.Count == 0) return null;

        foreach (var preset in list)
            if (preset.IsDefault) return preset;

        return list[0];
    }

    /// <summary>Path to the JSON file used for exporting/importing camera presets.</summary>
    public string CameraPresetsPath { get; set; } = string.Empty;

    /// <summary>
    /// Manage mods that are not equipment — animations, VFX, mounts and minions.
    /// </summary>
    /// <remarks>
    /// Off by default: it adds three filter buttons and three slot-picker entries that mean nothing
    /// to a wardrobe of gear. Items already imported into those categories are kept but hidden from
    /// the grid while it is off, so turning it back on restores them intact.
    /// </remarks>
    public bool ModCategoriesEnabled { get; set; }

    /// <summary>
    /// Colour chosen for a tag or style, keyed by its full path, packed as 0xRRGGBB.
    /// </summary>
    /// <remarks>
    /// Kept here rather than on the tag itself because a tag has no object of its own — it is a
    /// string on the items that carry it, and pre-made ones are a string in
    /// <see cref="DefinedTags"/>. Absent means the tag is drawn the way it always was, so this is
    /// only ever an addition to the default. Deleting a tag drops its colour with it.
    /// </remarks>
    public Dictionary<string, uint> TagColours { get; set; } = new();

    /// <summary>Use the colours in <see cref="TagColours"/> where tags, styles and items are drawn.</summary>
    /// <remarks>
    /// On by default, which is safe because it changes nothing until a colour is actually chosen —
    /// and because the right-click menu that chooses one is hidden while this is off, so defaulting
    /// it off would hide the feature behind a setting nobody knew to look for. Turning it off keeps
    /// every colour already picked, ready for turning it back on.
    /// </remarks>
    public bool TagColoursEnabled { get; set; } = true;

    /// <summary>
    /// Keep Glamourer's advanced dyes with an outfit, on top of the game's two dye channels.
    /// </summary>
    /// <remarks>
    /// No longer experimental as of 1.5.1.1 — it has been run against real outfits — but still off
    /// until asked for. It rides on Glamourer's state JSON rather than on any advanced-dye API,
    /// because there is not one, so it remains the part most likely to be broken by a Glamourer
    /// update. Something that reaches into another plugin's internals is worth choosing rather than
    /// arriving in an update, so the changelog says it is here and this stays where it was.
    /// Turning it off keeps anything already captured — the rows stay on their outfits — but
    /// the wardrobe stops applying them, so a look goes back to plain dyes until it is turned on
    /// again. Nothing already on the character is undone at that moment: switching this off is an
    /// instruction to stop touching advanced dyes, and reverting them would be one last touch.
    /// </remarks>
    public bool AdvancedDyesEnabled { get; set; }

    /// <summary>Show an icon instead of the slot name where slots are displayed.</summary>
    public bool SlotIconsEnabled { get; set; }

    /// <summary>Which icon set to use when <see cref="SlotIconsEnabled"/> is on.</summary>
    public SlotIconStyle SlotIconStyle { get; set; } = SlotIconStyle.GameIcons;

    /// <summary>Folder holding user-supplied slot icons, each named after the slot it replaces.</summary>
    /// <remarks>
    /// Layered over whichever <see cref="SlotIconStyle"/> is selected rather than replacing it, so a
    /// folder holding two files replaces two icons and leaves the rest alone. Empty means off —
    /// there is no separate toggle, matching <see cref="ImagesFolder"/> and the other folder
    /// settings, which are a path plus a Clear button.
    /// </remarks>
    public string CustomIconFolder { get; set; } = string.Empty;

    /// <summary>Folder name of the installed icon pack in use, or empty for none.</summary>
    /// <remarks>
    /// The folder name rather than a path, so the packs folder can move with the plugin's config
    /// directory. A pack is layered under <see cref="CustomIconFolder"/> rather than replacing it:
    /// a pack is someone else's set, and the folder is where you override the two icons of theirs
    /// you did not like. See <see cref="Services.IconPackService"/>.
    /// </remarks>
    public string ActiveIconPack { get; set; } = string.Empty;

    /// <summary>Size multiplier for slot icons on item cards. 1 is the original fixed size.</summary>
    /// <remarks>
    /// Item cards grow taller to match, so a larger icon pushes nothing off the bottom of a card.
    /// Clamped on read by <see cref="Services.SlotIconService"/> rather than on write, so a value
    /// hand-edited into the config file cannot break the layout.
    /// </remarks>
    public float SlotIconScale { get; set; } = 1f;

    /// <summary>Size multiplier for slot icons on the filter row, separate from the cards.</summary>
    public float SlotIconRowScale { get; set; } = 1f;

    /// <summary>
    /// Size multiplier for the cards in the item and outfit grids. 1 is the original size.
    /// </summary>
    /// <remarks>
    /// Applied on top of the layout scale rather than replacing it, so a card still grows with
    /// Dalamud's Global Font Scale and this only says how much bigger than that you want it. Both
    /// grids read it: a wardrobe browsed by picture wants large cards everywhere, not just where
    /// there is a toggle. Clamped on read by <see cref="Ui.PluginUi"/>.
    /// </remarks>
    public float CardScale { get; set; } = 1f;

    /// <summary>
    /// Size multiplier for the outfit grid's cards, separate from <see cref="CardScale"/>.
    /// </summary>
    /// <remarks>
    /// Its own number because the two grids are looked at differently: an outfit preview is usually
    /// a full-body shot and wants the room, while an item card is a close-up and more of them on
    /// screen is the point. One slider for both traded them against each other.
    /// </remarks>
    public float OutfitCardScale { get; set; } = 1f;

    /// <summary>
    /// Edge length, in pixels, of the square images a screenshot session writes.
    /// </summary>
    /// <remarks>
    /// 512 is enough for a card and keeps a wardrobe of hundreds small on disk. Larger is for
    /// looking at closely — the quick view, or a preview blown up on a big screen — and costs
    /// roughly four times the space per step. Capped at what the screenshot actually has: the crop
    /// is as tall as the game window, so a 1080p shot cannot fill 2048 and is not stretched to try.
    /// </remarks>
    public int CapturedImageSize { get; set; } = 512;

    /// <summary>
    /// Draw outfit previews as 9:16 portraits rather than squares, and capture them that way.
    /// </summary>
    /// <remarks>
    /// Outfit previews are full-body shots, and a square crop of one spends most of the frame on the
    /// floor either side of the character. Matching GPose's own portrait mode keeps the character and
    /// drops the empty space, at the cost of a taller card.
    /// <para>
    /// Off by default, and it changes only what is drawn and what is captured from now on: pictures
    /// already assigned are centre-cropped to whichever shape is in use, so turning it on or off
    /// never spoils a wardrobe that was built under the other one.
    /// </para>
    /// </remarks>
    public bool PortraitOutfitPreviews { get; set; }

    /// <summary>Ordering applied to the item grid.</summary>
    public ItemSortMode SortMode { get; set; } = ItemSortMode.NameAsc;

    /// <summary>Ordering applied to the image browser.</summary>
    public ImageSortMode ImageSortMode { get; set; } = ImageSortMode.NameAsc;

    /// <summary>
    /// Switch the character to the hairstyle a hair mod replaces when applying it. Without this a
    /// hair mod only shows if that hairstyle already happens to be selected.
    /// </summary>
    public bool ApplyHairstyleWithHairMods { get; set; } = true;

    /// <summary>
    /// Hairstyle the character had before a wardrobe hair item changed it, restored on revert.
    /// Only used when no <see cref="RevertDesignId"/> is configured.
    /// </summary>
    public int? HairstyleBeforeWardrobe { get; set; }

    /// <summary>
    /// Glamourer design holding your character's normal look. Reverting a customisation mod
    /// re-applies only its customisation half, so equipment is left alone.
    /// </summary>
    public Guid? RevertDesignId { get; set; }

    /// <summary>Display name of <see cref="RevertDesignId"/>, so settings can show it without a lookup.</summary>
    public string RevertDesignName { get; set; } = string.Empty;

    /// <summary>
    /// Hide mods that are already imported from the import panel's mod list entirely, rather
    /// than showing them greyed out.
    /// </summary>
    public bool HideImportedMods { get; set; }

    /// <summary>
    /// Hide mods that are only ever attached as supplementary mods from the import panel's
    /// mod lists.
    /// </summary>
    public bool HideSupportMods { get; set; }

    // ── Backups ───────────────────────────────────────────────────────────────

    /// <summary>When true, the config file is copied to <see cref="BackupFolder"/> once an hour.</summary>
    public bool BackupEnabled { get; set; }

    /// <summary>Folder that hourly backup copies are written to.</summary>
    public string BackupFolder { get; set; } = string.Empty;

    /// <summary>How many backups to keep per file before the oldest are deleted.</summary>
    public int BackupKeepCount { get; set; } = 48;

    /// <summary>Last time a backup was *considered*, whether or not one was written.</summary>
    public DateTime LastBackupCheckUtc { get; set; }

    /// <summary>
    /// Hash of the content captured by the most recent backup. A new backup is only written when
    /// the current content hashes differently, so an idle wardrobe does not accumulate copies.
    /// </summary>
    public string LastBackupHash { get; set; } = string.Empty;

    /// <summary>First-run setup has been completed or skipped.</summary>
    public bool OnboardingCompleted { get; set; }

    // ── Changelog ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The plugin version whose changelog has already been shown, or empty for none.
    /// </summary>
    /// <remarks>
    /// Written the moment the changelog is shown rather than when it is closed, so an update
    /// announces itself once even if the window is dismissed by quitting the game. Written on a
    /// silent update too — someone who turns the notice off and back on months later wants the next
    /// update's notes, not a backlog.
    /// </remarks>
    public string LastSeenVersion { get; set; } = string.Empty;

    /// <summary>Show what changed the first time a new version runs.</summary>
    /// <remarks>
    /// On by default, and the one notice this plugin gives itself permission to raise unasked. The
    /// release page is where the notes live, and almost nobody updating from the plugin installer
    /// ever sees it — so anything needing doing after an update was only ever said out of sight.
    /// </remarks>
    public bool ShowChangelogOnUpdate { get; set; } = true;

    /// <summary>
    /// Mods the wardrobe enabled itself, as "collection|directory". Only these are turned off again
    /// when the last item using them comes off.
    /// </summary>
    /// <remarks>
    /// Keyed on the mod rather than the item, so a mod shared by items in two slots is claimed once
    /// by whichever wore first and stays claimed until the last of them is removed.
    /// Persisted because it is the only record of what the wardrobe switched on that survives a
    /// session — <see cref="WornItems"/> is cleared on load, and Penumbra keeps no record of who
    /// enabled a mod.
    /// </remarks>
    public HashSet<string> ModsEnabledByWardrobe { get; set; } = new();

    /// <summary>Whether the one-off explanation of mod ownership still needs showing.</summary>
    public OwnershipNoticeState ModOwnershipNotice { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    /// <summary>
    /// Decides whether to explain, once, that mods enabled by an older version are not claimed.
    /// </summary>
    /// <remarks>
    /// Ownership tracking starts empty rather than assuming the wardrobe enabled everything that
    /// happens to be on: claiming a mod the user turned on themselves is the very fault being fixed,
    /// where leaving one enabled is visible and undoable. The cost is that mods an older version
    /// switched on are never claimed and so never switched off, which looks like a bug unless it is
    /// said out loud — hence the notice. Only for configs that predate the field; a fresh install has
    /// nothing left behind and is marked done without ever seeing it.
    /// Returns true if anything changed, so the caller can save.
    /// </remarks>
    public bool MigrateModOwnership()
    {
        if (ModOwnershipNotice != OwnershipNoticeState.Unevaluated) return false;

        ModOwnershipNotice = WardrobeItems.Count > 0
            ? OwnershipNoticeState.Pending
            : OwnershipNoticeState.Done;
        return true;
    }

    /// <summary>
    /// Marks setup as done for configs that predate it, so existing users are not shown a first-run
    /// wizard. Anything already imported or configured is treated as evidence of a set-up plugin.
    /// Returns true if anything changed.
    /// </summary>
    public bool MigrateOnboarding()
    {
        if (OnboardingCompleted) return false;

        var alreadyInUse = WardrobeItems.Count > 0
                           || !string.IsNullOrEmpty(ImagesFolder)
                           || !string.IsNullOrEmpty(ScreenshotsFolder)
                           || !string.IsNullOrEmpty(DefaultCollection);
        if (!alreadyInUse) return false;

        OnboardingCompleted = true;
        return true;
    }

    /// <summary>
    /// Backfills <see cref="WardrobeItem.DateAdded"/> for items saved before that field existed.
    /// Imports append to the list, so existing order already reflects insertion order; sequential
    /// timestamps preserve it while keeping every legacy item older than anything imported later.
    /// Returns true if anything changed, so the caller can save.
    /// </summary>
    public bool MigrateDateAdded()
    {
        var epoch   = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var changed = false;
        for (var i = 0; i < WardrobeItems.Count; i++)
        {
            if (WardrobeItems[i].DateAdded != default) continue;
            WardrobeItems[i].DateAdded = epoch.AddSeconds(i);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Groups items saved before <see cref="WardrobeItem.VariantOfId"/> existed, by inferring which
    /// are variants of which. Returns true if anything changed.
    /// </summary>
    /// <remarks>
    /// Runs once — any item already carrying a <c>VariantOfId</c> means the wardrobe has been
    /// through this, so a group the user has since broken apart by hand is not silently reassembled
    /// on the next launch.
    /// <para>
    /// The rule is the one the Variants feature has always described: items in the same slot backed
    /// by exactly the same mods are the same piece in different options. It is a guess, and it is
    /// the only one available for items that never recorded where they came from — so the earliest
    /// by <see cref="WardrobeItem.DateAdded"/> is taken as the original, matching the order they
    /// were actually created in, and the edit panel can detach anything it gets wrong.
    /// </para>
    /// <para>
    /// Items with no mods are left alone: their signature is empty, and grouping every such item
    /// together would fold unrelated things into one card.
    /// </para>
    /// </remarks>
    public bool MigrateVariantGroups()
    {
        if (WardrobeItems.Any(i => i.VariantOfId.HasValue)) return false;

        var changed = false;

        var groups = WardrobeItems
            .Where(i => i.ModSignature().Length > 0)
            .GroupBy(i => $"{i.Slot}\n{i.ModSignature()}");

        foreach (var group in groups)
        {
            var members = group.OrderBy(i => i.DateAdded).ToList();
            if (members.Count < 2) continue;

            foreach (var variant in members.Skip(1))
            {
                variant.VariantOfId = members[0].Id;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Moves presets from the one-per-slot dictionary into the per-slot lists. Returns true if
    /// anything moved.
    /// </summary>
    /// <remarks>
    /// Becomes "Preset 1", ticked as its slot's default, so a screenshot session goes on using
    /// exactly the angle it always did. Nothing is lost in the move and nothing has to be decided —
    /// anyone happy with one preset per slot simply never saves a second.
    /// </remarks>
    public bool MigrateCameraPresets()
    {
        if (SlotCameraPresets.Count == 0) return false;

        foreach (var (slot, preset) in SlotCameraPresets)
        {
            // Never into a slot that already holds something. A slot with presets has either been
            // through this before or has been saved to since, and either way what would go in is a
            // second copy of an angle already on the list — indistinguishable from the first, and
            // sitting right next to it. Emptying the dictionary below is meant to prevent that on
            // its own; this is the guard that does not depend on the write afterwards succeeding.
            if (SlotCameraPresetLists.TryGetValue(slot, out var existing) && existing.Count > 0)
                continue;

            if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "Preset 1";
            preset.IsDefault = true;

            SlotCameraPresetLists[slot] = new List<CameraPreset> { preset };
        }

        SlotCameraPresets.Clear();
        return true;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void SavePresets()
    {
        if (string.IsNullOrEmpty(CameraPresetsPath)) return;
        try
        {
            var json = JsonSerializer.Serialize(SlotCameraPresetLists, JsonOpts);
            File.WriteAllText(CameraPresetsPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Reads the presets file, accepting both the current per-slot lists and the single-preset-per-
    /// slot shape files exported before slots could hold more than one.
    /// </summary>
    /// <remarks>
    /// This file is the share and backup format, so an older one has to keep working — someone
    /// restoring a backup from last month should not silently get nothing. The two shapes cannot be
    /// confused for each other: a slot's value is an array in one and an object in the other, so
    /// each parse fails cleanly against the wrong file rather than half-reading it.
    /// </remarks>
    public bool LoadPresets()
    {
        if (string.IsNullOrEmpty(CameraPresetsPath) || !File.Exists(CameraPresetsPath))
            return false;

        string json;
        try { json = File.ReadAllText(CameraPresetsPath); }
        catch { return false; }

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<CameraPreset>>>(json);
            if (loaded != null)
            {
                SlotCameraPresetLists.Clear();
                foreach (var (k, v) in loaded)
                    SlotCameraPresetLists[k] = v ?? new List<CameraPreset>();
                return true;
            }
        }
        catch (JsonException) { /* not the current shape — try the old one below */ }

        try
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, CameraPreset>>(json);
            if (legacy == null) return false;

            SlotCameraPresetLists.Clear();
            foreach (var (k, v) in legacy)
            {
                if (string.IsNullOrWhiteSpace(v.Name)) v.Name = "Preset 1";
                v.IsDefault = true;
                SlotCameraPresetLists[k] = new List<CameraPreset> { v };
            }
            return true;
        }
        catch { return false; }
    }
}
