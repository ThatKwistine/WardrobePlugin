using System;
using System.Collections.Generic;

namespace WardrobePlugin.Services;

/// <summary>
/// Compares names the way a person reads them, so a run of digits sorts by its value.
/// </summary>
/// <remarks>
/// Ordinary string comparison puts <c>Glamour Plate 2</c> after <c>Glamour Plate 19</c> and
/// <c>Glamour Plate 20</c> before <c>Glamour Plate 3</c>, because it compares <c>2</c> against
/// <c>1</c> one character at a time and stops there. Nothing about a wardrobe wants that: plates are
/// numbered 1 to 20, screenshots come off the game as <c>ffxiv_001</c> upwards, and half the mods
/// anybody installs have a version or a variant number in the name.
/// <para>
/// Not offered as a setting. The two orderings are not a matter of taste with something to be said
/// for each — one of them is how counting works and the other is an artefact of comparing text, so
/// there is nothing here for anyone to prefer.
/// </para>
/// <para>
/// <b>A strict total order, and case-insensitive exactly where <see cref="StringComparer.OrdinalIgnoreCase"/>
/// is.</b> That matters because this is also the comparer behind sorted dictionaries keyed on tag
/// paths: two names compare equal only when they differ by nothing but case, so <c>Plate 01</c> and
/// <c>Plate 1</c> — equal in value, different as text — stay two distinct keys rather than
/// collapsing into one and losing a tag.
/// </para>
/// </remarks>
public sealed class NaturalOrderComparer : IComparer<string?>
{
    /// <summary>The shared instance. It holds no state, so one serves everything.</summary>
    public static readonly NaturalOrderComparer Instance = new();

    private NaturalOrderComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;

        while (i < x.Length && j < y.Length)
        {
            var a = x[i];
            var b = y[j];

            if (char.IsAsciiDigit(a) && char.IsAsciiDigit(b))
            {
                var compared = CompareNumbers(x, ref i, y, ref j);
                if (compared != 0) return compared;
                continue;
            }

            // Invariant upper-casing, which is the mapping OrdinalIgnoreCase uses, so this agrees
            // with it about which names are the same name
            var ua = char.ToUpperInvariant(a);
            var ub = char.ToUpperInvariant(b);

            if (ua != ub) return ua < ub ? -1 : 1;

            i++;
            j++;
        }

        // One ran out: the shorter is the prefix, and a prefix comes first
        if (i < x.Length) return 1;
        if (j < y.Length) return -1;

        // Everything read the same, so the two differ by nothing but leading zeros or case. Falling
        // back to OrdinalIgnoreCase over the whole string settles both correctly and is what keeps
        // this agreeing with it about equality: "Plate 01" and "Plate 1" separate on the '0', while
        // "Shoes/Boots" and "shoes/boots" stay the one name they are. Comparing ordinally here
        // instead would make case alone a difference, and a sorted dictionary keyed on tag paths
        // would then hold one tag twice.
        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    /// <summary>
    /// Compares the runs of digits starting at each cursor, leaving both past the run.
    /// </summary>
    /// <remarks>
    /// Compared by length once leading zeros are gone, then digit by digit, rather than by parsing.
    /// A name is free to contain forty digits, and <see cref="long"/> would overflow on it — badly,
    /// because the failure would be a wrong order rather than an error anybody notices.
    /// </remarks>
    private static int CompareNumbers(string x, ref int i, string y, ref int j)
    {
        while (i < x.Length && x[i] == '0') i++;
        while (j < y.Length && y[j] == '0') j++;

        var startX = i;
        var startY = j;

        while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
        while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

        var lengthX = i - startX;
        var lengthY = j - startY;

        // More digits means a larger number, once the leading zeros are out of the way
        if (lengthX != lengthY) return lengthX < lengthY ? -1 : 1;

        for (var k = 0; k < lengthX; k++)
        {
            var a = x[startX + k];
            var b = y[startY + k];
            if (a != b) return a < b ? -1 : 1;
        }

        return 0;
    }
}

/// <summary>Sorting helpers that read better at the call site than the comparer does.</summary>
public static class NaturalOrder
{
    /// <summary>The comparer, for the many places that want to pass one.</summary>
    /// <remarks>
    /// Typed on <c>string?</c> because some keys genuinely are nullable — <see cref="System.IO.Path.GetFileName(string)"/>
    /// is one, and the image browser sorts on it. <see cref="IComparer{T}"/> is contravariant, so
    /// this still serves every caller sorting plain non-null names.
    /// </remarks>
    public static IComparer<string?> Comparer => NaturalOrderComparer.Instance;

    /// <summary>Compares two names in reading order.</summary>
    public static int Compare(string? a, string? b) => NaturalOrderComparer.Instance.Compare(a, b);
}
