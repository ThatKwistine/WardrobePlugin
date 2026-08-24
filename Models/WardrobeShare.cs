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
