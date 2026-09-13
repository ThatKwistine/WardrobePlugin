using System;
using System.Collections.Generic;
using System.Linq;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// A first guess at which of a mod's option groups is the body size for a slot, made on import so
/// most items get the pick on their card without anyone opening Edit.
/// </summary>
/// <remarks>
/// Only ever a pre-fill, and only ever a tick: it marks a group, it never chooses an option. A
/// group counts when the slot has a say in it and at least two of its options read as different
/// sizes — small, medium or large by any of the usual words, "S / M / L" included — so a colour
/// group with a "Large logo" in it does not count, and neither does a group with one size word.
/// Body and legs only, which is where the issue said sizes live; anything else is a tick in Edit.
/// <para>
/// Dropdowns and checkbox groups alike: a mod that ships its sizes as three checkboxes in one group
/// reads the same as one that ships them as a dropdown, since it is the option names being read.
/// </para>
/// </remarks>
public static class SizeGuess
{
    private static readonly string[] ChestWords = { "chest", "bust", "breast", "breasts", "boob", "boobs" };
    private static readonly string[] LegWords   = { "leg", "legs", "thigh", "thighs", "hip", "hips", "butt" };

    private static readonly string[][] Families =
    {
        new[] { "small", "s", "xs", "xxs", "sm", "flat", "petite", "tiny", "slim", "thin", "lean", "smaller" },
        new[] { "medium", "m", "med", "mid", "normal", "regular", "standard", "average", "natural" },
        new[] { "large", "l", "xl", "xxl", "lg", "big", "huge", "thick", "curvy", "plus", "wide", "larger", "bigger" },
    };

    /// <summary>The group this mod's groups suggest for an item in this slot, or null when none reads as sizes.</summary>
    public static string? Guess(IReadOnlyList<ModOptionGroup>? groups, EquipSlot slot)
    {
        if (groups == null) return null;
        var axisWords = slot switch
        {
            EquipSlot.Body => ChestWords,
            EquipSlot.Legs => LegWords,
            _              => null,
        };
        if (axisWords == null) return null;

        // Every group the slot has a say in with two or more options reading as different sizes.
        // A group named for the body part wins; then the one naming the most sizes
        return groups
            .Where(g => g.AffectsSlot(slot) && g.OptionNames.Count >= 2)
            .Select(g => (g.GroupName, Sizes: SizesNamed(g), Named: Tokens(g.GroupName).Any(axisWords.Contains)))
            .Where(c => c.Sizes >= 2)
            .OrderByDescending(c => c.Named)
            .ThenByDescending(c => c.Sizes)
            .Select(c => c.GroupName)
            .FirstOrDefault();
    }

    /// <summary>Marks the guessed group on the item's primary mod, where nothing is marked yet.</summary>
    public static void Apply(WardrobeItem item, IReadOnlyList<ModOptionGroup>? groups, Configuration config)
    {
        if (!config.SizeOptionsEnabled || !config.GuessSizeGroups || item.Mods.Count == 0) return;
        var mod = item.Mods[0];
        if (mod.SizeGroups.Count > 0) return;
        if (Guess(groups, item.Slot) is { } group) mod.SizeGroups.Add(group);
    }

    /// <summary>Whether a name reads as a size — one of small, medium or large by any of the usual words.</summary>
    /// <remarks>
    /// What the card's pick uses to know which options of a checkbox group are alternatives to each
    /// other: picking "Large" turns "Small" off, and leaves a "Nipple fix" beside them alone.
    /// </remarks>
    public static bool ReadsAsSize(string name) => FamilyOf(name) >= 0;

    /// <summary>How many different sizes the group's options read as.</summary>
    private static int SizesNamed(ModOptionGroup group) =>
        group.OptionNames.Select(FamilyOf).Where(f => f >= 0).Distinct().Count();

    /// <summary>The one family a name reads as, or -1 when it reads as none or as more than one.</summary>
    private static int FamilyOf(string name)
    {
        var tokens = Tokens(name).ToList();
        var hits = Enumerable.Range(0, Families.Length).Where(i => tokens.Any(Families[i].Contains)).ToList();
        return hits.Count == 1 ? hits[0] : -1;
    }

    private static IEnumerable<string> Tokens(string name) =>
        name.ToLowerInvariant()
            .Split(new[] { ' ', '-', '_', '/', '(', ')', '[', ']', ',', '.', ':', '+' },
                   StringSplitOptions.RemoveEmptyEntries);
}
