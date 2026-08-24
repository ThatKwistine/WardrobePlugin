using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// Sends a wardrobe to somebody else, and browses one they sent back.
/// </summary>
/// <remarks>
/// Both halves live in one window because they are one idea and share most of their vocabulary —
/// what a bundle is, what it can and cannot carry, why an item might be unavailable. Split across
/// two entry points, the half somebody has not used yet is the half that never explains itself.
/// <para>
/// The browsing half is deliberately not a second wardrobe. It shows what is in the file and offers
/// to bring pieces into the real one; it does not let anything be worn straight out of the bundle.
/// Wearing writes to <see cref="Configuration.WornItems"/>, and everything that later takes an item
/// off — unwear, strip, the mod-ownership bookkeeping — finds it by looking it up in
/// <see cref="Configuration.WardrobeItems"/>. An item worn without being there first would go on and
/// never come off, leaving its mods enabled with nothing recording that the wardrobe turned them on.
/// So <c>Add and Wear</c> genuinely adds first, and says so.
/// </para>
/// </remarks>
public class SharePanel : Window, IDisposable
{
    private readonly Configuration        _config;
    private readonly WardrobeService      _wardrobe;
    private readonly WardrobeShareService _share;
    private readonly PenumbraIpc          _penumbra;
    private readonly ITextureProvider     _textures;
    private readonly IPluginLog           _log;

    private readonly FileDialogManager _fileDialog = new();

    /// <summary>Which half of the window is showing.</summary>
    private bool _sending = true;

    // ── Sending ───────────────────────────────────────────────────────────────

    private string _author      = string.Empty;
    private string _description = string.Empty;
    private bool   _withImages  = true;
    private string _sendSearch  = string.Empty;
    private string _sendStatus  = string.Empty;

    /// <summary>Items ticked for export. Ids rather than references, so a deletion cannot strand one.</summary>
    private readonly HashSet<Guid> _picked = new();

    // ── Receiving ─────────────────────────────────────────────────────────────

    private WardrobeShare? _loaded;
    private string _loadedPath  = string.Empty;
    private string _imageDir    = string.Empty;
    private string _loadStatus  = string.Empty;
    private string _recvSearch  = string.Empty;
    private bool   _onlyWearable;
    private bool   _tagWithSender = true;

    /// <summary>Availability per <see cref="SharedItem.SourceId"/>, worked out once per load.</summary>
    private Dictionary<Guid, ItemAvailability> _availability = new();

    /// <summary>Collections to file imported mods under, and which one is chosen.</summary>
    private IList<string> _collections   = Array.Empty<string>();
    private int           _collectionIdx;

    /// <summary>
    /// <see cref="SharedItem.SourceId"/>s already sitting in the wardrobe, refreshed on load and
    /// after every import so a card can say it has already been taken.
    /// </summary>
    private readonly HashSet<Guid> _alreadyImported = new();

    private IDisposable? _fontScope;
    private float        _lastScaleFactor;

    private static float CardWidth => UiScale.S(150f);

    /// <summary>
    /// Filter for the open and save dialogs, in the <c>Label{.ext}</c> form Dalamud's file dialog
    /// parses. The bare extension is not a filter it understands, and passing one shows every file.
    /// </summary>
    private static readonly string ShareFilter = $"Wardrobe Share{{{WardrobeShare.Extension}}}";

    public SharePanel(Configuration config, WardrobeService wardrobe, WardrobeShareService share,
        PenumbraIpc penumbra, ITextureProvider textures, IPluginLog log)
        : base("Share Wardrobe###WardrobeShare")
    {
        SizeCondition = ImGuiCond.FirstUseEver;

        _config   = config;
        _wardrobe = wardrobe;
        _share    = share;
        _penumbra = penumbra;
        _textures = textures;
        _log      = log;
    }

    /// <summary>Nothing to release — kept so the window is torn down like the others.</summary>
    /// <remarks>
    /// <see cref="FileDialogManager"/> holds no unmanaged handle of its own and has no Dispose;
    /// the main window does not release its own either.
    /// </remarks>
    public void Dispose() { }

