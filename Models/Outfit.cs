using System;
using System.Collections.Generic;

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
public class Outfit
{
    public Guid    Id        { get; set; } = Guid.NewGuid();
    public string  Name      { get; set; } = "New Outfit";
    public string? ImagePath { get; set; }

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
}
