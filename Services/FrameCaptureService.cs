using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
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
public unsafe class FrameCaptureService
{
    private readonly IPluginLog _log;

    public FrameCaptureService(IPluginLog log) => _log = log;

    /// <summary>Frames left before the capture fires, or zero when none is armed.</summary>
    private int _armedFrames;

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
    public void Tick()
    {
        if (_armedFrames == 0) return;
        if (--_armedFrames > 0) return;

        Last = Capture(_armedPath);
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
}
