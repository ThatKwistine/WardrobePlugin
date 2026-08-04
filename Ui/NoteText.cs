using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Utility;

namespace WardrobePlugin.Ui;

/// <summary>
/// Finds web links in an item's notes and draws them as clickable rows.
/// </summary>
/// <remarks>
/// Notes travel between people through the share service, so a link here is not necessarily one
/// the reader wrote. Two rules follow, and both are deliberate: only http and https are ever made
/// clickable — a file:// or a custom scheme can start a program, and nothing about a wardrobe needs
/// that — and the text of a link is always the address itself. Showing a label over a different
/// destination is exactly how a hostile link hides, so here what is on screen is what opens.
/// </remarks>
public static class NoteText
{
    // Everything up to whitespace is taken and the trailing punctuation trimmed afterwards, so a
    // link at the end of a sentence survives the full stop
    private static readonly Regex UrlPattern =
        new(@"^https?://[^\s]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Punctuation that reads as part of the sentence rather than the address
    private static readonly char[] TrailingPunctuation =
        { '.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'' };

    private static readonly char[] Separators = { ' ', '\t', '\r', '\n' };

    private static readonly Vector4 LinkColour        = new(0.45f, 0.70f, 1f, 1f);
    private static readonly Vector4 LinkColourHovered = new(0.65f, 0.85f, 1f, 1f);

    /// <summary>Web addresses in the notes, in the order written, without repeats.</summary>
    public static List<string> Links(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return new List<string>();

        return notes.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimEnd(TrailingPunctuation))
            .Where(w => UrlPattern.IsMatch(w) && IsSafe(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasLink(string? notes) => Links(notes).Count > 0;

    /// <summary>Draws each link in the notes as a row that opens it in the default browser.</summary>
    public static void DrawLinks(string? notes)
    {
        var links = Links(notes);
        if (links.Count == 0) return;

        ImGui.TextDisabled("Links");

        foreach (var url in links)
        {
            ImGui.PushID(url);
            DrawLink(url);
            ImGui.PopID();
        }
    }

    private static void DrawLink(string url)
    {
        // Coloured before the text is drawn, so the hover state is worked out from the rectangle it
        // is about to occupy — ImGui can only report hovering on an item that already exists
        // Measured at the width the text will actually be wrapped to: a URL is easily wider than
        // the panel, and a single-line measurement would put the hover rectangle off the side
        var start   = ImGui.GetCursorScreenPos();
        var avail   = ImGui.GetContentRegionAvail().X;
        var size    = ImGui.CalcTextSize(url, false, avail);
        var hovered = ImGui.IsMouseHoveringRect(start, new Vector2(start.X + size.X, start.Y + size.Y));
        var colour  = hovered ? LinkColourHovered : LinkColour;

        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted(url);
        ImGui.PopStyleColor();

        // Only underline a link that fits on one line — across a wrapped one the rule would run
        // under the whole block, past where the last line ends. The colour still marks it as a link.
        if (size.Y <= ImGui.GetTextLineHeight())
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), ImGui.GetColorU32(colour));
        }

        if (!ImGui.IsItemHovered()) return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetTooltip($"{url}\n\nOpens in your browser.");

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            Open(url);
    }

    /// <summary>
    /// Second gate on the address, in case the pattern above was talked into matching something it
    /// should not have. Only absolute http and https ever get as far as the browser.
    /// </summary>
    private static bool IsSafe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void Open(string url)
    {
        if (!IsSafe(url)) return;

        // Handed to Dalamud rather than started as a process here: it is the sanctioned way out to
        // the browser, and a plugin launching processes of its own is worth avoiding on principle.
        try { Util.OpenLink(url); }
        catch { /* nothing sensible to do if the browser will not open */ }
    }
}
