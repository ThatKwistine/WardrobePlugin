using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>What one mod is actually contributing to the character right now.</summary>
/// <param name="UncompressedCount">Textures in a format that ships every pixel whole.</param>
/// <param name="UncompressedBytes">
/// On-disk size of those textures. The number worth showing: it is what a synced friend downloads,
/// and roughly four times what the same texture costs once it is block-compressed.
/// </param>
/// <param name="TotalTextures">Every <c>.tex</c> the mod is currently supplying, for context.</param>
public record TextureFlag(int UncompressedCount, long UncompressedBytes, int TotalTextures)
{
    public bool Any => UncompressedCount > 0;

    public static readonly TextureFlag None = new(0, 0, 0);

    public TextureFlag Plus(TextureFlag other) => new(
        UncompressedCount + other.UncompressedCount,
        UncompressedBytes + other.UncompressedBytes,
        TotalTextures     + other.TotalTextures);
}

/// <summary>
/// Reads the texture formats a worn item is actually putting on the character, so an item costing
/// its viewers far more than it needs to can say so.
/// </summary>
/// <remarks>
/// The gap this fills is between two tools that do not talk to each other. Lightless auto-compress
/// only ever touches textures in its own download cache — its pipeline refuses any path outside that
/// folder — so it never sees your own mods. Converting those is a manual pass in its Character
/// Analysis window, the one place allowed to rewrite files in Penumbra's folders, and it is
/// per-file-on-disk rather than per-outfit: a mod converted once stays converted, and only newly
/// installed or updated mods fall back out of step. Which makes forgetting easy, because nothing
/// asks until a friend mentions the download.
/// <para>
/// What it reads is <see cref="PenumbraIpc.GetResolvedFilePaths"/> — the files Penumbra is feeding
/// the character — and <b>not</b> the contents of the mod folders. The first version of this did
/// read the folders, and was wrong: a mod ships every option it has, and Penumbra hands over only
/// the ones selected. One outfit here holds 15 textures of which 12 are uncompressed, all inside
/// option groups, while the character was wearing none of them. Lightless' own analysis, which
/// counts the same resolved set, reported everything on the character as already compressed.
/// </para>
/// <para>
/// It only ever reports. It does not convert anything, and deliberately so — conversion is lossy and
/// irreversible without the original mod archive, which is not a thing to do behind somebody's back
/// while they are picking an outfit.
/// </para>
/// </remarks>
public class TextureCompressionFlagService
{
    private readonly PenumbraIpc _penumbra;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    public TextureCompressionFlagService(PenumbraIpc penumbra, IFramework framework, IPluginLog log)
    {
        _penumbra  = penumbra;
        _framework = framework;
        _log       = log;
    }

    /// <summary>
    /// How stale the resolved set may be before it is asked for again.
    /// </summary>
    /// <remarks>
    /// Long enough that the cost is nothing and short enough that a conversion, or a change of
    /// clothes, shows up without being asked for. The refresh runs entirely off the draw thread; the
    /// previous answer stays on display while it does.
    /// </remarks>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The last completed reading, keyed by mod directory. Replaced wholesale rather than edited, so
    /// a draw call either sees the whole of one refresh or the whole of the one before it.
    /// </summary>
    private Dictionary<string, TextureFlag> _byMod = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _refreshedAt = DateTime.MinValue;
    private int      _refreshing;
    private bool     _haveEver;

    /// <summary>
    /// The <c>.tex</c> format codes that store pixels whole — the ones worth converting.
    /// </summary>
    /// <remarks>
    /// Listed rather than inferred from the absence of a block-compressed code, so a format neither
    /// list knows about is passed over in silence instead of being reported as a problem. A false
    /// badge on a mod that is already fine would train the eye to ignore the badge.
    /// </remarks>
    private static readonly HashSet<uint> UncompressedFormats = new()
    {
        0x1130, // L8
        0x1131, // A8
        0x1440, // B4G4R4A4
        0x1441, // B5G5R5A1
        0x1450, // B8G8R8A8 — far and away the most common offender
        0x1451, // B8G8R8X8
        0x2460, // R16G16B16A16F
        0x2470, // R32G32B32A32F
    };

    /// <summary>
    /// What the item is currently putting on the character, or <c>null</c> before the first reading
    /// has finished.
    /// </summary>
    /// <remarks>
    /// A dictionary lookup and nothing else. This runs for every worn card every frame, so the
    /// refresh it may start does all of its work elsewhere and this returns the previous answer
    /// meanwhile.
    /// </remarks>
    public TextureFlag? For(WardrobeItem item)
    {
        Touch();

        if (!_haveEver) return null;

        var byMod = _byMod;
        var total = TextureFlag.None;

        foreach (var mod in item.Mods)
        {
            if (string.IsNullOrEmpty(mod.ModDirectory)) continue;
            if (byMod.TryGetValue(mod.ModDirectory, out var flag)) total = total.Plus(flag);
        }

        return total;
    }

