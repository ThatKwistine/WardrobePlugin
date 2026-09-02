using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

public unsafe class CameraService : IDisposable
{
    /// <summary>Frames to keep re-applying a preset. ~0.5s at 60fps.</summary>
    private const int DefaultSustainFrames = 30;

    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private CameraPreset? _sustaining;
    private int           _framesLeft;
    private bool          _subscribed;

    public CameraService(IFramework framework, IPluginLog log)
    {
        _framework = framework;
        _log       = log;

        // Its own hook rather than the sustain's, which only runs while a preset is being held on.
        // Measuring the range means watching the player pan by hand, which is precisely when
        // nothing of ours is driving the camera.
        _framework.Update += OnMeasureUpdate;
    }

    /// <summary>Whether the pan range is being watched. Off, the hook below costs one bool test.</summary>
    public bool MeasuringPanRange { get; set; }

    private void OnMeasureUpdate(IFramework _)
    {
        if (!MeasuringPanRange || !InGpose) return;

        var mgr = CameraManager.Instance();
        if (mgr == null || mgr->Camera == null) return;

        SamplePanRange(mgr->Camera);
    }

    /// <summary>
    /// GPose's field-of-view offset, at <c>Camera+0x13C</c>. Not named in FFXIVClientStructs.
    /// </summary>
    /// <remarks>
    /// Found by diffing every float in both camera structs across a change to the Group Pose
    /// "Camera Distance" slider. See <see cref="CameraPreset.GPoseFoVOffset"/> for how the game
    /// derives the effective field of view from it.
    /// </remarks>
    private const int GPoseFoVOffsetOffset = 0x13C;

    private static float* GPoseFoV(Camera* cam) =>
        (float*)((byte*)cam + GPoseFoVOffsetOffset);

    /// <summary>
    /// Camera pan, at <c>Camera+0x160</c> and <c>+0x164</c>. Not named in FFXIVClientStructs.
    /// </summary>
    /// <remarks>
    /// Found the same way as the field above: the only pair of floats to change across a pan that
    /// was neither a copy of another field nor part of the transform matrix. See
    /// <see cref="CameraPreset.PanH"/> for why they are stored nullable.
    /// </remarks>
    private const int PanHOffset = 0x160;
    private const int PanVOffset = 0x164;

    private static float* PanH(Camera* cam) => (float*)((byte*)cam + PanHOffset);
    private static float* PanV(Camera* cam) => (float*)((byte*)cam + PanVOffset);

    /// <summary>
    /// GPose's Twist control, at <c>Camera+0x170</c>. Read for the log only — never written.
    /// </summary>
    /// <remarks>
    /// Presets do not save twist, by decision rather than oversight. The offset comes from Brio's map
    /// of the same struct and has not been confirmed here, which is exactly why reading it is safe and
    /// writing it would not be: a wrong offset costs a misleading log line instead of a broken camera.
    /// </remarks>
    private const int TwistOffset = 0x170;

    private static float* Twist(Camera* cam) => (float*)((byte*)cam + TwistOffset);


    /// <summary>
    /// Whether GPose is active, and so whether an angle can be saved or applied at all.
    /// </summary>
    /// <remarks>
    /// Presets only work against the native GPose camera — outside it there is nothing to read a
    /// meaningful angle from, and nothing that would hold one if it were applied. Asked so the preset
    /// panel can say that before the button is pressed rather than after it silently does nothing.
    /// </remarks>
    public bool InGpose => Plugin.ClientState.IsGPosing;

