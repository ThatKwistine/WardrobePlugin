using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// Folders on the two grids: the cards that stand in for what is filed inside them, opening one,
/// and the actions that file things.
/// </summary>
/// <remarks>
/// A folder is a tag under <see cref="PageText.FolderRoot"/> and nothing else — see there for why.
/// This file is the whole of what makes such a tag behave like a folder: a grid folds the cards
/// carrying one into a card per folder, clicking that card narrows the grid to the folder, and the
/// filing actions are tag actions wearing folder names. Rename, colour and delete are the tag
/// panel's own, reached from the card's menu.
/// <para>
/// One set of folders for both grids, since tags are one set for both: filing an outfit in
/// <c>Animations</c> puts it in the same folder the items are in. Each grid keeps its own place —
/// being inside a folder on the items grid says nothing about where the outfits grid is looking —
/// so everything that reads or moves the open folder takes which grid it is for.
/// </para>
/// </remarks>
public partial class PluginUi
{
    /// <summary>A folder as a grid shows it: the card, and what is behind it.</summary>
    /// <param name="Shown">The path as it is shown, "Animations/Idles".</param>
    /// <param name="Tag">The path as it is stored, "Folder/Animations/Idles".</param>
    /// <param name="Name">The last segment, which is what the card is labelled.</param>
    /// <param name="Items">What is inside after the current filters, in grid order, sub-folders included.</param>
    /// <param name="Total">What is inside before any filter, so a search can say "3 of 40".</param>
    private sealed record FolderCard<T>(string Shown, string Tag, string Name, List<T> Items, int Total);

    /// <summary>The folder each grid is showing the inside of, as shown; empty at the top level.</summary>
    private string _folderPath       = string.Empty;
    private string _outfitFolderPath = string.Empty;

    /// <summary>The folder cards each grid last built, drawn ahead of the loose cards.</summary>
    private List<FolderCard<WardrobeItem>> _gridFolders   = new();
    private List<FolderCard<Outfit>>       _outfitFolders = new();

    /// <summary>
    /// The Folders filter: on, a grid shows its folders — the cards at the top, and what is inside
    /// the one that is open. Off, every card stands on its own, filed or not.
    /// </summary>
    /// <remarks>
    /// A filter like ♥ and Worn rather than a mode the grid is always in. Folding the top level
    /// whenever a folder existed meant a filed item vanished from the grid the moment it was filed,
    /// and the only way to see everything at once was a setting. As a filter it composes with the
    /// rest: Worn and Folders together is the worn pieces, by folder. Shared by both grids, as the
    /// style filter is; each grid keeps its own open folder.
    /// </remarks>
    private bool _foldersOnly;

    /// <summary>Whether the grids fold anything at all right now.</summary>
    private bool FoldersOn => _foldersOnly;

    /// <summary>The open folder of one grid, by reference so it can be moved.</summary>
    private ref string FolderPathOf(bool outfits) => ref outfits ? ref _outfitFolderPath : ref _folderPath;

    /// <summary>Whether a grid is inside a folder rather than at the top.</summary>
    private bool InFolder(bool outfits) => FoldersOn && FolderPathOf(outfits).Length > 0;

    /// <summary>Every folder tag on a card, as shown paths.</summary>
    private static IEnumerable<string> FolderPathsOf(IEnumerable<string> tags) =>
        tags.Where(TagTree.IsFolder).Select(PageText.FolderShown);

