using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>One worn mod that shares files with another worn mod, from the first mod's side.</summary>
/// <param name="OwnDirectory">This mod's folder.</param>
/// <param name="OwnName">This mod's display name.</param>
/// <param name="RivalDirectory">The other mod's folder.</param>
/// <param name="RivalName">The other mod's display name.</param>
/// <param name="Shared">Game paths both mods ship that Penumbra is currently resolving to one of them.</param>
/// <param name="Won">Of those, how many resolve to this mod.</param>
/// <param name="Priority">This mod's Penumbra priority, when it could be read.</param>
/// <param name="RivalPriority">The other mod's, likewise.</param>
public record ModConflict(string OwnDirectory, string OwnName, string RivalDirectory, string RivalName, int Shared, int Won,
                          int? Priority, int? RivalPriority)
{
    public int Lost => Shared - Won;

    /// <summary>
    /// Whether the two priorities already differ in Penumbra — somebody has ordered them, so there
    /// is nothing to ask.
    /// </summary>
    public bool Settled => Priority.HasValue && RivalPriority.HasValue && Priority != RivalPriority;
}

/// <summary>
/// Finds worn mods that are fighting over the same game paths, so a conflict can be seen rather
/// than guessed at (#35).
/// </summary>
/// <remarks>
/// The wardrobe splits one Penumbra mod into a card per piece, and two cards from different mods
/// that start from the same base item contest its files; Penumbra's priority then decides which mod
/// is shown, silently. Putting the user's choice on top (<see cref="Configuration.ModPriorityChoices"/>)
/// is the fix; this is how to tell there was something to fix, or that it did not take.
/// <para>
/// Two sources, because neither is enough alone. A mod's folder lists every path it <i>could</i>
/// change, option or not, so it finds the overlap but says nothing of who is winning. What Penumbra
/// is resolving for the character (<see cref="PenumbraIpc.GetResolvedGamePaths"/>) names the winner
/// of each path but never the loser. A path counts as contested when both mods ship it and the
/// resolved file came from one of the two. A mod shipping a path inside an option that is not
/// selected can still be counted, so this over-reports rather than under-reports, and the wording
/// where it is shown says "may".
/// </para>
/// <para>
/// Nothing runs on a timer. A reading is taken only after the wardrobe changes or the character is
/// redrawn — the only things that can move a winner — and only once the change has been quiet for a
/// moment, so a run of wears costs one reading. With fewer than two worn mods there is nothing to
/// contest and Penumbra is never asked anything. The draw thread only ever does a dictionary lookup.
/// </para>
/// </remarks>
public class ModConflictService
{
    private readonly PenumbraIpc _penumbra;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly ModAnalysisService _analysis;

    /// <summary>How long a change has to sit before it is read, so Penumbra's reload can finish.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private DateTime _dueAt = DateTime.MinValue;

    /// <summary>A mod's folder listing is only reread when its meta.json has changed.</summary>
    private readonly ConcurrentDictionary<string, (DateTime Stamp, HashSet<string> Paths)> _pathCache =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<ModConflict>> _byMod = new(StringComparer.OrdinalIgnoreCase);

    private int      _refreshing;

    private readonly ConcurrentDictionary<Guid, IReadOnlyList<(string RivalName, int Shared)>?> _previews = new();
    private readonly ConcurrentDictionary<Guid, byte> _previewing = new();
    private string _root = string.Empty;
    private bool     _stale = true;

    public ModConflictService(PenumbraIpc penumbra, IFramework framework, IPluginLog log,
        Configuration config, ModAnalysisService analysis)
    {
        _penumbra  = penumbra;
        _framework = framework;
        _log       = log;
        _config    = config;
        _analysis  = analysis;

        _penumbra.GameObjectRedrawn += idx => { if (idx == 0) Invalidate(); };
    }