    public CameraPreset? Capture()
    {
        var mgr = CameraManager.Instance();
        if (mgr == null || mgr->Camera == null) return null;
        var cam = mgr->Camera;
        return new CameraPreset
        {
            Distance    = cam->Distance,
            FoV         = cam->FoV,
            DirH        = cam->DirH,
            DirV        = cam->DirV,
            TiltOffset  = cam->TiltOffset,
            GPoseFoVOffset = *GPoseFoV(cam),
            PanH        = *PanH(cam),
            PanV        = *PanV(cam),

            // Both are stored: the offset is what gets applied, and the absolute angle stays as the
            // fallback for a character the game cannot report a facing for
            DirHOffset  = PlayerFacing() is { } facing ? Normalise(cam->DirH - facing) : null,
        };
    }

    /// <summary>The local player's facing in radians, or null when there is nobody to read.</summary>
    private static float? PlayerFacing() => Plugin.Objects.LocalPlayer?.Rotation;

    /// <summary>Folds an angle into -π to π, the range the camera's own DirH uses.</summary>
    private static float Normalise(float radians)
    {
        const float twoPi = MathF.PI * 2f;

        radians %= twoPi;
        if (radians >  MathF.PI) radians -= twoPi;
        if (radians < -MathF.PI) radians += twoPi;
        return radians;
    }

    /// <summary>
    /// Applies a preset, then keeps re-applying it for a short burst of frames.
    /// </summary>
    /// <remarks>
    /// A single write does not stick: the game's own camera update runs every frame and
    /// overwrites DirH/DirV immediately afterwards, so the preset appears to do nothing.
    /// Re-applying for a brief window lets the write win, then hands control back to the player.
    /// </remarks>
    public void Apply(CameraPreset preset, int frames = DefaultSustainFrames)
    {
        if (!WriteToCamera(preset))
        {
            _log.Warning(InGpose
                ? "[Wardrobe] Camera apply failed — CameraManager or its camera was null."
                : "[Wardrobe] Camera apply skipped — not in GPose. A preset written to the gameplay " +
                  "camera would tilt it and stay that way.");
            return;
        }

        if (frames <= 1) return;

        _sustaining = preset;
        _framesLeft = frames;
        if (!_subscribed)
        {
            _framework.Update += OnFrameworkUpdate;
            _subscribed = true;
        }
    }

    /// <summary>Ends any in-progress sustained apply, returning camera control to the player.</summary>
    public void CancelSustain()
    {
        _sustaining = null;
        _framesLeft = 0;
        Unsubscribe();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (_sustaining == null || _framesLeft <= 0)
        {
            CancelSustain();
            return;
        }

        WriteToCamera(_sustaining);
        _framesLeft--;
    }

    /// <summary>
    /// The range each pan axis actually travels, measured off the game's own controls.
    /// </summary>
    /// <remarks>
    /// Pan is the one field here the camera publishes no limits for — there is no MinPan/MaxPan
    /// beside it the way MinDistance and DirVMin sit beside their fields — so these are measured
    /// rather than read, by holding each GPose pan key to its stop and recording where the game
    /// parks the field. See <see cref="ObservedPanRange"/>, which is the instrument.
    /// <para>
    /// <b>The two axes are nothing like each other.</b> Pan runs to about ±0.87 and tilt only to
    /// about ±0.35, so a single shared bound is wrong twice over: tight enough for tilt, it throws
    /// away more than half of pan's travel; loose enough for pan, it lets through the tilt values
    /// that jam the controls outright. One bound per side of each axis, and nothing shared.
    /// </para>
    /// <para>
    /// <b>The range is fixed.</b> Measured twice on 2026-09-02, once at distance 1.50 / FoV 0.690
    /// and once at distance 9.51 / FoV 0.780 — a six-fold change of distance and the whole of the
    /// FoV range — and the stops moved by under 0.003, which is the difference between holding a key
    /// firmly and holding it very firmly. So constants are the right shape, not functions of the
    /// live camera.
    /// </para>
    /// <para>
    /// Each bound is the least extreme value seen on that side across both runs, so every one sits
    /// inside every observation. Erring inwards costs a few thousandths of travel that nobody can
    /// see; erring outwards writes past the stop, which is what jams the controls.
    /// </para>
    /// <para>
    /// Tilt is genuinely asymmetric — about -0.356 down against +0.337 up — and consistently so
    /// across both runs, which is why it gets a bound per side rather than a magnitude. The tilt
    /// figure also explains the jam that started all this exactly: a stored tilt of -0.380463 is
    /// past the -0.3557 stop and killed all four pan keys, while -0.344889 is inside it and was fine.
    /// </para>
    /// </remarks>
    private const float MinPanH = -0.870f;
    private const float MaxPanH =  0.869f;
    private const float MinPanV = -0.355f;
    private const float MaxPanV =  0.337f;