    /// <summary>
    /// Everything uncompressed on the character, whatever is supplying it.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than the sum of the worn cards. The badges answer "which item is to blame",
    /// which only covers what the wardrobe knows about; this answers "what are my friends actually
    /// downloading", and a skin, a hair or a body replacer costs them exactly as much as a dress
    /// does. Keyed by mod, so a mod worn as six slot-items is counted once rather than six times.
    /// </remarks>
    public TextureFlag Total()
    {
        // Asks in its own right rather than relying on the cards having asked first: the banner is
        // drawn above the grid, and a grid filtered down to nothing — or a character whose textures
        // all come from mods the wardrobe has no item for — would otherwise never start a reading
        Touch();

        var total = TextureFlag.None;
        if (!_haveEver) return total;

        foreach (var flag in _byMod.Values) total = total.Plus(flag);
        return total;
    }

    /// <summary>The mods carrying the most uncompressed texture, heaviest first.</summary>
    /// <remarks>
    /// What makes the total worth showing: a number nobody can act on is just a complaint, and the
    /// answer is nearly always one or two mods rather than a spread.
    /// </remarks>
    public IReadOnlyList<(string Mod, TextureFlag Flag)> Worst(int max)
    {
        if (!_haveEver) return [];

        var worst = new List<(string, TextureFlag)>();
        foreach (var (mod, flag) in _byMod)
            if (flag.Any) worst.Add((mod, flag));

        worst.Sort((a, b) => b.Item2.UncompressedBytes.CompareTo(a.Item2.UncompressedBytes));
        return worst.Count > max ? worst.GetRange(0, max) : worst;
    }

    /// <summary>Starts a reading if the last one has gone stale. Cheap enough to call from a draw.</summary>
    private void Touch()
    {
        if (DateTime.UtcNow - _refreshedAt >= RefreshAfter) BeginRefresh();
    }

    /// <summary>
    /// Asks Penumbra what the character is wearing, then reads the headers of whatever it names.
    /// </summary>
    /// <remarks>
    /// The IPC call has to happen on the framework thread and the file reading must not, so the two
    /// are split. Only one refresh runs at a time; a second request while one is in flight is
    /// dropped rather than queued, because the answer it would produce is the one already coming.
    /// </remarks>
    private void BeginRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var root = await _framework.RunOnFrameworkThread(() => _penumbra.GetModRoot())
                    .ConfigureAwait(false);
                var paths = await _framework.RunOnFrameworkThread(() => _penumbra.GetResolvedFilePaths())
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(root) || paths == null)
                {
                    // Penumbra not ready, or resolving failed mid-redraw. Keep whatever is on display
                    // and try again on the next interval rather than reporting an empty character
                    return;
                }

                _byMod       = Read(paths, root!);
                _haveEver    = true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[Wardrobe] Texture flag refresh failed");
            }
            finally
            {
                _refreshedAt = DateTime.UtcNow;
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Sorts the resolved files by the mod that supplied them and reads each one's format.</summary>
    private Dictionary<string, TextureFlag> Read(IReadOnlyCollection<string> paths, string root)
    {
        var byMod = new Dictionary<string, TextureFlag>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) continue;

            var mod = ModDirectoryOf(path, root);
            if (mod == null) continue;

            if (!TryReadFormat(path, out var format)) continue;

            var uncompressed = UncompressedFormats.Contains(format);
            long bytes = 0;
            if (uncompressed)
            {
                try { bytes = new FileInfo(path).Length; } catch { /* counted without its size */ }
            }

            var add = new TextureFlag(uncompressed ? 1 : 0, bytes, 1);
            byMod[mod] = byMod.TryGetValue(mod, out var have) ? have.Plus(add) : add;
        }

        return byMod;
    }

    /// <summary>
    /// Which mod folder a resolved file came out of, or <c>null</c> if it came from outside the mod
    /// root entirely.
    /// </summary>
    /// <remarks>
    /// The first path segment under the root is the mod directory, which is the same name items
    /// store in <see cref="ModReference.ModDirectory"/> — that is what makes the two sides line up.
    /// </remarks>
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

    /// <summary>
    /// Pulls the format code out of a <c>.tex</c> header.
    /// </summary>
    /// <remarks>
    /// The header is 80 bytes and the format is a u32 at offset 4, after the attribute field. Only
    /// those eight bytes are read: the surface offset table further in is frequently wrong in
    /// mod-exported files, and nothing here needs it.
    /// </remarks>
    private static bool TryReadFormat(string path, out uint format)
    {
        format = 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Span<byte> head = stackalloc byte[8];
            var read = 0;
            while (read < head.Length)
            {
                var got = stream.Read(head[read..]);
                if (got <= 0) return false;
                read += got;
            }

            format = BitConverter.ToUInt32(head[4..]);
            return true;
        }
        catch
        {
            // A texture being written by Penumbra or TexTools as this runs is ordinary — skip it and
            // let the next refresh pick it up
            return false;
        }
    }

    /// <summary>Forces the next look to re-read rather than waiting out the interval.</summary>
    public void Clear()
    {
        _refreshedAt = DateTime.MinValue;
    }
}
