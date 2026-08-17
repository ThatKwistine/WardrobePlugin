using System.Collections.Generic;
using System.Linq;

namespace WardrobePlugin.Models;

/// <summary>
/// Something with a set of pictures: one cover and any number of others.
/// </summary>
/// <remarks>
/// Items and outfits both hold pictures the same way and needed the same handful of operations on
/// them, so the operations live once, here, rather than twice on two classes that would drift apart.
/// <para>
/// The cover stays the single <c>ImagePath</c> it always was rather than becoming the first entry of
/// one list. Everything that shows a card, a thumbnail, a preview or a tooltip reads that property,
/// and a session filing its first shot writes it — turning it into a list position would have meant
/// touching every one of those to gain nothing. The extra pictures are additions to it, so a wardrobe
/// saved before they existed is already correct.
/// </para>
/// </remarks>
public interface IImageOwner
{
    /// <summary>The picture shown on the card. Null when there is none.</summary>
    string? ImagePath { get; set; }

    /// <summary>
    /// Further pictures — other angles, other lighting, a detail worth keeping.
    /// </summary>
    /// <remarks>
    /// Never the cover, and never a duplicate of it: <see cref="ImageOwnerEx"/> is what maintains
    /// that, and everything that changes a picture set should go through it rather than adding here
    /// directly.
    /// </remarks>
    List<string> ExtraImages { get; set; }
}

public static class ImageOwnerEx
{
    /// <summary>
    /// Every picture, cover first. Blank entries are dropped and each path appears once.
    /// </summary>
    /// <remarks>
    /// Cover first because that is the order a gallery should open in, and because "the first picture"
    /// and "the picture on the card" being the same thing is what stops the two disagreeing. Files
    /// that have since been deleted are still listed: whether a path resolves is for the drawing code
    /// to discover, and hiding it here would leave a picture that cannot be removed because nothing
    /// shows it.
    /// </remarks>
    public static List<string> AllImages(this IImageOwner owner)
    {
        var all = new List<string>();

        if (!string.IsNullOrWhiteSpace(owner.ImagePath))
            all.Add(owner.ImagePath!);

        foreach (var path in owner.ExtraImages)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (all.Any(p => PathEquals(p, path))) continue;
            all.Add(path);
        }

        return all;
    }

    /// <summary>How many pictures there are in total, the cover included.</summary>
    public static int ImageCount(this IImageOwner owner) => owner.AllImages().Count;

    /// <summary>True when there is more than one picture, so a gallery is worth offering.</summary>
    public static bool HasGallery(this IImageOwner owner) => owner.ImageCount() > 1;

    /// <summary>
    /// Adds a picture: as the cover when there is none, otherwise alongside the others.
    /// </summary>
    /// <remarks>
    /// The first picture becoming the cover is what makes dropping one onto an empty card do the
    /// obvious thing. Adding one already in the set does nothing rather than repeating it.
    /// </remarks>
    /// <returns>True when the set changed.</returns>
    public static bool AddImage(this IImageOwner owner, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.Trim();
        if (owner.AllImages().Any(p => PathEquals(p, trimmed))) return false;

        if (string.IsNullOrWhiteSpace(owner.ImagePath))
            owner.ImagePath = trimmed;
        else
            owner.ExtraImages.Add(trimmed);

        return true;
    }

    /// <summary>
    /// Removes a picture, promoting the first of the others when the cover is the one going.
    /// </summary>
    /// <remarks>
    /// Promoting rather than leaving the cover empty: a card whose picture was removed while three
    /// others sat behind it would read as having lost all of them. The file itself is never touched —
    /// these are paths into a folder the user owns, and deleting someone's photographs because they
    /// took one off a card would be indefensible.
    /// </remarks>
    /// <returns>True when the set changed.</returns>
    public static bool RemoveImage(this IImageOwner owner, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (owner.ImagePath is { } cover && PathEquals(cover, path))
        {
            owner.ImagePath = null;
            Promote(owner);
            return true;
        }

        return owner.ExtraImages.RemoveAll(p => PathEquals(p, path)) > 0;
    }

    /// <summary>
    /// Makes a picture the cover, keeping the one it replaces among the others.
    /// </summary>
    /// <remarks>
    /// A swap rather than a move: the old cover goes to the front of the extras, so choosing the
    /// wrong one is undone by choosing the old one again and the set never loses a picture to a
    /// misclick.
    /// </remarks>
    /// <returns>True when the set changed.</returns>
    public static bool MakeCover(this IImageOwner owner, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (owner.ImagePath is { } cover && PathEquals(cover, path)) return false;

        var trimmed = path.Trim();
        owner.ExtraImages.RemoveAll(p => PathEquals(p, trimmed));

        if (!string.IsNullOrWhiteSpace(owner.ImagePath))
            owner.ExtraImages.Insert(0, owner.ImagePath!);

        owner.ImagePath = trimmed;
        return true;
    }

    /// <summary>
    /// Drops every picture but the cover, for a session about to file a fresh set of angles.
    /// </summary>
    /// <remarks>
    /// Only ever called where the session is taking more than one shot of the target, so it is
    /// replacing exactly what it is about to write. A one-shot session leaves the extras alone —
    /// re-taking a cover should not throw away angles it is not going to re-take.
    /// </remarks>
    public static void ClearExtraImages(this IImageOwner owner) => owner.ExtraImages.Clear();

    /// <summary>Moves the first extra picture into an empty cover, if there is one.</summary>
    private static void Promote(IImageOwner owner)
    {
        if (owner.ExtraImages.Count == 0) return;

        owner.ImagePath = owner.ExtraImages[0];
        owner.ExtraImages.RemoveAt(0);
    }

    /// <summary>
    /// Whether two stored paths mean the same picture.
    /// </summary>
    /// <remarks>
    /// Case-insensitive because Windows paths are, and trimmed because a path typed into the edit
    /// panel's box arrives with whatever whitespace was around it. Nothing is canonicalised beyond
    /// that: the same file reached by two different routes is a problem no comparison here can see,
    /// and every path the wardrobe writes itself comes from the same folder setting.
    /// </remarks>
    private static bool PathEquals(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), System.StringComparison.OrdinalIgnoreCase);
}
