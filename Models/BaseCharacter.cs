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
    /// Only its customisations are applied unless <see cref="DesignAppliesEquipment"/> says otherwise.
    /// A design is usually a whole look, gear included, and re-applying that on the way into a shot
    /// would put back the very clothes the strip just removed. The editor also has a button for
    /// applying one in full once, by hand.
    /// </remarks>
    public Guid? DesignId { get; set; }

    /// <summary>Display name of <see cref="DesignId"/>, so the UI needs no lookup to show it.</summary>
    public string DesignName { get; set; } = string.Empty;

    /// <summary>
    /// Apply the design's equipment as well as its customisations every time the base is put back.
    /// </summary>
    /// <remarks>
    /// The answer to issue #18: a base look that is partly gear — nails on the hands slot, a piece
    /// worn as skin — cannot be described by customisations alone, and building it out of wardrobe
    /// items means importing every piece of it. With this on, the design is the base look, and the
    /// order a session already runs in gives exactly what was asked for: strip, then the design, then
    /// the piece being photographed on top of it.
    /// <para>
    /// False by default, and deliberately the opposite of <see cref="Outfit.DesignAppliesEquipment"/>.
    /// An outfit is a whole look and its design usually is too, so applying gear is what is meant
    /// there. A base is what a strip strips down to, and a base character that already exists must
    /// not start dressing its owner because the plugin updated — a strip quietly leaving clothes on
    /// is precisely the surprise the base character exists to avoid.
    /// </para>
    /// </remarks>
    public bool DesignAppliesEquipment { get; set; }

    /// <summary>Whether the design's hairstyle applies along with the rest of it.</summary>
    /// <remarks>
    /// True, which is what this did before the switch existed — a design carries a hairstyle and it
    /// went on with everything else. Turn it off where the design is being applied for something
    /// other than its hair: a hair mod only replaces one hairstyle's files, so a design that switches
    /// you off that hairstyle leaves the mod enabled, correct and invisible.
    /// </remarks>
    public bool DesignAppliesHairstyle { get; set; } = true;

    /// <summary>
    /// Put the design back after every redraw, rather than only when the base is applied.
    /// </summary>
    /// <remarks>
    /// A design is already a live link and not a copy — <see cref="DesignId"/> is handed to
    /// Glamourer at the moment of applying, so an edit made to the design in Glamourer is in the
    /// next apply without anything being re-imported. What this changes is how often that moment
    /// comes round. Off, the design goes on when the base does: a strip, a screenshot session, a
    /// change of base, the button. On, it also goes back after any redraw of your character, which
    /// is what a redraw would otherwise take with it.
    /// <para>
    /// Off by default, because a base is usually a fallback rather than a thing being held in
    /// place, and because anyone editing their character in Glamourer directly would find their
    /// changes overwritten every time Penumbra reloaded — which is precisely the point when it is
    /// wanted and precisely the annoyance when it is not.
    /// </para>
    /// <para>
    /// Follows <see cref="DesignAppliesEquipment"/> for how much of the design goes back, so a base
    /// whose design is the whole look keeps the whole look, and one supplying only a face keeps
    /// only the face.
    /// </para>
    /// </remarks>
    public bool KeepDesignApplied { get; set; }

    /// <summary>
    /// Penumbra collection this base belongs to, empty when it belongs to no particular one.
    /// </summary>
    /// <remarks>
    /// For a character whose collection changes with them — see
    /// <see cref="Configuration.FollowActiveCollection"/> — where "which character am I on" and
    /// "which collection is in force" are the same question. When the collection changes to one a
    /// base names, that base becomes the active one, so the face, the ears and the skin follow the
    /// character without being picked by hand.
    /// <para>
    /// The design a base applies is the link to whatever else dresses that character. Glamourer
    /// cannot be asked which design is currently applied — a design becomes plain state the moment
    /// it lands — so pointing this base at the same design the character is set up with is what ties
    /// the two together, and it is done once.
    /// </para>
    /// <para>
    /// Bases naming no collection are left alone by all of this, and a collection no base names
    /// changes nothing: the base in force stays in force, rather than being cleared for want of a
    /// replacement.
    /// </para>
    /// </remarks>
    public string Collection { get; set; } = string.Empty;

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
