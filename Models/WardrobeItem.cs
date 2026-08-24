using System;
using System.Collections.Generic;

namespace WardrobePlugin.Models;

/// <summary>Ordering applied to the item grid. Values are persisted, so do not renumber.</summary>
/// <summary>Ordering applied to the image browser. Values are persisted, so do not renumber.</summary>
public enum ImageSortMode
{
    NameAsc     = 0,
    NameDesc    = 1,
    NewestFirst = 2,
    OldestFirst = 3,
}

/// <summary>
/// How a new variant's name is built from the item it came from. Values are persisted, so do not
/// renumber.
/// </summary>
public enum VariantNameStyle
{
    /// <summary>"Silk Top (variant)" — what variants were always named before this was a choice.</summary>
    Suffix    = 0,

    /// <summary>"Silk Top (Variant-1)", counting up from the variants the item already has.</summary>
    Numbered  = 1,

    /// <summary>"Silk Top (Variant-A)", lettered rather than numbered.</summary>
    Lettered  = 2,

    /// <summary>"Silk Top (07/08/26 - 21:45)" — when the variant was made.</summary>
    Timestamp = 3,
}

public enum ItemSortMode
{
    NameAsc       = 0,
    NameDesc      = 1,
    DateAddedDesc = 2,
    DateAddedAsc  = 3,
}

[Serializable]
public class WardrobeItem : IImageOwner
{
    public Guid          Id       { get; set; } = Guid.NewGuid();
    public string        Name     { get; set; } = "New Item";
    public EquipSlot     Slot     { get; set; } = EquipSlot.Unknown;
    public string?       ImagePath { get; set; }

    /// <inheritdoc cref="IImageOwner.ExtraImages"/>
    public List<string>  ExtraImages { get; set; } = new();

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

    /// <summary>
    /// Every customisation number this mod covers, keyed by model race code ("0101", "1801", …) — the
    /// face numbers of a face mod, and so on.
    /// </summary>
    /// <remarks>
    /// A list per race, not one number, because a mod routinely covers several: option groups let a
    /// single face mod ship f0001 through f0004, and often for more than one race beside. This is
    /// what makes it possible to say whether the face a <see cref="DesignId"/> would set is one the
    /// mod actually replaces, without warning about a mod that covers it perfectly well under a
    /// number that happened not to be read first.
    /// <para>
    /// Empty on items imported before this was recorded, and on anything that is not customisation.
    /// Empty means no check rather than a failed one: nothing is claimed about a mod nobody has
    /// re-read. Press Re-detect to fill it in.
    /// </para>
    /// </remarks>
    public Dictionary<string, List<ushort>> CustomizeIdsByRace { get; set; } = new();

    /// <summary>
    /// What this mod replaces within its category, for the mod categories that have no equipment
    /// slot to be exclusive on — the .pap file name for an animation, the monster id for a mount.
    /// </summary>
    /// <remarks>
    /// Two items sharing a key swap each other out on wear, exactly as two body mods in the same
    /// slot do. A blank key leaves the item independent of everything else in its category, which
    /// is the right default for VFX: those overlap in ways the file paths do not reveal.
    /// Detected on import and editable afterwards, so two mods for the same animation can be lined up
    /// by hand when their file names differ.
    /// </remarks>
    public string? Replaces { get; set; }

    /// <summary>
    /// Which layer of a customisation slot this item occupies, so two mods doing different jobs to
    /// the same part of the character can be worn together.
    /// </summary>
    /// <remarks>
    /// A face sculpt and a face retexture are both on the Face slot and are not alternatives to each
    /// other — the texture goes on the sculpt. Keying customisation purely on the slot made them
    /// mutually exclusive, so applying one took the other off and there was no way to have both.
    /// Items sharing a layer still displace each other, which is what keeps two sculpts, or two
    /// retextures, behaving as the alternatives they are.
    /// <para>
    /// Detected on import as <c>sculpt</c> when the mod ships a model for the slot and
    /// <c>texture</c> when it ships only materials and textures, and editable afterwards — it is free
    /// text, so a mod doing something the detection has no word for can be given one (<c>lashes</c>,
    /// <c>brows</c>) and will then only ever displace others marked the same.
    /// </para>
    /// <para>
    /// Blank means the item takes the whole slot, displacing everything else in it. That is what
    /// every item imported before this existed has, and deliberately so: the alternative was every
    /// old item becoming independent overnight, which would stop two face sculpts displacing each
    /// other and leave both enabled at once. Press Re-detect to fill it in.
    /// </para>
    /// <para>
    /// Ignored outside <see cref="EquipSlotExtensions.IsCustomization"/>. Equipment slots are
    /// exclusive because the character has one head, and mod categories have
    /// <see cref="Replaces"/> for the same job.
    /// </para>
    /// </remarks>
    public string? Layer { get; set; }

