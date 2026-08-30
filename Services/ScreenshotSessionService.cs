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

            // The two are opposite ends of the same dial — one waits on a person for every picture,
            // the other waits on nobody — so turning this on cannot leave the other on as well
            if (value) _config.AutoScreenshotMode = false;
            _config.Save();

            // Turning this on has just turned automatic mode off, so a countdown left running would
            // fire a shot for a session that is no longer taking them
            if (value) CancelAutoShot();

            // Turned on while a session is waiting, this simply means the current item stops advancing
            // by itself; turned off, the queued angles take over again from the next shot. Neither
            // needs anything undone here — the decision is read fresh every time a shot finishes.
            _log.Debug($"[Wardrobe] Session: manual mode {(value ? "on" : "off")}");
        }
    }

    /// <summary>Whether the game will take a screenshot when asked at all.</summary>
    /// <remarks>
    /// False means the game's screenshot task could not be found, which no setting here can do
    /// anything about — so the opt-in is not offered either, a tick box that cannot work being worse
    /// than none.
    /// </remarks>
    public bool AutoSupported => _shutter.Available;

    /// <summary>
    /// Whether fully automatic sessions are turned on in Experimental, and can be had at all.
    /// </summary>
    /// <remarks>
    /// The opt-in, not the mode: this is what decides whether the <b>Super Screenshot Session</b>
    /// button is on the toolbar and whether the HUD offers the tick. What a given session actually
    /// does is <see cref="Auto"/>.
    /// </remarks>
    public bool AutoEnabled
    {
        get => AutoSupported && _config.AutoScreenshotEnabled;
        set
        {
            if (_config.AutoScreenshotEnabled == value) return;
            _config.AutoScreenshotEnabled = value;

            // Switched off, nothing is left half-automatic: the mode goes with it, so a session
            // running at the time simply stops taking its own pictures
            if (!value) _config.AutoScreenshotMode = false;
            _config.Save();

            _log.Debug($"[Wardrobe] Session: automatic sessions {(value ? "enabled" : "disabled")}");

            if (!value) CancelAutoShot();
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// The session takes its own screenshots, so a whole wardrobe can be photographed unattended.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="Configuration.AutoScreenshotMode"/>, gated on <see cref="AutoEnabled"/>,
    /// and exclusive with <see cref="Manual"/> for the reason given there. Set by whichever toolbar
    /// button started the run, and settable mid-session: turned on while a session is already waiting,
    /// the shot it is waiting for is the first one taken for you.
    /// </remarks>
    public bool Auto
    {
        get => AutoEnabled && _config.AutoScreenshotMode;
        set
        {
            if (value && !AutoEnabled) return;
            if (_config.AutoScreenshotMode == value) return;
            _config.AutoScreenshotMode = value;
            if (value) _config.ManualScreenshotMode = false;
            _config.Save();

            _log.Debug($"[Wardrobe] Session: automatic mode {(value ? "on" : "off")}");

            if (value)
            {
                // Only while there is a session to drive. Idle, BeginSession does the subscribing, and
                // a per-frame handler held open by a tickbox nobody is using is a handler for nothing.
                if (State != SessionState.Idle) SubscribeTick();
                if (State == SessionState.WaitingForShot) ArmAutoShot();
            }
            else
            {
                CancelAutoShot();
            }

            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Photographs every item without a picture, taking the screenshots itself.
    /// </summary>
    /// <remarks>
    /// What the <b>Super Screenshot Session</b> button presses. The pair with <see cref="Start"/>,
    /// which clears the mode instead — between them the two buttons say which kind of run this is,
    /// rather than leaving it to a tick box in a panel behind the character.
    /// </remarks>
    public void StartSuper()
    {
        if (!AutoEnabled) return;
        Auto = true;
        Start();
    }

    /// <summary>An automatic session is held where it is until it is resumed.</summary>
    /// <remarks>
    /// The answer to the one thing a fully automated run cannot do by itself: stop, so you can move the
    /// camera. Sitting through the countdown to fix a framing you can see is wrong would otherwise mean
    /// ending the session.
    /// </remarks>
    public bool AutoPaused { get; private set; }

    /// <summary>Seconds until the next automatic shot, or zero when none is counting down.</summary>
    public float AutoCountdown =>
        Auto && !AutoPaused && !_autoFired && State == SessionState.WaitingForShot
            ? Math.Max(0f, (float)(_autoAt - DateTime.UtcNow).TotalSeconds)
            : 0f;

    /// <summary>True while an automatic shot has been asked for and the picture has not arrived.</summary>
    public bool AutoShotPending => Auto && _autoFired;

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
    private readonly GameScreenshotService _shutter;

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
        IFramework framework, IPluginLog log, CameraService camera, GameScreenshotService shutter)
    {
        _wardrobe  = wardrobe;
        _config    = config;
        _framework = framework;
        _log       = log;
        _camera    = camera;
        _shutter   = shutter;
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

        // Held for the length of the session: the shots below force the hat and the weapon for
        // themselves, and the wardrobe putting the worn outfit's own toggles back after every redraw
        // would take them straight off again, mid-session and between two pictures
        _wardrobe.OutfitVisibilityHeld = true;

        // Captured before anything is hidden or shown, so both can be put back exactly as they were
        _weaponVisibleBefore = Plugin.Glamourer.GetWeaponVisible();
        _hatVisibleBefore    = Plugin.Glamourer.GetHatVisible();
        _log.Debug($"[Wardrobe] Session start: weapon visible was " +
                   $"{_weaponVisibleBefore?.ToString() ?? "unknown"}, hat visible was " +
                   $"{_hatVisibleBefore?.ToString() ?? "unknown"}");

        // Every format the game can be set to write and GDI+ can read back. Watching only PNG meant a
        // player whose game was set to JPG ran a whole session against a folder that was filling up in
        // front of it, and never filed one picture. DDS is deliberately absent: the game will write it,
        // but nothing downstream of here can open it, so it is caught and named at the give-up instead
        // of being picked up and failing halfway through a crop.
        _watcher = new FileSystemWatcher(_config.ScreenshotsFolder)
        {
            NotifyFilter        = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        foreach (var pattern in WatchedShotTypes) _watcher.Filters.Add(pattern);
        _watcher.Created += OnFileCreated;

        AutoPaused = false;
        _filedThisSession = 0;
        CancelAutoShot();

        if (Auto)
        {
            SubscribeTick();
            LogAutoBanner();
        }

        WearNext();
    }

    public void Stop()
    {
        DisposeWatcher();
        UnsubscribeTick();
        CancelAutoShot();
        AutoPaused    = false;
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
        _wardrobe.OutfitVisibilityHeld = false;
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
            UnsubscribeTick();
            CancelAutoShot();

            if (Auto)
                _log.Information($"[Wardrobe] Automatic session finished: {CompletedCount} of " +
                                 $"{TotalCount} targets, {_filedThisSession} picture(s) filed.");

            State = SessionState.Done;
            _shot = null;
            _wardrobe.OutfitVisibilityHeld = false;
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

        // A redraw is the slowest thing a session does and the one an automatic shot must not race:
        // fire before it finishes and the picture is of the previous item, or of nobody at all
        _redrawReadyAt = DateTime.UtcNow.AddSeconds(RedrawSettleSeconds);

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

        if (Auto) ArmAutoShot();

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
                    _missedInARow = 0;
                    _filedThisSession++;
                    _config.Save();

                    var filed = $"[Wardrobe] Session: filed picture {TakenForTarget} of " +
                                $"'{name}' as {(shot is null or { IsCover: true } ? "the cover" : shot.Label)}" +
                                (_pendingShots.Count > 0 ? $", {_pendingShots.Count} more waiting" : string.Empty);

                    // Information while the session is running itself, for the reason given on
                    // LogAutoBanner: nobody is watching, so the log is the only record there will be
                    if (Auto) _log.Information(filed);
                    else      _log.Debug(filed);

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

    // ── Taking the shot ourselves ──────────────────────────────────

    /// <summary>
    /// Extra time given to the first shot of each target, on top of the configured delay.
    /// </summary>
    /// <remarks>
    /// A redraw is what separates the cover shot from the rest: the angles that follow only move the
    /// camera, but the cover comes straight after the character has been stripped, dressed and told to
    /// redraw. Added here rather than left to the delay setting so nobody has to raise the wait for
    /// every shot to cure a problem that affects one of them.
    /// </remarks>
    private const double RedrawSettleSeconds = 1.5;

    /// <summary>How long to wait for a picture to appear before assuming the shot did not happen.</summary>
    /// <remarks>
    /// Generous on purpose. The cost of being wrong is a second shot of the same thing filed as an
    /// extra picture, against a wait that only has to be sat through when something has genuinely gone
    /// wrong — and the game is asked whether a shot is still in flight before this is acted on anyway.
    /// </remarks>
    private const double ShotTimeoutSeconds = 12;

    /// <summary>
    /// How long a shot the game says is still in flight may stay in flight before it is called stuck.
    /// </summary>
    /// <remarks>
    /// <see cref="ShotTimeoutSeconds"/> is extended for as long as the game reports a shot still being
    /// written, which is right for a slow disk and wrong for a client whose screenshot thread has
    /// stopped answering: the flag never clears, so the extension never stops, and an unattended run
    /// waits on it forever without a word in the log. This is the point past which waiting longer is no
    /// longer telling anyone anything they do not already know.
    /// </remarks>
    private const double StuckShotGiveUpSeconds = 45;

    /// <summary>The screenshot formats a session will pick up out of the watched folder.</summary>
    private static readonly string[] WatchedShotTypes = { "*.png", "*.jpg", "*.jpeg", "*.bmp" };

    /// <summary>Times to ask again before giving up on a shot and moving on.</summary>
    private const int MaxShotRetries = 2;

    /// <summary>Shots that may go missing in a row before an automatic run stops and says so.</summary>
    private const int MaxMissesBeforePausing = 3;

    /// <summary>How long the game may refuse screenshots before the session stops asking.</summary>
    /// <remarks>
    /// A cutscene, a loading screen or a shot already in flight all pass in a moment and are simply
    /// waited out. Something that does not pass is not going to, and an unattended session grinding
    /// against it forever is worse than one that stops and says so.
    /// </remarks>
    private const double BlockedGiveUpSeconds = 30;

    /// <summary>The range the delay between automatic shots is allowed to take, in seconds.</summary>
    /// <remarks>
    /// The floor is not nothing: a shot with no wait at all lands while the camera is still moving to
    /// the angle it was asked for, which is a picture of the last angle. The ceiling is where a run
    /// over a large wardrobe stops being quicker than doing it by hand.
    /// </remarks>
    public const float MinAutoDelay = 0.5f;

    /// <inheritdoc cref="MinAutoDelay"/>
    public const float MaxAutoDelay = 20f;

    private bool      _tickSubscribed;
    private bool      _autoFired;
    private DateTime  _autoAt;
    private DateTime  _autoTimeoutAt;
    private DateTime  _shotFiredAt;
    private DateTime  _redrawReadyAt;
    private DateTime? _blockedSince;
    private int       _autoRetries;

    /// <summary>Shots asked for and never filed, since the last picture that was.</summary>
    private int       _missedInARow;

    /// <summary>Time left on the countdown when it was paused, so resuming does not restart it.</summary>
    private TimeSpan? _pausedRemaining;

    /// <summary>Holds an automatic session where it is, or lets it carry on.</summary>
    /// <remarks>
    /// The countdown is kept rather than restarted, so a pause taken to nudge the camera does not cost
    /// the whole wait again on the way back.
    /// </remarks>
    public void SetAutoPaused(bool paused)
    {
        if (AutoPaused == paused) return;
        AutoPaused = paused;

        if (paused)
        {
            _pausedRemaining = _autoFired ? null : _autoAt - DateTime.UtcNow;
        }
        else
        {
            // Whatever stopped the run is being resumed past, so the tally that stopped it starts again
            _missedInARow = 0;

            if (_pausedRemaining is { } left)
            {
                _autoAt          = DateTime.UtcNow + (left > TimeSpan.Zero ? left : TimeSpan.Zero);
                _pausedRemaining = null;
            }
            else if (State == SessionState.WaitingForShot)
            {
                // Paused by the session itself rather than by a person, so there is no countdown left
                // to put back — the shot it stopped on is simply asked for again
                ArmAutoShot();
            }
        }

        _log.Debug($"[Wardrobe] Session: automatic mode {(paused ? "paused" : "resumed")}");
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Takes the shot the session is waiting for, now, without a keypress or a countdown.
    /// </summary>
    /// <remarks>
    /// Offered in every mode, not only the automatic one. Manual mode is about deciding when a picture
    /// is worth taking rather than about which key takes it, and by the time the session is waiting the
    /// character is posed already — reaching for the screenshot key at that point is a step, not a
    /// decision.
    /// </remarks>
    /// <returns>False when the game would not take one, which is worth saying rather than swallowing.</returns>
    public bool TakeShotNow()
    {
        if (State != SessionState.WaitingForShot)
        {
            SetShutterProblem("The session is not waiting for a picture right now.");
            return false;
        }

        return FireShot();
    }

    /// <summary>The last reason the game gave for not taking a picture, and when it gave it.</summary>
    /// <remarks>
    /// Kept so the button can answer for itself. Pressing <b>Shoot Now</b> against a game that was
    /// refusing screenshots used to do nothing at all, visibly or in the log, which reads as a broken
    /// button rather than a client that said no — and left a bug report with nothing in it to go on.
    /// </remarks>
    public string?  ShutterProblem   { get; private set; }

    /// <inheritdoc cref="ShutterProblem"/>
    public DateTime ShutterProblemAt { get; private set; }

    private void SetShutterProblem(string? problem)
    {
        ShutterProblem   = problem;
        ShutterProblemAt = DateTime.UtcNow;
        if (problem != null) StateChanged?.Invoke();
    }

    /// <summary>Whatever the game will currently say about its screenshot task.</summary>
    public ShutterState ReadShutter() => _shutter.Read();

    /// <summary>Pictures filed since this session began, for the line it ends on.</summary>
    private int _filedThisSession;

    /// <summary>
    /// Writes down what an automatic run is about to do, before it does it.
    /// </summary>
    /// <remarks>
    /// At Information rather than Debug, and everything in one place, because an unattended session's
    /// log is the only record of it: nobody is watching, so a run that went wrong has to be
    /// diagnosable afterwards from what was written down. Every line here is something that has been
    /// the cause of a bad run — the wrong folder, no angles saved, not being in GPose — and each one
    /// is far easier to read back than to reconstruct from the pictures that came out.
    /// </remarks>
    private void LogAutoBanner()
    {
        // Worked out from the queue rather than from the wardrobe, so it answers the question that
        // matters — whether the things about to be photographed have an angle — rather than how many
        // slots have one in general
        var keys    = _queue.Select(t => t.Item?.Slot.ToString() ?? Configuration.OutfitPresetKey)
                            .Distinct()
                            .ToList();
        var without = keys.Where(k => _config.PresetsFor(k).Count == 0).ToList();

        _log.Information("[Wardrobe] Automatic session starting.");
        _log.Information($"[Wardrobe]   targets        : {TotalCount}");
        _log.Information($"[Wardrobe]   delay per shot : {Math.Clamp(_config.AutoScreenshotDelay, MinAutoDelay, MaxAutoDelay):0.0}s " +
                         $"(+{RedrawSettleSeconds:0.0}s after a redraw)");
        _log.Information($"[Wardrobe]   in GPose       : {_camera.InGpose}");
        _log.Information($"[Wardrobe]   angles ready   : {keys.Count - without.Count} of {keys.Count} " +
                         "preset list(s) used by this run");
        _log.Information($"[Wardrobe]   strip others   : {StripOthers}");
        _log.Information($"[Wardrobe]   image size     : {_config.CapturedImageSize}px" +
                         (_config.PortraitOutfitPreviews ? ", portrait outfits" : string.Empty));
        _log.Information($"[Wardrobe]   watching       : {_config.ScreenshotsFolder}");
        _log.Information($"[Wardrobe]   writing to     : {_config.ImagesFolder}");

        if (without.Count > 0)
            _log.Warning($"[Wardrobe] No camera angle saved for: {string.Join(", ", without)}. Those " +
                         "will be photographed from wherever the camera is standing.");

        if (!_camera.InGpose)
            _log.Warning("[Wardrobe] Not in GPose — camera angles will not be applied and every " +
                         "picture will be taken from wherever the camera is standing.");
    }

    /// <summary>Starts the countdown to the next automatic shot.</summary>
    private void ArmAutoShot()
    {
        _autoFired       = false;
        _autoRetries     = 0;
        _blockedSince    = null;
        _pausedRemaining = null;

        var delay = Math.Clamp(_config.AutoScreenshotDelay, MinAutoDelay, MaxAutoDelay);
        var due   = DateTime.UtcNow.AddSeconds(delay);

        // Never before the character has had time to come back from its redraw, however short the
        // delay is set — the two waits overlap rather than adding up
        _autoAt = due < _redrawReadyAt ? _redrawReadyAt : due;
    }

    /// <summary>Forgets any countdown or shot in flight, leaving the session waiting on a person.</summary>
    private void CancelAutoShot()
    {
        _autoFired       = false;
        _autoRetries     = 0;
        _missedInARow    = 0;
        _blockedSince    = null;
        _pausedRemaining = null;
    }

    /// <summary>Asks the game for a picture, and starts the clock on it arriving.</summary>
    private bool FireShot()
    {
        if (!_shutter.Take(out var refusal))
        {
            SetShutterProblem(refusal);
            return false;
        }

        var now = DateTime.UtcNow;
        _autoFired     = true;
        _shotFiredAt   = now;
        _autoTimeoutAt = now.AddSeconds(ShotTimeoutSeconds);
        _blockedSince  = null;
        SetShutterProblem(null);

        _log.Information($"[Wardrobe] Session: took a screenshot of '{CurrentName}' ({ShotLabel})");
        StateChanged?.Invoke();
        return true;
    }

    private void SubscribeTick()
    {
        if (_tickSubscribed) return;
        _framework.Update += OnFrameworkTick;
        _tickSubscribed = true;
    }

    private void UnsubscribeTick()
    {
        if (!_tickSubscribed) return;
        _framework.Update -= OnFrameworkTick;
        _tickSubscribed = false;
    }

    /// <summary>
    /// Drives an automatic session: counts down to each shot, takes it, and notices when one is lost.
    /// </summary>
    /// <remarks>
    /// Everything after the shot is the ordinary session's own doing — the folder watcher sees the
    /// file, it is cropped and filed, and <see cref="NextShot"/> leaves the session waiting again,
    /// which is what starts the next countdown. So this only ever has one thing to decide: whether it
    /// is time.
    /// </remarks>
    private void OnFrameworkTick(IFramework framework)
    {
        if (!Auto || AutoPaused) return;

        // Processing is a picture already in hand being cropped; anything else is not a session waiting
        if (State != SessionState.WaitingForShot) return;

        var now = DateTime.UtcNow;

        if (_autoFired)
        {
            if (now < _autoTimeoutAt) return;

            // Still being written. A slow disk is exactly what the timeout must not mistake for a
            // failure, so it is extended rather than the shot being taken twice — but only up to the
            // point where a shot that is still in flight has plainly stopped being in flight.
            if (_shutter.Pending)
            {
                if ((now - _shotFiredAt).TotalSeconds < StuckShotGiveUpSeconds)
                {
                    _autoTimeoutAt = now.AddSeconds(ShotTimeoutSeconds);
                    return;
                }

                var stuck = _shutter.Read();
                _log.Error("[Wardrobe] Session: the game accepted the screenshot request " +
                           $"{StuckShotGiveUpSeconds:0} seconds ago and has never finished it. Its " +
                           "screenshot task is stuck, which also stops your own screenshot key working " +
                           "until the game is restarted. Pausing the run.");
                _log.Error($"[Wardrobe] Session: shutter state — allowed: {stuck.CanTake}, " +
                           $"in flight: {stuck.Requested}, last result: {stuck.Result}, " +
                           $"format: {stuck.Format}");

                SetShutterProblem(
                    "The game accepted the screenshot but never finished it. Its screenshot task is " +
                    "stuck — your own screenshot key will not work either until the game is restarted.");

                _autoFired = false;
                SetAutoPaused(true);
                return;
            }

            if (_autoRetries < MaxShotRetries)
            {
                _autoRetries++;
                _log.Warning($"[Wardrobe] Session: no picture arrived for '{CurrentName}' " +
                             $"({ShotLabel}) — asking again ({_autoRetries} of {MaxShotRetries})");
                _autoFired = false;
                _autoAt    = now;
                return;
            }

            var missed = _shutter.Read();
            _log.Warning($"[Wardrobe] Session: giving up on '{CurrentName}' ({ShotLabel}) — no " +
                         "screenshot appeared in the watched folder. Check that Settings → " +
                         "Screenshots points at the folder the game actually saves to.");
            _log.Warning($"[Wardrobe] Session: shutter state — allowed: {missed.CanTake}, " +
                         $"in flight: {missed.Requested}, last result: {missed.Result}, " +
                         $"format: {missed.Format}");

            // The one refusal that is not about the folder at all. Nothing here can open a DDS, so a
            // game set to write them will fill the folder and file none of it, however right the path is
            if (string.Equals(missed.Format, "Dds", StringComparison.OrdinalIgnoreCase))
                _log.Warning("[Wardrobe] Session: the game is saving screenshots as DDS, which the " +
                             "wardrobe cannot read. Set Character Configuration → screenshot format " +
                             "to PNG or JPG.");
            _autoFired = false;

            // Three of these in a row is not three unlucky shots, it is a session photographing a
            // whole wardrobe into a folder nobody is watching — which it would otherwise do all the
            // way to the end, leaving hundreds of screenshots on disk and no pictures assigned
            if (++_missedInARow >= MaxMissesBeforePausing)
            {
                _log.Warning("[Wardrobe] Session: nothing has been filed for " +
                             $"{_missedInARow} shots running — pausing the automatic run.");

                // Held on the shot that failed rather than skipped past. Whatever is wrong is a
                // question about this item and this angle, and a paused session that has moved three
                // items on is a session that cannot answer it.
                SetAutoPaused(true);
                return;
            }

            SkipShot();
            return;
        }

        if (now < _autoAt) return;

        if (FireShot()) return;

        // The game will not take one at the moment. Come back shortly rather than treating a passing
        // refusal as a failure — but do not do it forever.
        _blockedSince ??= now;
        if ((now - _blockedSince.Value).TotalSeconds >= BlockedGiveUpSeconds)
        {
            _log.Warning("[Wardrobe] Session: the game has been refusing screenshots for " +
                         $"{BlockedGiveUpSeconds:0} seconds — pausing the automatic run.");

            SetShutterProblem("The game has been refusing screenshots for " +
                              $"{BlockedGiveUpSeconds:0} seconds, so the run has stopped here.");
            SetAutoPaused(true);
            return;
        }

        _autoAt = now.AddSeconds(0.5);
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

        // An outfit that says what it does with the weapon is photographed saying it. Only where it
        // has an opinion — with none, which is the default, the session's own rule below stands
        if (CurrentOutfit?.WeaponVisible is { } wanted)
        {
            Plugin.Glamourer.SetWeaponVisible(wanted);
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

        // As with the weapon above: a hood is part of the look, and an outfit that hides the headgear
        // has to be photographed with it hidden or the picture is of a different outfit
        if (CurrentOutfit?.HatVisible is { } wanted)
        {
            Plugin.Glamourer.SetHatVisible(wanted);
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

    public void Dispose()
    {
        UnsubscribeTick();
        DisposeWatcher();
    }
}
