using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using TerraFX.Interop.DirectX;

namespace WardrobePlugin.Services;

/// <summary>
/// How much of the frame a capture wants, and which way up.
/// </summary>
/// <remarks>
/// Read straight off the back buffer rather than cropped afterwards, so the pixels nobody is going
/// to keep are never copied, converted or written. A square item shot is a little over half the
/// frame; reading the other half and throwing it away is work done once per picture for nothing.
/// </remarks>
public enum CaptureShape
{
    /// <summary>The whole frame as it is. For the diagnostics test.</summary>
    Full,

    /// <summary>The largest centred square, which is what an item picture is cropped to.</summary>
    Square,

    /// <summary>The whole frame turned upright, for a shot framed in GPose's portrait mode.</summary>
    /// <remarks>
    /// Portrait mode draws its tall picture lying on its side across the ordinary wide frame, which
    /// is why the game's own screenshots of it come out upright — the game turns them as it saves.
    /// Reading the back buffer gets the picture as drawn, so the same turn has to happen here, and
    /// the whole frame is wanted rather than a slice of it: rotated, the full width becomes the full
    /// height, and a portrait cropped out of the middle instead would throw away most of the picture.
    /// </remarks>
    Portrait,
}

/// <summary>
/// What a captured frame turned out to be, whether or not one was captured.
/// </summary>
public readonly record struct FrameCaptureResult(
    bool   Captured,
    string Path,
    int    Width,
    int    Height,
    string Format,
    string Note);

/// <summary>
/// Takes the picture itself, out of the frame the game has just drawn.
/// </summary>
/// <remarks>
/// The alternative to asking the game for a screenshot, and the reason it is worth having: the game's
/// own screenshot function cannot be driven from a plugin at all — the request is accepted and the
/// picture never arrives — and the workaround for that is pressing the screenshot key, which is
/// synthetic input for something that ought not to need any. Reading the frame the game has already
/// drawn needs neither.
/// <para>
/// This class is deliberately only the experiment. It captures one frame to a file so the result can
/// be held up against a screenshot the game took of the same scene, because two things have to be
/// true before any of the session machinery could be moved onto it: the colours have to match the
/// pictures already in the wardrobe, and no plugin window may appear in the frame. Neither can be
/// settled by reasoning about render order, only by looking.
/// </para>
/// </remarks>
public unsafe class FrameCaptureService : IDisposable
{
    private readonly IPluginLog _log;

    public FrameCaptureService(IPluginLog log) => _log = log;

    /// <summary>Frames left before the capture fires, or zero when none is armed.</summary>
    private int _armedFrames;

    /// <summary>Frames left for the interface to actually go away before the frame is read.</summary>
    private int _settleFrames;

    /// <summary>Whether this capture is the one that hid the interface, and so must put it back.</summary>
    private bool _restoreUi;

    private bool _keepDrawing;

    /// <summary>
    /// Keeps draw callbacks coming even while the game's interface is hidden.
    /// </summary>
    /// <remarks>
    /// Not a nicety. Dalamud stops calling draw callbacks entirely while the game's interface is
    /// hidden, and the frame is read in a draw callback — so a player who hides their interface to
    /// frame a shot, which is exactly what someone taking wardrobe pictures does, would stop a
    /// session dead and never be told why. A session asks for this for as long as it runs.
    /// <para>
    /// One place decides the underlying flag, because two things want it — a session for its whole
    /// length, and a capture that hides the interface itself — and whichever finished first would
    /// otherwise switch it off underneath the other.
    /// </para>
    /// </remarks>
    public bool KeepDrawing
    {
        get => _keepDrawing;
        set
        {
            _keepDrawing = value;
            ApplyKeepDrawing();
        }
    }

    private void ApplyKeepDrawing() =>
        Plugin.PluginInterface.UiBuilder.DisableUserUiHide = _keepDrawing || _restoreUi;

