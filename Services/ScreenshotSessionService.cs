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
    public WardrobeItem?  CurrentItem   { get; private set; }
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

    private readonly Queue<WardrobeItem> _queue = new();
    private FileSystemWatcher? _watcher;
    private DateTime           _watchFrom;

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
        FoldersReady && _config.WardrobeItems.Any(i => string.IsNullOrEmpty(i.ImagePath));

    public void Start()
    {
        _queue.Clear();
        foreach (var item in _config.WardrobeItems.Where(i => string.IsNullOrEmpty(i.ImagePath)))
            _queue.Enqueue(item);

        if (_queue.Count == 0) return;
        BeginSession();
    }

    public void StartSingle(WardrobeItem item)
    {
        _queue.Clear();
        _queue.Enqueue(item);
        BeginSession();
    }

    private void BeginSession()
    {
        TotalCount     = _queue.Count;
        CompletedCount = 0;

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
        State       = SessionState.Idle;
        CurrentItem = null;
        _queue.Clear();
        Plugin.Glamourer.SetWeaponVisible(true);
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
            Plugin.Glamourer.SetWeaponVisible(true);
            StateChanged?.Invoke();
            return;
        }

        CurrentItem = _queue.Dequeue();
        if (StripOthers) _wardrobe.StripAll();
        _wardrobe.WearItem(CurrentItem);
        HideWeaponIfNeeded();
        Plugin.Penumbra.RedrawPlayer();

        var slotKey = CurrentItem.Slot.ToString();
        if (_config.SlotCameraPresets.TryGetValue(slotKey, out var preset))
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

        var item = CurrentItem!;
        Task.Run(() =>
        {
            try
            {
                WaitForFile(e.FullPath);

                var dest = UniquePath(_config.ImagesFolder, Sanitize(item.Name) + ".jpg");
                CropAndConvert(e.FullPath, dest);

                _framework.RunOnFrameworkThread(() =>
                {
                    item.ImagePath = dest;
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
            // The item being shot is a weapon — make sure it's visible
            Plugin.Glamourer.SetWeaponVisible(true);
            return;
        }
        Plugin.Glamourer.SetWeaponVisible(false);
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
