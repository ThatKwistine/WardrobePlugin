using System;

namespace WardrobePlugin.Models;

/// <summary>
/// The user's answer to "these two worn mods change the same files — which do you want on top?"
/// </summary>
/// <remarks>
/// Keyed on mod folders, which is how every other mod reference in the config survives a rename in
/// Penumbra. The pair is unordered: <see cref="Matches"/> finds it whichever mod asks.
/// </remarks>
public class ModPriorityChoice
{
    public string DirectoryA { get; set; } = string.Empty;
    public string NameA      { get; set; } = string.Empty;
    public string DirectoryB { get; set; } = string.Empty;
    public string NameB      { get; set; } = string.Empty;

    /// <summary>Folder of the mod that goes on top, or empty for "leave them as they are".</summary>
    public string WinnerDirectory { get; set; } = string.Empty;

    public bool Matches(string a, string b) =>
        (Is(DirectoryA, a) && Is(DirectoryB, b)) || (Is(DirectoryA, b) && Is(DirectoryB, a));

    private static bool Is(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
}
