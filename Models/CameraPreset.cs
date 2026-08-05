using System;

namespace WardrobePlugin.Models;

[Serializable]
public class CameraPreset
{
    public float Distance      { get; set; }
    public float FoV           { get; set; }
    public float DirH          { get; set; }
    public float DirV          { get; set; }
    public float TiltOffset    { get; set; }

    /// <summary>
    /// The GPose "Camera Distance" setting, which is a field-of-view offset rather than a distance.
    /// </summary>
    /// <remarks>
    /// Undocumented in FFXIVClientStructs, at <c>Camera+0x13C</c>. The game computes an effective
    /// field of view as <c>FoV + this</c> into <c>Camera+0x1A4</c>, and the render camera reads that
    /// result — so this offset is the input and the other two are outputs. Scroll-wheel zoom is
    /// <see cref="Distance"/>; this is the separate slider in the Group Pose settings menu.
    /// Zero is the neutral value, so presets saved before this existed need no special handling.
    /// </remarks>
    public float GPoseFoVOffset { get; set; }
}
