using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace WardrobePlugin.Ipc;

public class PenumbraIpc : IDisposable
{
    // Penumbra.Api.Enums.PenumbraApiEc — verified against live logs, where Penumbra reported
    // "returned NothingChanged" for a call that handed us 1.
    private const int EcSuccess        = 0;
    private const int EcNothingChanged = 1;

    private readonly IPluginLog _log;

    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getCollections;
    private readonly ICallGateSubscriber<Dictionary<string, string>> _getModList;

    private readonly ICallGateSubscriber<Guid, string, string, bool,
        (int, (bool, int, Dictionary<string, List<string>>, bool)?)> _getCurrentModSettings;

    private readonly ICallGateSubscriber<Guid, string, string, bool, int> _setMod;

    // Singular: sets a group to ONE option. Correct for Single-select groups only.
    private readonly ICallGateSubscriber<Guid, string, string, string, string, int> _setModSetting;

    // Plural: sets a group's ENTIRE selection at once. Required for Multi-select groups.
    private readonly ICallGateSubscriber<Guid, string, string, string, IReadOnlyList<string>, int> _setModSettings;

    // GetModDirectory() → the Penumbra mods root directory on disk (no args)
    private readonly ICallGateSubscriber<string> _getModDirectory;

    // ApiVersion.V5() → (major, minor). Throws if Penumbra is not loaded.
    private readonly ICallGateSubscriber<(int Major, int Minor)> _apiVersion;

    // RedrawObject.V5(int gameObjectIndex, int redrawType) → void  (index 0 = local player)
    private readonly ICallGateSubscriber<int, int, object?> _redrawObject;

    // GameObjectRedrawn.V3: (nint address, int objectIndex) — fired after async reload completes
    private readonly ICallGateSubscriber<nint, int, object?> _gameObjectRedrawn;

    /// <summary>Fires whenever Penumbra finishes redrawing a game object. Arg = game object index (0 = local player).</summary>
    public event Action<int>? GameObjectRedrawn;

    public PenumbraIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;

        _redrawObject = pi.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");

        _gameObjectRedrawn = pi.GetIpcSubscriber<nint, int, object?>("Penumbra.GameObjectRedrawn.V3");
        _gameObjectRedrawn.Subscribe(OnGameObjectRedrawn);

        _getCollections = pi.GetIpcSubscriber<Dictionary<Guid, string>>(
            "Penumbra.GetCollections.V5");

        _getModList = pi.GetIpcSubscriber<Dictionary<string, string>>(
            "Penumbra.GetModList");

        _getCurrentModSettings = pi.GetIpcSubscriber<Guid, string, string, bool,
            (int, (bool, int, Dictionary<string, List<string>>, bool)?)>(
            "Penumbra.GetCurrentModSettings.V5");

        _setMod = pi.GetIpcSubscriber<Guid, string, string, bool, int>(
            "Penumbra.TrySetMod.V5");

        _setModSetting = pi.GetIpcSubscriber<Guid, string, string, string, string, int>(
            "Penumbra.TrySetModSetting.V5");

        _setModSettings = pi.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>(
            "Penumbra.TrySetModSettings.V5");

        _getModDirectory = pi.GetIpcSubscriber<string>(
            "Penumbra.GetModDirectory");

