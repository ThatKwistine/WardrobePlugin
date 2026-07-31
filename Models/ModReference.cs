using System;
using System.Collections.Generic;

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
    public Dictionary<string, List<string>> MultiOptions { get; set; } = new();
}
