using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

public class ItemImportPanel : IDisposable
{
    public bool IsOpen { get; private set; }

    private readonly Configuration           _config;
    private readonly WardrobeService         _wardrobe;
    private readonly PenumbraIpc             _penumbra;
    private readonly GlamourerIpc            _glamourer;
    private readonly ModAnalysisService      _analysis;
    private readonly ItemLookupService       _itemLookup;
    private readonly ScreenshotSessionService _session;
    private readonly IPluginLog              _log;
    private readonly ItalicFontService       _italicFont;

    // ── Shared (edit mode) ────────────────────────────────────────────────────
    private WardrobeItem? _editTarget;
    private string        _editName     = string.Empty;
    private string        _editImage    = string.Empty;
    private int           _editSlotIdx  = 0;
    private List<string>  _editTags     = new();
    private string        _editTagInput = string.Empty;
    private string        _editReplaces = string.Empty;
    private string        _editNotes    = string.Empty;

    /// <summary>
    /// Slots offered by the slot combos, captured when the panel opens. Snapshotted rather than
    /// rebuilt per frame so the combo index stays pointing at the same slot for the whole edit,
    /// even if the mod-categories setting is toggled in another panel meanwhile.
    /// </summary>
    private EquipSlot[] _slotChoices = EquipSlotEx.All;

    /// <summary>The slot the combo currently has selected, guarded against a stale index.</summary>
    private EquipSlot SelectedSlot(int idx) =>
        _slotChoices.Length == 0 ? EquipSlot.Unknown : _slotChoices[Math.Clamp(idx, 0, _slotChoices.Length - 1)];

    // ── Import mode: pickers ─────────────────────────────────────────────────
    private IList<string>                    _collections   = Array.Empty<string>();
    private IList<(string Dir, string Name)> _mods          = Array.Empty<(string, string)>();
    private int  _collectionIdx = 0;
    private int  _modIdx        = 0;
    /// <summary>
    /// True once a main mod has actually been chosen. Until then _modIdx is just its default of 0,
    /// which is a real mod — so nothing may analyse or import from it.
    /// </summary>
    private bool _modPicked;
    private string _modSearch   = string.Empty;

    // ── Import mode: analysis results ─────────────────────────────────────────
    private ModAnalysisResult?      _analysisResult;
    private bool                    _analyzed       = false;
    private string                  _analyzeError   = string.Empty;

    // ── Edit mode: per-mod option editing ─────────────────────────────────────
    private class EditModOptions
    {
        public string?                             ResolvedPath;
        public bool                                PathExists;
        public ModAnalysisResult?                  Analysis;
        public Dictionary<string, int>             SingleSel = new();
        public Dictionary<string, HashSet<string>> MultiSel  = new();
    }
    private readonly List<EditModOptions> _editModOptions = new();

    // Staged per-mod collection names, index-aligned with _editTarget.Mods.
    // Held separately so Cancel discards the change like every other edit-mode field.
    private readonly List<string> _editModCollections = new();

    /// <summary>
    /// Indices into _editTarget.Mods staged for removal on save. Never contains 0 — the primary mod
    /// is what the item *is*, so detaching it would leave an item that points at nothing.
    /// Staged rather than applied immediately so Cancel discards it like every other edit.
    /// </summary>
    private readonly HashSet<int> _editModRemovals = new();

    // Manual game-item search in edit mode
    private string _itemSearch = string.Empty;

    // Single-select group → selected option index
    private readonly Dictionary<string, int> _groupSelections = new();
    // Multi-select (checkbox) group → set of checked option names
    private readonly Dictionary<string, HashSet<string>> _multiGroupSelections = new();

    // One config row per detected slot
    private readonly List<SlotConfig> _slotConfigs = new();

    // Set when a supplementary mod is added, removed or swapped; consumed after the extras
    // are drawn so the slot list can be rebuilt without mutating it mid-iteration.
    private bool _extrasDirty;

    // Fallback when no slots detected (user picks manually)
    private int    _manualSlotIdx  = 0;
    private string _manualName     = string.Empty;
    private string _manualImage    = string.Empty;

    // Additional mods beyond the primary
    private readonly List<ExtraMod> _extraMods = new();

    private class SlotConfig
    {
        public EquipSlot Slot;
        public bool      Include          = true;
        /// <summary>This mod already has a wardrobe item for this slot.</summary>
        public bool      AlreadyImported;
        /// <summary>Model set ID for this slot, used to offer items sharing the same model.</summary>
        public ushort?   SetId;
        /// <summary>Supplementary mod that contributed this slot, or null when the primary mod covers it.</summary>
        public string?   SourceMod;
        /// <summary>What the mod replaces within its category, for mod-category slots only.</summary>
        public string?   Replaces;
        public string    Name             = string.Empty;
        public string    Image            = string.Empty;
        public ulong?    GlamourerItemId;
        public string?   GlamourerItemName;
    }

    private class ExtraMod
    {
        public string              Label               = "Supplementary Mod";
        public int                 CollectionIdx       = 0;
        public int                 ModIdx              = 0;
        /// <summary>
        /// True once the user has actually chosen a mod. Until then ModIdx is just its default of
        /// 0, which is a real mod — so nothing may analyse or import from this row yet.
        /// </summary>
        public bool                ModPicked;
        public string              ModSearch           = string.Empty;
        public ModAnalysisResult?  Analysis;
        public Dictionary<string, int>             GroupSelections      = new();
        public Dictionary<string, HashSet<string>> MultiGroupSelections = new();
    }

    public ItemImportPanel(Configuration config, WardrobeService wardrobe,
        PenumbraIpc penumbra, GlamourerIpc glamourer, ModAnalysisService analysis,
        ItemLookupService itemLookup, ScreenshotSessionService session, IPluginLog log,
        ItalicFontService italicFont)
    {
        _italicFont = italicFont;
        _config     = config;
        _wardrobe   = wardrobe;
        _penumbra   = penumbra;
        _glamourer  = glamourer;
        _analysis   = analysis;
        _itemLookup = itemLookup;
        _session    = session;
        _log        = log;
    }

    // ── Open / close ──────────────────────────────────────────────────────────

