using System;

namespace WardrobePlugin.Ui;

/// <summary>
/// Establishes Dalamud's default font around a whole window, title bar included.
/// </summary>
/// <remarks>
/// Without this a plugin window draws at a third of the intended font size while Dalamud's own
/// windows draw correctly, so raising <b>Global Font Scale</b> grows the plugin's boxes but not its
/// text. It is wrong inconsistently, too: ImGui passes a window's font scale down only one level of
/// child nesting (<see href="https://github.com/ocornut/imgui/issues/2701"/>), so content one child
/// deep renders small while content two deep springs back to the correct size. Measured rather than
/// guessed — probes reported a font size of 16 at the window and one child in, but 48 two children
/// in, all in the same frame with <c>FontGlobalScale</c> at 3.
/// <para>
/// Pushed from <c>Window.PreDraw</c> and popped from <c>Window.PostDraw</c> rather than inside
/// <c>Draw</c>, because those bracket ImGui's <c>Begin</c> — and the title bar is drawn by
/// <c>Begin</c>, so a push inside <c>Draw</c> comes too late to reach it.
/// </para>
/// </remarks>
public static class FontScope
{
    /// <summary>Pushes the default font, storing the scope to be popped later.</summary>
    /// <remarks>
    /// Disposes anything already held first. PreDraw and PostDraw are called by Dalamud rather than
    /// by us, so a frame that somehow skipped PostDraw would otherwise leak a push onto ImGui's
    /// stack — and an unbalanced font stack is a crash inside cimgui a frame or two later, far from
    /// the cause.
    /// </remarks>
    public static void Push(ref IDisposable? scope)
    {
        scope?.Dispose();
        scope = Plugin.PluginInterface.UiBuilder.DefaultFontHandle?.Push();
    }

    public static void Pop(ref IDisposable? scope)
    {
        scope?.Dispose();
        scope = null;
    }
}
