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

    public bool IsUndyed => Stain1 == 0 && Stain2 == 0;
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

    public List<string> Tags { get; set; } = new();

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
}
