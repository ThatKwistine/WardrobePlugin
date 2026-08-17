using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// Bulk import: lists every Penumbra mod at once so a whole collection can be brought into the
/// wardrobe in one pass, with supplementary mods attached before anything is written.
/// </summary>
/// <remarks>
/// Deliberately not part of <see cref="ItemImportPanel"/>. Single import is a guided flow — analyse,
/// review each detected slot, name it — which is the right shape for one mod and unusable for two
/// hundred. Here nothing is analysed until it has to be, and items take the same auto-generated
/// names <see cref="ItemImportPanel"/> already produces, on the assumption that anything needing a
/// better name gets one later in edit mode.
/// Its own window rather than the main window's side panel: that panel is 360px wide, which the
/// three-column layout here does not fit into.
/// </remarks>
public class MassImportPanel : Window, IDisposable
{
    private readonly Configuration      _config;
    private readonly PenumbraIpc        _penumbra;
    private readonly ModAnalysisService _analysis;
    private readonly ItemLookupService  _itemLookup;
    private readonly IPluginLog         _log;
    private readonly ItalicFontService  _italicFont;

    private IList<string> _collections   = Array.Empty<string>();
    private int           _collectionIdx = 0;
    private string        _search        = string.Empty;
    private string        _importSummary = string.Empty;

    /// <summary>
    /// Tags written onto every item this import creates.
    /// </summary>
    /// <remarks>
    /// One set for the batch, not one per row. A tag control on each of a few hundred rows would
    /// bury the list, and the tags worth setting here are the ones that are not about the individual
    /// piece anyway — body type, creator, where the batch came from. Anything belonging to one item
    /// is better applied afterwards, by selecting it in the grid.
    /// </remarks>
    private readonly List<string> _batchTags = new();
    private string _batchTagInput = string.Empty;

    /// <summary>
    /// Search inside the supplement picker. Separate from <see cref="_search"/> on purpose: sharing
    /// one field made typing in the picker silently filter the list behind it.
    /// </summary>
    private string _suppSearch = string.Empty;

    /// <summary>
    /// Inner width of the row area, captured once per frame. Column positions must not be derived
    /// from GetContentRegionAvail() mid-row — it shrinks as each widget is placed, so every row
    /// would put its columns somewhere slightly different.
    /// </summary>
    private float _contentW = 800f;

    // Rebuilt once per frame while a search is active, so row visibility is a set lookup rather
    // than a scan of every other row for each row drawn.
    private readonly HashSet<string> _matched          = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _parentsOfMatched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One mod in the list, plus whatever the user has configured for it.</summary>
    private sealed class Row
    {
        public string Dir  = string.Empty;
        public string Name = string.Empty;

        /// <summary>Ticked for import in its own right. Always false while this row is a supplement.</summary>
        public bool Selected;

        /// <summary>
        /// Directory of the mod this row supplements, or null when it is a mod in its own right.
        /// </summary>
        public string? ParentDir;

        /// <summary>
        /// Position in the original alphabetical listing. Kept so un-setting a supplement can put
        /// the row back where it started instead of leaving it stranded under its former parent.
        /// </summary>
        public int BaseIndex;

        /// <summary>Already the primary mod of at least one wardrobe item.</summary>
        public bool AlreadyImported;

        /// <summary>
        /// Referenced by the wardrobe only ever as somebody's supplement, never as a primary mod.
        /// Worth telling apart from an unused mod — it is in use — and from an imported one.
        /// </summary>
        public bool SupportOnly;

        public ModAnalysisResult? Analysis;
        /// <summary>Set once analysis has been attempted, so a mod that fails is not retried every frame.</summary>
        public bool AnalysisTried;

        public Dictionary<string, int>             SingleSel = new();
        public Dictionary<string, HashSet<string>> MultiSel  = new();
    }

    /// <summary>
    /// The list in display order. Reordered in place — and only ever one row at a time — so that
    /// setting a supplement never reshuffles rows the user did not touch.
    /// </summary>
    private readonly List<Row> _rows = new();

    public MassImportPanel(Configuration config, PenumbraIpc penumbra, ModAnalysisService analysis,
        ItemLookupService itemLookup, IPluginLog log, ItalicFontService italicFont)
        : base("Mass Import###WardrobeMassImport")
    {
        // Sized in PreDraw, not here — see the note there
        SizeCondition = ImGuiCond.FirstUseEver;

        _config     = config;
        _penumbra   = penumbra;
        _analysis   = analysis;
        _itemLookup = itemLookup;
        _log        = log;
        _italicFont = italicFont;
    }

