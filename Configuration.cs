using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>Per-slot camera presets applied automatically during screenshot sessions. Key = EquipSlot.ToString().</summary>
    public Dictionary<string, CameraPreset> SlotCameraPresets { get; set; } = new();

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

    /// <summary>Show an icon instead of the slot name where slots are displayed.</summary>
    public bool SlotIconsEnabled { get; set; }

    /// <summary>Which icon set to use when <see cref="SlotIconsEnabled"/> is on.</summary>
    public SlotIconStyle SlotIconStyle { get; set; } = SlotIconStyle.GameIcons;

    /// <summary>Ordering applied to the item grid.</summary>
    public ItemSortMode SortMode { get; set; } = ItemSortMode.NameAsc;

    /// <summary>Ordering applied to the image browser.</summary>
    public ImageSortMode ImageSortMode { get; set; } = ImageSortMode.NameAsc;

    /// <summary>
    /// Draw outfit cards larger than item cards. Outfit previews are usually full-body shots
    /// rather than close-ups, so they need the room; off matches the item grid exactly.
    /// </summary>
    public bool LargeOutfitCards { get; set; } = true;

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

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void SavePresets()
    {
        if (string.IsNullOrEmpty(CameraPresetsPath)) return;
        try
        {
            var json = JsonSerializer.Serialize(SlotCameraPresets, JsonOpts);
            File.WriteAllText(CameraPresetsPath, json);
        }
        catch { }
    }

    public bool LoadPresets()
    {
        if (string.IsNullOrEmpty(CameraPresetsPath) || !File.Exists(CameraPresetsPath))
            return false;
        try
        {
            var json   = File.ReadAllText(CameraPresetsPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CameraPreset>>(json);
            if (loaded == null) return false;
            SlotCameraPresets.Clear();
            foreach (var (k, v) in loaded)
                SlotCameraPresets[k] = v;
            return true;
        }
        catch { return false; }
    }
}
