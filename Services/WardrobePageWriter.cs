using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace WardrobePlugin.Services;

/// <summary>How the written page carries its pictures. Values are persisted, so do not renumber.</summary>
public enum HtmlExportLayout
{
    /// <summary>A folder holding <c>index.html</c> with an <c>images</c> folder beside it.</summary>
    Folder     = 0,

    /// <summary>One <c>.html</c> file with every picture embedded in it.</summary>
    SingleFile = 1,
}

/// <summary>What a caller wants done with a page, and where.</summary>
public sealed class PageWriteOptions
{
    /// <summary>Folder the export is written into. It is created if it does not exist.</summary>
    public string Folder = string.Empty;

    /// <summary>A folder of files, or one self-contained page.</summary>
    public HtmlExportLayout Layout = HtmlExportLayout.Folder;

    /// <summary>Longest edge of the full-size picture behind a card. A ceiling, never an upscale.</summary>
    public int ImageSize = WardrobePageWriter.ImageSizes[1];

    /// <summary>
    /// Stem of the folder or file that is written, before the timestamp is appended.
    /// </summary>
    /// <remarks>
    /// Part of the options because the two sources want different words on disk: your own wardrobe
    /// exports as <c>Wardrobe-…</c>, and a bundle somebody sent should not land in the same folder
    /// under the same name as though it were yours.
    /// </remarks>
    public string Stem = "Wardrobe";

    /// <summary>Called as pictures are prepared, for a UI that wants to say how far along it is.</summary>
    public Action<string>? Progress;

    /// <summary>Called with a picture that could not be read, and why. Never throws the export away.</summary>
    public Action<string, string>? PictureFailed;
}

/// <summary>What a write produced.</summary>
public readonly record struct PageWriteResult(string Path, int Pictures, long Bytes)
{
    /// <summary>The size in the unit somebody would say it in.</summary>
    public string Size => WardrobePageWriter.Describe(Bytes);
}

/// <summary>
/// Puts a <see cref="PageModel"/> on disk: resizes its pictures, places them, and writes the page.
/// </summary>
/// <remarks>
/// The half of the job <see cref="WardrobePage"/> deliberately does not do. The renderer turns a
/// model into markup and knows nothing about files; this knows about files and nothing about markup,
/// beyond handing the renderer picture references it has already put somewhere.
/// <para>
/// Shared for the same reason the renderer is. Resizing, the two picture sizes, the never-upscale
/// rule, the white ground under a transparent PNG, the timestamped output that overwrites nothing —
/// all of that is the same work whether the wardrobe is yours or came out of a file, and having it
/// once is what stops the two answers diverging.
/// </para>
/// </remarks>
public static class WardrobePageWriter
{
    /// <summary>Longest edge, in pixels, offered for the full-size picture behind a card.</summary>
    /// <remarks>
    /// Never an upscale — a 512-pixel capture asked to be 1920 is written at 512 — so each of these
    /// is a ceiling, and only the smaller ones actually decide the size of an export.
    /// </remarks>
    public static readonly int[] ImageSizes = { 512, 800, 1280, 1920 };

    /// <summary>Longest edge of the picture drawn on a card, whatever the full size is.</summary>
    /// <remarks>
    /// Fixed rather than offered. A grid of three hundred cards is what decides whether the page
    /// opens at all, and it is the one place a picture is being looked at two inches wide — so there
    /// is no size worth choosing here, only one worth getting right.
    /// </remarks>
    private const int ThumbSize = 360;

