using System;
using System.Collections.Generic;

namespace WardrobePlugin.Models;

/// <summary>Ordering applied to the item grid. Values are persisted, so do not renumber.</summary>
public enum ItemSortMode
{
    NameAsc       = 0,
    NameDesc      = 1,
    DateAddedDesc = 2,
    DateAddedAsc  = 3,
}

[Serializable]
public class WardrobeItem
{
    public Guid          Id       { get; set; } = Guid.NewGuid();
    public string        Name     { get; set; } = "New Item";
    public EquipSlot     Slot     { get; set; } = EquipSlot.Unknown;
    public string?       ImagePath { get; set; }

    /// <summary>
    /// All mods required for this item.
    /// Index 0 = primary mod; subsequent entries = upscales, compatibility patches, etc.
    /// </summary>
    public List<ModReference> Mods { get; set; } = new();

    /// <summary>
    /// FFXIV item row ID to equip in Glamourer when wearing this item (auto-detected from mod files).
    /// </summary>
    public ulong? GlamourerItemId { get; set; }

    /// <summary>Human-readable name of the detected game item, stored for display purposes.</summary>
    public string? GlamourerItemName { get; set; }

    /// <summary>
    /// Equipment model set ID detected from the mod's file paths. Kept so the list of game items
    /// sharing that model can be re-offered later, letting the auto-detected item be overridden
    /// without re-analysing the mod. Null on items imported before this was recorded.
    /// </summary>
    public ushort? ModelSetId { get; set; }

    /// <summary>
    /// Hairstyle numbers this mod replaces, keyed by model race code ("0101", "1801", …).
    /// </summary>
    /// <remarks>
    /// Hairstyle numbering is per-race: one mod is commonly hair 151 on most races but 11 on
    /// Hrothgar and 5 on Viera, so a single number cannot be right for everyone.
    /// </remarks>
    public Dictionary<string, ushort> HairIdByRace { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    /// <summary>Marked as a favourite by the user; can be filtered on in the grid.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// When this item was imported (UTC). Items saved before this field existed are backfilled
    /// on load by <see cref="Configuration.MigrateDateAdded"/>, which preserves their list order.
    /// </summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
}
