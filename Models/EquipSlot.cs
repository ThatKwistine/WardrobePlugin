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
        _                   => "Unknown",
    };

    /// <summary>
    /// True for slots that replace the character model itself rather than an equipped item.
    /// These have no game item to equip, and no "Emperor's New" equivalent to strip to —
    /// wearing one is purely a matter of enabling its Penumbra mod.
    /// </summary>
    public static bool IsCustomization(this EquipSlot s) => s is
        EquipSlot.Hair or EquipSlot.Face or EquipSlot.Tail or EquipSlot.VieraEars or EquipSlot.Skin;

    public static readonly EquipSlot[] All =
    {
        EquipSlot.Head, EquipSlot.Body, EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet,
        EquipSlot.Ears, EquipSlot.Neck, EquipSlot.Wrists,
        EquipSlot.RingRight, EquipSlot.RingLeft,
        EquipSlot.MainHand, EquipSlot.OffHand,
        EquipSlot.Hair, EquipSlot.Face, EquipSlot.Tail, EquipSlot.VieraEars, EquipSlot.Skin,
    };
}
