using System;

namespace WardrobePlugin.Models;

/// <summary>
/// What the character was wearing the last time the wardrobe looked, kept so it can be put back on.
/// </summary>
/// <remarks>
/// Glamourer keeps nothing across a restart and Penumbra keeps no record of who asked for a mod, so
/// closing the game has always ended with the look on the floor: the mods stayed enabled, the
/// character came back in their own gear, and dressing again meant clicking through the wardrobe
/// from the start. This is the wardrobe's own note of what it was doing, written while the game runs
/// and read at the next login.
/// <para>
/// The look itself is an ordinary <see cref="Outfit"/>, which is the whole reason this record is
/// small: an outfit already knows how to hold items, dyes, the plain gear filling the slots they do
/// not, and a say over the hat and the weapon — and
/// <see cref="Services.WardrobeService.WearOutfit"/> already knows how to put all of that back on.
/// Nothing about restoring needs to be a second implementation of wearing.
/// </para>
/// <para>
/// Deliberately not one of <see cref="Configuration.Outfits"/>. It is a note about a session rather
/// than something anybody made, it is rewritten every time the look changes, and a card for it in
/// the grid would be a card nobody can keep.
/// </para>
/// </remarks>
[Serializable]
public class WornSnapshot
{
    /// <summary>The look, held as an outfit so it wears through the ordinary path.</summary>
    public Outfit Look { get; set; } = new();

    /// <summary>
    /// The outfit that was on, when the look came from one rather than from loose items.
    /// </summary>
    /// <remarks>
    /// Kept beside the copy rather than instead of it. The copy is what gets worn — an outfit
    /// deleted or emptied since must not take the restore down with it — while the id is what makes
    /// the wardrobe agree with itself afterwards: the card reads as worn, and a redraw re-applies
    /// that outfit's dyes, because the wardrobe knows which outfit is on and not merely which items.
    /// </remarks>
    public Guid? OutfitId { get; set; }

    /// <summary>The character this was captured on, for showing in the offer.</summary>
    public string Character { get; set; } = string.Empty;

    /// <summary>Their home world's row id, which is the half of the name that makes it unique.</summary>
    /// <remarks>
    /// Stored as the id rather than the world's name because it is only ever compared, never read
    /// out: two characters called the same thing on two worlds are a different look each, and the
    /// name on its own would hand one of them the other's clothes.
    /// </remarks>
    public uint World { get; set; }

    /// <summary>When the look was last written down, so the offer can say how long ago it was.</summary>
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when there is nothing here worth putting back on.</summary>
    public bool IsEmpty => Look.ItemIds.Count == 0 && Look.VanillaItems.Count == 0
                                                   && Look.DesignId is null;

    /// <summary>Whether this was captured on the character now in front of us.</summary>
    /// <remarks>
    /// A snapshot with no character recorded matches anybody: that is what the record written from
    /// the old <see cref="Configuration.WornItems"/> on the first load after updating looks like, and
    /// refusing to restore it would make the feature do nothing at all until its second session.
    /// </remarks>
    public bool Matches(string character, uint world) =>
        string.IsNullOrEmpty(Character) ||
        (Character.Equals(character, StringComparison.Ordinal) && World == world);
}
