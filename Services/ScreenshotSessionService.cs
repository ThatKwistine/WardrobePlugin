using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;
using WardrobePlugin.Ipc;

namespace WardrobePlugin.Services;

public enum SessionState { Idle, WaitingForShot, Processing, Done }

public class ScreenshotSessionService : IDisposable
{
    public SessionState   State         { get; private set; } = SessionState.Idle;

    /// <summary>Item being shot, or null when the current target is an outfit.</summary>
    public WardrobeItem?  CurrentItem   { get; private set; }

    /// <summary>Outfit being shot, or null when the current target is a single item.</summary>
    public Outfit?        CurrentOutfit { get; private set; }

    /// <summary>Display name of whatever is being shot.</summary>
    public string CurrentName => CurrentItem?.Name ?? CurrentOutfit?.Name ?? string.Empty;

    public int            TotalCount    { get; private set; }
    public int            CompletedCount { get; private set; }

    public event Action? StateChanged;

    /// <summary>
    /// When true, all other worn items are stripped before each item is equipped for its shot.
    /// Backed by <see cref="Configuration.StripOthersDuringSession"/> so the setting persists and is
    /// already in effect for the first item of a session, not just once the HUD checkbox is touched.
    /// </summary>
    public bool StripOthers
    {
        get => _config.StripOthersDuringSession;
        set
        {
            if (_config.StripOthersDuringSession == value) return;
            _config.StripOthersDuringSession = value;
            _config.Save();
        }
    }

    private readonly WardrobeService _wardrobe;
    private readonly Configuration   _config;
    private readonly IFramework      _framework;
    private readonly IPluginLog      _log;
    private readonly CameraService   _camera;

    /// <summary>One thing to photograph: either a wardrobe item or a whole outfit.</summary>
    private sealed record SessionTarget(WardrobeItem? Item, Outfit? Outfit);

    private readonly Queue<SessionTarget> _queue = new();
    private FileSystemWatcher? _watcher;
    private DateTime           _watchFrom;

    /// <summary>
    /// Weapon visibility as the user had it before the session, restored when it ends.
    /// </summary>
    /// <remarks>
    /// Sessions hide the weapon so it stays out of the shot. Turning it back on unconditionally
    /// would force it visible for anyone who keeps weapons hidden permanently, so the original
    /// value is captured instead. Null means it could not be read, in which case it is left alone.
    /// </remarks>
    private bool? _weaponVisibleBefore;

    public ScreenshotSessionService(WardrobeService wardrobe, Configuration config,
        IFramework framework, IPluginLog log, CameraService camera)
    {
        _wardrobe  = wardrobe;
        _config    = config;
        _framework = framework;
        _log       = log;
        _camera    = camera;
    }

    public bool FoldersReady =>
        !string.IsNullOrEmpty(_config.ImagesFolder)      && Directory.Exists(_config.ImagesFolder) &&
        !string.IsNullOrEmpty(_config.ScreenshotsFolder) && Directory.Exists(_config.ScreenshotsFolder);

    public bool CanStart =>
        FoldersReady && _config.WardrobeItems.Any(Photographable);

    /// <summary>
    /// Items worth queueing for a bulk session. Animations, VFX, mounts and minions are excluded:
    /// wearing one changes nothing about how the character stands there, so the session would
    /// take an identical shot of it for each. They can still be photographed one at a time from
    /// the edit panel, where the user is posing deliberately.
    /// </summary>
    private static bool Photographable(WardrobeItem item) =>
        string.IsNullOrEmpty(item.ImagePath) && !item.Slot.IsModCategory();

    /// <summary>True when there are outfits without a preview image to photograph.</summary>
    public bool CanStartOutfits =>
        FoldersReady && _config.Outfits.Any(o => string.IsNullOrEmpty(o.ImagePath));

    public void Start()
    {
        _queue.Clear();
        foreach (var item in _config.WardrobeItems.Where(Photographable))
            _queue.Enqueue(new SessionTarget(item, null));

        if (_queue.Count == 0) return;
        BeginSession();
    }

    /// <summary>Photographs every outfit that has no preview image yet.</summary>
    public void StartOutfits()
    {
        _queue.Clear();
        foreach (var outfit in _config.Outfits.Where(o => string.IsNullOrEmpty(o.ImagePath)))
            _queue.Enqueue(new SessionTarget(null, outfit));

        if (_queue.Count == 0) return;
        BeginSession();
    }

    public void StartSingle(WardrobeItem item)
    {
        _queue.Clear();
        _queue.Enqueue(new SessionTarget(item, null));
        BeginSession();
    }

    public void StartSingleOutfit(Outfit outfit)
    {
        _queue.Clear();
        _queue.Enqueue(new SessionTarget(null, outfit));
        BeginSession();
    }

