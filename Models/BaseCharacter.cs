using System;
using System.Collections.Generic;

namespace WardrobePlugin.Models;

/// <summary>
/// The character underneath the clothes: the customisation mods, permanent accessory slots and
/// Glamourer design that survive a strip.
/// </summary>
/// <remarks>
/// Stripping exists so one piece can be photographed on its own, and it takes the character with
/// it — the tail mod worn on a ring, the ear mod on a pair of earrings, the hair and skin that make
/// the character theirs. A base character is the answer to "everything except this is not mine to
/// remove": the wardrobe strips down to it rather than down to nothing (issue #14).
/// <para>
/// Several can be saved and one is active at a time, so a second character, or the same character
/// with different ears, is a matter of picking one rather than re-ticking slots.
/// </para>
/// </remarks>
public class BaseCharacter
{
    public Guid   Id   { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Base character";

    /// <summary>
    /// Equipment slots a strip never empties, as <see cref="EquipSlot"/> names.
    /// </summary>
    /// <remarks>
    /// Stored as strings because that is what the rest of the config does with slots — see
    /// <see cref="Configuration.WornItems"/> — and because an unrecognised name from a newer version
    /// then parses as "no such slot" rather than as whatever number happens to sit there.
    /// </remarks>
    public List<string> KeepSlots { get; set; } = new();

    /// <summary>
    /// Wardrobe items this base wears: applied when it is, and never taken off by a strip.
    /// </summary>
    /// <remarks>
    /// Usually the customisation mods — hair, skin, tail — plus whatever gear item is carrying a
    /// mod that is really part of the character. Their slots are protected on their behalf, so a
    /// tail worn on a ring needs no separate tick in <see cref="KeepSlots"/>.
    /// </remarks>
    public List<Guid> ItemIds { get; set; } = new();

    /// <summary>Glamourer design holding this character's face and colouring, or null for none.</summary>
    /// <remarks>
    /// Only its customisations are ever applied automatically. A design is usually a whole look,
    /// gear included, and re-applying that on the way into a shot would put back the very clothes
    /// the strip just removed. The editor has a button for applying one in full, by hand.
    /// </remarks>
    public Guid? DesignId { get; set; }

    /// <summary>Display name of <see cref="DesignId"/>, so the UI needs no lookup to show it.</summary>
    public string DesignName { get; set; } = string.Empty;

    public bool Keeps(EquipSlot slot) => KeepSlots.Contains(slot.ToString());

    public void SetKeep(EquipSlot slot, bool keep)
    {
        var name = slot.ToString();
        if (keep) { if (!KeepSlots.Contains(name)) KeepSlots.Add(name); }
        else      { KeepSlots.Remove(name); }
    }

    /// <summary>True when this base would change nothing — nothing kept, worn or applied.</summary>
    public bool IsEmpty => KeepSlots.Count == 0 && ItemIds.Count == 0 && DesignId == null;
}
