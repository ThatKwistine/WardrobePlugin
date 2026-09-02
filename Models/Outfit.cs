using System;
using System.Collections.Generic;
using System.Linq;

namespace WardrobePlugin.Models;

/// <summary>
/// A named set of wardrobe items that can be worn or removed together.
/// </summary>
/// <remarks>
/// Stores wardrobe item IDs rather than a Glamourer state blob. Wearing an outfit therefore goes
/// through the normal per-item path, which enables each item's Penumbra mods and applies their
/// options — the part a Glamourer design cannot do on its own.
/// </remarks>
/// <summary>The two dye channels an equipment piece can carry.</summary>
/// <remarks>
/// Also part of the share format — see <see cref="SharedOutfit.Dyes"/>. It carries nothing local,
/// which is what makes that safe; a field added here that does mean something only on this machine
/// would start travelling to other people's installs without anything saying so.
/// </remarks>
[Serializable]
public class OutfitDye
{
    public byte Stain1 { get; set; }
    public byte Stain2 { get; set; }

    /// <summary>
    /// Glamourer advanced dye rows for this piece: packed key to the row exactly as Glamourer wrote it.
    /// </summary>
    /// <remarks>
    /// Stored as opaque text on purpose. A row describes a material's colour table — diffuse,
    /// specular, gloss and the rest — and none of that has to be understood to keep it and hand it
    /// back. Glamourer owns the format and the editor; the wardrobe owns only the fact that these
    /// rows belong to this piece in this outfit. The key encodes the slot, so the rows kept here are
    /// filtered to this item's slot when they are captured.
    /// </remarks>
    public Dictionary<string, string> Advanced { get; set; } = new();

    /// <summary>True when this carries nothing at all — neither channel, nor an advanced row.</summary>
    public bool IsUndyed => Stain1 == 0 && Stain2 == 0 && Advanced.Count == 0;
}

/// <summary>
/// A game item worn in a slot the wardrobe has nothing in — plain gear, no mod behind it.
/// </summary>
/// <remarks>
/// Stored by item ID rather than as a wardrobe item because there is nothing to manage: no mod to
/// enable, no options to apply, nothing to detect. Wearing one is a single Glamourer call. Keeping
/// them on the outfit is what lets a look be saved when only some of it — or none of it — is modded.
/// <para>
/// Also part of the share format, under the same caveat as <see cref="OutfitDye"/>: a game item id
/// and two dye channels mean the same thing on every install, and anything added here that does not
/// would travel silently.
/// </para>
/// </remarks>
[Serializable]
public class VanillaPiece
{
    public ulong ItemId { get; set; }

    /// <summary>The item's name at the time it was saved, for showing the outfit without a lookup.</summary>
    public string Name { get; set; } = string.Empty;

    public byte Stain1 { get; set; }
    public byte Stain2 { get; set; }
}

[Serializable]
public class Outfit : IImageOwner
{
    public Guid    Id        { get; set; } = Guid.NewGuid();
    public string  Name      { get; set; } = "New Outfit";
    public string? ImagePath { get; set; }

    /// <inheritdoc cref="IImageOwner.ExtraImages"/>
    public List<string> ExtraImages { get; set; } = new();

    /// <summary>Wardrobe item IDs in this outfit. Items deleted since are skipped when worn.</summary>
    public List<Guid> ItemIds { get; set; } = new();

    /// <summary>
    /// Dye channels to apply per item, keyed by item ID. Absent means undyed.
    /// </summary>
    /// <remarks>
    /// Held on the outfit rather than the item so the same piece can be dyed differently in
    /// different outfits, which is the point of saving a look.
    /// </remarks>
    public Dictionary<string, OutfitDye> Dyes { get; set; } = new();

