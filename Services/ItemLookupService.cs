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
    /// <param name="secondaryId">
    /// The other half of the model id: a weapon's <c>b</c> number, or the material variant of a
    /// piece of gear. Zero when the mod's files did not say, which asks for the set alone.
    /// </param>
    /// <remarks>
    /// The set ID alone does not name a weapon. <c>w2501</c> is every Gunbreaker arm in the game —
    /// 155 items across 41 different <c>b</c> numbers — so matching on it and taking the first row
    /// answered "Revolver" for every gunblade mod there is, and the mod then had nothing to attach
    /// to because Penumbra's redirect is for the <c>b</c> the mod actually replaces.
    /// <para>
    /// Gear is the same story told with the other number. Bits 16-31 are the variant there, and a
    /// set is reused across patches for every recolour of it: <c>e0110</c> is the Hellhound armour
    /// at variant 1, the Grey Hound at 2 and the Shadowhound at 3, and one <c>e6234</c> carries ten
    /// Neotunics. 11,067 of the game's 20,238 non-weapon equippable items sit behind a variant the
    /// set-only match could never reach, and a mod for one of them was answered with the oldest
    /// item of the family — whose <c>material/v0001</c> files the mod does not replace, so wearing
    /// it showed the unmodded piece.
    /// </para>
    /// <para>
    /// Narrowing is not softened when it finds nothing: a mod replacing a variant no player item
    /// uses is not served by naming an item that will not show it. The edit panel's "Set game item
    /// manually" search is the way out of that, and says so.
    /// </para>
    /// </remarks>
    public IList<(ulong ItemId, string ItemName)> FindItems(ushort equipSetId, EquipSlot slot,
                                                            ushort secondaryId = 0)
    {
        // Facewear is not in the Item sheet at all, so the whole question is asked of another one.
        // Answered here rather than at the call sites: every picker, import row and re-detect
        // already asks this for a slot, and the slot is what says which sheet holds the answer.
        if (slot.IsFacewear()) return FindFacewear(equipSetId, secondaryId);

        if (_items == null || equipSetId == 0)
            return Array.Empty<(ulong, string)>();

        var results = new List<(ulong, string)>();
        foreach (var item in _items)
        {
            // ModelMain packs: bits 0-15 = primary equip set ID, bits 16-31 = secondary — the
            // variant on equipment, and a weapon's b number
            if ((ushort)(item.ModelMain & 0xFFFF) != equipSetId) continue;
            if (secondaryId != 0 && (ushort)((item.ModelMain >> 16) & 0xFFFF) != secondaryId) continue;
            if (!MatchesSlot(item, slot)) continue;

            var name = item.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            results.Add(((ulong)item.RowId, name));
        }

        results.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return results;
    }

    /// <summary>The display name of a game item, or empty when the ID is not a real item.</summary>
    /// <remarks>
    /// For showing a vanilla piece saved into an outfit without storing anything but its ID. Names
    /// are cached because an outfit panel asks for the same handful every frame it draws.
    /// </remarks>
    public string GetItemName(ulong itemId)
    {
        if (_items == null || itemId == 0 || itemId > uint.MaxValue) return string.Empty;
        if (_itemNames.TryGetValue(itemId, out var cached)) return cached;

        var name = _items.GetRowOrDefault((uint)itemId)?.Name.ExtractText() ?? string.Empty;
        _itemNames[itemId] = name;
        return name;
    }

    /// <summary>
    /// The display name of a stored item, asked of the sheet its slot belongs to.
    /// </summary>
    /// <remarks>
    /// Facewear ids are <c>Glasses</c> rows numbered from 1, so they collide with the low end of the
    /// <c>Item</c> sheet: id 5 is "Oval Spectacles" on a facewear item and a bronze ingot's
    /// neighbour anywhere else. Nothing in the id says which, so the slot has to come with it —
    /// which is why anything holding both should call this rather than
    /// <see cref="GetItemName(ulong)"/>.
    /// </remarks>
    public string GetItemName(ulong itemId, EquipSlot slot) =>
        slot.IsFacewear() ? GetFacewearName(itemId) : GetItemName(itemId);

    private readonly Dictionary<ulong, string> _itemNames = new();

    private string _lastSearchKey = string.Empty;
    private IList<(ulong ItemId, string ItemName)> _lastSearchResults = Array.Empty<(ulong, string)>();

    /// <summary>
    /// Searches equippable items by name for a slot, for manually assigning the game item when
    /// detection cannot find one.
    /// </summary>
    /// <remarks>
    /// Needed for mods that skin a specific existing item rather than replacing a model — a
    /// piercing hung on "The Emperor's New Ring", for instance. Such a mod is only visible while
    /// that item is equipped, so the wardrobe has to know to equip it.
    /// Results are capped and cached by query, since this scans the whole Item sheet.
    /// </remarks>
    public IList<(ulong ItemId, string ItemName)> SearchItems(string query, EquipSlot slot, int limit = 60)
    {
        if (_items == null || string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return Array.Empty<(ulong, string)>();

        var trimmed = query.Trim();
        var key     = $"{slot}|{trimmed}";
        if (key == _lastSearchKey) return _lastSearchResults;

        if (slot.IsFacewear())
        {
            _lastSearchKey     = key;
            _lastSearchResults = SearchFacewear(trimmed, limit);
            return _lastSearchResults;
        }

        var results = new List<(ulong, string)>();
        foreach (var item in _items)
        {
            if (!MatchesSlot(item, slot)) continue;

            var name = item.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;
            if (name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) < 0) continue;

            results.Add(((ulong)item.RowId, name));
            if (results.Count >= limit) break;
        }

        _lastSearchKey     = key;
        _lastSearchResults = results;
        return results;
    }

    /// <summary>Returns the best single match (first result), or null if none found.</summary>
    public (ulong ItemId, string ItemName)? FindBestItem(ushort equipSetId, EquipSlot slot,
                                                        ushort secondaryId = 0)
    {
        var list = FindItems(equipSetId, slot, secondaryId);
        return list.Count > 0 ? list[0] : null;
    }

    // ── Facewear ──────────────────────────────────────────────────────────────

    private ExcelSheet<Glasses>? _glasses;
    private ExcelSheet<Glasses>? GlassesSheet => _glasses ??= _data.GetExcelSheet<Glasses>();

    private HashSet<ushort>? _facewearSets;

    /// <summary>
    /// Every equipment set number that belongs to facewear rather than to head gear.
    /// </summary>
    /// <remarks>
    /// Read from the <c>Glasses</c> sheet rather than written down, because the numbers are the
    /// game's to change: they are 5501-5565 today and a patch that adds a sixty-second style will
    /// add 5566 without telling anyone. Nothing else needs to be updated for that to work.
    /// <para>
    /// Checked against the whole Item sheet when this was written: no head item shares a set with
    /// facewear, so a set number answers the question on its own.
    /// </para>
    /// </remarks>
    private HashSet<ushort> FacewearSets()
    {
        if (_facewearSets != null) return _facewearSets;

        _facewearSets = new HashSet<ushort>();
        if (GlassesSheet is not { } sheet) return _facewearSets;

        foreach (var row in sheet)
        {
            var set = (ushort)(row.Model & 0xFFFF);
            if (set != 0) _facewearSets.Add(set);
        }

        return _facewearSets;
    }

    /// <summary>
    /// Whether an <c>e{set}</c> number names facewear, which wears the head suffix in its paths.
    /// </summary>
    /// <remarks>
    /// What separates a glasses mod from a hat mod: both ship
    /// <c>chara/equipment/e{set}/…_met.mdl</c>, and only the set number says which is which. Handed
    /// to <see cref="ModAnalysisService"/> as a delegate so that class stays constructible without
    /// game data.
    /// </remarks>
    public bool IsFacewearSet(ushort equipSetId) => FacewearSets().Contains(equipSetId);

    /// <summary>
    /// Facewear whose model matches a set, optionally narrowed to one variant.
    /// </summary>
    /// <remarks>
    /// The <c>Glasses</c> sheet packs its model number exactly as <c>Item.ModelMain</c> does — set
    /// in bits 0-15, variant in bits 16-31 — so one set is a whole family of colours: <c>e5501</c>
    /// is every pair of oval spectacles there is, told apart by the <c>material/vNNNN</c> folder a
    /// mod replaces, the same as gear.
    /// </remarks>
    public IList<(ulong ItemId, string ItemName)> FindFacewear(ushort equipSetId, ushort variant = 0)
    {
        if (GlassesSheet is not { } sheet || equipSetId == 0)
            return Array.Empty<(ulong, string)>();

        var results = new List<(ulong, string)>();
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            if ((ushort)(row.Model & 0xFFFF) != equipSetId) continue;
            if (variant != 0 && (ushort)((row.Model >> 16) & 0xFFFF) != variant) continue;

            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            results.Add((row.RowId, name));
        }

        results.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return results;
    }

    /// <summary>Searches facewear by name, for setting one by hand.</summary>
    private IList<(ulong ItemId, string ItemName)> SearchFacewear(string trimmed, int limit)
    {
        if (GlassesSheet is not { } sheet) return Array.Empty<(ulong, string)>();

        var results = new List<(ulong, string)>();
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;

            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) < 0) continue;

            results.Add((row.RowId, name));
            if (results.Count >= limit) break;
        }

        return results;
    }

    private readonly Dictionary<ulong, string> _facewearNames = new();

    /// <summary>The display name of a facewear row, or empty when the id names none.</summary>
    public string GetFacewearName(ulong itemId)
    {
        if (itemId == 0 || itemId > uint.MaxValue) return string.Empty;
        if (_facewearNames.TryGetValue(itemId, out var cached)) return cached;

        var name = GlassesSheet?.GetRowOrDefault((uint)itemId)?.Name.ExtractText() ?? string.Empty;
        _facewearNames[itemId] = name;
        return name;
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
    /// <remarks>
    /// Facewear answers 0, which is not an Emperor's New anything: Glamourer's bonus slot takes 0
    /// as "wear nothing here", so the slot has a real empty of its own and needs no invisible item
    /// standing in for one. Answering it here rather than at the four call sites that clear a slot
    /// means they go on treating every slot alike.
    /// </remarks>
    public static ulong? FindEmperorsNewItem(EquipSlot slot) =>
        slot.IsFacewear() ? 0UL
        : EmperorsNewIds.TryGetValue(slot, out var id) ? id : null;

    private IList<(byte Id, string Name, uint Colour)>? _stains;

    /// <summary>
    /// All dyes, as ID, display name and packed RGB colour, with an "undyed" entry at ID 0.
    /// </summary>
    /// <remarks>
    /// The Stain sheet's Color is 0xRRGGBB. It is converted to ImGui's 0xAABBGGRR here so callers
    /// can pass it straight to a colour swatch.
    /// </remarks>
    public IList<(byte Id, string Name, uint Colour)> GetStains()
    {
        if (_stains != null) return _stains;

        var list = new List<(byte, string, uint)> { (0, "Undyed", 0xFF404040) };

        var sheet = _data.GetExcelSheet<Stain>();
        if (sheet != null)
        {
            foreach (var stain in sheet)
            {
                if (stain.RowId == 0 || stain.RowId > byte.MaxValue) continue;

                var name = stain.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var rgb = stain.Color;
                var abgr = 0xFF000000u
                           | ((rgb & 0x0000FFu) << 16)   // B
                           | (rgb & 0x00FF00u)           // G
                           | ((rgb & 0xFF0000u) >> 16);  // R

                list.Add(((byte)stain.RowId, name, abgr));
            }
        }

        _stains = list;
        return _stains;
    }

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

        BuildFacewearIcon();

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

    /// <summary>
    /// The symbol for the facewear slot, taken from the game's own facewear artwork.
    /// </summary>
    /// <remarks>
    /// Facewear belongs to no item UI category, so the trick every gear slot uses — read the
    /// category icon off that slot's Emperor's New piece — has nothing to read. The
    /// <c>GlassesStyle</c> sheet is the game's own list of facewear styles and each carries an
    /// icon, so the first of them stands in for the category, exactly as
    /// <see cref="BuildCustomizeIcons"/> takes the first hairstyle's icon to stand for hair.
    /// <para>
    /// Ordered by the sheet's own <c>Order</c> column rather than by row id, so the icon is the one
    /// the game itself puts at the head of the list. Falls back to any facewear row's icon if the
    /// style sheet is unreadable, and to nothing at all if neither is — which drops the slot to its
    /// FontAwesome glyph like any other slot with no game icon.
    /// </para>
    /// </remarks>
    private void BuildFacewearIcon()
    {
        var styles = _data.GetExcelSheet<GlassesStyle>();
        if (styles != null)
        {
            uint  best      = 0;
            ushort bestOrder = ushort.MaxValue;

            foreach (var style in styles)
            {
                if (style.Icon <= 0) continue;
                if (style.Order > bestOrder) continue;

                bestOrder = style.Order;
                best      = (uint)style.Icon;
            }

            if (best != 0)
            {
                _slotIcons[EquipSlot.Facewear] = best;
                return;
            }
        }

        if (GlassesSheet is not { } sheet) return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.Icon <= 0) continue;
            _slotIcons[EquipSlot.Facewear] = (uint)row.Icon;
            return;
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
