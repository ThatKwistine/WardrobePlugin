using System;
using System.Collections.Generic;

namespace WardrobePlugin.Models;

/// <summary>
/// One character's wardrobe: their items, outfits, bases, angles and pictures.
/// </summary>
/// <remarks>
/// Everything here is what one character owns. Everything not here — tags and styles, the folders
/// other than pictures, every setting — is shared across all of them, because a taxonomy that meant
/// something different on each character would make a copied item's tags meaningless and would have
/// to be built again from nothing for every alt.
/// <para>
/// A wardrobe exists whether or not per-character wardrobes are turned on. With the feature off
/// there is exactly one, holding what the config used to hold at the top level, and every path
/// through the plugin reads it through the same properties it always did — see the facade on
/// <see cref="Configuration"/>. That is what keeps this from being a rewrite: one wardrobe behaves
/// precisely as no wardrobes did.
/// </para>
/// </remarks>
public class WardrobeProfile
{
    public Guid   Id   { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My Wardrobe";

    /// <summary>
    /// Characters this wardrobe is for, as <c>Name@WorldId</c>. Empty means bound to nobody.
    /// </summary>
    /// <remarks>
    /// A list rather than one entry, because the same wardrobe genuinely does serve more than one
    /// character — a pair of alts of the same race sharing a body, or a character remade on another
    /// world. Nothing is ever bound without being asked for: logging in on an unknown character
    /// offers the choice and switches nothing until it is answered.
    /// <para>
    /// Home world rather than the current one, and its row id rather than its name, so a data centre
    /// visit is still the same character and a world rename does not orphan a wardrobe.
    /// </para>
    /// </remarks>
    public List<string> Characters { get; set; } = new();

    // ── What the character owns ───────────────────────────────────────────────

    public List<WardrobeItem> Items   { get; set; } = new();
    public List<Outfit>       Outfits { get; set; } = new();

    /// <summary>Slot name (EquipSlot.ToString()) → currently worn item id.</summary>
    /// <remarks>
    /// Per wardrobe even though only one character is ever logged in, because switching wardrobes
    /// has to leave the new one's idea of what is worn behind rather than inheriting the old one's.
    /// Cleared on load either way — Glamourer state does not survive a restart.
    /// </remarks>
    public Dictionary<string, Guid> WornItems { get; set; } = new();

    /// <summary>The look this character was last seen in. See <see cref="Services.LastWornService"/>.</summary>
    public WornSnapshot? LastWorn { get; set; }

    public List<BaseCharacter> BaseCharacters       { get; set; } = new();
    public Guid?               ActiveBaseCharacterId { get; set; }

    /// <summary>
    /// Camera angles per slot. Key = EquipSlot.ToString().
    /// </summary>
    /// <remarks>
    /// Per character because framing is about the body being photographed: a set of angles built
    /// around a Lalafell puts a Viera's head out of frame, and re-aiming every slot on every
    /// character swap is exactly the work the presets exist to save.
    /// </remarks>
    public Dictionary<string, List<CameraPreset>> SlotCameraPresetLists { get; set; } = new();

    /// <summary>
    /// File this wardrobe's angles are exported to and imported from, empty for none.
    /// </summary>
    /// <remarks>
    /// Per wardrobe because the angles are. One shared path across several wardrobes meant every
    /// preset edit rewrote the file with whichever wardrobe was active, and the load at startup
    /// then pushed that back into whichever wardrobe was active then — so two characters with
    /// different angles overwrote each other's, silently, a launch at a time.
    /// <para>
    /// A new wardrobe starts with none rather than inheriting one, for exactly that reason. The
    /// angles live in the config regardless; the file is an export, a backup and a way to hand a
    /// set to somebody else.
    /// </para>
    /// </remarks>
    public string CameraPresetsPath { get; set; } = string.Empty;

    /// <summary>Where this character's pictures are written and read.</summary>
    /// <remarks>
    /// Empty falls back to the shared folder, which is what every wardrobe does until it is given
    /// one of its own — so turning the feature on does not strand anybody's existing pictures.
    /// </remarks>
    public string ImagesFolder { get; set; } = string.Empty;

    /// <summary>Variant groups the user has expanded, keyed on the original item's id.</summary>
    public HashSet<string> ExpandedVariantGroups { get; set; } = new();

    // ── Binding ───────────────────────────────────────────────────────────────

    /// <summary>The key a character is recorded under.</summary>
    public static string KeyFor(string name, uint world) => $"{name}@{world}";

    public bool IsFor(string name, uint world) => Characters.Contains(KeyFor(name, world));

    public void Bind(string name, uint world)
    {
        var key = KeyFor(name, world);
        if (!Characters.Contains(key)) Characters.Add(key);
    }

    public void Unbind(string name, uint world) => Characters.Remove(KeyFor(name, world));

    /// <summary>The character names this is bound to, for showing without the world ids.</summary>
    public IEnumerable<string> CharacterNames()
    {
        foreach (var key in Characters)
        {
            var cut = key.LastIndexOf('@');
            yield return cut > 0 ? key[..cut] : key;
        }
    }
}
