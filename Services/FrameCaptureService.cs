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
            Plugin.PluginInterface.UiBuilder.DisableUserUiHide = true;

            atk->SetUiVisibility(false);
            _restoreUi = true;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.PluginInterface.UiBuilder.DisableUserUiHide = false;
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
            Plugin.PluginInterface.UiBuilder.DisableUserUiHide = false;
        }
    }

    /// <summary>Reads the back buffer and writes it out as a PNG.</summary>
    private FrameCaptureResult Capture(string path)
    {
        ID3D11Device*        device  = null;
        ID3D11DeviceContext* context = null;
        ID3D11Texture2D*     staging = null;

        try
        {
            var dev = Device.Instance();
            if (dev == null || dev->SwapChain == null || dev->SwapChain->BackBuffer == null)
                return Failed("The game's swap chain was not available.");

            var source = (ID3D11Texture2D*)dev->SwapChain->BackBuffer->D3D11Texture2D;
            if (source == null)
                return Failed("The back buffer had no texture behind it.");

            D3D11_TEXTURE2D_DESC desc;
            source->GetDesc(&desc);

            // Taken from the texture rather than from the game's own device fields, so the device is
            // certainly the one this texture belongs to rather than one that merely ought to be
            source->GetDevice(&device);
            if (device == null)
                return Failed("The back buffer would not name its device.");

            device->GetImmediateContext(&context);
            if (context == null)
                return Failed("The immediate context was not available.");

            if (desc.SampleDesc.Count > 1)
                return Failed($"The back buffer is multisampled ({desc.SampleDesc.Count}x), which " +
                              "needs resolving before it can be read. Not handled by this test.");

            var format = desc.Format;
            if (!IsReadableFormat(format))
                return new FrameCaptureResult(
                    false, string.Empty, (int)desc.Width, (int)desc.Height, format.ToString(),
                    "The back buffer is in a format this test does not convert — most likely HDR. " +
                    "It can be read, but it needs tone mapping to become a sane PNG, which is work " +
                    "worth doing only once the rest of the idea has been proved.");

            // A copy the processor is allowed to read. The back buffer itself never is
            var stagingDesc = desc;
            stagingDesc.Usage          = D3D11_USAGE.D3D11_USAGE_STAGING;
            stagingDesc.BindFlags      = 0;
            stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags      = 0;

            var hr = device->CreateTexture2D(&stagingDesc, null, &staging);
            if (hr.FAILED || staging == null)
                return Failed($"A readable copy of the back buffer could not be made (0x{hr.Value:X8}).");

            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)source);

            D3D11_MAPPED_SUBRESOURCE mapped;
            hr = context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            if (hr.FAILED)
                return Failed($"The copied frame could not be read (0x{hr.Value:X8}).");

            try
            {
                Write(path, (byte*)mapped.pData, (int)mapped.RowPitch,
                      (int)desc.Width, (int)desc.Height, format);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }

            var result = new FrameCaptureResult(
                true, path, (int)desc.Width, (int)desc.Height, format.ToString(),
                "Compare this against a screenshot the game took of the same scene.");

            _log.Information($"[Wardrobe] Frame capture: wrote {desc.Width}x{desc.Height} " +
                             $"({format}) to {path}");
            return result;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Wardrobe] Frame capture: failed.");
            return Failed("The capture threw: " + ex.Message);
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
    private static void Write(string path, byte* data, int rowPitch, int width, int height,
                              DXGI_FORMAT format)
    {
        var swapRedAndBlue = format is DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM
                                    or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, width, height),
                                     ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                var src = data + (long)y * rowPitch;
                var dst = (byte*)locked.Scan0 + (long)y * locked.Stride;

                for (var x = 0; x < width; x++)
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

                    d[3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, ImageFormat.Png);
    }

    private FrameCaptureResult Failed(string note)
    {
        _log.Warning("[Wardrobe] Frame capture: " + note);
        return new FrameCaptureResult(false, string.Empty, 0, 0, "-", note);
    }

    /// <summary>Never leave the game without its interface because the plugin went away.</summary>
    public void Dispose() => RestoreUi();
}
