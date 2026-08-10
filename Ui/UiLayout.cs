using Dalamud.Bindings.ImGui;

namespace WardrobePlugin.Ui;

/// <summary>
/// Layout helpers for content that has to fit a narrow column.
/// </summary>
/// <remarks>
/// The right-hand panel is a fixed 360px and the window itself resizes down to 480, so anything
/// laid out by hand runs off the side unless it is told where the edge is. Prose is handled once
/// per window with <c>ImGui.PushTextWrapPos(0)</c>, which covers every Text call inside it; these
/// helpers cover what wrapping cannot — rows of buttons and badges joined with SameLine.
/// </remarks>
public static class UiLayout
{
    // ── Removal guard ─────────────────────────────────────────────────────────

    /// <summary>Whether a removal control is currently armed.</summary>
    public static bool DeleteArmed => ImGui.GetIO().KeyCtrl;

    /// <summary>
    /// A button that removes something, inert and greyed until Ctrl is held.
    /// </summary>
    /// <param name="label">Button text, including any <c>##id</c> suffix.</param>
    /// <param name="tooltip">What this removes, shown under the Ctrl hint. No trailing newline.</param>
    /// <param name="size">Size for a full-height button. Null draws a small one.</param>
    /// <remarks>
    /// Every control that takes something away goes through here — deleting an item, an outfit, a
    /// preset or a pack, and equally taking a piece out of an outfit or a tag off an item. The line
    /// is not how hard a thing is to get back: these sit inches from the buttons people press
    /// constantly, and a rule with exceptions is one nobody can predict.
    /// <para>
    /// Disabled rather than merely inert while Ctrl is up, so the reason is visible before the
    /// click. The tooltip shows either way — a disabled control that also refuses to say why is a
    /// dead end.
    /// </para>
    /// </remarks>
    public static bool DeleteButton(string label, string tooltip, System.Numerics.Vector2? size = null)
    {
        var armed = DeleteArmed;

        ImGui.PushStyleColor(ImGuiCol.Button,        new System.Numerics.Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.5f, 0.1f, 0.1f, 1f));

        if (!armed) ImGui.BeginDisabled();

        var clicked = size is { } s ? ImGui.Button(label, s) : ImGui.SmallButton(label);

        if (!armed) ImGui.EndDisabled();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(armed ? tooltip : $"Hold Ctrl to remove.\n\n{tooltip}");

        return clicked;
    }

    /// <summary>
    /// The same guard as a context-menu entry, with Ctrl shown where a shortcut would be.
    /// </summary>
    /// <remarks>
    /// ImGui greys a disabled menu item and ignores clicks on it by itself, so the guard is the
    /// enabled flag; the shortcut column is what says why, since a menu item has no room for a
    /// tooltip anyone would wait for.
    /// </remarks>
    public static bool DeleteMenuItem(string label) =>
        ImGui.MenuItem(label, "Ctrl", false, DeleteArmed);

    /// <summary>
    /// Wraps every Text call in the current window at its right edge, until <see cref="PopWrap"/>.
    /// </summary>
    /// <remarks>
    /// Must be pushed per window: the wrap position lives on the window's draw state, and a child
    /// window starts with its own. Every panel and child that draws prose pushes this once.
    /// </remarks>
    public static void PushWrap() => ImGui.PushTextWrapPos(0f);

    /// <summary>Ends a <see cref="PushWrap"/> scope.</summary>
    public static void PopWrap() => ImGui.PopTextWrapPos();

    /// <summary>Suspends wrapping, for text positioned to the pixel and known to fit.</summary>
    public static void PushNoWrap() => ImGui.PushTextWrapPos(-1f);

    /// <summary>Width a Button or SmallButton with this label occupies.</summary>
    public static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>Width a Checkbox with this label occupies — the box, then the label beside it.</summary>
    public static float CheckboxWidth(string label) =>
        ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    /// <summary>
    /// SameLine, but only while something of the given width still fits beside the last item.
    /// Past that the row carries on over the next line instead of running off the side.
    /// </summary>
    public static void SameLineIfRoom(float nextWidth)
    {
        // Measured from the cursor, which after an item sits at the start of the next line: its X
        // is the content's left edge, and what remains from there is the full content width.
        var rightEdge = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var wouldEnd  = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + nextWidth;

        if (wouldEnd <= rightEdge) ImGui.SameLine();
    }

    /// <summary>
    /// <see cref="SameLineIfRoom(float)"/> for a button, measured from its visible label — pass the
    /// label without any <c>##id</c> suffix, which is not drawn.
    /// </summary>
    public static void SameLineIfRoomForButton(string label) => SameLineIfRoom(ButtonWidth(label));

    /// <summary>
    /// <see cref="SameLineIfRoom(float)"/> for a run of text, so a badge after a label drops to the
    /// next line rather than being cut off.
    /// </summary>
    public static void SameLineIfRoomForText(string text) => SameLineIfRoom(ImGui.CalcTextSize(text).X);
}
