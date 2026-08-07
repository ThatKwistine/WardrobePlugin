using System;
using System.Collections.Generic;
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
    /// Builds the tree from tags on items plus tags made ahead of use.
    /// </summary>
    public static TagNode Build(Configuration config)
    {
        var root = new TagNode();

        foreach (var item in config.WardrobeItems)
        foreach (var tag in item.Tags)
            AddPath(root, tag, inUse: true);

        // After the items, so a pre-made tag that has since been used is already marked in use and
        // is not dimmed back down again
        foreach (var tag in config.DefinedTags)
            AddPath(root, tag, inUse: false);

        return root;
    }

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