    /// <summary>Marks the last reading out of date, so the next ask starts a new one.</summary>
    public void Invalidate()
    {
        _previews.Clear();
        _dueAt = DateTime.UtcNow + Settle;
        _stale = true;
    }

    /// <summary>One entry per conflicting pair of worn mods, whichever side asks.</summary>
    public IReadOnlyList<ModConflict> Pairs()
    {
        Touch();

        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModConflict>();
        foreach (var list in _byMod.Values)
            foreach (var c in list)
            {
                var key = string.Compare(c.OwnDirectory, c.RivalDirectory, StringComparison.OrdinalIgnoreCase) < 0
                    ? c.OwnDirectory + "|" + c.RivalDirectory
                    : c.RivalDirectory + "|" + c.OwnDirectory;
                if (seen.Add(key)) result.Add(c);
            }

        return result;
    }

    /// <summary>
    /// The worn mods this item's mods are contesting files with. Empty when there are none or no
    /// reading has finished yet.
    /// </summary>
    public IReadOnlyList<ModConflict> For(WardrobeItem item)
    {
        Touch();

        var byMod = _byMod;
        var found = new List<ModConflict>();
        foreach (var mod in item.Mods)
        {
            if (string.IsNullOrEmpty(mod.ModDirectory)) continue;
            if (byMod.TryGetValue(mod.ModDirectory, out var list)) found.AddRange(list);
        }

        return found;
    }

    /// <summary>
    /// The worn mods an item that is not worn would share files with if it were put on, or
    /// <c>null</c> while that is still being worked out.
    /// </summary>
    /// <remarks>
    /// Asked for one item, on hover, so the cost is that mod's file list against the few worn ones
    /// rather than the library against itself. It can only say how many files are shared — nothing
    /// is resolved yet, so who would win is not knowable. Answers are dropped when the worn set
    /// changes.
    /// </remarks>
    public IReadOnlyList<(string RivalName, int Shared)>? Preview(WardrobeItem item)
    {
        if (_previews.TryGetValue(item.Id, out var done)) return done;
        if (!_previewing.TryAdd(item.Id, 0)) return null;

        if (string.IsNullOrEmpty(_root)) _root = _penumbra.GetModRoot() ?? string.Empty;
        var root = _root;
        var own  = item.Mods.Where(m => !string.IsNullOrEmpty(m.ModDirectory))
                           .Select(m => m.ModDirectory).ToList();
        var worn = new List<(string Directory, string Name)>();
        foreach (var other in _config.WardrobeItems)
        {
            if (other.Id == item.Id || !_config.WornItems.ContainsValue(other.Id)) continue;
            foreach (var m in other.Mods)
                if (!string.IsNullOrEmpty(m.ModDirectory) && !own.Contains(m.ModDirectory, StringComparer.OrdinalIgnoreCase)
                    && !worn.Any(w => string.Equals(w.Directory, m.ModDirectory, StringComparison.OrdinalIgnoreCase)))
                    worn.Add((m.ModDirectory, m.ModName));
        }

        var id = item.Id;
        _ = Task.Run(() =>
        {
            try
            {
                var hits = new List<(string, int)>();
                if (root.Length > 0 && own.Count > 0)
                {
                    var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in own) mine.UnionWith(PathsOf(root, d));

                    foreach (var (dir, name) in worn)
                    {
                        var shared = PathsOf(root, dir).Count(mine.Contains);
                        if (shared > 0) hits.Add((name, shared));
                    }
                }

                _previews[id] = hits;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[Wardrobe] Conflict preview failed");
                _previews[id] = new List<(string, int)>();
            }
            finally { _previewing.TryRemove(id, out _); }
        });

