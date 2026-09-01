using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>One installed icon pack: a folder of slot-named images under <see cref="IconPackService.PacksRoot"/>.</summary>
/// <param name="Id">Folder name, and what the config stores. Stable across renames of the display name.</param>
/// <param name="DisplayName">Name shown in the dropdown — the manifest's, or the folder's if it has none.</param>
/// <param name="Author">Optional, from the manifest. Empty when the pack did not supply one.</param>
/// <param name="Folder">Absolute path to the pack's folder.</param>
/// <param name="MatchedSlots">How many slots the pack actually supplies an icon for.</param>
public sealed record IconPack(string Id, string DisplayName, string Author, string Folder, int MatchedSlots);

/// <summary>
/// Installs, lists and removes zipped icon packs.
/// </summary>
/// <remarks>
/// A pack is nothing more than the folder of slot-named images the custom-icon setting already
/// accepts, moved somewhere the plugin owns. That is the point: a pack is something to hand
/// someone else, so the installed form has to stay a plain folder anyone can open, edit or zip
/// back up — there is no pack format to get wrong, only a naming convention that was already
/// documented.
/// </remarks>
public class IconPackService
{
    /// <summary>Folder under the plugin's config directory that holds every installed pack.</summary>
    public const string PacksFolderName = "IconPacks";

    // Bounds on what will be unpacked. A zip is a file someone was handed, so it is not assumed to
    // be well meant: without these, one archive could fill the drive from the settings window.
    private const int  MaxEntries    = 200;
    private const long MaxFileBytes  = 16L * 1024 * 1024;
    private const long MaxTotalBytes = 64L * 1024 * 1024;

    private const string ManifestName = "pack.json";

    private readonly Configuration _config;
    private readonly IPluginLog    _log;

    /// <summary>Installed packs, rebuilt by <see cref="Refresh"/> rather than read per frame.</summary>
    private readonly List<IconPack> _packs = new();

    private bool _scanned;

    public IconPackService(Configuration config, IPluginLog log)
    {
        _config = config;
        _log    = log;
    }