    /// <summary>
    /// A Glamourer design applied along with this item, for a mod that only shows on a character
    /// set up a particular way.
    /// </summary>
    /// <remarks>
    /// A face sculpt replaces the files of one specific face number, so it is invisible on a
    /// character wearing any other — the mod is enabled, everything is correct, and the face does not
    /// change. Hair has been handled since the beginning by reading the hairstyle number out of the
    /// mod and setting it; a face has no such single number to set, because what makes a sculpt look
    /// right is the face number together with the skin, eye and hair colouring around it. A design is
    /// the thing that already holds all of that.
    /// <para>
    /// A live link and never a copy, as everywhere else a design is referenced: only the id is
    /// stored, and it is handed to Glamourer at the moment of wearing, so editing the design in
    /// Glamourer puts the edit in the next apply.
    /// </para>
    /// <para>
    /// Null means the item applies no design, which is what every item has until somebody picks one.
    /// Offered only for <see cref="EquipSlotExtensions.IsCustomization"/> slots: gear has a Glamourer
    /// item of its own to equip, and outfits and base characters are where a design belongs when it
    /// is the whole look rather than one piece's prerequisite.
    /// </para>
    /// </remarks>
    public Guid? DesignId { get; set; }

    /// <summary>Display name of <see cref="DesignId"/>, so the UI needs no lookup to show it.</summary>
    public string DesignName { get; set; } = string.Empty;

    /// <summary>Whether <see cref="DesignId"/>'s gear is applied as well as its customisations.</summary>
    /// <remarks>
    /// False by default, and for a stronger reason than on a base character: this design is a
    /// prerequisite for one face or one body, and putting a wardrobe item on is not asking to be
    /// dressed. On, for the case where the design really is the whole character and the sculpt is
    /// part of it.
    /// </remarks>
    public bool DesignAppliesEquipment { get; set; }

    /// <summary>Whether <see cref="DesignId"/>'s hairstyle is applied along with the rest of it.</summary>
    /// <remarks>
    /// Off by default, and the odd one out among the design switches: gear is opt-in because it does
    /// too much, and the hairstyle is opt-in because it undoes something the wardrobe has usually
    /// just done on purpose. A hair mod only replaces one hairstyle's files, so wearing one switches
    /// the character to that hairstyle — and a face sculpt worn afterwards, carrying a design saved
    /// with some other hair, would put that hair straight back and leave the hair mod enabled and
    /// invisible. The design is being applied for its face, not for its hair.
    /// <para>
    /// Off, the hairstyle in force is read before the design is applied and written back after, so
    /// whatever you had — a hair mod's number or your own — survives. On, the design's hairstyle
    /// applies with everything else, for a design that really is the whole character.
    /// </para>
    /// </remarks>
    public bool DesignAppliesHairstyle { get; set; }

    /// <summary>
    /// Free text about the item — where it came from, what it goes with, anything worth
    /// remembering. Web links in it are clickable.
    /// </summary>
    /// <remarks>
    /// Kept separate from tags because it is prose rather than a filter: tags say what an item is,
    /// notes say the things about it that no field could anticipate. Searched along with the name,
    /// so writing the creator's name in here is enough to find the item by it later.
    /// </remarks>
    public string? Notes { get; set; }

    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Items worn and removed alongside this one — a top and the gloves that finish it, a hair mod
    /// and the accessory that sits in it.
    /// </summary>
    /// <remarks>
    /// Mutual: linking two items writes each into the other's list, so unlinking from either side
    /// breaks it and neither can end up pulling in something that does not know about it. Followed
    /// one hop only — what an item drags in is exactly the list stored here, never what its partners
    /// are in turn linked to, so a link can never reach further than the one place it is shown.
    /// <para>
    /// Ids rather than references because the config is serialised, and they may go stale: an item
    /// deleted while linked leaves its id behind in its partners. Every read resolves through
    /// <see cref="Configuration.WardrobeItems"/> and drops what no longer exists.
    /// </para>
    /// </remarks>
    public List<Guid> LinkedItemIds { get; set; } = new();

