using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// The right-click menu on an item card: the quick edits, without opening the editor.
/// </summary>
/// <remarks>
/// The card's buttons are the two or three things done constantly; everything else used to mean
/// Edit, a panel, and the grid reflowing to make room for it. Renaming, favouriting, tagging and
/// filing are none of them worth a panel, and a menu on the card is where a hand already holding
/// the mouse over the card expects to find them.
/// <para>
/// In select mode, right-clicking a ticked card acts on the whole selection: the menu says how
/// many, and the entries that only make sense for one card step aside. Right-clicking an unticked
/// card acts on that card alone, which is what the click was aimed at.
/// </para>
/// <para>
/// The whole card, picture included, opens this menu. The picture used to take the right-click
/// for the full-size viewer on its own, and the two fought over which a click a pixel either side
/// of the picture's edge meant; the viewer is an entry here now.
/// </para>
/// </remarks>
public partial class PluginUi
{
    /// <summary>The new-tag box in the menu, shared by every card since only one menu is open at a time.</summary>
    private string _menuNewTag = string.Empty;

    /// <summary>Opens the menu on a right-click anywhere on the card, and draws it.</summary>
    /// <param name="pendingDelete">Set to the item's id when Delete is chosen, as the card's X does.</param>
    private void DrawCardContextMenu(WardrobeItem item, bool worn, List<WardrobeItem> links, ref Guid? pendingDelete)
    {
        var popup = $"##cardmenu_{item.Id}";

        if (ImGui.IsWindowHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            ImGui.OpenPopup(popup);

        if (!ImGui.BeginPopup(popup)) return;

        // The batch: the selection when this card is part of it, otherwise this card
        var batch = _selectMode && _selected.Contains(item.Id) && _selected.Count > 1
            ? SelectedItems()
            : new List<WardrobeItem> { item };
        var single = batch.Count == 1;

        ImGui.TextDisabled(single ? Truncate(item.Name, 40) : $"{batch.Count} selected");
        ImGui.Separator();

        if (single) DrawMenuWearEntries(item, worn, links);

        if (single)
        {
            if (ImGui.MenuItem("Edit…")) OpenItemEditor(item);
            if (ImGui.MenuItem("Rename")) BeginCardRename(item);
        }

        DrawMenuSelect(item);

        DrawMenuFavourite(batch);

        ImGui.Separator();

        DrawPutInFolderMenu(item.Id.ToString(), false, SharedFolders(batch.Select(i => i.Tags)),
                            f => FileInFolder(batch, f));

        if (batch.Any(i => i.Tags.Any(TagTree.IsFolder)) && ImGui.MenuItem("Take out of folder"))
            _bulkStatus = $"Took {Unfile(batch)} item(s) out of their folder.";

        DrawMenuTags(batch, item.Id.ToString());
        DrawMenuStyles(batch);

        ImGui.Separator();

        if (_session.CanStart && ImGui.MenuItem(single ? "Screenshot" : $"Screenshot {batch.Count}"))
        {
            if (single) _session.StartSingle(item);
            else        _session.StartMany(batch);
        }
        if (_session.CanStart && ImGui.IsItemHovered())
            ImGui.SetTooltip(single
                ? "Wears this item and waits for a screenshot, as a session does."
                : "A session over exactly these, whether or not they have a picture.");

        if (single && !string.IsNullOrEmpty(item.ImagePath))
        {
            var count = item.ImageCount();
            if (ImGui.MenuItem(count > 1 ? $"View pictures ({count})" : "View picture"))
                _quickViewItem = item.Id;
        }

        DrawCopyToWardrobeMenu(batch, item.Id.ToString());

        if (single)
        {
            ImGui.Separator();
            if (UiLayout.DeleteMenuItem("Delete")) pendingDelete = item.Id;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Deletes '{item.Name}' from the wardrobe.\nThe Penumbra mod itself is not touched.");
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// Select this card — entering select mode with it ticked when not already selecting — or
    /// untick it when it is.
    /// </summary>
    /// <remarks>
    /// The way into select mode from the thing you want selected, rather than from the menu bar
    /// and then back to the card. Batches are left alone: with several selected the menu is about
    /// all of them, and the one to untick is the one under the cursor.
    /// </remarks>
    private void DrawMenuSelect(WardrobeItem item)
    {
        var picked = _selectMode && _selected.Contains(item.Id);

        if (!ImGui.MenuItem(picked ? "Deselect" : "Select")) return;

        if (!_selectMode)
        {
            _selectMode = true;
            EnterSelectMode();
        }
        Select(item.Id, !picked);
    }

    /// <summary>Wear or remove, with the linked pieces, and the solo version when there are any.</summary>
    private void DrawMenuWearEntries(WardrobeItem item, bool worn, List<WardrobeItem> links)
    {
        var (wearLabel, removeLabel) = item.Slot.ActionLabels();
        var suffix = links.Count > 0 ? $" +{links.Count}" : string.Empty;

        if (ImGui.MenuItem(worn ? $"{removeLabel}{suffix}" : $"{wearLabel}{suffix}"))
        {
            if (worn) _wardrobe.UnwearItemLinked(item);
            else      _wardrobe.WearItemLinked(item);
        }
        if (links.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip((worn ? "Also takes off:\n" : "Also wears:\n") + LinkList(links));

        if (links.Count > 0 && ImGui.MenuItem(worn ? $"{removeLabel} only this" : $"{wearLabel} only this"))
            Solo(item, worn);

        DrawMenuSizes(item);

        ImGui.Separator();
    }

    /// <summary>One entry that reads as whichever way the batch mostly is not.</summary>
    private void DrawMenuFavourite(List<WardrobeItem> batch)
    {
        var all = batch.All(i => i.IsFavorite);

        if (!ImGui.MenuItem(all ? "♥ Unfavourite" : "♥ Favourite")) return;

        foreach (var i in batch) i.IsFavorite = !all;
        _config.Save();
    }

    /// <summary>
    /// Every ordinary tag as a tick, plus a box for a new one.
    /// </summary>
    /// <remarks>
    /// Flat and alphabetical rather than the tree, for the same reason the folder list is: a menu
    /// is a poor place for a tree. A tick means every card in the batch has it; a tag some of them
    /// have shows unticked, and ticking it gives it to the rest. Styles and folders are not here —
    /// each has an entry of its own.
    /// </remarks>
    private void DrawMenuTags(List<WardrobeItem> batch, string id)
    {
        if (!ImGui.BeginMenu("Tags")) return;

        var tags = _config.AllTags()
            .Where(t => !TagTree.IsStyle(t) && !TagTree.IsFolder(t))
            .ToList();

        foreach (var tag in tags)
        {
            var all = batch.All(i => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

            if (ImGui.MenuItem(tag, string.Empty, all))
                SetTagOnBatch(batch, tag, !all);
        }

        if (tags.Count > 0) ImGui.Separator();

        ImGui.SetNextItemWidth(UiScale.S(180));
        var entered = ImGui.InputTextWithHint($"##menutag_{id}", "new tag, or Parent/Child",
            ref _menuNewTag, 64, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var canAdd = NormaliseTag(_menuNewTag).Length > 0;
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button($" Add ##{id}") || (entered && canAdd))
        {
            SetTagOnBatch(batch, NormaliseTag(_menuNewTag), true);
            _menuNewTag = string.Empty;
        }
        if (!canAdd) ImGui.EndDisabled();

        ImGui.EndMenu();
    }

    /// <summary>Every style as a tick, exactly as the tags.</summary>
    private void DrawMenuStyles(List<WardrobeItem> batch)
    {
        var styles = TagTree.Styles(_config);
        if (styles.Count == 0) return;

        if (!ImGui.BeginMenu("Styles")) return;

        foreach (var style in styles)
        {
            var all = batch.All(i => i.Tags.Contains(style.FullPath, StringComparer.OrdinalIgnoreCase));

            if (ImGui.MenuItem(style.Segment, string.Empty, all))
                SetTagOnBatch(batch, style.FullPath, !all);
        }

        ImGui.EndMenu();
    }

    /// <summary>Puts a tag on every card in the batch, or takes it off every one.</summary>
    private void SetTagOnBatch(List<WardrobeItem> batch, string tag, bool on)
    {
        var changed = 0;

        foreach (var item in batch)
        {
            if (on)
            {
                if (item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
                item.Tags.Add(tag);
                changed++;
            }
            else if (item.Tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                changed++;
            }
        }

        if (changed > 0) _config.Save();
        _bulkStatus = changed == 0 ? "No items changed."
                    : $"{(on ? "Tagged" : "Untagged")} {changed} item(s).";
    }

    // ── Folders on the filter row ─────────────────────────────────────────────

    /// <summary>
    /// The Folders filter button, beside ♥, Worn and Variants.
    /// </summary>
    /// <remarks>
    /// A toggle like the buttons beside it, lit while it is on. Only once a folder exists: before
    /// that it would be a button that empties the grid and never does anything else, which is why
    /// the Variants button waits the same way. Turning it off also leaves whatever folder was open,
    /// so the next press starts at the top.
    /// </remarks>
    private void DrawFoldersRowButton()
    {
        if (TagTree.Folders(_config).Children.Count == 0)
        {
            // The last folder gone while the filter was on would otherwise leave an empty grid
            // with no lit button to explain it
            if (_foldersOnly) SetFoldersFilter(false);
            return;
        }

        UiLayout.SameLineIfRoomForButton("Folders");

        // Captured before the button for the reason the filter buttons beside it capture theirs:
        // a pop guarded on the live value would pop a style that was never pushed
        var active = _foldersOnly;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.24f, 0.36f, 0.52f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.32f, 0.47f, 0.66f, 1f));
        }
        if (ImGui.Button("Folders", FilterRowButton("Folders"))) SetFoldersFilter(!active);
        var hovered = ImGui.IsItemHovered();
        if (active) ImGui.PopStyleColor(2);

        if (hovered)
            ImGui.SetTooltip(active
                ? (InFolder(_outfitsView)
                    ? $"Inside {FolderPathOf(_outfitsView)}. Click to show everything again."
                    : "Showing folders. Open one to see what is inside; click to show everything again.")
                : "Show the grid by folder: a card per folder, opened to what is\n" +
                  "inside. Search and the other filters look inside every folder.");
    }

    private void SetFoldersFilter(bool on)
    {
        _foldersOnly      = on;
        _folderPath       = string.Empty;
        _outfitFolderPath = string.Empty;
        _gridToTop        = true;
    }
}
