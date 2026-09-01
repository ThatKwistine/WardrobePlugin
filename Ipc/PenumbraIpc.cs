using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly Configuration _config;

    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getCollections;

    // GetCollectionForObject.V5(int gameObjectIdx) →
    //   (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)
    // The effective collection is the one Penumbra actually applies to that object, whether it got
    // there from an Individual Assignment, Your Character, or the default.
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _getCollectionForObject;
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

    // GetGameObjectResourcePaths.V5(ushort[] objectIndices) → one dictionary per index, mapping the
    // ACTUAL path of every resource in play to the game paths it stands in for. A redirected file
    // gives its path on disk; anything vanilla or swapped gives a game path instead.
    private readonly ICallGateSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>
        _getGameObjectResourcePaths;

    // ApiVersion.V5() → (major, minor). Throws if Penumbra is not loaded.
    private readonly ICallGateSubscriber<(int Major, int Minor)> _apiVersion;

    // RedrawObject.V5(int gameObjectIndex, int redrawType) → void  (index 0 = local player)
    private readonly ICallGateSubscriber<int, int, object?> _redrawObject;

    // GameObjectRedrawn.V3: (nint address, int objectIndex) — fired after async reload completes
    private readonly ICallGateSubscriber<nint, int, object?> _gameObjectRedrawn;

    /// <summary>Fires whenever Penumbra finishes redrawing a game object. Arg = game object index (0 = local player).</summary>
    public event Action<int>? GameObjectRedrawn;

    /// <summary>How long <see cref="GetActiveCollection"/> reuses its last answer.</summary>
    private static readonly TimeSpan ActiveCollectionTtl = TimeSpan.FromSeconds(1);

    private string   _activeCollection   = string.Empty;
    private DateTime _activeCollectionAt = DateTime.MinValue;
    private bool     _activeCollectionFailed;

    public PenumbraIpc(IDalamudPluginInterface pi, IPluginLog log, Configuration config)
    {
        _log    = log;
        _config = config;

        _redrawObject = pi.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");

        _gameObjectRedrawn = pi.GetIpcSubscriber<nint, int, object?>("Penumbra.GameObjectRedrawn.V3");
        _gameObjectRedrawn.Subscribe(OnGameObjectRedrawn);

        _getCollections = pi.GetIpcSubscriber<Dictionary<Guid, string>>(
            "Penumbra.GetCollections.V5");

        _getCollectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>(
            "Penumbra.GetCollectionForObject.V5");

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

        _getGameObjectResourcePaths =
            pi.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>(
                "Penumbra.GetGameObjectResourcePaths.V5");

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

    /// <summary>
    /// The collection Penumbra is applying to your character right now, or empty when it cannot say.
    /// </summary>
    /// <remarks>
    /// Empty covers everything that is not a real answer — nobody logged in, Penumbra absent, or an
    /// object it cannot identify — and every caller treats that as "no opinion" rather than as a
    /// collection name. The empty collection counts as no answer too: it exists in Penumbra as a
    /// deliberate nothing, is not in <see cref="GetCollections"/>, and enabling a mod in it would be
    /// a write that silently goes nowhere.
    /// <para>
    /// Answers are held for <see cref="ActiveCollectionTtl"/> because the wardrobe asks per mod
    /// operation and the settings panel asks per frame, while the answer only changes when you
    /// change character. A second of staleness costs nothing: a character swap has a loading screen
    /// in it, and every path that acts on this is a click that comes long after.
    /// </para>
    /// </remarks>
    public string GetActiveCollection()
    {
        if (DateTime.UtcNow - _activeCollectionAt < ActiveCollectionTtl) return _activeCollection;

        var name = string.Empty;
        try
        {
            var (objectValid, _, effective) = _getCollectionForObject.InvokeFunc(0); // 0 = local player
            var (id, collectionName) = effective;
            if (objectValid && id != Guid.Empty) name = collectionName ?? string.Empty;
            _activeCollectionFailed = false;
        }
        catch (Exception ex)
        {
            // Once per run of failures, not once per second: this is asked repeatedly, and a
            // Penumbra that is not there fails every single time it is
            if (!_activeCollectionFailed)
            {
                _activeCollectionFailed = true;
                _log.Warning(ex, "[Wardrobe] Penumbra GetCollectionForObject failed — items fall " +
                                 "back to the collection saved on them");
            }
        }

        if (!name.Equals(_activeCollection, StringComparison.Ordinal))
            _log.Debug($"[Wardrobe] Active collection is now '{(name.Length == 0 ? "(none)" : name)}'");

        _activeCollection   = name;
        _activeCollectionAt = DateTime.UtcNow;
        return name;
    }

    /// <summary>
    /// The collection a saved one should actually be read from and written to.
    /// </summary>
    /// <remarks>
    /// With <see cref="Configuration.FollowActiveCollection"/> off this is the saved name, unchanged,
    /// which is how the wardrobe has always worked. With it on the saved name is only a fallback for
    /// when <see cref="GetActiveCollection"/> has no answer, so a wardrobe built on one character
    /// applies to whichever one you are on.
    /// </remarks>
    public string ResolveCollection(string savedCollection)
    {
        if (!_config.FollowActiveCollection) return savedCollection;

        var active = GetActiveCollection();
        return string.IsNullOrEmpty(active) ? savedCollection : active;
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

    /// <summary>
    /// Every mod, newest first, by when its folder appeared on disk.
    /// </summary>
    /// <remarks>
    /// Penumbra records a real import date per mod, but keeps it in its own <c>mod_data.db</c> and
    /// exposes nothing for it — <see cref="GetMods"/>'s IPC hands over directory and name and no more.
    /// Reading another plugin's private database would be a dependency on a format nobody promised to
    /// keep, so the folder's creation time stands in for it: Penumbra makes that folder when it imports
    /// the mod, so the two agree for anything installed normally.
    /// <para>
    /// Where they part company is a mod folder that was copied rather than imported — moving a mod
    /// library to another drive gives every folder the date of the copy. That makes this a convenience
    /// for finding what you just installed, not a record of when you got it, which is why the wardrobe
    /// offers it as one sort order rather than as a date shown anywhere.
    /// </para>
    /// <para>
    /// Costs one filesystem stat per mod, so it is called when a picker opens rather than per frame.
    /// Anything that cannot be read sorts as oldest instead of dropping out of the list.
    /// </para>
    /// </remarks>
    public IList<(string ModDirectory, string ModName)> GetModsByInstalled()
    {
        var mods = GetMods();

        string? root = null;
        try { root = _getModDirectory.InvokeFunc(); }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra GetModDirectory failed"); }

        if (string.IsNullOrEmpty(root)) return mods;

        DateTime Installed((string Dir, string Name) mod)
        {
            try
            {
                var path = System.IO.Path.Combine(root, mod.Dir);
                return System.IO.Directory.Exists(path)
                    ? System.IO.Directory.GetCreationTimeUtc(path)
                    : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        return mods
            .Select(m => (Mod: m, At: Installed(m)))
            .OrderByDescending(x => x.At)
            .ThenBy(x => x.Mod.ModName, Services.NaturalOrder.Comparer)
            .Select(x => x.Mod)
            .ToList();
    }

    /// <summary>The Penumbra mods root directory on disk, or <c>null</c> if it cannot be read.</summary>
    public string? GetModRoot()
    {
        try
        {
            var root = _getModDirectory.InvokeFunc();
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra GetModDirectory failed"); return null; }
    }

    /// <summary>
    /// Every file Penumbra is actually feeding a character, as absolute paths on disk.
    /// </summary>
    /// <remarks>
    /// The difference between what a mod folder contains and what a character is wearing. A mod ships
    /// every option it has — all the colours, all the variants — and Penumbra hands over only the ones
    /// selected, so reading the folder answers a different and much larger question than "what is on
    /// this character". Anything vanilla or file-swapped comes back as a game path rather than a path
    /// on disk and is dropped here: nothing is redirecting it, so there is no mod file behind it.
    /// <para>
    /// Penumbra's own note is that this is best called just after a redraw, since it can fail to
    /// resolve paths when mod settings have moved since. Callers should treat a short answer as
    /// "ask again later" rather than as "nothing is there".
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string>? GetResolvedFilePaths(ushort objectIndex = 0)
    {
        try
        {
            var result = _getGameObjectResourcePaths.InvokeFunc([objectIndex]);
            var paths  = result is { Length: > 0 } ? result[0] : null;
            if (paths == null) return null;

            var files = new List<string>(paths.Count);
            foreach (var actual in paths.Keys)
            {
                if (string.IsNullOrEmpty(actual)) continue;
                // Game paths are relative; only a rooted path names a file a mod actually supplied
                if (!Path.IsPathRooted(actual)) continue;
                files.Add(actual);
            }

            return files;
        }
        catch (Exception ex)
        {
            _log.Debug($"[Wardrobe] Penumbra GetGameObjectResourcePaths failed: {ex.Message}");
            return null;
        }
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
        => IsModEnabledCore(collectionName, modDirectory, modName, resolve: true);

    /// <summary>
    /// Whether a mod is enabled in exactly the collection named, whatever collection is being
    /// followed.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="SetModEnabledIn"/> for the cleanup offered after the character's
    /// collection changes, which is by definition about a collection the character has left. These
    /// two are the only calls here that deliberately look somewhere other than where the wardrobe
    /// is currently writing, and both are reached from a button the user pressed.
    /// </remarks>
    public bool IsModEnabledIn(string collectionName, string modDirectory, string modName)
        => IsModEnabledCore(collectionName, modDirectory, modName, resolve: false);

    private bool IsModEnabledCore(string collectionName, string modDirectory, string modName, bool resolve)
    {
        try
        {
            if (!TryGetCollectionGuid(collectionName, out var id, resolve)) return false;
            var (ec, inner) = _getCurrentModSettings.InvokeFunc(id, modDirectory, modName, false);
            if (ec != 0 || !inner.HasValue) return false;
            var (enabled, _, _, _) = inner.Value;
            return enabled;
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] IsModEnabled query failed"); return false; }
    }

    /// <summary>
    /// Every option Penumbra currently has selected for a mod, as group name → selected options.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="GetModSettings"/>, which reduces each group to its first option
    /// and so cannot describe a multi-select group with more than one box ticked. Anything seeding
    /// an editable option set needs the whole selection, not a representative of it.
    /// </remarks>
    public Dictionary<string, List<string>> GetModSettingsFull(
        string collectionName, string modDirectory, string modName)
    {
        try
        {
            if (!TryGetCollectionGuid(collectionName, out var id)) return new();
            var (ec, inner) = _getCurrentModSettings.InvokeFunc(id, modDirectory, modName, false);
            if (ec != 0 || !inner.HasValue) return new();
            var (_, _, settings, _) = inner.Value;
            return settings.ToDictionary(kvp => kvp.Key, kvp => new List<string>(kvp.Value));
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Penumbra GetModSettingsFull failed"); return new(); }
    }

    public Dictionary<string, string> GetModSettings(string collectionName, string modDirectory, string modName) =>
        GetModSettingsFull(collectionName, modDirectory, modName)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.FirstOrDefault() ?? string.Empty);

    /// <summary>
    /// Returns true when every required option (single and multi-select) is currently active for
    /// the mod, or when both dictionaries are empty.
    /// </summary>
    /// <param name="requiredStates">
    /// Tri-states, when the item has them. An option forced on must be on and one forced off must be
    /// off; anything ignored is not evidence either way, so it cannot fail the match. When this is
    /// supplied, <paramref name="requiredMultiOptions"/> is skipped — the two describe the same
    /// groups and the older one would demand an exact selection the tri-states deliberately do not.
    /// </param>
    public bool ActiveOptionsMatch(string collectionName, string modDirectory, string modName,
        Dictionary<string, string> requiredOptions,
        Dictionary<string, List<string>>? requiredMultiOptions = null,
        Dictionary<string, Dictionary<string, bool>>? requiredStates = null)
    {
        var useStates = requiredStates is { Count: > 0 };
        if (useStates) requiredMultiOptions = null;

        if (requiredOptions.Count == 0 && !useStates &&
            (requiredMultiOptions == null || requiredMultiOptions.Count == 0))
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

            if (useStates)
            {
                foreach (var (group, options) in requiredStates!)
                {
                    if (options.Count == 0) continue;

                    // A group Penumbra does not report can still satisfy an item that only asks for
                    // options to be off, so absence is treated as an empty selection rather than a
                    // failure — unlike the exact-selection path above, which needs the group to exist
                    activeSettings.TryGetValue(group, out var active);
                    active ??= new List<string>();

                    foreach (var (option, on) in options)
                    {
                        if (active.Contains(option, StringComparer.OrdinalIgnoreCase) == on) continue;

                        _log.Debug($"[Wardrobe] ActiveOptionsMatch: '{modName}' group '{group}' — want '{option}' " +
                                   $"{(on ? "on" : "off")}, Penumbra has [{string.Join(", ", active)}]");
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] ActiveOptionsMatch query failed"); return true; }
    }

    public bool SetModEnabled(string collectionName, string modDirectory, string modName, bool enabled)
        => SetModEnabledCore(collectionName, modDirectory, modName, enabled, resolve: true);

    /// <inheritdoc cref="IsModEnabledIn"/>
    public bool SetModEnabledIn(string collectionName, string modDirectory, string modName, bool enabled)
        => SetModEnabledCore(collectionName, modDirectory, modName, enabled, resolve: false);

    private bool SetModEnabledCore(string collectionName, string modDirectory, string modName,
        bool enabled, bool resolve)
    {
        try
        {
            if (!TryGetCollectionGuid(collectionName, out var id, resolve)) return false;
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

    /// <summary>
    /// Applies tri-state option settings: forces some options on, some off, and leaves the rest
    /// however they are.
    /// </summary>
    /// <remarks>
    /// Penumbra has no per-option call — <c>TrySetModSettings.V5</c> takes a group's entire
    /// selection — so "leave that one alone" has to be built here: read what is selected now, add
    /// the options this item insists on, remove the ones it insists against, and send the result
    /// back as the whole list. Everything nobody expressed an opinion about goes back exactly as it
    /// came, which is what lets two items from one mod both get their way.
    /// <para>
    /// The read is per apply rather than cached: the current selection is precisely what another
    /// item may have changed a moment ago, and a cached copy would reintroduce the overwrite this
    /// exists to prevent.
    /// </para>
    /// </remarks>
    public void ApplyModOptionStates(string collectionName, string modDirectory, string modName,
        Dictionary<string, Dictionary<string, bool>> states)
    {
        if (states.Count == 0) return;
        if (!TryGetCollectionGuid(collectionName, out var id)) return;

        var current = GetModSettingsFull(collectionName, modDirectory, modName);

        foreach (var (group, options) in states)
        {
            if (options.Count == 0) continue; // every option ignored — nothing to say about this group

            var wanted = current.TryGetValue(group, out var now)
                ? new List<string>(now)
                : new List<string>();

            foreach (var (option, on) in options)
            {
                if (on)
                {
                    if (!wanted.Contains(option, StringComparer.OrdinalIgnoreCase)) wanted.Add(option);
                }
                else
                {
                    wanted.RemoveAll(o => o.Equals(option, StringComparison.OrdinalIgnoreCase));
                }
            }

            try
            {
                var ec = _setModSettings.InvokeFunc(id, modDirectory, modName, group, wanted);
                if (ec != EcSuccess && ec != EcNothingChanged)
                    _log.Warning($"[Wardrobe] TrySetModSettings.V5 ec={ec} for '{modName}' group '{group}' → [{string.Join(", ", wanted)}]");
                else
                    _log.Debug($"[Wardrobe] Option states '{modName}' group '{group}' → [{string.Join(", ", wanted)}] ec={ec}");
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

    /// <summary>
    /// The GUID Penumbra knows a collection by, resolving where the wardrobe should be writing first.
    /// </summary>
    /// <remarks>
    /// Every call that reads or changes mod settings comes through here, which makes it the one place
    /// <see cref="ResolveCollection"/> has to be applied for "follow the active collection" to hold
    /// everywhere — wearing, unwearing, the scan that decides what is on, and the import panel
    /// reading an option set back out. <see cref="GetCollections"/> deliberately does not resolve:
    /// it is the list you pick a collection from, not a use of one.
    /// </remarks>
    private bool TryGetCollectionGuid(string collectionName, out Guid id, bool resolve = true)
    {
        id = Guid.Empty;
        if (resolve) collectionName = ResolveCollection(collectionName);
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
