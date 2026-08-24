using System;
using System.Collections.Generic;

namespace WardrobePlugin.Models;

/// <summary>
/// A wardrobe, or part of one, in the form it travels to somebody else's install.
/// </summary>
/// <remarks>
/// Deliberately not a serialised <see cref="WardrobeItem"/>. Much of an item is local-machine state
/// that is meaningless on another machine and wrong to carry across — the Penumbra collection it was
/// filed under, the Glamourer design it applies, absolute paths to pictures, whether it is a
/// favourite, when it was imported. Sending those would either fail on arrival or quietly
/// reconfigure the recipient's wardrobe around names only the sender's install has.
/// <para>
/// The second reason is stability. <see cref="WardrobeItem"/> changes whenever the plugin grows a
/// field, and it must stay free to: it is read back only by the version that wrote it. A share file
/// is read by whatever version the recipient happens to be running, which may be older or newer than
/// the sender's, so its shape is a promise and is versioned by <see cref="CurrentFormat"/> rather
/// than following the model.
/// </para>
/// <para>
/// What is emphatically not here is mod files. A share carries the <em>description</em> of an item —
/// which mod, which options, which game item — and never the mod itself. Redistributing another
/// creator's work is not ours to do, and a wardrobe of a few hundred pieces would be hundreds of
/// megabytes besides. An item whose mod the recipient does not own is shown to them as something
/// they do not have, with whatever source link its notes carry, rather than being installed for them.
/// </para>
/// </remarks>
[Serializable]
public class WardrobeShare
{
    /// <summary>The format this file was written in. Bumped only for changes older readers cannot survive.</summary>
    /// <remarks>
    /// Adding a field does not bump it: an older reader ignores what it does not know, and a newer
    /// reader treats what is missing as absent, which is the same thing it would do with a field the
    /// sender left empty. It is bumped when the meaning of something already written changes, which
    /// is the only case where reading it as-if-understood gives a wrong answer rather than a partial
    /// one.
    /// </remarks>
    public const int CurrentFormat = 1;

    /// <summary>Name of the manifest inside the archive.</summary>
    public const string ManifestName = "wardrobe-share.json";

    /// <summary>Folder inside the archive that pictures live in.</summary>
    public const string ImageFolder = "images";

    /// <summary>Extension used for share bundles, so the file dialog can filter on it.</summary>
    public const string Extension = ".wardrobe";

    public int FormatVersion { get; set; } = CurrentFormat;

    /// <summary>Plugin version that wrote the file, for making sense of a bug report about it.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>
    /// Whatever the sender chose to call themselves, shown as the heading over the browsed wardrobe.
    /// </summary>
    /// <remarks>
    /// Free text and always optional. It is a label on a file being handed to a friend, not an
    /// identity: nothing verifies it, nothing is looked up by it, and a blank one reads as
    /// "Shared wardrobe" rather than prompting for anything. A character name is the obvious thing to
    /// put here and the plugin deliberately does not fill one in — who a wardrobe is shared as is the
    /// sender's business, not something to be decided for them by reading the logged-in character.
    /// </remarks>
    public string ExportedBy { get; set; } = string.Empty;

    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>A note from the sender shown above the items, if they wrote one.</summary>
    public string Description { get; set; } = string.Empty;

    public List<SharedItem> Items { get; set; } = new();

    /// <summary>
    /// Outfits in the bundle. Every item an outfit names is in <see cref="Items"/> as well.
    /// </summary>
    /// <remarks>
    /// Guaranteed by the export rather than hoped for: ticking an outfit pulls its pieces in behind
    /// it, because an outfit is a list of references and one arriving without them is a list of
    /// nothing. The reverse does not hold — items travel perfectly well on their own.
    /// </remarks>
    public List<SharedOutfit> Outfits { get; set; } = new();
}

