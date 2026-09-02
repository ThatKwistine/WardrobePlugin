using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// The offer to put back on what you were last wearing, in a window of its own.
/// </summary>
/// <remarks>
/// Its own window for the reason <see cref="ChangelogWindow"/> has one: it appears without being
/// asked for, at a moment when nothing of the plugin's is open. Above the wardrobe's grid the offer
/// is only ever read by somebody who went and opened the wardrobe — and the login where you have not
/// opened anything yet is precisely the login it exists for.
/// <para>
/// Opened and closed by <see cref="PreOpenCheck"/> from whether there is an offer outstanding, rather
/// than by anybody setting <c>IsOpen</c>. There is exactly one thing this window is for, and it is
/// true or it is not; a second copy of that state here could only ever disagree with the first.
/// </para>
/// <para>
/// The body is drawn by <see cref="PluginUi"/>, as <see cref="SettingsWindow"/>'s is, so the version
/// above the grid and this one cannot drift apart.
/// </para>
/// </remarks>
public class LastWornWindow : Window, IDisposable
{
    private readonly PluginUi        _ui;
    private readonly Configuration   _config;
    private readonly LastWornService _lastWorn;

    /// <summary>How big the window is, always.</summary>
    /// <remarks>
    /// Unscaled, as Dalamud multiplies it by the Global Font Scale on the way out, so the window
    /// grows with the text rather than clipping it. Wide enough for the three answers in one row and
    /// the paragraph above them in four lines, with room for a headline that runs to two.
    /// </remarks>
    private static readonly Vector2 FixedSize = new(440, 215);

    public LastWornWindow(PluginUi ui, Configuration config, LastWornService lastWorn)
        : base("What you were wearing###WardrobeLastWorn",
               ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoCollapse)
    {
        _ui       = ui;
        _config   = config;
        _lastWorn = lastWorn;

        // Always rather than FirstUseEver: there is one question here and one shape it is asked in,
        // so the window is a fixed size and its three answers can be laid out to the pixel across it.
        // Nothing in it is worth resizing for — it holds a sentence, a paragraph and three buttons,
        // and it is gone as soon as one of them is pressed.
        Size          = FixedSize;
        SizeCondition = ImGuiCond.Always;
    }

    /// <summary>
    /// Open exactly while there is an offer to make and a window is where it is being made.
    /// </summary>
    /// <remarks>
    /// Answering the offer clears it, which closes this on the next frame without anybody having to
    /// remember to. Turning the setting off while it is on screen closes it too, which is the right
    /// answer to "not like that": the offer itself is untouched and comes back above the grid.
    /// </remarks>
    public override void PreOpenCheck() =>
        IsOpen = _config.LastWornOfferPopup && _lastWorn.Offer != null;

    /// <summary>Closing the window is one of the three answers, and means "not now".</summary>
    /// <remarks>
    /// The mildest of them, which is what the close button and the Escape key should mean: the
    /// record is left alone and the offer comes back at the next login. Reached by answering it
    /// properly as well — the offer is already gone by then, and dismissing nothing does nothing.
    /// <para>
    /// Except when the setting is what closed it. Turning the window off is an answer about where to
    /// ask, not about the clothes, so the offer is left standing for the grid to draw instead —
    /// which is what <see cref="PreOpenCheck"/> has just decided should happen.
    /// </para>
    /// </remarks>
    public override void OnClose()
    {
        if (_config.LastWornOfferPopup) _lastWorn.DismissOffer();
    }

    /// <remarks>
    /// Centred on the screen each time it appears, rather than once: it is a question rather than a
    /// panel, it is shown for as long as it takes to answer, and a prompt that opens off the edge of
    /// the screen because of where it was last dragged is a prompt nobody answers. Still draggable
    /// while it is up.
    /// <para>
    /// The pivot needs the window's size to work, which is why this window has one rather than
    /// auto-resizing to its contents — see <c>PluginUi.CentreQuickView</c> for what centring an
    /// auto-sized window on the frame it appears actually does.
    /// </para>
    /// </remarks>
    public override void PreDraw()
    {
        var vp     = ImGui.GetMainViewport();
        var centre = new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f);

        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        FontScope.Push(ref _fontScope);
    }

    public override void PostDraw() => FontScope.Pop(ref _fontScope);

    /// <summary>Held between <see cref="PreDraw"/> and <see cref="PostDraw"/>. See <see cref="FontScope"/>.</summary>
    private IDisposable? _fontScope;

    public override void Draw()
    {
        UiLayout.PushWrap();
        _ui.DrawLastWornOfferWindow();
        UiLayout.PopWrap();
    }

    public void Dispose() => FontScope.Pop(ref _fontScope);
}
