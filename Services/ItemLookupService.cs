using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// Looks up FFXIV game items by equipment set ID (the number in chara/equipment/e{ID}/ paths).
/// Used to automatically detect which item a Penumbra mod replaces so Glamourer can equip it.
/// </summary>
public class ItemLookupService
{
    private readonly ExcelSheet<Item>? _items;
    private readonly IDataManager      _data;

    public ItemLookupService(IDataManager dataManager)
    {
        _data  = dataManager;
        _items = dataManager.GetExcelSheet<Item>();
    }

    /// <summary>
    /// Returns game items whose model set ID matches <paramref name="equipSetId"/> for the given slot.
    /// Results are sorted by row ID ascending (base items first, HQ/variant items after).
    /// </summary>
    public IList<(ulong ItemId, string ItemName)> FindItems(ushort equipSetId, EquipSlot slot)
    {
        if (_items == null || equipSetId == 0)
            return Array.Empty<(ulong, string)>();

        var results = new List<(ulong, string)>();
        foreach (var item in _items)
        {
            // ModelMain packs: bits 0-15 = primary equip set ID, bits 16-31 = secondary (variant)
            if ((ushort)(item.ModelMain & 0xFFFF) != equipSetId) continue;
            if (!MatchesSlot(item, slot)) continue;

            var name = item.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            results.Add(((ulong)item.RowId, name));
        }

        results.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return results;
    }

    /// <summary>Returns the best single match (first result), or null if none found.</summary>
    public (ulong ItemId, string ItemName)? FindBestItem(ushort equipSetId, EquipSlot slot)
    {
        var list = FindItems(equipSetId, slot);
        return list.Count > 0 ? list[0] : null;
    }

    // "The Emperor's New ___" item IDs — these make a slot appear empty in Glamourer.
    // IDs sourced from Garland Tools (https://www.garlandtools.org).
    private static readonly Dictionary<EquipSlot, ulong> EmperorsNewIds = new()
    {
        { EquipSlot.Head,      10032 },
        { EquipSlot.Body,      10033 },
        { EquipSlot.Hands,     10034 },
        { EquipSlot.Legs,      10035 },
        { EquipSlot.Feet,      10036 },
        { EquipSlot.Ears,       9293 },
        { EquipSlot.Neck,       9292 },
        { EquipSlot.Wrists,     9294 },
        { EquipSlot.RingRight,  9295 },
        { EquipSlot.RingLeft,   9295 },
        { EquipSlot.MainHand,  13775 },
        { EquipSlot.OffHand,   30067 },
    };

    /// <summary>
    /// Returns the row ID of the Emperor's New item for the given slot (makes the slot invisible).
    /// </summary>
    public static ulong? FindEmperorsNewItem(EquipSlot slot) =>
        EmperorsNewIds.TryGetValue(slot, out var id) ? id : null;

    private readonly Dictionary<EquipSlot, uint> _slotIcons = new();
    private bool _slotIconsBuilt;

    /// <summary>
    /// Game icon ID representing an equipment slot, read from that slot's Emperor's New item.
    /// </summary>
    /// <remarks>
    /// Taken from game data rather than hardcoded UI icon numbers, which are easy to get wrong and
    /// fail silently. The Emperor's New pieces are the game's own "empty slot" gear and have a
    /// distinct icon per slot, so they double as slot symbols.
    /// Returns null for customisation slots, which have no item and therefore no icon.
    /// </remarks>
    public uint? GetSlotIcon(EquipSlot slot)
    {
        BuildSlotIcons();
        return _slotIcons.TryGetValue(slot, out var icon) ? icon : null;
    }

    private readonly Dictionary<EquipSlot, uint> _customizeIcons = new();
    private bool _customizeIconsBuilt;

    /// <summary>
    /// Game icon representing a customisation slot, taken from character-creation data.
    /// </summary>
    /// <remarks>
    /// Hair and face paint are the only customisation options the game gives icons to, via
    /// <c>HairMakeType</c>'s hairstyle and facepaint lists. Tail, ear shape and skin are numeric
    /// options with no artwork, so they return null and fall back to FontAwesome.
    /// The first entry of each list is used as a stand-in symbol for the whole category.
    /// </remarks>
    public uint? GetCustomizationIcon(EquipSlot slot)
    {
        BuildCustomizeIcons();
        return _customizeIcons.TryGetValue(slot, out var icon) ? icon : null;
    }

    private void BuildCustomizeIcons()
    {
        if (_customizeIconsBuilt) return;
        _customizeIconsBuilt = true;

        var sheet = _data.GetExcelSheet<HairMakeType>();
        if (sheet == null) return;

        foreach (var row in sheet)
        {
            foreach (var h in row.CharaMakeStruct)
            {
                foreach (var opt in h.SubMenuParam)
                {
                    if (opt == 0) continue;
                    var cm = _data.GetExcelSheet<CharaMakeCustomize>()?.GetRowOrDefault(opt);
                    if (cm is { Icon: > 0 } v)
                    {
                        _customizeIcons.TryAdd(EquipSlot.Hair, v.Icon);
                        break;
                    }
                }
                if (_customizeIcons.ContainsKey(EquipSlot.Hair)) break;
            }
            if (_customizeIcons.ContainsKey(EquipSlot.Hair)) break;
        }
    }

    private void BuildSlotIcons()
    {
        if (_slotIconsBuilt) return;
        _slotIconsBuilt = true;
        if (_items == null) return;

        var wanted = new HashSet<ulong>(EmperorsNewIds.Values);

        foreach (var item in _items)
        {
            if (!wanted.Contains(item.RowId)) continue;

            // Prefer the item's UI category icon — a clean, high-contrast symbol for the gear
            // type. The Emperor's New item's own icon is a deliberately faint silhouette and is
            // barely visible at button size.
            var cat  = item.ItemUICategory.ValueNullable;
            var icon = cat is { Icon: > 0 } c ? (uint)c.Icon : item.Icon;
            if (icon == 0) continue;

            // Both ring slots map to the same item, so assign every slot using this row
            foreach (var (slot, id) in EmperorsNewIds)
                if (id == item.RowId)
                    _slotIcons[slot] = icon;
        }
    }

    private static bool MatchesSlot(Item item, EquipSlot slot)
    {
        var cat = item.EquipSlotCategory.ValueNullable;
        if (cat == null) return false;
        var c = cat.Value;
        return slot switch
        {
            EquipSlot.Head      => c.Head     > 0,
            EquipSlot.Body      => c.Body     > 0,
            EquipSlot.Hands     => c.Gloves   > 0,
            EquipSlot.Legs      => c.Legs     > 0,
            EquipSlot.Feet      => c.Feet     > 0,
            EquipSlot.Ears      => c.Ears     > 0,
            EquipSlot.Neck      => c.Neck     > 0,
            EquipSlot.Wrists    => c.Wrists   > 0,
            EquipSlot.RingRight => c.FingerR  > 0,
            EquipSlot.RingLeft  => c.FingerL  > 0,
            EquipSlot.MainHand  => c.MainHand > 0,
            EquipSlot.OffHand   => c.OffHand  > 0,
            _                   => false,
        };
    }
}