    // ── Building ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a grid's filtered, sorted cards into what is loose at its current level and what is
    /// folded into a folder card, and returns the folder cards.
    /// </summary>
    /// <param name="sorted">The cards after the grid's filters and sort.</param>
    /// <param name="all">Every card the grid could show, for the unfiltered counts.</param>
    /// <param name="tags">A card's tags.</param>
    /// <param name="level">The folder the grid is inside, as shown; empty at the top.</param>
    /// <param name="filtering">Whether any search or filter is narrowing the grid.</param>
    /// <param name="loose">What stays at this level, in the order given.</param>
    /// <remarks>
    /// Applied after the filters and before anything else folds, so a search or a slot filter
    /// reaches inside every folder and the card says how much of it matched — a search for "idle"
    /// showing three folders rather than a hundred cards was the request that started this. A
    /// folder with nothing left inside after filtering is not shown; an empty folder is, but only
    /// when nothing is being filtered, since that is the one time "empty" is a fact about the
    /// folder rather than about the filter.
    /// <para>
    /// A card is loose at a level when it carries that level's tag exactly; at the top nothing is,
    /// since the filter shows folders and a card in none of them is what turning it off is for.
    /// Anything filed deeper goes into the card for the next segment down; a piece filed in two
    /// folders is in both cards, which is what a tag means.
    /// </para>
    /// </remarks>
    private List<FolderCard<T>> FoldFolders<T>(List<T> sorted, IEnumerable<T> all,
        Func<T, List<string>> tags, string level, bool filtering, out List<T> loose)
    {
        loose = sorted;
        if (!FoldersOn) return new List<FolderCard<T>>();

        var prefix  = level.Length == 0 ? string.Empty : level + "/";
        var kept    = new List<T>(sorted.Count);
        var folders = new Dictionary<string, FolderCard<T>>(StringComparer.OrdinalIgnoreCase);

        FolderCard<T> CardFor(string segment)
        {
            if (folders.TryGetValue(segment, out var card)) return card;

            var shown = prefix + segment;
            return folders[segment] = new FolderCard<T>(shown, PageText.FolderPath(shown), segment,
                                                        new List<T>(), 0);
        }

        // The next segment down for every folder path under this level, or null for the level itself
        static string? SegmentBelow(string path, string prefix)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var rest = path[prefix.Length..];
            if (rest.Length == 0) return null;
            var cut = rest.IndexOf('/');
            return cut < 0 ? rest : rest[..cut];
        }

        foreach (var card in sorted)
        {
            var filedHere  = false;
            var filedBelow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in FolderPathsOf(tags(card)))
            {
                if (level.Length > 0 && path.Equals(level, StringComparison.OrdinalIgnoreCase))
                {
                    filedHere = true;
                    continue;
                }

                if (SegmentBelow(path, prefix) is { } segment) filedBelow.Add(segment);
            }

            // At the top the filter shows folders and nothing else: a card in none of them is
            // what turning the filter off is for
            if (filedHere) kept.Add(card);