    /// <summary>
    /// Holds a pan value inside the range its own axis is known to travel.
    /// </summary>
    /// <remarks>
    /// The write used to carry a comment saying pan needed no clamp because the camera exposes no
    /// limits for it. That was the bug: no published limit is not the same as no limit, and this one
    /// is enforced by the camera going deaf rather than by the value being rejected.
    /// </remarks>
    private float ClampPan(float value, float min, float max, string axis)
    {
        if (value >= min && value <= max) return value;

        var clamped = Math.Clamp(value, min, max);

        _log.Warning($"[Wardrobe] Camera: {axis} of {value:F6} is outside the measured range " +
                     $"{min:F3} to {max:F3} and would jam GPose panning — wrote {clamped:F6} " +
                     "instead. If this was captured from a working camera, the range is not fixed " +
                     "and the clamp needs to follow distance or field of view.");

        return clamped;
    }

    // ── Measuring the pan range ───────────────────────────────────────────────

    /// <summary>What the pan fields have been seen holding, and under what conditions.</summary>
    /// <param name="Samples">Frames observed. Zero means nothing has been watched yet.</param>
    public readonly record struct PanRange(
        float MinH, float MaxH, float MinV, float MaxV,
        float AtDistance, float AtFoV, float AtDirV, int Samples);

    private float _minH = float.MaxValue, _maxH = float.MinValue;
    private float _minV = float.MaxValue, _maxV = float.MinValue;
    private float _rangeDistance, _rangeFoV, _rangeDirV;
    private int   _rangeSamples;

    /// <summary>
    /// The widest pan values seen so far, for measuring where the game's own limit actually is.
    /// </summary>
    /// <remarks>
    /// The clamp on writing is built from two data points, both negative and both on the tilt axis,
    /// which is not a measurement of anything — it assumes the range is symmetric, that both axes
    /// share a bound, and that the bound is fixed. None of those has been established.
    /// <para>
    /// This settles it by watching rather than writing: hold each of the four GPose pan keys to its
    /// stop and the game parks the field at its own limit, which is the number wanted. Read-only, so
    /// running the measurement cannot cause the jam it is measuring.
    /// </para>
    /// <para>
    /// The distance, FoV and pitch at the time of the widest sample come with it, because the open
    /// question is whether the limit moves with them. Measure at one distance, reset, measure at
    /// another: if the bounds differ, a fixed clamp is the wrong shape.
    /// </para>
    /// </remarks>
    public PanRange ObservedPanRange => _rangeSamples == 0
        ? default
        : new PanRange(_minH, _maxH, _minV, _maxV,
                       _rangeDistance, _rangeFoV, _rangeDirV, _rangeSamples);

    /// <summary>
    /// What the pan fields hold right now, regardless of what has been seen before.
    /// </summary>
    /// <remarks>
    /// The observed range is a running maximum, so on its own it can only ever notice a range
    /// getting <i>wider</i> — hold a key to the stop under conditions where the limit is smaller and
    /// the old, larger figure simply stays on screen. This is the reading that answers that: park
    /// the key against the stop and look at the live number, which is where the game is holding the
    /// field this frame.
    /// </remarks>
    public (float H, float V)? CurrentPan
    {
        get
        {
            var mgr = CameraManager.Instance();
            if (mgr == null || mgr->Camera == null) return null;

            return (*PanH(mgr->Camera), *PanV(mgr->Camera));
        }
    }