/// <summary>One outfit as it crosses to another install.</summary>
/// <remarks>
/// Less of an outfit survives the crossing than survives for an item, because more of an outfit is a
/// pointer at something only the sender has. What is dropped, and why:
/// <list type="bullet">
/// <item><description>
/// <b>Glamour plate id.</b> A plate outfit mirrors one of the sender's twenty in-game plates. Carried
/// across it would attach to the recipient's plate of the same number, which holds something else
/// entirely — so a plate outfit arrives as an ordinary outfit holding the pieces it had, which is
/// exactly what makes a plate worth sharing in the first place.
/// </description></item>
/// <item><description>
/// <b>Glamourer design link.</b> A design id names a design in the sender's Glamourer and nothing in
/// anybody else's. A design card arrives as an ordinary outfit holding whatever items were attached
/// to it, and loses the design's own gear and colouring — which is most of what a design card is, so
/// it will usually arrive looking thin.
/// </description></item>
/// </list>
/// <para>
/// What does survive is the part that is genuinely the look: the pieces, the dyes, the plain game
/// items filling the rest of the slots, and whether the hat and weapon show.
/// </para>
/// </remarks>
[Serializable]
public class SharedOutfit
{
    /// <inheritdoc cref="SharedItem.SourceId"/>
    public Guid SourceId { get; set; }

    public string       Name { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();

    /// <inheritdoc cref="SharedItem.ImageFile"/>
    public string?      ImageFile       { get; set; }
    public List<string> ExtraImageFiles { get; set; } = new();

    /// <summary>
    /// <see cref="SharedItem.SourceId"/>s of the wardrobe items in this outfit, in order.
    /// </summary>
    /// <remarks>
    /// Remapped to the recipient's own ids on import. A piece whose mod they do not have is dropped
    /// from the outfit rather than blocking it: an outfit is worn a piece at a time, so most of a
    /// look is still most of a look.
    /// </remarks>
    public List<Guid> ItemSourceIds { get; set; } = new();

    /// <summary>
    /// Dye channels per piece, keyed by <see cref="SharedItem.SourceId"/> as a string.
    /// </summary>
    /// <remarks>
    /// Keyed by source id rather than by the sender's local item id, which the recipient has no way
    /// to resolve. Rewritten to their own ids on import, alongside <see cref="ItemSourceIds"/>.
    /// <para>
    /// <see cref="OutfitDye"/> is reused rather than copied because it holds nothing local — two dye
    /// channels and Glamourer's own opaque rows, which are keyed by slot and so mean the same thing
    /// on any character. That makes it part of this wire format: a field added to it is a field added
    /// to what a share carries, which is not true of anything else on <see cref="Outfit"/>.
    /// </para>
    /// </remarks>
    public Dictionary<string, OutfitDye> Dyes { get; set; } = new();

    /// <summary>
    /// Plain game items filling the slots this outfit's own pieces do not, keyed by slot name.
    /// </summary>
    /// <remarks>
    /// The one part of a share that always works. A vanilla piece is a game item id and two dye
    /// channels, so it needs nothing installed and arrives exactly as it left — which is why a plate
    /// outfit, or a mostly-unmodded look, survives the crossing almost intact.
    /// <para>
    /// <see cref="VanillaPiece"/> is reused for the same reason <see cref="OutfitDye"/> is, and with
    /// the same consequence: it is part of this format.
    /// </para>
    /// </remarks>
    public Dictionary<string, VanillaPiece> VanillaItems { get; set; } = new();

    public bool? HatVisible    { get; set; }
    public bool? WeaponVisible { get; set; }

    /// <summary>
    /// What the outfit was on the sender's side, when it was not an ordinary one — for telling the
    /// recipient why a plate or a design card arrived holding less than it used to.
    /// </summary>
    /// <remarks>
    /// A note to show, never a thing to act on. Nothing keys behaviour off it: the outfit that
    /// arrives is an ordinary outfit in every respect, and this only explains its shape.
    /// </remarks>
    public SharedOutfitOrigin Origin { get; set; } = SharedOutfitOrigin.Normal;
}

/// <summary>What an outfit was before it was shared. Values are persisted, so do not renumber.</summary>
public enum SharedOutfitOrigin
{
    Normal       = 0,
    GlamourPlate = 1,
    DesignCard   = 2,
}

/// <summary>One item as it crosses to another install.</summary>
[Serializable]
public class SharedItem
{
    /// <summary>
    /// The item's id on the sender's machine, kept so links between items in one bundle can be
    /// rebuilt and so re-importing the same bundle can recognise what it already brought in.
    /// </summary>
    /// <remarks>
    /// Never reused as the imported item's own id. Two people who both import a bundle and then share
    /// their wardrobes onward would otherwise circulate items that collide with each other, and an id
    /// is what everything local — worn slots, links, variants — keys on. The imported item gets a
    /// fresh id and remembers this one in <see cref="WardrobeItem.SharedFromId"/>.
    /// </remarks>
    public Guid SourceId { get; set; }