    private void BeginSession()
    {
        TotalCount     = _queue.Count;
        CompletedCount = 0;

        // Captured before anything is hidden, so it can be put back exactly as it was
        _weaponVisibleBefore = Plugin.Glamourer.GetWeaponVisible();
        _log.Debug($"[Wardrobe] Session start: weapon visible was " +
                   $"{_weaponVisibleBefore?.ToString() ?? "unknown"}");

        _watcher = new FileSystemWatcher(_config.ScreenshotsFolder, "*.png")
        {
            NotifyFilter        = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileCreated;

        WearNext();
    }

    public void Stop()
    {
        DisposeWatcher();
        State         = SessionState.Idle;
        CurrentItem   = null;
        CurrentOutfit = null;
        _queue.Clear();
        RestoreWeaponVisibility();
        StateChanged?.Invoke();
    }

    public void Skip()
    {
        if (State != SessionState.WaitingForShot) return;
        CompletedCount++;
        WearNext();
    }

    private void WearNext()
    {
        if (_queue.Count == 0)
        {
            DisposeWatcher();
            State = SessionState.Done;
            RestoreWeaponVisibility();
            StateChanged?.Invoke();
            return;
        }

        var target = _queue.Dequeue();
        CurrentItem   = target.Item;
        CurrentOutfit = target.Outfit;

        if (target.Item != null)
        {
            if (StripOthers) _wardrobe.StripAll();
            _wardrobe.WearItem(target.Item);
        }
        else if (target.Outfit != null)
        {
            // Always exclusive: an outfit shot should show the outfit and nothing else
            _wardrobe.WearOutfit(target.Outfit, removeOthers: true);
        }

        HideWeaponIfNeeded();
        Plugin.Penumbra.RedrawPlayer();

        // Camera presets are per slot, so they only apply to single items. A slot can hold several;
        // the one it uses is whichever the edit panel has ticked.
        if (target.Item != null &&
            _config.DefaultPresetFor(target.Item.Slot.ToString()) is { } preset)
            _camera.Apply(preset);

        _watchFrom = DateTime.UtcNow;
        State      = SessionState.WaitingForShot;
        StateChanged?.Invoke();
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (State != SessionState.WaitingForShot) return;

        try { if (new FileInfo(e.FullPath).CreationTimeUtc < _watchFrom) return; }
        catch { return; }

        State = SessionState.Processing;
        StateChanged?.Invoke();

        var item   = CurrentItem;
        var outfit = CurrentOutfit;
        var name   = CurrentName;

        Task.Run(() =>
        {
            try
            {
                WaitForFile(e.FullPath);

                var dest = UniquePath(_config.ImagesFolder, Sanitize(name) + ".jpg");
                CropAndConvert(e.FullPath, dest);

                _framework.RunOnFrameworkThread(() =>
                {
                    if (item != null)        item.ImagePath   = dest;
                    else if (outfit != null) outfit.ImagePath = dest;

                    _config.Save();
                    CompletedCount++;
                    WearNext();
                });
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[Wardrobe] Screenshot session: failed to process screenshot");
                _framework.RunOnFrameworkThread(() => { CompletedCount++; WearNext(); });
            }
        });
    }

    private void HideWeaponIfNeeded()
    {
        var slot = CurrentItem?.Slot ?? EquipSlot.Unknown;
        if (slot == EquipSlot.MainHand || slot == EquipSlot.OffHand)
        {
            // The item being shot is the weapon itself, so it has to be visible for the shot
            Plugin.Glamourer.SetWeaponVisible(true);
            return;
        }
        Plugin.Glamourer.SetWeaponVisible(false);
    }

    /// <summary>
    /// Puts weapon visibility back exactly as it was before the session.
    /// </summary>
    /// <remarks>
    /// Never forces the weapon on: someone who keeps weapons hidden should not have them switched
    /// back by taking screenshots. If the original value could not be read, nothing is changed.
    /// </remarks>
    private void RestoreWeaponVisibility()
    {
        if (_weaponVisibleBefore is not { } wasVisible)
        {
            _log.Debug("[Wardrobe] Session end: weapon visibility unknown, leaving it alone");
            _weaponVisibleBefore = null;
            return;
        }

        Plugin.Glamourer.SetWeaponVisible(wasVisible);
        _log.Debug($"[Wardrobe] Session end: weapon visibility restored to {wasVisible}");
        _weaponVisibleBefore = null;
    }

    private static void CropAndConvert(string sourcePath, string destPath)
    {
        using var img  = Image.FromFile(sourcePath);
        var size = Math.Min(img.Width, img.Height);
        var x    = (img.Width  - size) / 2;
        var y    = (img.Height - size) / 2;

        const int Target = 512;
        using var bmp = new Bitmap(Target, Target);
        using var g   = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img,
            new Rectangle(0, 0, Target, Target),
            new Rectangle(x, y, size, size),
            GraphicsUnit.Pixel);

        var codec  = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        bmp.Save(destPath, codec, encParams);
    }

    private static void WaitForFile(string path)
    {
        for (var i = 0; i < 25; i++)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
        }
    }

    private static string UniquePath(string folder, string filename)
    {
        var path = Path.Combine(folder, filename);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(filename);
        var ext  = Path.GetExtension(filename);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem}_{i}{ext}");
            if (!File.Exists(path)) return path;
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void DisposeWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose() => DisposeWatcher();
}
