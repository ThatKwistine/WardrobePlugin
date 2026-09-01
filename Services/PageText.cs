using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// Describing wardrobe things as lines of text: tags, mods, dyes, outfit pieces and links.
/// </summary>
/// <remarks>
/// Extracted so that every builder of a <see cref="PageModel"/> spells these the same way. A local
/// wardrobe and a bundle somebody sent hold a mod in two different classes carrying the same four
/// things, and an outfit's contents the same way — two spellings of "Body — Silk Blouse (Snow White)"
/// is precisely the drift the shared page exists to avoid.
/// <para>
/// Deliberately free of Dalamud, ImGui and the filesystem, as <see cref="WardrobePage"/> and
/// <see cref="WardrobePageWriter"/> are. The whole path from a description of a wardrobe to a page of
/// it is plain .NET, which is what lets it be tested without a game running and what will let
/// something other than this plugin's UI drive it.
/// </para>
/// </remarks>
public static class PageText
{
    /// <summary>
    /// Tag path segment reserved for styles — the mood or theme of a piece rather than what it is.
    /// </summary>
    /// <remarks>
    /// The single definition; <see cref="Ui.TagTree.StyleRoot"/> is this. It lives here rather than
    /// there because what a tag path means is a fact about the data, not about the tree widget that
    /// happened to need it first.
    /// </remarks>
    public const string StyleRoot = "Style";

    /// <summary>Whether a tag is a style. A bare "Style" with nothing under it is an ordinary tag.</summary>
    public static bool IsStyle(string tag) =>
        tag.StartsWith(StyleRoot + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tag a style name is stored as.</summary>
    public static string StylePath(string name) => $"{StyleRoot}/{name}";

    /// <summary>Splits a tag list onto a card as styles and ordinary tags, dropping the reserved prefix.</summary>
    public static void SplitTags(IEnumerable<string> tags, PageCard card)
    {
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;

            if (IsStyle(tag))
                card.Styles.Add(tag[(StyleRoot.Length + 1)..]);
            else
                card.Tags.Add(tag);
        }

        card.Styles.Sort(StringComparer.OrdinalIgnoreCase);
        card.Tags.Sort(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One line of an outfit's contents: where it goes, what it is, how it is dyed.</summary>
    public static string PieceLine(string slot, string name, string? dye) =>
        dye is null ? $"{slot} — {name}" : $"{slot} — {name} ({dye})";

    /// <summary>
    /// One mod and the options chosen in it, as a single line.
    /// </summary>
    /// <remarks>
    /// Takes the fields rather than a <see cref="ModReference"/> so a shared item's <c>SharedMod</c> —
    /// the same four things under a different type, because a share format must not follow a model
    /// that is free to change — describes itself identically.
    /// </remarks>
    public static string DescribeMod(string label, string modName, string modDirectory,
                                     IReadOnlyDictionary<string, string> single,
                                     IReadOnlyDictionary<string, List<string>> multi,
                                     IReadOnlyDictionary<string, Dictionary<string, bool>> states)
    {
        var name = string.IsNullOrWhiteSpace(modName) ? modDirectory : modName;
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var options = new List<string>();

        foreach (var one in single)
            options.Add($"{one.Key}: {one.Value}");

        foreach (var group in states)
        {
            var on = group.Value.Where(s => s.Value).Select(s => s.Key).ToList();
            if (on.Count > 0) options.Add($"{group.Key}: {string.Join(", ", on)}");
        }

        // Read only where tri-states have nothing to say for that group, matching how they are applied
        foreach (var group in multi)
        {
            if (states.ContainsKey(group.Key) || group.Value.Count == 0) continue;
            options.Add($"{group.Key}: {string.Join(", ", group.Value)}");
        }

        options.Sort(StringComparer.OrdinalIgnoreCase);

        var line = !string.IsNullOrWhiteSpace(label) && label != "Main Mod"
            ? $"{name} — {label}"
            : name;

        return options.Count == 0 ? line : $"{line} ({string.Join("; ", options)})";
    }

    /// <inheritdoc cref="DescribeMod(string,string,string,IReadOnlyDictionary{string,string},IReadOnlyDictionary{string,List{string}},IReadOnlyDictionary{string,Dictionary{string,bool}})"/>
    public static string DescribeMod(ModReference mod) =>
        DescribeMod(mod.Label, mod.ModName, mod.ModDirectory, mod.Options, mod.MultiOptions, mod.OptionStates);

    /// <summary>The dye channels as names, or null when the piece carries none.</summary>
    public static string? DescribeDye(OutfitDye dye, IReadOnlyDictionary<byte, string> stains)
    {
        var names = new List<string>();
        if (dye.Stain1 != 0) names.Add(stains.TryGetValue(dye.Stain1, out var a) ? a : $"Dye {dye.Stain1}");
        if (dye.Stain2 != 0) names.Add(stains.TryGetValue(dye.Stain2, out var b) ? b : $"Dye {dye.Stain2}");
        if (dye.Advanced.Count > 0) names.Add("advanced dyes");

        return names.Count == 0 ? null : string.Join(" + ", names);
    }

    // ── Links in free text ────────────────────────────────────────────────────

    private static readonly Regex UrlPattern =
        new(@"^https?://[^\s]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Punctuation that reads as part of the sentence rather than the address.</summary>
    private static readonly char[] TrailingPunctuation =
        { '.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'' };

    private static readonly char[] Separators = { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// Web addresses in a piece of free text, in the order written, without repeats.
    /// </summary>
    /// <remarks>
    /// Only http and https are ever returned. Notes are free text and a link in them is not
    /// necessarily one the reader wrote — a wardrobe entry can be pasted in from anywhere, and a
    /// shared one was written by somebody else entirely — so a <c>file://</c> or a custom scheme,
    /// which can start a program, never gets this far. Wherever these are shown the text of the link
    /// is the address itself: a label over a different destination is exactly how a hostile link
    /// hides.
    /// <para>
    /// The single definition, delegated to by <see cref="Ui.NoteText"/>, so what the wardrobe makes
    /// clickable in game and what an exported page makes clickable cannot come apart.
    /// </para>
    /// </remarks>
    public static List<string> Links(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        return text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimEnd(TrailingPunctuation))
            .Where(w => UrlPattern.IsMatch(w) && IsSafeLink(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Second gate on an address, in case the pattern was talked into matching something it should
    /// not have. Only absolute http and https ever pass.
    /// </summary>
    public static bool IsSafeLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
