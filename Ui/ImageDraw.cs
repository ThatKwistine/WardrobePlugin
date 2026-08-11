using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace WardrobePlugin.Ui;

/// <summary>
/// Image drawing helpers that keep previews square without distorting them.
/// </summary>
/// <remarks>
/// Every preview in the wardrobe is a square. ImGui.Image stretches whatever it is given into the
/// rectangle it is handed, so a portrait or landscape source would come out squashed. These helpers
/// centre-crop by adjusting the texture coordinates instead, which changes only what is sampled —
/// the file on disk is never touched, and reverting to a different display size costs nothing.
/// </remarks>
public static class ImageDraw
{
    /// <summary>
    /// Texture coordinates selecting the largest centred square of a source of the given size.
    /// </summary>
    public static (Vector2 Uv0, Vector2 Uv1) SquareCropUvs(float width, float height)
    {
        if (width <= 0 || height <= 0 || width == height)
            return (Vector2.Zero, Vector2.One);

        if (width > height)
        {
            var fraction = height / width;
            var offset   = (1f - fraction) / 2f;
            return (new Vector2(offset, 0f), new Vector2(offset + fraction, 1f));
        }
        else
        {
            var fraction = width / height;
            var offset   = (1f - fraction) / 2f;
            return (new Vector2(0f, offset), new Vector2(1f, offset + fraction));
        }
    }

    /// <summary>Draws a texture as a square, centre-cropped rather than stretched.</summary>
    public static void Square(IDalamudTextureWrap wrap, float size)
    {
        var (uv0, uv1) = SquareCropUvs(wrap.Width, wrap.Height);
        ImGui.Image(wrap.Handle, new Vector2(size, size), uv0, uv1);
    }

    /// <summary>Draws a texture as a square image button, centre-cropped rather than stretched.</summary>
    public static bool SquareButton(string id, IDalamudTextureWrap wrap, float size)
    {
        var (uv0, uv1) = SquareCropUvs(wrap.Width, wrap.Height);
        ImGui.PushID(id);
        var clicked = ImGui.ImageButton(wrap.Handle, new Vector2(size, size), uv0, uv1);
        ImGui.PopID();
        return clicked;
    }

    /// <summary>The 9:16 portrait shape, as height ÷ width.</summary>
    /// <remarks>
    /// Matches the game's own portrait mode in GPose, which is what these previews are shot with.
    /// </remarks>
    public const float PortraitRatio = 16f / 9f;

    /// <summary>Height of a portrait preview drawn at the given width.</summary>
    public static float PortraitHeight(float width) => width * PortraitRatio;

    /// <summary>
    /// Texture coordinates selecting the largest centred 9:16 rectangle of a source.
    /// </summary>
    /// <remarks>
    /// The same centre-crop as <see cref="SquareCropUvs"/> against a different target shape, which
    /// is what lets a wardrobe hold both kinds of picture at once: a square shot in a portrait card
    /// keeps its middle column, and a portrait shot in a square card keeps its middle band. Neither
    /// is stretched, and neither file is touched.
    /// </remarks>
    public static (Vector2 Uv0, Vector2 Uv1) PortraitCropUvs(float width, float height)
    {
        if (width <= 0 || height <= 0) return (Vector2.Zero, Vector2.One);

        var sourceRatio = height / width;

        if (sourceRatio > PortraitRatio)
        {
            // Taller than 9:16 — keep a middle band of its height
            var fraction = PortraitRatio / sourceRatio;
            var offset   = (1f - fraction) / 2f;
            return (new Vector2(0f, offset), new Vector2(1f, offset + fraction));
        }

        // Wider than 9:16, which includes every square image — keep a middle column of its width
        var widthFraction = sourceRatio / PortraitRatio;
        var widthOffset   = (1f - widthFraction) / 2f;
        return (new Vector2(widthOffset, 0f), new Vector2(widthOffset + widthFraction, 1f));
    }

    /// <summary>Draws a texture as a 9:16 portrait, centre-cropped rather than stretched.</summary>
    public static void Portrait(IDalamudTextureWrap wrap, float width)
    {
        var (uv0, uv1) = PortraitCropUvs(wrap.Width, wrap.Height);
        ImGui.Image(wrap.Handle, new Vector2(width, PortraitHeight(width)), uv0, uv1);
    }
}
