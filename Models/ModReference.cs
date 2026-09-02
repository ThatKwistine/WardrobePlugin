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
    };
}