    /// <summary>
    /// Writes the model and its pictures, into a folder of their own or into one file.
    /// </summary>
    /// <remarks>
    /// Every write is stamped with the minute it was made and nothing is ever overwritten. The
    /// alternative was emptying a folder somebody had chosen, which is not a thing to do on their
    /// behalf — a mistyped path would take the folder's real contents with it.
    /// <para>
    /// The model is mutated: each card's <see cref="PageCard.Shots"/> is filled in from its
    /// <see cref="PageCard.ImageSources"/>. Writing the same model twice is therefore fine but does
    /// the picture work twice.
    /// </para>
    /// </remarks>
    public static PageWriteResult Write(PageModel model, PageWriteOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Folder))
            throw new ArgumentException("No folder to write to.", nameof(options));

        Directory.CreateDirectory(options.Folder);

        var stamp  = model.When.ToString("yyyyMMdd-HHmm");
        var stem   = Sanitise(options.Stem);
        var single = options.Layout == HtmlExportLayout.SingleFile;

        var root     = single ? options.Folder : Path.Combine(options.Folder, $"{stem}-{stamp}");
        var pagePath = single
            ? Path.Combine(options.Folder, $"{stem}-{stamp}.html")
            : Path.Combine(root, "index.html");
        var imageDir = Path.Combine(root, "images");

        if (!single)
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(imageDir);
        }

        var pictures = PlacePictures(model, imageDir, single, options);

        options.Progress?.Invoke("Writing the page...");
        File.WriteAllText(pagePath, WardrobePage.Render(model), new UTF8Encoding(false));

        var bytes = new FileInfo(pagePath).Length;
        if (!single && Directory.Exists(imageDir))
            bytes += new DirectoryInfo(imageDir).GetFiles().Sum(f => f.Length);

        return new PageWriteResult(single ? pagePath : root, pictures, bytes);
    }

    /// <summary>
    /// Re-encodes every picture the cards reference, once each, and fills in where each one landed.
    /// </summary>
    /// <remarks>
    /// Keyed on the source path, so a picture used by an item and again by the outfit it belongs to
    /// is written once. A file that has since been deleted, or that nothing here can open, is
    /// reported and skipped rather than failing the write: a wardrobe of three hundred with two
    /// broken paths should still produce a page with two hundred and ninety-eight pictures in it.
    /// </remarks>
    private static int PlacePictures(PageModel model, string imageDir, bool single,
                                     PageWriteOptions options)
    {
        var sources = model.AllCards
            .SelectMany(c => c.ImageSources)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var built = new Dictionary<string, PageShot>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var source in sources)
        {
            index++;
            options.Progress?.Invoke($"Preparing pictures ({index} of {sources.Count})...");

            if (!File.Exists(source))
            {
                options.PictureFailed?.Invoke(source, "the file is no longer there");
                continue;
            }

            try
            {
                // Read into memory first: Image.FromFile holds a lock on the picture for as long as
                // the object lives, and this one lives for two resizes
                using var stream = new MemoryStream(File.ReadAllBytes(source));
                using var image  = Image.FromStream(stream);

                var shot = new PageShot();

                if (single)
                {
                    shot.FullRef  = DataUri(Resize(image, options.ImageSize, 82));
                    shot.ThumbRef = DataUri(Resize(image, ThumbSize, 78));
                }
                else
                {
                    var stem  = $"{index:D4}-{Slug(Path.GetFileNameWithoutExtension(source))}";
                    var full  = $"{stem}.jpg";
                    var thumb = $"{stem}-thumb.jpg";

                    File.WriteAllBytes(Path.Combine(imageDir, full),  Resize(image, options.ImageSize, 82));
                    File.WriteAllBytes(Path.Combine(imageDir, thumb), Resize(image, ThumbSize, 78));

                    shot.FullRef  = $"images/{full}";
                    shot.ThumbRef = $"images/{thumb}";
                }

                built[source] = shot;
            }
            catch (Exception ex)
            {
                options.PictureFailed?.Invoke(source, ex.Message);
            }
        }

        foreach (var card in model.AllCards)
        {
            card.Shots.Clear();
            foreach (var path in card.ImageSources)
                if (built.TryGetValue(path, out var shot)) card.Shots.Add(shot);
        }

        return built.Count;
    }

    /// <summary>Re-encodes a picture as a JPEG no larger than <paramref name="maxEdge"/> on its long side.</summary>
    private static byte[] Resize(Image source, int maxEdge, long quality)
    {
        var edge   = maxEdge > 0 ? maxEdge : ImageSizes[1];
        var scale  = Math.Min(1f, edge / (float)Math.Max(source.Width, source.Height));
        var width  = Math.Max(1, (int)Math.Round(source.Width  * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            // White rather than the default black, so a PNG with transparency in it comes out
            // looking like a picture instead of a silhouette
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source,
                new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        }

        var codec     = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

        using var ms = new MemoryStream();
        bmp.Save(ms, codec, encParams);
        return ms.ToArray();
    }

    private static string DataUri(byte[] jpeg) => "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

    /// <summary>A file-name-safe version of a picture's own name, so the folder stays readable.</summary>
    private static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-')  sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "picture" : slug;
    }

    /// <summary>
    /// Makes a folder or file name out of whatever the caller offered as a stem.
    /// </summary>
    /// <remarks>
    /// Stricter than it needs to be for a stem the plugin chose itself, and deliberately: a shared
    /// wardrobe's name comes from a file somebody else wrote, and a name is not a thing to build a
    /// path out of on trust.
    /// </remarks>
    private static string Sanitise(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new StringBuilder(stem.Length);

        foreach (var c in stem)
        {
            // Replaced with a space rather than dropped: a name is usually illegal where one word
            // ends and the next begins, and deleting the separator runs the two together
            if (invalid.Contains(c) || c == '.') sb.Append(' ');
            else sb.Append(c);
        }

        var cleaned = string.Join(' ',
            sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (cleaned.Length > 40) cleaned = cleaned[..40].Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Wardrobe" : cleaned;
    }

    /// <summary>A byte count in the unit somebody would say it in.</summary>
    public static string Describe(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:0.0} GB"
        : bytes >= 1024L * 1024      ? $"{bytes / 1024d / 1024:0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}