    /// <summary>Starts the measurement over, for sampling under different conditions.</summary>
    public void ResetPanRange()
    {
        _minH = float.MaxValue; _maxH = float.MinValue;
        _minV = float.MaxValue; _maxV = float.MinValue;
        _rangeSamples = 0;
    }

    /// <summary>
    /// Records the frame's pan values, if they widen what has been seen.
    /// </summary>
    /// <remarks>
    /// GPose only. Outside it these fields are not the ones the pan keys drive, so folding those
    /// frames in would widen the range with numbers that answer a different question.
    /// </remarks>
    private void SamplePanRange(Camera* cam)
    {
        var h = *PanH(cam);
        var v = *PanV(cam);

        // Only when something actually moved outwards, so the conditions recorded alongside are the
        // conditions the widest value was reached under rather than whatever the last frame held
        var widened = h < _minH || h > _maxH || v < _minV || v > _maxV;

        _minH = Math.Min(_minH, h);
        _maxH = Math.Max(_maxH, h);
        _minV = Math.Min(_minV, v);
        _maxV = Math.Max(_maxV, v);

        _rangeSamples++;

        if (!widened && _rangeSamples > 1) return;

        _rangeDistance = cam->Distance;
        _rangeFoV      = cam->FoV;
        _rangeDirV     = cam->DirV;
    }

