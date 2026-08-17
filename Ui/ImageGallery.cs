using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;

namespace WardrobePlugin.Ui;

/// <summary>
/// The row of thumbnails that manages an item's or an outfit's pictures.
/// </summary>
/// <remarks>
/// Shared by the item editor and the outfit editor, which live in different windows and would
/// otherwise have grown two versions of the same control. Everything it changes goes through
/// <see cref="ImageOwnerEx"/>, so which picture is the cover and what counts as a duplicate is decided
/// in one place rather than per caller.
/// <para>
/// Nothing here deletes a file. A thumbnail's × takes the picture off the item; the photograph stays
/// in the images folder, ready to be dragged back on from the browser.
/// </para>
/// </remarks>
public static class ImageGallery
{
    /// <summary>Payload the image browser drags, so a thumbnail row can be a drop target too.</summary>
    private const string DragPayload = "WRD_IMG";

    /// <summary>
    /// Draws the row and applies whatever the user did to it.
    /// </summary>
    /// <param name="id">Unique per drawing site, so two galleries on screen cannot share widget ids.</param>
    /// <param name="owner">The item or outfit whose pictures these are.</param>
    /// <param name="textures">Dalamud's texture provider, which caches the loads.</param>
    /// <param name="thumb">Side of each thumbnail, in already-scaled pixels.</param>
    /// <param name="onChanged">
    /// Called after any change, for saving the config and dropping whatever the caller caches. Not
    /// called for a click that changed nothing.
    /// </param>
    public static unsafe void Draw(string id, IImageOwner owner, ITextureProvider textures,
        float thumb, Action onChanged)
    {
        var images = owner.AllImages();

        ImGui.TextDisabled(images.Count switch
        {
            0 => "Pictures",
            1 => "Pictures  ·  1",
            _ => $"Pictures  ·  {images.Count}, first is the cover",
        });

        // Deferred: every one of these mutates the list being walked
        string? makeCover = null;
        string? remove    = null;
        string? added     = null;

        var perRow = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (thumb + ImGui.GetStyle().ItemSpacing.X)));

        for (var i = 0; i < images.Count; i++)
        {
            var path    = images[i];
            var isCover = i == 0;

            ImGui.PushID($"{id}_img_{i}");

            // Resolved before anything is pushed onto the style stack. A throw between a push and its
            // pop leaves the stack unbalanced, and an unbalanced ImGui stack is an access violation
            // several frames later rather than an exception here — so the fallible part happens first
            // and the drawing happens after it, outside the try.
            IDalamudTextureWrap? wrap = null;
            try
            {
                if (File.Exists(path)) wrap = textures.GetFromFile(path).GetWrapOrDefault();
            }
            catch { /* falls through to the placeholder */ }

            var drawn = wrap != null;

            if (wrap != null)
            {
                // The cover is ringed rather than labelled: a caption under every thumbnail would
                // double the height of the row to say something only one of them needs to say.
                // All three button colours, or hovering the cover would take the ring off it.
                if (isCover)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.85f, 0.7f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.8f, 0.3f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.75f, 0.62f, 0.18f, 1f));
                }

                ImageDraw.SquareButton($"{id}_thumb_{i}", wrap, thumb);

                if (isCover) ImGui.PopStyleColor(3);
            }
            else
            {
                // A missing file keeps its place in the row rather than vanishing from it. A picture
                // that cannot be shown is exactly the one that needs removing, and it cannot be
                // removed by a control that is not there.
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.12f, 0.12f, 1f));
                ImGui.Button($"?##{id}_missing_{i}", new Vector2(thumb, thumb));
                ImGui.PopStyleColor();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip((isCover ? "Cover picture.\n" : string.Empty) +
                                 (drawn ? string.Empty : "This file is missing.\n") +
                                 Path.GetFileName(path) +
                                 "\n\nRight-click for options.");

            if (ImGui.BeginPopupContextItem($"##{id}_ctx_{i}"))
            {
                ImGui.TextDisabled(Path.GetFileName(path));
                ImGui.Separator();

                if (!isCover && ImGui.MenuItem("Make this the cover"))
                    makeCover = path;

                if (ImGui.MenuItem("Remove from this item"))
                    remove = path;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Takes the picture off, leaving the file in your images folder.");

                ImGui.EndPopup();
            }

            // Dropping onto a thumbnail adds to the set rather than replacing that thumbnail: the row
            // is a place pictures are collected, and replacing one in the middle of it is not a thing
            // anybody asks for
            if (AcceptDrop(out var dropped)) added = dropped;

            if ((i + 1) % perRow != 0 && i < images.Count - 1) ImGui.SameLine();

            ImGui.PopID();
        }

        // The place to drop a new one, and the only thing in the row when there are none yet
        ImGui.PushID($"{id}_add");
        if (images.Count > 0 && images.Count % perRow != 0) ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.14f, 0.17f, 1f));
        ImGui.Button($"+##{id}_addbox", new Vector2(thumb, thumb));
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drag a picture here from the Image Browser to add it.\n\n" +
                             "Screenshot sessions fill this in for you: tick a camera preset to\n" +
                             "capture that angle as well as the cover.");

        if (AcceptDrop(out var addedHere)) added = addedHere;
        ImGui.PopID();

        var changed = false;
        if (makeCover != null) changed |= owner.MakeCover(makeCover);
        if (remove    != null) changed |= owner.RemoveImage(remove);
        if (added     != null) changed |= owner.AddImage(added);

        if (changed) onChanged();
    }

    /// <summary>
    /// Reads a dragged image path, if one was dropped on the widget just drawn.
    /// </summary>
    /// <remarks>
    /// The payload pointer is null-guarded before anything is called on it: <c>IsDelivery</c> on a null
    /// <c>ImGuiPayloadPtr</c> is an access violation, not an exception, and takes the game with it.
    /// </remarks>
    private static unsafe bool AcceptDrop(out string path)
    {
        path = string.Empty;
        if (!ImGui.BeginDragDropTarget()) return false;

        var payload = ImGui.AcceptDragDropPayload(DragPayload);
        var got     = false;

        if (Unsafe.As<ImGuiPayloadPtr, nint>(ref payload) != 0
            && payload.IsDelivery()
            && payload.DataSize > 0)
        {
            path = Encoding.UTF8.GetString((byte*)payload.Data, payload.DataSize).TrimEnd('\0');
            got  = !string.IsNullOrWhiteSpace(path);
        }

        ImGui.EndDragDropTarget();
        return got;
    }

    /// <summary>
    /// The pictures of a set that can actually be shown, for a viewer that has to page through them.
    /// </summary>
    /// <remarks>
    /// Missing files are dropped here, unlike in the editor row: paging onto a blank frame with no way
    /// to tell whether the picture failed or the arrow did is worse than the picture not being offered.
    /// The editor is where a broken path is dealt with, and it keeps them for exactly that reason.
    /// </remarks>
    public static List<string> Viewable(IImageOwner owner)
    {
        var usable = new List<string>();
        foreach (var path in owner.AllImages())
        {
            try { if (File.Exists(path)) usable.Add(path); }
            catch { /* an unreadable path is not viewable either */ }
        }
        return usable;
    }
}