    /// <summary>Whether to hide the game's own interface for the capture.</summary>
    /// <remarks>
    /// Off, because it buys nothing. The crop takes the largest centred rectangle, which at
    /// 2560x1440 starts at x=560 for a square and x=875 for a portrait, and the Group Pose panels end
    /// long before either — so the interface never reaches the picture that gets filed.
    /// <para>
    /// It is also the dangerous path. Hiding the interface stops Dalamud calling draw callbacks at
    /// all, which is how the first version of this hid the interface and then lost the ability to put
    /// it back. That is handled now, but a switch that can strand someone's interface is not one to
    /// have on by default for a benefit the crop already provides.
    /// </para>
    /// </remarks>
    public bool HideUi { get; set; }

    /// <summary>Where the armed capture will write.</summary>
    private string _armedPath = string.Empty;

    /// <summary>Whether a capture is counting down.</summary>
    public bool Arming => _armedFrames > 0;

    /// <summary>Roughly how long is left, for a button to count down with.</summary>
    public float ArmingSeconds => _armedFrames / 60f;

    /// <summary>What the last capture came to, for the panel to show.</summary>
    public FrameCaptureResult? Last { get; private set; }

    /// <summary>
    /// Arms a capture a few seconds out, so the plugin's own windows can be closed first.
    /// </summary>
    /// <remarks>
    /// The delay is the whole point of the test rather than a convenience. Whether a plugin window
    /// lands in the frame is one of the two questions being asked, and it cannot be answered while
    /// the panel holding the button is still on screen.
    /// </remarks>
    public void Arm(string path, float seconds = 4f)
    {
        _armedPath   = path;
        _armedFrames = Math.Max(1, (int)(seconds * 60f));

        _log.Information($"[Wardrobe] Frame capture: armed, about {seconds:0} seconds. Close the " +
                         "plugin's windows and frame the shot. It writes to:");
        _log.Information("[Wardrobe] Frame capture:   " + path);
    }

    /// <summary>
    /// Counts an armed capture down and takes it. Must be called from the draw callback.
    /// </summary>
    /// <remarks>
    /// The draw callback is the render thread, which is the only place the immediate context may be
    /// touched, and it runs before Dalamud has drawn this frame's interface — which is the reason to
    /// expect a clean frame, and exactly the expectation the test exists to check.
    /// </remarks>
    /// <summary>Frames the interface may stay hidden before it is put back regardless.</summary>
    /// <remarks>
    /// The backstop for every way the capture can fail to reach its own restore. Two seconds is far
    /// longer than a capture needs and far shorter than the time it takes to wonder where the
    /// interface went.
    /// </remarks>
    private const int UiWatchdogFrames = 120;

    /// <summary>Frames left on the watchdog while the interface is hidden by this class.</summary>
    private int _uiWatchdog;

    public void Tick()
    {
        if (_restoreUi && --_uiWatchdog <= 0)
        {
            _log.Warning("[Wardrobe] Frame capture: the interface had been hidden too long — putting " +
                         "it back without waiting for the capture.");
            _settleFrames = 0;
            _armedFrames  = 0;
            RestoreUi();
            return;
        }

        // Settling: the interface has been told to go away and the frame it was in may still be the
        // one on screen, so the read waits for it to be gone rather than photographing it
        if (_settleFrames > 0)
        {
            if (--_settleFrames > 0) return;

            try
            {
                Last = Capture(_armedPath);
            }
            finally
            {
                RestoreUi();
            }

            return;
        }

        // A session's shot: no countdown, no interface hiding, no file. The frame as it is, now
        if (_requestedShape != null)
        {
            TakeRequestedShot();
            return;
        }

        if (_armedFrames == 0) return;
        if (--_armedFrames > 0) return;

        if (HideUi && TryHideUi())
        {
            _settleFrames = UiSettleFrames;
            _uiWatchdog   = UiWatchdogFrames;
            return;
        }

        Last = Capture(_armedPath);
    }

    /// <summary>Frames given to the interface to disappear before the frame is read.</summary>
    private const int UiSettleFrames = 3;

