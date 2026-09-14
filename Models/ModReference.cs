using System;
using System.Collections.Generic;
using System.Linq;

namespace WardrobePlugin.Models;

[Serializable]
public class ModReference
{
    /// <summary>Human-readable label shown in the UI, e.g. "Main Mod" or "Body Upscale".</summary>
    public string Label        { get; set; } = "Main Mod";
    public string Collection   { get; set; } = string.Empty;
    public string ModDirectory { get; set; } = string.Empty;
    public string ModName      { get; set; } = string.Empty;

    /// <summary>Single-select group name → chosen option name; empty dict = use mod defaults.</summary>
    public Dictionary<string, string> Options { get; set; } = new();

    /// <summary>Multi-select (checkbox) group name → list of enabled option names.</summary>
    /// <remarks>
    /// The exact selection: anything not listed is unticked when the item is worn. Superseded by
    /// <see cref="OptionStates"/>, and read only while that is empty, so an item saved before
    /// tri-states existed keeps behaving exactly as it did until it is re-detected or edited.
    /// </remarks>
    public Dictionary<string, List<string>> MultiOptions { get; set; } = new();

    /// <summary>
    /// Multi-select group name → option name → on (true) or off (false). An option absent from the
    /// inner dictionary is left however it is found.
    /// </summary>
    /// <remarks>
    /// The point of the third state. Two items from one mod, worn together, used to fight over the
    /// whole option set — whichever was applied last won, so a variant's choices were undone by a
    /// sibling in another slot that had no stake in them (issue #12). An item that leaves those
    /// options alone contributes only what it cares about, and the two compound instead of
    /// replacing each other.
    /// <para>
    /// Empty means this item has no tri-states yet and <see cref="MultiOptions"/> is used instead.
    /// A group present here overrides that group in the legacy field; a group missing from both is
    /// untouched either way.
    /// </para>
    /// </remarks>
    public Dictionary<string, Dictionary<string, bool>> OptionStates { get; set; } = new();

    /// <summary>
    /// Groups of this mod that are a body size, shown on the item's card as a quick pick of their
    /// own options.
    /// </summary>
    /// <remarks>
    /// Issue #28. A top comes in the sizes its mod ships — "YAB+ S", "YAB+ M", "Bibo+" — and the
    /// mod's own names are the whole vocabulary: nothing is translated into a size name, since
    /// mods do not agree on any and the person choosing knows what "YAB+ M" is. Marking a group
    /// puts its options on the card, two clicks nearer than the edit panel; the item's stored
    /// options are still what is worn, and the pick writes to them.
    /// <para>
    /// A list rather than one name because a body mod can have a bust group and a hips group
    /// both about the body slot. Usually found on import — see <see cref="Services.SizeGuess"/>.
    /// </para>
    /// </remarks>
    public List<string> SizeGroups { get; set; } = new();

    /// <summary>
    /// Group name → option name → what the card calls the option, for a size option whose own
    /// name says nothing — a refit shipped as a mod of its own with a single toggle called "Top"
    /// is better shown as "Muse".
    /// </summary>
    /// <remarks>
    /// Only what differs: an option absent here is shown under the mod's own name. The mod's
    /// options are still applied by their real names, and Penumbra is untouched — renaming there
    /// is the mod author's business.
    /// </remarks>
    public Dictionary<string, Dictionary<string, string>> SizeOptionLabels { get; set; } = new();

    /// <summary>
    /// Group name → options left off the card's pick. A body mod can ship sixty sizes and one
    /// character wears four of them; the rest are noise in a popup taller than the screen.
    /// </summary>
    /// <remarks>
    /// The hidden ones rather than the shown, so an option a mod update adds turns up on the card
    /// until somebody hides it, and an item that never hid anything shows the lot. The option the
    /// item is at is always shown whether or not it is hidden, since the pick has to say where it
    /// stands.
    /// </remarks>
    public Dictionary<string, List<string>> SizeHiddenOptions { get; set; } = new();

    /// <summary>
    /// Size groups of this mod that are one set with the item's other set groups: a pick in any of
    /// them switches the toggles in the others off.
    /// </summary>
    /// <remarks>
    /// Per group, because it is a fact about the item — a body size and a refit's toggle are
    /// alternatives, a print beside them is not — and no setting can know which is which. A group
    /// not in the set is its own pick and is never touched by another. A set of one is nothing.
    /// </remarks>
    public List<string> SizeSetGroups { get; set; } = new();

    public bool InSizeSet(string group) => SizeSetGroups.Contains(group);

    public bool IsSizeOptionHidden(string group, string option) =>
        SizeHiddenOptions.TryGetValue(group, out var hidden) && hidden.Contains(option);

    /// <summary>What the card calls this option: its label here, else its own name.</summary>
    public string SizeOptionLabel(string group, string option) =>
        SizeOptionLabels.TryGetValue(group, out var labels) &&
        labels.TryGetValue(option, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label
            : option;

    /// <summary>A copy sharing nothing with this one, for putting an item in another wardrobe.</summary>
    /// <remarks>
    /// The dictionaries are rebuilt rather than handed over. The whole point of copying an item
    /// between wardrobes is to change its options for the other character, and two items sharing
    /// one options dictionary would have every such edit land on both.
    /// </remarks>
    public ModReference Copy() => new()
    {
        Label        = Label,
        Collection   = Collection,
        ModDirectory = ModDirectory,
        ModName      = ModName,
        Options      = new Dictionary<string, string>(Options),
        MultiOptions = MultiOptions.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
        OptionStates = OptionStates.ToDictionary(kv => kv.Key,
                                                 kv => new Dictionary<string, bool>(kv.Value)),
        SizeGroups   = new List<string>(SizeGroups),
        SizeOptionLabels = SizeOptionLabels.ToDictionary(kv => kv.Key,
                                                         kv => new Dictionary<string, string>(kv.Value)),
        SizeHiddenOptions = SizeHiddenOptions.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
        SizeSetGroups = new List<string>(SizeSetGroups),
    };
}
