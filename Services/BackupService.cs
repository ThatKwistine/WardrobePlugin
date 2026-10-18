using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace WardrobePlugin.Services;

/// <summary>One config backup on disk, as the restore picker lists it.</summary>
/// <param name="Stamp">When the copy was made, read from the file name rather than the file's own
/// timestamp — a sync client or a copy to a new machine rewrites the latter.</param>
public sealed record BackupEntry(string Path, DateTime Stamp, long Bytes)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// What a backup holds, so the picker can say so before anything is overwritten. A non-null
/// <see cref="Error"/> means the file is not something the plugin can load.
/// </summary>
public sealed record BackupSummary(int Wardrobes, int Items, int Outfits, string? Error = null)
{
    public bool Usable => Error == null;
}

/// <summary>
/// Copies the plugin config (and the camera preset file, if one is configured) to a user-chosen
/// folder once an hour. A backup is only written when the content has actually changed since the
/// last one, so leaving the game running does not fill the folder with identical copies.
/// </summary>
/// <remarks>
/// Also the way back: a backup is restored by staging a copy of it under the plugin's config
/// folder, which <see cref="ApplyStagedRestore"/> puts in place of the config file the next time the
/// plugin loads. See that method for why it is not swapped in while the plugin is running.
/// </remarks>
public class BackupService : IDisposable
{
    private const string ConfigPrefix = "WardrobePlugin";
    private const string PresetPrefix = "CameraPresets";

    /// <summary>The staged copy, waiting under the config folder for the next load.</summary>
    private const string StagedConfigName = "WardrobePlugin.restore.json";

    /// <summary>
    /// Prefix of the copy taken of the config being replaced. Timestamped, and pruned to the newest
    /// few, so a second restore does not write over the safety net from the first.
    /// </summary>
    private const string PreRestorePrefix = "WardrobePlugin.pre-restore";
    private const int    PreRestoreKeep   = 5;

    private const string StampFormat = "yyyyMMdd-HHmmss";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// How long after construction the hourly check first runs. The plugin's constructor is still
    /// going when the first framework tick arrives — it runs on its own thread — and it saves the
    /// config on the way: a version bump, a migration, the worn list. A backup starting in that
    /// window held the config open for the copy while Dalamud's save tried to replace it underneath,
    /// and the save failed with access denied and took the load down with it. The hour is not
    /// precious; the first minute is.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(1);

    private readonly Configuration _config;
    private readonly IFramework    _framework;
    private readonly IPluginLog    _log;
    private readonly DateTime      _constructedUtc = DateTime.UtcNow;

    private volatile bool _running;

    /// <summary>Human-readable outcome of the last backup attempt, for the settings panel.</summary>
    public string LastResult { get; private set; } = string.Empty;

    /// <summary>
    /// Name of the backup staged this session, for the settings panel to say which one is waiting.
    /// Null when none was staged here — a stage from an earlier session would have been applied at
    /// this load, so that is the same as nothing pending.
    /// </summary>
    public string? StagedFromName { get; private set; }

    /// <summary>Whether a restore is waiting for the next load.</summary>
    public bool RestoreStaged => File.Exists(StagedConfigPath);

