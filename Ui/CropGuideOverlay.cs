using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// Draws, over the game, the part of the screen a screenshot will actually keep.
/// </summary>
/// <remarks>
/// Issue #25. A captured picture is centre-cropped to a square, so on a widescreen window the sides
/// are thrown away — and framing a character against the window centres them in an image that no
/// longer exists. The mistake is only visible afterwards, by which time the pose and the camera are
/// gone.
/// <para>
/// <b>Square only.</b> A 9:16 outfit capture gets no guide, because the game already has one:
/// GPose's own portrait mode frames that shot and shows where the camera has to be for it. A second
/// frame drawn over the top would be the worse of the two guides, so this one stands down whenever
/// the shot is going to be a portrait — see <see cref="ShouldDraw"/>.
/// </para>
/// <para>
/// The geometry is not an approximation of the crop, it is the same arithmetic:
/// <c>ScreenshotSessionService</c> takes the largest centred rectangle of the wanted shape that the
/// screenshot contains, and the screenshot is the game window, so the same rectangle scaled to the
/// viewport is exactly what survives. <see cref="CropRect"/> and the service's own crop are kept in
/// step by both being written from the same rule; if one changes the other has to.
/// </para>
/// <para>
/// Drawn on the background draw list, so the wardrobe's own windows stay on top of it rather than
/// being obscured by a full-screen overlay. It does not appear in the resulting picture: the shot
/// comes from the game's own screenshot function, which does not capture Dalamud's interface — the
/// same property the automatic session already relies on to keep plugin windows out of shots.
/// </para>
/// </remarks>
public sealed class CropGuideOverlay
{
    private readonly Configuration            _config;
    private readonly ScreenshotSessionService _session;

    public CropGuideOverlay(Configuration config, ScreenshotSessionService session)
    {
        _config  = config;
        _session = session;
    }

    public void Draw()
    {
        if (!ShouldDraw()) return;

        var viewport = ImGui.GetMainViewport();
        if (viewport.Size.X < 1 || viewport.Size.Y < 1) return;

        var (min, max) = CropRect(viewport.Pos, viewport.Size);

        var draw = ImGui.GetBackgroundDrawList(viewport);

        // Dim what will be discarded rather than only outlining what will be kept. An outline alone
        // reads as a frame around the subject; the dimming is what makes "this half of the screen is
        // not in the picture" impossible to misread at a glance, which is the whole complaint.
        var pos  = viewport.Pos;
        var end  = viewport.Pos + viewport.Size;
        var veil = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.38f));

        draw.AddRectFilled(pos,                    new Vector2(min.X, end.Y), veil); // left
        draw.AddRectFilled(new Vector2(max.X, pos.Y), end,                    veil); // right
        draw.AddRectFilled(new Vector2(min.X, pos.Y), new Vector2(max.X, min.Y), veil); // above
        draw.AddRectFilled(new Vector2(min.X, max.Y), new Vector2(max.X, end.Y), veil); // below

        // The same gold the grid uses for a worn card, so the colour already means "this one" here
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.25f, 0.9f)), 0f,
            ImDrawFlags.None, 2f);

        DrawThirds(draw, min, max);
        DrawLabel(draw, min);
    }

    /// <summary>
    /// The largest centred square that fits the window.
    /// </summary>
    /// <remarks>
    /// Written from the crop's rule rather than copied from its code, which works on a file that
    /// does not exist yet: fit the shape inside, then centre it. Both directions are clamped rather
    /// than assuming the window is wider than it is tall, since a window can be neither.
    /// </remarks>
    private static (Vector2 Min, Vector2 Max) CropRect(Vector2 pos, Vector2 size)
    {
        var side = MathF.Min(size.X, size.Y);
        var min  = pos + new Vector2((size.X - side) / 2f, (size.Y - side) / 2f);

        return (min, min + new Vector2(side, side));
    }

    /// <summary>Thirds inside the kept area, faint enough to frame against without reading as clutter.</summary>
    private static void DrawThirds(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        var colour = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f));
        var size   = max - min;

        for (var i = 1; i <= 2; i++)
        {
            var x = min.X + size.X * i / 3f;
            var y = min.Y + size.Y * i / 3f;
            draw.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), colour);
            draw.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), colour);
        }
    }

    /// <summary>
    /// A line saying what the box is, placed inside the kept area.
    /// </summary>
    /// <remarks>
    /// Inside rather than above, because above is the dimmed part and a label there would be the one
    /// piece of the guide sitting in the region it is telling you to ignore.
    /// </remarks>
    private static void DrawLabel(ImDrawListPtr draw, Vector2 min)
    {
        const string text = "Preview crop";
        var pad = UiScale.S(6f);

        var textSize = ImGui.CalcTextSize(text);
        var at       = new Vector2(min.X + pad, min.Y + pad);

        // A plate behind it, or the text is unreadable over a pale floor
        draw.AddRectFilled(at - new Vector2(pad * 0.5f, pad * 0.25f),
            at + textSize + new Vector2(pad * 0.5f, pad * 0.25f),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.5f)), UiScale.S(3f));

        draw.AddText(at, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.55f, 0.95f)), text);
    }

    /// <summary>
    /// Whether there is a square crop coming that somebody could frame wrongly.
    /// </summary>
    /// <remarks>
    /// Stands down for a 9:16 outfit capture: GPose's portrait mode is already on screen showing
    /// where the camera has to be for that shot, and this would only be a second frame over the top
    /// of a better one.
    /// <para>
    /// The condition is the same one the session applies when it crops — an outfit, and the portrait
    /// setting on — so the guide can never be drawn for a shape other than the one being captured.
    /// Outside a session there is no target to ask, and an item is square whatever that setting
    /// says, so the square guide is the right answer there.
    /// </para>
    /// </remarks>
    private bool ShouldDraw()
    {
        if (_session.CurrentOutfit != null && _config.PortraitOutfitPreviews) return false;

        return _config.CropGuide switch
        {
            Configuration.CropGuideMode.Always   => true,
            Configuration.CropGuideMode.Sessions => _session.State != SessionState.Idle,
            _                                    => false,
        };
    }
}