            foreach (var segment in filedBelow)
                CardFor(segment).Items.Add(card);
        }

        // Folders made ahead of use, or emptied, still exist — but only shown when nothing is
        // being filtered, for the reason in the summary
        if (!filtering && FolderNodeAt(level) is { } node)
            foreach (var (segment, _) in node.Children)
                CardFor(segment);

        // The unfiltered count, so the card can say "3 of 40 match" during a search
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in all)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in FolderPathsOf(tags(card)))
                if (SegmentBelow(path, prefix) is { } segment && seen.Add(segment))
                    totals[segment] = totals.GetValueOrDefault(segment) + 1;
        }

        loose = kept;

        return folders.Values
            .Select(f => f with { Total = totals.GetValueOrDefault(f.Name) })
            .OrderBy(f => f.Name, NaturalOrder.Comparer)
            .ToList();
    }

    /// <summary>The folder tree node for a shown path, or null when no such folder exists.</summary>
    private TagNode? FolderNodeAt(string shown)
    {
        var node = TagTree.Folders(_config);
        if (shown.Length == 0) return node;

        foreach (var segment in shown.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(segment, out var child)) return null;
            node = child;
        }

        return node;
    }

    /// <summary>Leaves a folder that no longer exists — renamed or deleted under an open grid.</summary>
    private void EnsureFolderPathExists(bool outfits)
    {
        if (!InFolder(outfits)) return;
        if (FolderNodeAt(FolderPathOf(outfits)) == null) FolderPathOf(outfits) = string.Empty;
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>Set by a change of level, so the grid starts the new one at the top.</summary>
    private bool _gridToTop;

    private void OpenFolder(bool outfits, string shown)
    {
        // Opening a folder from anywhere — the panel, the crumbs, a card — is asking to see
        // folders, so the filter comes on with it rather than the grid staying flat and the
        // folder opening into nothing
        if (shown.Length > 0) _foldersOnly = true;

        FolderPathOf(outfits) = shown;
        _gridToTop = true;
        _log.Debug($"[Wardrobe] Opened folder '{shown}' on the {(outfits ? "outfits" : "items")} grid");
    }

    private void CloseFolder(bool outfits)
    {
        ref var path = ref FolderPathOf(outfits);
        var cut = path.LastIndexOf('/');
        path = cut < 0 ? string.Empty : path[..cut];
        _gridToTop = true;
    }

    /// <summary>
    /// The row above a grid saying which folder is open, with the way back.
    /// </summary>
    /// <remarks>
    /// Drawn only inside a folder. At the top there is nothing to say, and a row that appears only
    /// when there is somewhere to go back from is itself the sign that you are inside something.
    /// Every crumb is a button, so a folder three deep is one click from any level above it.
    /// </remarks>
    private void DrawFolderCrumbs(bool outfits)
    {
        if (!InFolder(outfits)) return;

        if (ImGui.Button(" < Back ")) CloseFolder(outfits);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Up one level.");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();

        if (ImGui.SmallButton("All##crumb")) OpenFolder(outfits, string.Empty);

        var segments = FolderPathOf(outfits).Split('/');
        var path     = string.Empty;

        for (var i = 0; i < segments.Length; i++)
        {
            path = i == 0 ? segments[0] : $"{path}/{segments[i]}";

            ImGui.SameLine();
            ImGui.TextDisabled("›");
            ImGui.SameLine();

            if (i == segments.Length - 1)
            {
                ImGui.TextUnformatted(segments[i]);
                continue;
            }

            var target = path;
            if (ImGui.SmallButton($"{segments[i]}##crumb{i}")) OpenFolder(outfits, target);
        }

        DrawAddToFolderButton(outfits);

        ImGui.Separator();
    }

    // ── The card ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A folder's card: a mosaic of what is inside, its name, its count, and the way in.
    /// </summary>
    /// <param name="size">The size of the cards around it, so the grid's arithmetic does not know it is there.</param>
    /// <param name="mosaic">The side of the mosaic — the width those cards give their picture.</param>
    /// <param name="texture">A card's picture, for the mosaic.</param>
    /// <param name="ids">A card's id, for select mode.</param>
    /// <param name="selected">The grid's selection, for select mode.</param>
    /// <remarks>
    /// The mosaic is the first nine pieces in grid order, which is the sort the user chose; a folder
    /// with fewer shows what it has and dark squares for the rest. The whole mosaic is the button
    /// that opens it, with an Open button below for anyone who expects one.
    /// <para>
    /// In select mode the card offers to tick everything inside instead of opening — moving a
    /// whole folder somewhere else is the reason to want that, and opening is one click away on
    /// the crumb row once the mode is off.
    /// </para>
    /// </remarks>
    private void DrawFolderCard<T>(bool outfits, FolderCard<T> folder, Vector2 size, float mosaic,
        Func<T, ISharedImmediateTexture?> texture, Func<T, Guid> ids, HashSet<Guid> selected)
    {
        var background = new Vector4(0.13f, 0.13f, 0.17f, 1f);
        var border     = new Vector4(0.36f, 0.36f, 0.44f, 1f);

        if (TagTree.Colour(_config, folder.Tag) is { } tint)
        {
            background = TagTree.Blend(background, tint, 0.3f);
            border     = TagTree.Blend(border,     tint, 0.75f);
        }

        ImGui.PushID("folder_" + folder.Tag);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border,  border);

        ImGui.BeginChild("##foldercard", size, true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        DrawFolderMosaic(folder, mosaic, texture);

        // Opened from the mosaic outside select mode. Inside it the mosaic is inert, so a click
        // meant for the tick button below cannot vanish into the folder.
        if (!_selectMode && ImGui.IsItemHovered())
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) OpenFolder(outfits, folder.Shown);
            ImGui.SetTooltip($"{folder.Shown}\n\nClick to open.");
        }

        DrawFolderContextMenu(outfits, folder.Shown, folder.Tag, folder.Name, folder.Total);

        ImGui.TextUnformatted(CardName(folder.Name));
        if (ImGui.IsItemHovered() && folder.Name != CardName(folder.Name))
            ImGui.SetTooltip(folder.Shown);

        // The count takes the badge line the other cards have, so the button row lands where it does there
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.75f, 0.95f, 1f));
        var searching = folder.Items.Count != folder.Total;
        var noun      = outfits ? "outfit" : "item";
        ImGui.TextUnformatted(searching
            ? $"{folder.Items.Count} of {folder.Total} match"
            : folder.Total == 1 ? $"1 {noun}" : $"{folder.Total} {noun}s");
        ImGui.PopStyleColor();
        if (searching && ImGui.IsItemHovered())
            ImGui.SetTooltip("How much of what is inside the current search or filter lets through.");

        if (_selectMode)
        {
            DrawFolderSelector(folder.Items.Select(ids).ToList(), selected);
        }
        else if (ImGui.Button(" Open ", new Vector2(-1, 0)))
        {
            OpenFolder(outfits, folder.Shown);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopID();
    }

    /// <summary>The 3x3 of what is inside, in the space a card gives its picture.</summary>
    private void DrawFolderMosaic<T>(FolderCard<T> folder, float side, Func<T, ISharedImmediateTexture?> texture)
    {
        var gap   = UiScale.S(2f);
        var cell  = (side - gap * 2) / 3f;
        var at    = ImGui.GetCursorScreenPos();
        var draw  = ImGui.GetWindowDrawList();
        var empty = ImGui.GetColorU32(new Vector4(0.07f, 0.07f, 0.09f, 1f));

        for (var i = 0; i < 9; i++)
        {
            var min = new Vector2(at.X + (i % 3) * (cell + gap), at.Y + (i / 3) * (cell + gap));
            var max = min + new Vector2(cell, cell);

            if (i < folder.Items.Count && texture(folder.Items[i])?.GetWrapOrDefault() is { } wrap)
            {
                var (uv0, uv1) = ImageDraw.SquareCropUvs(wrap.Width, wrap.Height);
                draw.AddImage(wrap.Handle, min, max, uv0, uv1);
            }
            else
            {
                draw.AddRectFilled(min, max, empty);
            }
        }

        // One item covering the whole mosaic, so hover and click read as the picture
        ImGui.InvisibleButton("##mosaic", new Vector2(side, side));
    }

    /// <summary>Select mode's row on a folder card: tick or untick everything inside.</summary>
    private void DrawFolderSelector(List<Guid> ids, HashSet<Guid> selected)
    {
        var all = ids.Count > 0 && ids.All(selected.Contains);
        var any = ids.Any(selected.Contains);

        var label = all ? "Untick all inside" : any ? "Tick the rest" : "Tick all inside";

        if (ids.Count == 0) ImGui.BeginDisabled();
        if (ImGui.Button(label, new Vector2(-1, 0)))
        {
            if (all) foreach (var id in ids) selected.Remove(id);
            else     foreach (var id in ids) selected.Add(id);
            _bulkStatus = string.Empty;
        }
        if (ids.Count == 0) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ids.Count == 0
                ? "Nothing inside to tick."
                : $"{ids.Count} inside, counting sub-folders — what the current\n" +
                  "search or filter lets through.");
    }

    /// <summary>Rename, colour, empty and delete, off the card's right-click.</summary>
    private void DrawFolderContextMenu(bool outfits, string shown, string tag, string name, int total)
    {
        if (!ImGui.BeginPopupContextItem("##folderctx")) return;

        ImGui.TextDisabled(shown);
        ImGui.Separator();

        if (ImGui.MenuItem("Open")) OpenFolder(outfits, shown);

        if (ImGui.MenuItem("Rename…"))
        {
            _renameFolderTag = tag;
            _renameFolderBuf = name;
            _renameFolderErr = string.Empty;
        }

        if (_config.TagColoursEnabled)
        {
            ImGui.Separator();
            DrawTagColourMenu(tag, "folder");
        }

        ImGui.Separator();

        if (total > 0 && ImGui.MenuItem("Take everything out"))
            TakeOutOfFolder(tag);
        if (total > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip("Unfiles what is inside, sub-folders included, items and\n" +
                             "outfits both. Nothing is deleted; it all goes back to the\n" +
                             "level above.");

        if (UiLayout.DeleteMenuItem("Delete folder"))
            DeleteFolder(tag);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Unfiles everything inside and forgets the folder. Nothing is\n" +
                             "deleted — a folder is only a place things are filed.");

        ImGui.EndPopup();
    }

    // ── Renaming ──────────────────────────────────────────────────────────────

    private string _renameFolderTag   = string.Empty;
    private string _renameFolderBuf   = string.Empty;
    private string _renameFolderErr   = string.Empty;
    private bool   _renameFolderShown;

    /// <summary>
    /// The rename popup, drawn wherever a grid is, over whatever card asked for it.
    /// </summary>
    /// <remarks>
    /// A popup rather than the in-place box tags and styles use, because a folder card has no
    /// row to rename in place — its name sits under a mosaic in a fixed-size card. The rename
    /// itself is the tag panel's, so a renamed folder takes its colour, its sub-folders and any
    /// filter pointing at it along, and both grids' open folders follow it.
    /// </remarks>
    private void DrawFolderRenamePopup()
    {
        if (_renameFolderTag.Length == 0) return;

        const string popup = "Rename folder###renamefolder";
        if (!_renameFolderShown && !ImGui.IsPopupOpen(popup)) ImGui.OpenPopup(popup);

        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // Closed by something other than its own buttons — Escape, say — is still closed
            if (_renameFolderShown) _renameFolderTag = string.Empty;
            _renameFolderShown = false;
            return;
        }

        _renameFolderShown = true;

        ImGui.TextDisabled(PageText.FolderShown(_renameFolderTag));
        ImGui.Spacing();

        ImGui.SetNextItemWidth(UiScale.S(240));
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        var entered = ImGui.InputText("##renamefolderbuf", ref _renameFolderBuf, 64,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        if (!string.IsNullOrEmpty(_renameFolderErr))
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.35f, 1f), _renameFolderErr);

        ImGui.Spacing();

        if (ImGui.Button(" Rename ") || entered)
        {
            var old = _renameFolderTag;
            _renameTagError = string.Empty;
            RenameTag(old, _renameFolderBuf);

            if (_renameTagError.Length > 0)
            {
                _renameFolderErr = _renameTagError;
                _renameTagError  = string.Empty;
            }
            else
            {
                var shownOld = PageText.FolderShown(old);
                var cut      = shownOld.LastIndexOf('/');
                var parent   = cut < 0 ? string.Empty : shownOld[..cut];
                var segment  = NormaliseTag(_renameFolderBuf);
                var shownNew = parent.Length == 0 ? segment : $"{parent}/{segment}";

                FollowRename(ref _folderPath,       shownOld, shownNew);
                FollowRename(ref _outfitFolderPath, shownOld, shownNew);

                _renameFolderTag = string.Empty;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(" Cancel "))
        {
            _renameFolderTag = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();

        // The open folder follows the rename, so the grid does not fall back to the top
        static void FollowRename(ref string path, string shownOld, string shownNew)
        {
            if (path.Equals(shownOld, StringComparison.OrdinalIgnoreCase))
                path = shownNew;
            else if (path.StartsWith(shownOld + "/", StringComparison.OrdinalIgnoreCase))
                path = shownNew + path[shownOld.Length..];
        }
    }

    // ── Filing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Files a card in a folder, replacing any folder it was in. True when anything changed.
    /// </summary>
    /// <remarks>
    /// A move rather than an add, because that is what putting something in a folder means to
    /// anyone who has used one, and the tag underneath being many-to-many is an implementation
    /// detail rather than a promise.
    /// </remarks>
    private static bool FileTags(List<string> tags, string tag)
    {
        // Already exactly there, and nowhere else
        var filed = tags.Where(TagTree.IsFolder).ToList();
        if (filed.Count == 1 && filed[0].Equals(tag, StringComparison.OrdinalIgnoreCase)) return false;

        tags.RemoveAll(TagTree.IsFolder);
        tags.Add(tag);
        return true;
    }

    /// <summary>
    /// Records a folder as a pre-made tag, so emptying it later leaves it standing — and turns
    /// folders on, if they were not.
    /// </summary>
    private void RememberFolder(string tag)
    {
        if (!_config.DefinedTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            _config.DefinedTags.Add(tag);
    }

    /// <summary>
    /// Files items in a folder. Variants go with their original: a group filed with its original
    /// left loose would surface as a stray card at the top, one per colour.
    /// </summary>
    private int FileInFolder(IEnumerable<WardrobeItem> items, string shown)
    {
        shown = NormaliseTag(shown);
        if (shown.Length == 0) return 0;

        var tag     = PageText.FolderPath(shown);
        var changed = WithVariants(items).Count(item => FileTags(item.Tags, tag));

        RememberFolder(tag);
        _config.Save();
        _log.Information($"[Wardrobe] Filed {changed} item(s) in folder '{shown}'");
        return changed;
    }

    /// <summary>Files outfits in a folder.</summary>
    private int FileOutfitsInFolder(IEnumerable<Outfit> outfits, string shown)
    {
        shown = NormaliseTag(shown);
        if (shown.Length == 0) return 0;

        var tag     = PageText.FolderPath(shown);
        var changed = outfits.Count(outfit => FileTags(outfit.Tags, tag));

        RememberFolder(tag);
        _config.Save();
        _log.Information($"[Wardrobe] Filed {changed} outfit(s) in folder '{shown}'");
        return changed;
    }

    /// <summary>Takes items out of whatever folder they are in, back to the top level.</summary>
    private int Unfile(IEnumerable<WardrobeItem> items)
    {
        var changed = WithVariants(items).Count(item => item.Tags.RemoveAll(TagTree.IsFolder) > 0);
        if (changed > 0) _config.Save();
        _log.Information($"[Wardrobe] Took {changed} item(s) out of their folder");
        return changed;
    }

    /// <summary>Takes outfits out of whatever folder they are in, back to the top level.</summary>
    private int UnfileOutfits(IEnumerable<Outfit> outfits)
    {
        var changed = outfits.Count(outfit => outfit.Tags.RemoveAll(TagTree.IsFolder) > 0);
        if (changed > 0) _config.Save();
        _log.Information($"[Wardrobe] Took {changed} outfit(s) out of their folder");
        return changed;
    }

    /// <summary>Empties a folder and everything under it, items and outfits, leaving the folder standing.</summary>
    private void TakeOutOfFolder(string tag)
    {
        bool Under(string t) => t.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                                t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase);

        // Kept as pre-made tags so the folder and its sub-folders survive being emptied — read
        // before the tags come off, since that is where the sub-folders are known from
        foreach (var sub in _config.AllTags().Where(Under).ToList())
            RememberFolder(sub);

        var changed = _config.WardrobeItems.Count(item => item.Tags.RemoveAll(Under) > 0)
                    + _config.Outfits.Count(outfit => outfit.Tags.RemoveAll(Under) > 0);

        _config.Save();
        _log.Information($"[Wardrobe] Emptied folder '{PageText.FolderShown(tag)}': {changed} card(s) unfiled");
    }

    /// <summary>
    /// Moves a folder, sub-folders and all, to sit inside another — or to the top with an empty
    /// parent. Null on success, otherwise why not.
    /// </summary>
    /// <remarks>
    /// A rename of everything above the last segment, so it is the rename's rewrite with a
    /// different target. Refused into itself or anything under it, which would be a path that
    /// contains itself, and onto a name the parent already has, since merging two folders is a
    /// bigger thing than moving one.
    /// </remarks>
    private string? MoveFolder(string shown, string newParent)
    {
        var name    = shown[(shown.LastIndexOf('/') + 1)..];
        var target  = newParent.Length == 0 ? name : $"{newParent}/{name}";

        if (target.Equals(shown, StringComparison.OrdinalIgnoreCase)) return null;

        if (newParent.Equals(shown, StringComparison.OrdinalIgnoreCase) ||
            newParent.StartsWith(shown + "/", StringComparison.OrdinalIgnoreCase))
            return $"'{shown}' cannot go inside itself.";

        var newTag = PageText.FolderPath(target);
        if (_config.AllTags().Any(t => t.Equals(newTag, StringComparison.OrdinalIgnoreCase)))
            return $"'{newParent}' already has a '{name}'.";

        var oldTag  = PageText.FolderPath(shown);
        var touched = RetagPath(oldTag, newTag);

        // Both grids' open folders follow the move, as they follow a rename
        foreach (var outfits in new[] { false, true })
        {
            ref var path = ref FolderPathOf(outfits);
            if (path.Equals(shown, StringComparison.OrdinalIgnoreCase))
                path = target;
            else if (path.StartsWith(shown + "/", StringComparison.OrdinalIgnoreCase))
                path = target + path[shown.Length..];
        }

        _log.Information($"[Wardrobe] Moved folder '{shown}' to '{target}' ({touched} tag(s))");
        return null;
    }

    /// <summary>Empties a folder and forgets it, sub-folders included. Nothing in it is deleted.</summary>
    private void DeleteFolder(string tag)
    {
        TakeOutOfFolder(tag);
        DeleteDefinedTag(tag);

        foreach (var key in _config.TagColours.Keys
                     .Where(k => k.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                                 k.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase))
                     .ToList())
            _config.TagColours.Remove(key);

        _config.Save();
        EnsureFolderPathExists(outfits: false);
        EnsureFolderPathExists(outfits: true);
        _log.Information($"[Wardrobe] Deleted folder '{PageText.FolderShown(tag)}'");
    }

    /// <summary>The items plus every variant of each, once each.</summary>
    private List<WardrobeItem> WithVariants(IEnumerable<WardrobeItem> items)
    {
        var result = new List<WardrobeItem>();
        var seen   = new HashSet<Guid>();

        foreach (var item in items)
        {
            if (seen.Add(item.Id)) result.Add(item);

            foreach (var variant in _config.WardrobeItems)
                if (variant.VariantOfId == item.Id && seen.Add(variant.Id))
                    result.Add(variant);
        }

        return result;
    }

    // ── Filing controls ───────────────────────────────────────────────────────

    private string _newFolderName = string.Empty;

    /// <summary>
    /// The "Put in folder" submenu: every folder there is, and a box for a new one.
    /// </summary>
    /// <remarks>
    /// Shared by a card's right-click menu and the selection panel's popup, on both grids, so one
    /// card and forty are filed the same way. Folders are listed flat with their full shown path
    /// rather than as a tree — a menu is a poor place for a tree, and a wardrobe has a handful of
    /// folders where it has a hundred tags.
    /// </remarks>
    /// <param name="filed">The folders every card in the batch is already in, for ticking those entries.</param>
    /// <param name="file">Files the batch in a folder and returns how many changed.</param>
    private void DrawPutInFolderMenu(string id, bool outfits, IReadOnlyList<string> filed, Func<string, int> file)
    {

        if (!ImGui.BeginMenu($"Put in folder##{id}")) return;
        DrawPutInFolderBody(id, outfits, filed, file);
        ImGui.EndMenu();
    }

    private void DrawPutInFolderBody(string id, bool outfits, IReadOnlyList<string> filed, Func<string, int> file)
    {
        var folders = _config.AllTags()
            .Where(TagTree.IsFolder)
            .Select(PageText.FolderShown)
            .OrderBy(f => f, NaturalOrder.Comparer)
            .ToList();

        // The folder being looked at first: filing into where you are is the common case inside one
        var here = FolderPathOf(outfits);
        if (here.Length > 0 && folders.RemoveAll(f => f.Equals(here, StringComparison.OrdinalIgnoreCase)) > 0)
            folders.Insert(0, here);

        var noun = outfits ? "outfit" : "item";

        foreach (var folder in folders)
        {
            var alreadyAll = filed.Contains(folder, StringComparer.OrdinalIgnoreCase);

            if (ImGui.MenuItem(folder, string.Empty, alreadyAll, !alreadyAll))
                _bulkStatus = $"Filed {file(folder)} {noun}(s) in '{folder}'.";
        }

        if (folders.Count > 0) ImGui.Separator();

        ImGui.SetNextItemWidth(UiScale.S(180));
        var entered = ImGui.InputTextWithHint($"##newfolder_{id}", "new folder, or Parent/Child",
            ref _newFolderName, 64, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var canMake = NormaliseTag(_newFolderName).Length > 0;
        if (!canMake) ImGui.BeginDisabled();
        if (ImGui.Button($" Make ##{id}") || (entered && canMake))
        {
            var shown = NormaliseTag(_newFolderName);
            _bulkStatus    = $"Filed {file(shown)} {noun}(s) in '{shown}'.";
            _newFolderName = string.Empty;
            ImGui.CloseCurrentPopup();
        }
        if (!canMake) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("A slash nests: \"Animations/Idles\" is Idles inside Animations.\n" +
                             (outfits ? "An outfit leaves whatever folder it was in."
                                      : "Variants are filed with their original, and an item leaves\n" +
                                        "whatever folder it was in."));
    }

    /// <summary>The folders every card in a batch shares, for ticking them in the menu.</summary>
    private static List<string> SharedFolders(IEnumerable<List<string>> tagLists)
    {
        List<string>? shared = null;

        foreach (var tags in tagLists)
        {
            var mine = FolderPathsOf(tags).ToList();
            shared = shared == null
                ? mine
                : shared.Where(f => mine.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        return shared ?? new List<string>();
    }

    /// <summary>The card menu's two folder entries, for one outfit.</summary>
    private void DrawOutfitFolderMenu(Outfit outfit)
    {

        var one = new[] { outfit };
        DrawPutInFolderMenu(outfit.Id.ToString(), true, FolderPathsOf(outfit.Tags).ToList(), f => FileOutfitsInFolder(one, f));

        if (outfit.Tags.Any(TagTree.IsFolder) && ImGui.MenuItem("Take out of folder"))
            _bulkStatus = $"Took {UnfileOutfits(one)} outfit(s) out of their folder.";
    }

    /// <summary>
    /// "Add to Folder…" on the select-mode bar, so filing a selection is one click from the grid
    /// rather than a trip into Edit Selected.
    /// </summary>
    /// <remarks>
    /// The same popup the selection panel and the card menu use, so it files the same way — a move,
    /// with variants along. Drawn only with folders on; the bar has no room for a button that can
    /// never do anything.
    /// </remarks>
    private void DrawBulkBarFolderButton(bool outfits, bool none)
    {

        const string label = " Add to Folder… ";
        UiLayout.SameLineIfRoomForButton(label);

        if (none) ImGui.BeginDisabled();
        if (ImGui.Button(label)) ImGui.OpenPopup("##barfolder");
        if (none) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(none
                ? $"Tick some {(outfits ? "outfits" : "items")} first."
                : "Files the selection in a folder you pick, or a new one. Each card\n" +
                  "leaves whatever folder it was in" +
                  (outfits ? "." : ", and variants go with their original.") +
                  "\n\nThe Folders button on the filter row shows the grid by folder.");

        if (!ImGui.BeginPopup("##barfolder")) return;

        if (outfits)
        {
            var chosen = _config.Outfits.Where(o => _selectedOutfits.Contains(o.Id)).ToList();
            DrawPutInFolderBody("bar", true, SharedFolders(chosen.Select(o => o.Tags)),
                                f => FileOutfitsInFolder(chosen, f));
        }
        else
        {
            var items = SelectedItems();
            DrawPutInFolderBody("bar", false, SharedFolders(items.Select(i => i.Tags)),
                                f => FileInFolder(items, f));
        }

        ImGui.EndPopup();
    }

    /// <summary>The selection panel's folder block, for either grid.</summary>
    private void DrawBulkFolderActions(bool outfits)
    {

        var items  = outfits ? new List<WardrobeItem>() : SelectedItems();
        var chosen = outfits ? _config.Outfits.Where(o => _selectedOutfits.Contains(o.Id)).ToList() : new List<Outfit>();
        if ((outfits ? chosen.Count : items.Count) == 0) return;

        var inOne = outfits ? chosen.Any(o => o.Tags.Any(TagTree.IsFolder))
                            : items.Any(i => i.Tags.Any(TagTree.IsFolder));

        ImGui.TextUnformatted("Folder");
        ImGui.TextDisabled(outfits
            ? "Every selected outfit moves into the folder you pick."
            : "Every selected item, and its variants, moves into the folder you pick.");
        ImGui.Spacing();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;

        if (ImGui.Button("Put in folder…", new Vector2(btnW, 0)))
            ImGui.OpenPopup("##bulkfolder");

        ImGui.SameLine();

        if (!inOne) ImGui.BeginDisabled();
        if (ImGui.Button("Take out", new Vector2(btnW, 0)))
            _bulkStatus = outfits
                ? $"Took {UnfileOutfits(chosen)} outfit(s) out of their folder."
                : $"Took {Unfile(items)} item(s) out of their folder.";
        if (!inOne) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(inOne ? "Back to the top level, out of whatever folder each is in."
                                   : "Nothing selected is in a folder.");

        if (ImGui.BeginPopup("##bulkfolder"))
        {
            if (outfits)
                DrawPutInFolderBody("bulk", true, SharedFolders(chosen.Select(o => o.Tags)),
                                    f => FileOutfitsInFolder(chosen, f));
            else
                DrawPutInFolderBody("bulk", false, SharedFolders(items.Select(i => i.Tags)),
                                    f => FileInFolder(items, f));
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }
}
