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
    /// Equipment and customisation are exclusive per slot, so the slot name is key enough. Mod
    /// categories are not — several animation mods can be active at once — so they key on what they
    /// replace instead, falling back to the item's own id when that is unknown so the item is
    /// simply independent rather than colliding with every other item in its category.
    /// A method rather than a property so it is not written into the saved config.
    /// </remarks>
    public string WornKey() => Slot.IsModCategory()
        ? $"{Slot}:{(string.IsNullOrWhiteSpace(Replaces) ? Id.ToString() : Replaces.Trim())}"
        : Slot.ToString();
}
