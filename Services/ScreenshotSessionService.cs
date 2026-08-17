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

    /// <summary>Which shot of the current target is being waited for, counting from one.</summary>
    public int ShotIndex { get; private set; }

    /// <summary>
    /// How many shots this target is being asked for — one, plus any ticked angles.
    /// </summary>
    /// <remarks>
    /// Worked out from the preset list as it stands rather than fixed when the item was worn, so ticking
    /// an angle mid-shoot changes it straight away. Meaningless in manual mode, where there is no number
    /// to reach: see <see cref="TakenForTarget"/>.
    /// </remarks>
    public int ShotCount =>
        _presetKey == null ? 1 : 1 + _config.ExtraShotPresetsFor(_presetKey).Count;

    /// <summary>
    /// What the shot being waited for is called: the preset's name, or "Cover" for the first.
    /// </summary>
    /// <remarks>
    /// The whole reason the HUD can say which angle it wants. Without it a session taking four shots
    /// of one item would show the same "waiting for screenshot" four times over, and the only way to
    /// know which angle had already been done would be to count.
    /// </remarks>
    public string ShotLabel { get; private set; } = string.Empty;

    /// <summary>True when this target is being photographed from more than one angle.</summary>
    public bool MultiShot => !Manual && ShotCount > 1;

    /// <summary>
    /// Waits on each item until told to move on, rather than advancing when a screenshot lands.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="Configuration.ManualScreenshotMode"/> so it persists and is in effect for
    /// the first item of a session, exactly as <see cref="StripOthers"/> is.
    /// </remarks>
    public bool Manual
    {
        get => _config.ManualScreenshotMode;
        set
        {
            if (_config.ManualScreenshotMode == value) return;
            _config.ManualScreenshotMode = value;
            _config.Save();

            // Turned on while a session is waiting, this simply means the current item stops advancing
            // by itself; turned off, the queued angles take over again from the next shot. Neither
            // needs anything undone here — the decision is read fresh every time a shot finishes.
            _log.Debug($"[Wardrobe] Session: manual mode {(value ? "on" : "off")}");
        }
    }

    /// <summary>How many pictures have been filed for the item being photographed.</summary>
    public int TakenForTarget { get; private set; }

    /// <summary>True when the session has other things queued after this one.</summary>
    /// <remarks>
    /// What decides whether <b>Next Item</b> is worth offering. A single-item shoot from an edit panel
    /// has nothing queued behind it, so there is nothing to move on to and End Session is the way out.
    /// </remarks>
    public bool HasMoreTargets => _queue.Count > 0;

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

    /// <summary>
    /// One photograph of the current target: which angle to take it from, and where it goes.
    /// </summary>
    /// <remarks>
    /// Made when the session starts waiting rather than queued in advance, so what it is depends on the
    /// state of things at that moment — the preset list as it stands, or in manual mode simply whether
    /// anything has been taken of this item yet.
    /// </remarks>
    /// <param name="Preset">The angle to snap to first, or null to leave the camera as it is.</param>
    /// <param name="IsCover">
    /// True for the shot that becomes the card's picture. Exactly one shot per target has it, and it
    /// is always the first — so a session interrupted halfway has still filled in the picture that
    /// matters rather than only the side view.
    /// </param>
    private sealed record SessionShot(CameraPreset? Preset, bool IsCover, string Label);

    private readonly Queue<SessionTarget> _queue = new();

    /// <summary>Angles already dealt with for this target, shot or skipped. Automatic mode only.</summary>
    private readonly HashSet<CameraPreset> _anglesDone = new();

    /// <summary>Whether this target's cover shot has been taken or skipped.</summary>
    private bool _coverDone;

    /// <summary>Which preset list this target draws its angles from, or null when it has none.</summary>
    private string? _presetKey;

    /// <summary>The shot being waited for, so the file that arrives knows where to go.</summary>
    private SessionShot? _shot;
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

    /// <summary>
    /// Hat visibility as the user had it before the session, restored when it ends.
    /// </summary>
    /// <remarks>
    /// The opposite errand to the weapon's. A session hides the weapon to keep it out of every shot, but
    /// a hat is the very thing being photographed when the item is a head piece — and Glamourer hiding
    /// hats is a setting plenty of people leave on permanently, which would otherwise produce a whole
    /// run of pictures of a bare head. So this is forced on for a head piece and put back for everything
    /// else, rather than being held at one value throughout.
    /// </remarks>
    private bool? _hatVisibleBefore;

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

    /// <summary>
    /// Outfits worth queueing for a bulk session: those with no picture yet.
    /// </summary>
    /// <remarks>
    /// A design card hidden by <see cref="Configuration.ShowGlamourerDesigns"/> is skipped. Wearing one
    /// works perfectly well, but photographing a card the user cannot see anywhere would leave a
    /// session standing there waiting for a shot of something with no name on screen.
    /// </remarks>
    private bool PhotographableOutfit(Outfit outfit) =>
        string.IsNullOrEmpty(outfit.ImagePath)
        && (_config.ShowGlamourerDesigns || !outfit.IsDesign);

    /// <summary>True when there are outfits without a preview image to photograph.</summary>
    public bool CanStartOutfits => FoldersReady && _config.Outfits.Any(PhotographableOutfit);

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
        foreach (var outfit in _config.Outfits.Where(PhotographableOutfit))
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

        // Captured before anything is hidden or shown, so both can be put back exactly as they were
        _weaponVisibleBefore = Plugin.Glamourer.GetWeaponVisible();
        _hatVisibleBefore    = Plugin.Glamourer.GetHatVisible();
        _log.Debug($"[Wardrobe] Session start: weapon visible was " +
                   $"{_weaponVisibleBefore?.ToString() ?? "unknown"}, hat visible was " +
                   $"{_hatVisibleBefore?.ToString() ?? "unknown"}");

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
        _anglesDone.Clear();

        // Anything still in hand belongs to a session that is over. Filing it now would attach a picture
        // to whatever the session had moved on to, or to nothing at all.
        _pendingShots.Clear();

        _coverDone     = false;
        _presetKey     = null;
        _shot          = null;
        ShotIndex      = 0;
        ShotLabel      = string.Empty;
        TakenForTarget = 0;
        RestoreWeaponVisibility();
        RestoreHatVisibility();
        StateChanged?.Invoke();
    }

    /// <summary>Gives up on the whole target, angles included, and moves to the next.</summary>
    public void Skip()
    {
        if (State != SessionState.WaitingForShot) return;
        NextTarget();
    }

    /// <summary>
    /// Finishes with the item being photographed and moves to the next, keeping what was taken.
    /// </summary>
    /// <remarks>
    /// The control manual mode is built around: nothing else ends an item there, so this is how a
    /// session gets through a queue. The same call serves Skip, because "I have what I want" and "I want
    /// none of this" differ only in whether any pictures were taken — and both mean the same thing to
    /// the queue.
    /// </remarks>
    public void NextTarget()
    {
        if (State is SessionState.Idle or SessionState.Done) return;

        _log.Debug($"[Wardrobe] Session: finished with '{CurrentName}' " +
                   $"({TakenForTarget} picture(s) taken)");

        _shot = null;
        CompletedCount++;
        WearNext();
    }

    /// <summary>
    /// Gives up on this angle only, moving to the next angle of the same target.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Skip"/> because with several angles queued the two are different
    /// wishes, and one control could not mean both: an angle that does not suit a particular piece —
    /// a back view of something with nothing on the back — is no reason to abandon the item whose
    /// cover shot you have already taken.
    /// </remarks>
    public void SkipShot()
    {
        if (State != SessionState.WaitingForShot) return;

        // NextShot marks this angle dealt with and asks what is left; when nothing is, it moves the
        // session on by itself — so skipping the last angle skips the target, which is what it means
        NextShot();
    }

    private void WearNext()
    {
        if (_queue.Count == 0)
        {
            DisposeWatcher();
            State = SessionState.Done;
            _shot = null;
            RestoreWeaponVisibility();
            RestoreHatVisibility();
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
        ShowHatIfNeeded();
        Plugin.Penumbra.RedrawPlayer();

        BeginTarget(target);
        NextShot();
    }

    /// <summary>
    /// Starts a target's run of shots: the cover, then every angle its preset list has ticked.
    /// </summary>
    /// <remarks>
    /// Nothing is queued up front. What is left to shoot is worked out from the preset list each time a
    /// shot finishes, so ticking an angle takes effect on the item in front of you rather than on the
    /// next one. Queueing at wear time read perfectly well in code and was wrong in practice: the moment
    /// anyone reaches for that tick is while looking at the piece they want another angle of, and the
    /// session would take the cover shot and move on — the feature appearing simply not to work.
    /// </remarks>
    private void BeginTarget(SessionTarget target)
    {
        _presetKey = target.Item?.Slot.ToString()
                  ?? (target.Outfit != null ? Configuration.OutfitPresetKey : null);

        _anglesDone.Clear();
        _coverDone      = false;
        _replacedExtras = false;
        _shot           = null;
        TakenForTarget  = 0;
        _targetSerial++;

        var extras = _presetKey == null ? 0 : _config.ExtraShotPresetsFor(_presetKey).Count;
        _log.Debug($"[Wardrobe] Session: '{CurrentName}' — {extras + 1} shot(s) queued " +
                   $"({(_presetKey == null ? "no preset list" : _presetKey)})");
    }

    /// <summary>
    /// The shot this target still owes, or null when it is finished.
    /// </summary>
    /// <remarks>
    /// Read from the live preset list every time, which is what lets a tick made mid-shoot count. An
    /// angle unticked, deleted or promoted to cover while the session waits simply stops being offered.
    /// <para>
    /// Angles are tracked by the preset object itself. Reloading the presets file mid-session replaces
    /// those objects, so an angle already shot could be offered once more — a repeat, which is a far
    /// better failure than the alternative of matching on names two presets are allowed to share.
    /// </para>
    /// </remarks>
    private SessionShot? NextAngle()
    {
        if (!_coverDone)
        {
            var cover = _presetKey != null ? _config.DefaultPresetFor(_presetKey) : null;
            return new SessionShot(cover, IsCover: true, ShotName(cover, "Cover"));
        }

        if (_presetKey == null) return null;

        foreach (var preset in _config.ExtraShotPresetsFor(_presetKey))
            if (!_anglesDone.Contains(preset))
                return new SessionShot(preset, IsCover: false, ShotName(preset, "Extra"));

        return null;
    }

    /// <summary>Marks whatever was being waited for as dealt with, whether it was shot or skipped.</summary>
    private void MarkShotDone()
    {
        if (_shot is null) return;

        if (_shot.IsCover)                  _coverDone = true;
        else if (_shot.Preset is { } preset) _anglesDone.Add(preset);

        _shot = null;
    }

    /// <summary>
    /// Whether this target's old extra pictures have already been cleared out for the new set.
    /// </summary>
    /// <remarks>
    /// Cleared when the first extra shot actually arrives rather than when the target is queued, so a
    /// session ended after the cover shot leaves the angles it never got round to re-taking in place.
    /// Doing it up front lost them to nothing more than a change of mind.
    /// </remarks>
    private bool _replacedExtras;

    /// <summary>A preset's name for the HUD, falling back to what kind of shot it is.</summary>
    private static string ShotName(CameraPreset? preset, string fallback) =>
        preset == null || string.IsNullOrWhiteSpace(preset.Name) ? fallback : preset.Name;

    /// <summary>
    /// Waits for the next shot of the current target, or moves on when there are none left.
    /// </summary>
    /// <remarks>
    /// The character is not re-worn between shots — it is already wearing the target, and the only
    /// thing that changes from one angle to the next is the camera.
    /// </remarks>
    private void NextShot()
    {
        // Whatever was being waited for is finished with, taken or skipped, before asking what is left
        MarkShotDone();

        // Manual mode never advances on its own. The next picture is another of the same item, and the
        // session sits here until Next Item or End Session — which is the whole point of it.
        var next = Manual
            ? new SessionShot(null, IsCover: TakenForTarget == 0, "Manual")
            : NextAngle();

        if (next is null)
        {
            CompletedCount++;
            WearNext();
            return;
        }

        _shot     = next;
        ShotIndex = Manual ? TakenForTarget + 1 : (_coverDone ? 1 : 0) + _anglesDone.Count + 1;
        ShotLabel = next.Label;

        if (next.Preset is { } preset) _camera.Apply(preset);

        _log.Debug(Manual
            ? $"[Wardrobe] Session: waiting — {TakenForTarget} picture(s) taken of '{CurrentName}' so far"
            : $"[Wardrobe] Session: waiting for shot {ShotIndex} of {ShotCount} — {ShotLabel}");

        _watchFrom = DateTime.UtcNow;
        State      = SessionState.WaitingForShot;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Screenshots that arrived while one was still being cropped, waiting their turn.
    /// </summary>
    /// <remarks>
    /// Cropping and writing a picture takes a moment, and a screenshot taken during it used to be
    /// dropped on the floor: the handler simply returned unless the session was idle and waiting. Nobody
    /// noticed while a session took one shot per item, and it became the obvious complaint the moment
    /// manual mode invited people to fire off several in a row. Queued instead, and worked through in
    /// order once each one is filed.
    /// <para>
    /// Only touched on the framework thread. The watcher raises its events on a thread-pool thread, so
    /// everything it hands over is marshalled across before any session state is read or written —
    /// which the old handler did not do either, having mutated <see cref="State"/> from wherever the
    /// watcher happened to call it.
    /// </para>
    /// </remarks>
    private readonly Queue<(string Path, int Target)> _pendingShots = new();

    /// <summary>
    /// Counts the targets a session has been through, so a queued screenshot knows which it was for.
    /// </summary>
    /// <remarks>
    /// A picture taken while the last one was being cropped belongs to the item that was on the
    /// character at the time. Without this, an accidental second shot in automatic mode — where the
    /// session moves on the instant the first lands — would be filed against whatever it moved on to:
    /// a picture of the previous piece becoming the next item's cover, which is worse than losing it.
    /// A burst in manual mode all carries the same number, because manual mode does not move on.
    /// </remarks>
    /// <remarks>
    /// Volatile because the watcher thread reads it: the number that matters is the one in force when
    /// the file appeared, not when the hop to the framework thread happens to run — by then a quick crop
    /// could already have moved the session on, and the picture would be tagged for the wrong item.
    /// </remarks>
    private volatile int _targetSerial;

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        // Only the two states that mean "a session is expecting pictures". Done and Idle drop them, as
        // they must: those are files the user took for their own reasons.
        if (State is not (SessionState.WaitingForShot or SessionState.Processing)) return;

        // Checked here rather than after the hop, against the watch mark as it stood when the file
        // appeared — a shot fired during processing predates the next shot's mark and would fail a
        // later comparison despite being exactly what was asked for
        try { if (new FileInfo(e.FullPath).CreationTimeUtc < _watchFrom) return; }
        catch { return; }

        var path   = e.FullPath;
        var serial = _targetSerial;

        _framework.RunOnFrameworkThread(() =>
        {
            _pendingShots.Enqueue((path, serial));
            ProcessPendingShot();
        });
    }

    /// <summary>
    /// Starts on the next screenshot in hand, if the session is free to take one.
    /// </summary>
    /// <remarks>
    /// Called when a file arrives and again after each one is filed, so a burst is worked through one at
    /// a time. Does nothing while another is being processed — that one calls back here when it is
    /// done — and nothing once the session has finished, which is what stops a late arrival being filed
    /// against whatever the queue moved on to.
    /// </remarks>
    private void ProcessPendingShot()
    {
        if (State != SessionState.WaitingForShot) return;

        string? source = null;

        while (_pendingShots.Count > 0)
        {
            var (path, target) = _pendingShots.Dequeue();

            // Taken for something the session has since moved past. Dropped rather than filed against
            // whatever is on the character now, which would be a picture of the wrong thing.
            if (target != _targetSerial)
            {
                _log.Debug($"[Wardrobe] Session: dropping a screenshot taken for the previous item");
                continue;
            }

            source = path;
            break;
        }

        if (source == null) return;

        State = SessionState.Processing;
        StateChanged?.Invoke();

        var item   = CurrentItem;
        var outfit = CurrentOutfit;
        var name   = CurrentName;
        var shot   = _shot;

        // The angle's name goes in the file name, so a folder of pictures says which is which without
        // opening them — and so the cover keeps the plain name it always had
        var stem = shot is { IsCover: false }
            ? $"{Sanitize(name)} - {Sanitize(shot.Label)}"
            : Sanitize(name);

        Task.Run(() =>
        {
            try
            {
                WaitForFile(source);

                var dest = UniquePath(_config.ImagesFolder, stem + ".jpg");
                // Portrait only for an outfit, and only when the setting is on: an item preview is a
                // close-up of one piece and has nothing to gain from a full-body frame
                CropAndConvert(source, dest, _config.CapturedImageSize,
                    portrait: outfit != null && _config.PortraitOutfitPreviews);

                _framework.RunOnFrameworkThread(() =>
                {
                    // The cover replaces the picture outright, exactly as a session always has —
                    // keeping the one it replaces would mean an item collected a spare picture every
                    // time its cover was re-taken. Any other angle joins the set behind it. The old
                    // file is left on disk either way, so it can be put back from the image browser.
                    var owner = (IImageOwner?)item ?? outfit;
                    if (owner != null)
                    {
                        if (shot is null or { IsCover: true })
                        {
                            owner.ImagePath = dest;
                        }
                        else
                        {
                            // The first new angle to land is what retires the old set — see
                            // _replacedExtras for why it happens here and not when the target was queued
                            if (!_replacedExtras)
                            {
                                owner.ClearExtraImages();
                                _replacedExtras = true;
                            }

                            owner.AddImage(dest);
                        }
                    }

                    TakenForTarget++;
                    _config.Save();
                    _log.Debug($"[Wardrobe] Session: filed picture {TakenForTarget} of " +
                               $"'{name}' as {(shot is null or { IsCover: true } ? "the cover" : shot.Label)}" +
                               (_pendingShots.Count > 0 ? $", {_pendingShots.Count} more waiting" : string.Empty));

                    // NextShot leaves the session waiting again — or on the next item, or finished — and
                    // only then can anything queued behind this one be taken up
                    NextShot();
                    ProcessPendingShot();
                });
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[Wardrobe] Screenshot session: failed to process screenshot");
                _framework.RunOnFrameworkThread(() =>
                {
                    // The failed one is gone, but the rest of a burst is not its fault
                    NextShot();
                    ProcessPendingShot();
                });
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
    /// Shows the hat while a head piece is being photographed, and puts it back for everything else.
    /// </summary>
    /// <remarks>
    /// Forced on rather than left alone, because a hat hidden in Glamourer photographs as a bare head
    /// and the session would fill the wardrobe with pictures of nothing. Only for a head piece: a hat
    /// shown over a shot of boots is the user's business, and the value they had is what goes back.
    /// <para>
    /// Nothing is changed at all when the original could not be read — the same rule the weapon
    /// follows, since forcing a state onto someone whose preference is unknown is how a session ends up
    /// having quietly changed their character.
    /// </para>
    /// </remarks>
    private void ShowHatIfNeeded()
    {
        var slot = CurrentItem?.Slot ?? EquipSlot.Unknown;

        if (slot == EquipSlot.Head)
        {
            Plugin.Glamourer.SetHatVisible(true);
            return;
        }

        if (_hatVisibleBefore is { } wasVisible)
            Plugin.Glamourer.SetHatVisible(wasVisible);
    }

    /// <summary>Puts hat visibility back exactly as it was before the session.</summary>
    private void RestoreHatVisibility()
    {
        if (_hatVisibleBefore is not { } wasVisible)
        {
            _hatVisibleBefore = null;
            return;
        }

        Plugin.Glamourer.SetHatVisible(wasVisible);
        _log.Debug($"[Wardrobe] Session end: hat visibility restored to {wasVisible}");
        _hatVisibleBefore = null;
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
