using System;

namespace WardrobePlugin.Models;

[Serializable]
public class CameraPreset
{
    /// <summary>What this angle is called — "Full body", "Boots close-up".</summary>
    /// <remarks>
    /// Empty on presets saved when a slot could only hold one, which
    /// <see cref="Configuration.MigrateCameraPresets"/> fills in. Not required to be unique: two
    /// angles a user considers worth the same name are their business, and the list is addressed by
    /// position everywhere rather than by name.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>The preset a screenshot session loads for this slot.</summary>
    /// <remarks>
    /// Marked on the preset rather than tracked as a position or a name elsewhere: it then survives
    /// renaming and reordering on its own, and travels with the preset through export and import.
    /// <para>
    /// Meant to be true on exactly one preset per slot, but nothing depends on that holding.
    /// <see cref="Configuration.DefaultPresetFor"/> takes the first marked preset and falls back to
    /// the first in the list, so a slot with none marked and a slot with several both still load
    /// something sensible rather than nothing.
    /// </para>
    /// </remarks>
    public bool IsDefault { get; set; }

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

    /// <summary>
    /// Camera pan — where the view is centred, as opposed to which way it is pointing.
    /// </summary>
    /// <remarks>
    /// Undocumented in FFXIVClientStructs, at <c>Camera+0x160</c> and <c>+0x164</c>. Found by
    /// diffing every float across a pan in both axes: they were the only pair that changed without
    /// being a copy of something else or part of the transform matrix, and they sit in the scalar
    /// control region just past <c>DirVMax</c>.
    /// <para>
    /// Which of the two is horizontal is not established — the axes were panned together, and it
    /// makes no difference to saving and restoring them. The names follow the <c>DirH</c>/<c>DirV</c>
    /// convention rather than claiming to have checked.
    /// </para>
    /// <para>
    /// Nullable because the neutral value is <b>not known</b>. The baseline read was already slightly
    /// panned, so nothing in the capture shows what a centred camera holds — and if it is not zero,
    /// a preset saved before this existed would deserialise to zero and yank the camera somewhere
    /// arbitrary. Null means "this preset predates pan", and the pan is left alone when it applies,
    /// which is exactly how those presets behave today.
    /// </para>
    /// </remarks>
    public float? PanH { get; set; }

    /// <inheritdoc cref="PanH"/>
    public float? PanV { get; set; }
}
