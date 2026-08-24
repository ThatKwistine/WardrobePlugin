using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Photo;

namespace WardrobePlugin.Services;

/// <summary>
/// Presses the game's own screenshot button, so a session does not have to wait for the user to.
/// </summary>
/// <remarks>
/// The last piece missing from an automated photoshoot. Everything else was already in place —
/// wearing each item in turn, moving the camera to each saved angle, watching the screenshots folder,
/// cropping what lands and filing it — and all of it sat waiting on one human keypress per picture.
/// <para>
/// This asks the game for that picture through <c>ScreenShot::ScheduleScreenShot</c>, the same call
/// the screenshot keybind ends up in. Going through the game rather than capturing the window
/// ourselves is what keeps the result identical to a picture taken by hand: the game's own file
/// format, its own folder, its own framing, and no plugin windows in it. The session's existing
/// folder watcher then picks the file up without knowing anything happened differently.
/// </para>
/// </remarks>
public unsafe class GameScreenshotService
{
    private readonly IPluginLog _log;

    public GameScreenshotService(IPluginLog log) => _log = log;

    /// <summary>Whether the game exposes its screenshot task at all.</summary>
    /// <remarks>
    /// False means an automated session cannot work, which is worth saying before one is started
    /// rather than leaving it counting down against a shutter that will never fire.
    /// </remarks>
    public bool Available => ScreenShot.Instance() != null;

    /// <summary>True while a screenshot has been asked for and not yet written.</summary>
    /// <remarks>
    /// What tells a waiting session the difference between a shot that is taking its time and one
    /// that never happened, so a slow disk extends the wait instead of triggering a second shot.
    /// </remarks>
    public bool Pending
    {
        get
        {
            var inst = ScreenShot.Instance();
            return inst != null && inst->ScreenShotRequested;
        }
    }

    /// <summary>
    /// Asks the game for a screenshot. Must be called on the framework thread.
    /// </summary>
    /// <returns>True if the game accepted the request.</returns>
    /// <remarks>
    /// No completion callback is passed. The game nulls that field once it has called it, so it has
    /// to cope with it being null anyway — and a function pointer into this assembly would outlive
    /// the assembly if the plugin were unloaded between the request and the file being written, which
    /// is a crash rather than a missed picture. The session finds out the shot landed the same way it
    /// always has, from the folder watcher.
    /// </remarks>
    public bool Take()
    {
        var inst = ScreenShot.Instance();
        if (inst == null)
        {
            _log.Warning("[Wardrobe] Automatic screenshot: the game's screenshot task was not available.");
            return false;
        }

        if (!inst->CanTakeScreenShot) return false;

        var accepted = inst->ScheduleScreenShot(null, null);
        if (!accepted)
            _log.Debug("[Wardrobe] Automatic screenshot: the game declined the request.");

        return accepted;
    }
}
