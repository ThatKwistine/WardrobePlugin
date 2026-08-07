using System.Numerics;
using Dalamud.Interface.Utility;

namespace WardrobePlugin.Ui;

/// <summary>
/// Converts the layout's design pixels into the size they should actually be drawn at.
/// </summary>
/// <remarks>
/// Every fixed size in this plugin — card widths, panel widths, button widths — was written against
/// Dalamud's default text size. Raise that text size and the numbers stop meaning anything: labels
/// outgrow the buttons holding them, names outgrow the cards, and the layout clips rather than
/// reflows, because none of these are containers that scroll.
/// <para>
/// The factor is Dalamud's own <b>Global Font Scale</b> — the 80/100/117/150/200/300% setting under
/// Look &amp; Feel — and nothing else. Deliberately not derived from the bound font's size: that
/// folds in whatever font Dalamud happens to have built, including any Windows DPI scaling, so the
/// layout would grow for reasons the user never asked for and could not undo from this plugin.
/// </para>
/// <para>
/// Read per use rather than cached. The setting can change while the plugin is running, and a value
/// captured once — in a constructor, say — would freeze the layout at whatever it was at load.
/// </para>
/// <para>
/// <b>Not for <c>Window.Size</c> or <c>Window.SizeConstraints</c>.</b> Dalamud scales those itself —
/// <c>IWindow</c>'s own documentation says so — and putting them through here scales them twice.
/// Worse, <c>ImGui.GetWindowSize</c> reports real pixels, so remembering a window's size and handing
/// it back as <c>Size</c> compounds the scale every frame it is applied; that is what once grew the
/// main window to tens of thousands of pixels wide. Keep window sizes in unscaled units throughout.
/// Raw ImGui calls — <c>SetNextWindowSize</c>, <c>BeginChild</c>, <c>SetNextItemWidth</c>, button
/// sizes — are <i>not</i> scaled by anyone, and do belong here.
/// </para>
/// </remarks>
public static class UiScale
{
    /// <summary>Multiplier from design pixels to drawn pixels. 1 at Dalamud's default 100%.</summary>
    public static float Factor => ImGuiHelpers.GlobalScale;

    /// <summary>Scales one design-pixel measurement.</summary>
    public static float S(float px) => px * Factor;

    /// <summary>Scales a design-pixel size. A zero stays zero, so ImGui's "auto" is preserved.</summary>
    public static Vector2 S(float x, float y)
    {
        var f = Factor;
        return new Vector2(x == 0f ? 0f : x * f, y == 0f ? 0f : y * f);
    }
}