    public void OpenImport()
    {
        ResetImport();
        _editTarget  = null;
        _slotChoices = EquipSlotEx.Choices(_config.ModCategoriesEnabled);
        _collections = _penumbra.GetCollections();
        _mods        = _penumbra.GetMods();

        // Pre-select the configured default collection. ResetImport() leaves _collectionIdx at 0,
        // which is whichever collection sorts first and is rarely the one the character uses.
        if (!string.IsNullOrEmpty(_config.DefaultCollection))
        {
            var idx = _collections.ToList().FindIndex(
                c => c.Equals(_config.DefaultCollection, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _collectionIdx = idx;
        }

        IsOpen = true;
    }

    public void OpenEdit(WardrobeItem item)
    {
        ResetImport();
        _editTarget   = item;
        _editName     = item.Name;
        _editImage    = item.ImagePath ?? string.Empty;

        // Ensure the item's own slot is in the list even when its category is switched off, so the
        // combo has something valid selected and saving cannot write a different slot back
        _slotChoices  = EquipSlotEx.Choices(_config.ModCategoriesEnabled, item.Slot);
        _editSlotIdx  = Array.IndexOf(_slotChoices, item.Slot);
        if (_editSlotIdx < 0) _editSlotIdx = 0;

        _editTags     = new List<string>(item.Tags);
        _editTagInput = string.Empty;
        _editReplaces = item.Replaces ?? string.Empty;
        _editNotes    = item.Notes ?? string.Empty;

        // Needed for the per-mod collection pickers, and for the supplementary-mod picker below;
        // edit mode may be opened without import mode ever having run, so neither list is
        // guaranteed to be loaded yet.
        _collections = _penumbra.GetCollections();
        _mods        = _penumbra.GetMods();
        _editModCollections.Clear();
        foreach (var mod in item.Mods)
            _editModCollections.Add(mod.Collection);

        // _editModOptions is populated lazily when the Mod Options section is first expanded
        IsOpen = true;
    }

    // Called the first time the Mod Options section is opened, and when the user clicks Reload.
    private void LoadEditModOptions()
    {
        _editModOptions.Clear();
        if (_editTarget == null) return;

        // Build a live mod-name → folder-path lookup for the fallback
        var liveMods = _penumbra.GetMods()
            .GroupBy(m => m.ModName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ModDirectory, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in _editTarget.Mods)
        {
            var entry = new EditModOptions();

            // Primary: use stored ModDirectory
            var path = _penumbra.GetModFolderPath(mod.ModDirectory);
            if (path == null || !Directory.Exists(path))
            {
                // Fallback: find the mod by name in the live list
                if (!string.IsNullOrEmpty(mod.ModName) &&
                    liveMods.TryGetValue(mod.ModName, out var liveDir))
                    path = _penumbra.GetModFolderPath(liveDir);
            }

            entry.ResolvedPath = path;
            entry.PathExists   = path != null && Directory.Exists(path);

            if (entry.PathExists)
            {
                entry.Analysis = _analysis.Analyze(path!);
                foreach (var g in entry.Analysis.OptionGroups)
                {
                    if (g.GroupType == ModGroupType.Multi)
                    {
                        var sel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (mod.MultiOptions.TryGetValue(g.GroupName, out var stored))
                            foreach (var s in stored) sel.Add(s);
                        entry.MultiSel[g.GroupName] = sel;
                    }
                    else
                    {
                        var idx = 0;
                        // Prefer stored config value, then live Penumbra state, then index 0.
                        // Using live Penumbra state as fallback prevents options mismatch
                        // from breaking detection when groups were never explicitly stored.
                        string? stored = null;
                        if (mod.Options.TryGetValue(g.GroupName, out var fromConfig))
                            stored = fromConfig;
                        else
                        {
                            var live = _penumbra.GetModSettings(mod.Collection, mod.ModDirectory, mod.ModName);
                            if (live.TryGetValue(g.GroupName, out var fromPenumbra))
                                stored = fromPenumbra;
                        }
                        if (stored != null)
                            for (var i = 0; i < g.OptionNames.Count; i++)
                                if (g.OptionNames[i].Equals(stored, StringComparison.OrdinalIgnoreCase))
                                { idx = i; break; }
                        entry.SingleSel[g.GroupName] = idx;
                    }
                }
            }
            _editModOptions.Add(entry);
        }
    }

    public void Close()
    {
        IsOpen = false;
        ResetImport();
        _editTarget = null;
    }

    // ── Draw ──────────────────────────────────────────────────────────────────

    public void Draw()
    {
        if (_editTarget != null) DrawEditMode();
        else                     DrawImportMode();
    }

    // ── Edit mode ─────────────────────────────────────────────────────────────

    private void DrawEditMode()
    {
        ImGui.TextUnformatted("Edit Item");
        ImGui.Separator();
        ImGui.Spacing();

        // Image preview
        var avail = ImGui.GetContentRegionAvail().X;
        if (!string.IsNullOrEmpty(_editImage) && System.IO.File.Exists(_editImage))
        {
            try
            {
                var wrap = Plugin.Textures.GetFromFile(_editImage).GetWrapOrDefault();
                if (wrap != null)
                {
                    ImGui.Image(wrap.Handle, new Vector2(avail, avail));
                    ImGui.Spacing();
                }
            }
            catch { }
        }

        ImGui.TextDisabled("Name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ename", ref _editName, 128);

        ImGui.Spacing();
        ImGui.TextDisabled("Image path");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##eimage", ref _editImage, 512);

        ImGui.Spacing();
        ImGui.TextDisabled("Slot");
        ImGui.SetNextItemWidth(-1);
        var slotNames = _slotChoices.Select(s => s.DisplayName()).ToArray();
        ImGui.Combo("##eslot", ref _editSlotIdx, slotNames, slotNames.Length);

        // Mod categories are not exclusive per slot, so what the item displaces is its own field
        if (SelectedSlot(_editSlotIdx).IsModCategory())
            DrawReplacesEditor(SelectedSlot(_editSlotIdx));

        ImGui.Spacing();
        ImGui.Separator();

        if (_editTarget?.GlamourerItemName != null)
        {
            ImGui.TextDisabled("Game item");
            ImGui.TextUnformatted(_editTarget.GlamourerItemName);
        }
        else if (_editTarget != null && _editTarget.Slot.IsModCategory())
        {
            ImGui.TextDisabled($"{_editTarget.Slot.DisplayName()} mods have no game item — " +
                               "enabling the Penumbra mod is the whole effect.");
        }
        else if (_editTarget != null && _editTarget.Slot.IsCustomization())
        {
            ImGui.TextDisabled($"{_editTarget.Slot.DisplayName()} mods replace the character " +
                               "model, so there is no item to equip.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                "No game item detected — Glamourer won't apply on Wear.");
        }

        // On its own line rather than beside the text above: that text wraps to the panel width,
        // so a button after it would sit off the side
        ImGui.Spacing();
        if (ImGui.SmallButton("Re-detect"))
            TryRedetectItem(_editTarget!);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-reads the mod's files to work out which FFXIV item it replaces.");

        // Override the auto-detected item when several share the same model
        if (_editTarget!.ModelSetId is { } editSetId)
        {
            if (DrawGameItemPicker("edititem", editSetId, _editTarget.Slot,
                    _editTarget.GlamourerItemId, out var pickedId, out var pickedName))
            {
                _editTarget.GlamourerItemId   = pickedId;
                _editTarget.GlamourerItemName = pickedName;
                _config.Save();
            }
        }
        else if (_editTarget.Mods.Count > 0 && !_editTarget.Slot.IsModCategory())
        {
            ImGui.TextDisabled("Click Re-detect to list other items sharing this model.");
        }

        if (!_editTarget.Slot.IsModOnly())
            DrawManualItemPicker(_editTarget);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Mods & Collections");
        ImGui.TextDisabled("A mod only takes effect in the collection your character uses.");
        ImGui.Spacing();
        DrawEditModCollections();

        ImGui.Spacing();
        DrawEditExtraMods();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Tags");
        DrawTagEditor();

        ImGui.Spacing();
        ImGui.Separator();
        DrawNotesEditor();

        ImGui.Spacing();
        ImGui.Separator();

        if (_session.FoldersReady)
        {
            if (ImGui.Button("Take Screenshot", new Vector2(-1, 0)))
            {
                // Save current edits first so the item is up to date
                _editTarget!.Name     = _editName.Trim();
                _editTarget.Slot      = SelectedSlot(_editSlotIdx);
                _editTarget.Replaces  = EditedReplaces();
                _editTarget.Notes     = EditedNotes();
                _editTarget.Tags      = new List<string>(_editTags);
                _config.Save();
                _session.StartSingle(_editTarget);
                Close();
                return; // Close() nulls _editTarget — nothing below may run this frame
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Wears this item and waits for a screenshot.\nThe result is cropped to 1:1 and saved as its image.");
        }

        // Any handler above may have called Close(), which nulls _editTarget. Bail rather than
        // dereference it — the panel is already closing and nothing below would be drawn anyway.
        if (_editTarget == null) return;

        // Collapsible mod options editor
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Mod Options"))
        {
            // Lazy load: run analysis the first time the section is opened
            if (_editModOptions.Count == 0 && _editTarget!.Mods.Count > 0)
                LoadEditModOptions();

            if (_editTarget!.Mods.Count == 0)
            {
                ImGui.TextDisabled("This item has no linked mods.");
            }
            else
            {
                for (var i = 0; i < _editTarget.Mods.Count && i < _editModOptions.Count; i++)
                {
                    var mod  = _editTarget.Mods[i];
                    var opts = _editModOptions[i];
                    ImGui.TextUnformatted(mod.Label);
                    UiLayout.SameLineIfRoomForText($"({mod.ModName})");
                    ImGui.TextDisabled($"({mod.ModName})");
                    if (opts.ResolvedPath == null)
                        ImGui.TextDisabled("  Could not resolve mod path (Penumbra IPC failed).");
                    else if (!opts.PathExists)
                    {
                        ImGui.TextDisabled("  Mod folder not found:");
                        ImGui.TextDisabled($"  {opts.ResolvedPath}");
                    }
                    else if (opts.Analysis == null || opts.Analysis.OptionGroups.Count == 0)
                    {
                        ImGui.TextDisabled("  No configurable options in this mod.");
                        ImGui.TextDisabled($"  ({opts.ResolvedPath})");
                    }
                    else
                        foreach (var g in opts.Analysis.OptionGroups)
                            DrawGroupPicker(g, opts.SingleSel, opts.MultiSel);
                    ImGui.Spacing();
                }
            }

            if (ImGui.SmallButton("Reload Options"))
                LoadEditModOptions();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Variants");
        ImGui.TextDisabled("A copy with the same mods but different options — another colour " +
                           "or style. It becomes an item of its own.");
        ImGui.Spacing();

        if (ImGui.Button("Create variant of this item", new Vector2(-1, 0)))
            CreateVariant(_editTarget!);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Saves this item, then opens a copy with the same mods, collections\n" +
                             "and options already filled in. Change its options and image from there.\n\n" +
                             "The original is left exactly as it is.");

        // Save / Cancel at bottom
        ImGui.Spacing();
        ImGui.Separator();
        var footerBtnW = (ImGui.GetContentRegionAvail().X - 8) / 2;
        if (ImGui.Button("Save", new Vector2(footerBtnW, 0)))
        {
            // Captured before the edits land: changing the slot, or what an animation replaces, changes
            // the key the item is tracked under. Without moving the entry across, the old key would
            // keep pointing at this item and it would read as both worn and not worn at once.
            var wasWorn    = _wardrobe.IsItemWorn(_editTarget!);
            var oldWornKey = _editTarget!.WornKey();

            _editTarget.Name       = _editName.Trim();
            _editTarget.ImagePath  = string.IsNullOrEmpty(_editImage) ? null : _editImage.Trim();
            _editTarget.Slot       = SelectedSlot(_editSlotIdx);
            _editTarget.Replaces   = EditedReplaces();
            _editTarget.Notes      = EditedNotes();
            _editTarget.Tags       = new List<string>(_editTags);

            if (wasWorn && _editTarget.WornKey() != oldWornKey)
            {
                _config.WornItems.Remove(oldWornKey);
                _config.WornItems[_editTarget.WornKey()] = _editTarget.Id;
            }

            // Write collections back first — the option propagation below matches on collection.
            for (var i = 0; i < _editTarget.Mods.Count && i < _editModCollections.Count; i++)
            {
                var newColl = _editModCollections[i];
                var oldColl = _editTarget.Mods[i].Collection;
                if (newColl.Equals(oldColl, StringComparison.OrdinalIgnoreCase)) continue;

                _editTarget.Mods[i].Collection = newColl;
                _log.Debug($"[Wardrobe] Edit: '{_editTarget.Name}' mod '{_editTarget.Mods[i].ModName}' " +
                           $"collection '{oldColl}' → '{newColl}'");

                // Other items referencing this mod in the same old collection were almost certainly
                // imported with the same wrong default, so move them together.
                var modDir = _editTarget.Mods[i].ModDirectory;
                foreach (var other in _config.WardrobeItems)
                {
                    if (other == _editTarget) continue;
                    foreach (var om in other.Mods)
                    {
                        if (om.ModDirectory.Equals(modDir, StringComparison.OrdinalIgnoreCase) &&
                            om.Collection.Equals(oldColl, StringComparison.OrdinalIgnoreCase))
                        {
                            om.Collection = newColl;
                            _log.Debug($"[Wardrobe] Edit: also moved '{other.Name}' mod '{om.ModName}' to '{newColl}'");
                        }
                    }
                }
            }

            // Write back mod options and propagate to all items sharing the same mod
            for (var i = 0; i < _editTarget.Mods.Count && i < _editModOptions.Count; i++)
            {
                var opts = _editModOptions[i];
                if (opts.Analysis == null) continue;

                var newSingle = BuildOptions(opts.Analysis.OptionGroups, opts.SingleSel);
                var newMulti  = BuildMultiOptions(opts.Analysis.OptionGroups, opts.MultiSel);
                _editTarget.Mods[i].Options      = newSingle;
                _editTarget.Mods[i].MultiOptions = newMulti;

                // Propagate to items in *other* slots only. Items sharing a mod across slots are
                // worn together, and Penumbra can only hold one option state per mod, so their
                // options must agree. Items in the same slot are variants — different option sets
                // for the same mod, never worn at once — and must be allowed to differ.
                var modDir = _editTarget.Mods[i].ModDirectory;
                var coll   = _editTarget.Mods[i].Collection;
                foreach (var other in _config.WardrobeItems)
                {
                    if (other == _editTarget) continue;
                    if (other.Slot == _editTarget.Slot) continue;

                    foreach (var otherMod in other.Mods)
                    {
                        if (otherMod.ModDirectory.Equals(modDir, StringComparison.OrdinalIgnoreCase) &&
                            otherMod.Collection.Equals(coll, StringComparison.OrdinalIgnoreCase))
                        {
                            otherMod.Options      = newSingle;
                            otherMod.MultiOptions = newMulti;
                        }
                    }
                }
            }

            // Last, because it can add to and remove from _editTarget.Mods, and the loops above
            // index into it against _editModCollections and _editModOptions.
            ApplyEditSupplementChanges();

            _config.Save();

            // If the item is currently worn, re-apply immediately so Penumbra and Glamourer update.
            if (_wardrobe.IsItemWorn(_editTarget))
            {
                _wardrobe.WearItem(_editTarget);
                Plugin.Penumbra.RedrawPlayer();
            }

            Close();
            return; // Close() nulls _editTarget — nothing below may run this frame
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(footerBtnW, 0)))
        {
            Close();
            return;
        }
    }

    /// <summary>
    /// Clones an item as a new wardrobe entry, so it can be given a different set of mod options.
    /// </summary>
    /// <remarks>
    /// The copy inherits everything — mods, collections, current option selections, detected game
    /// item and image — so only what differs has to be changed. Mod references are deep-copied;
    /// sharing them would make editing one variant silently rewrite the other.
    /// The copy is opened for editing immediately, since it always needs at least a rename.
    /// </remarks>
    private void CreateVariant(WardrobeItem source)
    {
        // Persist any in-progress edits first, so the copy reflects what is on screen
        source.Name      = _editName.Trim();
        source.ImagePath = string.IsNullOrEmpty(_editImage) ? null : _editImage.Trim();
        source.Slot      = SelectedSlot(_editSlotIdx);
        source.Replaces  = EditedReplaces();
        source.Notes     = EditedNotes();
        source.Tags      = new List<string>(_editTags);

        var copy = new WardrobeItem
        {
            Name              = $"{source.Name} (variant)",
            Slot              = source.Slot,
            Replaces          = source.Replaces,
            Notes             = source.Notes,
            ImagePath         = source.ImagePath,
            GlamourerItemId   = source.GlamourerItemId,
            GlamourerItemName = source.GlamourerItemName,
            ModelSetId        = source.ModelSetId,
            HairIdByRace      = new Dictionary<string, ushort>(source.HairIdByRace),
            Tags              = new List<string>(source.Tags),
            IsFavorite        = false,
        };

        foreach (var mod in source.Mods)
        {
            copy.Mods.Add(new ModReference
            {
                Label        = mod.Label,
                Collection   = mod.Collection,
                ModDirectory = mod.ModDirectory,
                ModName      = mod.ModName,
                Options      = new Dictionary<string, string>(mod.Options),
                MultiOptions = mod.MultiOptions.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
            });
        }

        _config.WardrobeItems.Add(copy);
        _config.Save();
        _log.Information($"[Wardrobe] Created variant '{copy.Name}' from '{source.Name}'");

        OpenEdit(copy);
    }

    /// <summary>
    /// Search-and-assign for the game item, when detection could not find one or found the wrong one.
    /// </summary>
    /// <remarks>
    /// Some mods skin an existing item rather than replacing a model — piercings and similar are
    /// often hung on an Emperor's New piece because it is invisible. Those are only visible while
    /// that exact item is equipped, so the item has to be set by hand for Wear to work.
    /// </remarks>
    private void DrawManualItemPicker(WardrobeItem item)
    {
        if (!ImGui.CollapsingHeader("Set game item manually")) return;

        ImGui.TextDisabled($"Searches equippable {item.Slot.DisplayName()} items. Use this for " +
                           "mods that skin an existing item, such as a piercing attached to an " +
                           "Emperor's New piece.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##itemsearch", "Type at least 2 characters…", ref _itemSearch, 128);

        var results = _itemLookup.SearchItems(_itemSearch, item.Slot);
        if (results.Count == 0)
        {
            if (_itemSearch.Trim().Length >= 2)
                ImGui.TextDisabled("No matching items for this slot.");
            return;
        }

        ImGui.Spacing();
        if (ImGui.BeginChild("##itemresults", new Vector2(-1, 160), true))
        {
            foreach (var (id, name) in results)
            {
                if (ImGui.Selectable($"{name}##pick_{id}", id == item.GlamourerItemId))
                {
                    item.GlamourerItemId   = id;
                    item.GlamourerItemName = name;
                    _config.Save();
                    _log.Debug($"[Wardrobe] Edit: '{item.Name}' game item set manually to '{name}' (id={id})");
                }
            }
        }
        ImGui.EndChild();

        if (item.GlamourerItemId.HasValue)
        {
            if (ImGui.SmallButton("Clear game item"))
            {
                item.GlamourerItemId   = null;
                item.GlamourerItemName = null;
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Wear will stop equipping anything for this item,\n" +
                                 "and Unwear will leave the slot alone.");
        }
    }

    /// <summary>Per-mod collection pickers shown in edit mode.</summary>
    private void DrawEditModCollections()
    {
        if (_editTarget == null) return;

        if (_editTarget.Mods.Count == 0)
        {
            ImGui.TextDisabled("This item has no linked mods.");
            return;
        }

        var collNames = _collections.Count > 0
            ? _collections.ToArray()
            : new[] { "(no collections)" };

        for (var i = 0; i < _editTarget.Mods.Count && i < _editModCollections.Count; i++)
        {
            var mod     = _editTarget.Mods[i];
            var removed = _editModRemovals.Contains(i);

            ImGui.PushID($"emod_{i}");

            if (removed)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), $"{mod.Label} — will be removed");
                ImGui.SameLine();
                if (ImGui.SmallButton("Keep")) _editModRemovals.Remove(i);
                ImGui.Spacing();
                ImGui.PopID();
                continue;
            }

            ImGui.TextUnformatted(mod.Label);
            UiLayout.SameLineIfRoomForText($"({mod.ModName})");
            ImGui.TextDisabled($"({mod.ModName})");

            // Only supplements can be detached; index 0 is the mod the item is built from
            if (i > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("X")) _editModRemovals.Add(i);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Remove this supplementary mod on save.\n" +
                                     "It is removed from every item built from the same mod.");
            }

