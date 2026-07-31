using System;
using System.IO;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin.Services;

namespace WardrobePlugin.Services;

/// <summary>
/// Provides an italic font handle for UI that needs to distinguish text by style rather than
/// colour alone. ImGui has no italic flag and none of Dalamud's built-in fonts ship an italic
/// face, so one is built from a system italic TTF.
/// </summary>
/// <remarks>
/// Everything here degrades safely: if no italic font is installed, or the atlas build fails,
/// <see cref="Available"/> stays false and callers simply render without italics.
/// </remarks>
public class ItalicFontService : IDisposable
{
    // Tried in order; the first that exists wins.
    private static readonly string[] Candidates =
    {
        "segoeuii.ttf", // Segoe UI Italic — present on every modern Windows
        "calibrii.ttf", // Calibri Italic
        "ariali.ttf",   // Arial Italic
    };

    private readonly IPluginLog   _log;
    private readonly IFontHandle? _handle;

    public ItalicFontService(IPluginLog log)
    {
        _log = log;

        var path = FindItalicFont();
        if (path == null)
        {
            _log.Debug("[Wardrobe] No system italic font found — italic styling disabled.");
            return;
        }

        try
        {
            _handle = Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
                e.OnPreBuild(tk =>
                {
                    var cfg = new SafeFontConfig
                    {
                        SizePx = Dalamud.Interface.UiBuilder.DefaultFontSizePx,
                    };
                    tk.Font = tk.AddFontFromFile(path, cfg);
                }));
            _log.Debug($"[Wardrobe] Italic font loaded from {path}");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Could not build italic font — italic styling disabled.");
            _handle = null;
        }
    }

    /// <summary>True when the italic font is built and safe to push.</summary>
    public bool Available => _handle is { Available: true };

    /// <summary>
    /// Pushes the italic font if it is ready. Returns true when a matching
    /// <see cref="Pop"/> is required.
    /// </summary>
    public bool Push()
    {
        if (!Available) return false;
        _handle!.Push();
        return true;
    }

    public void Pop() => _handle?.Pop();

    public void Dispose() => _handle?.Dispose();

    private static string? FindItalicFont()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(dir)) return null;

            foreach (var f in Candidates)
            {
                var full = Path.Combine(dir, f);
                if (File.Exists(full)) return full;
            }
        }
        catch { /* fall through to no italic */ }

        return null;
    }
}