    public override void PreDraw()
    {
        FontScope.Push(ref _fontScope);

        // Same reasoning as MassImportPanel.PreDraw — see the note there
        var factor  = UiScale.Factor;
        var rescale = _lastScaleFactor > 0f && MathF.Abs(factor - _lastScaleFactor) > 0.001f;
        _lastScaleFactor = factor;

        Size          = new Vector2(900, 640);
        SizeCondition = rescale ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PostDraw() => FontScope.Pop(ref _fontScope);

    /// <summary>
    /// Opens the window, optionally on the sending half with a selection already ticked.
    /// </summary>
    /// <param name="preselected">
    /// Items to tick — what the main window had selected when Share was pressed there. Null opens
    /// with nothing ticked rather than with everything: an export is a deliberate act, and a window
    /// that opens with three hundred items ready to go is one misclick from sending a wardrobe
    /// somebody had not finished choosing.
    /// </param>
    public void Open(IEnumerable<Guid>? preselected = null)
    {
        if (preselected != null)
        {
            _picked.Clear();
            foreach (var id in preselected) _picked.Add(id);
            _sending = true;
        }

        _sendStatus = string.Empty;
        IsOpen = true;
    }

    public override void Draw()
    {
        UiLayout.PushWrap();

        DrawModeSwitch();
        ImGui.Separator();
        ImGui.Spacing();

        if (_sending) DrawSend();
        else          DrawReceive();

        UiLayout.PopWrap();

        // Drawn last so the dialog sits over the window that opened it
        _fileDialog.Draw();
    }

    private void DrawModeSwitch()
    {
        DrawModeButton(" Send a wardrobe ", true);
        ImGui.SameLine();
        DrawModeButton(" Open one I was sent ", false);
    }

    private void DrawModeButton(string label, bool sendingMode)
    {
        var active = _sending == sendingMode;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
        }

        if (ImGui.Button(label)) _sending = sendingMode;

        if (active) ImGui.PopStyleColor(2);
    }

    // ── Sending ───────────────────────────────────────────────────────────────

    private void DrawSend()
    {
        ImGui.TextWrapped("A share file describes your items — which mod each one needs, which " +
                          "options, which game item — and carries your pictures. It does not " +
                          "contain the mods themselves, so whoever opens it needs to already own " +
                          "them, or to go and get them.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Shared by");
        ImGui.SetNextItemWidth(UiScale.S(260f));
        ImGui.InputTextWithHint("##shareAuthor", "optional — a name to show at the top", ref _author, 64);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shown as the heading when somebody opens the file.\n\n" +
                             "Entirely up to you, and safe to leave blank — nothing is\n" +
                             "filled in for you and nothing checks it.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Note");
        ImGui.SetNextItemWidth(UiScale.S(420f));
        ImGui.InputTextWithHint("##shareNote", "optional — what this is", ref _description, 256);

        ImGui.Spacing();
        ImGui.Checkbox("Include pictures", ref _withImages);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off makes a much smaller file that still carries every item and\n" +
                             "every mod requirement — just without the photographs.\n\n" +
                             "Worth turning off for a big wardrobe going over Discord.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSendPickerBar();
        ImGui.Spacing();
        DrawSendList();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var none = _picked.Count == 0;
        if (none) ImGui.BeginDisabled();
        if (ImGui.Button("  Export…  ")) BeginExport();
        if (none) ImGui.EndDisabled();
        if (none && ImGui.IsItemHovered())
            ImGui.SetTooltip("Tick some items first.");

