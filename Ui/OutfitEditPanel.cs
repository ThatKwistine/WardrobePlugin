using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// Side panel for creating or editing an outfit entry.
/// </summary>
public class OutfitEditPanel : IDisposable
{
    public bool IsOpen { get; private set; }

    private readonly Configuration   _config;
    private readonly WardrobeService _wardrobe;
    private readonly IPluginLog      _log;

    // Editing state
    private Outfit?  _target;       // null = creating a new outfit
    private bool     _isNew;

    // Buffered text fields (ImGui requires fixed-size char arrays)
    private string _name        = string.Empty;
    private string _description = string.Empty;
    private string _imagePath   = string.Empty;

    // Penumbra pickers
    private IList<string>                    _collections    = Array.Empty<string>();
    private IList<(string Dir, string Name)> _mods           = Array.Empty<(string, string)>();
    private int _selectedCollIdx = 0;
    private int _selectedModIdx  = -1;
    private bool _modEnabled     = true;

    // Glamourer
    private bool _hasGlamState;

    // Feedback
    private string? _statusMsg;
    private bool    _statusIsError;

    public OutfitEditPanel(Configuration config, WardrobeService wardrobe, IPluginLog log)
    {
        _config   = config;
        _wardrobe = wardrobe;
        _log      = log;
    }

    // ── Open ──────────────────────────────────────────────────────────────────

    public void OpenNew()
    {
        _isNew  = true;
        _target = new Outfit();
        PopulateFields(_target);
        IsOpen = true;
        _statusMsg = null;
    }

    public void OpenEdit(Outfit outfit)
    {
        _isNew  = false;
        _target = outfit;
        PopulateFields(outfit);
        IsOpen = true;
        _statusMsg = null;
    }

    private void PopulateFields(Outfit outfit)
    {
        _name        = outfit.Name;
        _description = outfit.Description;
        _imagePath   = outfit.ImagePath ?? string.Empty;
        _modEnabled  = outfit.ModEnabled;
        _hasGlamState = !string.IsNullOrEmpty(outfit.GlamourerStateBase64);

        RefreshCollections();
        RefreshMods();

        // Pre-select collection/mod if editing
        if (!string.IsNullOrEmpty(outfit.PenumbraCollection))
        {
            var ci = _collections.IndexOf(outfit.PenumbraCollection);
            if (ci >= 0) _selectedCollIdx = ci;
        }
        if (!string.IsNullOrEmpty(outfit.PenumbraModDirectory))
        {
            var mi = _mods.Select((m, i) => (m, i))
                          .FirstOrDefault(x => x.m.Dir == outfit.PenumbraModDirectory).i;
            _selectedModIdx = mi >= 0 ? mi : -1;
        }
    }

    // ── Draw ──────────────────────────────────────────────────────────────────

