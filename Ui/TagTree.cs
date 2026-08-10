using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace WardrobePlugin.Ui;

/// <summary>One segment of a tag path — "Boots" within "Shoes/Boots/Ankle Boots".</summary>
public sealed class TagNode
{
    public string Segment  = string.Empty;
    public string FullPath = string.Empty;

    /// <summary>
    /// Some item carries this tag or one below it. False on a branch that exists only because a tag
    /// was pre-made, which filters to nothing and is drawn dimmed to say so.
    /// </summary>
    public bool InUse;

    public SortedDictionary<string, TagNode> Children = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Tags arranged as a tree, shared by everywhere that shows them.
/// </summary>
/// <remarks>
/// There are three places tags are chosen — the filter panel, the bulk actions panel and the import
/// panel — and they disagreed about what a tag list looks like when each grew its own. Building and
/// picking live here so they cannot drift apart again; the filter panel keeps its own draw because
/// clicking there toggles a filter and right-clicking deletes, which is most of what it does.
/// </remarks>
public static class TagTree
{
    /// <summary>
    /// Tag path segment reserved for styles — the mood or theme of a piece rather than what it is.
    /// </summary>
    /// <remarks>
    /// Styles are ordinary tags under this root, so everything tags already do — pre-making them,
    /// bulk applying, search, backup — works on them without a second system to keep in step. What
    /// makes them styles is only where they are shown: their own row under the filters rather than
    /// somewhere in the tag tree, which is the whole of what was asked for.
    /// </remarks>
    public const string StyleRoot = "Style";

    /// <summary>Whether a tag is a style. A bare "Style" with nothing under it is an ordinary tag.</summary>
    public static bool IsStyle(string tag) =>
        tag.StartsWith(StyleRoot + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tag a style name is stored as.</summary>
    public static string StylePath(string name) => $"{StyleRoot}/{name}";

    /// <summary>
    /// Builds the tree from tags on items plus tags made ahead of use.
    /// </summary>
    /// <param name="includeStyles">
    /// Whether the reserved <see cref="StyleRoot"/> branch is part of the tree. False wherever
    /// styles have a control of their own and would otherwise be offered twice; true where the tree
    /// is the only way to reach a tag, so that bulk-applying a style to a selection stays possible.
    /// </param>
    public static TagNode Build(Configuration config, bool includeStyles)
    {
        var root = new TagNode();

        foreach (var item in config.WardrobeItems)
        foreach (var tag in item.Tags)
        {
            if (!includeStyles && IsStyle(tag)) continue;
            AddPath(root, tag, inUse: true);
        }

        // After the items, so a pre-made tag that has since been used is already marked in use and
        // is not dimmed back down again
        foreach (var tag in config.DefinedTags)
        {
            if (!includeStyles && IsStyle(tag)) continue;
            AddPath(root, tag, inUse: false);
        }

        return root;
    }

    /// <summary>
    /// The styles: the direct children of the reserved root, in name order, each carrying whether
    /// any item has it.
    /// </summary>
    /// <remarks>
    /// Only one level deep. A style nested further — <c>Style/Beach/Tropical</c> — is reported as
    /// its top style and still matched by it, since filtering on a tag has always included the tags
    /// below it. That keeps the row a row rather than a second tree, which is the point of it.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// Scanned straight out of the tags rather than read off <see cref="Build"/>, because the row
    /// this feeds is drawn every frame of the main window and building the whole tree to look at one
    /// branch of it would allocate the other branches for nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TagNode> Styles(Configuration config)
    {
        var styles = new SortedDictionary<string, TagNode>(StringComparer.OrdinalIgnoreCase);

        void Add(string tag, bool inUse)
        {
            if (!IsStyle(tag)) return;

            // Only the segment below the root: a style filed deeper is reported as the style it is
            // under, which prefix matching then filters correctly anyway
            var rest = tag[(StyleRoot.Length + 1)..];
            var cut  = rest.IndexOf('/');
            var name = (cut < 0 ? rest : rest[..cut]).Trim();
            if (name.Length == 0) return;

            if (!styles.TryGetValue(name, out var node))
                styles[name] = node = new TagNode { Segment = name, FullPath = StylePath(name) };

            if (inUse) node.InUse = true;
        }

        foreach (var item in config.WardrobeItems)
        foreach (var tag in item.Tags)
            Add(tag, inUse: true);

        // After the items, so a style made ahead of use that something has since taken is already
        // marked in use and is not dimmed back down again
        foreach (var tag in config.DefinedTags)
            Add(tag, inUse: false);

        return styles.Values.ToList();
    }

    // ── Colours ───────────────────────────────────────────────────────────────

    /// <summary>The colour chosen for a tag, or null when it has none and the defaults apply.</summary>
    public static Vector4? Colour(Configuration config, string path) =>
        config.TagColours.TryGetValue(path, out var packed) ? ToVector(packed) : null;

    /// <summary>Stores a tag's colour, dropping it if it matches nothing meaningful.</summary>
    public static void SetColour(Configuration config, string path, Vector3 rgb)
    {
        config.TagColours[path] = FromVector(rgb);
        config.Save();
    }

    /// <summary>Returns a tag to the default colouring.</summary>
    public static void ClearColour(Configuration config, string path)
    {
        if (config.TagColours.Remove(path)) config.Save();
    }

    /// <summary>Drops the colours of a tag and everything nested under it.</summary>
    /// <remarks>Called when a tag is deleted, so a path reused later does not inherit a colour
    /// nobody chose for it.</remarks>
    public static void ClearColours(Configuration config, string path)
    {
        var stale = config.TagColours.Keys
            .Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith($"{path}/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in stale)
            config.TagColours.Remove(key);
    }

    public static Vector4 ToVector(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8)  & 0xFF) / 255f,
        (rgb         & 0xFF) / 255f,
        1f);

