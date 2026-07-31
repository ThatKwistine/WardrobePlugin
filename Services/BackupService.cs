using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace WardrobePlugin.Services;

/// <summary>
/// Copies the plugin config (and the camera preset file, if one is configured) to a user-chosen
/// folder once an hour. A backup is only written when the content has actually changed since the
/// last one, so leaving the game running does not fill the folder with identical copies.
/// </summary>
public class BackupService : IDisposable
{
    private const string ConfigPrefix = "WardrobePlugin";
    private const string PresetPrefix = "CameraPresets";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly Configuration _config;
    private readonly IFramework    _framework;
    private readonly IPluginLog    _log;

    private volatile bool _running;

    /// <summary>Human-readable outcome of the last backup attempt, for the settings panel.</summary>
    public string LastResult { get; private set; } = string.Empty;

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
        if (DateTime.UtcNow - _config.LastBackupCheckUtc < Interval) return;

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

        // Record the attempt up front so a failing backup can't retry every frame
        _config.LastBackupCheckUtc = DateTime.UtcNow;
        _config.Save();

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
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

                foreach (var src in sources)
                {
                    var prefix = string.Equals(src, configPath, StringComparison.OrdinalIgnoreCase)
                        ? ConfigPrefix : PresetPrefix;
                    File.Copy(src, Path.Combine(folder, $"{prefix}_{stamp}.json"), true);
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

    /// <summary>Combined SHA-256 over every source file, so a change in any of them triggers a backup.</summary>
    private static string ComputeHash(string[] paths)
    {
        using var sha = SHA256.Create();
        using var ms  = new MemoryStream();
        foreach (var p in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(p);
            ms.Write(bytes, 0, bytes.Length);
        }
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