    /// <summary>
    /// The item this one is a variant of — the same mods in another colour or style. Null on an
    /// item that is not a variant, which includes the original of a group.
    /// </summary>
    /// <remarks>
    /// Groups are flat: making a variant of a variant records the original, not the variant it was
    /// copied from, so an item is either an original or one hop from one. A tree would have to
    /// decide what a count on the original means and how deep to fold, for a distinction — variant
    /// of a variant — that means nothing when they are all the same mods with different options.
    /// <para>
    /// Recorded on <c>Create variant of this item</c>, and backfilled once for items that predate
    /// the field by <see cref="Configuration.MigrateVariantGroups"/>, which has to infer it.
    /// </para>
    /// </remarks>
    public Guid? VariantOfId { get; set; }

    /// <summary>
    /// Identifies the set of mods behind this item, for spotting variants among items saved before
    /// they recorded which one they came from.
    /// </summary>
    /// <remarks>
    /// Collection and directory per mod, sorted so the order they were attached in does not matter,
    /// and lowercased to match how mods are compared everywhere else. Deliberately excludes options:
    /// differing options are precisely what makes two items variants rather than duplicates.
    /// Empty when the item has no usable mods, which callers treat as "cannot be grouped" rather
    /// than as a group of its own.
    /// </remarks>
    public string ModSignature()
    {
        var parts = new List<string>();
        foreach (var mod in Mods)
        {
            if (string.IsNullOrEmpty(mod.ModDirectory)) continue;
            parts.Add($"{mod.Collection}|{mod.ModDirectory}".ToLowerInvariant());
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Whether applying this item forces a Penumbra redraw of the character. Null until the toggle
    /// is touched, which reads as the default for the item's slot.
    /// </summary>
    /// <remarks>
    /// Nullable so items saved before the toggle existed behave as the slot says rather than as a
    /// stored false — a hair mod imported last month needs the redraw exactly as much as one
    /// imported today, and neither of them was ever asked. Read through
    /// <see cref="ForcesRedraw"/>; the raw field is for the editors that set it.
    /// </remarks>
    public bool? ForceRedraw { get; set; }

    /// <summary>
    /// Whether wearing this item redraws the character, taking the slot's default when the item has
    /// no opinion of its own.
    /// </summary>
    /// <remarks>
    /// Removal is not symmetrical: a removal that switched mods off without swapping a Glamourer
    /// item redraws whatever this says, or the mod would stay on the character after being taken
    /// off. Turning this off asks for no redraw on apply, not for the item to linger.
    /// </remarks>
    public bool ForcesRedraw() => ForceRedraw ?? Slot.RedrawsByDefault();

    /// <summary>Marked as a favourite by the user; can be filtered on in the grid.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// When this item was imported (UTC). Items saved before this field existed are backfilled
    /// on load by <see cref="Configuration.MigrateDateAdded"/>, which preserves their list order.
    /// </summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Key this item occupies in <see cref="Configuration.WornItems"/>, which is what decides
    /// whether wearing it displaces something already worn.
    /// </summary>
    /// <remarks>
    /// Equipment is exclusive per slot, so the slot name is key enough. Mod categories are not —
    /// several animation mods can be active at once — so they key on what they replace instead,
    /// falling back to the item's own id when that is unknown so the item is simply independent
    /// rather than colliding with every other item in its category.
    /// <para>
    /// Customisation sits between the two: exclusive by default, but a slot can carry more than one
    /// kind of mod at once — a sculpt and the texture painted on it — so a <see cref="Layer"/> may
    /// narrow the key to that kind. Blank leaves the item on the bare slot name, exclusive as
    /// before, which is why an item that has never been given a layer behaves exactly as it did.
    /// </para>
    /// A method rather than a property so it is not written into the saved config.
    /// </remarks>
    public string WornKey() => Slot.IsModCategory()
        ? $"{Slot}:{(string.IsNullOrWhiteSpace(Replaces) ? Id.ToString() : Replaces.Trim())}"
        : Slot.IsCustomization() && !string.IsNullOrWhiteSpace(Layer)
            ? $"{Slot}:{Layer.Trim()}"
            : Slot.ToString();
}