        _apiVersion = pi.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether Penumbra is loaded and responding, with its version for display.
    /// </summary>
    /// <remarks>
    /// Asking for the API version is the cheapest reliable probe: the call throws when the plugin
    /// is absent, rather than returning something that could be mistaken for real data.
    /// </remarks>
    public (bool Available, string Version) CheckAvailable()
    {
        try
        {
            var (major, minor) = _apiVersion.InvokeFunc();
            return (true, $"{major}.{minor}");
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    public IList<string> GetCollections()
    {
        try { return _getCollections.InvokeFunc().Values.ToList(); }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra GetCollections failed"); return Array.Empty<string>(); }
    }

    public IList<(string ModDirectory, string ModName)> GetMods()
    {
        try
        {
            return _getModList.InvokeFunc()
                .Select(kvp => (kvp.Key, kvp.Value))
                .OrderBy(m => m.Value)
                .ToList();
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra GetModList failed"); return Array.Empty<(string, string)>(); }
    }

    /// <summary>Returns the absolute filesystem path to a specific mod's folder on disk.</summary>
    public string? GetModFolderPath(string modDirectory)
    {
        try
        {
            var root = _getModDirectory.InvokeFunc();
            if (string.IsNullOrEmpty(root)) return null;
            return System.IO.Path.Combine(root, modDirectory);
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra GetModDirectory failed"); return null; }
    }

    public bool IsModEnabled(string collectionName, string modDirectory, string modName)
    {
        try
        {
            if (!TryGetCollectionGuid(collectionName, out var id)) return false;
            var (ec, inner) = _getCurrentModSettings.InvokeFunc(id, modDirectory, modName, false);
            if (ec != 0 || !inner.HasValue) return false;
            var (enabled, _, _, _) = inner.Value;
            return enabled;
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] IsModEnabled query failed"); return false; }
    }

    public Dictionary<string, string> GetModSettings(string collectionName, string modDirectory, string modName)
    {
        try
        {
            if (!TryGetCollectionGuid(collectionName, out var id)) return new();
            var (ec, inner) = _getCurrentModSettings.InvokeFunc(id, modDirectory, modName, false);
            if (ec != 0 || !inner.HasValue) return new();
            var (_, _, settings, _) = inner.Value;
            return settings.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.FirstOrDefault() ?? string.Empty);
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra GetModSettings failed"); return new(); }
    }

    /// <summary>
    /// Returns true when every required option (single and multi-select) is currently active for
    /// the mod, or when both dictionaries are empty.
    /// </summary>
    public bool ActiveOptionsMatch(string collectionName, string modDirectory, string modName,
        Dictionary<string, string> requiredOptions,
        Dictionary<string, List<string>>? requiredMultiOptions = null)
    {
        if (requiredOptions.Count == 0 && (requiredMultiOptions == null || requiredMultiOptions.Count == 0))
            return true;
        try
        {
            if (!TryGetCollectionGuid(collectionName, out var id)) return true;
            var (ec, inner) = _getCurrentModSettings.InvokeFunc(id, modDirectory, modName, false);
            if (ec != 0 || !inner.HasValue) return true;
            var (_, _, activeSettings, _) = inner.Value;

            foreach (var (group, required) in requiredOptions)
            {
                if (!activeSettings.TryGetValue(group, out var active))
                {
                    _log.Debug($"[Wardrobe] ActiveOptionsMatch: group '{group}' not found in Penumbra active settings for '{modName}'");
                    return false;
                }
                if (!active.Contains(required, StringComparer.OrdinalIgnoreCase))
                {
                    _log.Debug($"[Wardrobe] ActiveOptionsMatch: '{modName}' group '{group}' — want '{required}', Penumbra has [{string.Join(", ", active)}]");
                    return false;
                }
            }

            if (requiredMultiOptions != null)
            {
                foreach (var (group, requiredList) in requiredMultiOptions)
                {
                    if (!activeSettings.TryGetValue(group, out var active))
                    {
                        _log.Debug($"[Wardrobe] ActiveOptionsMatch: multi-group '{group}' not found in Penumbra active settings for '{modName}'");
                        return false;
                    }
                    foreach (var req in requiredList)
                        if (!active.Contains(req, StringComparer.OrdinalIgnoreCase))
                        {
                            _log.Debug($"[Wardrobe] ActiveOptionsMatch: '{modName}' multi-group '{group}' — want '{req}', Penumbra has [{string.Join(", ", active)}]");
                            return false;
                        }
                }
            }

            return true;
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] ActiveOptionsMatch query failed"); return true; }
    }

    public bool SetModEnabled(string collectionName, string modDirectory, string modName, bool enabled)
    {
        try
        {
            if (!TryGetCollectionGuid(collectionName, out var id)) return false;
            var ec = _setMod.InvokeFunc(id, modDirectory, modName, enabled);
            if (ec != EcSuccess && ec != EcNothingChanged)
                _log.Warning($"[Wardrobe] TrySetMod.V5 returned ec={ec} for '{modName}' enabled={enabled} in '{collectionName}'");
            else if (ec == EcNothingChanged)
                _log.Debug($"[Wardrobe] TrySetMod.V5: '{modName}' already {(enabled ? "enabled" : "disabled")} in '{collectionName}'");
            return ec == EcSuccess || ec == EcNothingChanged;
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra SetModEnabled failed"); return false; }
    }

    public void ApplyModSettings(string collectionName, string modDirectory, string modName,
        Dictionary<string, string> settings)
    {
        if (!TryGetCollectionGuid(collectionName, out var id)) return;
        foreach (var (group, option) in settings)
        {
            try { _setModSetting.InvokeFunc(id, modDirectory, modName, group, option); }
            catch (Exception ex) { _log.Warning(ex, $"[Wardrobe] SetModSetting failed for group '{group}'"); }
        }
    }

    /// <summary>
    /// Applies multi-select (checkbox) group options, setting each group's full selection in one call.
    /// </summary>
    /// <remarks>
    /// Must use TrySetModSettings.V5 (plural). The singular TrySetModSetting.V5 sets a group
    /// *to* the option passed — for a Multi group that replaces the whole selection rather than
    /// adding to it. Applying ["Top", "Choker"] one option at a time therefore leaves only the
    /// last one checked, and because the prior state was read once up front, successive wears
    /// oscillated between {Top} and {Choker} instead of converging on both.
    /// The plural call is idempotent: it returns NothingChanged when the selection already matches.
    /// </remarks>
    public void ApplyMultiModSettings(string collectionName, string modDirectory, string modName,
        Dictionary<string, List<string>> multiSettings)
    {
        if (multiSettings.Count == 0) return;
        if (!TryGetCollectionGuid(collectionName, out var id)) return;

        foreach (var (group, wantedOptions) in multiSettings)
        {
            try
            {
                var ec = _setModSettings.InvokeFunc(id, modDirectory, modName, group, wantedOptions);
                if (ec != EcSuccess && ec != EcNothingChanged)
                    _log.Warning($"[Wardrobe] TrySetModSettings.V5 ec={ec} for '{modName}' group '{group}' → [{string.Join(", ", wantedOptions)}]");
                else
                    _log.Debug($"[Wardrobe] TrySetModSettings.V5 '{modName}' group '{group}' → [{string.Join(", ", wantedOptions)}] ec={ec}");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"[Wardrobe] TrySetModSettings.V5 failed for '{modName}' group '{group}'");
            }
        }
    }

    /// <summary>Tells Penumbra to redraw the local player character (game object index 0).</summary>
    public void RedrawPlayer()
    {
        try { _redrawObject.InvokeAction(0, 0); } // 0 = player index, 0 = RedrawType.Redraw
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra RedrawObject failed"); }
    }

    public void Dispose()
    {
        _gameObjectRedrawn.Unsubscribe(OnGameObjectRedrawn);
    }

    private void OnGameObjectRedrawn(nint address, int objectIndex)
        => GameObjectRedrawn?.Invoke(objectIndex);

    private bool TryGetCollectionGuid(string collectionName, out Guid id)
    {
        id = Guid.Empty;
        try
        {
            foreach (var kvp in _getCollections.InvokeFunc())
            {
                if (kvp.Value.Equals(collectionName, StringComparison.OrdinalIgnoreCase))
                {
                    id = kvp.Key;
                    return true;
                }
            }
            _log.Warning($"[Wardrobe] Collection '{collectionName}' not found");
            return false;
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] TryGetCollectionGuid failed"); return false; }
    }
}