    /// <summary>Where packs are installed to. Inside the plugin's own config directory, so an
    /// uninstall of the plugin takes them with it and nothing is written outside it.</summary>
    public string PacksRoot { get; } =
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, PacksFolderName);

    public IReadOnlyList<IconPack> Packs
    {
        get
        {
            if (!_scanned) Refresh();
            return _packs;
        }
    }

    /// <summary>The pack the config selects, or null when none is selected or it has gone missing.</summary>
    public IconPack? Active =>
        string.IsNullOrEmpty(_config.ActiveIconPack)
            ? null
            : Packs.FirstOrDefault(p => p.Id == _config.ActiveIconPack);

    /// <summary>Folder of the active pack, or empty. What <see cref="SlotIconService"/> scans.</summary>
    /// <remarks>
    /// Held as a field rather than looked up through <see cref="Active"/> because this is read from
    /// the draw path — once per slot icon per frame — and searching the list there would allocate an
    /// enumerator for an answer that only changes when a pack is installed, removed or selected.
    /// </remarks>
    public string ActiveFolder
    {
        get
        {
            if (!_scanned) Refresh();
            return _activeFolder;
        }
    }

    private string _activeFolder = string.Empty;

    /// <summary>True when a pack is selected but its folder is no longer there.</summary>
    public bool ActiveIsMissing => !string.IsNullOrEmpty(_config.ActiveIconPack) && Active == null;

    /// <summary>Re-reads the packs folder. Cheap enough to call after any change, never per frame.</summary>
    public void Refresh()
    {
        _scanned = true;
        _packs.Clear();

        if (!Directory.Exists(PacksRoot)) return;

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(PacksRoot);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"[Wardrobe] Could not read icon pack folder '{PacksRoot}'");
            return;
        }

        foreach (var folder in folders)
        {
            var id = Path.GetFileName(folder);
            var (name, author) = ReadManifest(folder);

            _packs.Add(new IconPack(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name,
                author,
                folder,
                SlotIconService.CountMatchedSlots(folder)));
        }

        _packs.Sort((a, b) => NaturalOrder.Compare(a.DisplayName, b.DisplayName));
        CacheActiveFolder();
    }

    private void CacheActiveFolder() =>
        _activeFolder = _packs.FirstOrDefault(p => p.Id == _config.ActiveIconPack)?.Folder ?? string.Empty;

    /// <summary>
    /// Installs a zip as a pack and selects it.
    /// </summary>
    /// <param name="zipPath">Archive to read. It is only ever read — the file the user picked is left alone.</param>
    /// <param name="message">What to show the user, whether it worked or not.</param>
    /// <returns>True when a pack was installed.</returns>
    /// <remarks>
    /// Every image in the zip is extracted flat, whatever folder it sat in, and everything that is
    /// not an image is dropped. Flattening is what makes the common shape work — a zip almost always
    /// holds one folder rather than loose files — and it also means no entry name can escape the
    /// target folder, since only the bare file name is ever used.
    /// </remarks>
    public bool TryImport(string zipPath, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            message = "That file no longer exists.";
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var images = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && IsImage(e.Name))
                .Take(MaxEntries)
                .ToList();

            if (images.Count == 0)
            {
                message = "No images in that zip. A pack is a set of files named after their slots — " +
                          $"{SlotIconService.ExpectedFileName(EquipSlot.Head)} and the rest.";
                return false;
            }

            var (manifestName, manifestAuthor) = ReadManifest(archive);
            var wanted = string.IsNullOrWhiteSpace(manifestName)
                ? Path.GetFileNameWithoutExtension(zipPath)
                : manifestName;

            var folder = Path.Combine(PacksRoot, UniqueFolderName(Sanitize(wanted)));
            Directory.CreateDirectory(folder);

            var written = 0;
            long total  = 0;

            foreach (var entry in images)
            {
                if (entry.Length > MaxFileBytes || total + entry.Length > MaxTotalBytes)
                {
                    _log.Warning($"[Wardrobe] Icon pack entry '{entry.FullName}' skipped — too large");
                    continue;
                }

                // Bare file name only: nested folders collapse, and a crafted entry name such as
                // "../../config.json" cannot point anywhere but inside this folder
                var target = Path.Combine(folder, Path.GetFileName(entry.Name));
                if (File.Exists(target)) continue; // first copy of a duplicated name wins

                entry.ExtractToFile(target);
                total += entry.Length;
                written++;
            }

            if (written == 0)
            {
                Directory.Delete(folder, true);
                message = "Nothing in that zip could be unpacked.";
                return false;
            }

            WriteManifest(folder, string.IsNullOrWhiteSpace(manifestName) ? Path.GetFileName(folder) : manifestName,
                manifestAuthor, zipPath);

            Refresh();

            var id   = Path.GetFileName(folder);
            var pack = _packs.FirstOrDefault(p => p.Id == id);

            // Selected on import: having to pick it from the dropdown afterwards would be a second
            // step with no decision in it
            Select(id);

            var matched = pack?.MatchedSlots ?? 0;
            message = matched == 0
                ? $"Installed '{pack?.DisplayName ?? id}', but none of its {written} image(s) are named " +
                  "after a slot. Open Which slots to see the names it is looking for."
                : $"Installed '{pack?.DisplayName ?? id}' — {matched} slot(s) covered.";

            _log.Debug($"[Wardrobe] Icon pack '{id}': {written} file(s) from '{zipPath}'");
            return true;
        }
        catch (InvalidDataException)
        {
            message = "That file is not a readable zip.";
            return false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"[Wardrobe] Could not import icon pack '{zipPath}'");
            message = $"Import failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Deletes an installed pack's folder, clearing the selection if it was the active one.
    /// </summary>
    public bool Uninstall(string id, out string message)
    {
        message = string.Empty;

        var pack = Packs.FirstOrDefault(p => p.Id == id);
        if (pack == null)
        {
            message = "That pack is no longer installed.";
            Refresh();
            return false;
        }

        // The delete is recursive, so it is worth proving the path is one of ours before running it
        var full = Path.GetFullPath(pack.Folder);
        var root = Path.GetFullPath(PacksRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning($"[Wardrobe] Refused to remove icon pack outside '{root}': '{full}'");
            message = "That pack is not where it should be, so it was left alone.";
            return false;
        }

        try
        {
            Directory.Delete(full, true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"[Wardrobe] Could not remove icon pack '{full}'");
            message = $"Could not remove it: {ex.Message}";
            return false;
        }

        if (_config.ActiveIconPack == id)
        {
            _config.ActiveIconPack = string.Empty;
            _config.Save();
        }

        Refresh();
        message = $"Removed '{pack.DisplayName}'.";
        return true;
    }

    /// <summary>Selects a pack, or none when <paramref name="id"/> is empty.</summary>
    public void Select(string id)
    {
        _config.ActiveIconPack = id;
        _config.Save();
        CacheActiveFolder();
    }

    /// <summary>Makes sure the packs folder exists, for the Open Folder button.</summary>
    public void EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(PacksRoot);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"[Wardrobe] Could not create '{PacksRoot}'");
        }
    }

    // ── Naming and manifests ──────────────────────────────────────────────────

    private static bool IsImage(string name) =>
        SlotIconService.IconExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows device names, which cannot be folder names whatever the rest of the path is.</summary>
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray())
            .Trim()
            .Trim('.');

        if (cleaned.Length > 60) cleaned = cleaned[..60].Trim();

        if (string.IsNullOrWhiteSpace(cleaned)) return "Icon Pack";
        if (ReservedNames.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) return $"{cleaned} Pack";

        return cleaned;
    }

    /// <summary>Appends a number until the name is free, so importing twice does not merge two packs.</summary>
    private string UniqueFolderName(string wanted)
    {
        EnsureRoot();

        if (!Directory.Exists(Path.Combine(PacksRoot, wanted))) return wanted;

        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{wanted} ({n})";
            if (!Directory.Exists(Path.Combine(PacksRoot, candidate))) return candidate;
        }

        return $"{wanted} ({DateTime.Now:yyyyMMdd-HHmmss})";
    }

    /// <summary>Name and author from an installed pack's manifest, if it has one.</summary>
    private (string Name, string Author) ReadManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path)) return (string.Empty, string.Empty);

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _log.Debug($"[Wardrobe] Ignoring unreadable {ManifestName} in '{folder}': {ex.Message}");
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>Name and author from a manifest inside a zip, preferring the one nearest the root.</summary>
    private (string Name, string Author) ReadManifest(ZipArchive archive)
    {
        var entry = archive.Entries
            .Where(e => e.Name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
            .FirstOrDefault();

        if (entry == null) return (string.Empty, string.Empty);

        try
        {
            using var reader = new StreamReader(entry.Open());
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            _log.Debug($"[Wardrobe] Ignoring unreadable {ManifestName} in zip: {ex.Message}");
            return (string.Empty, string.Empty);
        }
    }

    private static (string Name, string Author) Parse(string json)
    {
        var obj = JObject.Parse(json);
        return (obj.Value<string>("Name") ?? string.Empty, obj.Value<string>("Author") ?? string.Empty);
    }

    /// <summary>
    /// Writes the manifest back into the installed folder.
    /// </summary>
    /// <remarks>
    /// Written even when the zip had none, so that zipping the folder back up produces a pack that
    /// keeps its name — otherwise a pack renamed on install would silently revert to the zip's file
    /// name when passed on.
    /// </remarks>
    private void WriteManifest(string folder, string name, string author, string sourceZip)
    {
        try
        {
            var obj = new JObject
            {
                ["Name"]       = name,
                ["Author"]     = author,
                ["Source"]     = Path.GetFileName(sourceZip),
                ["ImportedUtc"] = DateTime.UtcNow.ToString("o"),
            };

            File.WriteAllText(Path.Combine(folder, ManifestName), obj.ToString());
        }
        catch (Exception ex)
        {
            // A pack works without one — the folder name is the fallback everywhere it is read
            _log.Debug($"[Wardrobe] Could not write {ManifestName} in '{folder}': {ex.Message}");
        }
    }
}
