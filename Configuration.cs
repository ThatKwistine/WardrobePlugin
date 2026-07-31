using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Configuration;
using WardrobePlugin.Models;

namespace WardrobePlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── Legacy outfit system (kept for backward compatibility) ────────────────
    public List<Outfit>  Outfits              { get; set; } = new();
    public Guid?         CurrentlyWornOutfitId { get; set; }

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

    // ── Wardrobe sharing ──────────────────────────────────────────────────────

    /// <summary>
    /// Reveals unfinished features in the settings panel. Wardrobe sharing sits behind this:
    /// it needs a backend that does not exist yet, so it is hidden unless explicitly opted into.
    /// </summary>
    public bool ExperimentalFeatures { get; set; }

    /// <summary>Show an icon instead of the slot name where slots are displayed.</summary>
    public bool SlotIconsEnabled { get; set; }

    /// <summary>Which icon set to use when <see cref="SlotIconsEnabled"/> is on.</summary>
    public SlotIconStyle SlotIconStyle { get; set; } = SlotIconStyle.GameIcons;

    /// <summary>WebSocket server URL for the wardrobe sharing backend.</summary>
    public string       ShareServerUrl { get; set; } = string.Empty;

    /// <summary>Player names permitted to send remote wear/unequip commands.</summary>
    public List<string> ShareAllowlist { get; set; } = new();

    /// <summary>Ordering applied to the item grid.</summary>
    public ItemSortMode SortMode { get; set; } = ItemSortMode.NameAsc;

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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

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