    // ── Open / close ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sizes the window every frame, so it tracks Dalamud's Global Font Scale being changed while
    /// the plugin is running rather than freezing at whatever it was when the panel was constructed.
    /// </summary>
    /// <remarks>
    /// FirstUseEver, so assigning every frame lands once and never fights the user's own resize.
    /// </remarks>
    public override void PostDraw() => FontScope.Pop(ref _fontScope);

    /// <summary>Held between <see cref="PreDraw"/> and <see cref="PostDraw"/>. See <see cref="FontScope"/>.</summary>
    private IDisposable? _fontScope;

    /// <summary>Last <see cref="UiScale.Factor"/> seen, to spot the setting being changed.</summary>
    private float _lastScaleFactor;

    public override void PreDraw()
    {
        FontScope.Push(ref _fontScope);

        // A scaled MinimumSize forces this window larger when the setting goes up, and a smaller
        // minimum will not pull it back down again — so the size is re-applied on a change. Back to
        // the scaled default rather than a remembered size: this panel does not track one, and it is
        // opened for a task and closed again rather than left arranged.
        var factor  = UiScale.Factor;
        var rescale = _lastScaleFactor > 0f && MathF.Abs(factor - _lastScaleFactor) > 0.001f;
        _lastScaleFactor = factor;

        // Unscaled: Dalamud scales Size and SizeConstraints itself — see IWindow's docs
        Size          = new Vector2(1100, 700);
        SizeCondition = rescale ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open()
    {
        _rows.Clear();
        _search        = string.Empty;
        _importSummary = string.Empty;
        _batchTags.Clear();
        _batchTagInput = string.Empty;

        _collections = _penumbra.GetCollections();
        _collectionIdx = 0;
        if (!string.IsNullOrEmpty(_config.DefaultCollection))
        {
            var idx = _collections.ToList().FindIndex(
                c => c.Equals(_config.DefaultCollection, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _collectionIdx = idx;
        }

        // Classify every mod in one pass over the wardrobe. Asking per row instead would be a scan
        // of every item for each of a few hundred mods, on every open.
        var primaryDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var supportDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _config.WardrobeItems)
            for (var m = 0; m < item.Mods.Count; m++)
                (m == 0 ? primaryDirs : supportDirs).Add(item.Mods[m].ModDirectory);

        // The same order the single-mod picker uses, so "newest first" means one thing in the plugin
        var mods = _config.ImportListNewestFirst
            ? _penumbra.GetModsByInstalled()
            : _penumbra.GetMods();

        for (var i = 0; i < mods.Count; i++)
        {
            var (dir, name) = mods[i];
            _rows.Add(new Row
            {
                Dir             = dir,
                Name            = name,
                BaseIndex       = i,
                // Nothing is ticked by default. A mass import that pre-selects two hundred mods is
                // one misclick away from filling the wardrobe with items the user never wanted.
                Selected        = false,
                AlreadyImported = primaryDirs.Contains(dir),
                SupportOnly     = !primaryDirs.Contains(dir) && supportDirs.Contains(dir),
            });
        }

        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        _rows.Clear();
    }

    public void Dispose() { }

    // ── Relationships ─────────────────────────────────────────────────────────

    private Row? RowFor(string? dir) =>
        dir == null ? null : _rows.FirstOrDefault(r => r.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<Row> ChildrenOf(Row parent) =>
        _rows.Where(r => r.ParentDir != null &&
                         r.ParentDir.Equals(parent.Dir, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Makes <paramref name="child"/> a supplement of <paramref name="parent"/> and moves its row
    /// directly beneath the parent's existing children.
    /// </summary>
    /// <remarks>
    /// Only the one row moves. Re-sorting the whole list on every assignment would shift rows the
    /// user is part-way through working on, which in a two-hundred-row list means losing your place
    /// every time you configure anything.
    /// </remarks>
    private void SetSupplement(Row child, Row parent)
    {
        // A supplement cannot itself carry supplements, and nothing may parent itself: either would
        // make the relationship a tree the flat import loop below cannot walk.
        if (ReferenceEquals(child, parent)) return;
        if (ChildrenOf(child).Any()) return;

        child.ParentDir = parent.Dir;
        child.Selected  = false; // it is imported as part of the parent now, not on its own

        _rows.Remove(child);
        var insert = _rows.IndexOf(parent) + 1;
        while (insert < _rows.Count &&
               _rows[insert].ParentDir != null &&
               _rows[insert].ParentDir!.Equals(parent.Dir, StringComparison.OrdinalIgnoreCase))
            insert++;
        _rows.Insert(insert, child);
    }

    /// <summary>Detaches a supplement and returns its row to its original alphabetical position.</summary>
    private void ClearSupplement(Row child)
    {
        child.ParentDir = null;
        _rows.Remove(child);

        // Slot back in among the top-level rows by original order. Children travel with their
        // parents, so only top-level rows are candidates for the comparison.
        var insert = _rows.Count;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].ParentDir != null) continue;
            if (_rows[i].BaseIndex > child.BaseIndex) { insert = i; break; }
        }
        _rows.Insert(insert, child);
    }

    // ── Draw ──────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        DrawCollectionCombo();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##massSearch", "Search mods…", ref _search, 256);

        // The same two preferences the single-import picker uses, so these terms mean one thing
        // across the plugin and a wardrobe-wide setting is not shadowed by a window-local copy.
        var hideImported = _config.HideImportedMods;
        if (ImGui.Checkbox("Hide mods I have already imported", ref hideImported))
        {
            _config.HideImportedMods = hideImported;
            _config.Save();
            DeselectHidden();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off, they are still listed but greyed out.\n" +
                             "A mod counts as imported once any item has been made from it, even if " +
                             "some of its slots have not.");

        UiLayout.SameLineIfRoomForText("Hide supplementary mods");
        var hideSupport = _config.HideSupportMods;
        if (ImGui.Checkbox("Hide supplementary mods", ref hideSupport))
        {
            _config.HideSupportMods = hideSupport;
            _config.Save();
            DeselectHidden();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mods only ever attached to an item as a supplement — body upscales,\n" +
                             "compatibility patches — rather than imported in their own right.");

        ImGui.Spacing();

        RebuildMatchCache();

        // Header sits in its own child with the same width and border as the rows below, so both
        // measure the same inner width and the columns line up.
        var headerH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        if (ImGui.BeginChild("##massHeader", new Vector2(-1, headerH), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            DrawHeader();
        ImGui.EndChild();

        // Footer is drawn outside the scroll region so the counts and Import button stay put
        var footerH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing()
                      + ImGui.GetStyle().ItemSpacing.Y * 2;
        if (ImGui.BeginChild("##massRows", new Vector2(-1, -footerH), true))
        {
            _contentW = ImGui.GetContentRegionAvail().X;

            foreach (var row in _rows.ToList()) // ToList: relationship edits mutate _rows mid-loop
            {
                if (!Visible(row)) continue;
                if (row.ParentDir != null) DrawChildRow(row);
                else                       DrawParentRow(row);
            }
        }
        ImGui.EndChild();

        DrawFooter();
    }

    private void DrawCollectionCombo()
    {
        var label = _collections.Count > 0
            ? _collections[Math.Min(_collectionIdx, _collections.Count - 1)]
            : "(no collections)";

        ImGui.TextDisabled("Collection");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##massColl", label))
        {
            for (var i = 0; i < _collections.Count; i++)
            {
                if (ImGui.Selectable(_collections[i], i == _collectionIdx))
                    _collectionIdx = i;
                if (i == _collectionIdx) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.Spacing();
    }

    // Column geometry, shared by the header and every row so they line up.
    // Both combos are a fixed width and sit together on the right, so the name gets the whole of
    // the left and grows with the window instead of running into a combo part-way across the row.
    private static float CheckColW => UiScale.S(30f);
    private static float OptColW   => UiScale.S(190f);
    private static float SuppColW  => UiScale.S(260f);

    private float NameColX  => CheckColW;
    private float SuppColX  => Math.Max(NameColX + UiScale.S(130f) + OptColW, _contentW - SuppColW);
    private float OptColX   => SuppColX - OptColW - UiScale.S(10f);

    /// <summary>Room a row's name has before it would run into the options column.</summary>
    private float NameColW  => Math.Max(60f, OptColX - NameColX - 12f);

    /// <summary>
    /// Draws text clipped to a width, with an ellipsis and the full text on hover when it does not
    /// fit. Anything that fits is drawn whole — the width is a ceiling, not a fixed size.
    /// </summary>
    private static void TextClipped(string text, float maxWidth, Vector4? colour = null,
        string? tooltip = null)
    {
        var shown = text;
        if (ImGui.CalcTextSize(text).X > maxWidth)
        {
            // Longest prefix that still fits once the ellipsis is added. Binary search because
            // CalcTextSize is not free and this runs for every visible row, every frame.
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (ImGui.CalcTextSize(text[..mid] + "…").X <= maxWidth) lo = mid;
                else                                                     hi = mid - 1;
            }
            shown = lo > 0 ? text[..lo] + "…" : "…";
        }

        if (colour.HasValue) ImGui.TextColored(colour.Value, shown);
        else                 ImGui.TextUnformatted(shown);

        // Only worth a tooltip when something was actually hidden, or the caller asked for one
        var clipped = shown != text;
        if ((clipped || tooltip != null) && ImGui.IsItemHovered())
            ImGui.SetTooltip(clipped && tooltip != null ? $"{text}\n\n{tooltip}"
                           : clipped                    ? text
                                                        : tooltip!);
    }

    private void DrawHeader()
    {
        // Select-all only ever acts on what is actually on screen: with a search active, ticking a
        // box labelled "select all" and having it also take the filtered-out rows would be a trap.
        var visibleTop = _rows.Where(r => r.ParentDir == null && Visible(r)).ToList();
        var allOn      = visibleTop.Count > 0 && visibleTop.All(r => r.Selected);

        var toggle = allOn;
        if (ImGui.Checkbox("##massAll", ref toggle))
            foreach (var r in visibleTop)
                r.Selected = toggle;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(allOn ? "Deselect all listed mods" : "Select all listed mods");

        ImGui.SameLine();
        ImGui.SetCursorPosX(NameColX);
        ImGui.TextUnformatted("Mod Name");

        ImGui.SameLine();
        ImGui.SetCursorPosX(OptColX);
        ImGui.TextUnformatted("Mod Options");

        ImGui.SameLine();
        ImGui.SetCursorPosX(SuppColX);
        ImGui.TextUnformatted("Supplemental Mods");

        ImGui.Separator();
    }

    private void RebuildMatchCache()
    {
        _matched.Clear();
        _parentsOfMatched.Clear();
        if (string.IsNullOrWhiteSpace(_search)) return;

        foreach (var row in _rows)
        {
            if (!Matches(row)) continue;
            _matched.Add(row.Dir);
            if (row.ParentDir != null) _parentsOfMatched.Add(row.ParentDir);
        }
    }

    /// <summary>
    /// Whether a row survives the search box. A parent is kept when any of its supplements match and
    /// vice versa, so a filtered list never shows half of a relationship.
    /// </summary>
    private bool Visible(Row row)
    {
        if (!PassesUsageFilter(row)) return false;
        if (string.IsNullOrWhiteSpace(_search)) return true;
        if (_matched.Contains(row.Dir)) return true;
        if (row.ParentDir != null && _matched.Contains(row.ParentDir)) return true;
        return _parentsOfMatched.Contains(row.Dir);
    }

    /// <summary>
    /// Applies the "hide already imported" and "hide supplementary" preferences. A supplement is
    /// shown or hidden with its parent rather than judged on its own, so a configured pair never
    /// appears as half of itself — a stranded "Supplemental mod for X" row whose X is off the list.
    /// </summary>
    private bool PassesUsageFilter(Row row)
    {
        var judged = row.ParentDir != null ? RowFor(row.ParentDir) : row;
        if (judged == null) return true;

        if (_config.HideImportedMods && judged.AlreadyImported) return false;
        if (_config.HideSupportMods  && judged.SupportOnly)     return false;
        return true;
    }

    /// <summary>
    /// Unticks anything a filter has just hidden. A ticked row the user can no longer see to untick
    /// would still be imported.
    /// </summary>
    private void DeselectHidden()
    {
        foreach (var row in _rows)
            if (row.Selected && !Visible(row))
                row.Selected = false;
    }

    private bool Matches(Row row) =>
        row.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        row.Dir.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private void DrawParentRow(Row row)
    {
        ImGui.PushID(row.Dir);

        var sel = row.Selected;
        if (ImGui.Checkbox("##sel", ref sel)) row.Selected = sel;

        // Clipping the name to NameColW means it can no longer overrun, so the row can be drawn
        // left to right in the order it reads. Aligned to frame padding or the text would sit at
        // the top of a row whose height the combos decide.
        ImGui.SameLine();
        ImGui.SetCursorPosX(NameColX);
        ImGui.AlignTextToFramePadding();

        // Both states stay selectable — other slots from an imported mod may still be wanted, and a
        // support mod being somebody's upscale does not stop it being an item in its own right.
        var dim = new Vector4(0.55f, 0.55f, 0.6f, 1f);
        if (row.AlreadyImported)
        {
            TextClipped(row.Name, NameColW, dim,
                "Already imported.\nStill selectable — only slots it has not " +
                "produced an item for yet will be added.");
        }
        else if (row.SupportOnly)
        {
            var italic = _italicFont.Push();
            TextClipped($"{row.Name}   (support mod)", NameColW, dim,
                "Used as a supplementary mod on existing items,\n" +
                "but never imported as an item of its own.");
            if (italic) _italicFont.Pop();
        }
        else
        {
            TextClipped(row.Name, NameColW);
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(OptColX);
        DrawOptionsCombo(row);

        ImGui.SameLine();
        ImGui.SetCursorPosX(SuppColX);
        DrawSupplementCombo(row);

        ImGui.PopID();
    }

    /// <summary>
    /// A mod that has been set as somebody else's supplement: greyed, italic, and with no checkbox,
    /// because it is imported as part of its parent rather than in its own right.
    /// </summary>
    private void DrawChildRow(Row row)
    {
        var parent = RowFor(row.ParentDir);
        if (parent == null) { ClearSupplement(row); return; } // parent vanished; fail safe

        ImGui.PushID(row.Dir);

        // No checkbox in the first column, just the indent it would have occupied
        ImGui.Dummy(new Vector2(CheckColW - ImGui.GetStyle().ItemSpacing.X, ImGui.GetFrameHeight()));

        var grey   = new Vector4(0.55f, 0.55f, 0.6f, 1f);
        var italic = _italicFont.Push();

        ImGui.SameLine();
        ImGui.SetCursorPosX(NameColX);
        ImGui.AlignTextToFramePadding();
        TextClipped($"{row.Name} - Set as supplemental mod for {parent.Name}", NameColW, grey);

        // Leave room for the undo button, which follows on the same line
        ImGui.SameLine();
        ImGui.SetCursorPosX(SuppColX);
        TextClipped($"Supplemental mod for {parent.Name}", SuppColW - 44f, grey);
        if (italic) _italicFont.Pop();

        // Undo has to be reachable from here: with the list left unsorted the parent can be well
        // off-screen, and hunting for it to detach one row is worse than the row moving was.
        ImGui.SameLine();
        if (UiLayout.DeleteButton("x", $"Stop being a supplemental mod for {parent.Name}."))
            ClearSupplement(row);

        ImGui.PopID();
    }

    private void DrawOptionsCombo(Row row)
    {
        ImGui.SetNextItemWidth(OptColW);
        if (ImGui.BeginCombo("##opts", "Set Mod Options", ImGuiComboFlags.HeightLarge))
        {
            // Deferred to first open: analysis reads the mod off disk, and doing that for every mod
            // in the list up front would stall the game for seconds on a large Penumbra folder.
            EnsureAnalysis(row);

            DrawOptionGroups(row, row.Name);

            // The supplements' own options live here too, matching single import, where an extra
            // mod's groups are configured from the item being imported rather than on their own.
            foreach (var child in ChildrenOf(row).ToList())
            {
                EnsureAnalysis(child);
                ImGui.Separator();
                DrawOptionGroups(child, $"{child.Name}  (supplemental)");
            }

            ImGui.EndCombo();
        }
    }

    private void DrawOptionGroups(Row row, string heading)
    {
        ImGui.TextDisabled(heading);

        if (row.Analysis == null)
        {
            ImGui.TextDisabled("  Could not read this mod's folder.");
            return;
        }
        if (row.Analysis.OptionGroups.Count == 0)
        {
            ImGui.TextDisabled("  No options.");
            return;
        }

        foreach (var group in row.Analysis.OptionGroups)
            ModOptionPicker.Draw(group, row.SingleSel, row.MultiSel);
    }

    private void DrawSupplementCombo(Row row)
    {
        var children = ChildrenOf(row).ToList();
        var preview = children.Count switch
        {
            0 => "+ Add Supplementary Mod",
            1 => $"Configured - {children[0].Name}",
            _ => $"Configured - {children.Count} mods",
        };

        ImGui.SetNextItemWidth(SuppColW - 16f);
        if (!ImGui.BeginCombo("##supp", preview, ImGuiComboFlags.HeightLarge)) return;

        ImGui.TextDisabled("Supplementary mods for");
        ImGui.TextUnformatted(row.Name);
        ImGui.Separator();

        if (ImGui.IsWindowAppearing())
        {
            _suppSearch = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##suppSearch", "Search…", ref _suppSearch, 256);

        // Cheaper than calling ChildrenOf per candidate, which would rescan the whole list each time
        var parents = new HashSet<string>(
            _rows.Where(r => r.ParentDir != null).Select(r => r.ParentDir!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var cand in _rows.ToList())
        {
            if (ReferenceEquals(cand, row)) continue;
            // A mod carrying supplements of its own cannot also be one: the import loop treats the
            // relationship as exactly one level deep.
            if (parents.Contains(cand.Dir)) continue;
            // Already spoken for by a different parent
            if (cand.ParentDir != null &&
                !cand.ParentDir.Equals(row.Dir, StringComparison.OrdinalIgnoreCase)) continue;

            if (!string.IsNullOrWhiteSpace(_suppSearch) &&
                !cand.Name.Contains(_suppSearch, StringComparison.OrdinalIgnoreCase) &&
                !cand.Dir.Contains(_suppSearch, StringComparison.OrdinalIgnoreCase)) continue;

            var attached = cand.ParentDir != null;
            var was      = attached;
            if (ImGui.Checkbox($"{cand.Name}##supp_{cand.Dir}", ref attached) && attached != was)
            {
                if (attached) SetSupplement(cand, row);
                else          ClearSupplement(cand);
            }
        }

        ImGui.EndCombo();
    }

    private void DrawFooter()
    {
        var selected = _rows.Count(r => r.ParentDir == null && r.Selected);
        // Only supplements hanging off a mod that is actually being imported will be written
        var supps    = _rows.Count(r => r.ParentDir != null && RowFor(r.ParentDir) is { Selected: true });

        ImGui.Separator();
        ImGui.TextUnformatted($"{selected} mod(s) selected to import");
        UiLayout.SameLineIfRoomForText($" - {supps} mod(s) selected as Supplemental Mods to import");
        ImGui.TextDisabled($" - {supps} mod(s) selected as Supplemental Mods to import");

        if (!string.IsNullOrEmpty(_importSummary))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.6f, 1f), _importSummary);
        }

        var canImport = selected > 0;
        if (!canImport) ImGui.BeginDisabled();
        if (ImGui.Button("Import Mods", UiScale.S(140, 0)))
            DoImport();
        if (!canImport) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Close", UiScale.S(120, 0)))
            Close();

        // On the button row rather than above it. The footer's height is reserved by Draw so the row
        // list can fill what is left, and anything that adds a row here pushes these buttons off the
        // bottom of the window — a popup costs no height at all, however much is inside it.
        ImGui.SameLine();
        var label = _batchTags.Count > 0 ? $"Tags ({_batchTags.Count})" : "Tags";
        if (ImGui.Button($"{label}##batchTagsBtn", UiScale.S(120, 0)))
            ImGui.OpenPopup(BatchTagsPopup);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tags added to every item this import creates.");

        DrawBatchTagsPopup();
    }

    private const string BatchTagsPopup = "##batchTagsPopup";

    /// <summary>
    /// Tags applied to everything this import creates, in a popup off the footer.
    /// </summary>
    /// <remarks>
    /// A popup rather than an inline section because the footer's height is reserved in advance by
    /// <see cref="Draw"/> so the row list can take the rest — anything that adds height here pushes
    /// the Import button off the bottom of the window. The count on the button keeps a tag set and
    /// then dismissed visible without opening it again.
    /// </remarks>
    private void DrawBatchTagsPopup()
    {
        if (!ImGui.BeginPopup(BatchTagsPopup)) return;

        ImGui.TextDisabled("Added to every item this import creates. Best for what is true of\n" +
                           "the whole batch — a body type, a creator — rather than any one piece.");
        ImGui.Spacing();

        var removeIdx = -1;
        for (var i = 0; i < _batchTags.Count; i++)
        {
            if (i > 0) UiLayout.SameLineIfRoom(ImGui.CalcTextSize(_batchTags[i]).X + UiScale.S(40f));

            ImGui.PushID($"btag_{i}");
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.35f, 0.2f, 0.55f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.45f, 0.28f, 0.68f, 1f));
            ImGui.SmallButton(_batchTags[i]);
            ImGui.PopStyleColor(2);
            ImGui.SameLine();
            if (UiLayout.DeleteButton("×", $"Do not put '{_batchTags[i]}' on this batch.")) removeIdx = i;
            ImGui.PopID();
        }
        if (removeIdx >= 0) _batchTags.RemoveAt(removeIdx);

        ImGui.SetNextItemWidth(UiScale.S(260f));
        var entered = ImGui.InputTextWithHint("##batchTag", "tag name", ref _batchTagInput, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        UiLayout.SameLineIfRoomForButton("Add");
        if (ImGui.Button("Add") || entered) AddBatchTag(_batchTagInput);

        // Nested rather than an early return: bailing out here would skip EndPopup and leave ImGui's
        // stack unbalanced, which crashes inside cimgui a frame or two later, far from the cause
        if (_config.AllTags().Count > 0)
        {
            ImGui.Spacing();
            var height = ImGui.GetTextLineHeightWithSpacing() * 8;
            if (ImGui.BeginChild("##batchTagTree", new Vector2(UiScale.S(320f), height), true))
                // Styles included, as in the single-item import: a batch brought in together is
                // usually of a piece, and styling it here saves doing it item by item afterwards
                TagTree.DrawPicker(TagTree.Build(_config, includeStyles: true), "batchpick",
                    path => _batchTags.Contains(path, StringComparer.OrdinalIgnoreCase),
                    AddBatchTag);
            ImGui.EndChild();
        }

        ImGui.EndPopup();
    }

    private void AddBatchTag(string raw)
    {
        var tag = raw.Trim();
        if (tag.Length == 0) return;
        if (_batchTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;

        _batchTags.Add(tag);
        _batchTagInput = string.Empty;
    }

    // ── Import ────────────────────────────────────────────────────────────────

    private ModAnalysisResult? EnsureAnalysis(Row row)
    {
        if (row.Analysis != null || row.AnalysisTried) return row.Analysis;
        row.AnalysisTried = true;

        var path = _penumbra.GetModFolderPath(row.Dir);
        if (path == null)
        {
            _log.Warning($"[Wardrobe] Mass import: no folder for mod '{row.Name}' ({row.Dir})");
            return null;
        }

        try { row.Analysis = _analysis.Analyze(path); }
        catch (Exception ex) { _log.Warning(ex, $"[Wardrobe] Mass import: analysis failed for '{row.Name}'"); }

        // Start from whatever Penumbra already has active for this mod rather than from each
        // group's first option. Seeded here, against the collection selected at the time — changing
        // the collection afterwards does not re-seed, because that would throw away any option the
        // user had already set by hand.
        if (row.Analysis != null)
            ModAnalysisService.SeedSelectionsFromPenumbra(
                row.Analysis.OptionGroups,
                _penumbra.GetModSettingsFull(SelectedCollection(), row.Dir, row.Name),
                row.SingleSel, row.MultiSel);

        return row.Analysis;
    }

    private string SelectedCollection() =>
        _collections.Count > 0
            ? _collections[Math.Min(_collectionIdx, _collections.Count - 1)]
            : string.Empty;

    private List<EquipSlot> ImportedSlots(string modDir) =>
        _config.WardrobeItems
            .Where(i => i.Mods.Count > 0 &&
                        i.Mods[0].ModDirectory.Equals(modDir, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Slot)
            .Distinct()
            .ToList();

    private void DoImport()
    {
        var collection = SelectedCollection();

        var created = 0;
        var skipped = 0;

        foreach (var row in _rows.Where(r => r.ParentDir == null && r.Selected).ToList())
        {
            var analysis = EnsureAnalysis(row);
            if (analysis == null) { skipped++; continue; }

            var children = ChildrenOf(row).ToList();
            foreach (var child in children) EnsureAnalysis(child);

            var slots = BuildSlots(row, children, out var setIds, out var replaces);
            if (slots.Count == 0) { skipped++; continue; }

            var extraRefs = children
                .Where(c => c.Analysis != null)
                .Select(c => new ModReference
                {
                    Label        = "Supplementary Mod",
                    Collection   = collection,
                    ModDirectory = c.Dir,
                    ModName      = c.Name,
                    Options      = BuildOptions(c.Analysis!.OptionGroups, c.SingleSel),
                    MultiOptions = BuildMultiOptions(c.Analysis!.OptionGroups, c.MultiSel),
                })
                .ToList();

            var primaryOptions = BuildOptions(analysis.OptionGroups, row.SingleSel);
            var primaryMulti   = BuildMultiOptions(analysis.OptionGroups, row.MultiSel);

            foreach (var slot in slots)
            {
                // Same naming rule as single import: qualify by slot only when the mod produced
                // more than one, so the common one-slot case reads as just the mod's name.
                var name = slots.Count == 1 ? row.Name : $"{row.Name} ({slot.DisplayName()})";

                ulong?  glamId    = null;
                string? glamName  = null;
                ushort? slotSetId = null;
                if (setIds.TryGetValue(slot, out var setId))
                {
                    slotSetId = setId;
                    var found = _itemLookup.FindBestItem(setId, slot);
                    if (found.HasValue)
                    {
                        glamId   = found.Value.ItemId;
                        glamName = found.Value.ItemName;
                    }
                }

                var item = new WardrobeItem
                {
                    Name              = name,
                    Slot              = slot,
                    Replaces          = slot.IsModCategory() ? replaces.GetValueOrDefault(slot) : null,
                    GlamourerItemId   = glamId,
                    GlamourerItemName = glamName,
                    ModelSetId        = slotSetId,
                    HairIdByRace      = slot == EquipSlot.Hair
                        ? analysis.HairIdsByRace.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
                        : new Dictionary<string, ushort>(),

                    // Copied per item, not shared — one list across hundreds of items would have
                    // every one of them re-tagged when any single item was edited later
                    Tags              = new List<string>(_batchTags),
                };

                item.Mods.Add(new ModReference
                {
                    Label        = "Main Mod",
                    Collection   = collection,
                    ModDirectory = row.Dir,
                    ModName      = row.Name,
                    // Per slot, and a fresh dictionary per item: a mod covering body and legs makes
                    // two items, and each asserting the other's groups is what sets them fighting
                    // once either has a variant (#12)
                    Options      = ModOptionSets.ForSlot(primaryOptions, analysis.OptionGroups, slot),
                    MultiOptions = ModOptionSets.ForSlot(primaryMulti,   analysis.OptionGroups, slot),
                });
                item.Mods.AddRange(extraRefs);

                _config.WardrobeItems.Add(item);
                created++;
            }
        }

        _config.Save();
        _log.Information($"[Wardrobe] Mass import: created {created} item(s), skipped {skipped} mod(s)");

        _importSummary = skipped > 0
            ? $"Imported {created} item(s), skipped {skipped}."
            : $"Imported {created} item(s).";

        // Rows stay put rather than closing the window: after a big import the user usually wants to
        // see what landed and carry on with the rest of the list. Reclassify as we go, or the two
        // usage filters would keep judging every row on how things stood before this import.
        foreach (var row in _rows.Where(r => r.ParentDir == null && r.Selected).ToList())
        {
            row.AlreadyImported = true;
            row.SupportOnly     = false; // it has a primary reference now

            foreach (var child in ChildrenOf(row))
                if (!child.AlreadyImported) child.SupportOnly = true;
        }

        foreach (var row in _rows)
            row.Selected = false;
    }

    /// <summary>
    /// Merges the slots a mod covers with those its supplements add, the same way single import does
    /// — an upscale that adds legs to a body mod contributes a legs item the primary alone would
    /// never have produced.
    /// </summary>
    private List<EquipSlot> BuildSlots(Row row, List<Row> children,
        out Dictionary<EquipSlot, ushort> setIds, out Dictionary<EquipSlot, string> replaces)
    {
        setIds   = new Dictionary<EquipSlot, ushort>();
        replaces = new Dictionary<EquipSlot, string>();

        var slots = new HashSet<EquipSlot>(row.Analysis!.DetectedSlots);
        foreach (var (slot, id) in row.Analysis.SlotSetIds) setIds.TryAdd(slot, id);
        foreach (var (slot, key) in row.Analysis.ReplaceKeys) replaces.TryAdd(slot, key);

        foreach (var child in children)
        {
            if (child.Analysis == null) continue;
            foreach (var slot in child.Analysis.DetectedSlots) slots.Add(slot);
            // TryAdd throughout: the primary is the mod the item is really "about", so it wins
            // wherever both it and a supplement describe the same slot.
            foreach (var (slot, id) in child.Analysis.SlotSetIds) setIds.TryAdd(slot, id);
            foreach (var (slot, key) in child.Analysis.ReplaceKeys) replaces.TryAdd(slot, key);
        }

        if (!_config.ModCategoriesEnabled)
            slots.RemoveWhere(s => s.IsModCategory());

        // Don't re-create items that already exist for this mod and slot
        var already = ImportedSlots(row.Dir);
        slots.RemoveWhere(already.Contains);

        return slots.OrderBy(s => (int)s).ToList();
    }

    private static Dictionary<string, string> BuildOptions(
        IReadOnlyList<ModOptionGroup>? groups, Dictionary<string, int> selections)
    {
        var result = new Dictionary<string, string>();
        if (groups == null) return result;
        foreach (var g in groups.Where(g => g.GroupType == ModGroupType.Single))
        {
            // Leave alone is stored by leaving the group out, so nothing writes it on wear
            if (!selections.TryGetValue(g.GroupName, out var i) ||
                i == ModOptionPicker.Ignore || g.OptionNames.Count == 0) continue;

            result[g.GroupName] = i >= 0 && i < g.OptionNames.Count ? g.OptionNames[i] : g.OptionNames[0];
        }
        return result;
    }

    private static Dictionary<string, List<string>> BuildMultiOptions(
        IReadOnlyList<ModOptionGroup>? groups, Dictionary<string, HashSet<string>> selections)
    {
        var result = new Dictionary<string, List<string>>();
        if (groups == null) return result;
        foreach (var g in groups.Where(g => g.GroupType != ModGroupType.Single))
            if (selections.TryGetValue(g.GroupName, out var sel) && sel.Count > 0)
                result[g.GroupName] = sel.ToList();
        return result;
    }
}