    public string    Name  { get; set; } = string.Empty;
    public EquipSlot Slot  { get; set; } = EquipSlot.Unknown;
    public string?   Notes { get; set; }

    public List<string> Tags { get; set; } = new();

    /// <summary>The mods this item needs, in terms both installs can agree on. See <see cref="SharedMod"/>.</summary>
    public List<SharedMod> Mods { get; set; } = new();

    /// <summary>Game item to equip, which needs nothing installed to be correct.</summary>
    public ulong?  GlamourerItemId   { get; set; }
    public string? GlamourerItemName { get; set; }
    public ushort? ModelSetId        { get; set; }

    public Dictionary<string, ushort>       HairIdByRace       { get; set; } = new();
    public Dictionary<string, List<ushort>> CustomizeIdsByRace { get; set; } = new();

    public string? Replaces    { get; set; }
    public string? Layer       { get; set; }
    public bool?   ForceRedraw { get; set; }

    /// <summary>
    /// File name of the cover picture inside the bundle's images folder, or null if the item was
    /// shared without one.
    /// </summary>
    /// <remarks>
    /// A name within the archive and never a path from the sender's disk, which would not exist on
    /// the recipient's and would say rather more about the sender's machine than they meant to share.
    /// </remarks>
    public string?      ImageFile       { get; set; }
    public List<string> ExtraImageFiles { get; set; } = new();

    /// <summary>
    /// <see cref="SourceId"/>s of items shared alongside this one that are worn with it.
    /// </summary>
    /// <remarks>
    /// Remapped to the recipient's own ids on import, and only for items that actually came in the
    /// same bundle: a link to something left out of the export has nothing to point at, and is
    /// dropped rather than imported as an id that resolves to nothing.
    /// </remarks>
    public List<Guid> LinkedSourceIds { get; set; } = new();
}

/// <summary>A mod requirement, described so the recipient's install can be asked whether it has it.</summary>
/// <remarks>
/// <see cref="ModDirectory"/> is the match key. It is Penumbra's own folder name for the mod, and two
/// people who installed the same release of the same mod have the same one, which makes it the only
/// field here that reliably means the same thing on both machines. <see cref="ModName"/> is the
/// fallback for a folder somebody has renamed, and is what a recipient who does not have the mod is
/// shown, since a folder name is not what anybody calls it.
/// <para>
/// No collection. Collection names are the sender's own filing — "Default", "Glam Testing", a
/// character's name — and mean nothing on another install; a share carrying them would try to enable
/// mods in collections named after a stranger's characters, or fail because no such collection
/// exists. Which collection to use is chosen on the recipient's side at the moment of wearing.
/// </para>
/// </remarks>
[Serializable]
public class SharedMod
{
    public string Label        { get; set; } = "Main Mod";
    public string ModDirectory { get; set; } = string.Empty;
    public string ModName      { get; set; } = string.Empty;

    public Dictionary<string, string>                   Options      { get; set; } = new();
    public Dictionary<string, List<string>>             MultiOptions { get; set; } = new();
    public Dictionary<string, Dictionary<string, bool>> OptionStates { get; set; } = new();
}
