using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Photo;

namespace WardrobePlugin.Services;

/// <summary>
/// What the game's screenshot task is doing right now, in a form that can be read out loud.
/// </summary>
/// <remarks>
/// Written for one purpose: a bug report from a machine nobody here can reproduce on. Every field is
/// something the game decides and the plugin only obeys, so a reader who has "nothing happens when I
/// press the button" can say which of the several possible nothings it is.
/// </remarks>
public readonly record struct ShutterState(
    bool   Available,
    bool   CanTake,
    bool   Requested,
    string Result,
    string Format,
    long   Timestamp,
    double InFlightSeconds,
    int    Completed,
    bool   Worker,
    string SaveFolder);

/// <summary>
/// Presses the game's own screenshot button, so a session does not have to wait for the user to.
/// </summary>
/// <remarks>
/// The last piece missing from an automated photoshoot. Everything else was already in place —
/// wearing each item in turn, moving the camera to each saved angle, watching the screenshots folder,
/// cropping what lands and filing it — and all of it sat waiting on one human keypress per picture.
/// <para>
/// This asks the game for that picture through <c>ScreenShot::ScheduleScreenShot</c>, the same call
/// the screenshot keybind ends up in. Going through the game rather than capturing the window
/// ourselves is what keeps the result identical to a picture taken by hand: the game's own file
/// format, its own folder, its own framing, and no plugin windows in it. The session's existing
/// folder watcher then picks the file up without knowing anything happened differently.
/// </para>
/// </remarks>
public unsafe class GameScreenshotService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;

    private readonly IKeyState     _keys;
    private readonly Configuration _config;

    public GameScreenshotService(IPluginLog log, IFramework framework, IGameInteropProvider interop,
                                 IKeyState keys, Configuration config)
    {
        _log       = log;
        _framework = framework;
        _keys      = keys;
        _config    = config;

        // Watching the game make its own requests is the only way to see what a working call looks
        // like. Nothing here changes what the game does — see OnScheduled.
        try
        {
            _scheduleHook = interop.HookFromAddress<ScheduleScreenShotDelegate>(
                ScreenShot.Addresses.ScheduleScreenShot.Value, OnScheduled);
            _scheduleHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Automatic screenshot: could not watch the game's own " +
                             "screenshot requests. Diagnostics will be thinner; nothing else changes.");
        }
    }

    // ── Watching the game's own requests ──────────────────────────────────────

    private unsafe delegate bool ScheduleScreenShotDelegate(
        ScreenShot* self, delegate* unmanaged<void*, int, void> callback, void* callbackParam);

    private readonly Hook<ScheduleScreenShotDelegate>? _scheduleHook;

    /// <summary>True while this class is the one calling, so the log can tell the two apart.</summary>
    private bool _ours;

    /// <summary>The callback the game passes for its own screenshots, once one has been seen.</summary>
    /// <remarks>
    /// Kept so the wardrobe can make the game's exact call rather than one that merely resembles it.
    /// With the object and the parameter now known to match, this pointer is the last thing that
    /// differs between a request that takes a picture and one that hangs — so being able to borrow
    /// it turns the question into an experiment with one variable in it.
    /// <para>
    /// Only ever a pointer the game itself passed, in this client session. It is never guessed, never
    /// saved and never carried across a restart, because a function address means nothing once the
    /// game has been loaded somewhere else.
    /// </para>
    /// </remarks>
    private delegate* unmanaged<void*, int, void> _gameCallback;

    /// <summary>Whether the completion counter means anything for the way shots are being asked for.</summary>
    /// <remarks>
    /// Only the direct call passes the wardrobe's own callback, so only then can the game report
    /// back to it. Pressing the key hands the whole request to the game, which uses its own — and a
    /// counter stuck at zero would read as a fault when it is the normal, working case.
    /// </remarks>
    public bool CompletionCounted => !_config.UseScreenshotKey;

    /// <summary>Whether the game's own callback has been seen and can be borrowed.</summary>
    public bool CanReplayGameCall => _gameCallback != null;

    /// <summary>Whether to make the game's exact call rather than pass the wardrobe's own callback.</summary>
    public bool ReplayGameCall { get; set; }

    /// <summary>
    /// Watches every screenshot request the game makes, including its own keybind's.
    /// </summary>
    /// <remarks>
    /// A pure observer: it logs and calls the original, unchanged. It exists because the wardrobe's
    /// request and the screenshot key's request go through this one function, one works and one does
    /// not, and the arguments are the only thing that differs. Chief among them the object the
    /// function is called on — if the game's differs from <see cref="ScreenShot.Instance"/>, then the
    /// wardrobe has been asking the wrong screenshot for a picture all along, which no amount of
    /// reading the right one's fields would ever have shown.
    /// </remarks>
    private bool OnScheduled(ScreenShot* self, delegate* unmanaged<void*, int, void> callback,
                             void* callbackParam)
    {
        var accepted = _scheduleHook!.Original(self, callback, callbackParam);

        // Only the game's own, and only the first one — a later request while the wardrobe is
        // borrowing this pointer would otherwise record the borrow as though the game had made it
        if (!_ours && _gameCallback == null && callback != null)
        {
            _gameCallback = callback;
            _log.Information($"[Wardrobe] Shutter watch: remembered the game's own callback " +
                             $"(0x{(nint)callback:X}). The wardrobe can now make its exact call.");
        }

        var instance = ScreenShot.Instance();
        var line =
            $"[Wardrobe] Shutter watch: {(_ours ? "the wardrobe" : "THE GAME")} asked for a screenshot — " +
            $"accepted: {accepted}, this: 0x{(nint)self:X}, Instance(): 0x{(nint)instance:X}" +
            (self == instance ? " (same)" : " (DIFFERENT)") +
            $", callback: 0x{(nint)callback:X}, param: 0x{(nint)callbackParam:X}";

        // The player's own screenshots come through here too, and there can be a great many of them.
        // Loud while a probe is watching, because that is when someone is reading; quiet otherwise
        if (Probing || _ours) _log.Information(line);
        else                  _log.Debug(line);

        return accepted;
    }

    /// <summary>How many screenshots the game has told us it finished.</summary>
    /// <remarks>
    /// Written by <see cref="OnShotFinished"/>, which the game calls on whichever thread finished the
    /// picture, and read from the framework thread. Nothing here is a decision — it is a counter, kept
    /// so the diagnostics can say whether the game has ever completed one of our requests at all.
    /// </remarks>
    private static int _completed;

    /// <summary>The result code the game passed to the last completion callback, or -1 for none.</summary>
    private static int _lastCallbackResult = -1;

    /// <summary>What the game said the last completed shot came to, or null if none has completed.</summary>
    public static int? LastCompletionResult
    {
        get
        {
            var code = Volatile.Read(ref _lastCallbackResult);
            return code < 0 ? null : code;
        }
    }

    /// <summary>When the last request we made was accepted, or null if we have not made one.</summary>
    private DateTime? _requestedAt;

    /// <summary>When we first saw the game's in-flight flag set without having asked for it.</summary>
    private DateTime? _foreignPendingSince;

    /// <summary>Whether the callback we hand the game currently points into this assembly.</summary>
    private bool _callbackLive;

    /// <summary>
    /// How long a shot we asked for may stay in flight before the shutter is treated as jammed.
    /// </summary>
    /// <remarks>
    /// Long enough that a slow disk writing a 4K PNG is never mistaken for a stall. The session's own
    /// give-up sits at the same 45 seconds, so the two agree about when a shot has stopped happening.
    /// </remarks>
    private const double JammedAfterSeconds = 45;

    /// <summary>
    /// How long a flag nobody here set may stand before it is treated as left over.
    /// </summary>
    /// <remarks>
    /// Short, because this is the case where the game was already jammed when the session started —
    /// there is nothing of ours in flight to wait for. It is not zero only so that a screenshot the
    /// user took by hand a moment earlier is allowed to finish on its own.
    /// </remarks>
    private const double ForeignJammedAfterSeconds = 5;

    /// <summary>Whether the game exposes its screenshot task at all.</summary>
    /// <remarks>
    /// False means an automated session cannot work, which is worth saying before one is started
    /// rather than leaving it counting down against a shutter that will never fire.
    /// </remarks>
    public bool Available => ScreenShot.Instance() != null;

    /// <summary>True while a screenshot has been asked for and not yet written.</summary>
    /// <remarks>
    /// What tells a waiting session the difference between a shot that is taking its time and one
    /// that never happened, so a slow disk extends the wait instead of triggering a second shot.
    /// </remarks>
    public bool Pending
    {
        get
        {
            var inst = ScreenShot.Instance();
            return inst != null && inst->ScreenShotRequested;
        }
    }

    /// <summary>Everything the game will say about its screenshot task, for a diagnostics readout.</summary>
    public ShutterState Read()
    {
        var inst = ScreenShot.Instance();
        if (inst == null)
            return new ShutterState(false, false, false, "-", "-", 0, 0,
                                    Volatile.Read(ref _completed), false, "-");

        return new ShutterState(
            true,
            inst->CanTakeScreenShot,
            inst->ScreenShotRequested,
            inst->ScreenShotResult.ToString(),
            inst->ScreenShotFileFormat.ToString(),
            inst->ScreenShotTimestamp,
            inst->ScreenShotRequested ? InFlightSeconds(DateTime.UtcNow) : 0,
            Volatile.Read(ref _completed),
            inst->ThreadPtr != null,
            ReadSaveFolder(inst));
    }

    /// <summary>Where the game's own screenshot worker believes it is writing.</summary>
    /// <remarks>
    /// Not the plugin's watched folder and not the game's config option - the path the worker thread
    /// is actually holding. It is here because the folder is the one part of the game's screenshot
    /// path that can be wrong in a way nothing else reports: a request is accepted, the flag goes up,
    /// and a worker that cannot open a file where it is pointed never finishes, so nothing completes
    /// and nothing is written. A path that does not exist on disk names that outright.
    /// </remarks>
    private static string ReadSaveFolder(ScreenShot* inst)
    {
        if (inst->ThreadPtr == null) return "-";

        try
        {
            var path = inst->ThreadPtr->ScreenShotStorageDirectory.ToString();
            return string.IsNullOrWhiteSpace(path) ? "(empty)" : path;
        }
        catch
        {
            // A fixed buffer holding something that is not a path is itself worth reporting, and is
            // not worth throwing out of a diagnostics readout over
            return "(unreadable)";
        }
    }

    /// <summary>How long the current in-flight flag has stood, as far as this side can tell.</summary>
    private double InFlightSeconds(DateTime now)
    {
        var since = _requestedAt ?? _foreignPendingSince;
        return since is { } at ? (now - at).TotalSeconds : 0;
    }

    /// <summary>
    /// Called by the game on whichever thread finished the picture. Must do nothing that can throw.
    /// </summary>
    /// <remarks>
    /// The whole reason a callback is passed at all. The game's own completion path runs through this
    /// pointer — it calls it, nulls the field and clears <c>ScreenShotRequested</c> — so handing it
    /// null leaves the flag standing forever, and with it a client that refuses every later request
    /// including the user's own screenshot key. That was diagnosed as a Wine fault in issue #29 and it
    /// is not one; it happens on Windows just the same, and it is ours.
    /// <para>
    /// Nothing here touches managed state beyond two static counters, because it may run on the
    /// screenshot worker thread. The pointer is unhooked in <see cref="Dispose"/> so it can never
    /// outlive the assembly it points into.
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly]
    private static void OnShotFinished(void* param, int result)
    {
        Volatile.Write(ref _lastCallbackResult, result);
        Interlocked.Increment(ref _completed);
    }

    /// <summary>
    /// Asks the game for a screenshot. Must be called on the framework thread.
    /// </summary>
    /// <returns>True if the game accepted the request.</returns>
    public bool Take() => Take(out _);

    /// <inheritdoc cref="Take()"/>
    /// <param name="reason">
    /// Why the game would not take one, in words fit to put on screen — null when it would.
    /// </param>
    /// <remarks>
    /// Every no used to be the same silent false, which reached the user as a button that did nothing
    /// when pressed and a run that stopped for no stated cause. The refusals are all different and the
    /// difference is the whole of the diagnosis, so each one now says which it was.
    /// </remarks>
    public bool Take(out string? reason)
    {
        var inst = ScreenShot.Instance();
        if (inst == null)
        {
            reason = "This build of the game does not expose the screenshot function.";
            _log.Warning("[Wardrobe] Automatic screenshot: the game's screenshot task was not available.");
            return false;
        }

        var now = DateTime.UtcNow;

        // Asking again while one is in flight is at best a duplicate picture and at worst piling
        // requests onto a shutter that has already jammed, which is how a session ends up hammering a
        // client that has stopped answering
        if (inst->ScreenShotRequested)
        {
            if (_requestedAt is null)
                _foreignPendingSince ??= now;

            if (!IsJammed(now))
            {
                reason = "The game is still finishing the last screenshot.";
                _log.Debug("[Wardrobe] Automatic screenshot: a shot is already in flight — not asking again.");
                return false;
            }

            // Waited it out and it is not coming. The flag is the whole of the jam — clearing it is
            // what a client restart does, and leaving it standing costs the user their own screenshot
            // key as well as the run
            Unjam("it had stood for " + $"{InFlightSeconds(now):0}s without the game finishing a picture");
        }

        if (!inst->CanTakeScreenShot)
        {
            reason = "The game is not allowing screenshots right now.";
            _log.Debug("[Wardrobe] Automatic screenshot: the game is not allowing screenshots " +
                       "(CanTakeScreenShot is false).");
            return false;
        }

        // The game's own input path, which is the one this machine honours. Everything below it is
        // the direct call, kept for the machines where that works
        if (_config.UseScreenshotKey)
        {
            if (!PressScreenshotKey(out reason)) return false;

            _requestedAt         = now;
            _foreignPendingSince = null;
            return true;
        }

        // The game's own callback when it has been seen and this is switched on, ours otherwise.
        // Borrowing it is the whole experiment: everything else about the two calls now matches.
        var callback = ReplayGameCall && _gameCallback != null ? _gameCallback : &OnShotFinished;

        bool accepted;
        _ours = true;
        try
        {
            accepted = inst->ScheduleScreenShot(callback, null);
        }
        finally
        {
            _ours = false;
        }

        if (!accepted)
        {
            reason = "The game turned the screenshot request down.";
            _log.Debug("[Wardrobe] Automatic screenshot: the game declined the request.");
            return false;
        }

        _callbackLive        = true;
        _requestedAt         = now;
        _foreignPendingSince = null;
        reason               = null;
        return true;
    }

    // ── Pressing the game's own screenshot key ────────────────────────────────

    /// <summary>Frames left to hold the screenshot key down, or zero when it is not held.</summary>
    private int _keyHeldFrames;

    /// <summary>Whether the key is being held down for a shot right now.</summary>
    public bool KeyHeld => _keyHeldFrames > 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp   = 0x0101;

    /// <summary>The game's own window, which is the only place these keystrokes are sent.</summary>
    private nint _gameWindow;

    /// <summary>
    /// Presses the game's screenshot key, at the game's own window.
    /// </summary>
    /// <remarks>
    /// Dalamud's key state deliberately refuses to press keys — it allows a key to be suppressed and
    /// nothing else — so the press has to be a window message instead. Posted to the game's window by
    /// handle rather than injected into the system's input queue, so it cannot land in another
    /// program if the player alt-tabs in the middle of a session.
    /// <para>
    /// The point of going through input at all is that the request which follows is the game's own,
    /// made from wherever the game makes it. That is the only difference left between a request that
    /// takes a picture and one that hangs, and it is not one a caller can arrange any other way.
    /// </para>
    /// </remarks>
    private bool PressScreenshotKey(out string? reason)
    {
        var key = _config.ScreenshotKeyCode;

        if (_gameWindow == 0)
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            _gameWindow = self.MainWindowHandle;
        }

        if (_gameWindow == 0)
        {
            reason = "The game's window could not be found, so its screenshot key cannot be pressed.";
            _log.Warning("[Wardrobe] Automatic screenshot: the game's main window handle was zero.");
            return false;
        }

        // Not fatal. The game's key state array is not the same thing as its window messages, and a
        // key missing from one may still arrive through the other — but it is worth saying, because
        // a key code that matches nothing is the likeliest reason for a press that does nothing
        if (!_keys.IsVirtualKeyValid(key))
            _log.Warning($"[Wardrobe] Automatic screenshot: key code {key} is not one the game " +
                         "tracks. Check it matches your screenshot keybind.");

        // Shaped like a message Windows would have posted: repeat count of one, the key's real scan
        // code, and the transition and previous-state bits set on the release
        var scan = MapVirtualKeyW((uint)key, 0);
        var down = (nint)(1u | (scan << 16));
        _keyUp   = (nint)(1u | (scan << 16) | (1u << 30) | (1u << 31));

        if (!PostMessageW(_gameWindow, WmKeyDown, key, down))
        {
            reason = "The screenshot key press could not be delivered to the game.";
            _log.Warning("[Wardrobe] Automatic screenshot: posting the key down failed.");
            return false;
        }

        _keyHeldFrames     = KeyHoldFrames;
        _framework.Update += OnHoldKey;

        _log.Debug($"[Wardrobe] Automatic screenshot: pressed the screenshot key (code {key}).");

        reason = null;
        return true;
    }

    /// <summary>The lParam the release carries, built with the press it belongs to.</summary>
    private nint _keyUp;

    /// <summary>How long the key is held, so no poll can fall between the press and the release.</summary>
    private const int KeyHoldFrames = 3;

    private void OnHoldKey(IFramework framework)
    {
        if (--_keyHeldFrames > 0) return;

        _framework.Update -= OnHoldKey;
        _keyHeldFrames = 0;

        // A key never released is a key the game believes is still held
        if (!PostMessageW(_gameWindow, WmKeyUp, _config.ScreenshotKeyCode, _keyUp))
            _log.Warning("[Wardrobe] Automatic screenshot: posting the key up failed. The game may " +
                         "think the screenshot key is still held down.");
    }

    /// <summary>Whether the flag standing right now has stood long enough to be a jam.</summary>
    private bool IsJammed(DateTime now)
    {
        if (_requestedAt is { } ours)
            return (now - ours).TotalSeconds >= JammedAfterSeconds;

        return _foreignPendingSince is { } theirs
            && (now - theirs).TotalSeconds >= ForeignJammedAfterSeconds;
    }

    /// <summary>Whether the game's screenshot task looks stuck rather than busy.</summary>
    public bool Jammed => Pending && IsJammed(DateTime.UtcNow);

    /// <summary>
    /// Clears a screenshot request the game is never going to finish.
    /// </summary>
    /// <remarks>
    /// One byte, and the same one a client restart clears. It is only ever written after the flag has
    /// stood past <see cref="JammedAfterSeconds"/> with no picture, so there is no shot to interrupt:
    /// either the worker finished long ago and the completion never ran, or it never started.
    /// <para>
    /// The callback pointer goes with it. A stale one is a pointer into this assembly that the game
    /// still holds and would call after the next unload, which is a crash rather than a lost picture.
    /// </para>
    /// </remarks>
    public void Unjam(string why)
    {
        var inst = ScreenShot.Instance();
        if (inst == null) return;

        inst->ScreenShotRequested    = false;
        inst->ScreenShotCallbackFunc = null;

        _log.Warning($"[Wardrobe] Automatic screenshot: cleared a stuck screenshot request — {why}. " +
                     "Your own screenshot key should work again as well.");

        _callbackLive        = false;
        _requestedAt         = null;
        _foreignPendingSince = null;
    }

    // ── The probe ─────────────────────────────────────────────────────────────

    /// <summary>When the probe started, or null when none is running.</summary>
    private DateTime? _probeStartedAt;

    /// <summary>Which of the sample times have already been written down.</summary>
    private int _probeSamples;

    /// <summary>How long after the request the probe keeps watching, and when it looks.</summary>
    private static readonly double[] ProbeSampleSeconds = [0, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20];

    /// <summary>Files in the game's own folder when the probe asked, to notice a new one.</summary>
    private int _probeFilesBefore;

    /// <summary>Whether a probe is watching a request right now.</summary>
    public bool Probing => _probeStartedAt != null;

    /// <summary>
    /// Asks for one screenshot with nothing else happening, and writes down what the game does.
    /// </summary>
    /// <remarks>
    /// The measurement a session cannot make. A run asks for a picture with a queue behind it, a
    /// camera preset just written, a character mid-redraw and GPose open, so a shot that does not
    /// arrive has half a dozen possible authors. This asks for one from a standing start and samples
    /// the game's own state on a timer afterwards, counting the files in the game's folder either
    /// side. What comes out says whether the request alone is what breaks the shutter — which is the
    /// difference between a bug here and a bug in something else holding the game's frames.
    /// </remarks>
    public void Probe()
    {
        if (Probing)
        {
            _log.Information("[Wardrobe] Shutter probe: one is already running.");
            return;
        }

        var before = Read();
        _probeFilesBefore = CountSaved(before.SaveFolder);

        _log.Information("[Wardrobe] Shutter probe: asking for one screenshot with nothing else running.");
        _log.Information("[Wardrobe] Shutter probe:   asking by: " +
                         (_config.UseScreenshotKey
                             ? $"pressing the game's screenshot key (code {_config.ScreenshotKeyCode})"
                             : "calling the game's screenshot function"));
        _log.Information("[Wardrobe] Shutter probe:   callback: " +
                         (ReplayGameCall && _gameCallback != null
                             ? $"the game's own (0x{(nint)_gameCallback:X})"
                             : "the wardrobe's"));
        _log.Information($"[Wardrobe] Shutter probe:   before — {Describe(before)}");
        _log.Information($"[Wardrobe] Shutter probe:   in GPose: {Plugin.Camera.InGpose}");
        _log.Information("[Wardrobe] Shutter probe:   the game's folder: " + before.SaveFolder);
        _log.Information("[Wardrobe] Shutter probe:   files in it: " +
                         (_probeFilesBefore < 0 ? "could not be counted" : _probeFilesBefore.ToString()));

        if (!Take(out var refusal))
        {
            _log.Warning($"[Wardrobe] Shutter probe: the game would not take one — {refusal}");
            return;
        }

        _probeStartedAt = DateTime.UtcNow;
        _probeSamples   = 0;
        _framework.Update += OnProbeTick;
    }

    private void OnProbeTick(IFramework framework)
    {
        if (_probeStartedAt is not { } started)
        {
            _framework.Update -= OnProbeTick;
            return;
        }

        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        if (_probeSamples >= ProbeSampleSeconds.Length || elapsed < ProbeSampleSeconds[_probeSamples])
            return;

        var at    = ProbeSampleSeconds[_probeSamples++];
        var state = Read();
        var files = CountSaved(state.SaveFolder);

        var delta = _probeFilesBefore < 0 || files < 0
            ? "?"
            : (files - _probeFilesBefore).ToString("+0;-0;no change");

        _log.Information($"[Wardrobe] Shutter probe:   +{at,4:0.00}s — {Describe(state)}, " +
                         $"files: {(files < 0 ? "?" : files.ToString())} ({delta})");

        if (_probeSamples < ProbeSampleSeconds.Length) return;

        _framework.Update -= OnProbeTick;
        _probeStartedAt = null;

        var wrote  = _probeFilesBefore >= 0 && files > _probeFilesBefore;
        var stuck  = state.Requested;

        if (wrote && !stuck)
            _log.Information("[Wardrobe] Shutter probe: the game took the picture and cleared its " +
                             "flag. The shutter is working; a session that takes none is failing " +
                             "somewhere else.");
        else if (stuck)
            _log.Error("[Wardrobe] Shutter probe: the request alone jammed the shutter — no file was " +
                       "written and the flag never cleared, with no session, no camera preset and no " +
                       "redraw involved. Whatever is wrong is in this one call or in something else " +
                       "holding the game's frames, not in the rest of the run.");
        else
            _log.Warning("[Wardrobe] Shutter probe: the flag cleared but no file appeared. The game " +
                         "thinks it finished and wrote nothing — check the folder above, and the " +
                         "screenshot format.");
    }

    /// <summary>One line of shutter state, for the probe's trace.</summary>
    private static string Describe(ShutterState s) =>
        $"allowed: {s.CanTake}, in flight: {s.Requested}, result: {s.Result}, " +
        $"format: {s.Format}, stamp: {s.Timestamp}, finished: {s.Completed}";

    /// <summary>The result code as the game holds it, named only when the name is real.</summary>
    /// <remarks>
    /// ClientStructs knows two of these — Success and NoDiskSpace — and the game plainly has more,
    /// so an unrecognised one printed as its own name would be a lie. Printed as a number it is
    /// something a bug report can carry and someone with the binary can look up.
    /// </remarks>
    public static string DescribeResult(string result) =>
        int.TryParse(result, out var code) ? $"{code} (no name for this code)" : result;

    /// <summary>How many files the game's own screenshot folder holds, or -1 if it cannot be read.</summary>
    private static int CountSaved(string folder)
    {
        if (folder.Length <= 1 || folder.StartsWith('(')) return -1;

        try
        {
            return Directory.EnumerateFiles(folder).Count();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Takes back the callback pointer before the assembly holding it can go away.
    /// </summary>
    /// <remarks>
    /// The one real cost of passing a callback at all. The game keeps the pointer until it calls it,
    /// and a plugin unloaded in between — a disable, an update — leaves it aimed at memory that is no
    /// longer there. A shot in flight is given a moment to land on its own, and whatever is left is
    /// unhooked by hand, so nothing the game still holds points in here after this returns.
    /// </remarks>
    public void Dispose()
    {
        _scheduleHook?.Disable();
        _scheduleHook?.Dispose();

        if (_keyHeldFrames > 0)
        {
            _framework.Update -= OnHoldKey;
            _keyHeldFrames = 0;

            // A key left down because the plugin went away mid-press is a key the game believes is
            // still held, which is worse than a missed picture
            PostMessageW(_gameWindow, WmKeyUp, _config.ScreenshotKeyCode, _keyUp);
        }

        if (_probeStartedAt != null)
        {
            _framework.Update -= OnProbeTick;
            _probeStartedAt = null;
        }

        if (!_callbackLive) return;

        var inst = ScreenShot.Instance();
        if (inst == null) return;

        // Nulling the pointer under a shot that is genuinely mid-flight costs that one picture's
        // completion — worth it, since the alternative is a call into an unloaded assembly
        if (inst->ScreenShotCallbackFunc != null)
        {
            inst->ScreenShotCallbackFunc = null;
            inst->ScreenShotRequested    = false;
            _log.Debug("[Wardrobe] Automatic screenshot: unhooked the completion callback on shutdown.");
        }

        _callbackLive = false;
    }
}