    private bool WriteToCamera(CameraPreset preset)
    {
        // Outside GPose this is the gameplay camera, not a separate one, and a preset written into it
        // lands on the camera the player is using to walk around. Most of it is harmless because the
        // game's own update overwrites DirH/DirV the next frame — but TiltOffset is the Character
        // Configuration third-person camera angle, which persists, so an apply outside GPose left the
        // camera stuck at a GPose pitch until that slider was reset by hand. The guard belongs here
        // rather than at the call sites: Apply, its sustain loop and anything added later all pass
        // through this one place.
        if (!InGpose) return false;

        var mgr = CameraManager.Instance();
        if (mgr == null || mgr->Camera == null) return false;
        var cam = mgr->Camera;

        var dist = Math.Clamp(preset.Distance, cam->MinDistance, cam->MaxDistance);
        cam->Distance       = dist;
        cam->InterpDistance = dist; // snap rather than interpolate
        cam->FoV            = Math.Clamp(preset.FoV, cam->MinFoV, cam->MaxFoV);

        // Relative to the character where the preset has an offset, absolute where it does not
        cam->DirH = preset.DirHOffset is { } offset && PlayerFacing() is { } facing
            ? Normalise(facing + offset)
            : preset.DirH;

        cam->DirV           = Math.Clamp(preset.DirV, cam->DirVMin, cam->DirVMax);
        cam->TiltOffset     = preset.TiltOffset;
        *GPoseFoV(cam)      = preset.GPoseFoVOffset;

        // Anything the player had queued when the preset was applied would otherwise be spent the
        // moment the sustain stops writing, nudging the camera off the angle a second later — which
        // is exactly the drift reported after engaging a preset. Cleared every frame of the sustain,
        // so input during it is discarded rather than saved up.
        cam->InputDeltaH         = 0f;
        cam->InputDeltaV         = 0f;
        cam->InputDeltaHAdjusted = 0f;
        cam->InputDeltaVAdjusted = 0f;

        // Only when the preset actually recorded a pan. A preset saved before pan existed leaves the
        // camera's own pan untouched, rather than writing a zero that may not be the centred value.
        if (preset.PanH is { } panH) *PanH(cam) = ClampPan(panH, MinPanH, MaxPanH, "Pan");
        if (preset.PanV is { } panV) *PanV(cam) = ClampPan(panV, MinPanV, MaxPanV, "Tilt");


        return true;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Writes every camera field the presets know about to the log, named and with its offset.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="DumpOrDiff"/>: that one answers "which field moved", this one
    /// answers "what were they all set to". A report that an angle came back wrong needs both halves
    /// of the comparison — what the camera held when the preset was saved, and what it holds when the
    /// preset is applied — and neither is visible from a screenshot.
    /// <para>
    /// Two fields are logged that no preset stores. <c>0x170</c> is the GPose Twist control, Q and E,
    /// which the wardrobe deliberately does not save; it is here because a camera that comes back
    /// twisted is otherwise indistinguishable from one that comes back mis-panned. <c>TiltOffset</c>
    /// is the Character Configuration third-person camera angle, which is a saved game setting rather
    /// than camera state, and is worth being able to see for the same reason.
    /// </para>
    /// </remarks>
    public void LogState(string when)
    {
        var mgr = CameraManager.Instance();
        if (mgr == null || mgr->Camera == null)
        {
            _log.Information($"[Wardrobe] Camera state ({when}): no camera to read.");
            return;
        }

        var cam = mgr->Camera;

        _log.Information($"[Wardrobe] Camera state ({when}) — in GPose: {InGpose}");
        _log.Information($"[Wardrobe]   Distance   0x124 = {cam->Distance,12:F5}   (scroll-wheel zoom)");
        _log.Information($"[Wardrobe]   FoV        0x130 = {cam->FoV,12:F5}");
        _log.Information($"[Wardrobe]   GPose zoom 0x13C = {*GPoseFoV(cam),12:F5}   (the Camera Distance slider)");
        _log.Information($"[Wardrobe]   DirH       0x140 = {cam->DirH,12:F5}   (rotation)");
        _log.Information($"[Wardrobe]   DirV       0x144 = {cam->DirV,12:F5}   (vertical angle)");
        _log.Information($"[Wardrobe]   Pan        0x160 = {*PanH(cam),12:F5}   (Pan Camera, A/D)");
        _log.Information($"[Wardrobe]   Tilt       0x164 = {*PanV(cam),12:F5}   (Tilt Camera, W/S)");
        _log.Information($"[Wardrobe]   Twist      0x170 = {*Twist(cam),12:F5}   (Twist Camera, Q/E — NOT saved by presets)");
        _log.Information($"[Wardrobe]   TiltOffset 0x1E4 = {cam->TiltOffset,12:F5}   (Character Configuration camera angle, a saved setting)");
    }

    /// <summary>Writes what a preset actually stored, next to the camera it was taken from.</summary>
    /// <remarks>
    /// Null is printed as "not stored" rather than as a number, because that is a real and different
    /// state: pan and tilt are nullable, and a preset saved before they existed leaves the camera's
    /// own alone instead of writing one. A report of "it does not restore my tilt" is answered
    /// immediately by seeing "not stored" on that line.
    /// </remarks>
    public void LogPreset(CameraPreset preset, string when)
    {
        static string N(float? v) => v is { } f ? f.ToString("F5") : "not stored";

        _log.Information($"[Wardrobe] Preset '{preset.Name}' ({when}):");
        _log.Information($"[Wardrobe]   Distance   = {preset.Distance,12:F5}");
        _log.Information($"[Wardrobe]   FoV        = {preset.FoV,12:F5}");
        _log.Information($"[Wardrobe]   GPose zoom = {preset.GPoseFoVOffset,12:F5}");
        _log.Information($"[Wardrobe]   DirH       = {preset.DirH,12:F5}   offset from character: {N(preset.DirHOffset)}");
        _log.Information($"[Wardrobe]   DirV       = {preset.DirV,12:F5}");
        _log.Information($"[Wardrobe]   Pan        = {N(preset.PanH),12}");
        _log.Information($"[Wardrobe]   Tilt       = {N(preset.PanV),12}");
        _log.Information($"[Wardrobe]   TiltOffset = {preset.TiltOffset,12:F5}");
    }

    // ── Field finder ──────────────────────────────────────────────────────────

    private byte[]? _dumpBaseline;

    private const int CamBytes    = 704; // Camera is 0x2C0
    private const int RenderBytes = 640;

    /// <summary>
    /// First call snapshots both camera structs; the second reports every byte that changed.
    /// </summary>
    /// <remarks>
    /// Scans as floats *and* as raw bytes, because not every setting is a float — a checkbox is
    /// typically a single byte and would be invisible to a float-only scan. Byte hits that fall
    /// inside a float that already changed are suppressed, so a moved slider does not also report
    /// its own four bytes.
    /// <para>
    /// Kept in the shipping build behind an undocumented command, so a report about a camera control
    /// can be answered with what actually moved instead of a guess. Field names lie: on 2026-08-05
    /// four rounds were lost to names that sounded right, and one diff found the field. Use it in
    /// GPose with Brio's camera off, change exactly one control between the two calls, and write the
    /// input field rather than any output that moved with it.
    /// </para>
    /// </remarks>
    public void DumpOrDiff()
    {
        var mgr = CameraManager.Instance();
        if (mgr == null || mgr->Camera == null)
        {
            _log.Warning("[Wardrobe] Camera dump: no camera.");
            return;
        }

        var camB    = (byte*)mgr->Camera;
        var renderB = (byte*)mgr->Camera->SceneCamera.RenderCamera;

        var snap = new byte[CamBytes + RenderBytes];
        for (var i = 0; i < CamBytes; i++) snap[i] = camB[i];
        if (renderB != null)
            for (var i = 0; i < RenderBytes; i++) snap[CamBytes + i] = renderB[i];

        if (_dumpBaseline == null)
        {
            _dumpBaseline = snap;
            _log.Information("[Wardrobe] Camera dump: baseline taken. Change one setting in game, " +
                             "then run /wardrobe camdump again.");
            return;
        }

        var floatHits = new System.Collections.Generic.HashSet<int>();
        var changes   = 0;

        // Floats first, so their byte-level noise can be suppressed below
        for (var region = 0; region < 2; region++)
        {
            var start = region == 0 ? 0 : CamBytes;
            var len   = region == 0 ? CamBytes : RenderBytes;
            var label = region == 0 ? "cam   " : "render";

            for (var off = 0; off + 4 <= len; off += 4)
            {
                var a = BitConverter.ToSingle(_dumpBaseline, start + off);
                var b = BitConverter.ToSingle(snap,          start + off);
                if (a.Equals(b)) continue;
                if (!float.IsFinite(a) || !float.IsFinite(b)) continue;
                if (Math.Abs(a) > 100000f || Math.Abs(b) > 100000f) continue;

                for (var k = 0; k < 4; k++) floatHits.Add(start + off + k);
                _log.Information($"[Wardrobe]  f {label} +0x{off:X3} ({off,3}): " +
                                 $"{a,12:F5} -> {b,12:F5}");
                changes++;
            }
        }

        // Then anything else that moved, one byte at a time
        for (var i = 0; i < snap.Length; i++)
        {
            if (_dumpBaseline[i] == snap[i] || floatHits.Contains(i)) continue;
            var inCam = i < CamBytes;
            var off   = inCam ? i : i - CamBytes;
            _log.Information($"[Wardrobe]  b {(inCam ? "cam   " : "render")} +0x{off:X3} ({off,3}): " +
                             $"{_dumpBaseline[i],3} -> {snap[i],3}");
            changes++;
        }

        _log.Information($"[Wardrobe] Camera dump: {changes} change(s). Baseline cleared.");
        _dumpBaseline = null;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _framework.Update -= OnFrameworkUpdate;
        _framework.Update -= OnMeasureUpdate;
        _subscribed = false;
    }

    public void Dispose() => Unsubscribe();
}
