using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace WardrobePlugin.Ui;

/// <summary>
/// Settings, in a window of its own rather than in the wardrobe's right-hand column.
/// </summary>
/// <remarks>
/// The column is 360px wide and shared with the import panel, the tag tree, the image browser and
/// the outfit editor, so settings both crowded it and were crowded by it — a folder path or a
/// preset row had nowhere to go, and opening settings closed whatever you were working in.
/// <para>
/// A window can be resized, moved beside the grid, and left open while you change something and
/// watch the effect. It is also what Dalamud's own settings button on the plugin installer expects
/// to open, which it now does.
/// </para>
/// <para>
/// The body is drawn by <see cref="PluginUi"/> rather than moved here: every section reads the
/// same plugin state the grid does, and splitting that across two classes would buy nothing but a
/// long list of forwarded fields.
/// </para>
/// </remarks>
public class SettingsWindow : Window, IDisposable
{
    private readonly PluginUi _ui;

    // Unscaled, as Dalamud multiplies both by the Global Font Scale on the way out. Wide enough
    // for a folder path and the buttons under it without wrapping at the default scale.
    private static readonly Vector2 DefaultSize = new(520, 660);

    public SettingsWindow(PluginUi ui)
        : base("Wardrobe Settings###WardrobeSettings")
    {
        _ui = ui;

        Size            = DefaultSize;
        SizeCondition   = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <remarks>
    /// Around <c>Begin</c> rather than inside <see cref="Draw"/>, as every other window here does
    /// it: without the push the window draws its text at a fraction of the intended size while
    /// Dalamud's own windows draw correctly. See <see cref="FontScope"/>.
    /// </remarks>
    public override void PreDraw() => FontScope.Push(ref _fontScope);

    public override void PostDraw() => FontScope.Pop(ref _fontScope);

    /// <summary>Held between <see cref="PreDraw"/> and <see cref="PostDraw"/>.</summary>
    private IDisposable? _fontScope;

    public override void Draw()
    {
        UiLayout.PushWrap();
        _ui.DrawSettingsBody();
        UiLayout.PopWrap();
    }

    public void Dispose() => FontScope.Pop(ref _fontScope);
}