            var cur = Array.FindIndex(collNames,
                n => n.Equals(_editModCollections[i], StringComparison.OrdinalIgnoreCase));

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##ecoll{i}", _editModCollections[i]))
            {
                for (var c = 0; c < collNames.Length; c++)
                {
                    if (ImGui.Selectable(collNames[c], c == cur))
                        _editModCollections[i] = collNames[c];
                    if (c == cur) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            // Stored collection no longer exists in Penumbra (renamed or deleted)
            if (cur < 0)
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f),
                    $"  '{_editModCollections[i]}' not found in Penumbra.");

            ImGui.Spacing();
            ImGui.PopID();
        }

        if (_editModCollections.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                "These mods are in different collections — only the ones in your character's " +
                "collection will show up.");
    }

    /// <summary>
    /// Attaches further supplementary mods to an item that has already been imported.
    /// </summary>
    /// <remarks>
    /// Previously the only way to add an upscale or compatibility patch you had missed was to delete
    /// the item and import it again, which also lost its name, image and tags. Rows are staged in
    /// _extraMods and written on save, so Cancel discards them like every other edit-mode field.
    /// </remarks>
    private void DrawEditExtraMods()
    {
        if (_editTarget == null) return;

        ImGui.TextUnformatted("Add Supplementary Mods");
        ImGui.TextDisabled("Body upscales, compatibility patches, etc.");

        var siblings = SiblingsOfPrimary(_editTarget);
        if (siblings.Count > 1)
        {
            // Variants are counted in, but say so: they sit in the same slot as the item being
            // edited, so nothing else on screen hints that they are part of the total.
            var variants = siblings.Count(i => i != _editTarget && i.Slot == _editTarget.Slot);
            ImGui.TextDisabled(variants > 0
                ? $"Applies to all {siblings.Count} items built from this mod, variants included."
                : $"Applies to all {siblings.Count} items built from this mod.");
        }

        for (var i = _extraMods.Count - 1; i >= 0; i--)
            if (!DrawExtraMod(i))
                _extraMods.RemoveAt(i);

        // Import mode analyses supplements as a side effect of rebuilding its slot list, which never
        // runs here. Without this a mod picked in edit mode would show no option groups, so none of
        // its selections would be recorded and its options would be left to whatever Penumbra
        // happened to have set. Cached inside, so this is one disk read per mod picked.
        foreach (var extra in _extraMods)
            EnsureExtraAnalysis(extra);

        if (ImGui.Button("+ Add Supplementary Mod", new Vector2(-1, 0)))
            _extraMods.Add(new ExtraMod
            {
                // Inherit the item's own collection rather than defaulting to index 0, which is
                // whichever collection sorts first and is rarely the one in use.
                CollectionIdx = CollectionIndexOf(_editTarget.Mods.Count > 0
                    ? _editTarget.Mods[0].Collection
                    : _config.DefaultCollection),
            });
    }

    private int CollectionIndexOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var idx = _collections.ToList().FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? 0 : idx;
    }

    /// <summary>
    /// Every wardrobe item built from the same mod files as <paramref name="item"/> — the same
    /// primary mod in the same collection — including <paramref name="item"/> itself.
    /// </summary>
    /// <remarks>
    /// Unlike option propagation, this deliberately includes items in the same slot. Options must be
    /// allowed to differ between same-slot variants because Penumbra holds one option state per mod
    /// and variants are never worn together; but *which* mods are attached is a property of the mod
    /// files, so a body upscale belongs to every item the mod produced, variants included.
    /// </remarks>
    private List<WardrobeItem> SiblingsOfPrimary(WardrobeItem item)
    {
        if (item.Mods.Count == 0) return new List<WardrobeItem> { item };

        var dir  = item.Mods[0].ModDirectory;
        var coll = item.Mods[0].Collection;

        var siblings = _config.WardrobeItems
            .Where(i => i.Mods.Count > 0 &&
                        i.Mods[0].ModDirectory.Equals(dir, StringComparison.OrdinalIgnoreCase) &&
                        i.Mods[0].Collection.Equals(coll, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!siblings.Contains(item)) siblings.Add(item);
        return siblings;
    }

    /// <summary>
    /// Writes the staged supplementary-mod additions and removals to every item built from the same
    /// mod. Must run after the collection and option loops above, which index into
    /// _editTarget.Mods and would be thrown off by a removal.
    /// </summary>
    private void ApplyEditSupplementChanges()
    {
        if (_editTarget == null || _editTarget.Mods.Count == 0) return;

        var siblings = SiblingsOfPrimary(_editTarget);

        // Resolve removals to directories before touching anything — the staged indices are only
        // meaningful against the list as it stands right now.
        var removedDirs = _editModRemovals
            .Where(i => i > 0 && i < _editTarget.Mods.Count)
            .Select(i => _editTarget.Mods[i].ModDirectory)
            .ToList();

        foreach (var dir in removedDirs)
        {
            foreach (var item in siblings)
            {
                // From index 1: a mod that is somebody else's supplement may be another item's
                // primary, and removing that would orphan the item.
                for (var m = item.Mods.Count - 1; m >= 1; m--)
                {
                    if (!item.Mods[m].ModDirectory.Equals(dir, StringComparison.OrdinalIgnoreCase)) continue;
                    _log.Debug($"[Wardrobe] Edit: removed supplement '{item.Mods[m].ModName}' from '{item.Name}'");
                    item.Mods.RemoveAt(m);
                }
            }
        }

        foreach (var add in BuildExtraRefs())
        {
            foreach (var item in siblings)
            {
                if (item.Mods.Any(m => m.ModDirectory.Equals(add.ModDirectory, StringComparison.OrdinalIgnoreCase)))
                    continue; // already attached, or it is this item's own primary

                // Deep copy per item. Sharing one reference would make editing the supplement's
                // options on one item silently rewrite every other item built from the same mod.
                item.Mods.Add(new ModReference
                {
                    Label        = add.Label,
                    Collection   = add.Collection,
                    ModDirectory = add.ModDirectory,
                    ModName      = add.ModName,
                    Options      = new Dictionary<string, string>(add.Options),
                    MultiOptions = add.MultiOptions.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
                });
                _log.Debug($"[Wardrobe] Edit: added supplement '{add.ModName}' to '{item.Name}'");
            }
        }

        _editModRemovals.Clear();
        _extraMods.Clear();
    }

    // ── Import mode ───────────────────────────────────────────────────────────

    private void DrawImportMode()
    {
        ImGui.TextUnformatted("Import from Mod");
        ImGui.Separator();

        DrawCollectionCombo();
        DrawModCombo();

        ImGui.Spacing();
        // Capture up front so the Begin/End pair can never be unbalanced by the handler
        var analyzeDisabled = !_modPicked;
        if (analyzeDisabled) ImGui.BeginDisabled();
        if (ImGui.Button("Analyze Mod", new Vector2(-1, 0)))
            DoAnalyze();
        var analyzeHovered = ImGui.IsItemHovered();
        if (analyzeDisabled) ImGui.EndDisabled();
        if (analyzeDisabled && analyzeHovered)
            ImGui.SetTooltip("Pick a mod first.");

        if (!string.IsNullOrEmpty(_analyzeError))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _analyzeError);
        }

        if (!_analyzed)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Pick a mod and click Analyze.");
            ImGui.Spacing();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
                Close();
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Option groups (shared across all slots)
        if (_analysisResult?.OptionGroups.Count > 0)
        {
            ImGui.TextUnformatted("Options:");
            foreach (var g in _analysisResult.OptionGroups)
                DrawGroupPicker(g, _groupSelections, _multiGroupSelections);
            ImGui.Spacing();
        }

        // Per-slot rows or manual picker
        if (_slotConfigs.Count > 0)
        {
            ImGui.TextUnformatted("Items to create:");
            ImGui.TextDisabled("Uncheck slots you don't want.");
            ImGui.Spacing();

            foreach (var cfg in _slotConfigs)
                DrawSlotRow(cfg);
        }
        else
        {
            ImGui.TextDisabled("No equipment slots detected — choose manually:");
            ImGui.Spacing();

            var slotNames = _slotChoices.Select(s => s.DisplayName()).ToArray();
            ImGui.TextDisabled("Slot");
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##manSlot", ref _manualSlotIdx, slotNames, slotNames.Length);

            ImGui.TextDisabled("Name");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##manName", ref _manualName, 128);

            ImGui.TextDisabled("Image path (optional)");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##manImg", ref _manualImage, 512);
        }

        // Supplementary mods
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Additional Mods");
        ImGui.TextDisabled("Body upscales, compatibility patches, etc.");

        for (var i = _extraMods.Count - 1; i >= 0; i--)
            if (!DrawExtraMod(i))
            {
                _extraMods.RemoveAt(i);
                _extrasDirty = true;
            }

        if (ImGui.Button("+ Add Supplementary Mod", new Vector2(-1, 0)))
        {
            // Inherit the main mod's collection. Defaulting to index 0 silently put supplementary
            // mods in whichever collection sorted first, so they were enabled in a collection the
            // character does not use and had no visible effect.
            // No rebuild here: nothing is chosen yet, so there is nothing to analyse or merge.
            _extraMods.Add(new ExtraMod { CollectionIdx = _collectionIdx });
        }

        // Supplementary mods can contribute slots of their own, so the slot list is rebuilt
        // whenever they change. Deferred to here so the list is not mutated mid-iteration.
        if (_extrasDirty)
        {
            _extrasDirty = false;
            if (_analyzed) RebuildSlotConfigs();
        }

        // Confirm
        ImGui.Spacing();
        ImGui.Separator();

        var canImport = _slotConfigs.Count > 0
            ? _slotConfigs.Any(c => c.Include && !string.IsNullOrWhiteSpace(c.Name))
            : !string.IsNullOrWhiteSpace(_manualName);

        if (!canImport) ImGui.BeginDisabled();
        var importLabel = _slotConfigs.Count(c => c.Include) is var n and > 1
            ? $"Import {n} Items"
            : "Import";
        if (ImGui.Button(importLabel, new Vector2(150, 0)))
            DoImport();
        if (!canImport) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
            Close();
    }

    // ── Widget helpers ────────────────────────────────────────────────────────

    private void DrawCollectionCombo()
    {
        var names = _collections.Count > 0 ? _collections.ToArray() : new[] { "(no collections)" };
        var label = _collectionIdx < names.Length ? names[_collectionIdx] : names[0];

        ImGui.TextDisabled("Collection");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##coll", label))
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (ImGui.Selectable(names[i], i == _collectionIdx))
                    _collectionIdx = i;
                if (i == _collectionIdx) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// Slots already imported with this mod as their primary mod.
    /// </summary>
    /// <remarks>
    /// This flags duplicates rather than preventing them. One mod legitimately covers several
    /// slots — importing the same mod for Body, Legs and Neck is normal — so an already-imported
    /// mod stays selectable and only the slots that would actually duplicate are pre-unchecked.
    /// </remarks>
    private List<EquipSlot> ImportedSlots(string modDirectory) =>
        _config.WardrobeItems
            .Where(i => i.Mods.Count > 0 &&
                        i.Mods[0].ModDirectory.Equals(modDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Slot)
            .Distinct()
            .OrderBy(s => (int)s)
            .ToList();

    /// <summary>How a mod is currently used by the wardrobe, for import-list styling.</summary>
    private enum ModUsage
    {
        /// <summary>Not referenced by any item.</summary>
        Unused,
        /// <summary>Only ever attached as a supplementary mod, never imported in its own right.</summary>
        SupportOnly,
        /// <summary>Imported as the primary mod of at least one item.</summary>
        Primary,
    }

    /// <summary>
    /// Classifies a mod by how the wardrobe already references it. Supplementary-only mods are
    /// worth flagging (they are in use) but are not duplicates in the way a primary import is,
    /// so they get their own treatment rather than being lumped in with imported mods.
    /// </summary>
    private ModUsage UsageOf(string modDirectory)
    {
        var supportOnly = false;

        foreach (var item in _config.WardrobeItems)
        {
            for (var m = 0; m < item.Mods.Count; m++)
            {
                if (!item.Mods[m].ModDirectory.Equals(modDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (m == 0) return ModUsage.Primary;
                supportOnly = true;
            }
        }

        return supportOnly ? ModUsage.SupportOnly : ModUsage.Unused;
    }

    /// <summary>
    /// Draws one row of a mod picker, styled by how the wardrobe already uses that mod.
    /// Shared by the primary and supplementary pickers so both behave identically.
    /// </summary>
    /// <returns>
    /// True when the row was clicked. False when it was not — including when the row is hidden
    /// by the "hide already-imported mods" setting, in which case nothing is drawn at all.
    /// </returns>
    private bool DrawModSelectable(string dir, string name, bool selected, string id)
    {
        var usage = UsageOf(dir);

        if (usage == ModUsage.Primary    && _config.HideImportedMods) return false;
        if (usage == ModUsage.SupportOnly && _config.HideSupportMods) return false;

        var imported = usage == ModUsage.Primary ? ImportedSlots(dir) : new List<EquipSlot>();

        var dim = usage != ModUsage.Unused;
        if (dim)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.55f, 1f));

        var italic = usage == ModUsage.SupportOnly && _italicFont.Push();

        var rowLabel = usage switch
        {
            ModUsage.Primary     => $"{name}   · {string.Join(", ", imported.Select(s => s.DisplayName()))}",
            ModUsage.SupportOnly => $"{name}   (support mod)",
            _                    => name,
        };

        var clicked = ImGui.Selectable($"{rowLabel}##{id}", selected);
        if (selected) ImGui.SetItemDefaultFocus();

        if (italic) _italicFont.Pop();
        if (dim)    ImGui.PopStyleColor();

        if (dim && ImGui.IsItemHovered())
            ImGui.SetTooltip(usage == ModUsage.Primary
                ? $"Already imported for: {string.Join(", ", imported.Select(s => s.DisplayName()))}\n" +
                  "Still selectable — other slots from this mod can be imported."
                : "Used as a supplementary mod on existing items,\n" +
                  "but never imported as an item of its own.");

        return clicked;
    }

    private void DrawModCombo()
    {
        // Nothing is selected until the user picks — the preview must not imply mod 0 is chosen
        var label = _modPicked && _modIdx < _mods.Count
            ? _mods[_modIdx].Name
            : "(pick a mod)";

        ImGui.TextDisabled("Mod");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##mod", label, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##msearch", ref _modSearch, 256))
            { /* filter applied below */ }
            ImGui.Separator();

            for (var i = 0; i < _mods.Count; i++)
            {
                var (dir, name) = _mods[i];
                if (!string.IsNullOrEmpty(_modSearch) &&
                    !name.Contains(_modSearch, StringComparison.OrdinalIgnoreCase) &&
                    !dir.Contains(_modSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!DrawModSelectable(dir, name, _modPicked && i == _modIdx, $"mod_{i}")) continue;

                _modIdx    = i;
                _modPicked = true;
                _analyzed  = false;
                _modSearch = string.Empty;
            }
            ImGui.EndCombo();
        }
    }

    private static void DrawGroupPicker(ModOptionGroup group,
        Dictionary<string, int> singleSel, Dictionary<string, HashSet<string>> multiSel)
    {
        ImGui.TextDisabled(group.GroupName);

        if (group.GroupType == ModGroupType.Multi)
        {
            if (!multiSel.ContainsKey(group.GroupName))
                multiSel[group.GroupName] = new HashSet<string>();
            var selected = multiSel[group.GroupName];

            foreach (var opt in group.OptionNames)
            {
                var isChecked = selected.Contains(opt);
                if (ImGui.Checkbox($"{opt}##{group.GroupName}_{opt}", ref isChecked))
                {
                    if (isChecked) selected.Add(opt);
                    else           selected.Remove(opt);
                }
            }
        }
        else
        {
            if (!singleSel.ContainsKey(group.GroupName)) singleSel[group.GroupName] = 0;
            var idx   = singleSel[group.GroupName];
            var names = group.OptionNames.ToArray();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##{group.GroupName}", idx < names.Length ? names[idx] : names[0]))
            {
                for (var i = 0; i < names.Length; i++)
                {
                    if (ImGui.Selectable(names[i], i == idx))
                        singleSel[group.GroupName] = i;
                    if (i == idx) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
    }

    /// <summary>
    /// Dropdown of every game item sharing this slot's model, so the auto-detected one can be
    /// overridden.
    /// </summary>
    /// <remarks>
    /// Detection picks the lowest row ID among items sharing a model, which is arbitrary when a
    /// model is reused — "Asuran Hakama of Healing", "Nameless Hakama" and several others are one
    /// model. The stored item ID is what Glamourer equips and what worn-detection compares against,
    /// so being able to choose the intended one matters.
    /// Returns true when the user picked a different item.
    /// </remarks>
    private bool DrawGameItemPicker(string id, ushort? setId, EquipSlot slot, ulong? currentId,
        out ulong? pickedId, out string? pickedName)
    {
        pickedId   = null;
        pickedName = null;

        if (setId is not { } sid || sid == 0) return false;

        var candidates = _itemLookup.FindItems(sid, slot);
        if (candidates.Count <= 1) return false; // nothing to choose between

        var currentLabel = candidates.FirstOrDefault(c => c.ItemId == currentId).ItemName
                           ?? "(pick an item)";

        ImGui.TextDisabled($"Game item  ({candidates.Count} share this model)");
        ImGui.SetNextItemWidth(-1);

        var changed = false;
        if (ImGui.BeginCombo($"##gameitem_{id}", currentLabel))
        {
            foreach (var (itemId, itemName) in candidates)
            {
                var sel = itemId == currentId;
                if (ImGui.Selectable($"{itemName}##gi_{id}_{itemId}", sel))
                {
                    pickedId   = itemId;
                    pickedName = itemName;
                    changed    = true;
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        return changed;
    }

    /// <summary>
    /// The staged Replaces field, or null when the selected slot has no use for one — a stale key
    /// left on an item moved out of a mod category would silently change the key it is worn under.
    /// </summary>
    private string? EditedReplaces() =>
        SelectedSlot(_editSlotIdx).IsModCategory() && !string.IsNullOrWhiteSpace(_editReplaces)
            ? _editReplaces.Trim()
            : null;

    /// <summary>
    /// The staged notes, or null when they are blank — an empty string would be written into every
    /// saved config for no reason and read as "has notes" by anything checking the field.
    /// </summary>
    private string? EditedNotes() =>
        string.IsNullOrWhiteSpace(_editNotes) ? null : _editNotes.Trim();

    /// <summary>
    /// Editor for what a mod-category item replaces, which is what decides whether wearing it
    /// displaces something already worn. Free text rather than a picker: it is matched between
    /// items by string, so two mods whose file names differ can be lined up by typing the same
    /// thing into both.
    /// </summary>
    private void DrawReplacesEditor(EquipSlot slot)
    {
        var hint = slot switch
        {
            EquipSlot.Animation => "e.g. j_pose — the animation file name",
            EquipSlot.Mount => "e.g. m0361 — the monster id",
            _               => "blank to wear independently",
        };

        ImGui.Spacing();
        ImGui.TextDisabled("Replaces");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ereplaces", hint, ref _editReplaces, 128);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{slot.DisplayName()} mods are not exclusive the way gear slots are,\n" +
                             "so this is what decides which of them replace each other.\n\n" +
                             "Two items with the same value swap each other out when worn,\n" +
                             "exactly as two body mods do. Detected from the mod's files on\n" +
                             "import; type the same value into two items to pair them up by hand.");
        ImGui.TextDisabled($"Leave blank to wear this independently of other " +
                           $"{slot.DisplayName()} items.");
    }

    private void DrawSlotRow(SlotConfig cfg)
    {
        ImGui.PushID(cfg.Slot.ToString());

        var inc = cfg.Include;
        if (ImGui.Checkbox("##inc", ref inc)) cfg.Include = inc;
        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.75f, 0.95f, 1f));
        ImGui.TextUnformatted(cfg.Slot.DisplayName());
        ImGui.PopStyleColor();

        // Each badge drops to the next line rather than running off the side of the panel
        if (cfg.GlamourerItemName != null)
        {
            var badge = $"→ {cfg.GlamourerItemName}";
            UiLayout.SameLineIfRoomForText(badge);
            ImGui.TextDisabled(badge);
        }
        else if (cfg.Slot.IsModOnly())
        {
            const string badge = "(no item — the mod is the whole effect)";
            UiLayout.SameLineIfRoomForText(badge);
            ImGui.TextDisabled(badge);

            if (cfg.Replaces != null)
            {
                var replaces = $"· replaces {cfg.Replaces}";
                UiLayout.SameLineIfRoomForText(replaces);
                ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f), replaces);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Another {cfg.Slot.DisplayName()} item replacing '{cfg.Replaces}'\n" +
                                     "will swap this one out when worn. Editable after import.");
            }
        }
        else
        {
            const string badge = "(item not found in game data)";
            UiLayout.SameLineIfRoomForText(badge);
            ImGui.TextDisabled(badge);
        }

        if (cfg.SourceMod != null)
        {
            UiLayout.SameLineIfRoomForText("· from supplement");
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.6f, 1f), "· from supplement");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"This slot comes from '{cfg.SourceMod}',\nnot from the main mod.");
        }

        if (cfg.AlreadyImported)
        {
            UiLayout.SameLineIfRoomForText("· already imported");
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "· already imported");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A wardrobe item already exists for this mod and slot.\n" +
                                 "Tick the box anyway to import a second copy.");
        }

        if (!cfg.Include)
        {
            ImGui.PopID();
            return;
        }

        // Several game items can share one model — let the auto-picked one be overridden
        if (DrawGameItemPicker("slotitem", cfg.SetId, cfg.Slot, cfg.GlamourerItemId,
                out var pickedId, out var pickedName))
        {
            cfg.GlamourerItemId   = pickedId;
            cfg.GlamourerItemName = pickedName;
        }

        ImGui.TextDisabled("Name");
        ImGui.SetNextItemWidth(-1);
        var n = cfg.Name;
        if (ImGui.InputText("##name", ref n, 128)) cfg.Name = n;

        ImGui.TextDisabled("Image path (optional)");
        ImGui.SetNextItemWidth(-1);
        var img = cfg.Image;
        if (ImGui.InputText("##img", ref img, 512)) cfg.Image = img;

        ImGui.Spacing();
        ImGui.PopID();
    }

    // Returns false if the row was deleted
    private bool DrawExtraMod(int index)
    {
        var extra = _extraMods[index];
        ImGui.PushID($"extra_{index}");

        ImGui.Separator();

        ImGui.SetNextItemWidth(-60);
        var lbl = extra.Label;
        if (ImGui.InputText("##lbl", ref lbl, 64)) extra.Label = lbl;
        ImGui.SameLine();
        var remove = ImGui.Button("X", new Vector2(50, 0));

        var collNames = _collections.ToArray();
        if (collNames.Length > 0)
        {
            var ci = Math.Min(extra.CollectionIdx, collNames.Length - 1);
            ImGui.TextDisabled("Collection");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##ecoll", collNames[ci]))
            {
                for (var i = 0; i < collNames.Length; i++)
                {
                    if (ImGui.Selectable(collNames[i], i == ci))
                        extra.CollectionIdx = i;
                }
                ImGui.EndCombo();
            }
        }

        if (_mods.Count > 0)
        {
            var mi = Math.Min(extra.ModIdx, _mods.Count - 1);
            ImGui.TextDisabled("Mod  [~ to load options]");
            ImGui.SetNextItemWidth(-60);

            // Until a mod is chosen the preview must not imply mod 0 is selected
            var preview = extra.ModPicked ? _mods[mi].Name : "(pick a mod)";
            if (ImGui.BeginCombo("##emod", preview, ImGuiComboFlags.HeightLarge))
            {
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                var ms = extra.ModSearch;
                if (ImGui.InputText("##emsearch", ref ms, 256))
                    extra.ModSearch = ms;
                ImGui.Separator();

                for (var i = 0; i < _mods.Count; i++)
                {
                    var (dir, name) = _mods[i];
                    if (!string.IsNullOrEmpty(extra.ModSearch) &&
                        !name.Contains(extra.ModSearch, StringComparison.OrdinalIgnoreCase) &&
                        !dir.Contains(extra.ModSearch, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!DrawModSelectable(dir, name, extra.ModPicked && i == mi, $"emod_{index}_{i}")) continue;

                    extra.ModIdx        = i;
                    extra.ModPicked     = true;
                    extra.Analysis      = null;
                    extra.ModSearch     = string.Empty;
                    extra.GroupSelections.Clear();
                    _extrasDirty        = true;
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            // Captured before the button: picking a mod in the combo above can flip ModPicked,
            // and an unbalanced Begin/EndDisabled corrupts ImGui's stack.
            var reloadDisabled = !extra.ModPicked;
            if (reloadDisabled) ImGui.BeginDisabled();
            if (ImGui.Button("~", new Vector2(50, 0)))
            {
                var m    = _mods[mi];
                var path = _penumbra.GetModFolderPath(m.Dir);
                if (path != null)
                {
                    extra.Analysis = _analysis.Analyze(path);
                    extra.GroupSelections.Clear();
                    extra.MultiGroupSelections.Clear();
                    SeedExtraFromPenumbra(extra, m);
                    _extrasDirty   = true;
                }
            }
            var reloadHovered = ImGui.IsItemHovered();
            if (reloadDisabled) ImGui.EndDisabled();
            if (reloadHovered)
                ImGui.SetTooltip(reloadDisabled
                    ? "Pick a mod first."
                    : "Re-read this mod's files from disk.");

            if (extra.Analysis?.OptionGroups.Count > 0)
                foreach (var g in extra.Analysis.OptionGroups)
                    DrawGroupPicker(g, extra.GroupSelections, extra.MultiGroupSelections);
        }

        ImGui.PopID();
        return !remove;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the per-slot import rows from the primary mod *and* every supplementary mod.
    /// </summary>
    /// <remarks>
    /// A supplementary mod can cover slots the primary does not — an upscale that adds legs to a
    /// body-and-hands mod, for example. Detecting from the primary alone silently dropped those
    /// slots, so they could never be imported. Slots are merged; the primary wins on model IDs
    /// where both provide one, since it is the mod the item is really "about".
    /// Existing rows are preserved by slot so re-running this does not discard user edits.
    /// </remarks>
    private void RebuildSlotConfigs()
    {
        if (_analysisResult == null || _mods.Count == 0) return;

        var mod = _mods[Math.Min(_modIdx, _mods.Count - 1)];

        // Slot → model set ID, primary first so it takes precedence
        var setIds  = new Dictionary<EquipSlot, ushort>();
        var slots   = new HashSet<EquipSlot>(_analysisResult.DetectedSlots);
        var sources = new Dictionary<EquipSlot, string>();
        var replace = new Dictionary<EquipSlot, string>();

        foreach (var (slot, id) in _analysisResult.SlotSetIds)
            setIds.TryAdd(slot, id);
        foreach (var (slot, key) in _analysisResult.ReplaceKeys)
            replace.TryAdd(slot, key);

        foreach (var extra in _extraMods)
        {
            var analysis = EnsureExtraAnalysis(extra);
            if (analysis == null) continue;

            var extraName = _mods[Math.Min(extra.ModIdx, _mods.Count - 1)].Name;

            foreach (var slot in analysis.DetectedSlots)
            {
                // Only credit the supplementary mod for slots the primary does not cover
                if (slots.Add(slot)) sources[slot] = extraName;
            }
            foreach (var (slot, id) in analysis.SlotSetIds)
                setIds.TryAdd(slot, id);
            foreach (var (slot, key) in analysis.ReplaceKeys)
                replace.TryAdd(slot, key);
        }

        // Mod categories the user has not opted into would import as items that are then hidden
        // from the grid, which reads as the import having silently failed
        if (!_config.ModCategoriesEnabled)
            slots.RemoveWhere(s => s.IsModCategory());

        // Keep whatever the user has already typed or chosen for slots that still exist
        var previous = _slotConfigs.ToDictionary(c => c.Slot);
        _slotConfigs.Clear();

        var alreadyImported = ImportedSlots(mod.Dir);

        foreach (var slot in slots.OrderBy(s => (int)s))
        {
            if (previous.TryGetValue(slot, out var kept))
            {
                kept.SourceMod = sources.GetValueOrDefault(slot);
                _slotConfigs.Add(kept);
                continue;
            }

            ulong?  detectedId   = null;
            string? detectedName = null;
            ushort? slotSetId    = null;
            if (setIds.TryGetValue(slot, out var setId))
            {
                slotSetId = setId;
                var found = _itemLookup.FindBestItem(setId, slot);
                if (found.HasValue)
                {
                    detectedId   = found.Value.ItemId;
                    detectedName = found.Value.ItemName;
                }
            }

            _slotConfigs.Add(new SlotConfig
            {
                Slot              = slot,
                Include           = !alreadyImported.Contains(slot),
                AlreadyImported   = alreadyImported.Contains(slot),
                SetId             = slotSetId,
                SourceMod         = sources.GetValueOrDefault(slot),
                Replaces          = replace.GetValueOrDefault(slot),
                Name              = $"{mod.Name} ({slot.DisplayName()})",
                Image             = string.Empty,
                GlamourerItemId   = detectedId,
                GlamourerItemName = detectedName,
            });
        }

        // If only one slot, drop the parenthetical suffix for cleaner names
        if (_slotConfigs.Count == 1)
            _slotConfigs[0].Name = mod.Name;
    }

    /// <summary>Analyses a supplementary mod if it has not been analysed yet, caching the result.</summary>
    private ModAnalysisResult? EnsureExtraAnalysis(ExtraMod extra)
    {
        // Nothing chosen yet — ModIdx is still its default, so analysing would read an
        // arbitrary mod the user never selected.
        if (!extra.ModPicked) return null;
        if (extra.Analysis != null) return extra.Analysis;
        if (_mods.Count == 0) return null;

        var m    = _mods[Math.Min(extra.ModIdx, _mods.Count - 1)];
        var path = _penumbra.GetModFolderPath(m.Dir);
        if (path == null) return null;

        extra.Analysis = _analysis.Analyze(path);
        SeedExtraFromPenumbra(extra, m);
        return extra.Analysis;
    }

    /// <summary>
    /// Pre-selects a supplementary mod's options from Penumbra's live state, in its own collection
    /// rather than the primary's — the two are independently chosen and often differ.
    /// </summary>
    private void SeedExtraFromPenumbra(ExtraMod extra, (string Dir, string Name) mod)
    {
        if (extra.Analysis == null) return;

        var coll = _collections.Count > extra.CollectionIdx && extra.CollectionIdx >= 0
            ? _collections[extra.CollectionIdx]
            : string.Empty;

        ModAnalysisService.SeedSelectionsFromPenumbra(
            extra.Analysis.OptionGroups,
            _penumbra.GetModSettingsFull(coll, mod.Dir, mod.Name),
            extra.GroupSelections, extra.MultiGroupSelections);
    }

    /// <summary>The collection the import pickers currently point at, or empty when there are none.</summary>
    private string SelectedCollection() =>
        _collections.Count > 0
            ? _collections[Math.Min(_collectionIdx, _collections.Count - 1)]
            : string.Empty;

    private void DoAnalyze()
    {
        _analyzed      = false;
        _analyzeError  = string.Empty;
        _analysisResult = null;
        _slotConfigs.Clear();
        _groupSelections.Clear();

        if (_mods.Count == 0) { _analyzeError = "No mods available."; return; }
        if (!_modPicked)      { _analyzeError = "Pick a mod first.";  return; }

        var mod  = _mods[Math.Min(_modIdx, _mods.Count - 1)];
        var path = _penumbra.GetModFolderPath(mod.Dir);
        if (path == null) { _analyzeError = "Could not get mod path from Penumbra."; return; }

        _analysisResult = _analysis.Analyze(path);
        _analyzed       = true;

        RebuildSlotConfigs();

        _multiGroupSelections.Clear();
        foreach (var g in _analysisResult.OptionGroups)
        {
            if (g.GroupType == ModGroupType.Multi)
                _multiGroupSelections[g.GroupName] = new HashSet<string>();
            else
                _groupSelections[g.GroupName] = 0;
        }

        // Then overwrite those defaults with whatever Penumbra already has active, so the import
        // starts from the configuration the user set up there instead of from each group's first
        // option — which for a body mod is usually the wrong chest size.
        ModAnalysisService.SeedSelectionsFromPenumbra(
            _analysisResult.OptionGroups,
            _penumbra.GetModSettingsFull(SelectedCollection(), mod.Dir, mod.Name),
            _groupSelections, _multiGroupSelections);

        // Pre-fill manual fields too
        _manualName  = mod.Name;
        _manualImage = string.Empty;
    }

    private void DoImport()
    {
        if (_mods.Count == 0 || !_modPicked) return;

        var primaryMod = _mods[Math.Min(_modIdx, _mods.Count - 1)];
        var collection = _collections.Count > 0
            ? _collections[Math.Min(_collectionIdx, _collections.Count - 1)]
            : string.Empty;

        var primaryOptions      = BuildOptions(_analysisResult?.OptionGroups, _groupSelections);
        var primaryMultiOptions = BuildMultiOptions(_analysisResult?.OptionGroups, _multiGroupSelections);
        var extraRefs           = BuildExtraRefs();

        IEnumerable<(EquipSlot slot, string name, string? image, ulong? glamId, string? glamName,
            ushort? setId, string? replaces)> targets;

        if (_slotConfigs.Count > 0)
        {
            targets = _slotConfigs
                .Where(c => c.Include && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => (c.Slot, c.Name.Trim(),
                    string.IsNullOrEmpty(c.Image) ? (string?)null : c.Image.Trim(),
                    c.GlamourerItemId, c.GlamourerItemName, c.SetId, c.Replaces));
        }
        else
        {
            // Manually chosen slot, so there is no analysis to take a replace key from — an animation
            // imported this way is independent until one is typed in when editing it
            targets = new[]
            {
                (SelectedSlot(_manualSlotIdx),
                 _manualName.Trim(),
                 string.IsNullOrEmpty(_manualImage) ? (string?)null : _manualImage.Trim(),
                 (ulong?)null, (string?)null, (ushort?)null, (string?)null),
            };
        }

        foreach (var (slot, name, image, glamId, glamName, setId, replaces) in targets)
        {
            var item = new WardrobeItem
            {
                Name              = name,
                Slot              = slot,
                Replaces          = slot.IsModCategory() ? replaces : null,
                ImagePath         = image,
                GlamourerItemId   = glamId,
                GlamourerItemName = glamName,
                ModelSetId        = setId,
                HairIdByRace      = slot == EquipSlot.Hair && _analysisResult != null
                    ? _analysisResult.HairIdsByRace.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
                    : new Dictionary<string, ushort>(),
            };
            item.Mods.Add(new ModReference
            {
                Label        = "Main Mod",
                Collection   = collection,
                ModDirectory = primaryMod.Dir,
                ModName      = primaryMod.Name,
                Options      = primaryOptions,
                MultiOptions = primaryMultiOptions,
            });
            item.Mods.AddRange(extraRefs);
            _config.WardrobeItems.Add(item);
        }

        _config.Save();
        Close();
    }

    private static Dictionary<string, string> BuildOptions(
        IReadOnlyList<ModOptionGroup>? groups, Dictionary<string, int> selections)
    {
        var result = new Dictionary<string, string>();
        if (groups == null) return result;
        foreach (var g in groups.Where(g => g.GroupType == ModGroupType.Single))
        {
            if (selections.TryGetValue(g.GroupName, out var i))
                result[g.GroupName] = i < g.OptionNames.Count ? g.OptionNames[i] : g.OptionNames[0];
        }
        return result;
    }

    private static Dictionary<string, List<string>> BuildMultiOptions(
        IReadOnlyList<ModOptionGroup>? groups, Dictionary<string, HashSet<string>> selections)
    {
        var result = new Dictionary<string, List<string>>();
        if (groups == null) return result;
        foreach (var g in groups.Where(g => g.GroupType == ModGroupType.Multi))
        {
            if (selections.TryGetValue(g.GroupName, out var sel) && sel.Count > 0)
                result[g.GroupName] = sel.ToList();
        }
        return result;
    }

    private List<ModReference> BuildExtraRefs()
    {
        var refs = new List<ModReference>();
        foreach (var extra in _extraMods)
        {
            if (_mods.Count == 0) break;
            // Skip rows where no mod was ever chosen, rather than silently attaching mod 0
            if (!extra.ModPicked) continue;
            var m  = _mods[Math.Min(extra.ModIdx, _mods.Count - 1)];
            var ec = _collections.Count > extra.CollectionIdx
                ? _collections[extra.CollectionIdx] : string.Empty;
            refs.Add(new ModReference
            {
                Label        = string.IsNullOrWhiteSpace(extra.Label) ? "Supplementary Mod" : extra.Label,
                Collection   = ec,
                ModDirectory = m.Dir,
                ModName      = m.Name,
                Options      = BuildOptions(extra.Analysis?.OptionGroups, extra.GroupSelections),
                MultiOptions = BuildMultiOptions(extra.Analysis?.OptionGroups, extra.MultiGroupSelections),
            });
        }
        return refs;
    }

    /// <summary>
    /// Free-text notes about the item, with any web links in them shown clickable underneath.
    /// </summary>
    /// <remarks>
    /// The links are listed below the box rather than made clickable inside it: an input field is
    /// for typing into, and a click there has to keep meaning "put the caret here". This is also
    /// the only place they can be clicked at all — the grid shows notes in a tooltip, and a tooltip
    /// cannot be reached with the mouse.
    /// </remarks>
    private void DrawNotesEditor()
    {
        ImGui.TextUnformatted("Notes");
        ImGui.TextDisabled("Where it came from, what it goes with, a link to a preview…");
        ImGui.Spacing();

        var boxHeight = ImGui.GetTextLineHeight() * 4 + ImGui.GetStyle().FramePadding.Y * 2;
        ImGui.InputTextMultiline("##enotes", ref _editNotes, 2000,
            new Vector2(-1, boxHeight));

        if (!NoteText.HasLink(_editNotes)) return;

        ImGui.Spacing();
        NoteText.DrawLinks(_editNotes);
    }

    private void DrawTagEditor()
    {
        // Existing tags as chips with remove button.
        // Only call SameLine *between* chips — never after the last one — so the
        // next widget always starts on a fresh line without needing NewLine() to
        // cancel a dangling SameLine. A chip that would not fit starts a new row.
        var removeIdx = -1;
        for (var i = 0; i < _editTags.Count; i++)
        {
            if (i > 0) UiLayout.SameLineIfRoom(ChipWidth(_editTags[i]));
            ImGui.PushID($"tag_{i}");
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.35f, 0.2f, 0.55f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.45f, 0.28f, 0.68f, 1f));
            ImGui.SmallButton(_editTags[i]);
            ImGui.PopStyleColor(2);
            ImGui.SameLine();
            if (ImGui.SmallButton("×"))
                removeIdx = i;
            ImGui.PopID();
        }
        if (removeIdx >= 0) _editTags.RemoveAt(removeIdx);

        // Add new tag — use "/" to create sub-tags e.g. "Shoes/Boots/Ankle Boots"
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("##newtag", ref _editTagInput, 128,
                ImGuiInputTextFlags.EnterReturnsTrue))
            TryAddTag();
        UiLayout.SameLineIfRoomForButton("Add");
        if (ImGui.SmallButton("Add")) TryAddTag();
        ImGui.TextDisabled("Use / for sub-tags, e.g. Shoes/Boots/Ankle Boots");

        // Quick-add suggestions from other items
        var suggestions = _config.WardrobeItems
            .SelectMany(i => i.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !_editTags.Contains(t, StringComparer.OrdinalIgnoreCase)
                     && (string.IsNullOrEmpty(_editTagInput)
                         || t.Contains(_editTagInput, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t)
            .ToList();

        if (suggestions.Count > 0)
        {
            ImGui.TextDisabled("Existing tags — right-click one to edit it before adding.");
            for (var i = 0; i < suggestions.Count; i++)
            {
                var s     = suggestions[i];
                var label = s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] + "…" : s;

                if (i > 0) UiLayout.SameLineIfRoomForButton(label);

                if (ImGui.SmallButton($"{label}##sug_{s}"))
                {
                    _editTags.Add(s);
                    _editTagInput = string.Empty;
                }

                // Right-click loads the full tag into the input instead of adding it, so a
                // near-miss suggestion can be tweaked into a new one.
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    _editTagInput = s;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(s.Contains('/')
                        ? $"{s}\n\nClick to add · right-click to edit first"
                        : "Click to add · right-click to edit first");
            }
        }
    }

    /// <summary>
    /// Width of one tag chip: the tag button and the × that removes it, which are always drawn
    /// together and so have to move onto a new row together.
    /// </summary>
    private static float ChipWidth(string tag) =>
        UiLayout.ButtonWidth(tag) + ImGui.GetStyle().ItemSpacing.X + UiLayout.ButtonWidth("×");

    private void TryAddTag()
    {
        var tag = _editTagInput.Trim();
        if (!string.IsNullOrEmpty(tag) &&
            !_editTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            _editTags.Add(tag);
        _editTagInput = string.Empty;
    }

    private void TryRedetectItem(WardrobeItem item)
    {
        var primaryMod = item.Mods.Count > 0 ? item.Mods[0] : null;
        if (primaryMod == null || string.IsNullOrEmpty(primaryMod.ModDirectory))
        {
            _log.Warning("[Wardrobe] Re-detect: item has no primary mod directory");
            return;
        }

        var path = _penumbra.GetModFolderPath(primaryMod.ModDirectory);
        if (path == null)
        {
            _log.Warning("[Wardrobe] Re-detect: could not get mod folder path");
            return;
        }

        var result = _analysis.Analyze(path);

        // Mod categories identify themselves by name rather than by set ID, so they are re-read
        // from ReplaceKeys and there is no game item to look up afterwards
        if (item.Slot.IsModCategory())
        {
            if (result.ReplaceKeys.TryGetValue(item.Slot, out var key))
            {
                item.Replaces = key;
                _editReplaces = key;
                _config.Save();
                _log.Information($"[Wardrobe] Re-detected '{item.Name}': {item.Slot.DisplayName()} replaces '{key}'");
            }
            else
            {
                _log.Warning($"[Wardrobe] Re-detect: nothing detected for {item.Slot.DisplayName()} " +
                             $"in mod '{primaryMod.ModDirectory}'");
            }
            return;
        }

        if (result.SlotSetIds.TryGetValue(item.Slot, out var setId))
        {
            // Persist this regardless of the item lookup. For customisation slots it is the
            // hairstyle (or equivalent) number and there is no game item to find, so saving only
            // on a successful lookup would silently discard it.
            item.ModelSetId = setId;

            if (item.Slot == EquipSlot.Hair)
                item.HairIdByRace = result.HairIdsByRace
                    .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);

            _config.Save();

            if (item.Slot.IsCustomization())
            {
                var perRace = item.HairIdByRace.Count > 0
                    ? $" ({item.HairIdByRace.Count} race variants)" : string.Empty;
                _log.Information($"[Wardrobe] Re-detected '{item.Name}': {item.Slot.DisplayName()} id {setId}{perRace}");
                return;
            }

            var found = _itemLookup.FindBestItem(setId, item.Slot);
            if (found.HasValue)
            {
                item.GlamourerItemId   = found.Value.ItemId;
                item.GlamourerItemName = found.Value.ItemName;
                _log.Information($"[Wardrobe] Re-detected item for '{item.Name}': {found.Value.ItemName} (id={found.Value.ItemId})");
                _config.Save();
                return;
            }
        }
        _log.Warning($"[Wardrobe] Re-detect: nothing detected for slot {item.Slot} in mod '{primaryMod.ModDirectory}'");
    }

    private void ResetImport()
    {
        _analyzed       = false;
        _analyzeError   = string.Empty;
        _analysisResult = null;
        _modSearch      = string.Empty;
        _collectionIdx  = 0;
        _modIdx         = 0;
        _modPicked      = false;
        _manualSlotIdx  = 0;
        _manualName     = string.Empty;
        _manualImage    = string.Empty;
        _groupSelections.Clear();
        _multiGroupSelections.Clear();
        _editModOptions.Clear();
        _editModCollections.Clear();
        _editModRemovals.Clear();
        _slotConfigs.Clear();
        _extraMods.Clear();
        _extrasDirty = false;
        _editName     = string.Empty;
        _editImage    = string.Empty;
        _editSlotIdx  = 0;
        _editReplaces = string.Empty;
        _editNotes    = string.Empty;
        _slotChoices  = EquipSlotEx.Choices(_config.ModCategoriesEnabled);
        _editTags.Clear();
        _editTagInput = string.Empty;
    }

    public void Dispose() { }
}