    public static uint FromVector(Vector3 rgb) =>
        ((uint)(Math.Clamp(rgb.X, 0f, 1f) * 255f + 0.5f) << 16) |
        ((uint)(Math.Clamp(rgb.Y, 0f, 1f) * 255f + 0.5f) << 8)  |
        (uint)(Math.Clamp(rgb.Z, 0f, 1f) * 255f + 0.5f);

    /// <summary>
    /// A tag's colour adjusted for its state, so colour and state can both be read at once.
    /// </summary>
    /// <remarks>
    /// The two would otherwise fight: filtering is shown by colouring a tag, and a tag with a colour
    /// of its own has nothing left to say it with. Keeping the hue and moving only the brightness
    /// means a green tag stays green whether it is filtering, idle or unused — which is the point of
    /// having chosen green — while still looking different in each.
    /// </remarks>
    public static Vector4 Shade(Vector4 colour, bool active, bool inUse)
    {
        if (active)
            return new Vector4(
                colour.X + (1f - colour.X) * 0.35f,
                colour.Y + (1f - colour.Y) * 0.35f,
                colour.Z + (1f - colour.Z) * 0.35f,
                1f);

        return inUse ? colour : new Vector4(colour.X * 0.5f, colour.Y * 0.5f, colour.Z * 0.5f, 1f);
    }

    /// <summary>Mixes <paramref name="amount"/> of <paramref name="tint"/> into a base colour.</summary>
    /// <remarks>
    /// Used to tint a surface without repainting it. An item card carrying a style keeps the card
    /// colour it always had with the style's hue mixed in, so the grid still reads as a grid of
    /// cards rather than a row of coloured blocks.
    /// </remarks>
    public static Vector4 Blend(Vector4 baseColour, Vector4 tint, float amount) => new(
        baseColour.X + (tint.X - baseColour.X) * amount,
        baseColour.Y + (tint.Y - baseColour.Y) * amount,
        baseColour.Z + (tint.Z - baseColour.Z) * amount,
        baseColour.W);

    /// <summary>Black or white, whichever can be read on top of the given colour.</summary>
    public static Vector4 ReadableOn(Vector4 background) =>
        background.X * 0.299f + background.Y * 0.587f + background.Z * 0.114f > 0.55f
            ? new Vector4(0.05f, 0.05f, 0.06f, 1f)
            : new Vector4(1f, 1f, 1f, 1f);

    /// <summary>
    /// Walks a <c>Parent/Child</c> tag into the tree, creating what is missing.
    /// </summary>
    /// <remarks>
    /// Every segment along the path is marked in use, not just the last: a parent of a tag some item
    /// carries is a real container even when nothing is tagged with the parent alone.
    /// </remarks>
    private static void AddPath(TagNode root, string tag, bool inUse)
    {
        var parts = tag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var node  = root;
        var path  = string.Empty;

        foreach (var part in parts)
        {
            path = path.Length == 0 ? part : $"{path}/{part}";
            if (!node.Children.TryGetValue(part, out var child))
                node.Children[part] = child = new TagNode { Segment = part, FullPath = path };
            node = child;
            if (inUse) node.InUse = true;
        }
    }

    /// <summary>
    /// Draws the tree as a picker: clicking a tag chooses it.
    /// </summary>
    /// <param name="id">Distinguishes two pickers drawn in the same window.</param>
    /// <param name="isChosen">Whether a tag should be drawn as the current choice.</param>
    /// <param name="onPick">Called with the full path of a clicked tag.</param>
    public static void DrawPicker(TagNode node, string id,
        Func<string, bool> isChosen, Action<string> onPick)
    {
        foreach (var (_, child) in node.Children)
        {
            var chosen = isChosen(child.FullPath);

            // Explicit Vector4 — the overload taking a packed uint makes a target-typed `new` ambiguous
            if (chosen)            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.78f, 0.58f, 1f, 1f));
            else if (!child.InUse) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.45f, 0.52f, 1f));

            var isLeaf = child.Children.Count == 0;
            var flags  = ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isLeaf)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            else
                flags |= ImGuiTreeNodeFlags.OpenOnArrow;

            var open = ImGui.TreeNodeEx($"##{id}_{child.FullPath}", flags, child.Segment);
            if (chosen || !child.InUse) ImGui.PopStyleColor();

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                // On a branch, only the label picks — the arrow is for expanding
                var clickX = ImGui.GetMousePos().X - ImGui.GetItemRectMin().X;
                if (isLeaf || clickX >= ImGui.GetTreeNodeToLabelSpacing())
                    onPick(child.FullPath);
            }

            // A branch shows only its own segment, so the full path is worth having on hover
            if (!isLeaf && ImGui.IsItemHovered())
                ImGui.SetTooltip(child.FullPath);

            if (open && !isLeaf)
            {
                DrawPicker(child, id, isChosen, onPick);
                ImGui.TreePop();
            }
        }
    }
}