        if (!string.IsNullOrEmpty(_sendStatus))
        {
            ImGui.SameLine();
            ImGui.TextWrapped(_sendStatus);
        }
    }

    private void DrawSendPickerBar()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{_picked.Count} of {_config.WardrobeItems.Count} ticked");

        UiLayout.SameLineIfRoomForButton(" Tick All Shown ");
        if (ImGui.Button(" Tick All Shown "))
        {
            foreach (var item in VisibleForSend()) _picked.Add(item.Id);
            _sendStatus = string.Empty;
        }

        UiLayout.SameLineIfRoomForButton(" Clear ");
        if (ImGui.Button(" Clear "))
        {
            _picked.Clear();
            _sendStatus = string.Empty;
        }

        ImGui.SetNextItemWidth(UiScale.S(200f));
        ImGui.InputTextWithHint("##shareSearch", "Search…", ref _sendSearch, 64);
    }

    private IEnumerable<WardrobeItem> VisibleForSend()
    {
        if (string.IsNullOrWhiteSpace(_sendSearch)) return _config.WardrobeItems;

        var needle = _sendSearch.Trim();
        return _config.WardrobeItems.Where(i =>
            i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (i.Notes?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
            || i.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    private void DrawSendList()
    {
        var height = ImGui.GetContentRegionAvail().Y - UiScale.S(70f);
        if (height < UiScale.S(120f)) height = UiScale.S(120f);

        if (!ImGui.BeginChild("##shareList", new Vector2(0, height), true)) { ImGui.EndChild(); return; }

        UiLayout.PushWrap();

        foreach (var item in VisibleForSend())
        {
            var ticked = _picked.Contains(item.Id);
            ImGui.PushID(item.Id.ToString());

            if (ImGui.Checkbox($"{item.Name}##pick", ref ticked))
            {
                if (ticked) _picked.Add(item.Id);
                else        _picked.Remove(item.Id);
                _sendStatus = string.Empty;
            }

            var slot = item.Slot.DisplayName();
            UiLayout.SameLineIfRoomForText(slot);
            ImGui.TextDisabled(slot);

            ImGui.PopID();
        }

        UiLayout.PopWrap();
        ImGui.EndChild();
    }

    private void BeginExport()
    {
        var suggested = string.IsNullOrWhiteSpace(_author)
            ? $"wardrobe{WardrobeShare.Extension}"
            : $"{Sanitise(_author)}{WardrobeShare.Extension}";

        var start = Directory.Exists(_config.ImagesFolder)
            ? _config.ImagesFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _fileDialog.SaveFileDialog("Save Share File", ShareFilter, suggested,
            WardrobeShare.Extension, (ok, path) =>
        {
            if (!ok) return;

            // The dialog does not always append the extension, and a bundle without it is one the
            // open dialog's filter will not show
            if (!path.EndsWith(WardrobeShare.Extension, StringComparison.OrdinalIgnoreCase))
                path += WardrobeShare.Extension;

            var items = _config.WardrobeItems.Where(i => _picked.Contains(i.Id)).ToList();
            var result = _share.Export(items, path, _author, _description, _withImages);
            _sendStatus = result.Message;
        }, start);
    }

    /// <summary>Strips what a file name cannot contain, for suggesting one from free text.</summary>
    private static string Sanitise(string name)
    {
        var cleaned = new string(name.Trim()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "wardrobe" : cleaned;
    }

    // ── Receiving ─────────────────────────────────────────────────────────────

    private void DrawReceive()
    {
        if (ImGui.Button("  Open a share file…  ")) BeginOpen();

        if (!string.IsNullOrEmpty(_loadedPath))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Path.GetFileName(_loadedPath));
        }

        if (!string.IsNullOrEmpty(_loadStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_loadStatus);
        }

        if (_loaded == null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped("Open a .wardrobe file somebody sent you to browse what is in it.");
            ImGui.Spacing();
            ImGui.TextWrapped("You will be able to see every item they shared, and add the ones " +
                              "whose mods you already have. Items needing a mod you do not have " +
                              "are shown too, greyed out, with any link their notes carry — the " +
                              "file never contains the mods themselves.");
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawShareHeader(_loaded);

        ImGui.Spacing();
        DrawReceiveBar();
        ImGui.Spacing();

        DrawReceiveGrid(_loaded);
    }

    private void DrawShareHeader(WardrobeShare share)
    {
        var who = string.IsNullOrWhiteSpace(share.ExportedBy) ? "Shared wardrobe" : share.ExportedBy;
        ImGui.TextUnformatted(who);

        var wearable = _availability.Count(a => a.Value.CanWear);
        ImGui.TextDisabled($"{share.Items.Count} item(s) — you can wear {wearable} of them. " +
                           $"Shared {share.ExportedUtc.ToLocalTime():d}.");

        if (!string.IsNullOrWhiteSpace(share.Description))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(share.Description);
        }

        if (share.FormatVersion > WardrobeShare.CurrentFormat)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                "This file was made by a newer version of the plugin. Anything it uses that this " +
                "version does not know about has been left out.");
        }
    }

    private void DrawReceiveBar()
    {
        ImGui.SetNextItemWidth(UiScale.S(200f));
        ImGui.InputTextWithHint("##recvSearch", "Search…", ref _recvSearch, 64);

        ImGui.SameLine();
        ImGui.Checkbox("Only what I can wear", ref _onlyWearable);

        if (_collections.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("into");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(UiScale.S(180f));

            var names = _collections.ToArray();
            var idx   = Math.Min(_collectionIdx, names.Length - 1);
            if (ImGui.Combo("##recvCollection", ref idx, names, names.Length))
                _collectionIdx = idx;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Which of your Penumbra collections imported items are filed under.\n\n" +
                                 "The sender's own collection names are not carried across — they\n" +
                                 "describe their install, not yours.");
        }

        ImGui.Spacing();
        ImGui.Checkbox("Tag what I add with the sender's name", ref _tagWithSender);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds one tag to every item you take from this file, so they are\n" +
                             "easy to find together — and easy to remove again if you change\n" +
                             "your mind.");

        var addable = VisibleForReceive().Count(i => Available(i).CanWear && !_alreadyImported.Contains(i.SourceId));

        UiLayout.SameLineIfRoomForButton(" Add All Shown ");
        if (addable == 0) ImGui.BeginDisabled();
        if (ImGui.Button(" Add All Shown "))
        {
            var wanted = VisibleForReceive()
                .Where(i => Available(i).CanWear && !_alreadyImported.Contains(i.SourceId))
                .ToList();

            // Converted as a batch, which resolves the installed mod list once for the whole run
            // rather than once per item
            var converted = _share.ToLocalItems(wanted, ChosenCollection(), _imageDir);

            var added = 0;
            for (var i = 0; i < wanted.Count; i++)
                if (Register(wanted[i], converted[i])) added++;

            if (added > 0) _config.Save();

            _loadStatus = added == 0
                ? "Nothing was added."
                : $"Added {added} item(s) to your wardrobe.";
        }
        if (addable == 0) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(addable == 0
                ? "Nothing shown is both wearable and not already added."
                : $"Adds the {addable} item(s) shown that you have the mods for.");
    }

    private ItemAvailability Available(SharedItem item) =>
        _availability.TryGetValue(item.SourceId, out var a)
            ? a
            : new ItemAvailability(false, true, Array.Empty<SharedMod>(), Array.Empty<SharedMod>());

    private IEnumerable<SharedItem> VisibleForReceive()
    {
        if (_loaded == null) return Array.Empty<SharedItem>();

        IEnumerable<SharedItem> items = _loaded.Items;

        if (_onlyWearable) items = items.Where(i => Available(i).CanWear);

        if (!string.IsNullOrWhiteSpace(_recvSearch))
        {
            var needle = _recvSearch.Trim();
            items = items.Where(i =>
                i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (i.Notes?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || i.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        return items;
    }

    private void DrawReceiveGrid(WardrobeShare share)
    {
        if (!ImGui.BeginChild("##recvGrid", new Vector2(0, 0), false)) { ImGui.EndChild(); return; }

        var width   = CardWidth;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (width + spacing)));

        var column = 0;
        foreach (var item in VisibleForReceive())
        {
            if (column > 0) ImGui.SameLine();

            ImGui.PushID(item.SourceId.ToString());
            DrawSharedCard(item, width);
            ImGui.PopID();

            column = (column + 1) % columns;
        }

        ImGui.EndChild();
    }

    private void DrawSharedCard(SharedItem item, float width)
    {
        var availability = Available(item);
        var imported     = _alreadyImported.Contains(item.SourceId);

        ImGui.BeginGroup();

        // Two groups, not one. The inner group is what the tooltip hangs off — IsItemHovered reports
        // on the last item drawn, so hanging it off the card as a whole would have meant hovering
        // the badge and nothing else. The buttons stay outside it, or hovering one would raise the
        // tooltip over the button being aimed at.
        ImGui.BeginGroup();

        // The picture and labels dim when the mod behind the item is not installed. Dimmed rather
        // than hidden: the wardrobe being browsed is somebody else's, and quietly dropping the parts
        // of it the viewer cannot use would misrepresent what they were sent.
        if (!availability.CanWear) ImGui.BeginDisabled();

        DrawCardImage(item, width);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextUnformatted(item.Name);
        ImGui.PopTextWrapPos();

        ImGui.TextDisabled(item.Slot.DisplayName());

        if (!availability.CanWear) ImGui.EndDisabled();

        DrawCardBadge(item, availability, imported, width);

        ImGui.EndGroup();

        DrawCardTooltip(item, availability);
        DrawCardActions(item, availability, imported, width);

        ImGui.EndGroup();
    }

    private void DrawCardImage(SharedItem item, float width)
    {
        var path = string.IsNullOrEmpty(item.ImageFile)
            ? null
            : Path.Combine(_imageDir, item.ImageFile);

        if (path != null && File.Exists(path))
        {
            try
            {
                if (_textures.GetFromFile(path).GetWrapOrDefault() is { } wrap)
                {
                    ImageDraw.Square(wrap, width);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"[Wardrobe] Share picture '{path}' would not load: {ex.Message}");
            }
        }

        // Placeholder, so cards without a picture still line up in the grid
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, width));
        ImGui.GetWindowDrawList().AddRectFilled(
            start, new Vector2(start.X + width, start.Y + width),
            ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.20f, 1f)), UiScale.S(4f));
    }

    private void DrawCardBadge(SharedItem item, ItemAvailability availability, bool imported, float width)
    {
        if (imported)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), "In your wardrobe");
            return;
        }

        if (availability.PrimaryMissing)
        {
            var missing = availability.Missing.Count > 0
                ? availability.Missing[0].ModName
                : "a mod";

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(new Vector4(0.9f, 0.6f, 0.4f, 1f), $"Needs {Shorten(missing)}");
            ImGui.PopTextWrapPos();
            return;
        }

        // Wearable, but not quite as its sender has it — an upscale or a patch is absent
        if (availability.Missing.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.85f, 0.8f, 0.4f, 1f),
                $"Missing {availability.Missing.Count} extra");
        }
    }

    private void DrawCardTooltip(SharedItem item, ItemAvailability availability)
    {
        // AllowWhenDisabled on its own: an unavailable card is drawn disabled, and that is precisely
        // the card whose tooltip matters most, since it is the one that has to say what is missing
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(UiScale.S(360f));

        ImGui.TextUnformatted(item.Name);
        ImGui.TextDisabled(item.Slot.DisplayName());

        if (item.Tags.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(string.Join(", ", item.Tags));
        }

        if (item.Mods.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            foreach (var mod in item.Mods)
            {
                var have = availability.Present.Contains(mod);
                ImGui.TextColored(
                    have ? new Vector4(0.5f, 0.8f, 0.5f, 1f) : new Vector4(0.9f, 0.6f, 0.4f, 1f),
                    have ? $"have  {mod.ModName}" : $"need  {mod.ModName}");
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted(item.Notes);
        }

        if (availability.PrimaryMissing && NoteText.HasLink(item.Notes))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Links to where this came from are under the card.");
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void DrawCardActions(SharedItem item, ItemAvailability availability, bool imported, float width)
    {
        // Gated on CanWear rather than on PrimaryMissing, which is not the same question. An item
        // carrying no mods and no game item either — a stub somebody had not finished — has nothing
        // missing and still nothing to add, and offering the button would fail on the press.
        if (!availability.CanWear)
        {
            // The one thing that can actually be done about a missing mod: go and find it. Shown
            // only when the sender wrote a link into the notes, since nothing else in a bundle says
            // where a mod came from.
            NoteText.DrawLinks(item.Notes);
            return;
        }

        if (imported)
        {
            if (ImGui.Button("Wear##recvWear", new Vector2(width, 0)))
            {
                var local = _config.WardrobeItems.Find(i => i.SharedFromId == item.SourceId);
                if (local != null) _wardrobe.WearItemLinked(local);
            }
            return;
        }

        if (ImGui.Button("Add##recvAdd", new Vector2(width, 0)))
        {
            if (AddToWardrobe(item, wear: false))
                _loadStatus = $"Added '{item.Name}' to your wardrobe.";
        }

        if (ImGui.Button("Add and Wear##recvAddWear", new Vector2(width, 0)))
        {
            if (AddToWardrobe(item, wear: true))
                _loadStatus = $"Added '{item.Name}' and put it on.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds the item to your wardrobe, then wears it.\n\n" +
                             "It has to be added first — an item that is not in your\n" +
                             "wardrobe has nothing to take it off again afterwards.");
    }

    /// <summary>
    /// Brings one shared item into the wardrobe, optionally wearing it, and returns whether it
    /// landed.
    /// </summary>
    private bool AddToWardrobe(SharedItem shared, bool wear)
    {
        if (_loaded == null) return false;

        var item = _share.ToLocalItem(shared, ChosenCollection(), _imageDir);

        if (!Register(shared, item)) return false;

        _config.Save();

        if (wear) _wardrobe.WearItemLinked(item);

        return true;
    }

    /// <summary>
    /// Files an already-converted item into the wardrobe, returning whether it was worth keeping.
    /// </summary>
    /// <remarks>
    /// Deliberately does not save. The single-item path saves straight after; the batch path saves
    /// once at the end, rather than writing the whole config to disk three hundred times.
    /// </remarks>
    private bool Register(SharedItem shared, WardrobeItem item)
    {
        if (_loaded == null) return false;

        // Every mod it named was one this install does not have, and it has no game item to fall
        // back on — there is nothing left of the item to add
        if (item.Mods.Count == 0 && !item.GlamourerItemId.HasValue)
        {
            _loadStatus = $"'{shared.Name}' needs a mod you do not have, so there was nothing to add.";
            return false;
        }

        if (_tagWithSender && !string.IsNullOrWhiteSpace(_loaded.ExportedBy))
        {
            var tag = _loaded.ExportedBy.Trim();
            if (!item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                item.Tags.Add(tag);
        }

        _config.WardrobeItems.Add(item);
        _alreadyImported.Add(shared.SourceId);

        RelinkImported(shared, item);

        return true;
    }

    /// <summary>The collection imported mods are filed under — the picked one, or the configured default.</summary>
    private string ChosenCollection() =>
        _collections.Count > 0
            ? _collections[Math.Min(_collectionIdx, _collections.Count - 1)]
            : _config.DefaultCollection;

    /// <summary>
    /// Rebuilds this item's links to others from the same bundle that have already been added.
    /// </summary>
    /// <remarks>
    /// Links are mutual, so both sides are written — the same rule the editor follows. Only items
    /// already imported can be linked to: one that has not been added yet has no local id to point
    /// at, and it will make the link itself from its own side when it arrives, which is why this
    /// does not need to run again afterwards.
    /// </remarks>
    private void RelinkImported(SharedItem shared, WardrobeItem item)
    {
        foreach (var sourceId in shared.LinkedSourceIds)
        {
            var partner = _config.WardrobeItems.Find(i => i.SharedFromId == sourceId);
            if (partner == null) continue;

            if (!item.LinkedItemIds.Contains(partner.Id)) item.LinkedItemIds.Add(partner.Id);
            if (!partner.LinkedItemIds.Contains(item.Id)) partner.LinkedItemIds.Add(item.Id);
        }
    }

    private static string Shorten(string name) =>
        name.Length <= 28 ? name : name[..27] + "…";

    private void BeginOpen()
    {
        var start = Directory.Exists(_config.ImagesFolder)
            ? _config.ImagesFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _fileDialog.OpenFileDialog("Open Share File", ShareFilter, (ok, paths) =>
        {
            if (!ok || paths.Count == 0) return;
            LoadShare(paths[0]);
        }, 1, start);
    }

    private void LoadShare(string path)
    {
        var result = _share.Read(path);

        if (!result.Success || result.Share == null)
        {
            _loaded     = null;
            _loadedPath = string.Empty;
            _loadStatus = result.Message;
            return;
        }

        _loaded       = result.Share;
        _loadedPath   = result.Path;
        _imageDir     = result.ImageDir;
        _loadStatus   = string.Empty;
        _recvSearch   = string.Empty;
        _availability = _share.ResolveAvailability(result.Share);

        _collections = _penumbra.GetCollections();
        _collectionIdx = 0;
        if (!string.IsNullOrEmpty(_config.DefaultCollection))
        {
            var idx = _collections.ToList().FindIndex(
                c => c.Equals(_config.DefaultCollection, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _collectionIdx = idx;
        }

        _alreadyImported.Clear();
        foreach (var item in _config.WardrobeItems)
            if (item.SharedFromId is { } id) _alreadyImported.Add(id);
    }
}
