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