        return null;
    }

    private void Touch()
    {
        if (_stale && _refreshing == 0 && DateTime.UtcNow >= _dueAt) BeginRefresh();
    }

    private void BeginRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _stale = false;

        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await _framework.RunOnFrameworkThread(Snapshot).ConfigureAwait(false);
                if (snapshot == null) { _stale = true; _dueAt = DateTime.UtcNow + Settle; return; }

                _byMod = Compare(snapshot);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[Wardrobe] Mod conflict refresh failed");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private record WornMod(string Directory, string Name, int? Priority, string Collection);

    private record Reading(string Root, Dictionary<string, string> Resolved, List<WornMod> Worn);

    /// <summary>Everything that has to be asked of Penumbra or the config, on the framework thread.</summary>
    private Reading? Snapshot()
    {
        var worn = new List<WornMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _config.WardrobeItems)
        {
            if (!_config.WornItems.ContainsValue(item.Id)) continue;
            foreach (var m in item.Mods)
            {
                if (string.IsNullOrEmpty(m.ModDirectory) || !seen.Add(m.ModDirectory)) continue;
                worn.Add(new WornMod(m.ModDirectory, m.ModName, null, m.Collection));
            }
        }

        // Nothing to contest: skip every Penumbra call
        if (worn.Count < 2) return new Reading(string.Empty, new(), worn);

        var root     = _penumbra.GetModRoot();
        var resolved = _penumbra.GetResolvedGamePaths();
        // Penumbra not ready, or resolving failed mid-redraw: keep the last answer and retry
        if (string.IsNullOrEmpty(root) || resolved == null) return null;

        for (var i = 0; i < worn.Count; i++)
            worn[i] = worn[i] with
            {
                Priority = _penumbra.GetModPriority(worn[i].Collection, worn[i].Directory, worn[i].Name)
            };

        return new Reading(root!, resolved, worn);
    }

    private Dictionary<string, List<ModConflict>> Compare(Reading r)
    {
        var result = new Dictionary<string, List<ModConflict>>(StringComparer.OrdinalIgnoreCase);
        if (r.Worn.Count < 2) return result;

        var paths = r.Worn.ToDictionary(w => w.Directory, w => PathsOf(r.Root, w.Directory),
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < r.Worn.Count; i++)
        for (var j = i + 1; j < r.Worn.Count; j++)
        {
            var a = r.Worn[i];
            var b = r.Worn[j];

            int shared = 0, aWon = 0;
            foreach (var p in paths[a.Directory])
            {
                if (!paths[b.Directory].Contains(p)) continue;
                if (!r.Resolved.TryGetValue(p, out var file)) continue;
                var who = ModDirectoryOf(file, r.Root);
                if (who == null) continue;

                var isA = string.Equals(who, a.Directory, StringComparison.OrdinalIgnoreCase);
                var isB = string.Equals(who, b.Directory, StringComparison.OrdinalIgnoreCase);
                if (!isA && !isB) continue;

                shared++;
                if (isA) aWon++;
            }

            if (shared == 0) continue;

            Add(result, a.Directory, new ModConflict(a.Directory, a.Name, b.Directory, b.Name, shared, aWon, a.Priority, b.Priority));
            Add(result, b.Directory, new ModConflict(b.Directory, b.Name, a.Directory, a.Name, shared, shared - aWon, b.Priority, a.Priority));
        }

        return result;
    }

    private static void Add(Dictionary<string, List<ModConflict>> map, string key, ModConflict conflict)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<ModConflict>();
        list.Add(conflict);
    }

    private HashSet<string> PathsOf(string root, string directory)
    {
        var folder = Path.Combine(root, directory);
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(Path.Combine(folder, "meta.json")); }
        catch { stamp = DateTime.MinValue; }

        if (_pathCache.TryGetValue(directory, out var cached) && cached.Stamp == stamp)
            return cached.Paths;

        var paths = _analysis.GamePaths(folder);
        _pathCache[directory] = (stamp, paths);
        return paths;
    }

    private static string? ModDirectoryOf(string path, string root)
    {
        string relative;
        try { relative = Path.GetRelativePath(root, path); }
        catch { return null; }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        var cut = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return cut <= 0 ? null : relative[..cut];
    }
}
