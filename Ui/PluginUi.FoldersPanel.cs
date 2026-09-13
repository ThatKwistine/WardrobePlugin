using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// The Folders panel under View, and the "add to this folder" picker on an open folder.
/// </summary>
/// <remarks>
/// The panel is where folders are made ahead of use — the Tags panel does the same for tags — and
/// where every folder can be seen at once, renamed, coloured or deleted without hunting for its
/// card. Clicking a folder there opens it on whichever grid is in front.
/// <para>
/// The picker is the other half of filing: the card menu and the selection bar file cards <i>from
/// the grid</i>, and this files them <i>from inside the folder</i>, which is the natural place to
/// stand when the question is "what else belongs in here".
/// </para>
/// </remarks>
public partial class PluginUi
{
    // ── The panel ─────────────────────────────────────────────────────────────

    /// <summary>Whether the Folders panel is showing in the right-hand column.</summary>
    private bool _showFolders;

    private string _panelNewFolder   = string.Empty;
    private string _panelFolderStatus = string.Empty;

    private void DrawFoldersPanel()
    {
        if (DrawPanelHeader("Folders"))
        {
            _showFolders = false;
            return;
        }

        ImGui.Spacing();
        Hint("Folders file cards away, on both grids.",
             "The Folders button on the filter row shows the grid by folder. Make\n" +
             "them here ahead of filing anything, or file cards from Select\n" +
             "or a card's right-click menu and the folder is made as you go.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-UiLayout.ButtonWidth(" Make ") - ImGui.GetStyle().ItemSpacing.X);
        var entered = ImGui.InputTextWithHint("##panelnewfolder", "new folder, or Parent/Child",
            ref _panelNewFolder, 64, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var shown   = NormaliseTag(_panelNewFolder);
        var canMake = shown.Length > 0;
        if (!canMake) ImGui.BeginDisabled();
        if (ImGui.Button(" Make ") || (entered && canMake))
        {
            var tag = PageText.FolderPath(shown);
            if (_config.AllTags().Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                _panelFolderStatus = $"'{shown}' already exists.";
            }
            else
            {
                RememberFolder(tag);
                _config.Save();
                _panelFolderStatus = $"Made '{shown}'.";
                _panelNewFolder    = string.Empty;
                _log.Information($"[Wardrobe] Made folder '{shown}' ahead of use");
            }
        }
        if (!canMake) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("A slash nests: \"Animations/Idles\" is Idles inside Animations.");

        if (!string.IsNullOrEmpty(_panelFolderStatus))
            ImGui.TextDisabled(_panelFolderStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawPenumbraFoldersBlock();

        var root = TagTree.Folders(_config);
        if (root.Children.Count == 0)
        {
            ImGui.TextDisabled("No folders yet.");
            return;
        }

        Hint("Click a folder to open it on the grid. Right-click to rename, colour or delete it.");
        ImGui.Spacing();

        DrawFolderPanelTree(root);
    }

    // ── Folders from Penumbra ─────────────────────────────────────────────────

    private bool   _penumbraFoldersOnlyUnfiled = true;
    private string _penumbraFoldersStatus      = string.Empty;

    /// <summary>
    /// Files every item under the folder its mod sits in on Penumbra's mod list.
    /// </summary>
    /// <remarks>
    /// The mod list is where most people have already done their sorting, and the wardrobe's
    /// folders nest on the same slash, so a mod filed as <c>Gear/Dresses/Red Dress</c> puts its
    /// item in <c>Gear/Dresses</c>. It reads the first mod on an item, which is the one it was
    /// imported from; a supplementary mod's folder is not where the piece lives.
    /// <para>
    /// Only ever adds. By default an item already in a folder is left there, since a folder chosen
    /// by hand outranks one inferred from a mod list; the tick box lets the mod list win instead.
    /// An item whose mod sits at Penumbra's root, or has never been filed, is left alone either
    /// way — there is no folder to copy. The same rule the Glamourer design-folder import follows.
    /// </para>
    /// </remarks>
    private void DrawPenumbraFoldersBlock()
    {
        if (!_config.PenumbraFolderSyncEnabled) return;

        ImGui.TextUnformatted("From Penumbra");
        ImGui.TextDisabled("Files each item under the folder its mod is in on Penumbra's mod list.");
        ImGui.Spacing();

        ImGui.Checkbox("Only items not already in a folder", ref _penumbraFoldersOnlyUnfiled);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On, a folder you chose by hand is kept. Off, every item with a\n" +
                             "filed mod moves to that mod's folder, wherever it was.");

        if (ImGui.Button("File items by Penumbra folder", new Vector2(-1, 0)))
            FileItemsByPenumbraFolder();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads where each item's mod sits in Penumbra and files the item\n" +
                             "under the same folder path. Mods at Penumbra's root, or never\n" +
                             "filed there, are left alone. Nothing in Penumbra is changed.");

        if (!string.IsNullOrEmpty(_penumbraFoldersStatus))
            ImGui.TextDisabled(_penumbraFoldersStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void FileItemsByPenumbraFolder()
    {
        var filed     = 0;
        var kept      = 0;
        var unfiled   = 0;
        var folders   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache     = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _config.WardrobeItems)
        {
            if (_penumbraFoldersOnlyUnfiled && item.Tags.Any(TagTree.IsFolder)) { kept++; continue; }

            var mod = item.Mods.FirstOrDefault(m => !string.IsNullOrEmpty(m.ModDirectory));
            if (mod == null) { unfiled++; continue; }

            // One lookup per mod, not per item: variants share a mod, and a wardrobe of a thousand
            // items is a few hundred mods
            if (!cache.TryGetValue(mod.ModDirectory, out var folder))
                cache[mod.ModDirectory] = folder = Plugin.Penumbra.GetModSortFolder(mod.ModDirectory, mod.ModName);

            var shown = folder == null ? string.Empty : NormaliseTag(folder);
            if (shown.Length == 0) { unfiled++; continue; }

            var tag = PageText.FolderPath(shown);
            RememberFolder(tag);
            folders.Add(shown);

            if (FileTags(item.Tags, tag)) filed++;
            else                          kept++;
        }

        if (filed > 0) _config.Save();

        _penumbraFoldersStatus =
            $"Filed {filed} item(s) into {folders.Count} folder(s); " +
            $"{kept} left where they were, {unfiled} with no folder in Penumbra.";

        _log.Information($"[Wardrobe] Folders from Penumbra: {_penumbraFoldersStatus}");
    }

    /// <summary>The Experimental opt-in, under Settings → Grid & Cards.</summary>
    private void DrawPenumbraFolderSettings()
    {
        Hint("Files items under the folders their mods sit in on Penumbra's mod list.",
             "A button in View → Folders reads where each item's mod is filed in\n" +
             "Penumbra and puts the item in a wardrobe folder of the same path —\n" +
             "Gear/Dresses/Red Dress files its item in Gear/Dresses. Nothing in\n" +
             "Penumbra is changed, and the folders it makes are ordinary folders.");
        ImGui.Spacing();

        var on = _config.PenumbraFolderSyncEnabled;
        if (ImGui.Checkbox("Offer filing by Penumbra folder", ref on))
        {
            _config.PenumbraFolderSyncEnabled = on;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds a From Penumbra block to the Folders panel under View. It only\n" +
                             "ever files; by default items already in a folder are left there.");
    }

    /// <summary>The folder tree, each node with how much is filed under it.</summary>
    private void DrawFolderPanelTree(TagNode node)
    {
        foreach (var (_, child) in node.Children)
        {
            var shown  = PageText.FolderShown(child.FullPath);
            var total  = FiledUnder(child.FullPath);
            var isLeaf = child.Children.Count == 0;
            var here   = shown.Equals(FolderPathOf(_outfitsView), StringComparison.OrdinalIgnoreCase);

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth
                      | ImGuiTreeNodeFlags.DefaultOpen;
            if (isLeaf) flags |= ImGuiTreeNodeFlags.Leaf;
            if (here)   flags |= ImGuiTreeNodeFlags.Selected;

            // Scoped per node, or the context menus of two collapsed siblings share one id
            ImGui.PushID(child.FullPath);

            var tint   = _config.TagColoursEnabled ? TagTree.Colour(_config, child.FullPath) : null;
            if (tint is { } c) ImGui.PushStyleColor(ImGuiCol.Text, TagTree.Blend(new Vector4(1f, 1f, 1f, 1f), c, 0.6f));

            var open = ImGui.TreeNodeEx($"{child.Segment}  ({total})##folderpanel_{child.FullPath}", flags);

            if (tint != null) ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{shown}\n{total} filed here, counting sub-folders.\n\nClick to open it on the grid.");

            DrawFolderContextMenu(_outfitsView, shown, child.FullPath, child.Segment, total);

            // Only the label opens the folder; the arrow is for the tree
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var clickX = ImGui.GetMousePos().X - ImGui.GetItemRectMin().X;
                if (isLeaf || clickX >= ImGui.GetTreeNodeToLabelSpacing())
                    OpenFolder(_outfitsView, shown);
            }

            if (open)
            {
                if (!isLeaf) DrawFolderPanelTree(child);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }
    }

    /// <summary>How many items and outfits carry this folder tag or one beneath it.</summary>
    private int FiledUnder(string tag)
    {
        bool Under(string t) => t.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                                t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase);

        return _config.WardrobeItems.Count(i => i.Tags.Any(Under))
             + _config.Outfits.Count(o => o.Tags.Any(Under));
    }

    // ── Adding on mass, from inside a folder ──────────────────────────────────

    private string _addToFolderSearch = string.Empty;
    private readonly HashSet<Guid>   _addToFolderPicked        = new();
    private readonly HashSet<string> _addToFolderPickedFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The "Add to this folder…" button on the crumb row, and the picker it opens.
    /// </summary>
    /// <remarks>
    /// A popup with a search box and a tick list of everything not already filed exactly here,
    /// on whichever grid is in front — and above it, every other folder, so a folder can be moved
    /// inside this one, which is otherwise impossible: a rename changes only the last segment.
    /// The list is names and slots without pictures, and only the rows in view are drawn: the
    /// import panel learned the hard way what drawing every picture of a wardrobe in one list
    /// costs.
    /// </remarks>
    private void DrawAddToFolderButton(bool outfits)
    {
        var here = FolderPathOf(outfits);
        var noun = outfits ? "outfit" : "item";

        ImGui.SameLine();
        var label = " Add to this folder… ";
        var x     = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - UiLayout.ButtonWidth(label);
        if (x > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(x);

        if (ImGui.Button(label))
        {
            _addToFolderSearch = string.Empty;
            _addToFolderPicked.Clear();
            _addToFolderPickedFolders.Clear();
            ImGui.OpenPopup("##addtofolder");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Pick {noun}s from the whole wardrobe to file in '{here}',\n" +
                             "or other folders to move inside it.");

        if (!ImGui.BeginPopup("##addtofolder")) return;

        ImGui.TextDisabled($"Add to '{here}'");
        ImGui.Separator();

        ImGui.SetNextItemWidth(UiScale.S(280));
        ImGui.InputTextWithHint("##addtofoldersearch", "Search…", ref _addToFolderSearch, 128);

        var needle = _addToFolderSearch.Trim();
        var tag    = PageText.FolderPath(here);

        // Everything not already filed exactly here. What is filed elsewhere is offered — filing
        // is a move, and moving it here is a fair thing to want.
        var rows = outfits
            ? _config.Outfits
                .Where(o => !o.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                .Where(o => needle.Length == 0 || o.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.Name, NaturalOrder.Comparer)
                .Select(o => (o.Id, o.Name, Note: FolderNote(o.Tags)))
                .ToList()
            : _config.WardrobeItems
                .Where(i => !i.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                .Where(i => needle.Length == 0 || ImportSearchMatches(i, needle))
                .OrderBy(i => (int)i.Slot).ThenBy(i => i.Name, NaturalOrder.Comparer)
                .Select(i => (i.Id, i.Name, Note: (string?)(FolderNote(i.Tags) ?? i.Slot.DisplayName())))
                .ToList();

        // Every other folder that could sit inside this one: not this one, not what is above it
        // (a path cannot contain itself), and not what is already directly inside
        var movable = _config.AllTags()
            .Where(TagTree.IsFolder)
            .Select(PageText.FolderShown)
            .Where(f => !f.Equals(here, StringComparison.OrdinalIgnoreCase))
            .Where(f => !here.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase))
            .Where(f => !(f.StartsWith(here + "/", StringComparison.OrdinalIgnoreCase) &&
                          f.IndexOf('/', here.Length + 1) < 0))
            .Where(f => needle.Length == 0 || f.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, NaturalOrder.Comparer)
            .ToList();

        if (ImGui.SmallButton("Tick all shown"))
            foreach (var row in rows) _addToFolderPicked.Add(row.Id);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            _addToFolderPicked.Clear();
            _addToFolderPickedFolders.Clear();
        }

        ImGui.Spacing();

        if (movable.Count > 0)
        {
            ImGui.TextDisabled("Folders");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Ticked folders move inside this one, with everything in them.");

            foreach (var folder in movable)
            {
                var picked = _addToFolderPickedFolders.Contains(folder);
                if (ImGui.Checkbox($"{folder}##movefolder", ref picked))
                {
                    if (picked) _addToFolderPickedFolders.Add(folder);
                    else        _addToFolderPickedFolders.Remove(folder);
                }
            }

            ImGui.Spacing();
            ImGui.TextDisabled(outfits ? "Outfits" : "Items");
        }

        var height = Math.Min(UiScale.S(360), ImGui.GetFrameHeightWithSpacing() * Math.Max(rows.Count, 3) + UiScale.S(8));
        if (ImGui.BeginChild("##addtofolderlist", new Vector2(UiScale.S(360), height), true))
        {
            if (rows.Count == 0)
                ImGui.TextDisabled(needle.Length > 0 ? "Nothing matches." : $"Every {noun} is already here.");

            var rowH   = ImGui.GetFrameHeightWithSpacing();
            var top    = ImGui.GetScrollY() - rowH;
            var bottom = ImGui.GetScrollY() + ImGui.GetWindowHeight() + rowH;

            foreach (var (id, name, note) in rows)
            {
                var y = ImGui.GetCursorPosY();
                if (y + rowH < top || y > bottom)
                {
                    ImGui.Dummy(new Vector2(0f, ImGui.GetFrameHeight()));
                    continue;
                }

                ImGui.PushID(id.ToString());
                var picked = _addToFolderPicked.Contains(id);
                if (ImGui.Checkbox($"{name}##add", ref picked))
                {
                    if (picked) _addToFolderPicked.Add(id);
                    else        _addToFolderPicked.Remove(id);
                }

                if (note != null)
                {
                    UiLayout.SameLineIfRoomForText(note);
                    ImGui.TextDisabled(note);
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        var count   = _addToFolderPicked.Count;
        var moving  = _addToFolderPickedFolders.Count;
        var nothing = count == 0 && moving == 0;

        var addLabel = moving == 0 ? $" Add {count} {noun}(s) "
                     : count  == 0 ? $" Move {moving} folder(s) here "
                                   : $" Add {count} {noun}(s) and move {moving} folder(s) ";

        if (nothing) ImGui.BeginDisabled();
        if (ImGui.Button(addLabel, new Vector2(-1, 0)))
        {
            var filed = count == 0 ? 0 : outfits
                ? FileOutfitsInFolder(_config.Outfits.Where(o => _addToFolderPicked.Contains(o.Id)), here)
                : FileInFolder(_config.WardrobeItems.Where(i => _addToFolderPicked.Contains(i.Id)), here);

            var moved  = 0;
            var refused = new List<string>();
            foreach (var folder in _addToFolderPickedFolders.OrderBy(f => f, NaturalOrder.Comparer).ToList())
            {
                if (MoveFolder(folder, here) is { } why) refused.Add(why);
                else moved++;
            }

            _bulkStatus = (count > 0 ? $"Filed {filed} {noun}(s) in '{here}'. " : string.Empty)
                        + (moved > 0 ? $"Moved {moved} folder(s) inside. " : string.Empty)
                        + string.Join(" ", refused);
            _addToFolderPicked.Clear();
            _addToFolderPickedFolders.Clear();
            ImGui.CloseCurrentPopup();
        }
        if (nothing) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(nothing ? $"Tick some {noun}s or folders first."
                           : outfits ? "Each leaves whatever folder it was in."
                                     : "Each leaves whatever folder it was in; variants come with their original.");

        ImGui.EndPopup();
    }

    /// <summary>"in Summer" for a card filed somewhere, or null for one that is not.</summary>
    private static string? FolderNote(List<string> tags)
    {
        var filed = FolderPathsOf(tags).ToList();
        return filed.Count == 0 ? null : "in " + string.Join(", ", filed);
    }
}