    /// <summary>Hides the game's interface, and says whether it was this call that hid it.</summary>
    /// <remarks>
    /// False when the interface was already hidden, which is not a failure: it means the player has
    /// hidden it themselves and it must be left exactly as found rather than switched back on
    /// underneath them at the end of the capture.
    /// </remarks>
    private bool TryHideUi()
    {
        try
        {
            var atk = RaptureAtkModule.Instance();
            if (atk == null || !atk->IsUiVisible) return false;

            // Without this the plugin hides the interface and is never called again, because Dalamud
            // stops running draw callbacks while the game's interface is hidden — so the frame is
            // never read and, far worse, the interface is never put back. Asked for first, and put
            // back in RestoreUi, so the window in which it applies is the capture and nothing else
            _restoreUi = true;
            ApplyKeepDrawing();

            atk->SetUiVisibility(false);
            return true;
        }
        catch (Exception ex)
        {
            _restoreUi = false;
            ApplyKeepDrawing();
            _log.Warning(ex, "[Wardrobe] Frame capture: the game's interface could not be hidden.");
            return false;
        }
    }

    /// <summary>
    /// Puts the interface back, if this capture was the one that took it away.
    /// </summary>
    /// <remarks>
    /// Safe to call at any time and from anywhere, including twice, because everything that can
    /// leave the interface hidden calls it: the capture, the watchdog, and the plugin unloading.
    /// </remarks>
    public void RestoreUi()
    {
        if (!_restoreUi) return;
        _restoreUi = false;

        try
        {
            var atk = RaptureAtkModule.Instance();
            if (atk != null) atk->SetUiVisibility(true);
        }
        catch (Exception ex)
        {
            // Worth shouting about: the interface is the player's whole game, and leaving it hidden
            // with nothing left running to notice is far worse than a missed picture
            _log.Error(ex, "[Wardrobe] Frame capture: the game's interface could not be put back. " +
                           "Press the key you use to hide the interface.");
        }
        finally
        {
            ApplyKeepDrawing();
        }
    }

    // ── Shots for a session ───────────────────────────────────────────────────

    /// <summary>The shape a session has asked for, or null when nothing is waiting.</summary>
    private CaptureShape? _requestedShape;

    /// <summary>Who to hand the picture to, on the framework thread.</summary>
    private Action<Bitmap?, string?>? _onShot;

    /// <summary>The picture that came back, waiting to be collected.</summary>
    private Bitmap? _shot;

    /// <summary>Why the last requested shot did not happen, or null if it did.</summary>
    private string? _shotError;

    /// <summary>Whether a requested shot has been taken and not yet collected.</summary>
    public bool ShotReady => _shot != null || _shotError != null;

    /// <summary>Whether a shot has been asked for and not yet taken.</summary>
    public bool ShotRequested => _requestedShape != null;

    /// <summary>
    /// Asks for a picture of the given shape. Safe to call from the framework thread.
    /// </summary>
    /// <remarks>
    /// A request rather than a call because the back buffer may only be touched from the render
    /// thread, and a session runs on the framework one. <see cref="Tick"/> takes it on the next
    /// frame and leaves the result to be collected.
    /// </remarks>
    public void RequestShot(CaptureShape shape, Action<Bitmap?, string?> onShot)
    {
        DiscardShot();
        _requestedShape = shape;
        _onShot         = onShot;
    }

    /// <summary>
    /// Collects the picture a session asked for. The caller owns the bitmap and must dispose it.
    /// </summary>
    public Bitmap? TakeShot(out string? error)
    {
        error       = _shotError;
        var bitmap  = _shot;
        _shot       = null;
        _shotError  = null;
        return bitmap;
    }

    /// <summary>Throws away anything uncollected, so a bitmap cannot be left behind.</summary>
    public void DiscardShot()
    {
        _shot?.Dispose();
        _shot           = null;
        _shotError      = null;
        _requestedShape = null;
        _onShot         = null;
    }

