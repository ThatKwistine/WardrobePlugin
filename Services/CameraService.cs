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
    }

    /// <summary>Current camera values, for diagnostics.</summary>
    private string ReadBack()
    {
        var mgr = CameraManager.Instance();
        if (mgr == null || mgr->Camera == null) return "camera unavailable";
        var cam = mgr->Camera;
        return $"dist={cam->Distance:F2} fov={cam->FoV:F3} h={cam->DirH:F3} v={cam->DirV:F3} tilt={cam->TiltOffset:F3}";
    }

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
        };
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
        // Three data points from one apply, enough to tell the failure modes apart:
        //   write does not land            → we are writing somewhere that is not the live camera
        //   lands then reverts next frame  → something is overwriting it (game loop, or Brio)
        //   sticks but the view is unchanged → the camera being written is not the one rendering
        _log.Debug($"[Wardrobe] Camera before : {ReadBack()}");
        _log.Debug($"[Wardrobe] Camera wanted : dist={preset.Distance:F2} fov={preset.FoV:F3} " +
                   $"h={preset.DirH:F3} v={preset.DirV:F3} tilt={preset.TiltOffset:F3}");

        if (!WriteToCamera(preset))
        {
            _log.Warning("[Wardrobe] Camera apply failed — CameraManager or its camera was null.");
            return;
        }

        _log.Debug($"[Wardrobe] Camera after write: {ReadBack()}");

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
            if (_sustaining != null)
                _log.Debug($"[Wardrobe] Camera after sustain: {ReadBack()}");
            CancelSustain();
            return;
        }

        // Read before writing on the first sustained frame: this is the value the game left after
        // its own update ran, which is what reveals an overwrite.
        if (_framesLeft == DefaultSustainFrames)
            _log.Debug($"[Wardrobe] Camera next frame (before re-write): {ReadBack()}");

        WriteToCamera(_sustaining);
        _framesLeft--;
    }

    private bool WriteToCamera(CameraPreset preset)
    {
        var mgr = CameraManager.Instance();
        if (mgr == null || mgr->Camera == null) return false;
        var cam = mgr->Camera;

        var dist = Math.Clamp(preset.Distance, cam->MinDistance, cam->MaxDistance);
        cam->Distance       = dist;
        cam->InterpDistance = dist; // snap rather than interpolate
        cam->FoV            = Math.Clamp(preset.FoV, cam->MinFoV, cam->MaxFoV);
        cam->DirH           = preset.DirH;
        cam->DirV           = Math.Clamp(preset.DirV, cam->DirVMin, cam->DirVMax);
        cam->TiltOffset     = preset.TiltOffset;
        return true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _framework.Update -= OnFrameworkUpdate;
        _subscribed = false;
    }

    public void Dispose() => Unsubscribe();
}
