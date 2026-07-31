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
}