    private static string StagedConfigPath =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, StagedConfigName);

    public BackupService(Configuration config, IFramework framework, IPluginLog log)
    {
        _config    = config;
        _framework = framework;
        _log       = log;

        _framework.Update += OnUpdate;
    }

    public void Dispose() => _framework.Update -= OnUpdate;

    private void OnUpdate(IFramework _)
    {
        if (!_config.BackupEnabled || _running) return;
        if (string.IsNullOrWhiteSpace(_config.BackupFolder)) return;

        var now = DateTime.UtcNow;
        if (now - _constructedUtc < StartupGrace) return;
        if (now - _config.LastBackupCheckUtc < Interval) return;

        Run();
    }

    /// <summary>
    /// Runs a backup on a background thread. Safe to call from the UI; does nothing if one is
    /// already in flight. Skips writing when nothing has changed since the previous backup.
    /// </summary>
    public void Run()
    {
        if (_running) return;

        var folder = _config.BackupFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            LastResult = "No backup folder set.";
            return;
        }

        _running = true;

        // Record the attempt up front so a failing backup can't retry every frame. In memory only:
        // that is all the every-frame guard needs, and saving here put an eight-megabyte serialise
        // on the framework thread for the sake of a timestamp the next save carries anyway.
        _config.LastBackupCheckUtc = DateTime.UtcNow;

        var configPath = Plugin.PluginInterface.ConfigFile.FullName;
        var presetPath = _config.CameraPresetsPath;
        var knownHash  = _config.LastBackupHash;
        var keep       = Math.Max(1, _config.BackupKeepCount);

        _ = Task.Run(async () =>
        {
            try
            {
                var sources = new[] { configPath, presetPath }
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .Select(p => (Path: p, Bytes: ReadShared(p)))
                    .ToArray();

                if (sources.Length == 0)
                {
                    LastResult = "Nothing to back up — config file not found.";
                    return;
                }

                var hash = ComputeHash(sources);
                if (hash == knownHash)
                {
                    LastResult = $"No changes since last backup ({DateTime.Now:HH:mm}).";
                    _log.Debug("[Wardrobe] Backup skipped — content unchanged.");
                    return;
                }

                Directory.CreateDirectory(folder);
                var stamp = DateTime.Now.ToString(StampFormat);

                // The bytes that were hashed are the bytes written, so the recorded hash describes
                // the backup rather than whatever the file had become by the time it was copied
                foreach (var (src, bytes) in sources)
                {
                    var prefix = string.Equals(src, configPath, StringComparison.OrdinalIgnoreCase)
                        ? ConfigPrefix : PresetPrefix;
                    await File.WriteAllBytesAsync(Path.Combine(folder, $"{prefix}_{stamp}.json"), bytes);
                }

                Prune(folder, ConfigPrefix, keep);
                Prune(folder, PresetPrefix, keep);

                // Config writes go through the framework thread like every other config mutation
                await _framework.RunOnFrameworkThread(() =>
                {
                    _config.LastBackupHash = hash;
                    _config.Save();
                });

                LastResult = $"Backed up {sources.Length} file(s) at {DateTime.Now:HH:mm}.";
                _log.Information($"[Wardrobe] Backup written to {folder} ({sources.Length} file(s)).");
            }
            catch (Exception ex)
            {
                LastResult = $"Backup failed: {ex.Message}";
                _log.Error(ex, "[Wardrobe] Backup failed");
            }
            finally
            {
                _running = false;
            }
        });
    }

    // ── Restoring ─────────────────────────────────────────────────────────────

    /// <summary>The config backups in the backup folder, newest first. Empty when there is no folder.</summary>
    public IReadOnlyList<BackupEntry> ListConfigBackups()
    {
        var folder = _config.BackupFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Array.Empty<BackupEntry>();

        try
        {
            return new DirectoryInfo(folder)
                .GetFiles($"{ConfigPrefix}_*.json")
                .Select(f => new BackupEntry(f.FullName, StampOf(f), f.Length))
                .OrderByDescending(e => e.Stamp)
                .ToList();
        }
        catch
        {
            return Array.Empty<BackupEntry>();
        }
    }

    /// <summary>The time in a backup's name, falling back to the file's own when the name does not carry one.</summary>
    private static DateTime StampOf(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var cut  = name.IndexOf('_');

        if (cut >= 0 && DateTime.TryParseExact(name[(cut + 1)..], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var stamp))
            return stamp;

        return file.LastWriteTime;
    }

    /// <summary>
    /// Reads enough of a backup to say what is in it. Parses the whole file — megabytes for a full
    /// wardrobe — on the calling thread, so the picker calls it from a background task.
    /// </summary>
    /// <remarks>
    /// Reads the raw JSON rather than deserialising a <see cref="Configuration"/>: a backup from
    /// before the per-character wardrobes keeps its items at the top level, and only the load path's
    /// migrations know how to read that shape. Counting both shapes here is the whole point — the
    /// restore goes through those migrations, so an old backup is as restorable as a new one.
    /// </remarks>
    public static BackupSummary Describe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc    = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out _))
                return new BackupSummary(0, 0, 0, "Not a Wardrobe config file.");

            if (root.TryGetProperty("Profiles", out var profiles)
                && profiles.ValueKind == JsonValueKind.Array
                && profiles.GetArrayLength() > 0)
            {
                var items   = 0;
                var outfits = 0;
                foreach (var profile in profiles.EnumerateArray())
                {
                    items   += CountOf(profile, "Items");
                    outfits += CountOf(profile, "Outfits");
                }
                return new BackupSummary(profiles.GetArrayLength(), items, outfits);
            }

            // Pre-wardrobes shape: one wardrobe, kept at the top level under the old names
            return new BackupSummary(1, CountOf(root, "WardrobeItems"), CountOf(root, "Outfits"));
        }
        catch (Exception ex)
        {
            return new BackupSummary(0, 0, 0, $"Could not read it: {ex.Message}");
        }
    }

    private static int CountOf(JsonElement obj, string array) =>
        obj.TryGetProperty(array, out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;

    /// <summary>
    /// Copies a backup under the config folder, where the next load will pick it up. Nothing the
    /// running plugin holds changes: it keeps saving the wardrobe it has until it is unloaded.
    /// </summary>
    public bool StageRestore(BackupEntry entry, out string result)
    {
        try
        {
            Directory.CreateDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);
            File.Copy(entry.Path, StagedConfigPath, true);
            StagedFromName = entry.FileName;

            result = $"Restore staged from {entry.FileName}.";
            _log.Information($"[Wardrobe] {result} It is applied at the next load.");
            return true;
        }
        catch (Exception ex)
        {
            result = $"Could not stage the restore: {ex.Message}";
            _log.Error(ex, "[Wardrobe] Staging a restore failed");
            return false;
        }
    }

    /// <summary>Throws away a staged restore, so the next load reads the config as it is.</summary>
    public void CancelRestore()
    {
        try { File.Delete(StagedConfigPath); }
        catch (Exception ex) { _log.Warning(ex, "[Wardrobe] Could not remove the staged restore"); }

        StagedFromName = null;
    }

    /// <summary>
    /// Puts a staged backup in place of the config file. Called before the config is read, so the
    /// file this puts there is the one that loads.
    /// </summary>
    /// <remarks>
    /// The restore happens here, at load, rather than being swapped into the running plugin. Every
    /// service holds the live <see cref="Configuration"/> by reference, and the load path is the one
    /// place that knows how to bring any older shape of the file up to date — the migrations
    /// <see cref="Plugin"/> runs in order right after this. Going through them means a backup from
    /// any version restores the way it would have loaded, instead of a second, partial copy of that
    /// logic living here.
    /// <para>
    /// The config being replaced is copied aside first, timestamped, under the same folder. That is
    /// the way back from restoring the wrong one. If any of it fails the staged file is left where it
    /// is and the current config is untouched: the failure is logged, the settings panel still shows
    /// the restore as pending, and Cancel Restore is the way out.
    /// </para>
    /// </remarks>
    public static void ApplyStagedRestore(IDalamudPluginInterface pi, IPluginLog log)
    {
        var folder = pi.ConfigDirectory.FullName;
        var staged = Path.Combine(folder, StagedConfigName);
        if (!File.Exists(staged)) return;

        try
        {
            var target = pi.ConfigFile;
            if (target.Exists)
            {
                var aside = Path.Combine(folder, $"{PreRestorePrefix}_{DateTime.Now.ToString(StampFormat)}.json");
                target.CopyTo(aside, true);
                Prune(folder, PreRestorePrefix, PreRestoreKeep);
                log.Information($"[Wardrobe] Config copied to '{aside}' before the restore");
            }

            File.Move(staged, target.FullName, true);
            log.Information("[Wardrobe] Restored the config from the staged backup");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Wardrobe] Could not apply the staged restore; the current config is left as it was");
        }
    }

    /// <summary>
    /// Reads a file without standing in the way of a write to it. Dalamud saves the config by writing
    /// a temporary file and swapping it into place, and that swap needs every open handle on the
    /// old file to have allowed the delete — a plain read holds it without, and the save fails.
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Combined SHA-256 over every source, in path order, so a change in any of them triggers a
    /// backup. Path order rather than the order they were read in, so the hash of an unchanged pair
    /// of files matches the one recorded by earlier versions and does not force a backup once.
    /// </summary>
    private static string ComputeHash((string Path, byte[] Bytes)[] sources)
    {
        using var sha = SHA256.Create();
        using var ms  = new MemoryStream();
        foreach (var (_, bytes) in sources.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
            ms.Write(bytes, 0, bytes.Length);
        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms));
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> backups for one file prefix.</summary>
    private static void Prune(string folder, string prefix, int keep)
    {
        try
        {
            var stale = new DirectoryInfo(folder)
                .GetFiles($"{prefix}_*.json")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(keep);

            foreach (var f in stale)
            {
                try { f.Delete(); } catch { /* a locked file is not worth failing the backup over */ }
            }
        }
        catch { /* folder vanished mid-prune */ }
    }
}