    /// <summary>Takes a requested shot, on the render thread.</summary>
    private void TakeRequestedShot()
    {
        var shape = _requestedShape!.Value;
        var onShot = _onShot;
        _requestedShape = null;
        _onShot         = null;

        Bitmap? bitmap = null;
        string? error  = null;

        try
        {
            bitmap = Grab(shape, out var reason);
            if (bitmap == null) error = reason ?? "The frame could not be read.";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Wardrobe] Frame capture: taking a session shot failed.");
            error = "The frame could not be read: " + ex.Message;
        }

        if (onShot == null)
        {
            bitmap?.Dispose();
            return;
        }

        // Handed over where a session can use it. Everything a session does with a picture — the
        // queue, the filing, what it belongs to — belongs to the framework thread
        Plugin.Framework.RunOnFrameworkThread(() => onShot(bitmap, error));
    }

    /// <summary>Reads the whole back buffer and writes it out as a PNG, for the test.</summary>
    private FrameCaptureResult Capture(string path)
    {
        var bitmap = Grab(CaptureShape.Full, out var reason, out var format);
        if (bitmap == null)
            return new FrameCaptureResult(false, string.Empty, 0, 0, format,
                                          reason ?? "The frame could not be read.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Wardrobe] Frame capture: the frame could not be written.");
            return new FrameCaptureResult(false, string.Empty, bitmap.Width, bitmap.Height, format,
                                          "The frame was read but could not be written: " + ex.Message);
        }
        finally
        {
            bitmap.Dispose();
        }

        _log.Information($"[Wardrobe] Frame capture: wrote {format} to {path}");
        return new FrameCaptureResult(true, path, bitmap.Width, bitmap.Height, format,
                                      "Compare this against a screenshot the game took of the same scene.");
    }

    /// <inheritdoc cref="Grab(CaptureShape, out string?, out string)"/>
    public Bitmap? Grab(CaptureShape shape, out string? reason) => Grab(shape, out reason, out _);

    /// <summary>
    /// Reads the part of the back buffer the wanted shape needs. Render thread only.
    /// </summary>
    /// <remarks>
    /// The whole picture-taking mechanism, and the reason the wardrobe does not need the game's
    /// screenshot function or a pressed key: the frame the game has drawn is already in memory, and
    /// this is a copy of the part of it worth keeping.
    /// </remarks>
    /// <param name="format">The back buffer's format, worth reporting whether or not this worked.</param>
    public Bitmap? Grab(CaptureShape shape, out string? reason, out string format)
    {
        reason = null;
        format = "-";

        ID3D11Device*        device  = null;
        ID3D11DeviceContext* context = null;
        ID3D11Texture2D*     staging = null;

        try
        {
            var dev = Device.Instance();
            if (dev == null || dev->SwapChain == null || dev->SwapChain->BackBuffer == null)
                return Failed("The game's swap chain was not available.", out reason);

            var source = (ID3D11Texture2D*)dev->SwapChain->BackBuffer->D3D11Texture2D;
            if (source == null)
                return Failed("The back buffer had no texture behind it.", out reason);

            D3D11_TEXTURE2D_DESC desc;
            source->GetDesc(&desc);
            format = desc.Format.ToString();

            // Taken from the texture rather than from the game's own device fields, so the device is
            // certainly the one this texture belongs to rather than one that merely ought to be
            source->GetDevice(&device);
            if (device == null)
                return Failed("The back buffer would not name its device.", out reason);

            device->GetImmediateContext(&context);
            if (context == null)
                return Failed("The immediate context was not available.", out reason);

            if (desc.SampleDesc.Count > 1)
                return Failed($"The back buffer is multisampled ({desc.SampleDesc.Count}x), which " +
                              "would need resolving before it could be read.", out reason);

            if (!IsReadableFormat(desc.Format))
                return Failed($"The back buffer is {desc.Format}, which is not a format this " +
                              "converts — most likely HDR, which needs tone mapping to become a " +
                              "sane picture.", out reason);

            // A copy the processor is allowed to read. The back buffer itself never is
            var stagingDesc = desc;
            stagingDesc.Usage          = D3D11_USAGE.D3D11_USAGE_STAGING;
            stagingDesc.BindFlags      = 0;
            stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags      = 0;

            var hr = device->CreateTexture2D(&stagingDesc, null, &staging);
            if (hr.FAILED || staging == null)
                return Failed($"A readable copy of the back buffer could not be made (0x{hr.Value:X8}).",
                              out reason);

            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)source);

            D3D11_MAPPED_SUBRESOURCE mapped;
            hr = context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            if (hr.FAILED)
                return Failed($"The copied frame could not be read (0x{hr.Value:X8}).", out reason);

            try
            {
                return Build(shape, (byte*)mapped.pData, (int)mapped.RowPitch,
                             (int)desc.Width, (int)desc.Height, desc.Format);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Wardrobe] Frame capture: failed.");
            return Failed("The capture threw: " + ex.Message, out reason);
        }
        finally
        {
            if (staging != null) staging->Release();
            if (context != null) context->Release();
            if (device  != null) device->Release();
        }
    }

    /// <summary>The two orderings a desktop back buffer is realistically in.</summary>
    /// <remarks>
    /// sRGB and linear variants of each are the same bytes and are converted the same way — the tag
    /// says how a shader should sample it, not how it is laid out. A format outside this list is
    /// reported rather than guessed at, because guessing produces a picture that is wrong in a way
    /// nobody notices until the wardrobe is full of them.
    /// </remarks>
    private static bool IsReadableFormat(DXGI_FORMAT format) => format is
        DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB or
        DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;

    /// <summary>Copies the mapped rows into a bitmap and saves it.</summary>
    /// <remarks>
    /// Row by row, because the pitch the graphics card hands back is its own business and is very
    /// often wider than the picture. Alpha is forced opaque: a back buffer's alpha channel is
    /// whatever the last thing to write it left behind, and carrying it into a PNG produces a picture
    /// that looks right in one viewer and half transparent in the next.
    /// </remarks>
    private static Bitmap Build(CaptureShape shape, byte* data, int rowPitch, int width, int height,
                               DXGI_FORMAT format)
    {
        // Only the columns the shape keeps are read. A square out of an ordinary wide frame is a
        // little over half of it, and the rest is never touched
        var cropW = shape == CaptureShape.Square ? Math.Min(width, height) : width;
        var cropH = height;
        var cropX = (width - cropW) / 2;

        var swapRedAndBlue = format is DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM
                                    or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;

        var bitmap = new Bitmap(cropW, cropH, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, cropW, cropH),
                                     ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < cropH; y++)
            {
                var src = data + (long)y * rowPitch + (long)cropX * 4;
                var dst = (byte*)locked.Scan0 + (long)y * locked.Stride;

                for (var x = 0; x < cropW; x++)
                {
                    var s = src + x * 4;
                    var d = dst + x * 4;

                    if (swapRedAndBlue)
                    {
                        d[0] = s[2];
                        d[1] = s[1];
                        d[2] = s[0];
                    }
                    else
                    {
                        d[0] = s[0];
                        d[1] = s[1];
                        d[2] = s[2];
                    }

                    // A back buffer's alpha is whatever last wrote it. Carried into a file it makes a
                    // picture that looks right in one viewer and half transparent in the next
                    d[3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        if (shape == CaptureShape.Portrait) bitmap.RotateFlip(PortraitRotation);

        return bitmap;
    }

    /// <summary>Which way a portrait-mode frame is turned to stand it upright.</summary>
    /// <remarks>
    /// Clockwise, which is the way portrait mode lays its picture down. Fixed rather than settable:
    /// there are only two answers, the game only ever gives one of them, and the wrong one is a
    /// picture that is plainly upside down rather than something anyone would want to choose.
    /// </remarks>
    private const RotateFlipType PortraitRotation = RotateFlipType.Rotate90FlipNone;

    private static Bitmap? Failed(string note, out string? reason)
    {
        reason = note;
        return null;
    }



    /// <summary>Never leave the game without its interface because the plugin went away.</summary>
    public void Dispose()
    {
        RestoreUi();
        KeepDrawing = false;
        DiscardShot();
    }
}
