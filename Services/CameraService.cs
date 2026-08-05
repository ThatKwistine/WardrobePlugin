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
        if (!WriteToCamera(preset))
        {
            _log.Warning("[Wardrobe] Camera apply failed — CameraManager or its camera was null.");
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
        *GPoseFoV(cam)      = preset.GPoseFoVOffset;
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
