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
}
