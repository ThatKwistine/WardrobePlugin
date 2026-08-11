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

    /// <summary>
    /// Photographs exactly the items given, in the order given. Returns false if there was nothing
    /// to do or the folders are not set up.
    /// </summary>
    /// <remarks>
    /// Deliberately does not apply <see cref="Photographable"/>. That filter answers "what is worth
    /// queueing when the user asked for everything" — it skips items that already have a preview,
    /// and mod categories whose shots would all look identical. Neither applies to a set the user
    /// picked by hand: re-shooting a preview you are unhappy with is a reason to select it, and an
    /// animation chosen deliberately is the same considered act as photographing one from its edit
    /// panel. A selection is an instruction, not a suggestion to be filtered.
    /// </remarks>
    public bool StartMany(IEnumerable<WardrobeItem> items)
    {
        if (!FoldersReady) return false;

        _queue.Clear();
        foreach (var item in items)
            _queue.Enqueue(new SessionTarget(item, null));

        if (_queue.Count == 0) return false;

        BeginSession();
        return true;
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

        // Before the target, every time. The character being photographed is the user's own, so the
        // base character's customisations and kept slots are put back for each shot rather than only
        // at the start — a base item displaced by the last piece photographed has to come back, or
        // the session drifts further from the character with every shot. Stripping does this itself.
        if (!StripOthers || target.Item == null) _wardrobe.ApplyBase();

        if (target.Item != null)
        {
            if (StripOthers) _wardrobe.StripAll();
            _wardrobe.WearItem(target.Item);
        }
        else if (target.Outfit != null)
        {
            // Always exclusive: an outfit shot should show the outfit and nothing else — bar the
            // base character, which WearOutfit holds back for the same reason a strip does
            _wardrobe.WearOutfit(target.Outfit, removeOthers: true);
        }

        HideWeaponIfNeeded();
        Plugin.Penumbra.RedrawPlayer();

        // Presets are per slot for items and one shared set for outfits — whichever applies, the one
        // used is the one ticked as the session default
        var presetKey = target.Item?.Slot.ToString()
                     ?? (target.Outfit != null ? Configuration.OutfitPresetKey : null);

        if (presetKey != null && _config.DefaultPresetFor(presetKey) is { } preset)
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
                // Portrait only for an outfit, and only when the setting is on: an item preview is a
                // close-up of one piece and has nothing to gain from a full-body frame
                CropAndConvert(e.FullPath, dest, _config.CapturedImageSize,
                    portrait: outfit != null && _config.PortraitOutfitPreviews);

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

    /// <summary>
    /// Sizes offered for a captured image, in pixels square.
    /// </summary>
    /// <remarks>
    /// One per common screen height above the small default: 1024 for 1080p, 1440 for 1440p, 2048
    /// for 4K. A square crop can only be as tall as the game window, so these are the sizes at which
    /// each of those actually has the pixels to fill the image.
    /// </remarks>
    public static readonly int[] ImageSizes = { 512, 1024, 1440, 2048 };

    /// <summary>
    /// Centre-crops a screenshot to a square and writes it at the chosen size.
    /// </summary>
    /// <remarks>
    /// Never upscales. The crop is as tall as the game window, so a 1080p screenshot has about 1080
    /// square pixels to give and asking for 2048 would only produce a larger, softer file — the
    /// requested size is a ceiling rather than a promise. Quality rises with the size because a
    /// bigger image is usually wanted for looking at closely, which is exactly when JPEG artefacts
    /// start to show.
    /// </remarks>
    /// <param name="portrait">
    /// Crop 9:16 instead of square, for an outfit preview shot in GPose's portrait mode. The
    /// requested size becomes the height, since height is what a portrait has most of and what the
    /// screenshot limits.
    /// </param>
    private static void CropAndConvert(string sourcePath, string destPath, int requested, bool portrait)
    {
        using var img = Image.FromFile(sourcePath);

        // No rotation here on purpose. The game writes a portrait shot upright already, and rotating
        // anything landscape would turn an ordinary shot on its side — the file cannot say which
        // mode it was taken in, so there is nothing safe to infer from its shape.
        var wanted = requested <= 0 ? ImageSizes[0] : requested;

        // The largest centred rectangle of the wanted shape that the screenshot actually contains
        var ratio = portrait ? PortraitRatio : 1f;
        var cropH = Math.Min(img.Height, (int)(img.Width * ratio));
        var cropW = (int)(cropH / ratio);

        var x = (img.Width  - cropW) / 2;
        var y = (img.Height - cropH) / 2;

        var targetH = Math.Min(wanted, cropH);
        var targetW = Math.Max(1, (int)(targetH / ratio));

        using var bmp = new Bitmap(targetW, targetH);
        using var g   = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img,
            new Rectangle(0, 0, targetW, targetH),
            new Rectangle(x, y, cropW, cropH),
            GraphicsUnit.Pixel);

        var codec  = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, targetH >= 1024 ? 92L : 85L);
        bmp.Save(destPath, codec, encParams);
    }

    /// <summary>9:16, as height ÷ width. Mirrors <see cref="Ui.ImageDraw.PortraitRatio"/>.</summary>
    private const float PortraitRatio = 16f / 9f;

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
