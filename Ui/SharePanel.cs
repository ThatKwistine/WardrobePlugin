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

    /// <summary>Outfits ticked for export. Their pieces come along whether or not they are ticked.</summary>
    private readonly HashSet<Guid> _pickedOutfits = new();

    /// <summary>Whether the send list is showing outfits rather than items.</summary>
    private bool _sendOutfits;

    // ── Receiving ─────────────────────────────────────────────────────────────

    private WardrobeShare? _loaded;
    private string _loadedPath  = string.Empty;
    private string _imageDir    = string.Empty;
    private string _loadStatus  = string.Empty;

    /// <summary>A web page of the open bundle is being written, so the button says so.</summary>
    private volatile bool _pageBusy;
    private string _recvSearch  = string.Empty;
    private bool   _onlyWearable;
    private bool   _tagWithSender = true;

    /// <summary>Whether the browsed bundle is showing its outfits rather than its items.</summary>
    private bool _recvOutfits;

    /// <summary>Availability per <see cref="SharedItem.SourceId"/>, worked out once per load.</summary>
    private Dictionary<Guid, ItemAvailability> _availability = new();

    /// <summary>Availability per <see cref="SharedOutfit.SourceId"/>, worked out from the above.</summary>
    private Dictionary<Guid, OutfitAvailability> _outfitAvailability = new();

    /// <summary>Collections to file imported mods under, and which one is chosen.</summary>
    private IList<string> _collections   = Array.Empty<string>();
    private int           _collectionIdx;

    /// <summary>
    /// <see cref="SharedItem.SourceId"/>s already sitting in the wardrobe, refreshed on load and
    /// after every import so a card can say it has already been taken.
    /// </summary>
    private readonly HashSet<Guid> _alreadyImported = new();

    /// <inheritdoc cref="_alreadyImported"/>
    private readonly HashSet<Guid> _alreadyImportedOutfits = new();

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
            _sending     = true;
            _sendOutfits = false;
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
        ImGui.TextWrapped("A share file describes your items and outfits — which mod each piece " +
                          "needs, which options, which game item, which dyes — and carries your " +
                          "pictures. It does not contain the mods themselves, so whoever opens it " +
                          "needs to already own them, or to go and get them.");

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

        var none = _picked.Count == 0 && _pickedOutfits.Count == 0;
        if (none) ImGui.BeginDisabled();
        if (ImGui.Button("  Export…  ")) BeginExport();
        if (none) ImGui.EndDisabled();
        if (none && ImGui.IsItemHovered())
            ImGui.SetTooltip("Tick some items or outfits first.");

        if (!string.IsNullOrEmpty(_sendStatus))
        {
            ImGui.SameLine();
            ImGui.TextWrapped(_sendStatus);
        }
    }

    private void DrawSendPickerBar()
    {
        DrawListToggle($" Items ({_picked.Count}) ", false, ref _sendOutfits);
        ImGui.SameLine();
        DrawListToggle($" Outfits ({_pickedOutfits.Count}) ", true, ref _sendOutfits);

        // The pieces an outfit drags along, so the tick counts above are not the whole story and do
        // not have to pretend to be
        var pulled = PulledInCount();
        if (pulled > 0)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"+{pulled} for the outfits");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Items the ticked outfits need that you have not ticked yourself.\n\n" +
                                 "They travel too — an outfit is a list of pieces, and one sent\n" +
                                 "without them arrives empty.");
        }

        UiLayout.SameLineIfRoomForButton(" Tick All Shown ");
        if (ImGui.Button(" Tick All Shown "))
        {
            if (_sendOutfits) foreach (var o in VisibleOutfitsForSend()) _pickedOutfits.Add(o.Id);
            else              foreach (var i in VisibleForSend())        _picked.Add(i.Id);
            _sendStatus = string.Empty;
        }

        UiLayout.SameLineIfRoomForButton(" Clear ");
        if (ImGui.Button(" Clear "))
        {
            if (_sendOutfits) _pickedOutfits.Clear();
            else              _picked.Clear();
            _sendStatus = string.Empty;
        }

        ImGui.SetNextItemWidth(UiScale.S(200f));
        ImGui.InputTextWithHint("##shareSearch", "Search…", ref _sendSearch, 64);
    }

    /// <summary>A sub-list selector, drawn like the window's own mode buttons but smaller.</summary>
    private static void DrawListToggle(string label, bool showsOutfits, ref bool state)
    {
        var active = state == showsOutfits;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
        }

        if (ImGui.Button(label)) state = showsOutfits;

        if (active) ImGui.PopStyleColor(2);
    }

    /// <summary>
    /// How many items the ticked outfits would bring along that are not ticked in their own right.
    /// </summary>
    /// <remarks>
    /// Recomputed per frame from two small sets, which is cheap enough and cannot go stale — the
    /// alternative is a cached count that has to be invalidated by every tick, in both lists.
    /// </remarks>
    private int PulledInCount() =>
        PulledIn().Count(id => !_picked.Contains(id) && _config.WardrobeItems.Exists(i => i.Id == id));

    private IEnumerable<Outfit> VisibleOutfitsForSend()
    {
        if (string.IsNullOrWhiteSpace(_sendSearch)) return _config.Outfits;

        var needle = _sendSearch.Trim();
        return _config.Outfits.Where(o =>
            o.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || o.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
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

        if (_sendOutfits) DrawSendOutfitRows();
        else              DrawSendItemRows();

        UiLayout.PopWrap();
        ImGui.EndChild();
    }

    private void DrawSendItemRows()
    {
        // Pieces an outfit is dragging along are shown ticked and locked rather than left looking
        // untouched, so the list agrees with what the export will actually contain
        var pulled = PulledIn();

        foreach (var item in VisibleForSend())
        {
            var forced = pulled.Contains(item.Id);
            var ticked = forced || _picked.Contains(item.Id);

            ImGui.PushID(item.Id.ToString());

            if (forced) ImGui.BeginDisabled();
            if (ImGui.Checkbox($"{item.Name}##pick", ref ticked))
            {
                if (ticked) _picked.Add(item.Id);
                else        _picked.Remove(item.Id);
                _sendStatus = string.Empty;
            }
            if (forced) ImGui.EndDisabled();

            if (forced && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("A ticked outfit needs this piece, so it is going either way.\n\n" +
                                 "Untick the outfit to leave it out.");

            var slot = item.Slot.DisplayName();
            UiLayout.SameLineIfRoomForText(slot);
            ImGui.TextDisabled(slot);

            ImGui.PopID();
        }
    }

    private void DrawSendOutfitRows()
    {
        if (_config.Outfits.Count == 0)
        {
            ImGui.TextDisabled("You have no outfits yet.");
            return;
        }

        foreach (var outfit in VisibleOutfitsForSend())
        {
            var ticked = _pickedOutfits.Contains(outfit.Id);
            ImGui.PushID(outfit.Id.ToString());

            if (ImGui.Checkbox($"{outfit.Name}##pickOutfit", ref ticked))
            {
                if (ticked) _pickedOutfits.Add(outfit.Id);
                else        _pickedOutfits.Remove(outfit.Id);
                _sendStatus = string.Empty;
            }

            var pieces = outfit.ItemIds.Count + outfit.VanillaItems.Count;
            var label  = $"{pieces} piece(s)";
            UiLayout.SameLineIfRoomForText(label);
            ImGui.TextDisabled(label);

            // A plate or a design card arrives as a plain outfit, and that is worth saying before it
            // is sent rather than after — see SharedOutfit
            if (outfit.IsGlamourPlate || outfit.IsDesign)
            {
                UiLayout.SameLineIfRoomForText("changes when shared");
                ImGui.TextColored(new Vector4(0.85f, 0.8f, 0.4f, 1f), "changes when shared");

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(outfit.IsGlamourPlate
                        ? "A glamour plate outfit is sent as an ordinary outfit holding the\n" +
                          "same pieces. It cannot stay attached to a plate — plate 3 on their\n" +
                          "side holds something else entirely.\n\n" +
                          "The gear itself travels perfectly, so this is usually fine."
                        : "A design card is sent as an ordinary outfit holding the items\n" +
                          "attached to it. The Glamourer design itself does not travel —\n" +
                          "its id means nothing in anybody else's Glamourer.\n\n" +
                          "If the look is mostly the design rather than the items, very\n" +
                          "little of it will arrive.");
            }

            ImGui.PopID();
        }
    }

    /// <summary>Ids of items the ticked outfits require, whether or not they are ticked themselves.</summary>
    private HashSet<Guid> PulledIn()
    {
        var pulled = new HashSet<Guid>();
        if (_pickedOutfits.Count == 0) return pulled;

        foreach (var outfit in _config.Outfits)
        {
            if (!_pickedOutfits.Contains(outfit.Id)) continue;
            foreach (var id in outfit.ItemIds) pulled.Add(id);
        }

        return pulled;
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

            var items   = _config.WardrobeItems.Where(i => _picked.Contains(i.Id)).ToList();
            var outfits = _config.Outfits.Where(o => _pickedOutfits.Contains(o.Id)).ToList();

            // The whole wardrobe goes in as the third argument, not as content but as the lookup the
            // export resolves outfit pieces through
            var result = _share.Export(items, outfits, _config.WardrobeItems,
                path, _author, _description, _withImages);

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

        if (_loaded != null && _config.HtmlExportEnabled)
        {
            UiLayout.SameLineIfRoomForButton("  View as a web page…  ");
            if (_pageBusy) ImGui.BeginDisabled();
            if (ImGui.Button(_pageBusy ? "  Writing…  " : "  View as a web page…  ")) BeginPageExport();
            if (_pageBusy) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Writes this wardrobe out as a page you can open in a browser —\n" +
                                 "the same page your own wardrobe exports as.\n\n" +
                                 "For looking through somebody's wardrobe properly, on a second\n" +
                                 "screen or away from the game, without adding any of it first.\n\n" +
                                 "Nothing is uploaded. It writes a file to a folder you pick.");
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

        if (_recvOutfits) DrawReceiveOutfitGrid();
        else              DrawReceiveGrid(_loaded);
    }

    private void DrawShareHeader(WardrobeShare share)
    {
        var who = string.IsNullOrWhiteSpace(share.ExportedBy) ? "Shared wardrobe" : share.ExportedBy;
        ImGui.TextUnformatted(who);

        var wearable = _availability.Count(a => a.Value.CanWear);
        var summary  = share.Outfits.Count > 0
            ? $"{share.Items.Count} item(s) and {share.Outfits.Count} outfit(s) — you can wear {wearable} of the items. "
            : $"{share.Items.Count} item(s) — you can wear {wearable} of them. ";

        ImGui.TextDisabled(summary + $"Shared {share.ExportedUtc.ToLocalTime():d}.");

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
        if (_loaded is { Outfits.Count: > 0 })
        {
            DrawListToggle($" Items ({_loaded.Items.Count}) ", false, ref _recvOutfits);
            ImGui.SameLine();
            DrawListToggle($" Outfits ({_loaded.Outfits.Count}) ", true, ref _recvOutfits);
            ImGui.Spacing();
        }

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

        // "Add All Shown" reads the item list, so it is offered only while that is what is showing.
        // On the outfit list the equivalent bulk action would be adding several outfits and every
        // piece behind them at once, which is a lot to do on one press and no easier to undo.
        if (_recvOutfits) return;

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

    // Every card is the same height, so a row of them lines its buttons up. Two lines for a name,
    // one for the badge under it — both are slots rather than however much the text happened to
    // need, because cards are laid out with SameLine and SameLine aligns tops, not bottoms.
    private const int NameLines  = 2;
    private const int BadgeLines = 1;

    /// <summary>Height of a slot that always holds this many lines, whatever is put in it.</summary>
    private static float LinesH(int lines) => ImGui.GetTextLineHeightWithSpacing() * lines;

    /// <summary>
    /// Draws wrapped text into a fixed slot, cut short with an ellipsis when it would need more
    /// lines than the slot has, and leaving the cursor below the slot either way.
    /// </summary>
    /// <remarks>
    /// What makes the cards a grid rather than a ragged row. A mod called
    /// <c>[Anno] Ruthless (Ring (R))</c> wraps where its neighbours do not, and without a slot to
    /// sit in it pushes that one card's buttons a line below everybody else's.
    /// <para>
    /// Nothing is lost to the ellipsis: the card's tooltip carries the full name, and it is the
    /// same hover that says what the item needs.
    /// </para>
    /// </remarks>
    private static void TextBlock(string text, float width, int lines, Vector4? colour = null)
    {
        var top     = ImGui.GetCursorPosY();
        var ceiling = ImGui.GetTextLineHeight() * lines + 1f;
        var shown   = text;

        if (Wrapped(text, width) > ceiling)
        {
            // Longest prefix that still fits the slot once the ellipsis is on it. Binary search,
            // because measuring wrapped text is not free and this runs for every card, every frame.
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Wrapped(text[..mid] + "…", width) <= ceiling) lo = mid;
                else                                              hi = mid - 1;
            }
            shown = lo > 0 ? text[..lo] + "…" : "…";
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        if (colour.HasValue) ImGui.TextColored(colour.Value, shown);
        else                 ImGui.TextUnformatted(shown);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorPosY(top + LinesH(lines));

        static float Wrapped(string text, float width) =>
            ImGui.CalcTextSize(text, false, width).Y;
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

        TextBlock(item.Name, width, NameLines);

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

    /// <remarks>
    /// Always takes <see cref="BadgeLines"/> lines, even with nothing to say. A card that reports
    /// nothing is the commonest card there is, and if it were shorter than its neighbours every
    /// row would be ragged wherever one of them had something to report.
    /// </remarks>
    private void DrawCardBadge(SharedItem item, ItemAvailability availability, bool imported, float width)
    {
        var top = ImGui.GetCursorPosY();

        if (imported)
        {
            TextBlock("In your wardrobe", width, BadgeLines, new Vector4(0.5f, 0.8f, 0.5f, 1f));
        }
        else if (availability.PrimaryMissing)
        {
            var missing = availability.Missing.Count > 0
                ? availability.Missing[0].ModName
                : "a mod";

            TextBlock($"Needs {Shorten(missing)}", width, BadgeLines,
                new Vector4(0.9f, 0.6f, 0.4f, 1f));
        }
        // Wearable, but not quite as its sender has it — an upscale or a patch is absent
        else if (availability.Missing.Count > 0)
        {
            TextBlock($"Missing {availability.Missing.Count} extra", width, BadgeLines,
                new Vector4(0.85f, 0.8f, 0.4f, 1f));
        }

        // Nothing to report, or the block above already moved past it — either way the slot is the
        // same height for every card
        ImGui.SetCursorPosY(top + LinesH(BadgeLines));
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

    // ── Received outfits ─────────────────────────────────────

    private IEnumerable<SharedOutfit> VisibleOutfitsForReceive()
    {
        if (_loaded == null) return Array.Empty<SharedOutfit>();

        IEnumerable<SharedOutfit> outfits = _loaded.Outfits;

        if (_onlyWearable) outfits = outfits.Where(o => OutfitAvailable(o).CanAdd);

        if (!string.IsNullOrWhiteSpace(_recvSearch))
        {
            var needle = _recvSearch.Trim();
            outfits = outfits.Where(o =>
                o.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || o.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        return outfits;
    }

    private OutfitAvailability OutfitAvailable(SharedOutfit outfit) =>
        _outfitAvailability.TryGetValue(outfit.SourceId, out var a)
            ? a
            : new OutfitAvailability(0, outfit.ItemSourceIds.Count, outfit.VanillaItems.Count);

    private void DrawReceiveOutfitGrid()
    {
        if (!ImGui.BeginChild("##recvOutfitGrid", new Vector2(0, 0), false)) { ImGui.EndChild(); return; }

        var width   = CardWidth;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (width + spacing)));

        var column = 0;
        foreach (var outfit in VisibleOutfitsForReceive())
        {
            if (column > 0) ImGui.SameLine();

            ImGui.PushID(outfit.SourceId.ToString());
            DrawSharedOutfitCard(outfit, width);
            ImGui.PopID();

            column = (column + 1) % columns;
        }

        ImGui.EndChild();
    }

    private void DrawSharedOutfitCard(SharedOutfit outfit, float width)
    {
        var availability = OutfitAvailable(outfit);
        var imported     = _alreadyImportedOutfits.Contains(outfit.SourceId);

        ImGui.BeginGroup();

        // Same two-group structure as an item card, and for the same reason — see DrawSharedCard
        ImGui.BeginGroup();

        if (!availability.CanAdd) ImGui.BeginDisabled();

        DrawOutfitCardImage(outfit, width);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextUnformatted(outfit.Name);
        ImGui.PopTextWrapPos();

        var pieces = availability.TotalPieces + availability.VanillaPieces;
        ImGui.TextDisabled($"{pieces} piece(s)");

        if (!availability.CanAdd) ImGui.EndDisabled();

        DrawOutfitCardBadge(availability, imported, width);

        ImGui.EndGroup();

        DrawOutfitCardTooltip(outfit, availability);
        DrawOutfitCardActions(outfit, availability, imported, width);

        ImGui.EndGroup();
    }

    private void DrawOutfitCardImage(SharedOutfit outfit, float width)
    {
        var path = string.IsNullOrEmpty(outfit.ImageFile)
            ? null
            : Path.Combine(_imageDir, outfit.ImageFile);

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
                _log.Debug($"[Wardrobe] Share outfit picture '{path}' would not load: {ex.Message}");
            }
        }

        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, width));
        ImGui.GetWindowDrawList().AddRectFilled(
            start, new Vector2(start.X + width, start.Y + width),
            ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.20f, 1f)), UiScale.S(4f));
    }

    private void DrawOutfitCardBadge(OutfitAvailability availability, bool imported, float width)
    {
        if (imported)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), "In your wardrobe");
            return;
        }

        if (!availability.CanAdd)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(new Vector4(0.9f, 0.6f, 0.4f, 1f), "No pieces you have");
            ImGui.PopTextWrapPos();
            return;
        }

        if (!availability.Complete)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(new Vector4(0.85f, 0.8f, 0.4f, 1f),
                $"{availability.AvailablePieces} of {availability.TotalPieces} pieces");
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawOutfitCardTooltip(SharedOutfit outfit, OutfitAvailability availability)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(UiScale.S(360f));

        ImGui.TextUnformatted(outfit.Name);

        if (outfit.Tags.Count > 0)
            ImGui.TextDisabled(string.Join(", ", outfit.Tags));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (availability.TotalPieces > 0)
            ImGui.TextUnformatted($"{availability.AvailablePieces} of {availability.TotalPieces} " +
                                  "modded piece(s) you have the mods for");

        if (availability.VanillaPieces > 0)
            ImGui.TextUnformatted($"{availability.VanillaPieces} plain game piece(s), which always work");

        if (!availability.Complete && availability.TotalPieces > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"{availability.MissingPieces} piece(s) would be left out. Look through " +
                              "the items list for what is missing — the pieces are all in this file, " +
                              "greyed where you do not have the mod.");
        }

        // Why it may not look like the outfit they were sent
        if (outfit.Origin != SharedOutfitOrigin.Normal)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped(outfit.Origin == SharedOutfitOrigin.GlamourPlate
                ? "This was a glamour plate on the sender's side. It arrives as an ordinary outfit "
                  + "holding the same gear — it cannot stay attached to a plate, since your plates "
                  + "hold your own glamours."
                : "This was a Glamourer design card on the sender's side. It arrives as an ordinary "
                  + "outfit holding the items that were attached to it; the design itself does not "
                  + "travel, so any gear or colouring that lived in the design is not here.");
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void DrawOutfitCardActions(SharedOutfit outfit, OutfitAvailability availability, bool imported, float width)
    {
        if (imported || !availability.CanAdd) return;

        if (ImGui.Button("Add##recvAddOutfit", new Vector2(width, 0)))
        {
            var added = AddOutfitToWardrobe(outfit);
            if (added >= 0)
                _loadStatus = added == 0
                    ? $"Added '{outfit.Name}'."
                    : $"Added '{outfit.Name}' and {added} item(s) it needed.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds the outfit, and any of its pieces you do not already have\n" +
                             "in your wardrobe.");
    }

    /// <summary>
    /// Brings an outfit in, adding whichever of its pieces are not in the wardrobe yet, and returns
    /// how many pieces that was — or -1 if nothing could be added at all.
    /// </summary>
    /// <remarks>
    /// The pieces have to land first: an outfit is a list of local item ids, so it cannot be written
    /// until those items exist and have ids to point at.
    /// </remarks>
    private int AddOutfitToWardrobe(SharedOutfit outfit)
    {
        if (_loaded == null) return -1;

        // Built by assignment rather than ToDictionary, which throws on a duplicate key. Nothing this
        // plugin writes has two items under one source id, but the file came from somebody else and a
        // malformed one must not take the window down.
        var bySource = new Dictionary<Guid, SharedItem>();
        foreach (var shared in _loaded.Items) bySource[shared.SourceId] = shared;

        var wanted = outfit.ItemSourceIds
            .Where(id => !_alreadyImported.Contains(id))
            .Select(id => bySource.TryGetValue(id, out var shared) ? shared : null)
            .Where(shared => shared != null && Available(shared).CanWear)
            .Select(shared => shared!)
            .ToList();

        var converted = _share.ToLocalItems(wanted, ChosenCollection(), _imageDir);

        var addedPieces = 0;
        for (var i = 0; i < wanted.Count; i++)
            if (Register(wanted[i], converted[i])) addedPieces++;

        // Built after the pieces are registered, so the ones just added are in it
        var sourceToLocal = new Dictionary<Guid, Guid>();
        foreach (var item in _config.WardrobeItems)
            if (item.SharedFromId is { } id) sourceToLocal[id] = item.Id;

        var local = _share.ToLocalOutfit(outfit, sourceToLocal, _imageDir);
        local.SharedFromId = outfit.SourceId;

        if (local.ItemIds.Count == 0 && local.VanillaItems.Count == 0)
        {
            _loadStatus = $"None of '{outfit.Name}' could be put together from what you have.";
            return -1;
        }

        if (_tagWithSender && !string.IsNullOrWhiteSpace(_loaded.ExportedBy))
        {
            var tag = _loaded.ExportedBy.Trim();
            if (!local.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                local.Tags.Add(tag);
        }

        _config.Outfits.Add(local);
        _alreadyImportedOutfits.Add(outfit.SourceId);
        _config.Save();

        return addedPieces;
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

    /// <summary>
    /// Asks where to put a web page of the bundle currently open, then writes it.
    /// </summary>
    /// <remarks>
    /// The page is exactly the one your own wardrobe exports as, because it is the same renderer over
    /// the same model — see <see cref="SharedWardrobePage"/>. The settings it follows are the export's
    /// own, since they are answers to questions this asks too: how big the pictures should be, whether
    /// it is a folder or one file, whether the mods are listed.
    /// <para>
    /// Written on a background thread like the wardrobe's own export, and for the same reason: it
    /// re-encodes every picture in the bundle, which is not something to do between two frames.
    /// </para>
    /// </remarks>
    private void BeginPageExport()
    {
        if (_loaded is not { } share) return;

        var start = Directory.Exists(_config.HtmlExportFolder)
            ? _config.HtmlExportFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _fileDialog.OpenFolderDialog("Where should the page go?", (ok, folder) =>
        {
            if (!ok || string.IsNullOrWhiteSpace(folder)) return;

            var stains = new Dictionary<byte, string>();
            foreach (var (id, name, _) in Plugin.ItemLookup.GetStains()) stains[id] = name;

            var model = SharedWardrobePage.Build(share, _imageDir, stains, _config.HtmlExportIncludeMods);

            var options = new PageWriteOptions
            {
                Folder    = folder,
                Layout    = _config.HtmlExportLayout,
                ImageSize = _config.HtmlExportImageSize,

                // Named after whoever sent it, so two friends' wardrobes do not land in the same
                // folder under the same name, and neither is mistaken for your own
                Stem = string.IsNullOrWhiteSpace(share.ExportedBy)
                    ? "Shared wardrobe"
                    : $"{share.ExportedBy.Trim()} wardrobe",

                PictureFailed = (p, why) =>
                    _log.Warning($"[Wardrobe] Share page: skipped {p} — {why}"),
            };

            _pageBusy   = true;
            _loadStatus = "Writing the page...";

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var written = WardrobePageWriter.Write(model, options);
                    _loadStatus = $"Wrote a page of {model.Items.Count} item(s) and " +
                                  $"{model.Outfits.Count} outfit(s), {written.Size} — {written.Path}";
                    _log.Information($"[Wardrobe] Share page written to {written.Path}.");
                }
                catch (Exception ex)
                {
                    _loadStatus = $"Could not write the page: {ex.Message}";
                    _log.Error(ex, "[Wardrobe] Share page failed");
                }
                finally
                {
                    _pageBusy = false;
                }
            });
        }, start);
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

        _outfitAvailability = _share.ResolveOutfitAvailability(result.Share, _availability);
        _recvOutfits = false;

        _alreadyImported.Clear();
        foreach (var item in _config.WardrobeItems)
            if (item.SharedFromId is { } id) _alreadyImported.Add(id);

        _alreadyImportedOutfits.Clear();
        foreach (var outfit in _config.Outfits)
            if (outfit.SharedFromId is { } id) _alreadyImportedOutfits.Add(id);
    }
}
