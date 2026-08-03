using System.Collections.Generic;

namespace WardrobePlugin.Models;

public enum EquipSlot
{
    Unknown  = 0,
    Head     = 1,
    Body     = 2,
    Hands    = 3,
    Legs     = 4,
    Feet     = 5,
    Ears     = 6,
    Neck     = 7,
    Wrists   = 8,
    RingRight = 9,
    RingLeft  = 10,
    MainHand  = 11,
    OffHand   = 12,

    // Character customisation, not equipment. These replace parts of the character model itself
    // (chara/human/...) rather than a game item, so nothing is equipped in Glamourer for them —
    // enabling the Penumbra mod is the whole effect.
    Hair      = 20,
    Face      = 21,
    Tail      = 22,
    VieraEars = 23,
    Skin      = 24,

    /// <summary>
    /// A mod that is not tied to any equipment slot — shared character textures such as
    /// piercings, tattoos and face paints (chara/common/...). Wearing it simply toggles the mod.
    /// </summary>
    Other     = 25,

    // Mods that are not worn on the character at all. Like customisation slots these are pure
    // Penumbra toggles with no Glamourer item, but unlike them they are not exclusive per slot:
    // several emote mods can be active at once. What one displaces is decided by
    // WardrobeItem.Replaces instead. Hidden unless Configuration.ModCategoriesEnabled is on.
    Emote     = 30,
    Vfx       = 31,
    Mount     = 32,
}

/// <summary>Icon set used to represent equipment slots. Values are persisted, so do not renumber.</summary>
public enum SlotIconStyle
{
    GameIcons   = 0,
    FontAwesome = 1,
}

public static class EquipSlotEx
{
    public static string DisplayName(this EquipSlot s) => s switch
    {
        EquipSlot.Head      => "Head",
        EquipSlot.Body      => "Body",
        EquipSlot.Hands     => "Hands",
        EquipSlot.Legs      => "Legs",
        EquipSlot.Feet      => "Feet",
        EquipSlot.Ears      => "Ears",
        EquipSlot.Neck      => "Neck",
        EquipSlot.Wrists    => "Wrists",
        EquipSlot.RingRight => "Ring (R)",
        EquipSlot.RingLeft  => "Ring (L)",
        EquipSlot.MainHand  => "Main Hand",
        EquipSlot.OffHand   => "Off Hand",
        EquipSlot.Hair      => "Hair",
        EquipSlot.Face      => "Face",
        EquipSlot.Tail      => "Tail",
        EquipSlot.VieraEars => "Ears (Viera)",
        EquipSlot.Skin      => "Skin",
        EquipSlot.Other     => "Other",
        EquipSlot.Emote     => "Emote",
        EquipSlot.Vfx       => "VFX",
        EquipSlot.Mount     => "Mount / Minion",
        _                   => "Unknown",
    };

    /// <summary>
    /// True for slots that replace the character model itself rather than an equipped item.
    /// These have no game item to equip, and no "Emperor's New" equivalent to strip to —
    /// wearing one is purely a matter of enabling its Penumbra mod.
    /// </summary>
    public static bool IsCustomization(this EquipSlot s) => s is
        EquipSlot.Hair or EquipSlot.Face or EquipSlot.Tail or EquipSlot.VieraEars or EquipSlot.Skin
        or EquipSlot.Other;

    /// <summary>
    /// True for mods that are not worn on the character — emotes, VFX, mounts and minions.
    /// </summary>
    /// <remarks>
    /// These share the customisation slots' "enabling the Penumbra mod is the whole effect"
    /// behaviour, but not their exclusivity: nothing stops a dance mod and an idle-pose mod being
    /// active together, so the slot alone cannot decide what a new one displaces. See
    /// <see cref="WardrobeItem.WornKey"/>.
    /// </remarks>
    public static bool IsModCategory(this EquipSlot s) => s is
        EquipSlot.Emote or EquipSlot.Vfx or EquipSlot.Mount;

    /// <summary>
    /// True when there is no game item behind the slot, so Glamourer has nothing to equip or strip
    /// and enabling the Penumbra mod is the entire effect.
    /// </summary>
    public static bool IsModOnly(this EquipSlot s) => s.IsCustomization() || s.IsModCategory();

    /// <summary>
    /// Wording for the buttons that turn an item on and off. Gear is equipped, customisation is
    /// applied over the character you already have, and a mod category is simply switched on —
    /// "Unequip" reads wrong for an emote nobody is wearing.
    /// </summary>
    /// <param name="gearWear">The on-label for equipment, which differs by context.</param>
    public static (string Wear, string Remove) ActionLabels(this EquipSlot s, string gearWear = "Wear") =>
        s.IsModCategory()   ? ("Enable", "Disable")
        : s.IsCustomization() ? ("Apply", "Revert")
        : (gearWear, "Unequip");

    public static readonly EquipSlot[] All =
    {
        EquipSlot.Head, EquipSlot.Body, EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet,
        EquipSlot.Ears, EquipSlot.Neck, EquipSlot.Wrists,
        EquipSlot.RingRight, EquipSlot.RingLeft,
        EquipSlot.MainHand, EquipSlot.OffHand,
        EquipSlot.Hair, EquipSlot.Face, EquipSlot.Tail, EquipSlot.VieraEars, EquipSlot.Skin,
    };

    /// <summary>Mod categories, in the order they appear in the filter bar and in pickers.</summary>
    public static readonly EquipSlot[] ModCategories =
    {
        EquipSlot.Emote, EquipSlot.Vfx, EquipSlot.Mount,
    };

    /// <summary>
    /// Slots offered by a picker. A superset of <see cref="All"/>: <see cref="EquipSlot.Other"/> is
    /// only ever reached by hand or by detection, and the mod categories appear only when the
    /// caller opts in.
    /// </summary>
    /// <param name="ensure">
    /// Appended if it would otherwise be missing, so a picker opened on an item whose slot is
    /// currently hidden still has an entry to select — without it the combo index would be -1 and
    /// saving would index outside the array.
    /// </param>
    public static EquipSlot[] Choices(bool includeModCategories, EquipSlot? ensure = null)
    {
        var list = new List<EquipSlot>(All) { EquipSlot.Other };
        if (includeModCategories) list.AddRange(ModCategories);
        if (ensure is { } s && s != EquipSlot.Unknown && !list.Contains(s)) list.Add(s);
        return list.ToArray();
    }
}