    /// <summary>
    /// Plain game items in the slots this outfit's own items do not fill, keyed by slot name.
    /// </summary>
    /// <remarks>
    /// Captured from Glamourer when the outfit is saved or updated from what is worn, so a look made
    /// of two mods and six vanilla pieces saves as the look it actually is. Only slots no item in the
    /// outfit covers are stored: a wardrobe item is the better record wherever there is one, since it
    /// carries the mod and its options as well as the game item.
    /// </remarks>
    public Dictionary<string, VanillaPiece> VanillaItems { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 1–20 when this outfit mirrors one of the game's own glamour plates, null for a normal outfit.
    /// </summary>
    /// <remarks>
    /// A plate outfit is an ordinary outfit whose <see cref="VanillaItems"/> were read out of the
    /// game rather than off the character, which is why it wears and photographs through exactly
    /// the same paths. What the ID adds is ownership: the contents belong to the plate and are
    /// edited in-game, so the wardrobe shows them read-only and keeps them in step by resyncing.
    /// </remarks>
    public int? GlamourPlateId { get; set; }

    /// <summary>When the contents were last read from the game, for the resync controls to show.</summary>
    /// <remarks>
    /// Only the time is stored. Whether the plate has since been changed is answered by comparing
    /// <see cref="VanillaItems"/> against the live plate, so there is nothing kept here that could
    /// fall out of step with the pieces it describes.
    /// </remarks>
    public DateTime? PlateSyncedAt { get; set; }

    public bool IsGlamourPlate => GlamourPlateId is not null;

    /// <summary>
    /// The Glamourer design this card is linked to, or null for an outfit built out of items.
    /// </summary>
    /// <remarks>
    /// Not a copy of the design and not a sync of one — a link. Glamourer owns which designs exist and
    /// what they are called, so the card is created, renamed and removed by
    /// <see cref="Services.WardrobeService.ReconcileDesignCards"/> following the design list rather than
    /// by anyone pressing a button. What this record holds is the wardrobe's side of the card: the
    /// pictures, the tags, the dyes, and above all the <see cref="ItemIds"/> — a design carries gear and
    /// colouring but knows nothing about Penumbra, so attaching items is how the mods that belong with a
    /// look get enabled along with it.
    /// <para>
    /// Wearing one applies the design first and the items over the top, so an item always wins the slot
    /// it occupies. A design deleted in Glamourer takes an empty card with it and leaves a card with
    /// anything attached behind, for
    /// <see cref="Services.WardrobeService.StrandedDesignCards"/> to report.
    /// </para>
    /// <para>
    /// The whole feature is behind <see cref="Configuration.ShowGlamourerDesigns"/>, which is the only
    /// choice there is to make about it.
    /// </para>
    /// </remarks>
    public Guid? DesignId { get; set; }

    /// <summary>
    /// The design's name in Glamourer, for showing the card's badge without an IPC call.
    /// </summary>
    /// <remarks>
    /// Kept in step with the live name, as <see cref="Name"/> is: the card is a link, so its title is
    /// the design's title and follows it. Someone who wants a card called something else renames the
    /// design — which is the honest answer, since that name is what they will see in Glamourer too.
    /// </remarks>
    public string DesignName { get; set; } = string.Empty;

    /// <summary>
    /// Apply the design's equipment as well as its customisations when this outfit is worn.
    /// </summary>
    /// <remarks>
    /// True because a design usually is the look, gear included, and an outfit is a look. Turned off
    /// for a design that only holds a face, a body or colouring: applying its equipment would empty
    /// the slots the outfit's own items and vanilla pieces are there to fill, and there would be
    /// nothing on screen to say why.
    /// </remarks>
    public bool DesignAppliesEquipment { get; set; } = true;

    /// <summary>Whether the design's hairstyle applies along with the rest of it.</summary>
    /// <remarks>
    /// True, which is what this did before the switch existed — a design carries a hairstyle and it
    /// went on with everything else. Turn it off where the design is being applied for something
    /// other than its hair: a hair mod only replaces one hairstyle's files, so a design that switches
    /// you off that hairstyle leaves the mod enabled, correct and invisible.
    /// </remarks>
    public bool DesignAppliesHairstyle { get; set; } = true;

    /// <summary>
    /// Show or hide headgear when this outfit is worn, or null to leave it as it is.
    /// </summary>
    /// <remarks>
    /// Part of the look rather than a setting: a hood is the outfit, and a hat that ruins the hair
    /// under it is not something to remember to switch off by hand every time. Glamourer already owns
    /// the toggle — this only says what an outfit wants it set to.
    /// <para>
    /// Null means leave it alone, and is the default, so every outfit that already exists keeps
    /// behaving exactly as it does now. It is a third state rather than a false, because "this outfit
    /// has no opinion" and "this outfit wants the hat off" are different things: the first must not
    /// undo a toggle the wearer set themselves a moment earlier.
    /// </para>
    /// </remarks>
    public bool? HatVisible { get; set; }

    /// <summary>Show or hide the weapon when this outfit is worn, or null to leave it alone.</summary>
    /// <inheritdoc cref="HatVisible" path="/remarks"/>
    public bool? WeaponVisible { get; set; }

    /// <inheritdoc cref="WardrobeItem.SharedFromId"/>
    public Guid? SharedFromId { get; set; }

    /// <summary>The <see cref="Id"/> this was copied from, on an outfit brought in from another wardrobe.</summary>
    /// <remarks>Provenance, and what stops the copy offer making a second one every time it is used.</remarks>
    public Guid? CopiedFromId { get; set; }

    /// <summary>
    /// A copy of this outfit for another wardrobe, with its items pointed at that wardrobe's copies.
    /// </summary>
    /// <param name="items">
    /// Old item id to new. Anything absent is dropped rather than carried: an id the other wardrobe
    /// has never heard of would resolve to nothing every time the outfit was worn.
    /// </param>
    /// <remarks>
    /// The plate link is deliberately not carried. <see cref="GlamourPlateId"/> names a slot in one
    /// character's glamour plates, and the character being copied to has their own twenty — pointing
    /// a copy at plate 4 because the original was plate 4 would claim a sync that was never made.
    /// The Glamourer design is kept, because designs belong to Glamourer rather than to a character.
    /// </remarks>
    public Outfit CopyForWardrobe(IReadOnlyDictionary<Guid, Guid> items)
    {
        var copy = new Outfit
        {
            Id                     = Guid.NewGuid(),
            Name                   = Name,
            ImagePath              = ImagePath,
            ExtraImages            = new List<string>(ExtraImages),
            VanillaItems           = VanillaItems.ToDictionary(kv => kv.Key, kv => kv.Value),
            Tags                   = new List<string>(Tags),
            DateAdded              = DateTime.UtcNow,
            DesignId               = DesignId,
            DesignName             = DesignName,
            DesignAppliesEquipment = DesignAppliesEquipment,
            DesignAppliesHairstyle = DesignAppliesHairstyle,
            HatVisible             = HatVisible,
            WeaponVisible          = WeaponVisible,
            SharedFromId           = SharedFromId,
            CopiedFromId           = Id,
            Hidden                 = Hidden,
        };

        foreach (var id in ItemIds)
            if (items.TryGetValue(id, out var mapped))
                copy.ItemIds.Add(mapped);

        // Keyed by item id, so the keys move with the items
        foreach (var (key, dye) in Dyes)
            if (Guid.TryParse(key, out var id) && items.TryGetValue(id, out var mapped))
                copy.Dyes[mapped.ToString()] = dye;

        return copy;
    }

    /// <summary>
    /// Kept out of the grid until you ask to see it.
    /// </summary>
    /// <remarks>
    /// Issue #26, for the design cards it is mostly about: turning designs on gives every design in
    /// Glamourer a card, and a Glamourer library of several hundred buries the outfits somebody
    /// actually built under a mirror of a list they were not trying to manage here.
    /// <para>
    /// Hiding rather than deleting, and on any outfit rather than only a design card. A design card
    /// deleted while its design still exists comes straight back on the next reconcile, so delete is
    /// not even available as an answer — and for an outfit somebody built by hand, "I do not want to
    /// look at this right now" and "destroy it" should not be the same button. Everything attached
    /// to a hidden outfit is still there, and showing hidden ones brings it back exactly as it was.
    /// </para>
    /// </remarks>
    public bool Hidden { get; set; }

    public bool IsDesign => DesignId is not null;
}