    public void Draw()
    {
        if (_target == null) { IsOpen = false; return; }

        ImGui.TextUnformatted(_isNew ? "New Outfit" : "Edit Outfit");
        ImGui.Separator();

        // ── Basic info ────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##name", ref _name, 128);

        ImGui.TextUnformatted("Description");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##desc", ref _description, 256, new Vector2(-1, 60));

        // ── Preview image ─────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextUnformatted("Preview Image (local file path)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##imgpath", ref _imagePath, 512);
        if (!string.IsNullOrEmpty(_imagePath) && !File.Exists(_imagePath))
            DrawWarning("File not found.");

        // ── Glamourer ─────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Glamourer State");

        if (_hasGlamState)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 1f, 0.4f, 1f));
            ImGui.TextUnformatted("✔ State captured");
            ImGui.PopStyleColor();
        }
        else
        {
            DrawWarning("No state captured.");
        }

        if (ImGui.Button("Capture Current Glamourer State"))
        {
            var ok = _wardrobe.CaptureGlamourerState(_target);
            _hasGlamState  = ok;
            _statusMsg     = ok ? "Glamourer state captured!" : "Failed — is Glamourer loaded?";
            _statusIsError = !ok;
        }

        // ── Penumbra ──────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Penumbra Mod");

        // Collection picker
        ImGui.TextUnformatted("Collection");
        ImGui.SetNextItemWidth(-1);
        var collNames = _collections.ToArray();
        if (ImGui.Combo("##coll", ref _selectedCollIdx, collNames, collNames.Length))
            RefreshMods();

        if (ImGui.Button("↻ Refresh Lists"))
        { RefreshCollections(); RefreshMods(); }

        // Mod picker
        ImGui.TextUnformatted("Mod");
        ImGui.SetNextItemWidth(-1);
        var modLabels = _mods.Select(m => m.Name).ToArray();
        ImGui.Combo("##mod", ref _selectedModIdx, modLabels, modLabels.Length);

        // Enabled toggle
        ImGui.Checkbox("Enable mod when wearing", ref _modEnabled);

        // Capture settings
        if (ImGui.Button("Capture Current Mod Settings"))
        {
            SyncModSelectionToTarget();
            var ok = _wardrobe.CaptureModSettings(_target);
            _statusMsg     = ok ? $"Captured {_target.ModSettings.Count} option groups." : "Failed — check mod selection.";
            _statusIsError = !ok;
        }

        if (_target.ModSettings.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 1f, 0.4f, 1f));
            ImGui.TextUnformatted($"✔ {_target.ModSettings.Count} group(s) saved");
            ImGui.PopStyleColor();

            if (ImGui.TreeNode("Saved settings"))
            {
                foreach (var (group, option) in _target.ModSettings)
                    ImGui.TextUnformatted($"  {group}: {option}");
                ImGui.TreePop();
            }
        }

        // ── Status / Save / Cancel ────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();

        if (_statusMsg != null)
        {
            var col = _statusIsError
                ? new Vector4(1f, 0.3f, 0.3f, 1f)
                : new Vector4(0.3f, 1f, 0.3f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, col);
            ImGui.TextWrapped(_statusMsg);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        var btnW = (ImGui.GetContentRegionAvail().X - 8) / 2;

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.15f, 0.4f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.6f, 0.2f, 1f));
        if (ImGui.Button("Save", new Vector2(btnW, 0)))
            Save();
        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.4f, 0.1f, 0.1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.6f, 0.2f, 0.2f, 1f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0)))
            IsOpen = false;
        ImGui.PopStyleColor(2);

        // Delete (edit only)
        if (!_isNew)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.5f, 0.05f, 0.05f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.1f, 0.1f, 1f));
            if (ImGui.Button("Delete Outfit", new Vector2(-1, 0)))
                DeleteOutfit();
            ImGui.PopStyleColor(2);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void DrawWarning(string msg)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0.2f, 1f));
        ImGui.TextUnformatted("⚠ " + msg);
        ImGui.PopStyleColor();
    }

    private void RefreshCollections()
    {
        _collections = Plugin.Penumbra.GetCollections();
        if (_selectedCollIdx >= _collections.Count)
            _selectedCollIdx = 0;
    }

    private void RefreshMods()
    {
        _mods = Plugin.Penumbra.GetMods();
        _selectedModIdx = -1;
    }

    private void SyncModSelectionToTarget()
    {
        if (_target == null) return;

        _target.PenumbraCollection =
            _selectedCollIdx >= 0 && _selectedCollIdx < _collections.Count
                ? _collections[_selectedCollIdx]
                : string.Empty;

        if (_selectedModIdx >= 0 && _selectedModIdx < _mods.Count)
        {
            _target.PenumbraModDirectory = _mods[_selectedModIdx].Dir;
        }
        _target.ModEnabled = _modEnabled;
    }

    private void Save()
    {
        if (_target == null) return;

        _target.Name        = _name.Trim();
        _target.Description = _description.Trim();
        _target.ImagePath   = string.IsNullOrWhiteSpace(_imagePath) ? null : _imagePath.Trim();
        _target.ModEnabled  = _modEnabled;
        SyncModSelectionToTarget();

        if (string.IsNullOrEmpty(_target.Name))
        {
            _statusMsg     = "Name cannot be empty.";
            _statusIsError = true;
            return;
        }

        if (_isNew)
            _config.Outfits.Add(_target);

        _config.Save();
        IsOpen     = false;
        _statusMsg = null;
    }

    private void DeleteOutfit()
    {
        if (_target == null) return;

        // If this is the currently worn outfit, unequip first
        if (_config.CurrentlyWornOutfitId == _target.Id)
            _wardrobe.UnequipCurrentOutfit();

        _config.Outfits.Remove(_target);
        _config.Save();
        IsOpen = false;
    }

    public void Dispose() { }
}
