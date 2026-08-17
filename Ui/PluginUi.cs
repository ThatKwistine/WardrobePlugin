using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using WardrobePlugin;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

public class PluginUi : Window, IDisposable
{
    private readonly Configuration           _config;
    private readonly WardrobeService         _wardrobe;
    private readonly ITextureProvider        _textures;
    private readonly IPluginLog              _log;
    private readonly ItemImportPanel         _panel;
    private readonly ScreenshotSessionService _session;
    private readonly BackupService            _backup;
    private readonly MassImportPanel          _massImport;

    // Holds ISharedImmediateTexture references so Dalamud won't free the GPU resource while
    // we still have the handle. GetWrapOrDefault() is called each frame to get a live handle.
    // Keyed by item, holding the path it was resolved from so a changed path invalidates itself.
    // Texture is null when the file was missing — cached deliberately, so a broken path does not
    // re-hit the filesystem on every frame.
    private readonly Dictionary<Guid, (string Path, ISharedImmediateTexture? Texture)> _imageCache = new();

    // Same arrangement for outfit previews
    private readonly Dictionary<Guid, (string Path, ISharedImmediateTexture? Texture)> _outfitImageCache = new();
    private readonly FileDialogManager _fileDialog = new();

    // Slot filter: null = show all
    private EquipSlot? _slotFilter;

    // Tag filter: empty = show all
    private readonly HashSet<string> _tagFilter = new();

    /// <summary>
    /// Style filter — full tag paths, empty meaning show all.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="_tagFilter"/> rather than folded into it because the two have to
    /// combine differently: several tags, or several styles, widen what is shown, but a tag and a
    /// style together narrow it. Sharing one set would answer "casual boots" with everything that is
    /// either, which is not the question anyone is asking of the row.
    /// </remarks>
    private readonly HashSet<string> _styleFilter = new(StringComparer.OrdinalIgnoreCase);

    // Make-a-tag box in the Tags panel, and what the last attempt did
    private string _newTag       = string.Empty;
    private string _newTagStatus = string.Empty;

    // Make-a-style box in the Tags panel, and what the last attempt did
    private string _newStyle       = string.Empty;
    private string _newStyleStatus = string.Empty;

    // Name box for saving a camera preset, and the preset currently being renamed. The slot is part
    // of the rename state so that switching to an item in another slot mid-rename does not leave the
    // box open over an unrelated slot's list.
    private string _newPresetName   = string.Empty;
    private string _renamePresetSlot = string.Empty;
    private int    _renamePresetIdx  = -1;
    private string _renamePresetBuf  = string.Empty;

    /// <summary>
    /// The preset last snapped to in the compact session view, and the slot it belongs to.
    /// </summary>
    /// <remarks>
    /// Update there corrects this rather than the one the session loads. Those are the same until
    /// you click another preset, and after that the one you are looking at is the one you mean —
    /// clicking Preset 3, framing it and pressing Update has to change Preset 3.
    /// <para>
    /// Held by reference rather than index so it cannot come to mean a different preset, and dropped
    /// when the session moves to another slot, where it would mean nothing at all.
    /// </para>
    /// </remarks>
    private CameraPreset? _compactPreset;
    private string        _compactPresetSlot = string.Empty;

    // Per original item: how many of its variants the grid is showing, and how many it folded away.
    // Rebuilt every frame by FoldVariants from what the filters left, so it always describes what is
    // actually on screen rather than the whole wardrobe.
    private readonly Dictionary<Guid, (int Total, int Hidden)> _variantCounts = new();

    // Free-text search: empty = show all. Deliberately not persisted.
    private string _search = string.Empty;

    // Favourites-only filter
    private bool _favoritesOnly;

    // Worn-only filter: items the wardrobe has on, plus anything the last scan found enabled
    private bool _wornOnly;

    // Variant-group filter: show only pieces that exist in more than one version
    private bool _variantsOnly;

    // Items the grid drew last frame, for the toolbar count. The toolbar draws before the grid,
    // so this trails by one frame — imperceptible, and avoids running the filters twice.
    private int _visibleCount;

    // ── Bulk selection ────────────────────────────────────────────────────────

    /// <summary>
    /// While on, cards show a tick box in place of their Wear / Edit / X row and the whole card
    /// toggles selection. A mode rather than an always-present tick box: the row is swapped rather
    /// than added, so entering it does not resize a single card or reflow the grid.
    /// </summary>
    private bool _selectMode;

    /// <summary>
    /// Selected item ids. Cleared on leaving select mode, so no selection is ever carried around
    /// invisibly and acted on later from a grid showing something else entirely.
    /// </summary>
    private readonly HashSet<Guid> _selected = new();

    // Ids the grid drew last frame, so Select All acts on exactly what the filters are showing.
    // Trails a frame like _visibleCount, and for the same reason.
    private readonly List<Guid> _lastVisibleIds = new();

    // Bulk tag entry
    private string _bulkTag    = string.Empty;
    private string _bulkStatus = string.Empty;

    /// <summary>
    /// The right panel is showing the bulk actions rather than whatever was there before.
    /// </summary>
    /// <remarks>
    /// Actions live behind a click instead of on screen throughout, because the panel is also where
    /// the tag filter lives — and "filter to everything tagged X, then retag it" is the main reason
    /// to bulk-edit tags at all. Both need the full width, and they are needed at different moments:
    /// the filter while choosing, the actions only when applying.
    /// </remarks>
    private bool _bulkPanelOpen;

    // Penumbra collections for the bulk move, read once on entering select mode rather than every
    // frame: it is an IPC round trip, and the list does not change while the bar is on screen.
    private IList<string> _bulkCollections = Array.Empty<string>();
    private int           _bulkCollectionIdx;

    /// <summary>
    /// Confirmation for the bulk delete. A modal rather than an inline second click: deleting
    /// dozens of items cannot be undone, and it is worth stating what is about to happen — and what
    /// is not — somewhere the user has to read before agreeing.
    /// </summary>
    private const string BulkDeletePopup = "Delete selected items?###bulkDelete";

    /// <summary>Confirmation for removing an installed icon pack, which deletes its folder.</summary>
    private const string IconPackRemovePopup = "Remove icon pack?###iconPackRemove";

    /// <summary>Pack the x was clicked on, waiting for the confirmation to open. Null when idle.</summary>
    private string? _iconPackPendingRemoval;

    /// <summary>Result of the last import or removal, shown under the dropdown until the next one.</summary>
    private string _iconPackStatus = string.Empty;

    /// <summary>
    /// The item the last Capture was pressed on, and what it found.
    /// </summary>
    /// <remarks>
    /// Carries the item so the result cannot appear under a different row: capturing nothing on the
    /// hat must not read as a verdict on the boots. Null until one has been pressed.
    /// </remarks>
    private (Guid Item, int Rows)? _advancedDyeCaptured;

    /// <summary>What the last vanilla capture found, so pressing the button always shows a result.</summary>
    private string _vanillaStatus = string.Empty;

    /// <summary>Tag being typed into the outfit editor.</summary>
    private string _outfitTagInput = string.Empty;

    /// <summary>Parsed swatch colours per item, so the rows are not re-read every frame.</summary>
    private readonly Dictionary<Guid, (int Fingerprint, List<uint> Colours)> _advancedDyeSwatches = new();

    // Set when the user expands out of compact mode mid-session; cleared when the session ends,
    // so it overrides the setting for this session only rather than turning it off permanently.
    private bool _compactOverride;
    private bool _wasCompact;

    // The size the window had the last time it drew expanded, so leaving compact mode puts it back
    // where the user left it rather than at the default.
    private Vector2? _lastExpandedSize;

    // Right panel mode
    private bool _showImageBrowser;
    /// <summary>
    /// The changelog window, so settings can reopen it. Set by <see cref="Plugin"/> after
    /// construction — the window needs no part of the wardrobe, and threading it through the
    /// constructor would add a parameter to a list that is already long.
    /// </summary>
    public ChangelogWindow? Changelog { get; set; }

    private bool _showSettings;
    private bool _showTags;

    /// <summary>
    /// The all-slots camera preset panel, opened from Settings → Screenshots.
    /// </summary>
    /// <remarks>
    /// Its own panel rather than a section inside settings: the point of it is to be open in GPose while
    /// the camera is being moved around, and everything else in settings is a distraction at that moment.
    /// </remarks>
    private bool _showCameraPresets;

    /// <summary>Which slot's preset list is expanded in that panel, or null for none.</summary>
    /// <remarks>
    /// One at a time. Eighteen slots' worth of preset rows open at once is a panel nobody can find
    /// anything in, and the list being edited is always the one just clicked.
    /// </remarks>
    private string? _presetSlotOpen;
    // Grid shows saved outfits instead of items
    private bool _outfitsView;

    // Name field for saving the current look as an outfit
    private string _newOutfitName = string.Empty;

    // Outfit currently open in the edit panel, with its staged fields
    private Outfit? _editingOutfit;
    private string  _editOutfitName  = string.Empty;
    private string  _editOutfitImage = string.Empty;
    private string  _addToOutfitSearch = string.Empty;
    private string  _dyeSearch         = string.Empty;

    // Glamour plates: the message under the sync controls, whether the drift notice has been waved
    // away for now, and which plate outfits the last draw found out of step. The set is recomputed
    // each frame in the outfits grid so the cards and the notice always agree.
    private string _plateSyncStatus = string.Empty;
    private string _plateApplyStatus = string.Empty;
    private bool   _plateNoticeIgnored;
    private readonly HashSet<Guid> _platesOutOfSync = new();

    // Glamourer designs: the message under the stranded-cards notice, and which design cards the last
    // draw found no design for. Far less state than the plates need, because a linked design has
    // nothing to keep in step — no sync, no drift, and so nothing to report but a deletion.
    private string _designStatus = string.Empty;
    private readonly HashSet<Guid> _designsMissing = new();

    // Base character editor: the name being typed, whose name it is, and the add-item search. The id
    // is kept so switching base characters reloads the field instead of renaming the new one to the
    // name half-typed for the old.
    private string _baseNameEdit    = string.Empty;
    private Guid?  _baseNameEditId;
    private string _addToBaseSearch = string.Empty;

    // Mod-scan results: item IDs whose mods are detected enabled in Penumbra
    private readonly HashSet<Guid> _detectedWorn = new();
    private string _scanStatus = string.Empty;

    // Items whose Penumbra mods are enabled but which Glamourer is not showing. Populated by Scan
    // and by the automatic check below; cleared as each one is dealt with.
    private List<WardrobeItem> _desynced = new();

    // The desync check runs once per session on first open, so a crash that left mods enabled is
    // noticed without the user having to know to press Scan. Not persisted — WornItems is cleared
    // on load anyway, so every session starts needing the check.
    private bool _desyncChecked;

    // Settings panel feedback
    private string _cameraLoadStatus  = string.Empty;

    // Collections for the default-collection picker; loaded lazily so we don't hit IPC every frame
    private IList<string>? _settingsCollections;

    // Glamourer designs for the revert-design picker, loaded lazily for the same reason
    private IList<(Guid Id, string Name)>? _settingsDesigns;

    // First-run setup
    private int _onboardStep;
    private const int OnboardLastStep = 5;

    // Dependency probes, cached so the welcome step is not an IPC call every frame
    private (bool Available, string Version)? _penumbraCheck;
    private (bool Available, string Version)? _glamourerCheck;

    // Image browser state
    private string[] _browserImages      = Array.Empty<string>();
    private string   _lastBrowserFolder  = string.Empty;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp" };

    // Design pixels, at Dalamud's default text size. Everything below reads them through UiScale,
    // so a larger text setting grows the cards with the text rather than clipping it.
    private const float BaseCardWidth  = 180f;
    private const float BaseCardPad    = 10f;
    private const float BaseCardHeight = 280f;

    /// <summary>Bounds on the card size multiplier, so a stored value cannot make a grid unusable.</summary>
    /// <remarks>
    /// The lower bound is where a card still has room for its name and buttons; the upper is about
    /// three cards across a default-width window, past which the grid stops being a grid.
    /// </remarks>
    public const float MinCardScale = 0.7f;
    public const float MaxCardScale = 2.5f;

    /// <summary>
    /// The user's card size multiplier, applied on top of the layout scale.
    /// </summary>
    /// <remarks>
    /// Clamped on read rather than on write, as the slot icon scales are, so a value hand-edited
    /// into the config file cannot produce a grid with no way back to the slider that fixes it.
    /// </remarks>
    private float CardScale => Math.Clamp(_config.CardScale, MinCardScale, MaxCardScale);

    /// <summary>The outfit grid's own multiplier, on the same bounds as the item grid's.</summary>
    private float OutfitScale => Math.Clamp(_config.OutfitCardScale, MinCardScale, MaxCardScale);

    private float CardWidth => UiScale.S(BaseCardWidth) * CardScale;
    private float CardPad   => UiScale.S(BaseCardPad);
    private float ThumbSize => CardWidth - CardPad * 2; // square thumbnail

    // The outfit grid starts from the same card and scales it separately — see OutfitCardScale
    private float OutfitCardWidth  => UiScale.S(BaseCardWidth)  * OutfitScale;

    /// <summary>
    /// Outfit card height, taller when previews are portraits.
    /// </summary>
    /// <remarks>
    /// A 9:16 picture is nearly twice as tall as the square one it replaces, so the card has to grow
    /// by the difference or the buttons fall off the bottom — a card clips rather than scrolls. The
    /// rest of the card is unchanged, so the extra height is all picture.
    /// </remarks>
    private float OutfitCardHeight
    {
        get
        {
            var square = UiScale.S(BaseCardHeight) * OutfitScale;
            if (!_config.PortraitOutfitPreviews) return square;

            var thumb = OutfitCardWidth - CardPad * 2;
            return square + ImageDraw.PortraitHeight(thumb) - thumb;
        }
    }

    /// <summary>
    /// Card height, grown to fit a slot icon larger than the size the card was designed around.
    /// </summary>
    /// <remarks>
    /// The icon sits on the badge row, so scaling it up pushes the button row towards the bottom
    /// edge — and past it, since a card clips rather than scrolls. Everything that lays the grid out
    /// reads this, so the rows grow with it and nothing overlaps.
    /// </remarks>
    private float CardHeight =>
        UiScale.S(BaseCardHeight) * CardScale + (Plugin.SlotIcons.Enabled
            ? Math.Max(0f, Plugin.SlotIcons.ScaledSize - SlotIconService.BaseScaledSize)
            : 0f);


    // Window.Size and Window.SizeConstraints are scaled by Dalamud itself — see IWindow's docs — so
    // these stay in unscaled units. Putting them through UiScale would scale them twice.
    private static readonly Vector2 DefaultSize = new(1000, 700);
    // Tall enough for the camera preset block under the progress lines. The window has no scrollbar,
    // so anything that does not fit is simply not reachable.
    private static readonly Vector2 CompactSize = new(360, 275);
    private static readonly Vector2 Unbounded   = new(float.MaxValue, float.MaxValue);

    public PluginUi(Configuration config, WardrobeService wardrobe,
        ITextureProvider textures, IPluginLog log, ItemImportPanel panel,
        ScreenshotSessionService session, BackupService backup, MassImportPanel massImport)
        : base("Wardrobe###WardrobeMain",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _config   = config;
        _wardrobe = wardrobe;
        _textures = textures;
        _log      = log;
        _panel    = panel;
        _session  = session;
        _backup   = backup;
        _massImport = massImport;

        // The edit panel has the item and the picture, but the popup that shows it full size lives
        // here, over the whole window rather than inside a panel that is 360px wide
        _panel.QuickViewRequested = item => _quickViewItem = item.Id;

        // The cache keys on the item's cover path, so it would catch up by itself next frame — but the
        // gallery can change the cover, and a card showing the old one while the panel shows the new
        // one is exactly the sort of disagreement that reads as a bug
        _panel.ImagesChanged = item => _imageCache.Remove(item.Id);


        // Size and constraints are set in PreDraw rather than here, because both scale with
        // Dalamud's Global Font Scale and that can be changed while the plugin is running. Read once
        // in the constructor, the window would keep whatever bounds were right at load.
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>True while the main window should render as a compact session view.</summary>
    private bool CompactActive =>
        _config.CompactDuringSession && !_compactOverride && _session.State != SessionState.Idle;

    public override void PreDraw()
    {
        FontScope.Push(ref _fontScope);

        // Forget any manual expand once the session is over
        if (_session.State == SessionState.Idle) _compactOverride = false;

        var compact = CompactActive;

        // Global Font Scale changed — carry the window's size with it, in both directions.
        //
        // Growing happens on its own whether we like it or not: MinimumSize is scaled, so at 300%
        // the window is forced out to at least 1440x1080. Shrinking does not, because a smaller
        // minimum only *permits* a smaller window, it does not resize one — so without this, scaling
        // up and back down left the window stranded at its enlarged size.
        //
        // No arithmetic on the size itself: _lastExpandedSize is kept in unscaled units, and Dalamud
        // multiplies Size by the current scale on the way out. Scaling it here as well is what blew
        // the window up to tens of thousands of pixels, compounding on every change.
        var factor  = UiScale.Factor;
        var rescale = _lastScaleFactor > 0f && MathF.Abs(factor - _lastScaleFactor) > 0.001f;
        _lastScaleFactor = factor;

        if (compact != _wasCompact || rescale)
        {
            // Resize only on a transition or a scale change, so the window stays user-resizable
            // either side of both
            Size          = compact ? CompactSize : _lastExpandedSize ?? DefaultSize;
            SizeCondition = ImGuiCond.Always;
            _wasCompact   = compact;
        }
        else
        {
            // FirstUseEver, so this only lands on the very first frame and never fights a resize
            Size          = _lastExpandedSize ?? DefaultSize;
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        SizeConstraints = compact
            ? new WindowSizeConstraints
            {
                MinimumSize = new Vector2(300, 140),
                MaximumSize = new Vector2(700, 500),
            }
            : new WindowSizeConstraints
            {
                MinimumSize = new Vector2(480, 360),
                MaximumSize = Unbounded,
            };
    }

    public override void PostDraw() => FontScope.Pop(ref _fontScope);

    /// <summary>Held between <see cref="PreDraw"/> and <see cref="PostDraw"/>. See <see cref="FontScope"/>.</summary>
    private IDisposable? _fontScope;

    /// <summary>Last <see cref="UiScale.Factor"/> seen, to spot the setting being changed.</summary>
    private float _lastScaleFactor;

    public override void Draw()
    {
        // Remember the expanded size every frame it is not compact. On the frame we go compact this
        // is already skipped, so what is kept is the size from before the switch — the user's own.
        //
        // Divided back out of the current scale, because GetWindowSize reports real pixels while
        // Size is in the unscaled units Dalamud multiplies up. Storing pixels and handing them back
        // as Size is a feedback loop that multiplies the window by the scale on every frame it is
        // applied.
        if (!CompactActive) _lastExpandedSize = ImGui.GetWindowSize() / UiScale.Factor;

        if (!_config.OnboardingCompleted)
        {
            DrawOnboarding();
            _fileDialog.Draw();
            return;
        }

        if (CompactActive)
        {
            DrawCompactSession();
            _fileDialog.Draw();
            return;
        }

        // Once per session, on the first proper draw: a crash leaves Penumbra mods enabled while
        // WornItems is cleared on load, and nothing else would ever surface that. Read-only —
        // it reports, and the user decides.
        if (!_desyncChecked)
        {
            _desyncChecked = true;
            _desynced      = new List<WardrobeItem>(_wardrobe.FindDesynced());
        }

        var totalW  = ImGui.GetContentRegionAvail().X;
        var totalH  = ImGui.GetContentRegionAvail().Y;
        var rightOpen = _panel.IsOpen || _showImageBrowser || _showSettings || _showTags
                        || _showCameraPresets
                        || _editingOutfit != null || (_selectMode && _bulkPanelOpen);
        var panelW    = rightOpen ? UiScale.S(360f) : 0f;
        var gridW     = totalW - panelW - (rightOpen ? 8f : 0f);


        // ── Left: sticky header + scrolling grid only ─────────────────────────
        ImGui.BeginChild("##leftOuter", new Vector2(gridW, totalH), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);


        UiLayout.PushWrap();
        DrawToolbar();
        DrawBulkBar();
        DrawModOwnershipNotice();
        DrawDesyncNotice();
        ImGui.Separator();
        DrawSlotFilter();
        ImGui.Separator();
        UiLayout.PopWrap();

        ImGui.BeginChild("##wardrobeGrid", new Vector2(-1, ImGui.GetContentRegionAvail().Y));
        UiLayout.PushWrap();
        if (_outfitsView) DrawOutfitsGrid();
        else              DrawGrid();
        UiLayout.PopWrap();
        ImGui.EndChild();

        ImGui.EndChild();

        // ── Right: tags + optional panel ──────────────────────────────────────
        if (rightOpen)
        {
            ImGui.SameLine();
            ImGui.BeginChild("##rightPanel", new Vector2(panelW, totalH), true,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);


            UiLayout.PushWrap();

            if (_panel.IsOpen)
                _panel.Draw();
            // Above the filter panels on purpose: it is opened deliberately from the toolbar and
            // steps back to whichever of them was showing.
            else if (_selectMode && _bulkPanelOpen)
                DrawSelectionPanel();
            else if (_editingOutfit != null)
                DrawOutfitEditPanel();
            else if (_showTags)
                DrawTagFilter();
            else if (_showImageBrowser)
                DrawImageBrowser();
            // Before settings, since that is where it is opened from: leaving settings above it would
            // have the panel close itself the moment it was asked for
            else if (_showCameraPresets)
                DrawCameraPresetsPanel();
            else if (_showSettings)
                DrawSettings();

            UiLayout.PopWrap();
            ImGui.EndChild();
        }

        // Must be drawn outside child windows so the dialog isn't clipped
        _fileDialog.Draw();

        // Same reason: the popup is opened from a button inside the grid's child window, and a modal
        // begun in there would be clipped to it
        DrawQuickView();

        DrawSessionHud();
    }

    // ── First-run setup ───────────────────────────────────────────────────────

    /// <summary>
    /// Walks a new user through the settings that cause the most trouble when left unset:
    /// the collection, the two folders, and backups.
    /// </summary>
    /// <remarks>
    /// Replaces the whole window rather than opening a second one, so it cannot be lost behind
    /// the main UI. Every step is skippable and everything here is editable later in Settings.
    /// </remarks>
    private void DrawOnboarding()
    {
        UiLayout.PushWrap();
        ImGui.TextUnformatted("Welcome to Wardrobe");
        ImGui.SameLine();
        ImGui.TextDisabled($"·  step {_onboardStep + 1} of {OnboardLastStep + 1}");
        ImGui.Separator();
        ImGui.Spacing();

        var bodyH = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - 8f;
        if (ImGui.BeginChild("##onboardBody", new Vector2(-1, bodyH)))
        {
            UiLayout.PushWrap();
            switch (_onboardStep)
            {
                case 0: DrawOnboardIntro();       break;
                case 1: DrawOnboardCollection();  break;
                case 2: DrawOnboardContents();    break;
                case 3: DrawOnboardImages();      break;
                case 4: DrawOnboardScreenshots(); break;
                case 5: DrawOnboardBackups();     break;
            }
            UiLayout.PopWrap();
        }
        ImGui.EndChild();

        ImGui.Separator();

        if (_onboardStep > 0)
        {
            if (ImGui.Button("Back", UiScale.S(90, 0))) _onboardStep--;
            ImGui.SameLine();
        }

        if (_onboardStep < OnboardLastStep)
        {
            if (ImGui.Button("Next", UiScale.S(90, 0))) _onboardStep++;
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.38f, 0.13f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.55f, 0.18f, 1f));
            if (ImGui.Button("Finish", UiScale.S(90, 0)))
            {
                _config.OnboardingCompleted = true;
                _config.Save();
            }
            ImGui.PopStyleColor(2);
        }

        // Right-aligned, so it is available but never the obvious button to press
        var skip = "Skip setup";
        var skipW = ImGui.CalcTextSize(skip).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine();
        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - skipW;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);
        if (ImGui.Button(skip))
        {
            _config.OnboardingCompleted = true;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("All of this can be set later in Settings.");

        UiLayout.PopWrap();
    }

    private void DrawOnboardIntro()
    {
        ImGui.TextWrapped("Wardrobe turns your Penumbra mods into a visual, per-slot wardrobe. " +
                          "Each item covers one equipment slot, so you can change one piece " +
                          "without disturbing the rest of your look.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Required plugins");
        ImGui.Spacing();

        // Probed once rather than per frame; Re-check re-runs it after installing something
        var penumbra  = _penumbraCheck  ??= Plugin.Penumbra.CheckAvailable();
        var glamourer = _glamourerCheck ??= Plugin.Glamourer.CheckAvailable();

        DrawDependencyRow("Penumbra",  penumbra,  "Supplies and toggles the mods themselves.");
        DrawDependencyRow("Glamourer", glamourer, "Equips the matching game item for each mod.");

        ImGui.Spacing();
        if (ImGui.SmallButton("Re-check"))
        {
            _penumbraCheck  = null;
            _glamourerCheck = null;
        }

        if (!penumbra.Available || !glamourer.Available)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                "Install the missing plugin, then press Re-check.");
            ImGui.TextDisabled("Setup can continue, but Wardrobe will not work without both.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("This short setup covers the settings that cause the most confusion when " +
                          "they are left unset. It takes about a minute, and nothing here is " +
                          "permanent — it can all be changed later in Settings.");

        // Re-running this is a normal thing to do, so say plainly that it cannot cost anything.
        // The wizard only ever writes those settings; it never touches items.
        if (_config.WardrobeItems.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f),
                $"Your {_config.WardrobeItems.Count} wardrobe item(s) are not affected.");
            ImGui.TextDisabled("Each step starts from your current setting, and only what you " +
                               "change is changed.");
        }
    }

    private static void DrawDependencyRow(string name, (bool Available, string Version) status, string why)
    {
        // Dot rather than a tick: U+2714 is in the same Dingbats block as U+2715, which the
        // default font does not carry — it renders as an empty box. The colour and the
        // "found"/"not found" text carry the meaning regardless.
        ImGui.PushStyleColor(ImGuiCol.Text, status.Available
            ? new Vector4(0.4f, 0.9f, 0.4f, 1f)
            : new Vector4(1f, 0.4f, 0.4f, 1f));
        ImGui.TextUnformatted("●");
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.SameLine();
        ImGui.TextDisabled(status.Available ? $"— found (API {status.Version})" : "— not found");
        ImGui.TextDisabled($"    {why}");
    }

    private void DrawOnboardCollection()
    {
        ImGui.TextUnformatted("Which Penumbra collection does your character use?");
        ImGui.Spacing();
        ImGui.TextWrapped("This is the most important setting. A mod only affects your character " +
                          "if it is enabled in the collection that character uses. Enable it " +
                          "anywhere else and Penumbra reports success, and nothing appears.");
        ImGui.Spacing();
        ImGui.TextDisabled("In Penumbra, see Collections → Your Character — and note that an " +
                           "Individual Assignment for that character overrules it.");
        ImGui.Spacing();

        _settingsCollections ??= Plugin.Penumbra.GetCollections();

        var names  = new[] { "(first available)" }.Concat(_settingsCollections).ToArray();
        var curIdx = 0;
        if (!string.IsNullOrEmpty(_config.DefaultCollection))
        {
            var found = Array.FindIndex(names,
                n => n.Equals(_config.DefaultCollection, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) curIdx = found;
        }

        ImGui.SetNextItemWidth(UiScale.S(300));
        if (ImGui.BeginCombo("##onboardColl", names[curIdx]))
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (ImGui.Selectable(names[i], i == curIdx))
                {
                    _config.DefaultCollection = i == 0 ? string.Empty : names[i];
                    _config.Save();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh##onboardColl"))
            _settingsCollections = Plugin.Penumbra.GetCollections();

        ImGui.Spacing();
        ImGui.TextDisabled("Imports will start with this collection selected.");
    }

    /// <summary>
    /// What the wardrobe holds, and what the import lists show.
    /// </summary>
    /// <remarks>
    /// All three are off by default and none of them announce themselves: a wardrobe of animation mods
    /// is invisible until mod categories are on, and the import lists stay full of mods you have
    /// already dealt with until you find the two hide options. Asked here because a first-time user
    /// has no reason to go looking for any of them.
    /// </remarks>
    private void DrawOnboardContents()
    {
        ImGui.TextUnformatted("What should the wardrobe hold?");
        ImGui.Spacing();
        ImGui.TextWrapped("Gear is always managed. Animations, VFX, and mounts and minions can be " +
                          "kept alongside it — they have no game item to equip, so wearing one only " +
                          "enables its Penumbra mod.");
        ImGui.Spacing();

        var categories = _config.ModCategoriesEnabled;
        if (ImGui.Checkbox("Manage animations, VFX and mounts too##onboard", ref categories))
        {
            _config.ModCategoriesEnabled = categories;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds Animation, VFX and Mount / Minion to the filter bar and to the\n" +
                             "slot pickers. Animation covers emotes, idles, poses and battle\n" +
                             "animations. Turning it off later hides those items but keeps them.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Importing");
        ImGui.Spacing();
        ImGui.TextWrapped("When you import, the mod lists show every mod in your collection. Once " +
                          "the wardrobe is a few hundred items in, most of that is mods you have " +
                          "already imported. These trim the lists down to what is left.");
        ImGui.Spacing();

        var hideImported = _config.HideImportedMods;
        if (ImGui.Checkbox("Hide mods I have already imported##onboard", ref hideImported))
        {
            _config.HideImportedMods = hideImported;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off, they are still listed but greyed out.");

        var hideSupport = _config.HideSupportMods;
        if (ImGui.Checkbox("Hide supplementary mods##onboard", ref hideSupport))
        {
            _config.HideSupportMods = hideSupport;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mods only ever attached to an item as a supplement — body upscales,\n" +
                             "compatibility patches — rather than imported in their own right.");

        ImGui.Spacing();
        ImGui.TextDisabled("Both can be changed later in Settings → Importing.");
    }

    private void DrawOnboardImages()
    {
        ImGui.TextUnformatted("Where should item images live?");
        ImGui.Spacing();
        ImGui.TextWrapped("Images in this folder appear in the Image Browser, ready to drag onto " +
                          "item cards as previews. It is also where a screenshot session writes " +
                          "its finished shots, cropped square and named after the item.");
        ImGui.Spacing();
        ImGui.TextDisabled("Items work fine without one, but a screenshot session needs it set.");
        ImGui.Spacing();

        if (string.IsNullOrEmpty(_config.ImagesFolder))
            ImGui.TextDisabled("No folder selected.");
        else
            ImGui.TextUnformatted(_config.ImagesFolder);

        ImGui.Spacing();
        if (ImGui.Button(" Browse…##onboardImg "))
        {
            var start = Directory.Exists(_config.ImagesFolder)
                ? _config.ImagesFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            _fileDialog.OpenFolderDialog("Select Images Folder", (ok, path) =>
            {
                if (!ok) return;
                _config.ImagesFolder = path;
                _config.Save();
                RefreshBrowserImages();
            }, start);
        }

        // Also a matter of how the grid reads, and the preview below is the quickest way to judge it
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Slot names or icons?");
        ImGui.Spacing();
        ImGui.TextWrapped("Cards and the filter bar can show a slot's icon instead of its name. " +
                          "Icons are narrower, so more slots fit on the bar before the rest move " +
                          "into its More dropdown.");
        ImGui.Spacing();

        var icons = _config.SlotIconsEnabled;
        if (ImGui.Checkbox("Use icons for slots##onboard", ref icons))
        {
            _config.SlotIconsEnabled = icons;
            _config.Save();
        }

        if (!_config.SlotIconsEnabled) return;

        ImGui.Spacing();
        DrawSlotIconPreview();
    }

    private void DrawOnboardScreenshots()
    {
        ImGui.TextUnformatted("Where does FFXIV save your screenshots?");
        ImGui.Spacing();
        ImGui.TextWrapped("Wardrobe watches this folder during a screenshot session: it wears each " +
                          "item without a preview, waits for you to take a shot, then crops it, " +
                          "saves it to your images folder and assigns it — all automatically.");
        ImGui.Spacing();

        var defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "FINAL FANTASY XIV - A Realm Reborn", "screenshots");

        if (string.IsNullOrEmpty(_config.ScreenshotsFolder))
            ImGui.TextDisabled("No folder configured.");
        else
            ImGui.TextUnformatted(_config.ScreenshotsFolder);

        // Said out loud when the folder already is the detected one. Otherwise the Auto-detect
        // button below simply is not drawn, and a correctly detected folder is indistinguishable
        // from one the plugin never looked for.
        if (_config.ScreenshotsFolder == defaultFolder)
            ImGui.TextDisabled("Detected automatically — this is where FFXIV usually saves them.");

        ImGui.Spacing();
        if (Directory.Exists(defaultFolder) && _config.ScreenshotsFolder != defaultFolder)
        {
            if (ImGui.Button(" Auto-detect##onboardSs "))
            {
                _config.ScreenshotsFolder = defaultFolder;
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Use the usual location:\n{defaultFolder}");
            ImGui.SameLine();
        }

        if (ImGui.Button(" Browse…##onboardSs "))
        {
            var start = Directory.Exists(_config.ScreenshotsFolder) ? _config.ScreenshotsFolder
                      : Directory.Exists(defaultFolder)             ? defaultFolder
                      : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            _fileDialog.OpenFolderDialog("Select FFXIV Screenshots Folder", (ok, path) =>
            {
                if (!ok) return;
                _config.ScreenshotsFolder = path;
                _config.Save();
            }, start);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("While a session runs");
        ImGui.Spacing();

        // Off by default, and the difference between a set of clean per-item shots and a set of
        // photographs of your whole outfit. Worth asking before the first session rather than after
        var strip = _session.StripOthers;
        if (ImGui.Checkbox("Strip other items before each shot##onboard", ref strip))
            _session.StripOthers = strip;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes everything else via Emperor's New before equipping each\n" +
                             "item, so the shot is of that item alone.\n\n" +
                             "Leave it off to photograph each item on the outfit you have on.");

        var compact = _config.CompactDuringSession;
        if (ImGui.Checkbox("Shrink the wardrobe window during a session##onboard", ref compact))
        {
            _config.CompactDuringSession = compact;
            _config.Save();
            _compactOverride = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hides the grid and panels, leaving a small session view that\n" +
                             "stays out of the shot. It has an Expand button to come back.");

        ImGui.Spacing();
        ImGui.TextDisabled("Both are also on the session HUD, so they can be changed mid-session.");

        // Not a setting — it happens on every shot, Strip or no Strip — but it is a visible change
        // to your character, so it should not be a surprise
        ImGui.Spacing();
        ImGui.TextDisabled("Your weapon is hidden in Glamourer for each shot either way, unless the " +
                           "item being photographed is the weapon. It is put back as you had it " +
                           "when the session ends.");
    }

    private void DrawOnboardBackups()
    {
        ImGui.TextUnformatted("Keep backups of your wardrobe?");
        ImGui.Spacing();
        ImGui.TextWrapped("Your wardrobe is a single config file. Backups copy it to a folder of " +
                          "your choosing once an hour, and only when something has actually " +
                          "changed, so an idle session produces nothing.");
        ImGui.Spacing();
        ImGui.TextDisabled("Strongly recommended once you have more than a few items.");
        ImGui.Spacing();

        var enabled = _config.BackupEnabled;
        if (ImGui.Checkbox("Enable hourly backups##onboard", ref enabled))
        {
            _config.BackupEnabled = enabled;
            _config.Save();
        }

        ImGui.Spacing();
        if (string.IsNullOrEmpty(_config.BackupFolder))
            ImGui.TextDisabled("No backup folder selected.");
        else
            ImGui.TextUnformatted(_config.BackupFolder);

        ImGui.Spacing();
        if (ImGui.Button(" Browse…##onboardBackup "))
        {
            var start = Directory.Exists(_config.BackupFolder)
                ? _config.BackupFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            _fileDialog.OpenFolderDialog("Select Backup Folder", (ok, path) =>
            {
                if (!ok) return;
                _config.BackupFolder = path;
                _config.Save();
            }, start);
        }

        if (_config.BackupEnabled && string.IsNullOrEmpty(_config.BackupFolder))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                "Backups are on but no folder is set — nothing will be written.");
        }
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void DrawToolbar()
    {
        // Row 1: import + panels
        if (ImGui.Button("  + Import from Mod  "))
            _panel.OpenImport();

        UiLayout.SameLineIfRoomForButton("  Mass Import  ");
        if (ImGui.Button("  Mass Import  "))
            _massImport.Open();

        UiLayout.SameLineIfRoomForButton(" Select ");
        var wasSelecting = _selectMode;
        ToggleButton(" Select ", ref _selectMode);
        // ToggleButton only reports activation, and leaving the mode has to drop the selection
        if (wasSelecting && !_selectMode) ExitSelectMode();
        if (!wasSelecting && _selectMode) EnterSelectMode();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick several items and edit them together.\n" +
                             "Cards show a tick box instead of their buttons while this is on.");

        UiLayout.SameLineIfRoomForButton("  Images  ");
        ToggleButton("  Images  ", ref _showImageBrowser, onActivate: () =>
        {
            _showSettings = false;
            _showTags     = false;
            RefreshBrowserImages();
        });

        UiLayout.SameLineIfRoomForButton(" Settings ");
        ToggleButton(" Settings ", ref _showSettings, onActivate: () =>
        {
            _showImageBrowser = false;
            _showTags         = false;
        });

        UiLayout.SameLineIfRoomForButton(" Tags ");
        ToggleButton(" Tags ", ref _showTags, onActivate: () =>
        {
            _showImageBrowser = false;
            _showSettings     = false;

            // Whatever the last visit ended on has been read by now, and would otherwise reappear
            // as if something had just happened
            _newTag       = string.Empty;
            _newTagStatus = string.Empty;
        });


        if (_session.CanStart)
        {
            UiLayout.SameLineIfRoomForButton(" Screenshot Session ");
            if (ImGui.Button(" Screenshot Session "))
            {
                _showImageBrowser = false;
                _showSettings     = false;
                _session.Start();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Automatically wear each unimaged item and watch for a\nnew screenshot, then crop it to 1:1 and assign it.");
        }

        DrawItemCount();

        // Row 2: wardrobe actions
        ImGui.Spacing();

        if (_config.WornItems.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.45f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.12f, 0.12f, 1f));
            if (ImGui.Button(" Unequip All "))
            {
                foreach (var id in _config.WornItems.Values.ToList())
                {
                    var item = _config.WardrobeItems.Find(x => x.Id == id);
                    if (item != null) _wardrobe.UnwearItem(item, save: false);
                }
                _config.WornItems.Clear();
                _config.Save();
                _detectedWorn.Clear();
            }
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Take off every wardrobe item you are wearing." +
                                 (_config.ActiveBaseCharacter is { } unequipBase
                                     ? $"\n\nIncluding '{unequipBase.Name}' — this is the wardrobe\n" +
                                       "emptying itself, not a strip, so the base character is\n" +
                                       "not held back. Strip keeps it."
                                     : string.Empty));
            UiLayout.SameLineIfRoomForButton(" Strip ");
        }

        // Strip: force Emperor's New on every slot regardless of tracking
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.35f, 0.06f, 0.06f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.10f, 0.10f, 1f));
        if (ImGui.Button(" Strip "))
        {
            _wardrobe.StripAll();

            // Anything left running keeps its marker, so the grid does not claim the animation mods
            // that are still enabled were turned off
            _detectedWorn.RemoveWhere(id =>
                _config.WardrobeItems.Find(x => x.Id == id) is not { } item || !item.Slot.IsModCategory());
            _scanStatus = string.Empty;
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force every equipment slot to Emperor's New in Glamourer\n" +
                             "and disable all worn mods.\n\n" +
                             "Animations, VFX and mounts are left running — they are not\n" +
                             "on the character, so there is nothing to strip." +
                             (_config.ActiveBaseCharacter is { } stripBase
                                 ? $"\n\nStrips down to '{stripBase.Name}': its slots and items stay on."
                                 : string.Empty));

        UiLayout.SameLineIfRoomForButton(" In-Game Look ");

        // The counterpart to Strip: that one empties every slot, this one gets out of the way so the
        // gear the game has on shows through. Blue rather than the reds beside it — nothing is being
        // taken away here that the server was not already showing everyone else.
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.32f, 0.5f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.44f, 0.68f, 1f));
        if (ImGui.Button(" In-Game Look "))
        {
            var removed = _wardrobe.RevertToInGameLook();

            // As Strip does: anything left running keeps its marker, so the grid does not claim the
            // animation mods that are still enabled were turned off
            _detectedWorn.RemoveWhere(id =>
                _config.WardrobeItems.Find(x => x.Id == id) is not { } item || !item.Slot.IsModCategory());

            _scanStatus = removed > 0
                ? $"Took off {removed} item(s) — showing the game's own look."
                : "Already showing the game's own look.";
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Take the wardrobe's clothes off and clear Glamourer, so the\n" +
                             "character shows exactly what the game has on them —\n" +
                             "including any glamour plate you have applied.\n\n" +
                             "Animations, VFX and mounts are left running, as with Strip." +
                             (_config.KeepBaseCharacterOnRevert && _config.ActiveBaseCharacter is { } revertBase
                                 ? $"\n\n'{revertBase.Name}' goes back on top. Turn that off beside\n" +
                                   "Show In-Game Look in any glamour plate's edit panel."
                                 : _config.ActiveBaseCharacter != null
                                     ? "\n\nYour base character is not held back — the revert is\n" +
                                       "absolute, showing the unmodded character."
                                     : string.Empty));

        UiLayout.SameLineIfRoomForButton(" Refresh ");

        // Refresh: Penumbra redraw local player
        if (ImGui.Button(" Refresh "))
            Plugin.Penumbra.RedrawPlayer();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tell Penumbra to redraw the local player character.");

        UiLayout.SameLineIfRoomForButton(" Scan ");

        // Scan: detect worn state from enabled Penumbra mods
        if (ImGui.Button(" Scan "))
        {
            _detectedWorn.Clear();
            var scan = _wardrobe.ScanAndSyncWorn();
            foreach (var id in scan.Adopted) _detectedWorn.Add(id);

            // Also mark items already in WornItems as detected
            foreach (var id in _config.WornItems.Values)
                _detectedWorn.Add(id);

            _desynced = new List<WardrobeItem>(scan.Desynced);

            _scanStatus = scan.Adopted.Count > 0
                ? $"Detected {scan.Adopted.Count} new item(s) as worn."
                : _detectedWorn.Count > 0
                    ? "Wardrobe already in sync."
                    : "No wardrobe items detected as worn.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan Penumbra for enabled mods and mark matching\n" +
                             "wardrobe items as worn, and report any whose mods are\n" +
                             "enabled without Glamourer showing them.");

        if (!string.IsNullOrEmpty(_scanStatus))
        {
            UiLayout.SameLineIfRoomForText(_scanStatus);
            ImGui.TextDisabled(_scanStatus);
        }

        // Last, so it takes the right-hand end of the row rather than a place in the queue
        DrawCardSizeButton();
    }

    /// <summary>
    /// Warns about items whose Penumbra mods are enabled while Glamourer is showing something
    /// else, and offers the two ways out: finish applying them, or turn the leftover mods off.
    /// </summary>
    /// <remarks>
    /// This state is otherwise invisible and inescapable from inside the plugin. WornItems is
    /// cleared on load, so after a crash the wardrobe believes nothing is worn while the mods are
    /// still enabled in Penumbra — Unequip All then has nothing to unequip, and Strip walks the
    /// same empty list, so the only way to turn the mods off is to go and find them in Penumbra.
    /// </remarks>
    /// <summary>
    /// Explains, once, that mods enabled before ownership was tracked will not be turned off.
    /// </summary>
    /// <remarks>
    /// Shown only to configs that predate the change. Without it the new behaviour is indisting-
    /// uishable from a bug: mods an older version switched on stay on when their item is removed,
    /// with nothing on screen to say why or what to do instead.
    /// </remarks>
    private void DrawModOwnershipNotice()
    {
        if (_config.ModOwnershipNotice != OwnershipNoticeState.Pending) return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f),
            "● The wardrobe now only turns off mods it turned on itself.");
        ImGui.TextDisabled("A mod already enabled when you wear an item is left alone when you remove it, " +
                           "so Glamourer designs relying on it keep working. Mods enabled by an earlier " +
                           "version are not recognised as the wardrobe's and will stay on — turn those off " +
                           "in Penumbra, or use Disable Their Mods whenever they are listed as leftovers.");
        ImGui.Spacing();

        if (ImGui.Button(" Got It ##ownership "))
        {
            _config.ModOwnershipNotice = OwnershipNoticeState.Done;
            _config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawDesyncNotice()
    {
        // Deleting an item while the notice is up would otherwise leave a dangling reference here,
        // and acting on it would record a deleted item as worn
        if (_desynced.Count > 0)
            _desynced.RemoveAll(i => !_config.WardrobeItems.Contains(i));

        if (_desynced.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
            $"● {_desynced.Count} item(s) have mods enabled that Glamourer is not showing.");
        ImGui.TextDisabled("Usually a crash or /glamour disable. Wear them to finish applying, " +
                           "or turn the leftover mods off.");
        ImGui.Spacing();

        if (ImGui.Button(" Wear Them "))
        {
            foreach (var item in _desynced)
            {
                _wardrobe.WearItem(item);
                _detectedWorn.Add(item.Id);
            }
            _scanStatus = $"Re-applied {_desynced.Count} item(s).";
            _desynced.Clear();
            return; // the list the loop below draws is gone
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Equips each one in Glamourer and records it as worn,\n" +
                             "putting the wardrobe back in step with Penumbra.");

        UiLayout.SameLineIfRoomForButton(" Disable Their Mods ");
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.45f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.12f, 0.12f, 1f));
        var disableAll = ImGui.Button(" Disable Their Mods ");
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turns the leftover mods off in Penumbra and leaves\n" +
                             "Glamourer alone, so gear you are actually wearing stays put.\n\n" +
                             "Mods another worn item still needs are kept enabled.\n" +
                             "Everything else goes off whether the wardrobe enabled it\n" +
                             "or not — this is the one place that does.");
        if (disableAll)
        {
            foreach (var item in _desynced)
                _wardrobe.DisableItemMods(item);
            _scanStatus = $"Disabled mods for {_desynced.Count} item(s).";
            _desynced.Clear();
            return;
        }

        UiLayout.SameLineIfRoomForButton(" Ignore ");
        if (ImGui.Button(" Ignore "))
        {
            _desynced.Clear();
            return;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hides this until the next Scan. Nothing is changed.");

        // Per-item rows, so a single stray mod can be dealt with without touching the rest
        WardrobeItem? handled = null;
        foreach (var item in _desynced)
        {
            ImGui.PushID(item.Id.ToString());

            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(item.Name);

            var slotLabel = $"({item.Slot.DisplayName()})";
            UiLayout.SameLineIfRoomForText(slotLabel);
            ImGui.TextDisabled(slotLabel);

            UiLayout.SameLineIfRoomForButton("Wear");
            if (ImGui.SmallButton("Wear"))
            {
                _wardrobe.WearItem(item);
                _detectedWorn.Add(item.Id);
                handled = item;
            }

            UiLayout.SameLineIfRoomForButton("Disable");
            if (ImGui.SmallButton("Disable"))
            {
                _wardrobe.DisableItemMods(item);
                handled = item;
            }

            ImGui.PopID();
        }

        // Removed after the loop rather than inside it, so the list is not mutated while enumerated
        if (handled != null) _desynced.Remove(handled);
    }

    /// <summary>
    /// Empties the selection without leaving the actions panel — it says "nothing selected" and
    /// waits, rather than vanishing mid-task because you emptied the list to start again.
    /// </summary>
    private void ClearSelection()
    {
        _selected.Clear();
        _bulkTag    = string.Empty;
        _bulkStatus = string.Empty;
    }

    /// <summary>Leaves select mode entirely: no selection, and the panel hands back to whatever it covered.</summary>
    private void ExitSelectMode()
    {
        _selectMode    = false;
        _bulkPanelOpen = false;
        ClearSelection();
    }

    private void EnterSelectMode()
    {
        _bulkCollections   = Plugin.Penumbra.GetCollections();
        _bulkCollectionIdx = 0;

        // Start on the configured default rather than whichever collection sorts first, which is
        // rarely the one in use and would make a mis-click move items somewhere meaningless
        if (!string.IsNullOrEmpty(_config.DefaultCollection))
        {
            var idx = _bulkCollections.ToList().FindIndex(
                c => c.Equals(_config.DefaultCollection, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _bulkCollectionIdx = idx;
        }
    }

    /// <summary>Selected items that still exist, in the grid's current order.</summary>
    private List<WardrobeItem> SelectedItems() =>
        _config.WardrobeItems.Where(i => _selected.Contains(i.Id)).ToList();

    /// <summary>
    /// Actions for the current selection, shown under the toolbar while select mode is on.
    /// </summary>
    /// <remarks>
    /// Select All takes what the grid drew rather than the whole wardrobe, so it composes with the
    /// search, slot, tag, worn and favourite filters instead of duplicating any of them: narrow the
    /// grid to what you mean, then act on all of it.
    /// </remarks>
    private void DrawBulkBar()
    {
        if (!_selectMode) return;

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{_selected.Count} selected");

        UiLayout.SameLineIfRoomForButton(" Select All ");
        if (ImGui.Button(" Select All "))
        {
            foreach (var id in _lastVisibleIds) _selected.Add(id);
            _bulkStatus = string.Empty;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Selects every item the current filters are showing,\n" +
                             "not the whole wardrobe.");

        UiLayout.SameLineIfRoomForButton(" Clear ");
        if (ImGui.Button(" Clear ")) ClearSelection();

        var none = _selected.Count == 0;

        UiLayout.SameLineIfRoomForButton(" Edit Selected ");
        if (none) ImGui.BeginDisabled();
        if (ImGui.Button(" Edit Selected ")) _bulkPanelOpen = true;
        if (none) ImGui.EndDisabled();
        if (none && ImGui.IsItemHovered())
            ImGui.SetTooltip("Tick some items first.");

        UiLayout.SameLineIfRoomForButton(" Done ");
        if (ImGui.Button(" Done ")) ExitSelectMode();
    }

    private void ApplyBulkFavourite(bool on)
    {
        var changed = 0;
        foreach (var item in SelectedItems())
        {
            if (item.IsFavorite == on) continue;
            item.IsFavorite = on;
            changed++;
        }

        if (changed > 0) _config.Save();
        _bulkStatus = changed == 0
            ? "No items changed."
            : $"{(on ? "Favourited" : "Unfavourited")} {changed} item(s).";
    }

    /// <summary>
    /// Moves every mod on the selected items into one Penumbra collection.
    /// </summary>
    /// <remarks>
    /// The single-item editor also moves other items referencing the same mod in the same old
    /// collection, on the grounds that one item's wrong collection was probably wrong everywhere.
    /// This deliberately does not: the selection *is* the statement of what to move, and quietly
    /// changing items the user did not select would make the count in the bar a lie.
    /// </remarks>
    private void DrawBulkCollectionActions()
    {
        if (_bulkCollections.Count == 0) return;

        var current = _bulkCollections[Math.Min(_bulkCollectionIdx, _bulkCollections.Count - 1)];

        ImGui.TextUnformatted("Collection");
        ImGui.TextDisabled("Moves every mod on the selected items into one collection. Other items " +
                           "using the same mods are left where they are.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##bulkColl", current))
        {
            for (var i = 0; i < _bulkCollections.Count; i++)
            {
                if (ImGui.Selectable(_bulkCollections[i], i == _bulkCollectionIdx))
                    _bulkCollectionIdx = i;
                if (i == _bulkCollectionIdx) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        if (ImGui.Button($"Move to '{Truncate(current, 24)}'", new Vector2(-1, 0)))
            ApplyBulkCollection(current);
    }

    private void ApplyBulkCollection(string collection)
    {
        var changed = 0;
        var reWear  = new List<WardrobeItem>();

        foreach (var item in SelectedItems())
        {
            var touched = false;
            foreach (var mod in item.Mods)
            {
                if (mod.Collection.Equals(collection, StringComparison.OrdinalIgnoreCase)) continue;
                mod.Collection = collection;
                touched = true;
            }

            if (!touched) continue;
            changed++;
            if (_wardrobe.IsItemWorn(item)) reWear.Add(item);
        }

        if (changed > 0) _config.Save();

        // A worn item's mods were enabled in the collection it just left, so re-apply it to switch
        // them on in the new one. The old collection keeps them enabled — the same limitation the
        // single-item editor has.
        foreach (var item in reWear) _wardrobe.WearItem(item);
        if (reWear.Count > 0) Plugin.Penumbra.RedrawPlayer();

        _log.Information($"[Wardrobe] Bulk move: {changed} item(s) to '{collection}', " +
                         $"{reWear.Count} re-applied");

        _bulkStatus = changed == 0
            ? "Already in that collection."
            : $"Moved {changed} item(s) to '{collection}'.";
    }

    private void DrawBulkDeleteConfirm()
    {
        // Centred on the viewport, so it cannot open partly off-screen from a toolbar near an edge
        var vp     = ImGui.GetMainViewport();
        var centre = new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f);
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(BulkDeletePopup, ImGuiWindowFlags.AlwaysAutoResize)) return;

        var items = SelectedItems();
        var worn  = items.Count(_wardrobe.IsItemWorn);

        ImGui.TextUnformatted($"Delete {items.Count} item(s) from the wardrobe?");
        ImGui.Spacing();

        // The reassurance that matters: this is a wardrobe entry, not the mod on disk
        ImGui.TextDisabled("Your Penumbra mods are not touched — only these wardrobe entries,\n" +
                           "with their names, images, tags and notes.");

        if (worn > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f),
                $"{worn} of them are currently worn and will be taken off first.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("This cannot be undone.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Guarded as well as confirmed. The dialog says what is about to happen, but this is the
        // largest delete in the plugin and the rule is the rule everywhere.
        if (DeleteButton($" Delete {items.Count} ",
                $"Deletes {items.Count} item(s) from the wardrobe.", UiScale.S(140, 0)))
        {
            ApplyBulkDelete(items);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(" Cancel ", UiScale.S(120, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void ApplyBulkDelete(List<WardrobeItem> items)
    {
        foreach (var item in items)
        {
            // save: false — one write at the end rather than one per item
            if (_wardrobe.IsItemWorn(item)) _wardrobe.UnwearItem(item, save: false);
            _wardrobe.ForgetLinksTo(item.Id);
            _wardrobe.ReparentVariantsOf(item);
            _config.WardrobeItems.Remove(item);
            _imageCache.Remove(item.Id);
        }

        _config.Save();
        _log.Information($"[Wardrobe] Bulk delete: removed {items.Count} item(s)");

        // Nothing selected is left to act on, and a stale count in the bar would be misleading
        _selected.Clear();
        _bulkStatus = $"Deleted {items.Count} item(s).";
    }

    /// <summary>
    /// The bulk actions, shown in the right panel once Edit Selected is pressed.
    /// </summary>
    /// <remarks>
    /// Back rather than a close: it returns to whatever the panel was showing — usually the tag
    /// filter — with the selection intact, so narrowing the grid and acting on it can be alternated
    /// without starting over.
    /// </remarks>
    private void DrawSelectionPanel()
    {
        if (ImGui.Button(" < Back ")) _bulkPanelOpen = false;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Back to the panel you were on. The selection is kept.");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{_selected.Count} selected");

        ImGui.Separator();
        ImGui.Spacing();

        if (_selected.Count == 0)
        {
            ImGui.TextDisabled("Nothing selected. Tick some items in the grid.");
            return;
        }

        DrawBulkLinkActions();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBulkTagActions();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBulkCollectionActions();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Favourites");
        ImGui.Spacing();
        var favW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        if (ImGui.Button("♥ Favourite", new Vector2(favW, 0)))  ApplyBulkFavourite(true);
        ImGui.SameLine();
        if (ImGui.Button("Unfavourite", new Vector2(favW, 0)))   ApplyBulkFavourite(false);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBulkScreenshotAction();

        if (!string.IsNullOrEmpty(_bulkStatus))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.6f, 1f), _bulkStatus);
        }

        // Pinned to the bottom, well away from everything else. A destructive action should not sit
        // in the flow directly under a text field you were just typing in.
        var bottom = ImGui.GetWindowHeight() - ImGui.GetFrameHeight() - ImGui.GetStyle().WindowPadding.Y;
        if (bottom > ImGui.GetCursorPosY() + ImGui.GetFrameHeight())
            ImGui.SetCursorPosY(bottom);
        else
            ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.45f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.12f, 0.12f, 1f));
        if (ImGui.Button($"Delete {_selected.Count} item(s)", new Vector2(-1, 0)))
            ImGui.OpenPopup(BulkDeletePopup);
        ImGui.PopStyleColor(2);

        DrawBulkDeleteConfirm();
    }

    /// <summary>
    /// Starts a screenshot session over the selection.
    /// </summary>
    /// <remarks>
    /// The session over everything only queues items with no preview yet, which is right when the
    /// answer to "what should I photograph" is "whatever is missing". It is the wrong rule for a set
    /// picked by hand — re-shooting previews you are unhappy with is most of why you would select a
    /// batch — so this runs exactly what is ticked, and says so rather than quietly dropping the
    /// ones that already have an image.
    /// </remarks>
    private void DrawBulkScreenshotAction()
    {
        var items    = SelectedItems();
        var withShot = items.Count(i => !string.IsNullOrEmpty(i.ImagePath));

        ImGui.TextUnformatted("Screenshots");

        if (!_session.FoldersReady)
        {
            ImGui.TextDisabled("Set the images and screenshots folders in Settings first.");
            return;
        }

        if (_session.State != SessionState.Idle)
        {
            ImGui.TextDisabled("A screenshot session is already running.");
            return;
        }

        ImGui.TextDisabled(withShot == 0
            ? $"Photographs all {items.Count} selected item(s)."
            : $"Photographs all {items.Count} selected item(s), including the {withShot} that " +
              "already\nhave a preview — those will be replaced.");

        ImGui.Spacing();

        if (ImGui.Button($"Photograph {items.Count} item(s)", new Vector2(-1, 0)))
        {
            // The selection is kept: a session that misfires should be re-runnable without ticking
            // everything again, and the panel it was started from is where you would go back to
            if (_session.StartMany(items))
            {
                _bulkPanelOpen = false;
                _log.Information($"[Wardrobe] Screenshot session started for {items.Count} selected item(s)");
            }
            else
            {
                _bulkStatus = "Could not start the session.";
            }
        }
    }

    /// <summary>
    /// Link every item in the selection to every other, or break those links again.
    /// </summary>
    /// <remarks>
    /// Every pair rather than a chain, because links are followed one hop only: a chain would leave
    /// the ends unable to reach each other, so wearing the middle piece would bring the set and
    /// wearing an end would bring one item. Pairing them all makes any member wear the whole set.
    /// Two items in one slot cannot be linked and are reported rather than silently dropped —
    /// selecting a row of tops and pressing Link is an easy mistake, and one that would otherwise
    /// look like the button had done nothing.
    /// </remarks>
    private void DrawBulkLinkActions()
    {
        var items = SelectedItems();

        ImGui.TextUnformatted("Linked items");
        ImGui.TextDisabled("Linked items are worn and taken off together. Each card keeps a button " +
                           "for using just that one.");
        ImGui.Spacing();

        var tooFew = items.Count < 2;
        if (tooFew) ImGui.BeginDisabled();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        if (ImGui.Button("Link", new Vector2(btnW, 0)))   ApplyBulkLink(items, link: true);
        ImGui.SameLine();
        if (ImGui.Button("Unlink", new Vector2(btnW, 0))) ApplyBulkLink(items, link: false);

        if (tooFew) ImGui.EndDisabled();

        // AllowWhenDisabled, or the one tooltip that explains why the buttons are greyed out would
        // be the one tooltip ImGui refuses to show
        if (tooFew && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Tick at least two items.");

        // Unlink above only undoes links *within* the selection, which cannot reach a partner that
        // is no longer ticked. This is the way out of that.
        if (ImGui.SmallButton("Clear every link on these items"))
            ApplyClearLinks(items);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Unlinks the selected items from everything, including items\n" +
                             "that are not selected. The items themselves are kept.");
    }

    private void ApplyBulkLink(List<WardrobeItem> items, bool link)
    {
        var changed = 0;
        var refused = new List<string>();

        for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
        {
            if (link)
            {
                if (WardrobeService.AreLinked(items[i], items[j])) continue;
                if (_wardrobe.Link(items[i], items[j], out var refusal)) changed++;
                else if (refusal != null) refused.Add($"{items[i].Name} + {items[j].Name}");
            }
            else if (_wardrobe.Unlink(items[i], items[j]))
            {
                changed++;
            }
        }

        if (changed > 0) _config.Save();

        _log.Information($"[Wardrobe] Bulk link: {(link ? "linked" : "unlinked")} {changed} pair(s) " +
                         $"across {items.Count} selected item(s)"
                       + (refused.Count > 0 ? $"; refused: {string.Join(", ", refused)}" : string.Empty));

        if (changed == 0 && refused.Count == 0)
            _bulkStatus = link ? "Already linked." : "None of these were linked.";
        else
            _bulkStatus = $"{(link ? "Linked" : "Unlinked")} {changed} pair(s)."
                        + (refused.Count > 0
                            ? $" {refused.Count} pair(s) share a slot and cannot be linked — " +
                              "wearing one would take the other off."
                            : string.Empty);
    }

    private void ApplyClearLinks(List<WardrobeItem> items)
    {
        // Counted up front: two selected items linked to each other are cleared by whichever is
        // reached first, and the second would otherwise look untouched and go unreported
        var changed = items.Count(i => i.LinkedItemIds.Count > 0);

        foreach (var item in items)
        {
            if (item.LinkedItemIds.Count == 0) continue;

            // Both sides, so the partners left behind are not still pointing here
            foreach (var partner in _wardrobe.ResolveLinks(item))
                partner.LinkedItemIds.Remove(item.Id);

            item.LinkedItemIds.Clear();
        }

        if (changed > 0) _config.Save();
        _log.Information($"[Wardrobe] Bulk link: cleared all links on {changed} item(s)");

        _bulkStatus = changed == 0
            ? "None of these had links."
            : $"Cleared every link on {changed} item(s).";
    }

    /// <summary>
    /// Add or remove one tag across the selection.
    /// </summary>
    /// <remarks>
    /// Add and remove rather than "set": replacing the tag list would silently discard whatever
    /// each item had of its own, which is exactly the kind of loss a bulk action must never cause.
    /// </remarks>
    private void DrawBulkTagActions()
    {
        // Pre-made tags included: applying one to a batch here is the main reason to make it early
        var known = _config.AllTags();

        ImGui.TextUnformatted("Tags");
        ImGui.TextDisabled("Added to or removed from every selected item. Tags an item already " +
                           "has, and tags you are not naming here, are left alone.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##bulkTag", "tag name", ref _bulkTag, 64);

        // Browsing beats a flat list: tags are nested, so the tree is both the shortest route to a
        // deep one and the only view that shows what already exists under a heading before a
        // near-duplicate is typed in beside it
        if (known.Count > 0)
        {
            ImGui.Spacing();
            DrawBulkTagTree();
        }

        var tag      = _bulkTag.Trim();
        var disabled = tag.Length == 0;

        ImGui.Spacing();
        if (disabled) ImGui.BeginDisabled();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        if (ImGui.Button("Add Tag", new Vector2(btnW, 0)))    ApplyBulkTag(tag, add: true);
        ImGui.SameLine();
        if (ImGui.Button("Remove Tag", new Vector2(btnW, 0))) ApplyBulkTag(tag, add: false);

        if (disabled) ImGui.EndDisabled();
    }

    /// <summary>
    /// The tag tree, inline in the selection panel, for picking a tag to apply to the selection.
    /// </summary>
    /// <remarks>
    /// The same shape as the Tags panel's filter tree, so tags are chosen the way they are
    /// organised rather than out of a flat alphabetical list where <c>Shoes/Heels/Pumps</c> and
    /// <c>Shoes/Boots/Ankle</c> sit apart from each other. Branches collapse, which is what keeps it
    /// usable once there are a hundred tags — a flat list only gets longer.
    /// <para>
    /// A scrolling box of its own rather than part of the panel's flow: the panel below it is fixed
    /// content ending in Delete, and a tree that grew with the tag list would push that around.
    /// </para>
    /// </remarks>
    private void DrawBulkTagTree()
    {
        var height = ImGui.GetTextLineHeightWithSpacing() * 12;

        // EndChild unconditionally — it is required even when BeginChild returns false
        // Styles included here, unlike the filter tree: this is the only place a style can be put on
        // a whole selection at once, which is most of the reason to have one
        if (ImGui.BeginChild("##bulkTagTree", new Vector2(-1, height), true))
            TagTree.DrawPicker(TagTree.Build(_config, includeStyles: true), "bulkpick",
                path => _bulkTag.Trim().Equals(path, StringComparison.OrdinalIgnoreCase),
                path =>
                {
                    _bulkTag    = path;
                    _bulkStatus = string.Empty;
                });
        ImGui.EndChild();

        ImGui.TextDisabled("Click a tag to put it in the box above. Greyed tags have no items yet.");
    }

    private void ApplyBulkTag(string tag, bool add)
    {
        var changed = 0;

        foreach (var item in SelectedItems())
        {
            if (add)
            {
                if (item.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) continue;
                item.Tags.Add(tag);
                changed++;
            }
            else
            {
                changed += item.Tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)) > 0 ? 1 : 0;
            }
        }

        if (changed > 0) _config.Save();

        _log.Information($"[Wardrobe] Bulk tag: {(add ? "added" : "removed")} '{tag}' " +
                         $"on {changed} of {_selected.Count} selected item(s)");

        // Says nothing changed rather than claiming a count, so applying a tag everything already
        // has does not read as having done something
        _bulkStatus = changed == 0
            ? "No items changed."
            : $"{(add ? "Tagged" : "Untagged")} {changed} item(s).";

        // The selection survives, so several tags can be applied to the same set in a row
    }

    /// <summary>
    /// The card size slider, behind an icon under the item count.
    /// </summary>
    /// <remarks>
    /// The same setting as the one in Settings, put where it is used. Card size is judged by looking
    /// at the grid and dragging until it looks right, and a slider four panels away from the thing
    /// it resizes cannot be judged at all — you set it, close Settings, look, and go back.
    /// </remarks>
    private void DrawCardSizeButton()
    {
        var text = FontAwesomeIcon.Search.ToIconString();

        float width;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
            width = ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2;

        // Against the right edge of the actions row. Placed rather than queued: it is a view control
        // among wardrobe actions, so it belongs at the far end rather than in the run of buttons that
        // change what is worn.
        ImGui.SameLine();

        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);

        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
            clicked = ImGui.Button($"{text}##cardsizepop");

        if (clicked) ImGui.OpenPopup(CardSizePopup);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Card size — items {_config.CardScale:0.00}x, " +
                             $"outfits {_config.OutfitCardScale:0.00}x\nClick to change them.");

        if (!ImGui.BeginPopup(CardSizePopup)) return;

        ImGui.TextDisabled("Card size");
        ImGui.Spacing();

        // Both grids, since the popup is reachable from either and the two are set against each other
        DrawCardScaleSlider("Items", UiScale.S(220));
        ImGui.Spacing();
        DrawCardScaleSlider("Outfits", UiScale.S(220));

        ImGui.EndPopup();
    }

    private const string CardSizePopup = "##cardsizeslider";

    /// <summary>
    /// Wardrobe item count, right-aligned on the toolbar's first row. Shows how many of the total
    /// are currently visible whenever a filter or search is narrowing the grid.
    /// </summary>
    private void DrawItemCount()
    {
        var total = _config.WardrobeItems.Count;
        if (total == 0) return;

        var text = _visibleCount == total
            ? $"{total} item{(total == 1 ? "" : "s")}"
            : $"{_visibleCount} of {total} items";

        ImGui.SameLine();
        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);

        ImGui.AlignTextToFramePadding();

        // Placed to sit exactly against the right edge, so wrapping there would break it onto a
        // second line for a rounding error's worth of width
        UiLayout.PushNoWrap();
        ImGui.TextDisabled(text);
        UiLayout.PopWrap();

        if (_visibleCount != total && ImGui.IsItemHovered())
            ImGui.SetTooltip("Filtered by the current search, slot, style, tag, worn or favourites " +
                             "selection.");
    }

    private void ToggleButton(string label, ref bool state, Action? onActivate = null)
    {
        var highlighted = state && !_panel.IsOpen;
        if (highlighted)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
        }

        if (ImGui.Button(label))
        {
            state = !state;
            if (state) onActivate?.Invoke();
        }

        if (highlighted)
            ImGui.PopStyleColor(2);
    }

    // ── Slot filter bar ───────────────────────────────────────────────────────

    /// <summary>Label on the dropdown holding the slot filters that did not fit on the bar.</summary>
    private const string MoreLabel = "More…";

    /// <summary>
    /// Below this much room, the bar stops reserving space for the search and sort controls and
    /// takes the whole row — a couple of slot buttons squeezed against them helps nobody.
    /// </summary>
    private static float MinSlotRunWidth => UiScale.S(180f);

    private static float SearchBoxWidth => UiScale.S(200f);
    private static float SortBoxWidth   => UiScale.S(150f);

    /// <summary>
    /// Width of the styles dropdown. Narrower than the boxes beside it — its preview is one style
    /// name and a count, not a sentence.
    /// </summary>
    private static float StyleBoxWidth  => UiScale.S(110f);

    /// <summary>
    /// The styles as of this frame, or empty where the dropdown is not drawn.
    /// </summary>
    /// <remarks>
    /// Read once at the top of the filter row because two things need it and neither should scan
    /// every item's tags again for it: the dropdown itself, and the width of the block it sits in —
    /// which is what decides how many slot buttons fit before the rest go into the More dropdown.
    /// </remarks>
    private IReadOnlyList<TagNode> _frameStyles = Array.Empty<TagNode>();

    /// <summary>
    /// Spare room a slot button needs before it is promoted out of the More dropdown, so the bar
    /// settles instead of flickering while the window is being dragged.
    /// </summary>
    private static float PromoteSlack => UiScale.S(24f);

    /// <summary>
    /// Slot buttons currently on the bar. Kept across frames because the split has hysteresis —
    /// see <see cref="VisibleSlotCount"/>.
    /// </summary>
    private int _visibleSlots;

    /// <summary>
    /// Width of the search and sort controls together, as they are drawn at the end of the filter
    /// row. Shared so the bar can reserve exactly what they will take.
    /// </summary>
    /// <summary>
    /// Width of the search box's clear button. Read by both the layout maths and the button itself,
    /// so the space reserved and the space filled cannot drift apart.
    /// </summary>
    private static float ClearButtonWidth() =>
        ImGui.CalcTextSize("×").X + ImGui.GetStyle().FramePadding.X * 2;

    private float SearchBlockWidth()
    {
        var style = ImGui.GetStyle();

        // The clear button's width counts whether or not it is drawn — an empty search box holds its
        // place with a Dummy. This width decides how many slot buttons fit before the rest go into
        // More, so letting it depend on the box being empty meant typing the first character could
        // push a slot button out and reflow the whole row.
        var clearW = ClearButtonWidth() + style.ItemSpacing.X;
        var styleW = _frameStyles.Count == 0 ? 0f : StyleBoxWidth + style.ItemSpacing.X;

        return styleW + SearchBoxWidth + clearW + style.ItemSpacing.X + SortBoxWidth;
    }

    private void DrawSlotFilter()
    {
        ImGui.Spacing();

        var styles = TagTree.Styles(_config);

        // Deleting the last style while filtered on it would otherwise leave the grid empty with
        // nothing on screen to clear it — the control that held the filter is gone with it
        if (styles.Count == 0) _styleFilter.Clear();

        // Shown in both views: outfits carry styles of their own and are filtered by them, so the
        // dropdown means the same thing on either side and the filter survives switching between them
        _frameStyles = styles;

        // Outfits is a view of its own rather than a slot filter, so it sits first and apart
        var outfitsActive = _outfitsView;
        if (outfitsActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
        }
        if (ImGui.Button("Outfits", FilterRowButton("Outfits"))) _outfitsView = !_outfitsView;
        var outfitsHovered = ImGui.IsItemHovered();
        if (outfitsActive) ImGui.PopStyleColor(2);
        if (outfitsHovered)
            ImGui.SetTooltip(outfitsActive
                ? "Showing saved outfits. Click to go back to items."
                : "Show saved outfits instead of items.");

        UiLayout.SameLineIfRoomForButton("All");
        DrawFilterButton("All", null);

        UiLayout.SameLineIfRoomForButton("♥");

        // Capture the state up front: the button toggles _favoritesOnly, so guarding the pop on
        // the live value pops a style that was never pushed (or leaks one that was), corrupting
        // ImGui's stack and crashing inside cimgui on a later frame.
        var favActive = _favoritesOnly;
        if (favActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.15f, 0.28f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.72f, 0.2f, 0.36f, 1f));
        }
        if (ImGui.Button("♥", FilterRowButton("♥"))) _favoritesOnly = !_favoritesOnly;
        var favHovered = ImGui.IsItemHovered();
        if (favActive) ImGui.PopStyleColor(2);

        if (favHovered)
            ImGui.SetTooltip(favActive ? "Showing favourites only." : "Show favourites only.");

        UiLayout.SameLineIfRoomForButton("Worn");

        // Same up-front capture as the favourites button above, for the same reason
        var wornActive = _wornOnly;
        if (wornActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.45f, 0.35f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.60f, 0.46f, 1f));
        }
        if (ImGui.Button("Worn", FilterRowButton("Worn"))) _wornOnly = !_wornOnly;
        var wornHovered = ImGui.IsItemHovered();
        if (wornActive) ImGui.PopStyleColor(2);

        if (wornHovered)
            ImGui.SetTooltip(wornActive
                ? "Showing worn items only. Click to show everything again."
                : "Show only what is currently on — items the wardrobe is\n" +
                  "tracking as worn, plus anything the last Scan found\n" +
                  "enabled in Penumbra.");

        // Only offered once something is actually grouped. On a wardrobe with no variants it would
        // be a button that empties the grid and never does anything else.
        if (_config.WardrobeItems.Any(i => i.VariantOfId.HasValue))
        {
            UiLayout.SameLineIfRoomForButton("Variants");

            // Same up-front capture as the two buttons above, for the same reason
            var varActive = _variantsOnly;
            if (varActive)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.36f, 0.26f, 0.55f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.48f, 0.35f, 0.72f, 1f));
            }
            if (ImGui.Button("Variants", FilterRowButton("Variants"))) _variantsOnly = !_variantsOnly;
            var varHovered = ImGui.IsItemHovered();
            if (varActive) ImGui.PopStyleColor(2);

            if (varHovered)
                ImGui.SetTooltip(varActive
                    ? "Showing items that have variants. Click to show everything again."
                    : "Show only pieces you have more than one version of —\n" +
                      "an original and its variants. Folded groups still show\n" +
                      "as one card, so this is a list of the pieces, not the copies.");
        }

        DrawSlotButtons();
        DrawSearchAndSort();
        ImGui.Spacing();
    }

    /// <summary>
    /// The styles row: one dropdown under the slot filters for narrowing by mood or theme.
    /// </summary>
    /// <remarks>
    /// A row of its own rather than another button on the filter row, because styles are a scheme
    /// that grows — ten of them would push the slot buttons into the More dropdown. Drawn only once
    /// at least one style exists, so a wardrobe that does not use them is not given an empty control
    /// to wonder about, the same way the Variants button waits for a variant.
    /// </remarks>
    /// <summary>
    /// The styles dropdown, sitting in the search block on the filter row.
    /// </summary>
    /// <remarks>
    /// Clearing lives inside the popup rather than as a button beside it. On a row this crowded a
    /// control that only appears once a filter is on pushes everything left of it along as it comes
    /// and goes, and the place you are already looking when you want to drop a style is the list of
    /// them.
    /// </remarks>
    private void DrawStyleCombo()
    {
        // Chosen in the order they are shown, so the preview does not reshuffle as styles are ticked
        var chosen = _frameStyles.Where(s => _styleFilter.Contains(s.FullPath)).ToList();
        var active = chosen.Count > 0;

        var preview = chosen.Count switch
        {
            0 => "Styles",
            1 => chosen[0].Segment,
            _ => $"{chosen[0].Segment} +{chosen.Count - 1}",
        };

        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0.52f, 0.38f, 0.74f, 1f));
        }

        ImGui.SetNextItemWidth(StyleBoxWidth);
        var open = ImGui.BeginCombo("##stylefilter", preview);
        if (active) ImGui.PopStyleColor(3);

        if (!open)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(active
                    ? $"Showing {string.Join(", ", chosen.Select(c => c.Segment))}."
                    : "Filter by mood or theme — Casual, Beach, Comfy.\n" +
                      "Set them on an item in its edit panel, or on a\n" +
                      "whole selection from Select → Edit Selected.");
            return;
        }

        if (active)
        {
            if (ImGui.Selectable("Clear", false, ImGuiSelectableFlags.DontClosePopups))
                _styleFilter.Clear();
            ImGui.Separator();
        }

        foreach (var style in _frameStyles)
        {
            var on = _styleFilter.Contains(style.FullPath);

            // The popup stays open on a click: picking several styles is the normal case, and
            // reopening the dropdown between each would make it the tedious one
            if (ImGui.Selectable(style.Segment, on, ImGuiSelectableFlags.DontClosePopups))
            {
                if (!_styleFilter.Remove(style.FullPath))
                    _styleFilter.Add(style.FullPath);
            }

            if (!style.InUse && ImGui.IsItemHovered())
                ImGui.SetTooltip("No items have this style yet.");
        }

        ImGui.EndCombo();
    }

    /// <summary>
    /// Slot filters: as many as fit on the row as buttons, the rest behind a More dropdown.
    /// </summary>
    /// <remarks>
    /// Seventeen slots — twenty with mod categories on — fit no sensible window width, least of all
    /// spelled out rather than shown as icons. The split is worked out afresh every frame from the
    /// space actually left on the row, so widening the window promotes slots out of the dropdown
    /// and narrowing it puts them back. Slots stay in their usual order and are only ever taken
    /// from the end, so a given slot never jumps position within the bar.
    /// </remarks>
    private void DrawSlotButtons()
    {
        var slots = _config.ModCategoriesEnabled
            ? EquipSlotEx.All.Concat(EquipSlotEx.ModCategories).ToArray()
            : EquipSlotEx.All;

        var style   = ImGui.GetStyle();
        var moreW   = MoreButtonWidth();
        var lastX   = ImGui.GetItemRectMax().X;
        var content = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;

        // Search and sort right-align onto this same row, so the space they will take is reserved
        // rather than overrun — unless doing so would leave the buttons no usable run, in which
        // case they get the whole row and DrawSearchAndSort drops to a line of its own.
        var rowRight = content - SearchBlockWidth() - style.ItemSpacing.X;
        if (rowRight - lastX < MinSlotRunWidth) rowRight = content;

        var visible = VisibleSlotCount(slots, lastX, rowRight, moreW);

        for (var i = 0; i < visible; i++)
        {
            UiLayout.SameLineIfRoom(FilterButtonWidth(slots[i]));
            DrawFilterButton(slots[i].DisplayName(), slots[i]);
        }

        if (visible < slots.Length)
            DrawMoreSlots(slots.Skip(visible).ToArray(), moreW);
    }

    /// <summary>
    /// How many slot buttons to show on the bar, with hysteresis so a drag-resize does not make
    /// the last button flicker in and out.
    /// </summary>
    /// <remarks>
    /// A button is only promoted onto the bar once there is <see cref="PromoteSlack"/> more room
    /// than it strictly needs, but is demoted the moment it truly does not fit. That leaves a dead
    /// band either side of the boundary: dragging the window edge through it changes nothing, and
    /// the button appears once the drag is clearly past the point where it belongs on the bar.
    /// Without it, promoting the button consumes the very space that let it be promoted, and the
    /// bar alternates between two layouts every frame.
    /// </remarks>
    private int VisibleSlotCount(IReadOnlyList<EquipSlot> slots, float startX, float rowRight, float moreW)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        int Fits(float slack)
        {
            var count = 0;
            var x     = startX;

            for (var i = 0; i < slots.Count; i++)
            {
                // Room for the More button has to be kept whenever anything is left over
                var tail = i == slots.Count - 1 ? 0f : spacing + moreW;

                if (x + spacing + FilterButtonWidth(slots[i]) + tail + slack > rowRight) break;

                x += spacing + FilterButtonWidth(slots[i]);
                count++;
            }

            return count;
        }

        // Shrinking is immediate, since the alternative is a button drawn off the side
        var mustFit = Fits(0f);
        if (_visibleSlots > mustFit) _visibleSlots = mustFit;

        var roomToGrow = Fits(PromoteSlack);
        if (_visibleSlots < roomToGrow) _visibleSlots = roomToGrow;

        return Math.Clamp(_visibleSlots, 0, slots.Count);
    }

    /// <summary>Dropdown holding the slot filters that did not fit on the bar.</summary>
    private void DrawMoreSlots(IReadOnlyList<EquipSlot> slots, float width)
    {
        // The label carries the selection when it is one of the hidden slots, so the active filter
        // is still readable without opening the dropdown
        var selected = _slotFilter is { } f && slots.Contains(f) ? f : (EquipSlot?)null;
        var preview  = selected?.DisplayName() ?? MoreLabel;

        UiLayout.SameLineIfRoom(width);

        if (selected != null)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0.4f, 0.6f, 0.9f, 1f));
        }

        // A combo takes no explicit size, so its frame is grown with padding instead. Popped as soon
        // as the frame is laid out, so the list inside keeps its normal row spacing.
        var extraY = Math.Max(0f, (FilterRowHeight - ImGui.GetFrameHeight()) / 2f);
        if (extraY > 0f)
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().FramePadding.Y + extraY));

        ImGui.SetNextItemWidth(width);
        var open = ImGui.BeginCombo("##moreslots", preview);

        if (extraY > 0f) ImGui.PopStyleVar();
        if (selected != null) ImGui.PopStyleColor(3);

        if (!open)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{slots.Count} more slot(s) — the bar shows as many as fit.");
            return;
        }

        foreach (var slot in slots)
        {
            var active = _slotFilter == slot;

            // Re-picking the active slot clears the filter, so the dropdown can undo itself
            // without reaching back to the All button
            if (ImGui.Selectable(slot.DisplayName(), active))
                _slotFilter = active ? null : slot;

            if (active) ImGui.SetItemDefaultFocus();
            if (slot.Hint() is { } hint && ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
        }

        ImGui.EndCombo();
    }

    /// <summary>Width the More dropdown occupies.</summary>
    /// <remarks>
    /// Reserved for the selected slot's name whenever there is a slot filter, whether or not that
    /// slot has actually overflowed. Measuring the label the dropdown happens to be showing would
    /// make its width depend on the split, which in turn depends on its width; this only ever
    /// changes when the filter itself is changed, which is a click rather than a drag.
    /// </remarks>
    private float MoreButtonWidth()
    {
        var label  = _slotFilter?.DisplayName();
        var widest = Math.Max(ImGui.CalcTextSize(MoreLabel).X,
                              label == null ? 0f : ImGui.CalcTextSize(label).X);

        // Text, the frame padding either side, and the square arrow ImGui puts on a combo
        return widest + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight();
    }

    /// <summary>
    /// Height for every control on the filter row, so the text buttons match the icon buttons
    /// standing next to them once the icons are scaled up.
    /// </summary>
    /// <remarks>
    /// Zero while icons are off, which is ImGui's "size to the label" — the row's original height,
    /// and one code path rather than a branch at every button.
    /// </remarks>
    private static float FilterRowHeight => Plugin.SlotIcons.Enabled
        ? Plugin.SlotIcons.ScaledRowSize + ImGui.GetStyle().FramePadding.Y * 2
        : 0f;

    /// <summary>Size for a text button on the filter row: its own width, the row's height.</summary>
    private static Vector2 FilterRowButton(string label) =>
        new(UiLayout.ButtonWidth(label), FilterRowHeight);

    /// <summary>
    /// Width one slot filter button occupies, which depends on whether it is drawn as an icon.
    /// Must track <see cref="DrawFilterButton"/>, or the bar will measure itself wrongly.
    /// </summary>
    private static float FilterButtonWidth(EquipSlot slot)
    {
        if (Plugin.SlotIcons.Enabled &&
            (Plugin.SlotIcons.HasCustomIcon(slot) ||
             Plugin.SlotIcons.TryGetGameIcon(slot, out _) || Plugin.SlotIcons.TryGetFontIcon(slot, out _)))
            return Plugin.SlotIcons.ScaledRowSize + ImGui.GetStyle().FramePadding.X * 2;

        return UiLayout.ButtonWidth(slot.DisplayName());
    }

    /// <summary>
    /// Search box and sort combo, right-aligned on the slot filter row. Falls back to its own
    /// line when the slot buttons leave too little room, rather than overlapping them.
    /// </summary>
    private void DrawSearchAndSort()
    {
        var style = ImGui.GetStyle();

        // Right edge of the last slot button, in window-local coordinates
        var lastBtnRight = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;

        var contentRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var startX       = contentRight - SearchBlockWidth();

        // Only share the row if the controls clear the last slot button
        var sharingRow = startX > lastBtnRight + style.ItemSpacing.X;
        if (sharingRow)
            ImGui.SameLine(startX);

        // Boxes take no explicit height, so they are padded up to the buttons' — but only while
        // actually beside them. Wrapped onto a line of their own there is nothing to match, and
        // stretching them there would just waste the height.
        var extraY = sharingRow
            ? Math.Max(0f, (FilterRowHeight - ImGui.GetFrameHeight()) / 2f)
            : 0f;
        if (extraY > 0f)
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(style.FramePadding.X, style.FramePadding.Y + extraY));

        // The block is a fixed width, and the search box takes whatever the clear button is not
        // using. So the box gives up exactly the button's width as it appears and takes it back as
        // it goes: the sort box never moves, the row never reflows, and there is no gap left sitting
        // in the middle of the row when the box is empty.
        var hasSearch = !string.IsNullOrEmpty(_search);
        var clearW    = ClearButtonWidth() + style.ItemSpacing.X;

        // Beside the search box rather than on a row of its own: it narrows the grid the same way
        // search does, and a whole line with a label for one dropdown was more furniture than the
        // control is worth
        if (_frameStyles.Count > 0)
        {
            DrawStyleCombo();
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(hasSearch ? SearchBoxWidth : SearchBoxWidth + clearW);
        ImGui.InputTextWithHint("##search", "Search name, tag, mod, note…", ref _search, 128);

        if (hasSearch)
        {
            ImGui.SameLine();

            // Deliberately unsized. While this row is shared, FramePadding is already pushed to make
            // plain widgets match the icon buttons, so an auto-sized button is exactly the right
            // height — whereas asking for FilterRowHeight here would add that padding a second time
            // and draw a button taller than the boxes either side of it.
            if (ImGui.Button("×##clearsearch")) _search = string.Empty;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear search.");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(SortBoxWidth);

        // Order must match the ItemSortMode enum values
        var sortLabels = new[] { "Name (A–Z)", "Name (Z–A)", "Newest first", "Oldest first" };
        var sortIdx    = (int)_config.SortMode;
        var sortChanged = ImGui.Combo("##sort", ref sortIdx, sortLabels, sortLabels.Length);

        // This overload draws its own popup inside the call, so the four sort options inherit the
        // padding and sit a little further apart than usual. Not worth splitting into BeginCombo and
        // a hand-written list to avoid — the More dropdown does that only because it already had to.
        if (extraY > 0f) ImGui.PopStyleVar();

        if (sortChanged)
        {
            _config.SortMode = (ItemSortMode)sortIdx;
            _config.Save();
        }
    }

    /// <summary>
    /// Whether an item carries any of a set of tags, counting a tag nested under one of them.
    /// </summary>
    /// <remarks>
    /// Filtering on <c>Shoes</c> has always included <c>Shoes/Boots</c>, and the same rule is what
    /// lets a style stay one entry on the row while anything filed below it still answers to it.
    /// </remarks>
    private static bool HasAnyTag(WardrobeItem item, HashSet<string> filter) =>
        HasAnyTag(item.Tags, filter);

    private static bool HasAnyTag(List<string> tags, HashSet<string> filter) =>
        tags.Any(t => filter.Any(f =>
            string.Equals(t, f, StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Whether an outfit passes the tag and style filters, which it does when nothing is filtered.
    /// </summary>
    /// <remarks>
    /// The same rules the item grid uses, so a tag scheme means one thing across the plugin. Tags
    /// and styles are separate conditions rather than one pool: ticking a style and a tag asks for
    /// outfits that are both, which is what picking two different kinds of label reads as.
    /// </remarks>
    private bool OutfitMatchesTagFilters(Outfit outfit) =>
        (_tagFilter.Count   == 0 || HasAnyTag(outfit.Tags, _tagFilter)) &&
        (_styleFilter.Count == 0 || HasAnyTag(outfit.Tags, _styleFilter));

    private void DrawFilterButton(string label, EquipSlot? slot)
    {
        var active = _slotFilter == slot;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
        }

        // "All" has no slot, so it always stays as text
        var useIcon = slot.HasValue && Plugin.SlotIcons.Enabled;

        bool clicked, hovered;
        if (useIcon)
        {
            clicked = DrawIconButton(slot!.Value, out hovered);
        }
        else
        {
            clicked = ImGui.Button(label, FilterRowButton(label));
            hovered = ImGui.IsItemHovered();
        }

        if (clicked) _slotFilter = slot;
        if (active) ImGui.PopStyleColor(2);

        if (!hovered) return;

        // The name is no longer on screen once icons are on, so the tooltip carries it, along with
        // the slot's note where it has one
        var hint = slot?.Hint();
        if (useIcon)      ImGui.SetTooltip(hint == null ? label : $"{label}\n\n{hint}");
        else if (hint is { } h) ImGui.SetTooltip(h);
    }

    /// <summary>
    /// Button whose face is a slot icon rather than text. Hover state is captured from the button
    /// itself, since the icon drawn over it becomes the "last item" afterwards.
    /// </summary>
    private static bool DrawIconButton(EquipSlot slot, out bool hovered)
    {
        var style = ImGui.GetStyle();
        var size  = Plugin.SlotIcons.ScaledRowSize;
        var btn   = new Vector2(size + style.FramePadding.X * 2, size + style.FramePadding.Y * 2);

        ImGui.PushID((int)slot);

        bool clicked;
        if (Plugin.SlotIcons.TryGetCustomIcon(slot, out var custom) && custom != null)
        {
            // Centre-cropped rather than stretched — a supplied image will not always be square
            clicked = ImageDraw.SquareButton("customslot", custom, size);
        }
        else if (Plugin.SlotIcons.TryGetGameIcon(slot, out var handle))
        {
            // ImageButton centres the image in the frame itself — no overlay needed.
            // Uniqueness comes from the PushID above, since this overload takes no string id.
            clicked = ImGui.ImageButton(handle, new Vector2(size, size));
        }
        else if (Plugin.SlotIcons.TryGetFontIcon(slot, out var glyph))
        {
            // The glyph is the button's own label, so ImGui centres it and the frame is sized
            // identically to the image case
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
                clicked = ImGui.Button(glyph, btn);
        }
        else
        {
            // A slot with neither a game icon nor a glyph — still has to match the row's height
            clicked = ImGui.Button(slot.DisplayName(), FilterRowButton(slot.DisplayName()));
        }

        hovered = ImGui.IsItemHovered();
        ImGui.PopID();
        return clicked;
    }

    // ── Tag filter tree ───────────────────────────────────────────────────────

    private void DrawTagTree(TagNode node)
    {
        // Deleting mutates DefinedTags, which the tree being walked was built from
        string? deleted = null;

        foreach (var (_, child) in node.Children)
        {
            // The box takes the row while it is open, so the tag stays where it is in the tree
            if (_renameTagPath == child.FullPath)
            {
                DrawTagRenameRow(child.FullPath);
                continue;
            }

            var active = _tagFilter.Contains(child.FullPath);
            var custom = _config.TagColoursEnabled ? TagTree.Colour(_config, child.FullPath) : null;
            var tinted = custom.HasValue;

            // A chosen colour replaces both defaults, shaded for the state so filtering and
            // unused still read at a glance
            if (tinted)            ImGui.PushStyleColor(ImGuiCol.Text, TagTree.Shade(custom!.Value, active, child.InUse));
            else if (active)       ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.78f, 0.58f, 1f, 1f));
            else if (!child.InUse) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.45f, 0.52f, 1f));

            var isLeaf = child.Children.Count == 0;
            var flags  = ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isLeaf)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            else
                flags |= ImGuiTreeNodeFlags.OpenOnArrow; // expand/collapse only via the arrow

            var open = ImGui.TreeNodeEx($"##{child.FullPath}", flags, child.Segment);
            if (tinted || active || !child.InUse) ImGui.PopStyleColor();

            var colourable = _config.TagColoursEnabled;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(child.InUse
                    ? $"{child.FullPath}\n\nRight-click to rename{(colourable ? " or colour" : "")} it."
                    : $"{child.FullPath}\n\nNo items have this tag yet.\n" +
                      $"Right-click to rename{(colourable ? ", colour" : "")} or delete it.");

            // Every tag, not only the unused ones: renaming and colouring are the reasons to
            // right-click a tag something is actually wearing, and renaming is the whole point —
            // a typo used to mean re-tagging everything by hand. Deleting stays limited to tags
            // nothing carries, so it can never take a tag off an item.
            if (ImGui.BeginPopupContextItem($"##tagctx_{child.FullPath}"))
            {
                if (ImGui.MenuItem($"Rename '{child.Segment}'"))
                {
                    _renameTagPath  = child.FullPath;
                    _renameTagBuf   = child.Segment;
                    _renameTagError = string.Empty;
                }

                if (colourable)
                {
                    ImGui.Separator();
                    DrawTagColourMenu(child.FullPath, "tag");
                }

                if (!child.InUse)
                {
                    ImGui.Separator();
                    if (UiLayout.DeleteMenuItem($"Delete '{child.Segment}'"))
                        deleted = child.FullPath;
                }

                ImGui.EndPopup();
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                // For branch nodes, only toggle the filter when the label was clicked,
                // not the expand arrow (arrow occupies [0, GetTreeNodeToLabelSpacing()) px).
                var clickX       = ImGui.GetMousePos().X - ImGui.GetItemRectMin().X;
                var labelClicked = isLeaf || clickX >= ImGui.GetTreeNodeToLabelSpacing();
                if (labelClicked)
                {
                    if (!_tagFilter.Remove(child.FullPath))
                        _tagFilter.Add(child.FullPath);
                }
            }

            if (open && !isLeaf)
            {
                DrawTagTree(child);
                ImGui.TreePop();
            }
        }

        if (deleted != null) DeleteDefinedTag(deleted);
    }

    /// <summary>
    /// The colour picker shown when a tag or style is right-clicked.
    /// </summary>
    /// <remarks>
    /// Behind a submenu rather than laid out in the context menu itself: the picker is several
    /// times the height of the menu items around it, and a right-click aimed at Rename or Delete
    /// should not have to scroll past a colour square to reach them.
    /// <para>
    /// It stays a submenu rather than a window of its own so choosing a colour is still one gesture
    /// from the thing being coloured. The picker is fine inside a popup — only <c>MenuItem</c>
    /// closes one on click, so dragging around the square keeps the menu up and the tag underneath
    /// recolours as it goes.
    /// </para>
    /// <para>
    /// Shown in 0-255 rather than ImGui's default 0-1 floats: this is a colour someone is matching
    /// to something they already have, and the numbers people have for colours are the ones on the
    /// side of every other colour picker.
    /// </para>
    /// </remarks>
    private void DrawTagColourMenu(string path, string noun)
    {
        var existing = TagTree.Colour(_config, path);

        if (!ImGui.BeginMenu($"Set {noun} colour"))
        {
            // Shown on the closed entry, so a colour can be seen without opening anything
            if (existing is { } set)
            {
                ImGui.SameLine();
                var pos  = ImGui.GetCursorScreenPos();
                var size = ImGui.GetTextLineHeight();
                ImGui.GetWindowDrawList().AddRectFilled(pos, new Vector2(pos.X + size, pos.Y + size),
                    ImGui.ColorConvertFloat4ToU32(set));
                ImGui.Dummy(new Vector2(size, size));
            }
            return;
        }

        var rgb = existing is { } c
            ? new Vector3(c.X, c.Y, c.Z)
            : new Vector3(0.78f, 0.58f, 1f); // the colour tags already filter in, as a starting point

        ImGui.SetNextItemWidth(UiScale.S(180));
        if (ImGui.ColorPicker3($"##tagcolour_{path}", ref rgb,
                ImGuiColorEditFlags.DisplayRgb | ImGuiColorEditFlags.Uint8 |
                ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview))
            TagTree.SetColour(_config, path, rgb);

        if (existing != null)
        {
            ImGui.Spacing();
            if (ImGui.MenuItem("Reset colour"))
                TagTree.ClearColour(_config, path);
        }

        ImGui.EndMenu();
    }

    /// <summary>Tag or style being renamed, with its edit buffer and any complaint about it.</summary>
    private string _renameTagPath  = string.Empty;
    private string _renameTagBuf   = string.Empty;
    private string _renameTagError = string.Empty;

    /// <summary>
    /// The rename box, drawn in place of a tag or style while it is being renamed.
    /// </summary>
    /// <remarks>
    /// In place rather than in a dialog, matching how a camera preset renames, so the thing being
    /// renamed stays where it is and the list does not jump about under a modal.
    /// </remarks>
    private void DrawTagRenameRow(string path)
    {
        ImGui.SetNextItemWidth(UiScale.S(160));

        var entered = ImGui.InputText($"##renametag_{path}", ref _renameTagBuf, 64,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        ImGui.SameLine();
        if (ImGui.SmallButton($"Save##renametag_{path}") || entered)
            RenameTag(path, _renameTagBuf);

        ImGui.SameLine();
        if (ImGui.SmallButton($"Cancel##renametag_{path}")) CancelTagRename();

        if (!string.IsNullOrEmpty(_renameTagError))
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.35f, 1f), _renameTagError);
    }

    private void CancelTagRename()
    {
        _renameTagPath  = string.Empty;
        _renameTagBuf   = string.Empty;
        _renameTagError = string.Empty;
    }

    /// <summary>
    /// Renames a tag or style everywhere it appears, taking anything nested under it along.
    /// </summary>
    /// <remarks>
    /// A tag is not an object anywhere — it is the same string repeated on every item that carries
    /// it, in the pre-made list, in the filters and in the colour map — so renaming one means
    /// rewriting all of those in step. Only the last segment changes; the parent is kept, which is
    /// what makes the same code rename a style, since a style is a tag under <c>Style/</c>.
    /// <para>
    /// A name already in use is refused rather than merged. Merging two tags is a bigger thing than
    /// a typo fix, it cannot be undone, and someone who wanted it can rename to a free name and
    /// re-tag deliberately.
    /// </para>
    /// </remarks>
    private void RenameTag(string oldPath, string typed)
    {
        var segment = NormaliseTag(typed);
        if (segment.Length == 0)
        {
            _renameTagError = "Give it a name.";
            return;
        }

        var cut     = oldPath.LastIndexOf('/');
        var parent  = cut < 0 ? string.Empty : oldPath[..cut];
        var newPath = parent.Length == 0 ? segment : $"{parent}/{segment}";

        if (newPath.Equals(oldPath, StringComparison.Ordinal))
        {
            CancelTagRename();
            return;
        }

        // A pure change of case is a rename of itself, not a collision with itself
        var renamingCaseOnly = newPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase);
        if (!renamingCaseOnly &&
            _config.AllTags().Any(t => t.Equals(newPath, StringComparison.OrdinalIgnoreCase)))
        {
            _renameTagError = $"'{segment}' already exists.";
            return;
        }

        bool Matches(string tag) =>
            tag.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith($"{oldPath}/", StringComparison.OrdinalIgnoreCase);

        string Rewrite(string tag) => newPath + tag[oldPath.Length..];

        var touched = 0;

        foreach (var item in _config.WardrobeItems)
            for (var i = 0; i < item.Tags.Count; i++)
                if (Matches(item.Tags[i]))
                {
                    item.Tags[i] = Rewrite(item.Tags[i]);
                    touched++;
                }

        for (var i = 0; i < _config.DefinedTags.Count; i++)
            if (Matches(_config.DefinedTags[i]))
                _config.DefinedTags[i] = Rewrite(_config.DefinedTags[i]);

        // Filters and colours are keyed by the old path and would otherwise point at nothing
        RewriteSet(_tagFilter);
        RewriteSet(_styleFilter);

        foreach (var key in _config.TagColours.Keys.Where(Matches).ToList())
        {
            var colour = _config.TagColours[key];
            _config.TagColours.Remove(key);
            _config.TagColours[Rewrite(key)] = colour;
        }

        _config.Save();
        _log.Information($"[Wardrobe] Renamed tag '{oldPath}' to '{newPath}' on {touched} item tag(s)");
        CancelTagRename();
        return;

        void RewriteSet(HashSet<string> filters)
        {
            foreach (var f in filters.Where(Matches).ToList())
            {
                filters.Remove(f);
                filters.Add(Rewrite(f));
            }
        }
    }

    /// <summary>
    /// Removes a pre-made tag, along with any pre-made tags nested under it.
    /// </summary>
    /// <remarks>
    /// Only ever reached from a branch nothing is tagged with, so nothing below it can be in use
    /// either and no item loses a tag. The prefix sweep is what makes deleting a parent mean what it
    /// looks like it means, rather than leaving orphaned children behind with no parent to find them
    /// under.
    /// </remarks>
    private void DeleteDefinedTag(string path)
    {
        var removed = _config.DefinedTags.RemoveAll(t =>
            t.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith($"{path}/", StringComparison.OrdinalIgnoreCase));

        if (removed == 0) return;

        _tagFilter.RemoveWhere(f =>
            f.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith($"{path}/", StringComparison.OrdinalIgnoreCase));

        // Styles are deleted through here too, and a filter left pointing at one that no longer
        // exists would quietly empty the grid
        _styleFilter.RemoveWhere(f =>
            f.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith($"{path}/", StringComparison.OrdinalIgnoreCase));

        TagTree.ClearColours(_config, path);

        _config.Save();
        _log.Information($"[Wardrobe] Deleted {removed} pre-made tag(s) under '{path}'");
    }

    private void DrawTagFilter()
    {
        // Header first, so the panel stays closable even when there are no tags to show
        if (DrawPanelHeader("Tags"))
        {
            _showTags = false;
            return;
        }

        ImGui.Spacing();
        DrawStylesSection();

        ImGui.Spacing();
        DrawTagsSection();

        ImGui.Spacing();
    }

    /// <summary>
    /// Where tags are made, deleted and filtered on.
    /// </summary>
    /// <remarks>
    /// Making a tag lives inside this header rather than above it, so the box that makes tags and
    /// the list they appear in are one thing. Loose at the top of the panel it read as unrelated to
    /// either group — the whole Styles section sat between typing a tag and seeing where it went.
    /// Styles have always worked this way; this is tags matching them.
    /// </remarks>
    private void DrawTagsSection()
    {
        // Built once and reused: the tree decides both whether there is anything to show and what
        // gets drawn, and building it twice to answer the same question was wasted work
        var tree = TagTree.Build(_config, includeStyles: false);

        var header = _tagFilter.Count > 0
            ? $"Tags  ·  {_tagFilter.Count} active###TagsHeader"
            : "Tags###TagsHeader";

        if (!ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();
        DrawNewTagRow();

        // Styles are tags, so the panel is never truly empty once one exists — but this tree can
        // still be, and says so rather than leaving a gap under the box that fills it
        if (tree.Children.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Nothing here yet. Make one above, or add them while editing an item.");
            ImGui.Spacing();
            return;
        }

        ImGui.Spacing();
        if (_tagFilter.Count > 0)
        {
            if (ImGui.SmallButton("× Clear")) _tagFilter.Clear();
            ImGui.Separator();
        }

        DrawTagTree(tree);
        ImGui.Spacing();
    }

    /// <summary>
    /// A starter scheme, offered once to a wardrobe that has no styles of its own yet.
    /// </summary>
    /// <remarks>
    /// Broad and few on purpose: enough that the row has something in it and the idea explains
    /// itself, not so many that anyone taking the offer inherits a scheme they then have to prune.
    /// </remarks>
    private static readonly string[] StarterStyles =
    {
        "Casual", "Comfy", "Cute", "Elegant", "Formal",
        "Beach", "Sleepy", "Tech", "Fantasy", "Modern",
    };

    /// <summary>
    /// Where styles are made, deleted and filtered on from the Tags panel.
    /// </summary>
    /// <remarks>
    /// Above the tag tree rather than inside it: the tree no longer contains them, and a style is
    /// the coarser cut of the two — what mood something is, before what kind of thing it is.
    /// </remarks>
    private void DrawStylesSection()
    {
        var styles = TagTree.Styles(_config);

        var header = _styleFilter.Count > 0
            ? $"Styles  ·  {_styleFilter.Count} active###StylesHeader"
            : "Styles###StylesHeader";

        if (!ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-UiLayout.ButtonWidth(" Make Style ") - ImGui.GetStyle().ItemSpacing.X);
        var entered = ImGui.InputTextWithHint("##newstyle", "new style", ref _newStyle, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var name  = NormaliseStyle(_newStyle);
        var empty = name.Length == 0;

        if (empty) ImGui.BeginDisabled();
        if (ImGui.Button(" Make Style ") || (entered && !empty)) MakeStyle(name);
        if (empty) ImGui.EndDisabled();

        ImGui.TextDisabled("A mood or theme that cuts across slots — Casual, Beach, Comfy. Shown as " +
                           "a dropdown\nunder the filters, and set on an item in its edit panel.");

        if (!string.IsNullOrEmpty(_newStyleStatus))
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.6f, 1f), _newStyleStatus);

        DrawStarterStylesOffer(styles.Count);

        if (styles.Count == 0)
        {
            ImGui.Spacing();
            return;
        }

        ImGui.Spacing();
        if (_styleFilter.Count > 0)
        {
            if (ImGui.SmallButton("× Clear##clearstylefilter")) _styleFilter.Clear();
            ImGui.Separator();
        }

        // Deleting mutates DefinedTags, which the list being walked was built from
        string? deleted = null;

        for (var i = 0; i < styles.Count; i++)
        {
            var style  = styles[i];
            var active = _styleFilter.Contains(style.FullPath);

            if (_renameTagPath == style.FullPath)
            {
                // On its own line: the box plus its two buttons will not sit in a row of chips
                DrawTagRenameRow(style.FullPath);
                continue;
            }

            if (i > 0) UiLayout.SameLineIfRoomForButton(style.Segment);

            var custom = _config.TagColoursEnabled ? TagTree.Colour(_config, style.FullPath) : null;
            var pushed = 0;

            if (custom is { } c)
            {
                // The chip is its own colour whatever its state, brightened while filtering and
                // dimmed while nothing carries it, so the colour survives every state it can be in
                var fill = TagTree.Shade(c, active, style.InUse);
                ImGui.PushStyleColor(ImGuiCol.Button,        fill);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TagTree.Shade(c, true, style.InUse));
                ImGui.PushStyleColor(ImGuiCol.Text,          TagTree.ReadableOn(fill));
                pushed = 3;
            }
            else if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
                pushed = 2;
            }
            else if (!style.InUse)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.45f, 0.52f, 1f));
                pushed = 1;
            }

            var clicked = ImGui.SmallButton($"{style.Segment}##style_{style.FullPath}");

            if (pushed > 0) ImGui.PopStyleColor(pushed);

            if (clicked && !_styleFilter.Remove(style.FullPath))
                _styleFilter.Add(style.FullPath);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(style.InUse
                    ? $"Right-click to rename{(_config.TagColoursEnabled ? " or colour" : "")} it."
                    : "No items have this style yet.\n" +
                      $"Right-click to rename{(_config.TagColoursEnabled ? ", colour" : "")} or delete it.");

            // Same rules as the tag tree: renaming and colouring are open to any style, but only one
            // nothing carries can be deleted, so removing one can never take a style off an item
            if (ImGui.BeginPopupContextItem($"##stylectx_{style.FullPath}"))
            {
                if (ImGui.MenuItem($"Rename '{style.Segment}'"))
                {
                    _renameTagPath  = style.FullPath;
                    _renameTagBuf   = style.Segment;
                    _renameTagError = string.Empty;
                }

                if (_config.TagColoursEnabled)
                {
                    ImGui.Separator();
                    DrawTagColourMenu(style.FullPath, "style");
                }

                if (!style.InUse)
                {
                    ImGui.Separator();
                    if (UiLayout.DeleteMenuItem($"Delete '{style.Segment}'")) deleted = style.FullPath;
                }

                ImGui.EndPopup();
            }
        }

        if (deleted != null) DeleteDefinedTag(deleted);
        ImGui.Spacing();
    }

    /// <summary>
    /// The one-time offer of <see cref="StarterStyles"/>.
    /// </summary>
    /// <remarks>
    /// Only while the wardrobe has no styles at all, and only until it is answered either way, so
    /// anyone with a scheme of their own can say no once and never see it again. Declining records
    /// the same flag as accepting: the point is that the question is asked once, not that it is
    /// asked until it gets the answer it wants.
    /// </remarks>
    private void DrawStarterStylesOffer(int existingStyles)
    {
        if (_config.StarterStylesOffered || existingStyles > 0) return;

        ImGui.Spacing();
        ImGui.TextDisabled("Nothing here yet. Start from a ready-made set, or make your own above:");
        ImGui.TextDisabled($"  {string.Join(", ", StarterStyles)}");

        if (ImGui.SmallButton("Add these"))
        {
            foreach (var style in StarterStyles)
                _config.DefinedTags.Add(TagTree.StylePath(style));

            _config.StarterStylesOffered = true;
            _config.Save();
            _log.Information($"[Wardrobe] Added {StarterStyles.Length} starter styles");
            _newStyleStatus = $"Added {StarterStyles.Length} styles. Delete any you do not want.";
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("No thanks"))
        {
            _config.StarterStylesOffered = true;
            _config.Save();
        }
    }

    /// <summary>
    /// Puts a typed style name into the tag it is stored as.
    /// </summary>
    /// <remarks>
    /// Tolerates the prefix being typed in as well as left off, so someone who has noticed how
    /// styles are stored does not end up with <c>Style/Style/Casual</c> for using that knowledge.
    /// </remarks>
    private static string NormaliseStyle(string raw)
    {
        var name = NormaliseTag(raw);
        if (TagTree.IsStyle(name))
            name = NormaliseTag(name[(TagTree.StyleRoot.Length + 1)..]);

        return name.Length == 0 ? string.Empty : TagTree.StylePath(name);
    }

    private void MakeStyle(string path)
    {
        // Against every known tag, as with tags: a style some item already carries needs no entry
        if (_config.AllTags().Any(t => t.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            _newStyleStatus = $"'{path[(TagTree.StyleRoot.Length + 1)..]}' already exists.";
            return;
        }

        _config.DefinedTags.Add(path);
        _config.Save();
        _log.Information($"[Wardrobe] Made style '{path}'");

        _newStyle       = string.Empty;
        _newStyleStatus = $"Made '{path[(TagTree.StyleRoot.Length + 1)..]}'.";
    }

    /// <summary>
    /// Creates a tag before any item has it, so a scheme can be laid out in one go and then applied.
    /// </summary>
    /// <remarks>
    /// Tags otherwise only exist as a side effect of typing one into an item, which means the tag
    /// list can only be built one item at a time and a typo becomes a second tag rather than an
    /// error. Made here, they show up dimmed in the tree directly below and in every tag picker,
    /// ready to be applied — most usefully from Select → Edit Selected, which tags a whole batch at
    /// once. Drawn inside the Tags header by <see cref="DrawTagsSection"/>, which is what makes
    /// "directly below" true.
    /// </remarks>
    private void DrawNewTagRow()
    {
        ImGui.SetNextItemWidth(-UiLayout.ButtonWidth(" Make Tag ") - ImGui.GetStyle().ItemSpacing.X);
        var entered = ImGui.InputTextWithHint("##newtag", "new tag", ref _newTag, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var name  = NormaliseTag(_newTag);
        var empty = name.Length == 0;

        if (empty) ImGui.BeginDisabled();
        if (ImGui.Button(" Make Tag ") || (entered && !empty)) MakeTag(name);
        if (empty) ImGui.EndDisabled();

        ImGui.TextDisabled("Use / for sub-tags, e.g. Shoes/Boots. Tags with no items yet are shown " +
                           "greyed out.");

        if (!string.IsNullOrEmpty(_newTagStatus))
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.6f, 1f), _newTagStatus);
    }

    /// <summary>
    /// Puts a typed tag into the exact form the tag tree builds its paths in.
    /// </summary>
    /// <remarks>
    /// The tree trims each segment and drops empty ones, so <c>"Shoes / Boots"</c> becomes the path
    /// <c>Shoes/Boots</c>. Storing the raw text would leave the stored name and the path that
    /// represents it different strings, and deleting the tag — which matches on the path — would
    /// quietly do nothing.
    /// </remarks>
    private static string NormaliseTag(string raw) =>
        string.Join('/', raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void MakeTag(string name)
    {
        // Compared against every known tag, not just the pre-made ones: a tag some item already
        // carries needs no entry here, and adding one would be a duplicate that does nothing
        if (_config.AllTags().Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _newTagStatus = $"'{name}' already exists.";
            return;
        }

        _config.DefinedTags.Add(name);
        _config.Save();
        _log.Information($"[Wardrobe] Made tag '{name}'");

        _newTag       = string.Empty;
        _newTagStatus = $"Made '{name}'.";
    }

    // ── Item grid ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the item is tracked as worn, or the last scan found its mods enabled in Penumbra.
    /// </summary>
    /// <remarks>
    /// The two states the cards mark with a star and a dot respectively. Both count as "on": the
    /// scan-detected ones are exactly the items whose mods are live but which the wardrobe was not
    /// tracking, which is the case worth being able to pick out of the grid.
    /// </remarks>
    private bool IsWornOrDetected(WardrobeItem item) =>
        _wardrobe.IsItemWorn(item) || _detectedWorn.Contains(item.Id);

    private void DrawGrid()
    {
        IEnumerable<WardrobeItem> query = _config.WardrobeItems;

        // Animations, VFX and mounts are hidden entirely rather than merely unfilterable while the mode
        // is off — with no filter button for them they would otherwise be stuck in every view.
        if (!_config.ModCategoriesEnabled)
            query = query.Where(x => !x.Slot.IsModCategory());

        if (_favoritesOnly)
            query = query.Where(x => x.IsFavorite);
        if (_variantsOnly)
        {
            // Both halves of a group: a variant on its own would be an odd thing to list without
            // the item it is a variant of, and folding then has nothing to fold it into
            var hasVariants = _config.WardrobeItems
                .Where(x => x.VariantOfId.HasValue)
                .Select(x => x.VariantOfId!.Value)
                .ToHashSet();
            query = query.Where(x => x.VariantOfId.HasValue || hasVariants.Contains(x.Id));
        }
        if (_wornOnly)
            query = query.Where(IsWornOrDetected);
        if (_slotFilter != null)
            query = query.Where(x => x.Slot == _slotFilter);
        if (_tagFilter.Count > 0)
            query = query.Where(x => HasAnyTag(x, _tagFilter));

        // A second clause rather than more of the first: within either set the matches widen what is
        // shown, but a style and a tag together narrow it — "boots" and "casual" means casual boots
        if (_styleFilter.Count > 0)
            query = query.Where(x => HasAnyTag(x, _styleFilter));

        if (!string.IsNullOrWhiteSpace(_search))
        {
            var q = _search.Trim();
            query = query.Where(x =>
                x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                x.Mods.Any(m => m.ModName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (x.GlamourerItemName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                // Notes are searched too, so a creator's name written in there finds the item
                (x.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Secondary key keeps ordering stable when several items share a timestamp
        // (a multi-slot import creates them within the same tick) or the same name.
        query = _config.SortMode switch
        {
            ItemSortMode.NameDesc      => query.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                                               .ThenByDescending(x => x.DateAdded),
            ItemSortMode.DateAddedDesc => query.OrderByDescending(x => x.DateAdded)
                                               .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            ItemSortMode.DateAddedAsc  => query.OrderBy(x => x.DateAdded)
                                               .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _                          => query.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                                               .ThenBy(x => x.DateAdded),
        };

        var items = FoldVariants(query.ToList());
        _visibleCount = items.Count;

        // What Select All acts on. Recorded even when select mode is off, so turning it on and
        // pressing Select All immediately does not act on a stale list from whenever it was last on.
        _lastVisibleIds.Clear();
        _lastVisibleIds.AddRange(items.Select(i => i.Id));

        // Deleted items must not linger in the selection and reappear in a later bulk action.
        // Only while selecting, and against a set rather than a scan per id: this runs every frame.
        if (_selectMode && _selected.Count > 0)
        {
            var live = _config.WardrobeItems.Select(i => i.Id).ToHashSet();
            _selected.RemoveWhere(id => !live.Contains(id));
        }

        if (items.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(_config.WardrobeItems.Count == 0
                ? "No items yet. Click '+ Import from Mod' to add your first item."
                : !string.IsNullOrWhiteSpace(_search)
                    ? $"No items match \"{_search.Trim()}\"."
                    : _favoritesOnly
                        ? "No favourites yet. Click the ♥ on an item to add one."
                        : "No items match the current filter.");
            return;
        }

        var avail   = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((avail + CardPad) / (CardWidth + CardPad)));
        Guid? toDelete = null;

        // Only draw the rows actually on screen. Cards are a fixed size, so the visible range is
        // just arithmetic, and empty spacers above and below keep the scrollbar honest. Drawing
        // every card regardless of scroll position is what made a 200-item wardrobe expensive.
        var rowHeight = CardHeight + ImGui.GetStyle().ItemSpacing.Y;
        var totalRows = (items.Count + columns - 1) / columns;
        var scrollY   = ImGui.GetScrollY();
        var viewH     = ImGui.GetWindowHeight();

        // One row of overscan each way, so a partially-scrolled row is never clipped mid-draw
        var firstRow = Math.Max(0, (int)(scrollY / rowHeight) - 1);
        var lastRow  = Math.Min(totalRows - 1, (int)((scrollY + viewH) / rowHeight) + 1);

        if (firstRow > 0)
            ImGui.Dummy(new Vector2(1, firstRow * rowHeight));

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var c = 0; c < columns; c++)
            {
                var idx = row * columns + c;
                if (idx >= items.Count) break;
                if (c > 0) ImGui.SameLine();
                DrawCard(items[idx], ref toDelete);
            }
        }

        var rowsBelow = totalRows - 1 - lastRow;
        if (rowsBelow > 0)
            ImGui.Dummy(new Vector2(1, rowsBelow * rowHeight));

        if (toDelete.HasValue)
        {
            var idx = _config.WardrobeItems.FindIndex(x => x.Id == toDelete.Value);
            if (idx >= 0)
            {
                var removed = _config.WardrobeItems[idx];
                if (_wardrobe.IsItemWorn(removed))
                    _wardrobe.UnwearItem(removed, save: false);
                _wardrobe.ForgetLinksTo(removed.Id);
                _wardrobe.ReparentVariantsOf(removed);
                _config.WardrobeItems.RemoveAt(idx);
                _imageCache.Remove(toDelete.Value);
                _config.Save();
            }
        }
    }

    /// <summary>
    /// Items predating the DateAdded field are backfilled to a year-2000 epoch purely to preserve
    /// their order, so their timestamp is not a real import date and is not shown as one.
    /// </summary>
    private static string DateAddedLabel(WardrobeItem item) =>
        item.DateAdded.Year <= 2000
            ? "Added before dates were tracked"
            : $"Added {item.DateAdded.ToLocalTime():d MMM yyyy}";

    /// <summary>Shortens text for a tooltip, so a long note cannot cover the window.</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// Heart toggle, right-aligned on the card's name row. A single glyph is used for both states
    /// (tinted when set, dimmed when not) so this does not depend on an outline glyph being present
    /// in the font. The filled star is already spoken for by the worn indicator.
    /// </summary>
    private void DrawFavoriteToggle(WardrobeItem item)
    {
        const string glyph = "♥";
        var style = ImGui.GetStyle();
        var width = ImGui.CalcTextSize(glyph).X + style.FramePadding.X * 2;

        ImGui.SameLine();
        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(1f, 1f, 1f, 0.20f));
        ImGui.PushStyleColor(ImGuiCol.Text, item.IsFavorite
            ? new Vector4(1f, 0.35f, 0.5f, 1f)
            : new Vector4(0.45f, 0.45f, 0.5f, 1f));

        if (ImGui.SmallButton($"{glyph}##fav"))
        {
            item.IsFavorite = !item.IsFavorite;
            _config.Save();
        }

        ImGui.PopStyleColor(4);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(item.IsFavorite ? "Remove from favourites" : "Add to favourites");
    }

    /// <summary>
    /// The colour of the coloured style an item carries, or null if it has none.
    /// </summary>
    /// <remarks>
    /// A style rather than any tag: a style is already the one label an item wears at most a couple
    /// of, and is meant to describe the whole piece — which is what a whole card can stand for. Tags
    /// are many and specific, and a card tinted by whichever of eight tags happened to sort first
    /// would be colour without meaning.
    /// <para>
    /// Alphabetical when an item carries more than one coloured style, so the grid does not reshuffle
    /// its colours when a tag list is reordered. Runs per card per frame, hence the early exit before
    /// any string work.
    /// </para>
    /// </remarks>
    private Vector4? StyleTint(WardrobeItem item) => StyleTint(item.Tags);

    /// <param name="tags">An item's or an outfit's tags — both are tinted by the same rule.</param>
    private Vector4? StyleTint(List<string> tags)
    {
        if (!_config.TagColoursEnabled || _config.TagColours.Count == 0) return null;

        Vector4? colour = null;
        string?  best   = null;

        foreach (var tag in tags)
        {
            if (!TagTree.IsStyle(tag)) continue;
            if (TagTree.Colour(_config, tag) is not { } c) continue;
            if (best != null && string.CompareOrdinal(tag, best) >= 0) continue;

            best   = tag;
            colour = c;
        }

        return colour;
    }

    private void DrawCard(WardrobeItem item, ref Guid? pendingDelete)
    {
        var worn = _wardrobe.IsItemWorn(item);

        var background = new Vector4(0.11f, 0.11f, 0.13f, 1f);
        var border     = new Vector4(0.28f, 0.28f, 0.33f, 1f);

        // A style's colour tints the card it is on, mixed into the usual card colour rather than
        // replacing it. Worn keeps its gold untouched — that has to stay unmistakable, and a card
        // that is both worn and styled would otherwise have two things competing to say so.
        if (!worn && StyleTint(item) is { } tint)
        {
            background = TagTree.Blend(background, tint, 0.3f);
            border     = TagTree.Blend(border,     tint, 0.75f);
        }

        ImGui.PushID(item.Id.ToString());
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            worn ? new Vector4(0.22f, 0.18f, 0.04f, 1f) : background);
        ImGui.PushStyleColor(ImGuiCol.Border,
            worn ? new Vector4(1f, 0.85f, 0.25f, 1f) : border);

        ImGui.BeginChild($"##card_{item.Id}", new Vector2(CardWidth, CardHeight),
            true, ImGuiWindowFlags.NoScrollbar);

        DrawItemImage(item);

        // Name + worn star / detected indicator
        var dispName = CardName(item.Name);
        ImGui.TextUnformatted(dispName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{item.Name}\n{DateAddedLabel(item)}");
        if (worn)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));
            ImGui.TextUnformatted("★");
            ImGui.PopStyleColor();
        }
        else if (_detectedWorn.Contains(item.Id))
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.85f, 1f));
            ImGui.TextUnformatted("◉");
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Detected as worn (mods are enabled in Penumbra)");
        }

        // A pilcrow rather than a pencil or a note glyph: both of those live in the Dingbats block,
        // which the default font does not carry, and would draw as an empty box.
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.68f, 1f));
            ImGui.TextUnformatted("¶");
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{Truncate(item.Notes!, 400)}\n\n" +
                                 "Open Edit to follow any links in here.");
        }

        DrawFavoriteToggle(item);

        // Slot badge + tag indicator
        if (Plugin.SlotIcons.Enabled)
        {
            Plugin.SlotIcons.Draw(item.Slot);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Slot.DisplayName());
            ImGui.SameLine();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.75f, 0.95f, 1f));
        var slotText = Plugin.SlotIcons.Enabled ? string.Empty : item.Slot.DisplayName();
        var badge = item.Mods.Count > 1
            ? (slotText.Length > 0 ? $"{slotText} · {item.Mods.Count} mods" : $"{item.Mods.Count} mods")
            : slotText;
        if (item.Tags.Count > 0) badge += badge.Length > 0 ? " ●" : "●";

        var hasBadge = badge.Length > 0;
        if (hasBadge) ImGui.TextUnformatted(badge);
        ImGui.PopStyleColor();

        // Styles first and without the root they are filed under — on a card it is the name that
        // means something, and "#Style/Casual" reads as a tag scheme leaking out
        if (item.Tags.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("  ",
                item.Tags.Where(TagTree.IsStyle)
                    .Select(t => t[(TagTree.StyleRoot.Length + 1)..])
                    .Concat(item.Tags.Where(t => !TagTree.IsStyle(t)).Select(t => $"#{t}"))));

        // The badge line is kept even when it is empty, so every card stays the same height —
        // whichever of the two filled it, or an empty line when neither did
        if (!DrawVariantToggle(item, sameLine: hasBadge) && !hasBadge)
            ImGui.NewLine();

        // In select mode the tick box takes the button row's place rather than being added below it.
        // Same card height, so entering the mode does not reflow the grid, and the delete button is
        // not on screen to be hit by mistake while clicking through dozens of cards.
        if (_selectMode)
        {
            DrawCardSelector(item);
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            ImGui.PopID();
            return;
        }

        // Buttons. The row is the card's inner width less the gaps between three of them, so the
        // literals scale with everything else or the delete button walks off the edge.
        var deleteW = UiScale.S(22f);
        var editGap = UiScale.S(26f);
        var btnW    = (CardWidth - CardPad * 2 - UiScale.S(6f)) / 2;

        // Customisation mods are not worn or removed — you always have hair. They are applied, and
        // "removing" one just turns the mod back off. The same goes for animations, VFX and mounts,
        // which are not on the character at all.
        var (wearLabel, removeLabel) = item.Slot.ActionLabels();
        var modOnly = item.Slot.IsModOnly();

        // The count goes in the label rather than behind a glyph, so what the button is about to do
        // is legible without hovering it
        var links      = _wardrobe.ResolveLinks(item);
        var linkSuffix = links.Count > 0 ? $" +{links.Count}" : string.Empty;

        if (worn)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
            if (ImGui.Button($"{removeLabel}{linkSuffix}", new Vector2(btnW, 0)))
                _wardrobe.UnwearItemLinked(item);
            ImGui.PopStyleColor(2);
            if (links.Count > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip($"Also takes off:\n{LinkList(links)}");
            else if (modOnly && ImGui.IsItemHovered())
                ImGui.SetTooltip("Turn this mod back off, restoring the default appearance.");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.38f, 0.13f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.55f, 0.18f, 1f));
            if (ImGui.Button($"{wearLabel}{linkSuffix}", new Vector2(btnW, 0)))
                _wardrobe.WearItemLinked(item);
            ImGui.PopStyleColor(2);
            if (links.Count > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip($"Also wears:\n{LinkList(links)}");
        }

        // Right-click reaches the solo action too. The row below is the discoverable way in, but it
        // is the first thing to go when a card runs short of room, and this cannot be crowded out.
        DrawSoloContextMenu(item, worn, links);

        // Scaled down far enough, "Edit" no longer fits its button and ImGui clips it to "Edi".
        // A pencil says the same thing in the room that is left rather than most of a word.
        ImGui.SameLine();
        var editW = btnW - editGap;

        if (ImGui.CalcTextSize("Edit").X + ImGui.GetStyle().FramePadding.X * 2 <= editW)
        {
            if (ImGui.Button("Edit", new Vector2(editW, 0)))
                OpenItemEditor(item);
        }
        else
        {
            bool editClicked;
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
                editClicked = ImGui.Button($"{FontAwesomeIcon.Pen.ToIconString()}##edit_{item.Id}",
                    new Vector2(editW, 0));

            if (editClicked) OpenItemEditor(item);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this item.");
        }

        ImGui.SameLine();
        if (DeleteButton("X", $"Deletes '{item.Name}' from the wardrobe.\n" +
                              "The Penumbra mod itself is not touched.", new Vector2(deleteW, 0)))
            pendingDelete = item.Id;

        DrawSoloRow(item, worn, links);

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopID();
    }

    /// <summary>
    /// The fold control: on an original, how many variants are tucked behind it; on a variant, a way
    /// back to folded without hunting for the original.
    /// </summary>
    /// <remarks>
    /// Shares the badge row rather than taking a row of its own. The action row below is three
    /// buttons across a 180px card with nothing spare, and the badge — a slot name and sometimes a
    /// mod count — leaves most of its line empty. <see cref="UiLayout.SameLineIfRoom"/> drops the
    /// button to the next line on the rare card whose badge is long enough to crowd it.
    /// </remarks>
    /// <param name="sameLine">
    /// Whether the badge drew anything to sit beside. False when the card has no badge text at all,
    /// where the button starts the line instead of joining it.
    /// </param>
    /// <returns>True if a button was drawn, so the caller knows the badge line is filled.</returns>
    private bool DrawVariantToggle(WardrobeItem item, bool sameLine)
    {
        if (!_config.GroupVariants) return false;

        var key = item.VariantOfId?.ToString() ?? item.Id.ToString();

        // A variant only says so, and offers the way back. Everything about the group — the count,
        // what is folded — is the original's to report, and repeating it on each variant would put
        // the same number on four cards at once.
        if (item.VariantOfId.HasValue)
        {
            if (sameLine) UiLayout.SameLineIfRoomForButton(" Fold ");
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f));
            ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.62f, 0.62f, 0.70f, 1f));
            var fold = ImGui.SmallButton(" Fold ");
            ImGui.PopStyleColor(3);

            if (fold)
            {
                _config.ExpandedVariantGroups.Remove(key);
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A variant. Folds this group back into its original.");
            return true;
        }

        if (!_variantCounts.TryGetValue(item.Id, out var counts) || counts.Total == 0) return false;

        var expanded = _config.ExpandedVariantGroups.Contains(key);

        // Collapsed with nothing actually folded away — every variant is worn, and so pinned visible.
        // There is nothing for the button to reveal, so it is not offered.
        if (!expanded && counts.Hidden == 0) return false;

        var label = expanded ? $" Fold {counts.Total} " : $" +{counts.Hidden} ";

        if (sameLine) UiLayout.SameLineIfRoomForButton(label);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.72f, 0.66f, 0.85f, 1f));
        var clicked = ImGui.SmallButton(label);
        ImGui.PopStyleColor(3);

        if (clicked)
        {
            if (!_config.ExpandedVariantGroups.Remove(key))
                _config.ExpandedVariantGroups.Add(key);
            _config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(expanded
                ? $"{counts.Total} variant(s) of this item are showing.\nClick to fold them away."
                : $"{counts.Hidden} variant(s) are folded into this card.\nClick to show them.");

        return true;
    }

    /// <summary>
    /// Drops variants into their original's card, leaving a count behind.
    /// </summary>
    /// <remarks>
    /// Runs after filtering and sorting, on what the grid was about to draw, so folding narrows the
    /// visible set rather than deciding it. Two things are deliberately never folded away:
    /// <list type="bullet">
    /// <item>A variant whose original the filters excluded, which would otherwise be unreachable —
    /// searching for a variant by name has to find it even though its original does not match.</item>
    /// <item>A variant that is on the character. The original's card would show as unworn while the
    /// variant it hid is what you are actually wearing.</item>
    /// </list>
    /// </remarks>
    private List<WardrobeItem> FoldVariants(List<WardrobeItem> items)
    {
        _variantCounts.Clear();
        if (!_config.GroupVariants) return items;

        var visible = items.Select(i => i.Id).ToHashSet();
        var result  = new List<WardrobeItem>(items.Count);

        foreach (var item in items)
        {
            if (item.VariantOfId is not { } originalId || !visible.Contains(originalId))
            {
                result.Add(item);
                continue;
            }

            var counts   = _variantCounts.GetValueOrDefault(originalId);
            var expanded = _config.ExpandedVariantGroups.Contains(originalId.ToString());

            if (expanded || IsWornOrDetected(item))
            {
                result.Add(item);
                _variantCounts[originalId] = (counts.Total + 1, counts.Hidden);
            }
            else
            {
                _variantCounts[originalId] = (counts.Total + 1, counts.Hidden + 1);
            }
        }

        return result;
    }

    /// <summary>Linked partners as one name per line, for a tooltip.</summary>
    private static string LinkList(List<WardrobeItem> links) =>
        string.Join("\n", links.Select(i => $"  {i.Slot.DisplayName()} — {i.Name}"));

    /// <summary>
    /// The escape hatch from a link: wear or remove this item on its own, leaving its partners as
    /// they are.
    /// </summary>
    /// <remarks>
    /// A small button under the action row rather than a fourth button beside it — that row is
    /// already three buttons across a 180px card and has no width left. Drawn only when the card has
    /// the height to spare, since the card is a fixed size and growing it would reflow the grid for
    /// every item whether it has links or not; the right-click menu covers the rest.
    /// </remarks>
    private void DrawSoloRow(WardrobeItem item, bool worn, List<WardrobeItem> links)
    {
        if (links.Count == 0) return;

        var needed = ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y * 2;
        if (ImGui.GetContentRegionAvail().Y < needed) return;

        var (wearLabel, removeLabel) = item.Slot.ActionLabels();
        var label = worn ? $"{removeLabel} only this" : $"{wearLabel} only this";

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.62f, 0.62f, 0.70f, 1f));
        var clicked = ImGui.SmallButton($"{label}##solo");
        ImGui.PopStyleColor(3);

        if (clicked) Solo(item, worn);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(worn
                ? $"Takes off only this item.\nLeaves these on:\n{LinkList(links)}"
                : $"Wears only this item.\nLeaves these alone:\n{LinkList(links)}");
    }

    private void DrawSoloContextMenu(WardrobeItem item, bool worn, List<WardrobeItem> links)
    {
        if (links.Count == 0) return;
        if (!ImGui.BeginPopupContextItem($"##solomenu_{item.Id}")) return;

        var (wearLabel, removeLabel) = item.Slot.ActionLabels();
        if (ImGui.MenuItem(worn ? $"{removeLabel} only this" : $"{wearLabel} only this"))
            Solo(item, worn);

        ImGui.EndPopup();
    }

    private void Solo(WardrobeItem item, bool worn)
    {
        if (worn) _wardrobe.UnwearItem(item);
        else      _wardrobe.WearItem(item);
    }

    /// <summary>
    /// The select-mode row: a tick box, plus the whole card as a click target for it.
    /// </summary>
    /// <remarks>
    /// The card body counts because ticking forty items through a 20px box each time is miserable,
    /// and nothing on the body is otherwise clickable — the image, name and badges are plain text.
    /// The favourite heart stays live, so it is excluded along with the tick box itself by ignoring
    /// clicks that landed on any widget.
    /// </remarks>
    private void DrawCardSelector(WardrobeItem item)
    {
        var selected = _selected.Contains(item.Id);

        if (ImGui.Checkbox("##pick", ref selected))
            Select(item.Id, selected);

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(selected ? "Selected" : "Select");

        if (ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            Select(item.Id, !selected);
    }

    private void Select(Guid id, bool on)
    {
        if (on) _selected.Add(id);
        else    _selected.Remove(id);
        _bulkStatus = string.Empty;
    }

    private unsafe void DrawItemImage(WardrobeItem item)
    {
        var size = new Vector2(ThumbSize, ThumbSize);
        var path = item.ImagePath ?? string.Empty;

        // Resolve once per item, not once per frame. File.Exists here was a synchronous stat on
        // every card of every frame, which is what made large wardrobes crawl.
        if (!_imageCache.TryGetValue(item.Id, out var entry) || entry.Path != path)
        {
            ISharedImmediateTexture? texture = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { texture = _textures.GetFromFile(path); }
                catch (Exception ex)
                {
                    _log.Warning(ex, $"[Wardrobe] Could not load image for '{item.Name}'");
                }
            }

            entry = (path, texture);
            _imageCache[item.Id] = entry;
        }

        var top = ImGui.GetCursorPos();

        if (entry.Texture?.GetWrapOrDefault() is { } wrap)
        {
            ImageDraw.Square(wrap, ThumbSize);
            AcceptImageDrop(item);
            DrawQuickViewOverlay(item, top, hasImage: true);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.07f, 0.07f, 0.09f, 1f));
        ImGui.Button(item.Slot.DisplayName(), size);
        ImGui.PopStyleColor();
        AcceptImageDrop(item);
        DrawQuickViewOverlay(item, top, hasImage: false);
    }

    /// <summary>
    /// The magnifier in the corner of a card's picture, and the click on the picture itself.
    /// </summary>
    /// <remarks>
    /// A right-click on the picture rather than a button on it: the card is small at the sizes where
    /// this matters most, and anything drawn over the thumbnail is covering the very thing it exists
    /// to show you. The tooltip is what makes it findable, so the picture carries one whether or not
    /// there is anything else to say about it.
    /// <para>
    /// Only on a real picture. A card with no image has the slot name as its placeholder, and there
    /// is nothing to view any larger.
    /// </para>
    /// </remarks>
    private void DrawQuickViewOverlay(WardrobeItem item, Vector2 thumbTop, bool hasImage)
    {
        if (!hasImage) return;

        var count = item.ImageCount();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(count > 1
                ? $"{count} pictures. Right-click to view them full size."
                : "Right-click to view full size.");

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _quickViewItem = item.Id;

        // A count in the corner of the thumbnail, and only when there is more than one. Nothing else
        // on the card could say that a piece has a back view, and a gallery nobody knows about is a
        // gallery nobody opens.
        if (count > 1) DrawImageCountBadge(thumbTop, count);
    }

    /// <summary>
    /// The small "3" over the corner of a card's picture, saying how many there are.
    /// </summary>
    /// <remarks>
    /// Drawn over the picture rather than under it: the cards are sized to the pixel and a badge on a
    /// line of its own would cost every card in the grid a row of height to serve the few that have
    /// several pictures. Not a click target — the right-click that opens the viewer is on the whole
    /// picture already, and a button here would only steal from it.
    /// </remarks>
    private static void DrawImageCountBadge(Vector2 thumbTop, int count)
    {
        var text = count.ToString();
        var pad  = UiScale.S(3f);
        var size = ImGui.CalcTextSize(text);

        var restore = ImGui.GetCursorPos();

        // Top-left, where a wardrobe's pictures are least likely to have anything worth covering — the
        // subject of a centre-cropped shot of a character sits in the middle
        ImGui.SetCursorPos(new Vector2(thumbTop.X + pad, thumbTop.Y + pad));

        var min = ImGui.GetCursorScreenPos();
        var max = new Vector2(min.X + size.X + pad * 2, min.Y + size.Y + pad);
        ImGui.GetWindowDrawList().AddRectFilled(min, max,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.62f)), UiScale.S(3f));

        ImGui.SetCursorPos(new Vector2(thumbTop.X + pad * 2, thumbTop.Y + pad));
        ImGui.TextUnformatted(text);

        ImGui.SetCursorPos(restore);
    }

    /// <summary>Item being shown full size, or null. Cleared by closing the popup.</summary>
    private Guid? _quickViewItem;

    /// <summary>Outfit being shown full size. Checked first, so only one picture is ever up.</summary>
    private Guid? _quickViewOutfit;

    /// <summary>
    /// The same full-size look, for an outfit.
    /// </summary>
    /// <remarks>
    /// Its own method rather than a branch through the item one: the two share the picture and
    /// nothing else. An outfit has no slot, its buttons wear and remove a whole set, and what is
    /// worth saying under the picture is how many pieces it holds and what styles it carries.
    /// </remarks>
    private void DrawOutfitQuickView(Guid id)
    {
        var outfit = _config.Outfits.Find(o => o.Id == id);
        if (outfit == null)
        {
            _quickViewOutfit = null;
            return;
        }

        if (!ImGui.IsPopupOpen(QuickViewPopup)) ImGui.OpenPopup(QuickViewPopup);

        var vp     = ImGui.GetMainViewport();
        var centre = new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f);
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(QuickViewPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            _quickViewOutfit = null;
            return;
        }

        var side = Math.Min(vp.Size.Y * 0.7f, vp.Size.X * 0.5f);

        // Fitted to the height available rather than the width for a portrait, since a portrait is
        // tall and the point of viewing one full size is to see all of it at once
        var portrait = _config.PortraitOutfitPreviews;
        DrawQuickViewPictures(outfit.Id, outfit,
            portrait ? Math.Min(side, vp.Size.Y * 0.75f / ImageDraw.PortraitRatio) : side,
            portrait, "This outfit has no picture.");

        var items = _wardrobe.ResolveOutfit(outfit);

        ImGui.Spacing();
        ImGui.TextUnformatted(outfit.Name);
        ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f),
            $"{items.Count} item{(items.Count == 1 ? "" : "s")}" +
            (outfit.VanillaItems.Count > 0 ? $" · {outfit.VanillaItems.Count} vanilla" : ""));

        if (outfit.Tags.Count > 0)
            ImGui.TextDisabled(string.Join(" · ", outfit.Tags.Select(t =>
                TagTree.IsStyle(t) ? t[(TagTree.StyleRoot.Length + 1)..] : t)));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var worn = _wardrobe.IsOutfitWorn(outfit);
        var btnW = UiScale.S(130);

        if (ImGui.Button(worn ? "Remove" : "Wear", new Vector2(btnW, 0)))
        {
            if (worn) _wardrobe.UnwearOutfit(outfit);
            else      _wardrobe.WearOutfit(outfit, removeOthers: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Edit", new Vector2(btnW, 0)))
        {
            OpenOutfitEdit(outfit);
            CloseQuickView();
        }

        ImGui.SameLine();
        if (ImGui.Button("Close", new Vector2(btnW, 0)))
            CloseQuickView();

        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) CloseQuickView();

        ImGui.EndPopup();
    }

    /// <summary>
    /// A full-size look at one item's picture, over the grid.
    /// </summary>
    /// <remarks>
    /// Sized from the viewport rather than the window: the point is to see the picture properly, and
    /// the wardrobe window is often narrow. It stays a look rather than an editor — the buttons on
    /// it are the two things worth doing while looking at a picture, and everything else is a click
    /// away in Edit.
    /// </remarks>
    private void DrawQuickView()
    {
        if (_quickViewOutfit is { } outfitId)
        {
            DrawOutfitQuickView(outfitId);
            return;
        }

        if (_quickViewItem is not { } id) return;

        var item = _config.WardrobeItems.Find(x => x.Id == id);
        if (item == null)
        {
            _quickViewItem = null;
            return;
        }

        if (!ImGui.IsPopupOpen(QuickViewPopup)) ImGui.OpenPopup(QuickViewPopup);

        var vp     = ImGui.GetMainViewport();
        var centre = new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f);
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(QuickViewPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            _quickViewItem = null;
            return;
        }

        var side = Math.Min(vp.Size.Y * 0.7f, vp.Size.X * 0.5f);

        // Loaded from the path rather than read out of the card cache. The cache is cleared whenever
        // an item is opened for editing, which is one of the two places this is reached from — taking
        // it from there would show an empty square exactly where the picture was asked for.
        DrawQuickViewPictures(item.Id, item, side, portrait: false, "This item has no picture.");

        ImGui.Spacing();
        ImGui.TextUnformatted(item.Name);
        ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f), item.Slot.DisplayName());

        if (item.Tags.Count > 0)
            ImGui.TextDisabled(string.Join(" · ", item.Tags.Select(t =>
                TagTree.IsStyle(t) ? t[(TagTree.StyleRoot.Length + 1)..] : t)));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var worn  = _wardrobe.IsItemWorn(item);
        var btnW  = UiScale.S(130);
        var (wearLabel, removeLabel) = item.Slot.ActionLabels();

        if (ImGui.Button(worn ? removeLabel : wearLabel, new Vector2(btnW, 0)))
        {
            if (worn) _wardrobe.UnwearItem(item);
            else      _wardrobe.WearItemLinked(item);
        }

        ImGui.SameLine();
        if (ImGui.Button("Edit", new Vector2(btnW, 0)))
        {
            OpenItemEditor(item);
            CloseQuickView();
        }

        ImGui.SameLine();
        if (ImGui.Button("Close", new Vector2(btnW, 0)))
            CloseQuickView();

        // Escape is what people press at a picture they have finished looking at
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) CloseQuickView();

        ImGui.EndPopup();
    }

    /// <summary>Which picture the quick view is showing, and whose set it is counting through.</summary>
    /// <remarks>
    /// The id is kept alongside the number so opening the viewer on something else starts at its cover
    /// rather than at page three of the last thing looked at. Tracked here rather than reset at every
    /// place that opens the viewer, of which there are several.
    /// </remarks>
    private int   _quickViewPage;
    private Guid? _quickViewPageOf;

    /// <summary>
    /// The picture, and the controls for paging through the others when there are any.
    /// </summary>
    /// <remarks>
    /// Shared by the item and outfit viewers, which differ only in the shape they draw and in what
    /// they say when there is nothing to show. Arrow keys work as well as the buttons: this is a
    /// picture viewer, and reaching for the keyboard at one is instinct.
    /// </remarks>
    private void DrawQuickViewPictures(Guid id, IImageOwner owner, float side, bool portrait,
        string noneText)
    {
        var images = ImageGallery.Viewable(owner);

        if (_quickViewPageOf != id)
        {
            _quickViewPageOf = id;
            _quickViewPage   = 0;
        }

        if (images.Count == 0)
        {
            // Says which of the two it is: a set whose files have all been moved away is a different
            // problem from never having taken a picture, and the fix is not the same either
            ImGui.TextDisabled(owner.ImageCount() > 0
                ? "None of this one's pictures could be loaded — check the files are still there."
                : noneText);
            return;
        }

        _quickViewPage = Math.Clamp(_quickViewPage, 0, images.Count - 1);
        var path = images[_quickViewPage];

        try
        {
            if (_textures.GetFromFile(path).GetWrapOrDefault() is { } wrap)
            {
                if (portrait) ImageDraw.Portrait(wrap, side);
                else          ImageDraw.Square(wrap, side);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"[Wardrobe] Could not load '{path}' for quick view");
        }

        if (images.Count == 1) return;

        // Under the picture, centred on it as far as the layout allows: a pager beside the frame would
        // push the frame off centre in a popup that sizes itself to its contents
        if (GlyphButton(FontAwesomeIcon.ChevronLeft, "qvprev", "Previous picture (left arrow)."))
            _quickViewPage--;

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{_quickViewPage + 1} / {images.Count}");

        ImGui.SameLine();
        if (GlyphButton(FontAwesomeIcon.ChevronRight, "qvnext", "Next picture (right arrow)."))
            _quickViewPage++;

        UiLayout.SameLineIfRoomForText(Path.GetFileName(path));
        ImGui.TextDisabled(Path.GetFileName(path));

        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))  _quickViewPage--;
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) _quickViewPage++;

        // Wraps rather than stopping, so a set of four can be gone round without hunting for the end
        if (_quickViewPage < 0)              _quickViewPage = images.Count - 1;
        if (_quickViewPage >= images.Count)  _quickViewPage = 0;
    }

    private void CloseQuickView()
    {
        _quickViewItem   = null;
        _quickViewOutfit = null;
        ImGui.CloseCurrentPopup();
    }

    private const string QuickViewPopup = "Quick view###quickView";

    private unsafe void AcceptImageDrop(WardrobeItem item)
    {
        if (!ImGui.BeginDragDropTarget()) return;

        var payload = ImGui.AcceptDragDropPayload("WRD_IMG");

        // ImGuiPayloadPtr wraps a native pointer as its first field; read it as nint to null-guard
        // before calling any method (IsDelivery on null → access violation crash).
        if (Unsafe.As<ImGuiPayloadPtr, nint>(ref payload) != 0
            && payload.IsDelivery()
            && payload.DataSize > 0)
        {
            var path = Encoding.UTF8.GetString((byte*)payload.Data, payload.DataSize).TrimEnd('\0');
            item.ImagePath = path;
            _imageCache.Remove(item.Id);
            _config.Save();
        }

        ImGui.EndDragDropTarget();
    }

    /// <summary>Same drag target as items, for outfit cards.</summary>
    private unsafe void AcceptOutfitImageDrop(Outfit outfit)
    {
        if (!ImGui.BeginDragDropTarget()) return;

        var payload = ImGui.AcceptDragDropPayload("WRD_IMG");

        // Null-guard the payload pointer before touching it — IsDelivery on null is a crash
        if (Unsafe.As<ImGuiPayloadPtr, nint>(ref payload) != 0
            && payload.IsDelivery()
            && payload.DataSize > 0)
        {
            var path = Encoding.UTF8.GetString((byte*)payload.Data, payload.DataSize).TrimEnd('\0');
            outfit.ImagePath = path;
            _outfitImageCache.Remove(outfit.Id);

            // Keep the editor's staged field in step if this outfit is open
            if (_editingOutfit == outfit) _editOutfitImage = path;

            _config.Save();
        }

        ImGui.EndDragDropTarget();
    }

    // ── Image browser panel ───────────────────────────────────────────────────

    private unsafe void DrawImageBrowser()
    {
        if (DrawPanelHeader("Image Browser"))
        {
            _showImageBrowser = false;
            return;
        }

        if (string.IsNullOrEmpty(_config.ImagesFolder))
        {
            ImGui.TextDisabled("No folder set. Open Settings to configure one.");
            return;
        }

        // Path on its own line: it is long enough to wrap, which would push a button beside it
        // off the side of the panel
        ImGui.TextDisabled(_config.ImagesFolder);

        // Plain text, not a refresh arrow: U+21BA is not in the default font either
        if (ImGui.SmallButton("Refresh")) RefreshBrowserImages();

        if (_browserImages.Length == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No images found in this folder.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Sort");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);

        // Order must match the ImageSortMode enum values
        var sortLabels = new[] { "Name (A–Z)", "Name (Z–A)", "Newest first", "Oldest first" };
        var sortIdx    = (int)_config.ImageSortMode;
        if (ImGui.Combo("##imgsort", ref sortIdx, sortLabels, sortLabels.Length))
        {
            _config.ImageSortMode = (ImageSortMode)sortIdx;
            _config.Save();
            RefreshBrowserImages(); // re-sorts; the date modes read the filesystem
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Drag an image onto a wardrobe item card.");
        ImGui.Separator();

        var avail    = ImGui.GetContentRegionAvail().X;
        var thumbW   = 100f;
        var thumbH   = 100f;
        var cols     = Math.Max(1, (int)((avail + 4f) / (thumbW + 4f)));
        var col      = 0;

        foreach (var imgPath in _browserImages)
        {
            ImGui.PushID(imgPath);

            IDalamudTextureWrap? wrap = null;
            try { wrap = _textures.GetFromFile(imgPath).GetWrapOrDefault(); }
            catch { }

            var fname = Path.GetFileNameWithoutExtension(imgPath);
            var label = fname.Length > 13 ? fname[..11] + "…" : fname;

            if (wrap != null)
            {
                ImageDraw.SquareButton($"browse_{imgPath}", wrap, thumbW);
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.1f, 0.1f, 0.12f, 1f));
                ImGui.Button(label, new Vector2(thumbW, thumbH));
                ImGui.PopStyleColor();
            }

            // Drag source
            if (ImGui.BeginDragDropSource())
            {
                var bytes = Encoding.UTF8.GetBytes(imgPath + "\0");
                ImGui.SetDragDropPayload("WRD_IMG", (ReadOnlySpan<byte>)bytes);

                if (wrap != null) ImageDraw.Square(wrap, 64f);
                ImGui.TextUnformatted(label);
                ImGui.EndDragDropSource();
            }

            col++;
            if (col < cols) ImGui.SameLine();
            else col = 0;

            ImGui.PopID();
        }
    }

    private void RefreshBrowserImages()
    {
        var folder = _config.ImagesFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            _browserImages = Array.Empty<string>();
            return;
        }

        var files = Directory.GetFiles(folder)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)));

        // Sorting happens here rather than at draw time: the date modes need a filesystem stat per
        // file, which must not run every frame.
        var sorted = _config.ImageSortMode switch
        {
            ImageSortMode.NameDesc    => files.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            ImageSortMode.NewestFirst => files.OrderByDescending(SafeWriteTime).ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            ImageSortMode.OldestFirst => files.OrderBy(SafeWriteTime).ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            _                         => files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
        };

        _browserImages = sorted.ToArray();

        _lastBrowserFolder = folder;
    }

    /// <summary>Last write time, or the epoch if the file vanished between listing and sorting.</summary>
    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    // ── Screenshot session HUD ────────────────────────────────────────────────

    /// <summary>
    /// Compact stand-in for the whole window during a screenshot session: progress and the
    /// controls you actually need mid-shot, with the grid and panels hidden.
    /// </summary>
    private void DrawCompactSession()
    {
        UiLayout.PushWrap();
        ImGui.TextUnformatted("Screenshot Session");
        ImGui.SameLine();
        ImGui.TextDisabled($"·  {_session.CompletedCount} / {_session.TotalCount}");

        var expand = "Expand";
        var width  = ImGui.CalcTextSize(expand).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine();
        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);
        if (ImGui.SmallButton(expand)) _compactOverride = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Return to the full window for the rest of this session.\n" +
                             "The setting itself is left unchanged.");

        ImGui.Separator();
        ImGui.Spacing();

        if (_session.State == SessionState.Done)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "Session complete!");
            ImGui.Spacing();
            if (ImGui.Button("Close", new Vector2(-1, 0))) _session.Stop();
            UiLayout.PopWrap();
            return;
        }

        ImGui.TextUnformatted(_session.CurrentName);

        // As in the full HUD: what the session is waiting for is the thing the window has to say, and
        // this is the window most of a session is actually watched in
        if (_session.MultiShot)
            ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f),
                $"Shot {_session.ShotIndex} of {_session.ShotCount} — {_session.ShotLabel}");
        else if (_session.Manual)
            ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f),
                $"{_session.TakenForTarget} taken  ·  manual");

        ImGui.Spacing();

        switch (_session.State)
        {
            case SessionState.WaitingForShot:
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Waiting for screenshot…");
                break;
            case SessionState.Processing:
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Processing image…");
                break;
        }

        ImGui.Spacing();

        // Whichever the session is on: an item's slot presets, or the outfit set
        if (_session.CurrentItem is { } shooting)
            DrawCompactCameraPresets(shooting.Slot.ToString(), shooting.Slot.DisplayName());
        else if (_session.CurrentOutfit != null)
            DrawCompactCameraPresets(Configuration.OutfitPresetKey, "Outfits");

        ImGui.Separator();
        ImGui.Spacing();
        DrawSessionActionRow(_session.CurrentItem != null ? "Item" : "Outfit");
        UiLayout.PopWrap();
    }

    /// <summary>
    /// Camera presets for the slot being photographed, inside the compact session view.
    /// </summary>
    /// <remarks>
    /// The moment a preset is wrong is the moment you are looking at the shot it framed, and until
    /// now fixing it meant expanding the window back over the very scene you were photographing.
    /// <para>
    /// Deliberately not the whole panel. Renaming, deleting and choosing the session default are
    /// wardrobe housekeeping, and a stray click on a delete button mid-session is a preset gone with
    /// the window too small to notice. What is here is the three things worth doing between shots:
    /// switch to another angle, correct the one being used, and keep a good angle you just found.
    /// Expand is a click away for the rest.
    /// </para>
    /// </remarks>
    private void DrawCompactCameraPresets(string slotKey, string label)
    {
        var presets = _config.PresetsFor(slotKey);

        ImGui.Separator();
        ImGui.TextDisabled($"Camera  ·  {label}");

        // Moving to another slot makes the remembered preset meaningless — it belonged to the last one
        if (_compactPresetSlot != slotKey)
        {
            _compactPresetSlot = slotKey;
            _compactPreset     = null;
        }

        var sessionPreset = _config.DefaultPresetFor(slotKey);

        // What Update corrects: whatever was last snapped to here, falling back to the one the
        // session loaded, since that is what is on screen before anything has been clicked
        var target = _compactPreset != null && presets.Contains(_compactPreset)
            ? _compactPreset
            : sessionPreset;

        var applyIdx = -1;
        for (var i = 0; i < presets.Count; i++)
        {
            var isTarget  = ReferenceEquals(presets[i], target);
            var isSession = ReferenceEquals(presets[i], sessionPreset);
            var name      = string.IsNullOrWhiteSpace(presets[i].Name) ? "(unnamed)" : presets[i].Name;

            // The session's own preset is marked, so picking another does not lose track of which one
            // the remaining items will be shot with
            var buttonLabel = isSession ? $"*{name}" : name;

            if (i > 0) UiLayout.SameLineIfRoomForButton(buttonLabel);

            if (isTarget)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
            }

            if (ImGui.SmallButton($"{buttonLabel}##compactpreset_{i}")) applyIdx = i;
            if (isTarget) ImGui.PopStyleColor(2);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Snap to this angle." +
                                 (isSession ? "\n* The session loads this one." : string.Empty) +
                                 (isTarget  ? "\nUpdate corrects this one."     : string.Empty));
        }

        if (applyIdx >= 0)
        {
            Plugin.Camera.Apply(presets[applyIdx]);
            _compactPreset = presets[applyIdx];
        }

        if (presets.Count > 0 && target != null)
        {
            var targetName = string.IsNullOrWhiteSpace(target.Name) ? "(unnamed)" : target.Name;

            // Named on the button rather than left to a tooltip. Which preset Update overwrites is
            // the whole question being asked of it, and a hover is a poor place to answer that.
            if (ImGui.SmallButton($"Update {targetName}##compactupdate"))
            {
                var index = _config.SlotCameraPresetLists[slotKey].IndexOf(target);
                if (index >= 0) OverwritePreset(slotKey, index);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Replace '{targetName}' with the camera as it is now.\n" +
                                 "Click another preset above to correct that one instead.");

            UiLayout.SameLineIfRoomForButton("Save New");
        }

        if (ImGui.SmallButton(presets.Count > 0 ? "Save New" : "Save Camera"))
            SaveCameraPreset(slotKey, string.Empty);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(presets.Count > 0
                ? $"Keep the current camera as another {label} preset."
                : $"Save the current camera as the {label} preset.\n" +
                  "The session will use it from the next shot on.");

        ImGui.Spacing();
    }

    private void DrawSessionHud()
    {
        if (_session.State == SessionState.Idle) return;

        ImGui.SetNextWindowSize(UiScale.S(340, 0), ImGuiCond.Always);
        ImGui.SetNextWindowPos(new Vector2(60, 60), ImGuiCond.Appearing);
        ImGui.SetNextWindowBgAlpha(0.92f);

        var open = true;
        ImGui.Begin("Screenshot Session###WardrobeSessionHud",
            ref open,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoBringToFrontOnFocus);

        UiLayout.PushWrap();

        var manual = _session.Manual;
        if (ImGui.Checkbox("Manual mode", ref manual))
            _session.Manual = manual;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The session stays on each item, however many screenshots you take.\n" +
                             "The first becomes the card's picture and the rest join it.\n\n" +
                             "Press Next Item when you are happy with it, or End Session to stop.\n\n" +
                             "Off, the session moves on as soon as a screenshot lands — one per\n" +
                             "item, plus any camera angles you have ticked.");

        var stripOthers = _session.StripOthers;
        if (ImGui.Checkbox("Strip other items before each shot", ref stripOthers))
            _session.StripOthers = stripOthers;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes all other worn items via Emperor's New before\nequipping each item, so only that item appears in the shot.");

        var compact = _config.CompactDuringSession;
        if (ImGui.Checkbox("Compact main window during session", ref compact))
        {
            _config.CompactDuringSession = compact;
            _config.Save();
            _compactOverride = false; // re-ticking it should take effect immediately
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shrinks the wardrobe window to a small session view while\n" +
                             "a session runs, so it stays out of the shot.");

        ImGui.Spacing();
        DrawSessionBasePicker();

        ImGui.Separator();
        ImGui.Spacing();

        if (_session.State == SessionState.Done)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "Session complete!");
            ImGui.TextDisabled($"{_session.CompletedCount} / {_session.TotalCount} items processed.");
            ImGui.Spacing();
            if (ImGui.Button("Close")) _session.Stop();
        }
        else
        {
            var item  = _session.CurrentItem;
            var label = item != null ? "Item" : "Outfit";
            ImGui.TextDisabled($"{label} {_session.CompletedCount + 1} of {_session.TotalCount}");
            ImGui.TextUnformatted(_session.CurrentName);

            // Which angle, when there is more than one. The camera has already moved to it, so
            // without this the only clue about what the session wants is where it is pointing.
            if (_session.MultiShot)
                ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f),
                    $"Shot {_session.ShotIndex} of {_session.ShotCount} — {_session.ShotLabel}");
            // A count and nothing more. It used to explain what the first picture was for — which the
            // line below already does, so the two said one thing twice in different words, and the
            // shorter of them ("1 picture taken — the card's.") read as a riddle.
            else if (_session.Manual)
                ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f),
                    _session.TakenForTarget switch
                    {
                        0 => "No pictures of this one yet.",
                        1 => "1 picture taken.",
                        _ => $"{_session.TakenForTarget} pictures taken.",
                    });

            ImGui.Spacing();
            switch (_session.State)
            {
                case SessionState.WaitingForShot:
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Waiting for screenshot…");
                    ImGui.TextDisabled(_session.Manual
                        ? "Take as many as you like. The first is the card's picture and the rest join it."
                        : "Position your character, then press your screenshot key.");
                    ImGui.Spacing();
                    DrawSessionCameraPresets();
                    break;
                case SessionState.Processing:
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Processing image…");
                    break;
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawSessionActionRow(label);
        }

        UiLayout.PopWrap();
        ImGui.End();

        if (!open) _session.Stop();
    }

    /// <summary>
    /// The session's buttons — the skips and End Session — as one row of equal widths.
    /// </summary>
    /// <remarks>
    /// One row, and shared by both session windows so the two cannot drift apart. They were laid out
    /// separately before: a <c>SameLine</c> after Skip, then a separator, which cancels it — so Skip sat
    /// alone on one row and End Session below it at a different width, looking like two unrelated
    /// controls rather than the choices they are.
    /// <para>
    /// Widths are divided out of what the window actually has rather than fixed, because the two windows
    /// are different sizes and the compact one follows the main window's width.
    /// </para>
    /// </remarks>
    /// <param name="targetLabel">"Item" or "Outfit", for naming the skip that abandons the whole thing.</param>
    private void DrawSessionActionRow(string targetLabel)
    {
        var waiting = _session.State == SessionState.WaitingForShot;
        var manual  = _session.Manual;
        var noun    = targetLabel.ToLowerInvariant();

        // Manual mode has one button before End Session and only when there is something to move on to;
        // automatic has a skip, and a second one for the angle when several are queued
        var others  = !waiting ? 0
                    : manual   ? (_session.HasMoreTargets ? 1 : 0)
                    : _session.MultiShot ? 2 : 1;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var size    = new Vector2((ImGui.GetContentRegionAvail().X - spacing * others) / (others + 1), 0);

        if (waiting && manual && _session.HasMoreTargets)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.32f, 0.5f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.44f, 0.68f, 1f));
            if (ImGui.Button($"Next {targetLabel}", size))
                _session.NextTarget();
            ImGui.PopStyleColor(2);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Finished with this {noun} — keep what you have taken of it and move\n" +
                                 "on to the next.\n\n" +
                                 "Nothing is lost by taking none: the pictures already filed stay, and a\n" +
                                 $"{noun} you took none of is simply left as it was.");
            ImGui.SameLine();
        }

        if (waiting && !manual && _session.MultiShot)
        {
            if (ImGui.Button("Skip Angle", size))
                _session.SkipShot();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Skip this angle and move on to the next one of this {noun}.");
            ImGui.SameLine();
        }

        if (waiting && !manual)
        {
            if (ImGui.Button(_session.MultiShot ? $"Skip {targetLabel}" : "Skip", size))
                _session.Skip();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_session.MultiShot
                    ? $"Skip this {noun} entirely, remaining angles included."
                    : $"Skip this {noun} and move on to the next without taking a screenshot.");
            ImGui.SameLine();
        }

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.Button("End Session", size)) _session.Stop();
        ImGui.PopStyleColor(2);
    }

    /// <summary>
    /// The base character a session photographs against, chosen from the HUD.
    /// </summary>
    /// <remarks>
    /// Here as well as in settings because this is where you find out it is wrong: the first shot
    /// with your ears missing is the moment you go looking for the control, and it should be the one
    /// already on screen rather than three clicks into a panel behind the character.
    /// </remarks>
    private void DrawSessionBasePicker()
    {
        ImGui.TextDisabled("Base character");

        ImGui.SetNextItemWidth(-1);
        DrawBaseCharacterCombo("##sessionbase");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Applied before every shot, and held back when other items are\n" +
                             "stripped. Set one up in Settings → Base character.");

        if (_config.BaseCharacters.Count == 0)
        {
            ImGui.TextDisabled("None saved yet — Settings → Base character.");
            return;
        }

        if (_config.ActiveBaseCharacter is not { } active) return;

        var kept = _wardrobe.KeptSlots(active);
        ImGui.TextDisabled(kept.Count == 0
            ? "No slots kept."
            : $"Keeps {string.Join(", ", kept.OrderBy(s => (int)s).Select(s => s.DisplayName()))}.");
    }

    // ── Camera preset controls (used inside session HUD) ──────────────────────

    /// <summary>
    /// The camera presets for an item's slot: apply, overwrite, rename, reorder and delete, plus a
    /// box for saving the current camera as a new one.
    /// </summary>
    /// <remarks>
    /// Presets belong to the slot rather than the item, so every pair of boots shares them — which
    /// is the point, since the angle that frames one pair frames them all. Several per slot because
    /// one angle is rarely enough: a full-body shot and a close-up are different pictures of the
    /// same piece, and before this you had to re-aim the camera by hand between them.
    /// </remarks>
    /// <remarks>
    /// Whichever list the session is on, exactly as the compact view decides it: an item's slot, or
    /// the one shared set for outfits. It used to take the item and return when there was not one,
    /// so an outfit session showed no preset controls at all here while the compact window showed
    /// them — the angle was adjustable only in the smaller of the two windows.
    /// </remarks>
    private void DrawSessionCameraPresets()
    {
        if (_session.CurrentItem is { } item)
            DrawCameraPresetControls(item.Slot.ToString(), item.Slot.DisplayName(),
                $"for all {item.Slot.DisplayName()} items");
        else if (_session.CurrentOutfit != null)
            DrawCameraPresetControls(Configuration.OutfitPresetKey, "Outfits", "for every outfit");
    }

    /// <summary>
    /// Every slot's camera presets in one panel, for setting angles up before a session runs.
    /// </summary>
    /// <remarks>
    /// Until now the only place to edit a slot's presets was a session that had reached an item in it,
    /// which is the wrong moment: a session over a whole wardrobe stops at each slot in turn and waits,
    /// so the angles have to be invented one at a time with the run already going. This is where they
    /// can be framed in advance — go into GPose, work down the list, then start the session and let it
    /// run.
    /// <para>
    /// One slot expanded at a time. The controls inside are exactly the ones the session HUD shows, so
    /// there is one set of behaviour to learn and no second implementation to keep in step.
    /// </para>
    /// </remarks>
    private void DrawCameraPresetsPanel()
    {
        if (DrawPanelHeader("Camera Presets"))
        {
            _showCameraPresets = false;
            return;
        }

        ImGui.Spacing();
        ImGui.TextWrapped("Angles for a screenshot session, per slot. Frame the camera in GPose, then " +
                          "save it here — a session loads each slot's cover angle by itself, plus any " +
                          "extra angles you tick.");

        ImGui.Spacing();

        // Slots the wardrobe actually holds something for come first in the reader's mind, so the count
        // of what still has no angle is the useful summary — it is the list of work left to do
        var slots = SlotsForPresets();
        var without = slots.Count(s => _config.PresetsFor(s.Key).Count == 0);

        ImGui.TextDisabled(without == 0
            ? $"All {slots.Count} have at least one angle."
            : $"{slots.Count - without} of {slots.Count} have an angle. {without} still to do.");

        if (!Plugin.Camera.InGpose)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                "Not in GPose — saving an angle needs the GPose camera.");
            ImGui.TextDisabled("The lists below can still be renamed, reordered and tidied.");
        }

        ImGui.Spacing();
        ImGui.Separator();

        foreach (var (key, label, scope, items) in slots)
        {
            ImGui.PushID($"presetslot_{key}");

            var open  = _presetSlotOpen == key;
            var count = _config.PresetsFor(key).Count;
            var extra = _config.ExtraShotPresetsFor(key).Count;

            // The header says what the slot has without being opened: how many angles, how many of them
            // a session will shoot, and how many items would be photographed with them
            var summary = count == 0 ? "no angles" : extra > 0 ? $"{count} · {extra + 1} shots" : $"{count}";
            var heading = $"{label}  ({summary})";

            if (ImGui.Selectable(heading, open))
                _presetSlotOpen = open ? null : key;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(items >= 0
                    ? $"{items} item(s) in this slot.\nClick to {(open ? "close" : "edit")} its angles."
                    : $"Shared by every outfit.\nClick to {(open ? "close" : "edit")} its angles.");

            if (open)
            {
                ImGui.Indent();
                DrawCameraPresetControls(key, label, scope);
                ImGui.Unindent();
                ImGui.Separator();
            }

            ImGui.PopID();
        }
    }

    /// <summary>
    /// The preset lists a wardrobe has: one per equipment slot it uses, plus the shared outfit set.
    /// </summary>
    /// <remarks>
    /// Slots with no items are still listed — a wardrobe grows, and an angle saved before the first pair
    /// of boots arrives is exactly the preparation this panel is for. Mod categories are left out: an
    /// animation or a mount is photographed deliberately from its own edit panel, and a session skips
    /// them precisely because one angle of a dance says nothing.
    /// </remarks>
    private List<(string Key, string Label, string Scope, int Items)> SlotsForPresets()
    {
        var list = new List<(string, string, string, int)>
        {
            // First, because it is the one set that is not per slot and the one most people set up first
            (Configuration.OutfitPresetKey, "Outfits", "for every outfit", -1),
        };

        foreach (var slot in EquipSlotEx.All)
            list.Add((slot.ToString(), slot.DisplayName(), $"for all {slot.DisplayName()} items",
                _config.WardrobeItems.Count(i => i.Slot == slot)));

        return list;
    }

    /// <param name="slotKey">Which list to edit — a slot's name, or <see cref="Configuration.OutfitPresetKey"/>.</param>
    /// <param name="label">What the list is called on screen.</param>
    /// <param name="scope">Who the presets apply to, for the Save button's tooltip.</param>
    private void DrawCameraPresetControls(string slotKey, string label, string scope)
    {
        var presets = _config.PresetsFor(slotKey);

        ImGui.TextDisabled($"Camera presets — {label}");

        if (presets.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.TextUnformatted("None saved");
            ImGui.PopStyleColor();
        }

        if (presets.Count > 1)
            ImGui.TextDisabled("The selected one is the cover shot. Tick any others to photograph " +
                               "those angles too.");

        // Deferred so the list is not mutated while it is being walked
        var applyIdx   = -1;
        var updateIdx  = -1;
        var deleteIdx  = -1;
        var defaultIdx = -1;
        var captureIdx = -1;

        // Which row draws as the default. Read once rather than per row, so a slot with nothing
        // marked shows the fallback ticked instead of showing nothing ticked while still loading one
        var defaultPreset = _config.DefaultPresetFor(slotKey);

        for (var i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];

            ImGui.PushID($"campreset_{i}");

            if (_renamePresetSlot == slotKey && _renamePresetIdx == i)
            {
                DrawPresetRenameRow(slotKey, i);
                ImGui.PopID();
                continue;
            }

            // A radio rather than a menu item: picking one of several is exactly what the control
            // means, and it shows which is picked without anyone having to open anything
            if (ImGui.RadioButton("##default", ReferenceEquals(preset, defaultPreset)))
                defaultIdx = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Take the cover shot from this angle — the picture that ends up on the card.\n" +
                                 "A session loads it automatically.");

            ImGui.SameLine();

            // Not offered on the cover's own row: the session photographs that angle regardless, and a
            // tick there would only ask for the same picture twice
            if (!ReferenceEquals(preset, defaultPreset))
            {
                var capture = preset.CaptureInSession;
                if (ImGui.Checkbox("##capture", ref capture)) captureIdx = i;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Take an extra shot from this angle too, on top of the cover.\n\n" +
                                     "A session waits for a screenshot at each ticked angle in turn and\n" +
                                     "files them as this item's other pictures — the side, the back, a\n" +
                                     "close-up of the detail.");

                ImGui.SameLine();
            }

            var presetLabel = string.IsNullOrWhiteSpace(preset.Name) ? "(unnamed)" : preset.Name;
            if (ImGui.SmallButton(presetLabel)) applyIdx = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Snap the camera to this preset now.\n" +
                                 "Camera control returns to you after about half a second.\n\n" +
                                 "Right-click to rename.");

            // Marked rather than converted. The offset a preset needs is the character's facing at
            // the moment it was saved, which nothing recorded — so the only honest conversion is to
            // re-save it while the shot looks right, and the only useful thing to do here is say so.
            var worldAngle = preset.DirHOffset == null;
            if (worldAngle)
            {
                UiLayout.SameLineIfRoomForText("world angle");
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "world angle");

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Saved before angles followed your character, so it points the\n" +
                                     "same way whichever way you are facing.\n\n" +
                                     "Frame the shot as you want it and press Update to convert it.");
            }

            if (ImGui.BeginPopupContextItem("##presetctx"))
            {
                if (ImGui.MenuItem("Rename"))
                {
                    _renamePresetSlot = slotKey;
                    _renamePresetIdx  = i;
                    _renamePresetBuf  = preset.Name;
                }

                ImGui.Separator();
                if (UiLayout.DeleteMenuItem("Delete")) deleteIdx = i;
                ImGui.EndPopup();
            }

            // A button rather than only a menu item: re-aiming a preset is the thing you do most
            // after making one, and it is worth nothing if it is hidden behind a right-click
            UiLayout.SameLineIfRoomForButton("Update");
            if (ImGui.SmallButton("Update")) updateIdx = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Replace '{presetLabel}' with the camera as it is now.\n" +
                                 "Keeps the name." +
                                 (worldAngle ? "\n\nAlso converts it to follow your character." : string.Empty));

            UiLayout.SameLineIfRoomForButton("×");
            if (DeleteButton("×", $"Deletes the camera preset '{presetLabel}'.")) deleteIdx = i;

            ImGui.PopID();
        }

        if (applyIdx >= 0) Plugin.Camera.Apply(presets[applyIdx]);
        if (updateIdx >= 0) OverwritePreset(slotKey, updateIdx);
        if (defaultIdx >= 0) SetPresetDefault(slotKey, defaultIdx);
        if (captureIdx >= 0) TogglePresetCapture(slotKey, captureIdx);
        if (deleteIdx >= 0) DeletePreset(slotKey, deleteIdx);

        // Said once under the list rather than per row: how many shots a session will ask for is the
        // thing worth knowing before starting one, and it is not obvious from a column of ticks
        var extras = _config.ExtraShotPresetsFor(slotKey).Count;
        if (extras > 0)
            ImGui.TextDisabled($"Sessions take {extras + 1} shots: cover, then " +
                               string.Join(", ", _config.ExtraShotPresetsFor(slotKey)
                                   .Select(p => string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name)) + ".");

        // Saving a new one. The name is optional — an unnamed save is numbered, so the camera can be
        // caught quickly in GPose without stopping to think of a word for it.
        ImGui.SetNextItemWidth(-UiLayout.ButtonWidth("Save Camera") - ImGui.GetStyle().ItemSpacing.X);
        var entered = ImGui.InputTextWithHint("##newpreset", "preset name (optional)",
            ref _newPresetName, 48, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (ImGui.SmallButton("Save Camera") || entered) SavePresetFromCamera(slotKey);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Save the current GPose camera as a new preset\n{scope}.");
    }

    /// <summary>The rename box, drawn in place of a preset's row while it is being renamed.</summary>
    private void DrawPresetRenameRow(string slotKey, int index)
    {
        ImGui.SetNextItemWidth(-UiLayout.ButtonWidth("Save") - ImGui.GetStyle().ItemSpacing.X);

        var entered = ImGui.InputText("##renamepreset", ref _renamePresetBuf, 48,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (ImGui.SmallButton("Save") || entered)
        {
            var list = _config.SlotCameraPresetLists[slotKey];
            list[index].Name = _renamePresetBuf.Trim();
            _config.Save();
            _config.SavePresets();
            CancelPresetRename();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rename this preset.");

        UiLayout.SameLineIfRoomForButton("Cancel");
        if (ImGui.SmallButton("Cancel")) CancelPresetRename();
    }

    private void CancelPresetRename()
    {
        _renamePresetSlot = string.Empty;
        _renamePresetIdx  = -1;
        _renamePresetBuf  = string.Empty;
    }

    private void SavePresetFromCamera(string slotKey)
    {
        SaveCameraPreset(slotKey, _newPresetName);
        _newPresetName = string.Empty;
    }

    /// <summary>
    /// Saves the camera as a new preset for a slot. A blank name is numbered.
    /// </summary>
    /// <remarks>
    /// Takes the name rather than reading the edit panel's box, so the compact session view — which
    /// has no room for a name box — cannot pick up whatever happens to be typed in a panel that is
    /// not even on screen.
    /// </remarks>
    private void SaveCameraPreset(string slotKey, string rawName)
    {
        var captured = Plugin.Camera.Capture();
        if (captured == null)
        {
            _log.Warning("[Wardrobe] Save preset: no camera to capture.");
            return;
        }

        if (!_config.SlotCameraPresetLists.TryGetValue(slotKey, out var list))
            _config.SlotCameraPresetLists[slotKey] = list = new List<CameraPreset>();

        var name = rawName.Trim();
        captured.Name = name.Length > 0 ? name : $"Preset {list.Count + 1}";

        // The first preset a slot ever gets is the one sessions will load, so it marks itself
        if (list.Count == 0) captured.IsDefault = true;
        list.Add(captured);

        _config.Save();
        _config.SavePresets();
        _log.Information($"[Wardrobe] Saved camera preset '{captured.Name}' for {slotKey}");
    }

    private void OverwritePreset(string slotKey, int index)
    {
        var captured = Plugin.Camera.Capture();
        if (captured == null) return;

        var list = _config.SlotCameraPresetLists[slotKey];

        // The name is the one thing an overwrite must not take with it — the point of overwriting is
        // that this angle is still "Close-up", just a better one
        captured.Name = list[index].Name;
        list[index]   = captured;

        _config.Save();
        _config.SavePresets();
    }

    /// <summary>
    /// Marks one preset as its slot's default, clearing the mark from the rest.
    /// </summary>
    /// <remarks>
    /// Order is left alone. Choosing which angle a session loads and choosing where it sits in the
    /// list are separate decisions, and folding them together would mean the list could not be
    /// arranged to taste without changing behaviour.
    /// </remarks>
    private void SetPresetDefault(string slotKey, int index)
    {
        var list = _config.SlotCameraPresetLists[slotKey];
        for (var i = 0; i < list.Count; i++)
            list[i].IsDefault = i == index;

        // The cover angle is photographed because it is the cover, so a tick left on it from before it
        // was promoted would ask a session for the same picture twice — and the row it was ticked on
        // no longer shows the control that could untick it
        list[index].CaptureInSession = false;

        _config.Save();
        _config.SavePresets();
    }

    /// <summary>
    /// Turns a preset's extra-shot tick on or off.
    /// </summary>
    /// <remarks>
    /// The cover's own tick is cleared as it becomes the cover, in <see cref="SetPresetDefault"/> —
    /// not here — because that is where a preset can gain the role while already ticked.
    /// </remarks>
    private void TogglePresetCapture(string slotKey, int index)
    {
        var list = _config.SlotCameraPresetLists[slotKey];
        list[index].CaptureInSession = !list[index].CaptureInSession;

        _config.Save();
        _config.SavePresets();
    }

    private void DeletePreset(string slotKey, int index)
    {
        var list      = _config.SlotCameraPresetLists[slotKey];
        var wasDefault = list[index].IsDefault;
        list.RemoveAt(index);

        // Deleting the default hands the mark to whatever is now first, so the radio shows the
        // preset that would actually load rather than nothing at all
        if (wasDefault && list.Count > 0) list[0].IsDefault = true;

        // The slot's entry goes with its last preset, so a slot with none is absent rather than
        // holding an empty list that every later read has to allow for
        if (list.Count == 0) _config.SlotCameraPresetLists.Remove(slotKey);

        // A rename in progress now points at the wrong preset, or at none
        CancelPresetRename();

        _config.Save();
        _config.SavePresets();
    }

    // ── Outfits grid ──────────────────────────────────────────────────────────

    /// <summary>
    /// The item grid replaced by saved outfits: named sets of items worn and removed together.
    /// </summary>
    /// <remarks>
    /// Built on wardrobe items rather than Glamourer designs, so wearing an outfit also enables
    /// each item's Penumbra mods and applies their options.
    /// </remarks>
    private void DrawOutfitsGrid()
    {
        var wornCount = _config.WornItems.Count;

        ImGui.Spacing();
        ImGui.SetNextItemWidth(UiScale.S(240));
        ImGui.InputTextWithHint("##outfitname", "Name this outfit…", ref _newOutfitName, 128);

        var saveLabel = $"Save current look ({wornCount} item(s))";
        UiLayout.SameLineIfRoomForButton(saveLabel);

        var canSave = wornCount > 0 && !string.IsNullOrWhiteSpace(_newOutfitName);
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button(saveLabel))
        {
            _wardrobe.SaveCurrentAsOutfit(_newOutfitName);
            _newOutfitName = string.Empty;
        }
        if (!canSave) ImGui.EndDisabled();

        if (wornCount == 0)
        {
            UiLayout.SameLineIfRoomForText("Wear some items first.");
            ImGui.TextDisabled("Wear some items first.");
        }

        if (_session.CanStartOutfits)
        {
            UiLayout.SameLineIfRoomForButton(" Screenshot Outfits ");
            if (ImGui.Button(" Screenshot Outfits "))
                _session.StartOutfits();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Wears each outfit that has no preview yet and waits for a\n" +
                                 "screenshot, exactly like an item session.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawOutfitSourcesBar();

        if (_config.Outfits.Count == 0)
        {
            ImGui.TextDisabled("No outfits saved yet. Wear a look, name it above, then save.");
            return;
        }

        // The same filter row drives both grids, so a style ticked while looking at outfits narrows
        // the outfits rather than quietly doing nothing
        var outfits = _config.Outfits
            // Design cards are hidden rather than deleted while the setting is off, exactly as items in
            // a switched-off mod category are — so turning it back on brings them back untouched
            .Where(o => _config.ShowGlamourerDesigns || !o.IsDesign)
            .Where(OutfitMatchesTagFilters)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (outfits.Count == 0)
        {
            ImGui.TextDisabled("No outfit matches the tags or styles you are filtering on.");
            return;
        }

        // The same card as the item grid's, on its own multiplier — the two grids are looked at
        // differently, and the old Large cards toggle was a two-position version of this
        var cardW = OutfitCardWidth;
        var cardH = OutfitCardHeight;

        // A card clips rather than scrolls, so the style line has to be paid for in height or it
        // pushes the buttons past the bottom edge. Grown for the whole grid rather than per card,
        // so the rows still line up — the same rule the item grid follows for slot icons.
        if (outfits.Any(o => o.Tags.Any(TagTree.IsStyle)))
            cardH += ImGui.GetTextLineHeightWithSpacing();

        // Same rule for the plate cards' apply button: paid for across the grid so the rows still
        // line up, rather than per card, which would leave the plates standing taller than the rest
        if (outfits.Any(o => o.IsGlamourPlate))
            cardH += ImGui.GetFrameHeightWithSpacing();

        // A design card carries a badge line naming the design, and the same rule applies to it: paid
        // for once across the grid, or the button row would be pushed off the bottom of those cards
        if (outfits.Any(o => o.IsDesign))
            cardH += ImGui.GetTextLineHeightWithSpacing();

        var avail   = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((avail + CardPad) / (cardW + CardPad)));
        var col     = 0;
        Outfit? toDelete = null;

        foreach (var outfit in outfits)
        {
            DrawOutfitCard(outfit, cardW, cardH, ref toDelete);
            col++;
            if (col < columns) ImGui.SameLine();
            else col = 0;
        }

        if (toDelete != null)
        {
            // Do not leave the editor pointing at an outfit that no longer exists
            if (_editingOutfit == toDelete) CloseOutfitEdit();
            _outfitImageCache.Remove(toDelete.Id);
            _wardrobe.DeleteOutfit(toDelete);
        }
    }

    /// <summary>
    /// One row for what the outfits grid pulls in from elsewhere — the game's glamour plates — and the
    /// notices for that and for the linked Glamourer designs.
    /// </summary>
    /// <remarks>
    /// One row because the space is the grid's. This used to be a block per source, each with a button,
    /// a sentence of explanation and a separator, which between them pushed the first card most of a
    /// screen down the window on a wardrobe that had never touched either. The explanation is in the
    /// button's tooltip now and the counts are chips beside it.
    /// <para>
    /// Designs have no controls here at all: they are linked rather than synced, so there is nothing to
    /// press. Whether they appear is one switch in Settings, and
    /// <see cref="WardrobeService.ReconcileDesignCards"/> keeps up with Glamourer on its own.
    /// </para>
    /// <para>
    /// The notices below are not compressed the same way. They are the half of this that matters: a
    /// plate quietly serving last week's gear, or a card whose design has been deleted from under the
    /// mods attached to it, is worth the lines it takes to say so — and unlike controls, neither is on
    /// screen unless it has actually happened.
    /// </para>
    /// </remarks>
    private void DrawOutfitSourcesBar()
    {
        // Recomputed every frame so the cards below and the notices here cannot disagree. Cheap: the
        // plate read is cached for half a second in its service, the design read in the IPC.
        var platesLoaded = Plugin.GlamourPlates.PlatesLoaded;
        var plates       = _wardrobe.PlateOutfits();
        var desynced     = platesLoaded ? _wardrobe.DesyncedPlates() : new List<Outfit>();
        var orphanPlates = platesLoaded ? _wardrobe.OrphanedPlates() : new List<Outfit>();
        var newPlates    = platesLoaded ? _wardrobe.UnsyncedPlateCount() : 0;

        _platesOutOfSync.Clear();
        foreach (var o in desynced) _platesOutOfSync.Add(o.Id);

        // The link keeps itself current; this is the one call that makes that true, and it saves only
        // when Glamourer's list has actually changed
        _wardrobe.ReconcileDesignCards();

        var stranded = _config.ShowGlamourerDesigns
            ? _wardrobe.StrandedDesignCards()
            : new List<Outfit>();

        _designsMissing.Clear();
        foreach (var o in stranded) _designsMissing.Add(o.Id);

        DrawPlateSyncControls(platesLoaded, plates.Count, newPlates);

        DrawPlateNotices(orphanPlates, desynced);
        DrawStrandedDesignNotice(stranded);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>The plate half of the sources row: one button and its counts.</summary>
    /// <remarks>
    /// Disabled rather than replaced by a sentence when the client is holding no plate data. The
    /// tooltip is what explains the wait, and it is asked for with <c>AllowWhenDisabled</c> — a
    /// disabled item does not register as hovered otherwise, so the explanation would never appear.
    /// </remarks>
    private void DrawPlateSyncControls(bool loaded, int saved, int unsynced)
    {
        if (!loaded) ImGui.BeginDisabled();

        var label = saved == 0 ? " Sync Plates " : " Resync Plates ";
        if (ImGui.Button(label))
        {
            var (created, updated) = _wardrobe.SyncGlamourPlates();
            _plateSyncStatus = created == 0 && updated == 0
                ? "Plates already up to date."
                : $"Plates: {created} added, {updated} updated.";
            _plateNoticeIgnored = false;
        }

        if (!loaded) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(loaded
                ? "Reads your glamour plates from the game and saves each one as an outfit.\n\n" +
                  "Their contents are the game's and cannot be edited here — resync after\n" +
                  "changing a plate in-game. Names, previews and tags you set are kept."
                : "Waiting on the game for your plates.\n\n" +
                  "Open your Glamour Plate window once — at a summoning bell, an inn or\n" +
                  "the Glamour Dresser — and they can be read from then on." +
                  (saved > 0
                      ? "\n\nThe plates already saved are unaffected and still wearable. They\n" +
                        "just cannot be checked against the game until then."
                      : string.Empty));

        // Both counts as one chip, and nothing at all when there is nothing to count
        var chip = saved > 0 && unsynced > 0 ? $"{saved} plates · {unsynced} new"
                 : saved > 0                 ? $"{saved} plates"
                 : unsynced > 0              ? $"{unsynced} to add"
                                             : string.Empty;

        if (chip.Length > 0)
        {
            UiLayout.SameLineIfRoomForText(chip);
            ImGui.TextDisabled(chip);
        }

        if (string.IsNullOrEmpty(_plateSyncStatus)) return;

        UiLayout.SameLineIfRoomForText(_plateSyncStatus);
        ImGui.TextDisabled(_plateSyncStatus);
    }

    /// <summary>The plate notices: plates emptied in the game, and plates that have drifted.</summary>
    private void DrawPlateNotices(List<Outfit> orphaned, List<Outfit> desynced)
    {
        if (orphaned.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f),
                $"● {orphaned.Count} saved plate(s) are now empty in the game: " +
                string.Join(", ", orphaned.Select(o => o.Name)));
            ImGui.TextDisabled("Their saved copies are kept and still wearable. Delete them yourself if you " +
                               "cleared the plate on purpose.");
        }

        if (desynced.Count > 0 && !_plateNoticeIgnored)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                $"● {desynced.Count} glamour plate(s) have been changed in the game.");
            ImGui.TextDisabled("The saved copies still hold the old gear. Resync to bring them up to date — " +
                               "names, previews and tags are kept.");
            ImGui.Spacing();

            if (ImGui.Button(" Resync All "))
            {
                var (_, updated) = _wardrobe.SyncGlamourPlates();
                _plateSyncStatus = $"Resynced {updated} glamour plate(s).";
            }

            UiLayout.SameLineIfRoomForButton(" Ignore ##plates ");
            if (ImGui.Button(" Ignore ##plates "))
                _plateNoticeIgnored = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hides this until the next sync. Nothing is changed.");

            // Acted on after the loop, so the list being walked is not resynced out from under it
            Outfit? resync = null;

            foreach (var plate in desynced)
            {
                ImGui.PushID($"plate_{plate.Id}");
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(plate.Name);

                var label = $"(plate {plate.GlamourPlateId})";
                UiLayout.SameLineIfRoomForText(label);
                ImGui.TextDisabled(label);

                UiLayout.SameLineIfRoomForButton("Resync");
                if (ImGui.SmallButton("Resync")) resync = plate;
                ImGui.PopID();
            }

            if (resync != null)
            {
                _wardrobe.SyncGlamourPlate(resync);
                _plateSyncStatus = $"Resynced '{resync.Name}'.";
            }
        }
    }

    /// <summary>
    /// The one design notice: cards whose design has been deleted from under them.
    /// </summary>
    /// <remarks>
    /// The only thing about a linked design that can go wrong. There is no drift to report — Glamourer
    /// owns the design and applies it as it stands, and a rename follows through to the card by itself —
    /// but a card holding pictures and mods whose design has gone is worth saying out loud, because
    /// nothing else on screen would explain why it no longer changes your appearance.
    /// <para>
    /// Cards holding nothing are not reported: <see cref="WardrobeService.ReconcileDesignCards"/> has
    /// already dropped those, since there was nothing in them to lose.
    /// </para>
    /// </remarks>
    private void DrawStrandedDesignNotice(List<Outfit> stranded)
    {
        if (stranded.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.78f, 0.6f, 0.95f, 1f),
            $"● {stranded.Count} card(s) have lost their Glamourer design: " +
            string.Join(", ", stranded.Select(o => o.Name)));
        ImGui.TextDisabled("Their pictures, tags and attached items are kept, and the items can still be " +
                           "worn — wearing one just no longer applies a design. Keep them as ordinary " +
                           "outfits from Edit, or forget the cards.");

        ImGui.Spacing();
        if (UiLayout.DeleteButton(" Forget Them ",
                $"Deletes {stranded.Count} card(s) whose design is gone. The wardrobe items " +
                "attached to them are kept, exactly as deleting an outfit keeps its items.",
                UiScale.S(140, 0)))
        {
            var forgotten = _wardrobe.ForgetStrandedDesignCards();
            _designStatus = $"Forgot {forgotten} card(s).";
        }

        if (!string.IsNullOrEmpty(_designStatus))
        {
            UiLayout.SameLineIfRoomForText(_designStatus);
            ImGui.TextDisabled(_designStatus);
        }
    }

    /// <summary>
    /// What a card's name shows on hover: its wardrobe items, or for a plate, the pieces it holds.
    /// </summary>
    /// <remarks>
    /// A plate has no wardrobe items at all, so the ordinary listing would leave its card as the one
    /// thing in the grid that says nothing about its own contents.
    /// </remarks>
    private string OutfitCardTooltip(Outfit outfit, List<WardrobeItem> items)
    {
        if (outfit.IsGlamourPlate)
        {
            var plate = outfit.VanillaItems
                .Select(kv => (Slot: Enum.TryParse<EquipSlot>(kv.Key, out var s) ? s : EquipSlot.Unknown, kv.Value))
                .OrderBy(p => (int)p.Slot)
                .Select(p => $"{p.Slot.DisplayName()} — {p.Value.Name}");
            return $"{outfit.Name}\n\n" + string.Join("\n", plate);
        }

        var listing = items.Count > 0
            ? string.Join("\n", items.Select(i => $"{i.Slot.DisplayName()} — {i.Name}"))
            : string.Empty;

        if (!outfit.IsDesign)
            return listing.Length > 0 ? $"{outfit.Name}\n\n{listing}" : outfit.Name;

        // A design card's two halves are worth keeping apart: the pieces are Glamourer's and the items
        // are the wardrobe's, and someone reading the card wants to know which is doing what
        var text = outfit.Name;

        if (_wardrobe.DesignContents(outfit) is { } contents)
        {
            text += contents.AppliesEquipment switch
            {
                true  => "\n\nThe design applies:\n" + string.Join("\n",
                             contents.Pieces.Select(p => $"{p.Slot.DisplayName()} — {p.Name}")),
                false => "\n\nThe design applies no gear — appearance only.",
                // A non-human design keeps its equipment packed, so there is nothing to list
                null  => "\n\nThe design's equipment cannot be listed (saved on a non-human form).",
            };
        }

        if (listing.Length > 0)
            text += "\n\nWith these items:\n" + listing;

        return text;
    }

    private void DrawOutfitCard(Outfit outfit, float cardW, float cardH, ref Outfit? pendingDelete)
    {
        var items  = _wardrobe.ResolveOutfit(outfit);
        var worn   = _wardrobe.IsOutfitWorn(outfit);
        var partly = _wardrobe.IsOutfitPartlyWorn(outfit);

        var background = new Vector4(0.11f, 0.11f, 0.13f, 1f);
        var border     = new Vector4(0.28f, 0.28f, 0.33f, 1f);

        // Tinted by its style exactly as an item card is, so a grid of outfits sorts by mood the
        // same way. Worn keeps its gold, for the same reason it does there.
        if (!worn && StyleTint(outfit.Tags) is { } tint)
        {
            background = TagTree.Blend(background, tint, 0.3f);
            border     = TagTree.Blend(border,     tint, 0.75f);
        }

        // A plate keeps a border of its own whatever style it is tagged with. The whole point of the
        // colour is to say at a glance which cards the wardrobe does not own, so a style tint must
        // not be able to disguise one — it still tints the background, just not the edge.
        if (outfit.IsGlamourPlate)
            border = new Vector4(0.38f, 0.58f, 0.75f, 1f);

        // Violet for a design, for the same reason and by the same rule: the edge says at a glance
        // that something outside the wardrobe decides what this card is, and a style tint must not be
        // able to disguise that. A different hue from the plates' blue, since they are not the same
        // kind of card — a plate's contents are read-only, a design outfit's items are not.
        if (outfit.IsDesign)
            border = new Vector4(0.6f, 0.44f, 0.82f, 1f);

        ImGui.PushID(outfit.Id.ToString());
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            worn ? new Vector4(0.22f, 0.18f, 0.04f, 1f) : background);
        ImGui.PushStyleColor(ImGuiCol.Border,
            worn ? new Vector4(1f, 0.85f, 0.25f, 1f) : border);

        ImGui.BeginChild($"##outfit_{outfit.Id}", new Vector2(cardW, cardH),
            true, ImGuiWindowFlags.NoScrollbar);

        DrawOutfitImage(outfit, cardW - CardPad * 2);

        var dispName = CardName(outfit.Name);
        ImGui.TextUnformatted(dispName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(OutfitCardTooltip(outfit, items));

        if (worn)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));
            ImGui.TextUnformatted("★");
            ImGui.PopStyleColor();
        }

        if (outfit.IsGlamourPlate)
        {
            // Said in words, not just in the border colour: the border explains itself only to
            // someone who already knows what it means
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.72f, 0.92f, 1f));
            ImGui.TextUnformatted($"Glamour Plate {outfit.GlamourPlateId}");
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("One of the game's own glamour plates, mirrored here.\n" +
                                 "Its contents are edited in-game, not in the wardrobe.");

            if (_platesOutOfSync.Contains(outfit.Id))
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.3f, 1f));
                ImGui.TextUnformatted("●");
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This plate has been changed in the game.\nResync it to bring this copy up to date.");
            }
        }

        // Read once and used twice below — for the badge, and for the piece count on the line after it
        var design = outfit.IsDesign ? _wardrobe.DesignContents(outfit) : null;

        if (outfit.IsDesign)
        {
            var designGone = _designsMissing.Contains(outfit.Id);
            var noGear     = design is { AppliesEquipment: false };

            // Said in words as the plate badge is: the border colour explains itself only to someone
            // who already knows what it means. An appearance-only design says so here rather than
            // leaving a card with no pieces looking like one that failed to load.
            ImGui.PushStyleColor(ImGuiCol.Text, designGone
                ? new Vector4(0.78f, 0.6f, 0.95f, 1f)
                : new Vector4(0.72f, 0.56f, 0.92f, 1f));
            ImGui.TextUnformatted(CardName(designGone ? $"Design missing: {outfit.DesignName}"
                                         : noGear     ? $"Design (looks only): {outfit.DesignName}"
                                                      : $"Design: {outfit.DesignName}"));
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(designGone
                    ? $"'{outfit.DesignName}' no longer exists in Glamourer.\n" +
                      "Wearing this still applies the items attached to it, but no design."
                    : noGear
                    ? $"'{outfit.DesignName}' sets no equipment at all — a face, a body or\n" +
                      "colouring. Wearing this changes your appearance and leaves your\n" +
                      "clothes to the items attached to it."
                    : $"Wearing this applies the Glamourer design '{outfit.DesignName}'" +
                      (outfit.DesignAppliesEquipment ? " in full,\n" : " — customisations only —\n") +
                      "then any items attached to it over the top.");
        }

        // A deleted item leaves a gap in the outfit; say so rather than quietly wearing fewer
        var missing = outfit.ItemIds.Count - items.Count;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.75f, 0.95f, 1f));

        // A design card counts both halves: the pieces the design puts on you, and the items whose mods
        // the wardrobe enables with it. Nothing about the pieces while they are still being read —
        // an unread design is not a design with nothing in it.
        var counts = $"{items.Count} items" + (missing > 0 ? $" · {missing} missing" : string.Empty);
        ImGui.TextUnformatted(
            outfit.IsGlamourPlate            ? $"{outfit.VanillaItems.Count} pieces"
            : design is { AppliesEquipment: true } d ? $"{d.Pieces.Count} pieces · {counts}"
            : design is { AppliesEquipment: false }  ? $"looks only · {counts}"
                                                    : counts);
        ImGui.PopStyleColor();

        DrawOutfitStyleLabels(outfit, cardW);

        if (partly)
        {
            var wornOfOutfit = items.Count(_wardrobe.IsItemWorn);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.3f, 1f));
            ImGui.TextUnformatted($"{wornOfOutfit} still on");
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Part of this outfit is still applied:\n" +
                                 string.Join("\n", items.Where(_wardrobe.IsItemWorn)
                                     .Select(i => $"{i.Slot.DisplayName()} — {i.Name}")));
        }

        var btnW = (cardW - CardPad * 2 - UiScale.S(6f)) / 2;

        if (worn)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Remove", new Vector2(btnW, 0)))
                _wardrobe.UnwearOutfit(outfit);
            ImGui.PopStyleColor(2);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.38f, 0.13f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.55f, 0.18f, 1f));
            if (ImGui.Button("Wear", new Vector2(btnW, 0)))
                _wardrobe.WearOutfit(outfit, removeOthers: false);
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(outfit.IsGlamourPlate
                    ? "Shows this plate's gear through Glamourer, leaving anything else you have on in place.\n\n" +
                      "This is not the game applying the plate: your real glamour and equipment are\n" +
                      "untouched and only you see the change. In exchange it works anywhere — no\n" +
                      "summoning bell, no gearset — including gpose."
                    : outfit.IsDesign
                    ? $"Applies the design '{outfit.DesignName}', then any items attached to it.\n\n" +
                      "The design goes on first, so an attached item always wins the slot it\n" +
                      "occupies and the design dresses everything else."
                    : "Wear these items, leaving anything else you have on in place.");
        }

        ImGui.SameLine();

        // Update takes the place of "Only this" while the outfit is on the character. That button
        // means "wear these and drop the rest", which is the one control the state does not need —
        // Wear beside it already puts back whatever a swap took off — and this is exactly when
        // there is something to save. Handing it the slot keeps the card from growing a row it has
        // no vertical room for.
        if (worn || partly)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.32f, 0.5f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.44f, 0.68f, 1f));
            if (ImGui.Button("Update", new Vector2(btnW, 0)))
                _wardrobe.UpdateOutfitFromWorn(outfit);
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Saves what you are wearing now as this outfit,\n" +
                                 "so pieces swapped by hand are kept.\n" +
                                 "Dyes stay with the items that remain, and a piece\n" +
                                 "swapped into a slot takes over that slot's dye.");
        }
        else
        {
            if (ImGui.Button("Only this", new Vector2(btnW, 0)))
                _wardrobe.WearOutfit(outfit, removeOthers: true);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Wear these items and remove everything else the wardrobe has on.");
        }

        // On the card rather than buried in the editor: applying a plate is one click in the game's
        // own gear set list, and a wardrobe that mirrors those plates should not make it harder
        if (outfit.IsGlamourPlate) DrawApplyPlateButton(outfit, cardW - CardPad * 2);

        if (ImGui.SmallButton("Edit"))
            OpenOutfitEdit(outfit);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(outfit.IsGlamourPlate
                ? "Rename it, set a preview, take a photo, and resync it from the game.\n\n" +
                  "Right-click to copy it into an editable outfit."
                : outfit.IsDesign
                ? "Rename it, set a preview, take a photo, and attach the mods that\n" +
                  "belong with this design.\n\n" +
                  "Right-click to copy it into an outfit with no design attached."
                : "Rename it, set a preview, take a photo, and add or remove items.\n\n" +
                  "Right-click to duplicate it.");

        // On the Edit button rather than the card body, which is plain text and has no click target
        // of its own. The grid is where you notice you want a variant of something.
        if (ImGui.BeginPopupContextItem($"##outfitctx_{outfit.Id}"))
        {
            // A plate gets the one copy that makes sense for it: a wardrobe outfit cut loose from
            // the game. Duplicating it as another plate would only make a second card fighting over
            // the same plate number.
            if (outfit.IsGlamourPlate)
            {
                if (ImGui.MenuItem("Duplicate as editable outfit"))
                    OpenOutfitEdit(_wardrobe.DuplicateAsOutfit(outfit));
            }
            else if (outfit.IsDesign)
            {
                // The same one copy that makes sense for a plate, for the same reason: a second card
                // claiming the same design would leave the sync refreshing one and ignoring the other.
                // What the copy is for here is a look that has outgrown its design.
                if (ImGui.MenuItem("Duplicate without the design"))
                    OpenOutfitEdit(_wardrobe.DuplicateAsOutfit(outfit));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Copies the items, dyes and tags into an ordinary outfit.\n" +
                                     "The copy applies no design, and resyncing never touches it.");
            }
            else if (ImGui.MenuItem("Duplicate"))
            {
                OpenOutfitEdit(_wardrobe.DuplicateOutfit(outfit));
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();

        // A partly-worn outfit shows Wear above, so without this there would be nothing to press to
        // take off what is still applied — most often the animation a Strip deliberately left running
        if (partly)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.45f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.12f, 0.12f, 1f));
            if (ImGui.SmallButton("Remove"))
                _wardrobe.UnwearOutfit(outfit);
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Takes off the parts of this outfit that are still applied,\n" +
                                 "including any animation or VFX mods still enabled.");
            ImGui.SameLine();
        }

        // A design card cannot be deleted while its design exists — the link would put it straight back
        // — so the button is described as what it actually does there: empties the card out
        if (DeleteButton("X", outfit.IsDesign && !_designsMissing.Contains(outfit.Id)
                ? "Clears everything the wardrobe keeps for this design: its pictures, tags, dyes and " +
                  "attached items. The card itself stays while the design exists in Glamourer, and the " +
                  "items themselves are kept.\n\nTo remove the card, delete the design in Glamourer — or " +
                  "turn the whole set off in Settings."
                : "Deletes the outfit only. The items themselves are kept."))
            pendingDelete = outfit;

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopID();
    }

    /// <summary>
    /// Editing panel for one outfit: rename, set a preview, and manage its members as a list of
    /// rows with a thumbnail, an equip toggle and a remove control.
    /// </summary>
    private void DrawOutfitEditPanel()
    {
        var outfit = _editingOutfit;
        if (outfit == null) return;

        if (DrawPanelHeader("Edit Outfit"))
        {
            CloseOutfitEdit();
            return;
        }

        // Large preview, as the item edit panel does
        var previewW = ImGui.GetContentRegionAvail().X;
        if (!string.IsNullOrEmpty(_editOutfitImage) && File.Exists(_editOutfitImage))
        {
            try
            {
                if (_textures.GetFromFile(_editOutfitImage).GetWrapOrDefault() is { } wrap)
                {
                    if (_config.PortraitOutfitPreviews) ImageDraw.Portrait(wrap, previewW);
                    else                                ImageDraw.Square(wrap, previewW);

                    // As on the card and in the item editor: the panel is a narrow column, and full
                    // size is the whole window
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Right-click to view full size.");

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        _quickViewOutfit = outfit.Id;

                    ImGui.Spacing();
                }
            }
            catch { /* falls through to no preview */ }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##outfitEditName", ref _editOutfitName, 128);

        ImGui.Spacing();
        ImGui.TextDisabled("Image path (optional)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##outfitEditImage", ref _editOutfitImage, 512);

        // As in the item editor: applied to the outfit as they are made rather than staged for Save,
        // since dropping a picture on and pressing Cancel should not undo the picture
        ImGui.Spacing();
        ImageGallery.Draw($"outfit_{outfit.Id}", outfit, _textures, UiScale.S(56f), () =>
        {
            _editOutfitImage = outfit.ImagePath ?? string.Empty;
            _outfitImageCache.Remove(outfit.Id);
            _config.Save();
        });

        var items = _wardrobe.ResolveOutfit(outfit);

        // Duplicating swaps the panel to the copy, so the rest of this frame would be drawing
        // controls for an outfit the editor no longer points at
        if (outfit.IsGlamourPlate && DrawGlamourPlateEditHeader(outfit)) return;
        if (outfit.IsDesign      && DrawDesignEditHeader(outfit)) return;

        // Vanilla pieces count as something to photograph. Without them the check hid the button on
        // any outfit made only of plain gear — every glamour plate, and every look saved before a
        // single mod went into it.
        var hasSomethingToWear = items.Count > 0 || outfit.VanillaItems.Count > 0;

        ImGui.Spacing();
        if (_session.FoldersReady && hasSomethingToWear)
        {
            if (ImGui.Button("Take Screenshot", new Vector2(-1, 0)))
            {
                // Save first, so the shot is filed under the name shown here
                outfit.Name      = string.IsNullOrWhiteSpace(_editOutfitName) ? outfit.Name : _editOutfitName.Trim();
                outfit.ImagePath = string.IsNullOrWhiteSpace(_editOutfitImage) ? null : _editOutfitImage.Trim();
                _config.Save();
                _session.StartSingleOutfit(outfit);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Wears this outfit on its own and waits for a screenshot,\n" +
                                 "then crops and assigns it as the outfit's image.");

            // One set for every outfit, as slot presets are one set per slot: the angle that frames
            // one whole look frames the next
            ImGui.Spacing();
            DrawCameraPresetControls(Configuration.OutfitPresetKey, "Outfits", "for every outfit");
        }
        else if (!hasSomethingToWear)
        {
            ImGui.TextDisabled("Add items before taking a screenshot.");
        }
        else
        {
            ImGui.TextDisabled("Set the images and screenshots folders to enable screenshots.");
        }

        // Everything from here to the tags is about editing the outfit's contents, which for a plate
        // belong to the game. Skipped wholesale rather than disabled: a column of greyed-out
        // controls invites the reader to look for the one that would turn them back on.
        if (!outfit.IsGlamourPlate)
            DrawOutfitContentsEditor(outfit, items);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawOutfitVanillaItems(outfit);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawOutfitTags(outfit);

        if (!outfit.IsGlamourPlate)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawAddToOutfit(outfit);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Above Save rather than beside it: this makes a second outfit and moves the panel onto it,
        // which is a different kind of act from the two that close the panel on the one you opened.
        // Not offered for plates — copying one means cutting it loose from the game, which is what
        // Duplicate As Editable Outfit does further up. A plain copy would only make a second outfit
        // claiming the same plate number, which the sync would then quietly ignore.
        if (!outfit.IsGlamourPlate)
        {
            if (ImGui.Button("Duplicate This Outfit", new Vector2(-1, 0)))
            {
                // Saved first, so a rename typed but not yet committed is what gets copied — the
                // panel is about to move to the copy, and the edit would otherwise be thrown away
                outfit.Name      = string.IsNullOrWhiteSpace(_editOutfitName) ? outfit.Name : _editOutfitName.Trim();
                outfit.ImagePath = string.IsNullOrWhiteSpace(_editOutfitImage) ? null : _editOutfitImage.Trim();

                OpenOutfitEdit(_wardrobe.DuplicateOutfit(outfit));
                return;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Makes a copy of this outfit — items, dyes, vanilla pieces, tags and\n" +
                                 "preview — and opens it, so a variant starts from the finished look\n" +
                                 "rather than from nothing.\n\n" +
                                 "The original is left exactly as it is.");

            ImGui.Spacing();
        }

        var footerW = (ImGui.GetContentRegionAvail().X - 8) / 2;
        if (ImGui.Button("Save", new Vector2(footerW, 0)))
        {
            outfit.Name      = string.IsNullOrWhiteSpace(_editOutfitName) ? outfit.Name : _editOutfitName.Trim();
            outfit.ImagePath = string.IsNullOrWhiteSpace(_editOutfitImage) ? null : _editOutfitImage.Trim();
            _config.Save();
            CloseOutfitEdit();
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(footerW, 0)))
        {
            CloseOutfitEdit();
        }
    }

    /// <summary>
    /// Says what a design card is, and offers what can be done to one: apply the design now, choose how
    /// much of it to apply, or keep the card as an ordinary outfit with no design behind it.
    /// </summary>
    /// <remarks>
    /// Shorter than the plate header has to be, because nothing here is read-only in the way a plate's
    /// pieces are: the items below are the wardrobe's own. That is the whole point of a design card — a
    /// design carries gear and colouring but knows nothing about Penumbra, so attaching items here is
    /// what makes the mods that belong with the look go on with it.
    /// <para>
    /// No resync control, and no name field worth offering: the card is a live link, so its name is the
    /// design's name and follows it. Someone who wants it called something else renames the design.
    /// </para>
    /// </remarks>
    /// <returns>True when the panel has moved to a different outfit and should stop drawing.</returns>
    private bool DrawDesignEditHeader(Outfit outfit)
    {
        var missing = !_wardrobe.DesignIsLive(outfit);

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.72f, 0.56f, 0.92f, 1f),
            $"● Linked to the Glamourer design '{outfit.DesignName}'.");

        if (missing)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                "That design no longer exists in Glamourer.");
            ImGui.TextWrapped("Wearing this still applies the items attached to it, but no design. Nothing " +
                              "here has been deleted — keep it as an ordinary outfit below, or delete the " +
                              "card from the grid.");
        }
        else
        {
            ImGui.TextWrapped("Glamourer owns the design and applies it as it stands there, so there is nothing " +
                              "to sync and nothing here that can go stale — rename it in Glamourer and this " +
                              "card follows. The items below are the wardrobe's: attach the mods that belong " +
                              "with this look and they go on over the top, so an item always wins the slot it " +
                              "occupies.");
        }

        // What the design actually puts on the character, above the switch that decides how much of it
        // to apply — the answer to "should I turn this off" is the list itself
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawDesignPieces(outfit);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // A design with no gear in it has nothing for this switch to act on, so it is disabled rather
        // than left as a control that appears to do something
        var noGear = _wardrobe.DesignContents(outfit) is { AppliesEquipment: false };

        if (noGear) ImGui.BeginDisabled();

        var equipment = outfit.DesignAppliesEquipment;
        if (ImGui.Checkbox("Apply the design's equipment too", ref equipment))
        {
            outfit.DesignAppliesEquipment = equipment;
            _config.Save();
        }

        if (noGear) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(noGear
                ? "This design sets no equipment, so there is nothing for this to apply\n" +
                  "either way. Whatever you attach below is all that will be worn."
                : "On: the design is applied whole — its gear as well as its face,\n" +
                  "body and colouring. This is what you want for a design that is\n" +
                  "the whole look.\n\n" +
                  "Off: only its customisations are applied, so it can carry a face\n" +
                  "or a body without emptying the slots this card's own items and\n" +
                  "vanilla pieces are there to fill.");

        ImGui.Spacing();

        if (missing) ImGui.BeginDisabled();
        if (ImGui.Button("Apply Design Now", new Vector2(-1, 0)))
            _designStatus = _wardrobe.ApplyOutfitDesign(outfit)
                ? $"Applied '{outfit.DesignName}'."
                : $"Glamourer would not apply '{outfit.DesignName}'.";
        if (missing) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(missing
                ? "The design is gone from Glamourer, so there is nothing to apply."
                : "Applies the design on its own, without touching the items attached to it.\n" +
                  "For putting the look back after something else has changed the character.");

        if (ImGui.Button("Duplicate Without The Design", new Vector2(-1, 0)))
        {
            OpenOutfitEdit(_wardrobe.DuplicateAsOutfit(outfit));
            return true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies the items, dyes and tags into an ordinary outfit that applies\n" +
                             "no design. For a look that has outgrown the design behind it.\n\n" +
                             "This card is left exactly as it is.");

        if (!string.IsNullOrEmpty(_designStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_designStatus);
        }

        return false;
    }

    /// <summary>
    /// Says what a plate outfit is, and offers the two things that can still be done to one:
    /// bring it back in step with the game, or copy it somewhere editable.
    /// </summary>
    /// <returns>True when the panel has moved to a different outfit and should stop drawing.</returns>
    private bool DrawGlamourPlateEditHeader(Outfit outfit)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.72f, 0.92f, 1f),
            $"● Mirrors glamour plate {outfit.GlamourPlateId}.");
        ImGui.TextWrapped("Its pieces are the game's, so they are shown here but not editable — change the " +
                          "plate in-game and resync. The name, preview image and tags are yours and are " +
                          "kept through every resync.");

        ImGui.Spacing();
        ImGui.TextDisabled("Wearing it shows the plate's gear through Glamourer. Your real glamour and " +
                           "equipment are untouched and only you see it, which is why it works anywhere — " +
                           "no summoning bell, gpose included.");

        if (outfit.PlateSyncedAt is { } synced)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Last synced {synced.ToLocalTime():g}.");
        }

        ImGui.Spacing();

        var loaded = Plugin.GlamourPlates.PlatesLoaded;
        if (!loaded) ImGui.BeginDisabled();
        if (ImGui.Button("Resync From Game", new Vector2(-1, 0)))
        {
            if (!_wardrobe.SyncGlamourPlate(outfit))
                _plateSyncStatus = $"Plate {outfit.GlamourPlateId} is empty in the game — nothing to read.";
        }
        if (!loaded) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(loaded
                ? "Reads this plate from the game again, replacing the pieces below."
                : "Open your Glamour Plate window once — at a summoning bell, an inn\n" +
                  "or the Glamour Dresser — to let the game hand over your plates.");

        if (ImGui.Button("Duplicate As Editable Outfit", new Vector2(-1, 0)))
        {
            OpenOutfitEdit(_wardrobe.DuplicateAsOutfit(outfit));
            return true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies these pieces into an ordinary outfit you can edit and add\n" +
                             "wardrobe items to. Resyncing this plate will never touch the copy.");

        if (!string.IsNullOrEmpty(_plateSyncStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_plateSyncStatus);
        }

        DrawApplyPlateInGame(outfit);

        return false;
    }

    /// <summary>
    /// The real thing: hands the plate to the game to apply, rather than showing it through Glamourer.
    /// </summary>
    /// <remarks>
    /// One click, no confirmation, exactly as applying a plate from the game's own gear set list is.
    /// A guard was tried and taken back off: the wardrobe is mirroring a feature the game gives you
    /// in one click, and making its copy harder to use than the original is not caution, it is just
    /// friction. The undo is the same as the game's, too — apply a different plate.
    /// </remarks>
    private bool DrawApplyPlateButton(Outfit outfit, float width)
    {
        if (outfit.GlamourPlateId is not { } plateId) return false;

        var canApply = Plugin.GlamourPlates.CanApplyInGame(out var reason);

        // Gold rather than the green Wear carries: the two sit next to each other and do very
        // different things, and the colour is the fastest way to say which one is the real apply
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.30f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.60f, 0.44f, 0.12f, 1f));

        if (!canApply) ImGui.BeginDisabled();
        var clicked = ImGui.Button($"Apply In Game##plate{plateId}", new Vector2(width, 0));
        if (!canApply) ImGui.EndDisabled();

        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canApply
                ? $"Applies glamour plate {plateId} for real, over the gear set you have equipped —\n" +
                   "the same thing the game does from its Gear Set List.\n\n" +
                   "Your gear and job do not change, only the glamour. Everyone sees it,\n" +
                   "and it stays until you change it again.\n\n" +
                   "The wardrobe then takes its own clothes off so you can see the result" +
                   (_config.KeepBaseCharacterOnRevert && _config.ActiveBaseCharacter is { } b
                       ? $",\nkeeping '{b.Name}' on top."
                       : ".") + "\n\n" +
                   "A weapon your current job cannot equip will not glamour; the game says so."
                : reason);

        if (clicked)
        {
            _wardrobe.ApplyPlateInGame(outfit, out var message);
            _plateApplyStatus = message;
        }

        return clicked;
    }

    /// <summary>
    /// Whether the base character survives a revert to the in-game look.
    /// </summary>
    /// <remarks>
    /// Drawn in two places on purpose. It belongs with the base character in settings, since that is
    /// what it is about and the toolbar's In-Game Look honours it whether or not you own a single
    /// glamour plate; and it belongs beside the plate's own revert, where the question actually
    /// occurs to people. One setting, two doors.
    /// </remarks>
    private void DrawKeepBaseOnRevertSetting(BaseCharacter baseChar)
    {
        var keep = _config.KeepBaseCharacterOnRevert;
        if (ImGui.Checkbox($"Keep '{baseChar.Name}' on top##keepBaseOnRevert", ref keep))
        {
            _config.KeepBaseCharacterOnRevert = keep;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts your base character back on after a revert — the hair, skin,\n" +
                             "tail and design that make the character yours, over the game's gear.\n\n" +
                             "Off, the revert is absolute: you see the unmodded character the\n" +
                             "server sees.\n\n" +
                             "Applies everywhere the wardrobe reverts: the In-Game Look button\n" +
                             "in the toolbar, and applying a glamour plate in game.");
    }

    /// <summary>The same apply, with the explanation the edit panel has room for.</summary>
    private void DrawApplyPlateInGame(Outfit outfit)
    {
        if (!outfit.IsGlamourPlate) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Apply in game");
        ImGui.TextWrapped("Wearing, above, is a Glamourer preview only you can see. This instead asks the " +
                          "game to apply the plate for real, over the gear set you have equipped: your " +
                          "actual glamour changes, everyone sees it, and it stays until you change it " +
                          "again. Your gear and job are not touched.");

        ImGui.Spacing();
        ImGui.TextDisabled("The wardrobe then takes its own clothes off, since a Glamourer override sits " +
                           "on top of the game and would hide the plate you just applied.");

        if (_config.KeepBaseCharacterOnRevert && _config.ActiveBaseCharacter is { } plateBase)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"'{plateBase.Name}' goes back on over the top — its items, its design's " +
                               "customisations, and whatever it keeps in its slots. The plate is the " +
                               "clothes; the base is still the character wearing them.");
        }

        ImGui.Spacing();
        DrawApplyPlateButton(outfit, -1);

        if (!Plugin.GlamourPlates.CanApplyInGame(out var reason))
        {
            ImGui.Spacing();
            ImGui.TextDisabled(reason);
        }

        ImGui.Spacing();
        DrawRevertToInGameLook();

        if (!string.IsNullOrEmpty(_plateApplyStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_plateApplyStatus);
        }
    }

    /// <summary>
    /// The revert on its own, plus the choice of whether the character survives it.
    /// </summary>
    /// <remarks>
    /// Separate from applying a plate because it is useful without one: "show me what I actually
    /// look like to everyone else" is a question the wardrobe can otherwise only answer by taking
    /// every item off by hand.
    /// </remarks>
    private void DrawRevertToInGameLook()
    {
        var baseChar = _config.ActiveBaseCharacter;

        if (baseChar != null) DrawKeepBaseOnRevertSetting(baseChar);
        else                  ImGui.TextDisabled("No base character is set, so a revert shows the unmodded character.");

        ImGui.Spacing();

        if (ImGui.Button("Show In-Game Look", new Vector2(-1, 0)))
        {
            var removed = _wardrobe.RevertToInGameLook();
            _plateApplyStatus = removed > 0
                ? $"Took off {removed} item(s). You are now showing the game's own look."
                : "You were already showing the game's own look.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Takes the wardrobe's clothes off and clears Glamourer, so the character\n" +
                             "shows exactly what the game has on them — plate included.\n\n" +
                             "Animations, VFX and mounts are left running.");
    }

    /// <summary>
    /// The editable half of the outfit panel: update from worn, the item rows and their dyes.
    /// </summary>
    private void DrawOutfitContentsEditor(Outfit outfit, List<WardrobeItem> items)
    {
        ImGui.Spacing();
        DrawUpdateFromWorn(outfit);

        ImGui.Spacing();
        ImGui.Separator();

        var missing = outfit.ItemIds.Count - items.Count;

        ImGui.TextUnformatted($"Items  ({items.Count})");
        if (missing > 0)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                $"{missing} item(s) in this outfit no longer exist.");

        ImGui.Spacing();

        DrawOutfitDyeAll(outfit, items);

        Guid? removeId = null;
        const float rowThumb = 56f;

        foreach (var item in items)
        {
            ImGui.PushID(item.Id.ToString());

            var top  = ImGui.GetCursorPos();
            var worn = _wardrobe.IsItemWorn(item);

            DrawOutfitRowThumb(item, rowThumb);

            // Text and controls sit to the right of the thumbnail
            ImGui.SetCursorPos(new Vector2(top.X + rowThumb + 8, top.Y));
            ImGui.TextUnformatted(item.Name.Length > 24 ? item.Name[..22] + "…" : item.Name);

            // The name opens the item, as well as the Edit button below. Clicking the thing you
            // want to work on is the guess most people make first, and the button is there for
            // everyone who does not.
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{item.Name}\n\nClick to edit this item.");
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (ImGui.IsItemClicked()) OpenItemEditor(item);

            ImGui.SetCursorPos(new Vector2(top.X + rowThumb + 8, top.Y + ImGui.GetTextLineHeightWithSpacing()));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.75f, 0.95f, 1f));
            ImGui.TextUnformatted(item.Slot.DisplayName());
            ImGui.PopStyleColor();

            ImGui.SetCursorPos(new Vector2(top.X + rowThumb + 8, top.Y + ImGui.GetTextLineHeightWithSpacing() * 2 + 2));

            var (wearLabel, removeLabel) = item.Slot.ActionLabels("Equip");
            var rowDye                   = WardrobeService.GetDye(outfit, item.Id);

            if (worn)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.08f, 0.08f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
                if (ImGui.SmallButton(removeLabel))
                    _wardrobe.UnwearItem(item);
                ImGui.PopStyleColor(2);
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.38f, 0.13f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.55f, 0.18f, 1f));
                if (ImGui.SmallButton(wearLabel))
                    _wardrobe.WearItem(item);
                ImGui.PopStyleColor(2);
            }

            // Plain Equip deliberately leaves the piece undyed, so the dyes can be judged against
            // the bare item. This applies the outfit's dyes without wearing the whole outfit.
            if (rowDye != null)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.32f, 0.5f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.44f, 0.68f, 1f));
                if (ImGui.SmallButton("+ Dye"))
                    _wardrobe.WearItem(item, rowDye);
                ImGui.PopStyleColor(2);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Equip this item with the dyes set below,\n" +
                                     "without wearing the rest of the outfit.");
            }

            // Small, to sit level with the rest of the row's buttons rather than towering over them
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit"))
                OpenItemEditor(item);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open this item's own panel — mod options, tags, image, slot.\n\n" +
                                 "Closing it comes back here.");

            // A × rather than "Remove from outfit": four labelled buttons ran off the edge of the
            // panel, and this is the one whose meaning a tooltip can carry
            ImGui.SameLine();
            if (DeleteButton("×", "Take this item out of the outfit.\nThe item itself is kept."))
                removeId = item.Id;

            // Dyes sit below the row, full width, since two pickers do not fit beside the thumbnail
            ImGui.SetCursorPos(new Vector2(top.X, top.Y + rowThumb + 6));

            // Dyes are a property of an equipped game item, so anything without one — hair, an
            // animation, a mount — has nothing to dye
            if (item.Slot.IsModOnly())
            {
                ImGui.TextDisabled($"    {item.Slot.DisplayName()} mods cannot be dyed.");
            }
            else
            {
                var s1 = rowDye?.Stain1 ?? 0;
                var s2 = rowDye?.Stain2 ?? 0;

                var half   = (ImGui.GetContentRegionAvail().X - 8) / 2;
                var dyeTop = ImGui.GetCursorPos();

                if (DrawDyePicker($"dye1_{item.Id}", "Dye 1", s1, half, out var newS1))
                    _wardrobe.SetDye(outfit, item.Id, newS1, s2);

                // Positioned explicitly rather than with SameLine: the first picker is a group,
                // and SameLine aligns to the group's baseline, which leaves the second one lower.
                var afterDye = ImGui.GetCursorPos();
                ImGui.SetCursorPos(new Vector2(dyeTop.X + half + 8, dyeTop.Y));

                if (DrawDyePicker($"dye2_{item.Id}", "Dye 2", s2, half, out var newS2))
                    _wardrobe.SetDye(outfit, item.Id, s1, newS2);

                ImGui.SetCursorPos(new Vector2(dyeTop.X, afterDye.Y));

                DrawAdvancedDyes(outfit, item, worn, rowDye);
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PopID();
        }

        if (removeId.HasValue)
        {
            outfit.ItemIds.Remove(removeId.Value);
            _config.Save();
        }
    }

    /// <summary>
    /// Saves whatever is worn right now as the outfit's contents.
    /// </summary>
    /// <remarks>
    /// The counterpart to editing the list by hand: wear the outfit, swap pieces on the character
    /// until it looks right, then keep the result. Seeing a swap is the only way to judge it, and
    /// reproducing it by adding and removing rows here means doing the same work twice.
    /// </remarks>
    private void DrawUpdateFromWorn(Outfit outfit)
    {
        var wornCount = _config.WornItems.Values.Distinct().Count();

        if (wornCount == 0)
        {
            ImGui.TextDisabled("Wear something to update this outfit from what you have on.");
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.32f, 0.5f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.44f, 0.68f, 1f));
        if (ImGui.Button($"Update from what I'm wearing  ({wornCount})", new Vector2(-1, 0)))
            _wardrobe.UpdateOutfitFromWorn(outfit);
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Replaces this outfit's items with everything you have on,\n" +
                             "so pieces swapped by hand are kept.\n\n" +
                             "Items still in the outfit keep their dyes, and a piece swapped\n" +
                             "into a slot takes over the dye the old one had there.\n" +
                             "The name and preview image are left alone.");
    }

    /// <summary>
    /// Takes the design's own dyes onto the mods attached to a design card.
    /// </summary>
    /// <remarks>
    /// A mod attached to a design card is there to make sure the look's mods are enabled, so the colour
    /// it should be dyed is the one the design already uses in that slot. Getting that by hand means
    /// reading the design's dye off the list above and finding it again in the picker below, for every
    /// piece — and a wrong colour is the mistake least visible on screen, because the piece is right.
    /// <para>
    /// A button rather than continuous inheritance: the design's colours can change in Glamourer at any
    /// time, and following them automatically would overwrite a colour deliberately chosen for a mod.
    /// New items inherit as they are attached, which covers the case that matters without ever
    /// overwriting anything.
    /// </para>
    /// </remarks>
    private void DrawInheritDesignDyes(Outfit outfit)
    {
        if (!outfit.IsDesign) return;

        var count = _wardrobe.DesignDyeableCount(outfit);
        if (count == 0) return;

        if (ImGui.Button($"Take dyes from the design  ({count})", new Vector2(-1, 0)))
        {
            var applied = _wardrobe.InheritDesignDyes(outfit);
            _designStatus = $"{applied} item(s) took the design's dyes.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dyes each attached item the colour the design uses in its slot.\n\n" +
                             "Items inherit that colour as you attach them, so this is for catching up\n" +
                             "after the design's own dyes have changed in Glamourer — or for putting a\n" +
                             "colour back after changing one here.");

        ImGui.Spacing();
    }

    /// <summary>
    /// Dye pickers that set one channel across every dyeable item in the outfit at once.
    /// </summary>
    /// <remarks>
    /// Sits above the item list because it is the usual first move — pick the outfit's dye, then
    /// change the few pieces that should differ. The per-item pickers below write the same store,
    /// so an edit there simply shows up here as "Mixed".
    /// </remarks>
    private void DrawOutfitDyeAll(Outfit outfit, List<WardrobeItem> items)
    {
        // With nothing dyeable in the outfit there is nothing for these to act on
        if (!items.Any(i => !i.Slot.IsModOnly())) return;

        DrawInheritDesignDyes(outfit);

        ImGui.TextDisabled("Dye all items");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sets this dye on every dyeable item in the outfit.\n" +
                             "Individual items can still be changed below.");

        var half   = (ImGui.GetContentRegionAvail().X - 8) / 2;
        var dyeTop = ImGui.GetCursorPos();

        if (DrawDyePicker("dyeall1", "Dye 1", _wardrobe.CommonDye(outfit, 1), half, out var all1))
            _wardrobe.SetDyeAll(outfit, 1, all1);

        // Positioned explicitly rather than with SameLine, for the same reason as the per-item row
        var afterDye = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(dyeTop.X + half + 8, dyeTop.Y));

        if (DrawDyePicker("dyeall2", "Dye 2", _wardrobe.CommonDye(outfit, 2), half, out var all2))
            _wardrobe.SetDyeAll(outfit, 2, all2);

        ImGui.SetCursorPos(new Vector2(dyeTop.X, afterDye.Y));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Dye picker for one channel, showing a colour swatch beside the dye name.
    /// </summary>
    /// <remarks>
    /// Colours come from the game's Stain sheet, so the swatch matches what the dye actually looks
    /// like rather than a guess. A null <paramref name="current"/> means the items being covered do
    /// not agree on a dye, which shows as "Mixed" and makes any pick count as a change — otherwise
    /// picking Undyed over a mix would read as no change and quietly do nothing.
    /// Returns true when a different dye was chosen.
    /// </remarks>
    /// <summary>
    /// The dyeing the two channels above cannot do: hand off to Glamourer's editor, then keep what
    /// it produced.
    /// </summary>
    /// <remarks>
    /// Deliberately three small controls rather than an editor. Glamourer owns the colour table and
    /// the live preview, and duplicating either would mean guessing at how many materials the loaded
    /// model has. What the wardrobe adds is memory: the rows are Glamourer's, and this is where they
    /// become part of the outfit.
    /// <para>
    /// All of it needs the piece equipped. A colour row addresses the material of whatever occupies
    /// the slot right now, so there is nothing to capture from, and nothing to show it on, until the
    /// item is on the character.
    /// </para>
    /// </remarks>
    private void DrawAdvancedDyes(Outfit outfit, WardrobeItem item, bool worn, OutfitDye? dye)
    {
        // Off by default and hidden entirely when off. Captured rows stay on the outfit, so turning
        // it back on finds them where they were left.
        if (!_config.AdvancedDyesEnabled) return;

        var rows  = dye?.Advanced ?? NoAdvancedDyes;
        var saved = rows.Count > 0;

        ImGui.Spacing();

        // Turning it on has to read from the character, so it needs the piece worn. Turning it off
        // never does, and a saved row you cannot get rid of without equipping the item first would
        // be a trap.
        var locked = !saved && !worn;
        if (locked) ImGui.BeginDisabled();

        var on = saved;
        if (ImGui.Checkbox($"Advanced dyes##adv{item.Id}", ref on))
        {
            if (on)
                _advancedDyeCaptured = (item.Id, _wardrobe.CaptureAdvancedDyes(outfit, item));
            else
            {
                _wardrobe.ClearAdvancedDyes(outfit, item);
                _advancedDyeCaptured = null;
            }
        }

        if (locked) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(locked
                ? "Equip this piece first — advanced dyes are read from the\n" +
                  "character as they are now, not from a row in an outfit."
                : "Keeps this piece's advanced dyes with the outfit and puts\n" +
                  "them back whenever it is worn. Untick to forget them and\n" +
                  "return the material to its game colours.");

        // The palette icon rather than a sentence: the button is a door to Glamourer, and the row
        // is already carrying two dye pickers and three buttons above it. IconButton places itself
        // on the line, so there is no SameLine here.
        if (!worn) ImGui.BeginDisabled();
        if (IconButton(FontAwesomeIcon.Palette, $"advopen{item.Id}",
                "Open Glamourer on your character.\nThe palette icon beside the slot's dyes is the editor."))
            Plugin.Glamourer.OpenOnPlayer();

        if (saved && IconButton(FontAwesomeIcon.Sync, $"advre{item.Id}",
                "Capture again, replacing what is saved with\nwhatever the slot has on it now."))
            _advancedDyeCaptured = (item.Id, _wardrobe.CaptureAdvancedDyes(outfit, item));
        if (!worn) ImGui.EndDisabled();

        if (saved)
        {
            DrawAdvancedDyeSwatches(item.Id, rows);
        }
        else if (_advancedDyeCaptured is { Rows: 0 } miss && miss.Item == item.Id)
        {
            ImGui.Indent(UiScale.S(22f));
            ImGui.TextDisabled("nothing advanced dyed on that slot");
            ImGui.Unindent(UiScale.S(22f));
        }
    }

    /// <summary>An item with no advanced dyes, so the drawing path has no null to special-case.</summary>
    private static readonly Dictionary<string, string> NoAdvancedDyes = new();

    /// <summary>
    /// One square per captured row, in its diffuse colour.
    /// </summary>
    /// <remarks>
    /// A row count alone says nothing — six rows of what? The swatches are what make the setting
    /// legible at a glance, and they are the only honest thing to show: the wardrobe stores rows it
    /// deliberately does not interpret, so a colour and a count is all it truthfully knows.
    /// </remarks>
    private void DrawAdvancedDyeSwatches(Guid itemId, Dictionary<string, string> rows)
    {
        var colours = AdvancedDyeSwatches(itemId, rows);

        // Its own line, indented under the tick box. On the same line it shared a row with a
        // checkbox, a label and two buttons, and the count was wrapping mid-word off the edge.
        ImGui.Indent(UiScale.S(22f));

        var size = ImGui.GetTextLineHeight();
        var gap  = UiScale.S(3f);

        for (var i = 0; i < colours.Count; i++)
        {
            if (i > 0) UiLayout.SameLineIfRoom(size + gap);

            var pos = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(pos, new Vector2(pos.X + size, pos.Y + size), colours[i]);
            ImGui.Dummy(new Vector2(size, size));
        }

        var label = rows.Count == 1 ? "1 row" : $"{rows.Count} rows";
        if (colours.Count != rows.Count) label += $", {colours.Count} coloured";

        if (colours.Count > 0) UiLayout.SameLineIfRoomForText(label);
        ImGui.TextDisabled(label);

        ImGui.Unindent(UiScale.S(22f));
    }

    /// <summary>
    /// Diffuse colours of an item's stored rows, parsed once rather than every frame.
    /// </summary>
    /// <remarks>
    /// The fingerprint is the row count and the total length of their JSON — enough to notice a
    /// re-capture without parsing to find out, which is the whole point of the cache. This draws
    /// inside the outfit editor, so it runs for every item in the outfit on every frame.
    /// </remarks>
    private List<uint> AdvancedDyeSwatches(Guid itemId, Dictionary<string, string> rows)
    {
        var fingerprint = rows.Count * 397 + rows.Values.Sum(v => v.Length);

        if (_advancedDyeSwatches.TryGetValue(itemId, out var cached) && cached.Fingerprint == fingerprint)
            return cached.Colours;

        var colours = rows.Values
            .Select(GlamourerIpc.RowDiffuseColour)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToList();

        _advancedDyeSwatches[itemId] = (fingerprint, colours);
        return colours;
    }

    /// <summary>
    /// A delete button that does nothing unless Ctrl is held.
    /// </summary>
    /// <param name="label">Button text, including any <c>##id</c> suffix.</param>
    /// <param name="tooltip">What this deletes, shown under the Ctrl hint. No trailing newline.</param>
    /// <param name="size">Size for a full-height button. Null draws a small one.</param>
    /// <remarks>
    /// These are the one-click deletes: an X on a card removes the thing immediately, with no
    /// confirmation and no undo, and it sits a few pixels from the buttons people press constantly.
    /// Ctrl is the cheapest guard that cannot be clicked through by accident — deliberately not a
    /// modal, since a dialog on every X would be worse than the mistake it prevents.
    /// <para>
    /// The button is disabled rather than merely inert while Ctrl is up, so the reason is visible
    /// before the click rather than explained after nothing happens. The tooltip is shown either way
    /// — a disabled control that also refuses to say why is a dead end.
    /// </para>
    /// </remarks>
    private static bool DeleteButton(string label, string tooltip, Vector2? size = null) =>
        UiLayout.DeleteButton(label, tooltip, size);

    /// <summary>
    /// An ordinary-sized button carrying a Font Awesome glyph instead of a label.
    /// </summary>
    /// <remarks>
    /// Icons come from Font Awesome rather than from characters typed into a string. Dalamud's default
    /// font carries a subset of Unicode, and the shapes that look right in an editor — arrows, ticks,
    /// crosses — are mostly outside it and render as empty boxes in game. This has caught the plugin out
    /// before, which is why the icon font is the rule for anything that is a picture rather than words.
    /// <para>
    /// The tooltip is set outside the font scope on purpose: pushed inside it, the tooltip's own text
    /// would be drawn in the icon font and come out as nonsense.
    /// </para>
    /// </remarks>
    private static bool GlyphButton(FontAwesomeIcon icon, string id, string tooltip)
    {
        bool clicked;

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}");

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        return clicked;
    }

    /// <summary>
    /// A square button drawn with a Font Awesome glyph, sized to sit level with a checkbox.
    /// </summary>
    /// <remarks>
    /// A full-height button rather than a small one, and the glyph scaled up inside it: an icon is
    /// the only label these carry, so it has to be legible at a glance in a row that also holds a
    /// tick box and text. <see cref="ImGui.GetFrameHeight"/> is what the checkbox beside them uses,
    /// so they line up rather than floating in the middle of the row.
    /// </remarks>
    private static bool IconButton(FontAwesomeIcon icon, string id, string tooltip)
    {
        bool clicked;

        ImGui.SameLine();

        var text    = icon.ToIconString();
        var padding = ImGui.GetStyle().FramePadding.X * 2;

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
        {
            ImGui.SetWindowFontScale(IconGlyphScale);

            // Measured under the same font and scale it will be drawn with, then squared off. Sizing
            // the frame from the row height instead let the scaled glyph outgrow its own button and
            // clip — a Font Awesome glyph is not the width of a line of text.
            var glyph = ImGui.CalcTextSize(text);
            var side  = Math.Max(ImGui.GetFrameHeight(), Math.Max(glyph.X, glyph.Y) + padding);

            clicked = ImGui.Button($"{text}##{id}", new Vector2(side, side));
            ImGui.SetWindowFontScale(1f);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    /// <summary>How much larger than the body text an icon button's glyph is drawn.</summary>
    private const float IconGlyphScale = 1.2f;

    private bool DrawDyePicker(string id, string label, byte? current, float width, out byte picked)
    {
        picked = current ?? 0;

        var stains = Plugin.ItemLookup.GetStains();
        var match  = stains.FirstOrDefault(s => s.Id == current);
        var name   = current == null            ? "Mixed"
                   : string.IsNullOrEmpty(match.Name) ? "Undyed"
                   : match.Name;

        ImGui.BeginGroup();
        ImGui.TextDisabled(label);

        ImGui.SetNextItemWidth(width);
        var changed = false;

        // The combo popup scrolls on its own — a child window inside it would add a second scrollbar
        if (ImGui.BeginCombo($"##{id}", name, ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint($"##search_{id}", "Search dyes…", ref _dyeSearch, 64);
            ImGui.Separator();

            foreach (var (stainId, stainName, colour) in stains)
            {
                if (!string.IsNullOrWhiteSpace(_dyeSearch) &&
                    stainName.IndexOf(_dyeSearch.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                ImGui.PushID(stainId);

                // Swatch, then the name as the clickable row
                var pos  = ImGui.GetCursorScreenPos();
                var size = ImGui.GetTextLineHeight();
                ImGui.GetWindowDrawList().AddRectFilled(
                    pos, new Vector2(pos.X + size, pos.Y + size), colour);
                ImGui.Dummy(new Vector2(size, size));
                ImGui.SameLine();

                if (ImGui.Selectable(stainName, stainId == current))
                {
                    picked     = stainId;
                    changed    = stainId != current;
                    _dyeSearch = string.Empty;
                }

                ImGui.PopID();
            }

            ImGui.EndCombo();
        }

        ImGui.EndGroup();
        return changed;
    }

    /// <summary>
    /// The plain game items an outfit carries for the slots its own items do not fill.
    /// </summary>
    /// <remarks>
    /// Read-only apart from removing one and re-capturing the lot. There is no picker here on
    /// purpose: choosing a game item piece by piece is what Glamourer is for, and the wardrobe's
    /// part is to notice what you are already wearing and keep it. Wear the look, press the button.
    /// </remarks>
    private void DrawOutfitVanillaItems(Outfit outfit)
    {
        // A plate's pieces are the same thing stored the same way, but they came out of the game
        // rather than off the character, so neither capturing over them nor dropping one by hand
        // makes sense — both would be edits to a copy the next resync overwrites
        if (outfit.IsGlamourPlate)
        {
            DrawGlamourPlatePieces(outfit);
            return;
        }

        // A design card has no use for these and is actively harmed by them. The design already supplies
        // gear for every slot it applies, and vanilla pieces are put on *after* the items — so capturing
        // while the design is worn would freeze a copy of the design's own gear and then apply that copy
        // over the top of the live design every time, which is the snapshot the link exists to avoid.
        // The design's pieces are listed in its header instead.
        if (outfit.IsDesign)
        {
            ImGui.TextDisabled("Vanilla items");
            ImGui.TextDisabled("Not used on a design card: the design already supplies the gear for every " +
                               "slot it applies, and the items you attach cover the rest. Its pieces are " +
                               "listed under The design applies, above.");
            return;
        }

        ImGui.TextDisabled("Vanilla items");
        ImGui.TextDisabled("Plain gear in the slots this outfit's own items do not fill. Saved with " +
                           "the outfit and put back when it is worn.");
        ImGui.Spacing();

        if (ImGui.SmallButton("Capture what I'm wearing##vanilla"))
        {
            var before = outfit.VanillaItems.Count;
            var found  = _wardrobe.CaptureVanillaItems(outfit);
            _config.Save();

            // Always says something. Capturing the same pieces twice changes nothing on screen, and
            // a button that looks identical before and after reads as a button that does not work.
            _vanillaStatus = found > 0
                ? $"Captured {found}: {string.Join(", ", outfit.VanillaItems.Keys
                    .Select(k => Enum.TryParse<EquipSlot>(k, out var s) ? s.DisplayName() : k))}."
                : _wardrobe.ResolveOutfit(outfit).Count > 0
                    ? "Nothing to capture — the outfit's own items already cover every slot you are wearing."
                    : "Nothing to capture — Glamourer reported no equipment.";

            if (found > 0 && found == before)
                _vanillaStatus += " (Unchanged.)";
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads Glamourer and stores whatever is in the slots this outfit\n" +
                             "does not already cover, dyes and all. Replaces what is stored now.");

        if (!string.IsNullOrEmpty(_vanillaStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_vanillaStatus);
        }

        if (outfit.VanillaItems.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("None saved.");
            return;
        }

        ImGui.Spacing();

        string? drop = null;

        foreach (var (slotName, piece) in outfit.VanillaItems.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var slot = Enum.TryParse<EquipSlot>(slotName, out var parsed) ? parsed : EquipSlot.Unknown;

            ImGui.PushID($"vanilla_{slotName}");

            if (DeleteButton("×", $"Stop saving the {slot.DisplayName()} piece with this outfit."))
                drop = slotName;

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f), slot.DisplayName());

            ImGui.SameLine();
            ImGui.TextUnformatted(string.IsNullOrEmpty(piece.Name) ? $"#{piece.ItemId}" : piece.Name);

            DrawStainSwatch(piece.Stain1);
            DrawStainSwatch(piece.Stain2);

            ImGui.PopID();
        }

        if (drop == null) return;

        outfit.VanillaItems.Remove(drop);
        _config.Save();
    }

    /// <summary>
    /// A plate's pieces, in slot order, with nothing on the row that could change them.
    /// </summary>
    /// <remarks>
    /// Ordered by slot rather than by the dictionary key, which sorts alphabetically and would put
    /// the earrings above the body. A plate is read as a picture of a character, top down.
    /// </remarks>
    private void DrawGlamourPlatePieces(Outfit outfit)
    {
        ImGui.TextDisabled("Plate contents");

        if (outfit.VanillaItems.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Nothing saved. Resync from the game to read this plate.");
            return;
        }

        ImGui.Spacing();

        var pieces = outfit.VanillaItems
            .Select(kv => (Slot: Enum.TryParse<EquipSlot>(kv.Key, out var s) ? s : EquipSlot.Unknown, kv.Value))
            .OrderBy(p => (int)p.Slot);

        foreach (var (slot, piece) in pieces)
        {
            ImGui.PushID($"platePiece_{slot}");

            ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f), slot.DisplayName());

            ImGui.SameLine();
            ImGui.TextUnformatted(string.IsNullOrEmpty(piece.Name) ? $"#{piece.ItemId}" : piece.Name);

            DrawStainSwatch(piece.Stain1);
            DrawStainSwatch(piece.Stain2);

            ImGui.PopID();
        }
    }

    /// <summary>
    /// The pieces a linked design puts on the character, read live from Glamourer.
    /// </summary>
    /// <remarks>
    /// The design's half of the card, and read-only for the same reason a plate's pieces are: Glamourer
    /// owns them, and they are changed by editing the design there. Not stored anywhere — a copy of this
    /// list would be exactly the stale snapshot the link exists to avoid, so it is asked for each time
    /// and served from a rationed cache.
    /// <para>
    /// A design that sets no equipment is the case this exists to make legible. Those are common — a
    /// design saved for a face or a body — and without a word here their card would show an empty list
    /// and look broken.
    /// </para>
    /// </remarks>
    private void DrawDesignPieces(Outfit outfit)
    {
        ImGui.TextDisabled("The design applies");

        var contents = _wardrobe.DesignContents(outfit);

        if (contents == null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(_wardrobe.DesignIsLive(outfit)
                ? "Reading it from Glamourer…"
                : "Glamourer no longer has this design, so there is nothing to read.");
            return;
        }

        ImGui.Spacing();

        if (contents.AppliesEquipment == null)
        {
            // Not "no gear": a design saved on a non-human form keeps its equipment packed, so the
            // honest answer is that it cannot be listed rather than that there is none
            ImGui.TextDisabled("This design was saved on a non-human form, so Glamourer stores its " +
                               "equipment packed rather than slot by slot. It applies normally — it " +
                               "just cannot be listed here.");
            return;
        }

        if (contents.AppliesEquipment == false)
        {
            ImGui.TextColored(new Vector4(0.72f, 0.56f, 0.92f, 1f), "No gear — appearance only.");
            ImGui.TextWrapped(contents.AppliesCustomize
                ? "This design sets your face, body or colouring and leaves your clothes alone. Attach " +
                  "items below to dress it, and they are all that will be equipped."
                : "This design sets no equipment and no appearance either. Wearing it changes nothing " +
                  "on its own — whatever you attach below is the whole of it.");
            return;
        }

        foreach (var piece in contents.Pieces)
        {
            ImGui.PushID($"designPiece_{piece.Slot}");

            ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f), piece.Slot.DisplayName());

            ImGui.SameLine();
            ImGui.TextUnformatted(piece.Name);

            DrawStainSwatch(piece.Stain1);
            DrawStainSwatch(piece.Stain2);

            ImGui.PopID();
        }

        if (!contents.AppliesCustomize) return;

        ImGui.Spacing();
        ImGui.TextDisabled("It also sets your appearance — face, body or colouring.");
    }

    /// <summary>
    /// A name cut to what fits on a card at the current size.
    /// </summary>
    /// <remarks>
    /// Scaled with the card rather than fixed at twenty characters: enlarging a card to read the
    /// names on it and getting the same truncation in more empty space is the opposite of what the
    /// slider is for.
    /// </remarks>
    private string CardName(string name)
    {
        var max = Math.Max(8, (int)(20 * CardScale));
        return name.Length > max ? name[..(max - 2)] + "…" : name;
    }

    /// <summary>
    /// The styles an outfit carries, named on its card in their own colours.
    /// </summary>
    /// <remarks>
    /// The card is already tinted by the first coloured style, but a tint alone cannot be read back
    /// — it says "this one is different" without saying what it is, and says nothing at all on a
    /// style with no colour set. The name is what makes a grid of outfits sortable by eye.
    /// <para>
    /// Text rather than buttons: a card is a thing to look at, and everything on it that can be
    /// clicked already does something. Styles are set in the edit panel.
    /// </para>
    /// </remarks>
    private void DrawOutfitStyleLabels(Outfit outfit, float cardW)
    {
        var styles = outfit.Tags.Where(TagTree.IsStyle).ToList();
        if (styles.Count == 0) return;

        var names = styles
            .Select(s => s[(TagTree.StyleRoot.Length + 1)..])
            .Select(s => s.Contains('/') ? s[..s.IndexOf('/')] : s)
            .ToList();

        // One line, ellipsised rather than wrapped: the card has a fixed height and a second row of
        // styles would push the buttons off the bottom of it
        var label = string.Join(" · ", names);
        var room  = cardW - CardPad * 4;

        while (label.Length > 4 && ImGui.CalcTextSize(label).X > room)
            label = label[..^2] + "…";

        var colour = _config.TagColoursEnabled ? TagTree.Colour(_config, styles[0]) : null;
        ImGui.PushStyleColor(ImGuiCol.Text, colour is { } c
            ? TagTree.Shade(c, true, true)
            : new Vector4(0.72f, 0.62f, 0.9f, 1f));

        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(names.Count == 1 ? $"Style: {names[0]}" : "Styles: " + string.Join(", ", names));
    }

    /// <summary>
    /// Opens an item's own edit panel from wherever it was clicked.
    /// </summary>
    /// <remarks>
    /// The right panel draws one thing at a time and the item editor sits above the outfit editor
    /// in that order, so opening it covers the outfit and closing it uncovers the outfit again —
    /// still open, still on the same outfit, because nothing here clears
    /// <see cref="_editingOutfit"/>. Going to change a mod option and coming back is therefore the
    /// default behaviour rather than something to arrange.
    /// <para>
    /// The cached thumbnail is dropped on the way, matching the item grid's Edit: the picture is the
    /// thing most likely to be changed in there, and a stale one would survive the trip back.
    /// </para>
    /// </remarks>
    private void OpenItemEditor(WardrobeItem item)
    {
        _imageCache.Remove(item.Id);
        _panel.OpenEdit(item);
    }

    /// <summary>
    /// Styles and tags on an outfit, sharing the wardrobe's one tag scheme.
    /// </summary>
    /// <remarks>
    /// The same strings items use, filtered by the same row: an outfit tagged <c>Beach</c> answers
    /// to the Beach filter exactly as a swimsuit does, and there is no second vocabulary to keep in
    /// step. Styles are toggles and tags are chips, matching the item edit panel, because a style is
    /// picked from a short known list while a tag can be anything.
    /// </remarks>
    private void DrawOutfitTags(Outfit outfit)
    {
        ImGui.TextDisabled("Styles");
        ImGui.Spacing();

        var styles  = TagTree.Styles(_config);
        var changed = false;

        if (styles.Count == 0)
        {
            ImGui.TextDisabled("None yet — make one in the Tags panel.");
        }
        else
        {
            for (var i = 0; i < styles.Count; i++)
            {
                var style = styles[i];
                var on    = outfit.Tags.Contains(style.FullPath, StringComparer.OrdinalIgnoreCase);

                if (i > 0) UiLayout.SameLineIfRoomForButton(style.Segment);

                var colour = _config.TagColoursEnabled ? TagTree.Colour(_config, style.FullPath) : null;
                var pushed = 0;

                if (colour is { } c)
                {
                    var fill = TagTree.Shade(c, on, true);
                    ImGui.PushStyleColor(ImGuiCol.Button,        on ? fill : TagTree.Blend(fill, new Vector4(0.16f, 0.16f, 0.19f, 1f), 0.55f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, fill);
                    ImGui.PushStyleColor(ImGuiCol.Text,          on ? TagTree.ReadableOn(fill) : new Vector4(0.75f, 0.75f, 0.8f, 1f));
                    pushed = 3;
                }
                else if (on)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
                    pushed = 2;
                }

                if (ImGui.SmallButton($"{style.Segment}##outfitstyle_{style.FullPath}"))
                {
                    if (on) outfit.Tags.RemoveAll(t => t.Equals(style.FullPath, StringComparison.OrdinalIgnoreCase));
                    else    outfit.Tags.Add(style.FullPath);
                    changed = true;
                }

                if (pushed > 0) ImGui.PopStyleColor(pushed);
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Tags");
        ImGui.Spacing();

        // Styles are shown above, so a style is never repeated down here as a chip
        var removeIdx = -1;
        var drawn     = 0;

        for (var i = 0; i < outfit.Tags.Count; i++)
        {
            if (TagTree.IsStyle(outfit.Tags[i])) continue;

            if (drawn++ > 0) UiLayout.SameLineIfRoomForButton(outfit.Tags[i]);

            ImGui.PushID($"outfittag_{i}");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.35f, 0.2f, 0.55f, 1f));
            ImGui.SmallButton(outfit.Tags[i]);
            ImGui.PopStyleColor();

            ImGui.SameLine();
            if (DeleteButton("×", $"Take '{outfit.Tags[i]}' off this outfit.")) removeIdx = i;
            ImGui.PopID();
        }

        if (removeIdx >= 0)
        {
            outfit.Tags.RemoveAt(removeIdx);
            changed = true;
        }

        ImGui.SetNextItemWidth(UiScale.S(200));
        var entered = ImGui.InputTextWithHint("##outfittaginput", "new tag", ref _outfitTagInput, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);

        UiLayout.SameLineIfRoomForButton("Add");
        if ((ImGui.SmallButton("Add") || entered) && AddOutfitTag(outfit)) changed = true;

        // Every known tag, so a scheme laid out in the Tags panel is one click away here too
        var suggestions = _config.AllTags()
            .Where(t => !TagTree.IsStyle(t)
                     && !outfit.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)
                     && (string.IsNullOrEmpty(_outfitTagInput)
                         || t.Contains(_outfitTagInput, StringComparison.OrdinalIgnoreCase)))
            .Take(24)
            .ToList();

        if (suggestions.Count > 0)
        {
            ImGui.TextDisabled("Existing tags");
            for (var i = 0; i < suggestions.Count; i++)
            {
                var s     = suggestions[i];
                var label = s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] + "…" : s;

                if (i > 0) UiLayout.SameLineIfRoomForButton(label);

                if (ImGui.SmallButton($"{label}##outfitsug_{s}"))
                {
                    outfit.Tags.Add(s);
                    _outfitTagInput = string.Empty;
                    changed = true;
                }

                if (ImGui.IsItemHovered()) ImGui.SetTooltip(s);
            }
        }

        if (changed) _config.Save();
    }

    private bool AddOutfitTag(Outfit outfit)
    {
        var tag = NormaliseTag(_outfitTagInput);
        if (tag.Length == 0) return false;

        _outfitTagInput = string.Empty;

        if (outfit.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return false;

        outfit.Tags.Add(tag);
        return true;
    }

    /// <summary>A small square of a dye's colour, skipped entirely when the channel is undyed.</summary>
    private void DrawStainSwatch(byte stain)
    {
        if (stain == 0) return;

        var entry = Plugin.ItemLookup.GetStains().FirstOrDefault(s => s.Id == stain);
        if (entry.Id == 0) return;

        ImGui.SameLine();

        var pos  = ImGui.GetCursorScreenPos();
        var size = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddRectFilled(pos, new Vector2(pos.X + size, pos.Y + size), entry.Colour);
        ImGui.Dummy(new Vector2(size, size));

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(entry.Name);
    }

    /// <summary>Adds an existing wardrobe item to the outfit, searchable by name.</summary>
    private void DrawAddToOutfit(Outfit outfit)
    {
        ImGui.TextUnformatted("Add to outfit");
        ImGui.Spacing();

        var candidates = _config.WardrobeItems
            .Where(i => !outfit.ItemIds.Contains(i.Id))
            // Offering a category the user has switched off would add an item they then cannot
            // see anywhere in the grid
            .Where(i => _config.ModCategoriesEnabled || !i.Slot.IsModCategory())
            .Where(i => string.IsNullOrWhiteSpace(_addToOutfitSearch) ||
                        i.Name.Contains(_addToOutfitSearch.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => (int)i.Slot)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##addtooutfit", $"Pick an item…  ({candidates.Count})"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##addsearch", "Search…", ref _addToOutfitSearch, 128);
            ImGui.Separator();

            foreach (var item in candidates)
            {
                if (!ImGui.Selectable($"{item.Slot.DisplayName()} — {item.Name}##add_{item.Id}")) continue;

                outfit.ItemIds.Add(item.Id);

                // On a design card, a mod attached to a slot the design fills starts out dyed the colour
                // the design uses there — the whole reason the mod is being attached is to be that piece
                if (outfit.IsDesign && _wardrobe.DesignDyeFor(outfit, item.Slot) is { } dye)
                {
                    outfit.Dyes[item.Id.ToString()] = dye;
                    _designStatus = $"'{item.Name}' took the design's dye for {item.Slot.DisplayName()}.";
                }

                _config.Save();
                _addToOutfitSearch = string.Empty;
            }
            ImGui.EndCombo();
        }

        if (candidates.Count == 0 && string.IsNullOrWhiteSpace(_addToOutfitSearch))
            ImGui.TextDisabled("Every wardrobe item is already in this outfit.");
    }

    private void CloseOutfitEdit()
    {
        _editingOutfit     = null;
        _editOutfitName    = string.Empty;
        _editOutfitImage   = string.Empty;
        _addToOutfitSearch = string.Empty;

        // Belongs to the outfit that was open, and would otherwise report on it under the next one
        _vanillaStatus     = string.Empty;
        _outfitTagInput    = string.Empty;
    }

    private void OpenOutfitEdit(Outfit outfit)
    {
        _editingOutfit     = outfit;
        _editOutfitName    = outfit.Name;
        _editOutfitImage   = outfit.ImagePath ?? string.Empty;
        _addToOutfitSearch = string.Empty;

        // Cleared with the panel: "Applied glamour plate 3 in game" left sitting under a different
        // plate's button reads as something that just happened to that one
        _plateApplyStatus  = string.Empty;
        _plateSyncStatus   = string.Empty;
    }

    /// <summary>Small square thumbnail for one item inside the outfit edit list.</summary>
    private void DrawOutfitRowThumb(WardrobeItem item, float size)
    {
        var box = new Vector2(size, size);
        var path = item.ImagePath ?? string.Empty;

        if (!_imageCache.TryGetValue(item.Id, out var entry) || entry.Path != path)
        {
            ISharedImmediateTexture? texture = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { texture = _textures.GetFromFile(path); }
                catch { /* falls through to the placeholder */ }
            }
            entry = (path, texture);
            _imageCache[item.Id] = entry;
        }

        if (entry.Texture?.GetWrapOrDefault() is { } wrap)
        {
            ImageDraw.Square(wrap, size);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.07f, 0.07f, 0.09f, 1f));
        ImGui.Button($"##thumb_{item.Id}", box);
        ImGui.PopStyleColor();
    }

    private unsafe void DrawOutfitImage(Outfit outfit, float thumbSize)
    {
        var size = new Vector2(thumbSize, thumbSize);
        var path = outfit.ImagePath ?? string.Empty;

        // Resolved once per outfit, as for items — never stat the filesystem per frame
        if (!_outfitImageCache.TryGetValue(outfit.Id, out var entry) || entry.Path != path)
        {
            ISharedImmediateTexture? texture = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { texture = _textures.GetFromFile(path); }
                catch (Exception ex) { _log.Warning(ex, $"[Wardrobe] Could not load image for outfit '{outfit.Name}'"); }
            }

            entry = (path, texture);
            _outfitImageCache[outfit.Id] = entry;
        }

        var top = ImGui.GetCursorPos();

        if (entry.Texture?.GetWrapOrDefault() is { } wrap)
        {
            if (_config.PortraitOutfitPreviews) ImageDraw.Portrait(wrap, thumbSize);
            else                                ImageDraw.Square(wrap, thumbSize);

            AcceptOutfitImageDrop(outfit);

            // The same right-click as an item card's picture. An outfit preview is a full-body shot,
            // which is the one that suffers most from being looked at in a card.
            var count = outfit.ImageCount();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(count > 1
                    ? $"{count} pictures. Right-click to view them full size."
                    : "Right-click to view full size.");

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                _quickViewOutfit = outfit.Id;

            if (count > 1) DrawImageCountBadge(top, count);

            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.07f, 0.07f, 0.09f, 1f));
        ImGui.Button("Outfit", _config.PortraitOutfitPreviews
            ? new Vector2(thumbSize, ImageDraw.PortraitHeight(thumbSize))
            : size);
        ImGui.PopStyleColor();
        AcceptOutfitImageDrop(outfit);
    }

    // ── Settings panel ────────────────────────────────────────────────────────

    /// <summary>
    /// Picks the Glamourer design used as the appearance to return to when reverting a
    /// customisation mod.
    /// </summary>
    private void DrawRevertDesignPicker()
    {
        ImGui.TextUnformatted("Revert customisation mods to");
        ImGui.TextDisabled("A Glamourer design holding your normal look. Only its customisations " +
                           "are used; its gear is ignored.");
        ImGui.Spacing();

        _settingsDesigns ??= Plugin.Glamourer.GetDesigns();

        var current = _config.RevertDesignId.HasValue
            ? (string.IsNullOrEmpty(_config.RevertDesignName) ? "(unnamed design)" : _config.RevertDesignName)
            : "(none — put back the previous hairstyle)";

        ImGui.SetNextItemWidth(UiScale.S(260));
        if (ImGui.BeginCombo("##revertdesign", current))
        {
            if (ImGui.Selectable("(none — put back the previous hairstyle)", !_config.RevertDesignId.HasValue))
            {
                _config.RevertDesignId   = null;
                _config.RevertDesignName = string.Empty;
                _config.Save();
            }

            foreach (var (id, name) in _settingsDesigns)
            {
                if (ImGui.Selectable($"{name}##design_{id}", _config.RevertDesignId == id))
                {
                    _config.RevertDesignId   = id;
                    _config.RevertDesignName = name;
                    _config.Save();
                }
            }
            ImGui.EndCombo();
        }

        UiLayout.SameLineIfRoomForButton("Refresh");
        if (ImGui.Button("Refresh##designs"))
            _settingsDesigns = Plugin.Glamourer.GetDesigns();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-read the design list from Glamourer.");

        if (_settingsDesigns.Count == 0)
            ImGui.TextDisabled("No designs found — create one in Glamourer first.");
    }

    /// <summary>
    /// The changelog shown after an update, and the way back to it.
    /// </summary>
    /// <remarks>
    /// The button matters as much as the switch: the window closes on a click, and "what did that
    /// last update say about re-saving presets?" is asked a day later, by which time the only copy
    /// was on a release page nobody bookmarked.
    /// </remarks>
    private void DrawChangelogSettings()
    {
        ImGui.TextUnformatted("Changelog");
        ImGui.TextDisabled("What changed, shown once the first time a new version runs.");
        ImGui.Spacing();

        var show = _config.ShowChangelogOnUpdate;
        if (ImGui.Checkbox("Show what changed after each update", ref show))
        {
            _config.ShowChangelogOnUpdate = show;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turn this off and updates arrive quietly.\n" +
                             "The button below still works either way.");

        ImGui.Spacing();
        if (ImGui.Button(" View changelog "))
            Changelog?.OpenAll();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Every version's notes, newest first — including the one you are on.");

        ImGui.SameLine();
        ImGui.TextDisabled($"You are on {Services.Changelog.Current}.");
    }

    private void DrawBackupSettings()
    {
        ImGui.TextUnformatted("Backups");
        ImGui.TextDisabled("Copies your wardrobe to a folder hourly, and only when it has changed.");
        ImGui.Spacing();

        var enabled = _config.BackupEnabled;
        if (ImGui.Checkbox("Enable hourly backups", ref enabled))
        {
            _config.BackupEnabled = enabled;
            _config.Save();
        }

        ImGui.Spacing();
        if (string.IsNullOrEmpty(_config.BackupFolder))
            ImGui.TextDisabled("No backup folder selected.");
        else
        {
            ImGui.TextUnformatted(_config.BackupFolder);
            if (!Directory.Exists(_config.BackupFolder))
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Folder will be created on the next backup.");
            }
        }

        ImGui.Spacing();
        if (ImGui.Button(" Browse…##backupFolder "))
        {
            var startDir = Directory.Exists(_config.BackupFolder)
                ? _config.BackupFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            _fileDialog.OpenFolderDialog("Select Backup Folder", (confirmed, path) =>
            {
                if (!confirmed) return;
                _config.BackupFolder = path;
                _config.Save();
            }, startDir);
        }

        if (!string.IsNullOrEmpty(_config.BackupFolder))
        {
            UiLayout.SameLineIfRoomForButton(" Back Up Now ");
            if (ImGui.Button(" Back Up Now "))
                _backup.Run();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Write a backup immediately.\nStill skipped if nothing has changed since the last one.");

            UiLayout.SameLineIfRoomForButton(" Clear ");
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.Button(" Clear##backupFolder "))
            {
                _config.BackupFolder = string.Empty;
                _config.Save();
            }
            ImGui.PopStyleColor(2);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Backups to keep (per file)");
        ImGui.SetNextItemWidth(UiScale.S(120));
        var keep = _config.BackupKeepCount;
        if (ImGui.InputInt("##backupKeep", ref keep))
        {
            _config.BackupKeepCount = Math.Clamp(keep, 1, 999);
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Older backups beyond this count are deleted automatically.");

        if (!string.IsNullOrEmpty(_backup.LastResult))
        {
            ImGui.Spacing();
            ImGui.TextDisabled(_backup.LastResult);
        }
    }

    /// <summary>
    /// Panel heading with a close button on the right. Returns true when the panel was closed,
    /// so the caller can bail out of drawing the rest of it this frame.
    /// </summary>
    private static bool DrawPanelHeader(string title)
    {
        ImGui.TextUnformatted(title);

        var style  = ImGui.GetStyle();
        var width  = ImGui.CalcTextSize("×").X + style.FramePadding.X * 2;

        ImGui.SameLine();
        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);

        var closed = ImGui.SmallButton($"×##close_{title}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Close this panel");

        ImGui.Separator();
        return closed;
    }

    /// <summary>
    /// The settings panel, in sections that follow the order you meet them in: set up the
    /// collection, import, wear, then the optional extras.
    /// </summary>
    private void DrawSettings()
    {
        if (DrawPanelHeader("Settings"))
        {
            _showSettings = false;
            return;
        }
        ImGui.Spacing();

        DrawCollectionSettings();
        SettingsBreak();
        DrawImportSettings();
        SettingsBreak();
        DrawModCategorySettings();
        SettingsBreak();
        DrawGlamourerDesignSettings();
        SettingsBreak();
        DrawWearingSettings();
        SettingsBreak();
        DrawBaseCharacterSettings();
        SettingsBreak();
        DrawAdvancedDyeSettings();
        SettingsBreak();
        DrawVariantSettings();
        SettingsBreak();
        DrawSlotIconSettings();
        SettingsBreak();
        DrawCardSizeSettings();
        SettingsBreak();
        DrawTagColourSettings();
        SettingsBreak();
        DrawImageFolderSettings();
        SettingsBreak();
        DrawScreenshotSettings();
        SettingsBreak();
        DrawBackupSettings();
        SettingsBreak();
        DrawChangelogSettings();
        SettingsBreak();
        DrawSetupSettings();
    }

    // The Experimental section is gone with the graduation of advanced dyes, which was the only
    // thing in it. Worth putting back the next time something ships that has not been proven in the
    // game — saying so out loud is better than letting it look as settled as the rest of the panel.

    /// <summary>Spacing and a rule between two settings sections.</summary>
    private static void SettingsBreak()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawCollectionSettings()
    {
        ImGui.TextUnformatted("Collection");
        ImGui.TextDisabled("The collection new imports start in.");
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f),
            "It must be the one your character actually uses.");
        ImGui.Spacing();

        _settingsCollections ??= Plugin.Penumbra.GetCollections();

        var collNames = new[] { "(first available)" }.Concat(_settingsCollections).ToArray();
        var curIdx    = 0;
        if (!string.IsNullOrEmpty(_config.DefaultCollection))
        {
            var found = Array.FindIndex(collNames,
                n => n.Equals(_config.DefaultCollection, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) curIdx = found;
        }

        ImGui.SetNextItemWidth(UiScale.S(260));
        if (ImGui.BeginCombo("##defaultColl", collNames[curIdx]))
        {
            for (var i = 0; i < collNames.Length; i++)
            {
                if (ImGui.Selectable(collNames[i], i == curIdx))
                {
                    _config.DefaultCollection = i == 0 ? string.Empty : collNames[i];
                    _config.Save();
                }
                if (i == curIdx) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        UiLayout.SameLineIfRoomForButton("Refresh");
        if (ImGui.Button("Refresh##colls"))
            _settingsCollections = Plugin.Penumbra.GetCollections();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-read the collection list from Penumbra.");

        // Configured but missing: the collection was renamed or deleted in Penumbra
        if (!string.IsNullOrEmpty(_config.DefaultCollection) && curIdx == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f),
                $"'{_config.DefaultCollection}' was not found in Penumbra.");
        }

        // Easily the most common reason an item "does nothing": the mod enables successfully, in a
        // collection the character is not using. Nothing looks wrong anywhere, so it needs saying.
        ImGui.Spacing();
        ImGui.TextDisabled("A mod enabled anywhere else reports success and never appears. " +
                           "In Penumbra, see Collections → Your Character, and check that no " +
                           "Individual Assignment overrules it.");
    }

    private void DrawImportSettings()
    {
        ImGui.TextUnformatted("Importing");
        ImGui.TextDisabled("What the import panel's mod lists leave out.");
        ImGui.Spacing();

        var hideImported = _config.HideImportedMods;
        if (ImGui.Checkbox("Hide already-imported mods when importing", ref hideImported))
        {
            _config.HideImportedMods = hideImported;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes mods you have already imported from the import panel's\n" +
                             "mod lists, instead of showing them greyed out.");

        var hideSupport = _config.HideSupportMods;
        if (ImGui.Checkbox("Hide support mods when importing", ref hideSupport))
        {
            _config.HideSupportMods = hideSupport;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes mods only ever attached as supplementary mods,\n" +
                             "instead of showing them greyed and italic.\n\n" +
                             "With both options on, the lists show only mods the\n" +
                             "wardrobe does not reference at all.");
    }

    private void DrawWearingSettings()
    {
        ImGui.TextUnformatted("Wearing");
        ImGui.Spacing();

        var applyHair = _config.ApplyHairstyleWithHairMods;
        if (ImGui.Checkbox("Switch hairstyle when applying hair mods", ref applyHair))
        {
            _config.ApplyHairstyleWithHairMods = applyHair;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A hair mod replaces one specific hairstyle, so it only shows while\n" +
                             "that hairstyle is selected. With this on, applying a hair item also\n" +
                             "switches your character to it, and reverting puts back the one you had.\n\n" +
                             "Turn this off to leave your hairstyle untouched.");

        ImGui.Spacing();
        DrawRevertDesignPicker();
    }

    /// <summary>Label for having no base character, used in both pickers that offer one.</summary>
    private const string NoBaseLabel = "(none — a strip takes everything)";

    /// <summary>
    /// The base character picker, drawn both in settings and on the session HUD.
    /// </summary>
    /// <remarks>
    /// Switching goes through <see cref="WardrobeService.SwitchBase"/> rather than writing the id,
    /// so the base being left takes its items off with it and the new one goes on — picking one
    /// mid-session is the whole reason it is on the HUD, and it has to take effect before the next
    /// shot rather than at the next strip. The caller sets the width.
    /// </remarks>
    private void DrawBaseCharacterCombo(string id)
    {
        var active = _config.ActiveBaseCharacter;

        if (!ImGui.BeginCombo(id, active?.Name ?? NoBaseLabel)) return;

        if (ImGui.Selectable(NoBaseLabel, active == null) && active != null)
            _wardrobe.SwitchBase(active, null);

        foreach (var candidate in _config.BaseCharacters)
            if (ImGui.Selectable($"{candidate.Name}##{id}_{candidate.Id}", active?.Id == candidate.Id) &&
                active?.Id != candidate.Id)
                _wardrobe.SwitchBase(active, candidate);

        ImGui.EndCombo();
    }

    /// <summary>
    /// The base character: what a strip leaves on, and what a session puts back before each shot.
    /// </summary>
    /// <remarks>
    /// In settings rather than on a panel of its own because it is set up once and then only
    /// switched, and switching has a picker in the session HUD where the switching happens.
    /// </remarks>
    private void DrawBaseCharacterSettings()
    {
        ImGui.TextUnformatted("Base character");
        ImGui.TextDisabled("The character underneath the clothes. Stripping strips down to this " +
                           "instead of down to nothing, and a screenshot session puts it back " +
                           "before every shot — so a tail worn on a ring, or an ear mod on a pair " +
                           "of earrings, survives being photographed in something else.");
        ImGui.Spacing();

        var active = _config.ActiveBaseCharacter;

        // Full width on its own line, with the buttons under it. The settings panel is a narrow
        // column and a fixed-width combo plus two buttons ran the second one off the edge.
        ImGui.SetNextItemWidth(-1);
        DrawBaseCharacterCombo("##basechar");

        if (ImGui.Button(" New##basechar "))
        {
            var created = new BaseCharacter { Name = $"Base {_config.BaseCharacters.Count + 1}" };
            _config.BaseCharacters.Add(created);
            _wardrobe.SwitchBase(active, created);
            active = created;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Start another base character and make it the active one.\n\n" +
                             "One per character, or one per version of the same character —\n" +
                             "the ears you wear on days off need not be the ones you photograph in.");

        if (active == null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Nothing is held back: a strip empties every equipment slot and " +
                               "turns off every worn mod, as it always has.");
            return;
        }

        UiLayout.SameLineIfRoomForButton(" Delete ");
        if (DeleteButton(" Delete##basechar ",
                "Delete this base character.\nThe items in it are kept — only the grouping goes."))
        {
            _wardrobe.SwitchBase(active, null);
            _config.BaseCharacters.Remove(active);
            _config.Save();
            _baseNameEditId = null;
            return;
        }

        ImGui.Spacing();
        DrawKeepBaseOnRevertSetting(active);

        // Reloaded whenever the active base changes, or a half-typed name would follow the switch
        // onto whichever one was picked next
        if (_baseNameEditId != active.Id)
        {
            _baseNameEdit   = active.Name;
            _baseNameEditId = active.Id;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Name");
        ImGui.SetNextItemWidth(UiScale.S(260));
        ImGui.InputText("##basename", ref _baseNameEdit, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            active.Name = string.IsNullOrWhiteSpace(_baseNameEdit) ? active.Name : _baseNameEdit.Trim();
            _baseNameEdit = active.Name;
            _config.Save();
        }

        ImGui.Spacing();
        DrawBaseKeptSlots(active);

        ImGui.Spacing();
        DrawBaseItems(active);

        ImGui.Spacing();
        DrawBaseDesignPicker(active);

        ImGui.Spacing();
        if (ImGui.Button(" Apply base character now "))
            _wardrobe.ApplyBase(active);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Put this base character on right now: its design's customisations,\n" +
                             "then any of its items you are not already wearing.\n\n" +
                             "Nothing is removed — this only puts the base back.");

        if (active.IsEmpty)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f),
                "This base character is empty, so it changes nothing yet. Keep a slot, add an " +
                "item, or pick a design.");
        }
    }

    /// <summary>Slots the base character holds against a strip, in its two halves.</summary>
    /// <remarks>
    /// Split because a strip does two different things and each half is held back differently. Gear
    /// slots are emptied to Emperor's New, so keeping one means leaving whatever is worn there. Hair,
    /// face, skin and the rest have no item to empty — a strip removes them by switching their
    /// Penumbra mods off, so keeping one means leaving the mod worn there enabled.
    /// </remarks>
    private void DrawBaseKeptSlots(BaseCharacter baseChar)
    {
        ImGui.TextUnformatted("What a strip leaves alone");
        ImGui.Spacing();

        // Ticked and locked for the slots its own items occupy: those are already protected on the
        // item's behalf, and a tick box that cannot change anything is better shown than hidden
        var fromItems = _wardrobe.ResolveBase(baseChar).Select(i => i.Slot).ToHashSet();

        ImGui.TextDisabled("Equipment slots — for the slot a mod is riding on rather than the gear " +
                           "in it.");
        ImGui.Spacing();
        DrawKeepSlotGrid(baseChar, EquipSlotEx.All.Where(s => !s.IsCustomization()).ToArray(), fromItems,
            s => $"Leave the {s.DisplayName()} slot exactly as it is when stripping.\n" +
                 "Whatever is worn there stays on, wardrobe item or plain gear.");

        ImGui.Spacing();
        ImGui.TextDisabled("Character mods — the hair, face and skin that are the character rather " +
                           "than clothes.");
        ImGui.Spacing();

        // Other is not in EquipSlotEx.All — it is only ever reached by detection or by hand — but a
        // piercing or a face paint is exactly the kind of thing that belongs to the character
        var characterSlots = EquipSlotEx.All.Where(s => s.IsCustomization())
            .Append(EquipSlot.Other)
            .ToArray();

        DrawKeepSlotGrid(baseChar, characterSlots, fromItems,
            s => $"Leave the {s.DisplayName()} mod you are wearing switched on when stripping.\n\n" +
                 "A strip cannot empty this slot — there is no game item in it — but it does\n" +
                 "turn the mod off, which takes the character's own hair or skin with it.");

        var kept = _wardrobe.KeptSlots(baseChar);
        ImGui.Spacing();
        ImGui.TextDisabled(kept.Count == 0
            ? "Nothing kept — a strip empties every equipment slot and turns off every worn mod."
            : $"Kept: {string.Join(", ", kept.OrderBy(s => (int)s).Select(s => s.DisplayName()))}");
    }

    /// <summary>
    /// A block of keep-this-slot tick boxes, three to a row.
    /// </summary>
    /// <remarks>
    /// Three to a row because a dozen slots down a single column is a lot of scrolling for a list
    /// everyone reads as a block. The tooltip is a callback rather than a string: the two blocks
    /// are held back for different reasons, and one wording for both would be wrong for each.
    /// </remarks>
    private void DrawKeepSlotGrid(BaseCharacter baseChar, EquipSlot[] slots,
        IReadOnlySet<EquipSlot> fromItems, Func<EquipSlot, string> tooltip)
    {
        var left   = ImGui.GetCursorPosX();
        var column = ImGui.GetContentRegionAvail().X / 3;

        for (var i = 0; i < slots.Length; i++)
        {
            var slot   = slots[i];
            var forced = fromItems.Contains(slot);
            var keep   = forced || baseChar.Keeps(slot);

            if (i % 3 != 0) ImGui.SameLine(left + column * (i % 3));

            if (forced) ImGui.BeginDisabled();
            if (ImGui.Checkbox($"{slot.DisplayName()}##keep_{slot}", ref keep) && !forced)
            {
                baseChar.SetKeep(slot, keep);
                _config.Save();
            }
            if (forced) ImGui.EndDisabled();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(forced
                    ? "Kept because one of this base character's items is worn here.\n" +
                      "Take the item out of the base to release the slot."
                    : tooltip(slot));
        }
    }

    /// <summary>The wardrobe items a base character wears, and the pickers that fill the list.</summary>
    private void DrawBaseItems(BaseCharacter baseChar)
    {
        var items = _wardrobe.ResolveBase(baseChar);

        ImGui.TextUnformatted($"Items  ({items.Count})");
        ImGui.TextDisabled("Applied with the base and never stripped. Hair, skin and the like, plus " +
                           "any gear item that is really part of the character.");
        ImGui.Spacing();

        var missing = baseChar.ItemIds.Count - items.Count;
        if (missing > 0)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                $"{missing} item(s) in this base character no longer exist.");

        Guid? removeId = null;
        foreach (var item in items)
        {
            ImGui.PushID($"baseitem_{item.Id}");

            if (DeleteButton("×", "Take this item out of the base character.\nThe item itself is kept."))
                removeId = item.Id;

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.75f, 0.95f, 1f));
            ImGui.TextUnformatted(item.Slot.DisplayName());
            ImGui.PopStyleColor();

            ImGui.SameLine();
            ImGui.TextUnformatted(item.Name);

            if (!_wardrobe.IsItemWorn(item))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(not worn)");
            }

            ImGui.PopID();
        }

        if (removeId.HasValue)
        {
            baseChar.ItemIds.Remove(removeId.Value);
            _config.Save();
        }

        ImGui.Spacing();

        var candidates = _config.WardrobeItems
            .Where(i => !baseChar.ItemIds.Contains(i.Id))
            // An animation or a mount is never stripped in the first place, so putting one in a
            // base character would protect it from nothing
            .Where(i => !i.Slot.IsModCategory())
            .Where(i => string.IsNullOrWhiteSpace(_addToBaseSearch) ||
                        i.Name.Contains(_addToBaseSearch.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => (int)i.Slot)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.SetNextItemWidth(UiScale.S(260));
        if (ImGui.BeginCombo("##addtobase", $"Add an item…  ({candidates.Count})"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##addbasesearch", "Search…", ref _addToBaseSearch, 128);
            ImGui.Separator();

            foreach (var item in candidates)
            {
                if (!ImGui.Selectable($"{item.Slot.DisplayName()} — {item.Name}##addbase_{item.Id}")) continue;

                // Guarded even though the candidate list already excludes what is in the base: a
                // duplicate here is invisible until it is four rows deep, and one user found it that
                // way. The list is the record, so it is the list that has to refuse.
                if (!baseChar.ItemIds.Contains(item.Id)) baseChar.ItemIds.Add(item.Id);
                _config.Save();
                _addToBaseSearch = string.Empty;
            }
            ImGui.EndCombo();
        }

        // The quickest route in for anyone already wearing their character: hair, skin, tail and
        // the rest are on right now, and naming them one at a time in the picker above is the
        // same list typed out by hand
        var worn = _config.WornItems.Values
            .Select(id => _config.WardrobeItems.Find(x => x.Id == id))
            .Where(i => i != null && i.Slot.IsCustomization() && !baseChar.ItemIds.Contains(i.Id))
            .Select(i => i!)
            .ToList();

        if (worn.Count > 0)
        {
            // Wraps to its own line when the picker beside it leaves no room, as the toolbar does
            UiLayout.SameLineIfRoomForButton($" Add worn customisation ({worn.Count}) ");
            if (ImGui.Button($" Add worn customisation ({worn.Count}) "))
            {
                foreach (var item in worn)
                    if (!baseChar.ItemIds.Contains(item.Id)) baseChar.ItemIds.Add(item.Id);
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Add every customisation mod you are wearing right now:\n" +
                                 string.Join("\n", worn.Select(i => $"  {i.Slot.DisplayName()} — {i.Name}")));
        }
    }

    /// <summary>Picks the Glamourer design a base character's customisations come from.</summary>
    private void DrawBaseDesignPicker(BaseCharacter baseChar)
    {
        ImGui.TextUnformatted("Glamourer design");
        ImGui.TextDisabled("Only its customisations are applied — face, colouring, hairstyle. Its " +
                           "gear is ignored, or it would put back the clothes a strip just removed.");
        ImGui.Spacing();

        _settingsDesigns ??= Plugin.Glamourer.GetDesigns();

        var current = baseChar.DesignId.HasValue
            ? (string.IsNullOrEmpty(baseChar.DesignName) ? "(unnamed design)" : baseChar.DesignName)
            : "(none)";

        ImGui.SetNextItemWidth(UiScale.S(260));
        if (ImGui.BeginCombo("##basedesign", current))
        {
            if (ImGui.Selectable("(none)", !baseChar.DesignId.HasValue))
            {
                baseChar.DesignId   = null;
                baseChar.DesignName = string.Empty;
                _config.Save();
            }

            foreach (var (id, name) in _settingsDesigns)
            {
                if (!ImGui.Selectable($"{name}##basedesign_{id}", baseChar.DesignId == id)) continue;

                baseChar.DesignId   = id;
                baseChar.DesignName = name;
                _config.Save();
            }
            ImGui.EndCombo();
        }

        UiLayout.SameLineIfRoomForButton("Refresh");
        if (ImGui.Button("Refresh##basedesigns"))
            _settingsDesigns = Plugin.Glamourer.GetDesigns();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-read the design list from Glamourer.");

        if (_settingsDesigns.Count == 0)
        {
            ImGui.TextDisabled("No designs found — create one in Glamourer first.");
            return;
        }

        if (!baseChar.DesignId.HasValue) return;

        UiLayout.SameLineIfRoomForButton("Apply in full");
        if (ImGui.Button("Apply in full##basedesign"))
            Plugin.Glamourer.ApplyDesignFull(baseChar.DesignId.Value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Apply this design's gear as well as its customisations, once, now.\n\n" +
                             "For getting dressed as your character in the first place. Nothing\n" +
                             "does this on its own — a strip would only take the gear off again.");
    }

    /// <summary>
    /// Whether outfits carry Glamourer's advanced dyes, and the probe for when one misbehaves.
    /// </summary>
    private void DrawAdvancedDyeSettings()
    {
        ImGui.TextUnformatted("Advanced dyes");
        ImGui.TextDisabled("Glamourer can dye a piece far past the game's two channels, editing a " +
                           "material's colour rows directly. With this on, an outfit can keep those " +
                           "rows for each of its items and put them back when it is worn.");
        ImGui.Spacing();

        var enabled = _config.AdvancedDyesEnabled;
        if (ImGui.Checkbox("Keep advanced dyes with outfits", ref enabled))
        {
            _config.AdvancedDyesEnabled = enabled;
            _config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds a tick box to each item's dyes in an outfit's edit panel.\n" +
                             "Glamourer stays the editor — the wardrobe only remembers.");

        if (!_config.AdvancedDyesEnabled)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Anything already captured is kept, and comes back when this is " +
                               "turned on again.");
        }
    }

    /// <summary>
    /// How big the cards in both grids are drawn.
    /// </summary>
    /// <remarks>
    /// A slider rather than a set of sizes: how many cards fit is a function of the window width,
    /// the font scale and how much of the picture someone wants to see, and no three presets are
    /// right for all of that. Both grids read it — a wardrobe browsed by picture wants big cards
    /// everywhere, and the outfits toggle only ever governed outfits.
    /// </remarks>
    private void DrawCardSizeSettings()
    {
        ImGui.TextUnformatted("Card Size");
        ImGui.TextDisabled("How large the cards in the item and outfit grids are drawn. Larger cards " +
                           "mean bigger previews and fewer per row.");
        ImGui.Spacing();

        DrawCardScaleSlider("Items", UiScale.S(260));
        ImGui.Spacing();
        DrawCardScaleSlider("Outfits", UiScale.S(260));

        ImGui.Spacing();
        ImGui.TextDisabled("Separate, because the two grids are looked at differently: an outfit " +
                           "preview is usually a full-body shot and wants the room, while more item " +
                           "cards on screen is the point of the item grid.");
    }

    /// <summary>
    /// One of the two card size sliders, with its pixel readout and a Reset once it is off default.
    /// </summary>
    /// <param name="grid">"Items" or "Outfits" — the only two, and what the slider is labelled with.</param>
    private void DrawCardScaleSlider(string grid, float width)
    {
        var outfits = grid == "Outfits";
        var value   = outfits ? _config.OutfitCardScale : _config.CardScale;
        var shownW  = outfits ? OutfitCardWidth  : CardWidth;
        var shownH  = outfits ? OutfitCardHeight : CardHeight;

        ImGui.TextDisabled(grid);
        ImGui.SetNextItemWidth(width);

        if (ImGui.SliderFloat($"##cardscale{grid}", ref value, MinCardScale, MaxCardScale,
                $"{shownW:0} × {shownH:0} px  (%.2fx)"))
        {
            if (outfits) _config.OutfitCardScale = value;
            else         _config.CardScale       = value;
            _config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ctrl-click to type a value.\n" +
                             "This is on top of Dalamud's Global Font Scale, not instead of it.");

        if (Math.Abs(value - 1f) <= 0.001f) return;

        ImGui.SameLine();
        if (!ImGui.Button($"Reset##cardscale{grid}")) return;

        if (outfits) _config.OutfitCardScale = 1f;
        else         _config.CardScale       = 1f;
        _config.Save();
    }

    /// <summary>
    /// Whether tags and styles carry a colour, and item cards take the colour of their style.
    /// </summary>
    private void DrawTagColourSettings()
    {
        ImGui.TextUnformatted("Tag Colours");
        ImGui.TextDisabled("Right-click a tag or style in the Tags panel to give it a colour. An " +
                           "item card takes the colour of its style, so a glance across the grid " +
                           "sorts it by mood before you read a word of it.");
        ImGui.Spacing();

        var enabled = _config.TagColoursEnabled;
        if (ImGui.Checkbox("Colour tags and styles", ref enabled))
        {
            _config.TagColoursEnabled = enabled;
            _config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turning this off keeps every colour you have picked —\n" +
                             "it only stops them being used.");

        var coloured = _config.TagColours.Count;

        ImGui.Spacing();
        if (!_config.TagColoursEnabled)
        {
            ImGui.TextDisabled(coloured > 0
                ? $"{coloured} colour(s) kept, and used again when this is turned back on."
                : "Nothing is coloured yet.");
            return;
        }

        ImGui.TextDisabled(coloured > 0
            ? $"{coloured} tag(s) and style(s) have a colour."
            : "Nothing is coloured yet. Right-click one in the Tags panel to start.");

        // Worn cards keep their gold, and it is worth saying so before someone colours a style and
        // wonders why the piece they are wearing ignored it
        ImGui.TextDisabled("A worn item keeps its gold card, whatever its style.");
    }

    private void DrawImageFolderSettings()
    {
        ImGui.TextUnformatted("Images Folder");
        ImGui.TextDisabled("Images here appear in the Image Browser, ready to drag onto item cards. " +
                           "A screenshot session also writes its finished shots here, cropped " +
                           "square and named after the item.");
        ImGui.Spacing();

        if (string.IsNullOrEmpty(_config.ImagesFolder))
            ImGui.TextDisabled("No folder selected.");
        else
        {
            ImGui.TextUnformatted(_config.ImagesFolder);
            if (!Directory.Exists(_config.ImagesFolder))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Folder does not exist.");
            }
        }

        ImGui.Spacing();
        if (ImGui.Button(" Browse… "))
        {
            var startDir = Directory.Exists(_config.ImagesFolder)
                ? _config.ImagesFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            _fileDialog.OpenFolderDialog("Select Images Folder", (confirmed, path) =>
            {
                if (!confirmed) return;
                _config.ImagesFolder = path;
                _config.Save();
                RefreshBrowserImages();
            }, startDir);
        }

        if (!string.IsNullOrEmpty(_config.ImagesFolder))
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.Button(" Clear##imgFolder"))
            {
                _config.ImagesFolder = string.Empty;
                _config.Save();
                _browserImages = Array.Empty<string>();
            }
            ImGui.PopStyleColor(2);
        }
    }

    /// <summary>
    /// Everything a screenshot session needs: where the game writes shots, how the wardrobe
    /// behaves while one runs, and where camera presets are kept.
    /// </summary>
    private void DrawScreenshotSettings()
    {
        ImGui.TextUnformatted("Screenshots");
        ImGui.Spacing();

        ImGui.TextUnformatted("FFXIV screenshots folder");
        ImGui.TextDisabled("Where FFXIV saves screenshots. A session watches it for new shots, " +
                           "then writes the cropped result to the images folder above.");
        ImGui.Spacing();

        var defaultSsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "FINAL FANTASY XIV - A Realm Reborn", "screenshots");

        if (string.IsNullOrEmpty(_config.ScreenshotsFolder))
            ImGui.TextDisabled("No folder configured.");
        else
        {
            ImGui.TextUnformatted(_config.ScreenshotsFolder);
            if (!Directory.Exists(_config.ScreenshotsFolder))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Folder does not exist.");
            }
        }

        // Same reason as in the walkthrough: with the folder already detected the Auto-detect
        // button is not drawn, and nothing else would say that detection had happened
        if (_config.ScreenshotsFolder == defaultSsFolder)
            ImGui.TextDisabled("Detected automatically.");

        ImGui.Spacing();

        if (ImGui.Button(" Browse…##ss"))
        {
            var startDir = Directory.Exists(_config.ScreenshotsFolder) ? _config.ScreenshotsFolder
                : Directory.Exists(defaultSsFolder)                    ? defaultSsFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            _fileDialog.OpenFolderDialog("Select FFXIV Screenshots Folder", (confirmed, path) =>
            {
                if (!confirmed) return;
                _config.ScreenshotsFolder = path;
                _config.Save();
            }, startDir);
        }

        if (Directory.Exists(defaultSsFolder) && _config.ScreenshotsFolder != defaultSsFolder)
        {
            UiLayout.SameLineIfRoomForButton(" Auto-detect ");
            if (ImGui.Button(" Auto-detect "))
            {
                _config.ScreenshotsFolder = defaultSsFolder;
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Use the usual location:\n{defaultSsFolder}");
        }

        if (!string.IsNullOrEmpty(_config.ScreenshotsFolder))
        {
            UiLayout.SameLineIfRoomForButton(" Clear ");
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.Button(" Clear##ssFolder"))
            {
                _config.ScreenshotsFolder = string.Empty;
                _config.Save();
            }
            ImGui.PopStyleColor(2);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Captured image size");
        ImGui.TextDisabled("Every shot is centre-cropped to a square and saved at this size. Larger " +
                           "is for looking at closely; each step up costs roughly four times the " +
                           "space per image.");
        ImGui.Spacing();

        var sizes    = ScreenshotSessionService.ImageSizes;
        var labels   = sizes.Select(s => $"{s} × {s}").ToArray();
        var sizeIdx  = Array.IndexOf(sizes, _config.CapturedImageSize);
        if (sizeIdx < 0) sizeIdx = 0;

        ImGui.SetNextItemWidth(UiScale.S(180));
        if (ImGui.Combo("##capturedsize", ref sizeIdx, labels, labels.Length))
        {
            _config.CapturedImageSize = sizes[sizeIdx];
            _config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("One per screen height: 1024 for 1080p, 1440 for 1440p, 2048 for 4K.\n\n" +
                             "Never upscales — the crop is as tall as your game window, so a 1080p\n" +
                             "screenshot gives about 1080 pixels however large a size is chosen.");

        ImGui.TextDisabled("Existing images are left as they are — this applies to shots taken from now on.");

        ImGui.Spacing();

        var portrait = _config.PortraitOutfitPreviews;
        if (ImGui.Checkbox("Portrait outfit previews (9:16)", ref portrait))
        {
            _config.PortraitOutfitPreviews = portrait;
            _config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Outfit previews are full-body shots, and a square crop of one spends\n" +
                             "most of the frame on the floor either side of the character. Match\n" +
                             "GPose's own portrait mode and the card grows taller instead.\n\n" +
                             "Shoot in GPose's portrait mode and the game saves it upright, ready\n" +
                             "to crop.\n\n" +
                             "Item previews stay square — they are close-ups of one piece.");

        ImGui.TextDisabled(portrait
            ? "Outfit cards are taller. Pictures already assigned are centre-cropped to fit, so " +
              "nothing has to be re-taken."
            : "Outfit and item previews are both square.");

        ImGui.Spacing();
        ImGui.TextUnformatted("During a session");
        ImGui.Spacing();

        var manualSession = _session.Manual;
        if (ImGui.Checkbox("Manual mode", ref manualSession))
            _session.Manual = manualSession;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The session stays on each item however many screenshots you take: the\n" +
                             "first becomes the card's picture and the rest join it, and it moves on\n" +
                             "when you press Next Item.\n\n" +
                             "Off, it moves on as soon as a screenshot lands — one per item, plus any\n" +
                             "camera angles you have ticked.\n\n" +
                             "Set here, this applies from the very first item of a session.\n" +
                             "The same checkbox is on the session HUD.");

        ImGui.TextDisabled(manualSession
            ? "Framing a piece well usually takes a few attempts, and this is the mode for that."
            : "One screenshot per item, plus a shot at each camera angle you have ticked.");

        var stripDuringSession = _session.StripOthers;
        if (ImGui.Checkbox("Strip other items before each shot", ref stripDuringSession))
            _session.StripOthers = stripDuringSession;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes all other worn items via Emperor's New before equipping\n" +
                             "each item, so only that item appears in the shot.\n\n" +
                             "Set here, this applies from the very first item of a session.\n" +
                             "The same checkbox is on the session HUD.");

        var compactDuringSession = _config.CompactDuringSession;
        if (ImGui.Checkbox("Compact main window during session", ref compactDuringSession))
        {
            _config.CompactDuringSession = compactDuringSession;
            _config.Save();
            _compactOverride = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shrinks the wardrobe window to a small session view for the\n" +
                             "duration of a screenshot session, hiding the grid and panels.\n\n" +
                             "The same checkbox is on the session HUD, and the compact view\n" +
                             "has an Expand button for a one-off return to the full window.");

        // Not settings, but visible changes to the character while a session runs
        ImGui.Spacing();
        ImGui.TextDisabled("Your weapon is hidden in Glamourer for each shot regardless of the " +
                           "above, unless the item being photographed is the weapon. Your hat is " +
                           "shown while a head piece is being photographed, since a hidden one would " +
                           "give you a folder of bare heads. Both are put back as you had them when " +
                           "the session ends.");

        // No setting of its own, because the angles live on the presets — which is the point. Said
        // here because this is where someone reads about sessions, and a session that could have taken
        // a back view of everything is worth knowing about before running one over a whole wardrobe.
        ImGui.Spacing();
        ImGui.TextDisabled("A session takes one shot per item by default. Tick a camera preset in a " +
                           "slot's list and it takes a shot at that angle too, filed as another of " +
                           "that item's pictures — a side, a back, a close-up. The preset selected " +
                           "with the button on its left is the cover.");

        if (_config.SlotCameraPresetLists.Values.Any(list => list.Any(p => p.CaptureInSession)))
        {
            var slots = _config.SlotCameraPresetLists
                .Where(kv => _config.ExtraShotPresetsFor(kv.Key).Count > 0)
                .Select(kv => $"{kv.Key} ({_config.ExtraShotPresetsFor(kv.Key).Count + 1})")
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

            ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f),
                "Extra angles set for: " + string.Join(", ", slots) + ".");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shots per item, the cover included, for every slot that has more\n" +
                                 "than one. Every other slot still takes a single shot.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Camera presets");
        ImGui.TextDisabled("Angles a session shoots from, per slot. Set them up before a run rather " +
                           "than inventing each one while the session waits.");
        ImGui.Spacing();

        if (ImGui.Button("Edit Angles For Every Slot", new Vector2(-1, 0)))
        {
            _showCameraPresets = true;
            _showSettings      = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens every slot's preset list in one panel, so a whole wardrobe's angles\n" +
                             "can be framed in one visit to GPose.\n\n" +
                             "The same controls the session HUD shows for whichever slot it is on.");

        var slotsWithPresets = _config.SlotCameraPresetLists.Count(kv => kv.Value.Count > 0);
        ImGui.TextDisabled(slotsWithPresets == 0
            ? "No angles saved yet."
            : $"{slotsWithPresets} slot(s) have an angle saved.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Camera presets file");
        ImGui.TextDisabled("Per-slot camera presets are saved here automatically.");
        ImGui.Spacing();

        if (string.IsNullOrEmpty(_config.CameraPresetsPath))
            ImGui.TextDisabled("No file configured.");
        else
        {
            ImGui.TextUnformatted(_config.CameraPresetsPath);
            if (!File.Exists(_config.CameraPresetsPath))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "File does not exist yet — will be created on first save.");
            }
        }

        ImGui.Spacing();

        if (ImGui.Button(" Browse…##camPresets"))
        {
            var startDir = !string.IsNullOrEmpty(_config.CameraPresetsPath)
                ? System.IO.Path.GetDirectoryName(_config.CameraPresetsPath) ?? string.Empty
                : !string.IsNullOrEmpty(_config.ImagesFolder) ? _config.ImagesFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            _fileDialog.SaveFileDialog("Camera Presets File", ".json", "camera_presets", ".json",
                (confirmed, path) =>
                {
                    if (!confirmed) return;

                    // Only write when the file is new. Pointing at one that already exists is how a
                    // presets file gets imported — from a backup, or from someone who shared theirs
                    // — and writing over it here destroyed the very thing Load from File was about
                    // to read, a second before it could be read.
                    var isNew = !File.Exists(path);

                    _config.CameraPresetsPath = path;
                    _config.Save();
                    if (isNew) _config.SavePresets();

                    _cameraLoadStatus = isNew
                        ? "Presets file created."
                        : "Existing file — press Load from File to read it.";
                }, startDir);
        }

        if (!string.IsNullOrEmpty(_config.CameraPresetsPath))
        {
            UiLayout.SameLineIfRoomForButton(" Load from File ");
            if (ImGui.Button(" Load from File "))
            {
                _cameraLoadStatus = _config.LoadPresets() ? "Presets loaded." : "Failed to load — check file path.";
                _config.Save();
            }

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.Button(" Clear##camPresetsPath"))
            {
                _config.CameraPresetsPath = string.Empty;
                _config.Save();
            }
            ImGui.PopStyleColor(2);
        }

        if (!string.IsNullOrEmpty(_cameraLoadStatus))
        {
            ImGui.Spacing();
            ImGui.TextDisabled(_cameraLoadStatus);
        }
    }

    private void DrawSetupSettings()
    {
        ImGui.TextUnformatted("Setup");
        ImGui.TextDisabled("The first-run walkthrough, if you want it again.");
        ImGui.Spacing();

        if (ImGui.Button(" Run first-time setup again "))
        {
            _onboardStep    = 0;
            _penumbraCheck  = null;
            _glamourerCheck = null;
            _config.OnboardingCompleted = false;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Walks through the collection, what the wardrobe holds, the folders\n" +
                             "and backups again.\n\n" +
                             "Every step starts from your current setting, and only what you\n" +
                             "change is changed. Wardrobe items and images are never touched.");
    }

    /// <summary>
    /// Opt-in for managing mods that are not equipment. Kept behind a switch because the extra
    /// filter buttons and slot-picker entries are noise to a wardrobe made only of gear.
    /// </summary>
    private void DrawModCategorySettings()
    {
        ImGui.TextUnformatted("Other Mod Types");
        ImGui.TextDisabled("Keep animations, VFX and mounts alongside your gear. They have no game " +
                           "item, so wearing one only enables its Penumbra mod.");
        ImGui.Spacing();

        var enabled = _config.ModCategoriesEnabled;
        if (ImGui.Checkbox("Manage other mod types", ref enabled))
        {
            _config.ModCategoriesEnabled = enabled;

            // A filter pointing at a category that just disappeared would leave the grid
            // permanently empty with no visible button to clear it
            if (!enabled && _slotFilter is { } f && f.IsModCategory())
                _slotFilter = null;

            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds Animation, VFX and Mount / Minion to the filter bar and to the\n" +
                             "slot pickers when importing or editing an item.\n\n" +
                             "Animation covers every animation mod, not only emotes — idles,\n" +
                             "poses, movement and battle animations all land there.\n\n" +
                             "Turning it off hides items in those categories from the grid\n" +
                             "but keeps them saved — turning it back on restores them.");

        var modCategoryCount = _config.WardrobeItems.Count(i => i.Slot.IsModCategory());
        if (modCategoryCount > 0 && !_config.ModCategoriesEnabled)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f),
                $"{modCategoryCount} item(s) in these categories are currently hidden.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Animation mods replacing the same animation swap each other out, like " +
                           "two body mods. What each one replaces is detected on import, and can " +
                           "be changed when editing it.");
    }

    /// <summary>
    /// The one switch behind design cards: whether your Glamourer designs appear in the outfits grid.
    /// </summary>
    /// <remarks>
    /// Deliberately the whole of it. There is nothing to sync and nothing to keep up to date, so there
    /// is nothing else to decide — the cards are Glamourer's design list as it stands, and everything
    /// per-card lives on the card. Modelled on <see cref="DrawModCategorySettings"/>, which is the other
    /// switch in here that decides what the grid contains rather than how it behaves.
    /// </remarks>
    private void DrawGlamourerDesignSettings()
    {
        ImGui.TextUnformatted("Glamourer Designs");
        ImGui.TextDisabled("Show your Glamourer designs in the outfits grid, so each one can carry " +
                           "pictures, tags and the mods that belong with it.");
        ImGui.Spacing();

        var show = _config.ShowGlamourerDesigns;
        if (ImGui.Checkbox("Show Glamourer designs as outfits", ref show))
        {
            _config.ShowGlamourerDesigns = show;
            _config.Save();

            // Straight away rather than on the next visit to the grid, so the count below is the truth
            // by the time the checkbox has finished being clicked — and from Glamourer rather than from
            // the half-second cache, since this is the one moment the answer is being asked for
            if (show)
            {
                Plugin.Glamourer.InvalidateDesigns();
                _wardrobe.ReconcileDesignCards();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A card per design, alongside your own outfits.\n\n" +
                             "This is a live link, not a copy: a design saved in Glamourer appears\n" +
                             "here on its own, a rename follows through, and deleting it there\n" +
                             "removes the card. Nothing to sync, nothing to keep up to date.\n\n" +
                             "Wearing one applies the design, then any wardrobe items attached to\n" +
                             "it — which is how the mods that belong with a look get enabled, since\n" +
                             "a design knows nothing about Penumbra.\n\n" +
                             "Turning this off hides the cards and keeps everything attached to them.");

        var live  = Plugin.Glamourer.GetDesignsCached().Count;
        var cards = _wardrobe.DesignOutfits().Count;

        if (!show)
        {
            if (cards > 0)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f),
                    $"{cards} design card(s) are currently hidden. Nothing attached to them is lost.");
            else if (live > 0)
                ImGui.TextDisabled($"Glamourer has {live} design(s) to show.");
            return;
        }

        ImGui.TextDisabled(live == 0
            ? "Glamourer has no designs to show, or is not answering."
            : $"{live} design(s) linked. Renames and deletions in Glamourer follow through by themselves.");

        // The one thing that can go wrong, offered here as well as in the grid: someone who turned the
        // cards off is exactly the person who will not see the notice there
        var stranded = _wardrobe.StrandedDesignCards();
        if (stranded.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.78f, 0.6f, 0.95f, 1f),
            $"{stranded.Count} card(s) have lost their design: " +
            string.Join(", ", stranded.Select(o => o.Name)));
        ImGui.TextDisabled("Their pictures, tags and attached items are kept. Cards holding nothing are " +
                           "dropped by themselves — these are the ones with something in them.");
    }

    private static readonly (VariantNameStyle Style, string Label)[] VariantNameStyles =
    {
        (VariantNameStyle.Suffix,    "Plain — (variant)"),
        (VariantNameStyle.Numbered,  "Numbered — (Variant-1)"),
        (VariantNameStyle.Lettered,  "Lettered — (Variant-A)"),
        (VariantNameStyle.Timestamp, "When it was made — (date - time)"),
    };

    private void DrawVariantSettings()
    {
        ImGui.TextUnformatted("Variants");
        ImGui.TextDisabled("Fold an item's variants into its card, with a button on the card to " +
                           "show them. Turn this off to give every variant a card of its own.");
        ImGui.Spacing();

        DrawVariantNameSetting();

        ImGui.Spacing();

        var group = _config.GroupVariants;
        if (ImGui.Checkbox("Group variants under their original", ref group))
        {
            _config.GroupVariants = group;
            _config.Save();
        }

        if (!_config.GroupVariants) return;

        ImGui.Spacing();
        if (ImGui.Button("Fold every group", new Vector2(-1, 0)))
        {
            var count = _config.ExpandedVariantGroups.Count;
            _config.ExpandedVariantGroups.Clear();
            _config.Save();
            _log.Information($"[Wardrobe] Folded {count} expanded variant group(s)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts every group you have expanded back to showing just its original.");

        ImGui.Spacing();
        ImGui.TextDisabled("A variant that is currently worn is never folded away, so the grid " +
                           "always shows what is on your character.");
    }

    /// <summary>
    /// What <b>Create variant of this item</b> calls the copy it makes.
    /// </summary>
    /// <remarks>
    /// Shown with a worked example of the next two, because the difference between the styles is
    /// entirely in what happens on the second one — the plain suffix gives both the same name, and
    /// the timestamp gives both the same name too if they are made in the same minute. Neither is
    /// apparent from a single sample.
    /// </remarks>
    private void DrawVariantNameSetting()
    {
        ImGui.TextDisabled("Name new variants");

        var current = VariantNameStyles.FirstOrDefault(s => s.Style == _config.VariantNameStyle);
        var preview = current.Label ?? VariantNameStyles[0].Label;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##variantName", preview))
        {
            foreach (var (style, label) in VariantNameStyles)
            {
                if (ImGui.Selectable(label, style == _config.VariantNameStyle))
                {
                    _config.VariantNameStyle = style;
                    _config.Save();
                }
                if (style == _config.VariantNameStyle) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var first  = "Silk Top" + WardrobeService.VariantSuffix(_config.VariantNameStyle, 1);
        var second = "Silk Top" + WardrobeService.VariantSuffix(_config.VariantNameStyle, 2);

        // The timestamp style is the one case where the two samples matching says nothing — they
        // match because the preview draws them in the same frame, not because the style repeats
        // itself. Showing one sample and naming the real condition is the honest way round.
        if (_config.VariantNameStyle == VariantNameStyle.Timestamp)
        {
            ImGui.TextDisabled($"Right now that would be:  {first}");
            ImGui.TextDisabled("Down to the minute, so two variants made within the same minute " +
                               "share a name. Made any further apart, each is distinct.");
        }
        else
        {
            ImGui.TextDisabled($"First two would be:  {first},  {second}");

            if (first == second)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                    "Every variant of an item gets the same name with this style.");
        }

        ImGui.TextDisabled("Only the name it starts with — the copy opens for editing, so it can " +
                           "be changed there. Existing variants are not renamed.");
    }

    private void DrawSlotIconSettings()
    {
        ImGui.TextUnformatted("Slot Icons");
        ImGui.TextDisabled("Show an icon instead of the slot name on cards and filters. " +
                           "Icons are narrower, so more slots fit on the filter bar.");
        ImGui.Spacing();

        var icons = _config.SlotIconsEnabled;
        if (ImGui.Checkbox("Use icons for slots", ref icons))
        {
            _config.SlotIconsEnabled = icons;
            _config.Save();
        }

        if (!_config.SlotIconsEnabled) return;

        ImGui.Spacing();
        ImGui.TextDisabled("Icon set");
        ImGui.SetNextItemWidth(UiScale.S(220));

        // Order must match the SlotIconStyle enum values
        var styleLabels = new[] { "FFXIV game icons", "Font Awesome" };
        var styleIdx    = (int)_config.SlotIconStyle;
        if (ImGui.Combo("##sloticonstyle", ref styleIdx, styleLabels, styleLabels.Length))
        {
            _config.SlotIconStyle = (SlotIconStyle)styleIdx;
            _config.Save();
        }

        if (_config.SlotIconStyle == SlotIconStyle.GameIcons)
            ImGui.TextDisabled("Hair uses a character-creation icon. Face, tail, ears and skin " +
                               "have no game artwork, so they use Font Awesome.");

        ImGui.Spacing();
        DrawSlotIconSizes();

        ImGui.Spacing();
        DrawCustomIconSettings();

        // Live preview — also the quickest way to spot a wrong game-icon ID
        ImGui.Spacing();
        DrawSlotIconPreview();
    }

    /// <summary>
    /// Folder of user-supplied slot icons, layered over whichever set is selected.
    /// </summary>
    /// <remarks>
    /// The weakness of naming files after slots is that getting a name wrong does nothing visible,
    /// so the coverage list is not decoration — it is the only way to tell "I have not added that
    /// one yet" from "I spelled it wrong". It names the file each missing slot is looking for.
    /// </remarks>
    private void DrawCustomIconSettings()
    {
        ImGui.TextDisabled("Your own icons");
        ImGui.TextDisabled("Images named after their slots — Head.png, Body.png, RingRight.png. " +
                           "Install a zipped pack, or point at a folder of your own. Any slot you " +
                           "supply uses your image; the rest stay on the set chosen above.");
        ImGui.Spacing();

        DrawIconPackSettings();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawCustomIconFolderSettings();

        // Once, under both — what is covered is the sum of the two, and where a given icon came
        // from is not something the list needs to answer
        if (Plugin.IconPacks.Active != null || !string.IsNullOrEmpty(_config.CustomIconFolder))
            DrawCustomIconCoverage();
    }

    /// <summary>
    /// Installed icon packs: a dropdown to choose one, an x on each to remove it, and an importer.
    /// </summary>
    /// <remarks>
    /// A pack is unpacked into the plugin's own config directory rather than left zipped, so what is
    /// installed is the same folder of slot-named files the setting below already accepts. Nothing
    /// new can go wrong at draw time, and a pack can be opened, edited or zipped back up by hand.
    /// </remarks>
    private void DrawIconPackSettings()
    {
        var packs  = Plugin.IconPacks.Packs;
        var active = Plugin.IconPacks.Active;

        ImGui.TextDisabled("Icon pack");

        ImGui.SetNextItemWidth(UiScale.S(220));
        if (ImGui.BeginCombo("##iconpack", active?.DisplayName ?? "None"))
        {
            if (ImGui.Selectable("None", active == null))
            {
                Plugin.IconPacks.Select(string.Empty);
                Plugin.SlotIcons.Rescan();
            }

            foreach (var pack in packs)
            {
                // The row is narrowed to leave room for the x, which sits on the same line. A pack
                // is removed from where it is chosen — the alternative is selecting one in order to
                // delete it, which changes what you are looking at on the way to throwing it away.
                var removeWidth = ImGui.CalcTextSize("x").X + ImGui.GetStyle().FramePadding.X * 2;
                var rowWidth    = Math.Max(UiScale.S(120), ImGui.GetContentRegionAvail().X - removeWidth
                                                           - ImGui.GetStyle().ItemSpacing.X);

                if (ImGui.Selectable($"{pack.DisplayName}##pack{pack.Id}", pack.Id == active?.Id,
                        ImGuiSelectableFlags.None, new Vector2(rowWidth, 0)))
                {
                    Plugin.IconPacks.Select(pack.Id);
                    Plugin.SlotIcons.Rescan();
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(PackTooltip(pack));

                ImGui.SameLine();
                if (DeleteButton($"x##remove{pack.Id}", $"Remove '{pack.DisplayName}'."))
                {
                    // Deferred: opening a modal from inside a combo popup fights with the combo for
                    // focus, so the popup is opened next frame from outside it
                    _iconPackPendingRemoval = pack.Id;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndCombo();
        }

        // Wraps rather than running off the panel: the combo is a fixed 220 and these two buttons
        // behind it do not fit beside it at every text scale
        UiLayout.SameLineIfRoomForButton(" Import zip… ");
        if (ImGui.Button(" Import zip… "))
        {
            _fileDialog.OpenFileDialog("Import Icon Pack", "Icon pack{.zip}", (confirmed, path) =>
            {
                if (!confirmed) return;

                // The zip is only read — the file stays wherever the user keeps it
                Plugin.IconPacks.TryImport(path, out _iconPackStatus);
                Plugin.SlotIcons.Rescan();
            });
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Unpacks it into the plugin's own folder and switches to it.");

        if (packs.Count > 0)
        {
            // The path rather than the folder itself: an installed pack is a folder of ordinary
            // files worth editing, and this plugin does not start processes of its own — see the
            // note in NoteText.Open
            UiLayout.SameLineIfRoomForButton(" Copy path ");
            if (ImGui.Button(" Copy path##iconpacks "))
            {
                Plugin.IconPacks.EnsureRoot();
                ImGui.SetClipboardText(Plugin.IconPacks.PacksRoot);
                _iconPackStatus = "Path copied — paste it into Explorer to edit or zip a pack.";
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Plugin.IconPacks.PacksRoot);
        }

        if (Plugin.IconPacks.ActiveIsMissing)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                "The selected pack is no longer installed.");
        }
        else if (active != null)
        {
            ImGui.TextDisabled(PackTooltip(active));
        }

        if (!string.IsNullOrEmpty(_iconPackStatus))
            ImGui.TextWrapped(_iconPackStatus);

        DrawIconPackRemoveConfirm();
    }

    private static string PackTooltip(IconPack pack)
    {
        var author = string.IsNullOrWhiteSpace(pack.Author) ? string.Empty : $" · by {pack.Author}";
        return $"{pack.MatchedSlots} slot(s){author}";
    }

    /// <summary>
    /// Confirms removing a pack, since the folder and everything in it is deleted.
    /// </summary>
    /// <remarks>
    /// Worth a modal rather than an inline second click: a pack may be the only copy of the images
    /// if the zip it came from has since been tidied away, and the x is small and sits in a list
    /// people are scrolling through to pick something else.
    /// </remarks>
    private void DrawIconPackRemoveConfirm()
    {
        if (_iconPackPendingRemoval != null && !ImGui.IsPopupOpen(IconPackRemovePopup))
            ImGui.OpenPopup(IconPackRemovePopup);

        var vp     = ImGui.GetMainViewport();
        var centre = new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f);
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(IconPackRemovePopup, ImGuiWindowFlags.AlwaysAutoResize)) return;

        var pack = Plugin.IconPacks.Packs.FirstOrDefault(p => p.Id == _iconPackPendingRemoval);
        if (pack == null)
        {
            _iconPackPendingRemoval = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Remove the icon pack '{pack.DisplayName}'?");
        ImGui.Spacing();
        ImGui.TextDisabled("Its folder and the images in it are deleted. Your own icon folder and\n" +
                           "everything else in the wardrobe are untouched.");
        ImGui.Spacing();
        ImGui.TextDisabled("Re-import the zip to get it back.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (DeleteButton(" Remove ", $"Deletes the pack folder and the images in it.", UiScale.S(140, 0)))
        {
            Plugin.IconPacks.Uninstall(pack.Id, out _iconPackStatus);
            Plugin.SlotIcons.Rescan();
            _iconPackPendingRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(" Cancel ", UiScale.S(120, 0)))
        {
            _iconPackPendingRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawCustomIconFolderSettings()
    {
        ImGui.TextDisabled("Your own folder");
        ImGui.TextDisabled("Layered over the pack, so a file here replaces that one slot and leaves " +
                           "the rest of the pack alone.");
        ImGui.Spacing();

        var folder = _config.CustomIconFolder;

        if (string.IsNullOrEmpty(folder))
        {
            ImGui.TextDisabled("No folder selected.");
        }
        else
        {
            ImGui.TextUnformatted(folder);
            if (!Directory.Exists(folder))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Folder does not exist.");
            }
        }

        ImGui.Spacing();
        if (ImGui.Button(" Browse…##customIcons "))
        {
            var startDir = Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            _fileDialog.OpenFolderDialog("Select Custom Icon Folder", (confirmed, path) =>
            {
                if (!confirmed) return;
                _config.CustomIconFolder = path;
                _config.Save();
                Plugin.SlotIcons.Rescan();
            }, startDir);
        }

        // Files are added to either source outside the game, so there has to be a way to pick them
        // up without changing the setting or restarting
        UiLayout.SameLineIfRoomForButton(" Rescan ");
        if (ImGui.Button(" Rescan "))
        {
            Plugin.IconPacks.Refresh();
            Plugin.SlotIcons.Rescan();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-reads the pack and the folder. Use after adding or renaming a file.");

        if (string.IsNullOrEmpty(folder)) return;

        UiLayout.SameLineIfRoomForButton(" Clear ");
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.Button(" Clear##customIcons "))
        {
            _config.CustomIconFolder = string.Empty;
            _config.Save();
            Plugin.SlotIcons.Rescan();
        }
        ImGui.PopStyleColor(2);
    }

    private void DrawCustomIconCoverage()
    {
        var slots = SlotIconService.IconSlots
            .Where(s => _config.ModCategoriesEnabled || !s.IsModCategory())
            .ToList();

        var found = slots.Count(Plugin.SlotIcons.HasCustomIcon);

        ImGui.Spacing();
        ImGui.TextColored(found > 0 ? new Vector4(0.5f, 0.85f, 0.6f, 1f) : new Vector4(1f, 0.75f, 0.3f, 1f),
            $"{found} of {slots.Count} slots have your icon.");

        if (!ImGui.CollapsingHeader("Which slots###customIconCoverage")) return;

        ImGui.TextDisabled("Hover a slot for every name it will answer to.");
        ImGui.Spacing();

        foreach (var slot in slots)
        {
            if (Plugin.SlotIcons.HasCustomIcon(slot))
            {
                ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.6f, 1f), $"  {slot.DisplayName()}");
            }
            else
            {
                ImGui.TextDisabled($"  {slot.DisplayName()}");
                UiLayout.SameLineIfRoomForText($"— add {SlotIconService.ExpectedFileName(slot)}");
                ImGui.TextDisabled($"— add {SlotIconService.ExpectedFileName(slot)}");
            }

            // The names are the whole difficulty of custom icons: a misspelt file is indistinguishable
            // from one that was never added, so the alternatives are worth having to hand
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Any of: " + string.Join(", ", SlotIconService.AcceptedNames(slot)));
        }
    }

    /// <summary>
    /// The two icon sizes: on item cards, and on the slot filter row.
    /// </summary>
    /// <remarks>
    /// Separate because the two are traded against different things. Shrinking the filter row fits
    /// more slots on it before the rest spill into <b>More</b>, which has nothing to do with how
    /// legible one icon is on a card. Both are shown as their pixel size as well as a multiplier,
    /// since a multiplier alone means nothing until you know what it multiplies.
    /// </remarks>
    private void DrawSlotIconSizes()
    {
        ImGui.TextDisabled("Icon size");

        var card = _config.SlotIconScale;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##iconscale", ref card,
                SlotIconService.MinScale, SlotIconService.MaxScale,
                $"Cards — {Plugin.SlotIcons.ScaledSize:0} px  (%.2fx)"))
        {
            _config.SlotIconScale = card;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The icon on each item card. Cards grow taller to fit a\n" +
                             "larger one, so nothing is pushed off the bottom.");

        var row = _config.SlotIconRowScale;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##iconrowscale", ref row,
                SlotIconService.MinScale, SlotIconService.MaxScale,
                $"Filter row — {Plugin.SlotIcons.ScaledRowSize:0} px  (%.2fx)"))
        {
            _config.SlotIconRowScale = row;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The slot buttons along the top. Smaller icons fit more\n" +
                             "slots on the row before the rest move into More.");

        // Ctrl-click to type a value is an ImGui convention, not something a slider advertises
        ImGui.TextDisabled("Drag, or ctrl-click to type a value.");

        if (card != 1f || row != 1f)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
            {
                _config.SlotIconScale    = 1f;
                _config.SlotIconRowScale = 1f;
                _config.Save();
            }
        }
    }

    /// <summary>
    /// Every slot's icon in a row, so the setting can be judged by looking at it.
    /// </summary>
    /// <remarks>
    /// The row is as long as the slot list, so it continues onto the next line rather than running
    /// off the side — the settings panel is only 360px wide.
    /// </remarks>
    private static void DrawSlotIconPreview()
    {
        ImGui.TextDisabled("Preview");

        var drewAny = false;
        foreach (var slot in EquipSlotEx.All)
        {
            if (drewAny) UiLayout.SameLineIfRoom(Plugin.SlotIcons.ScaledSize);
            if (!Plugin.SlotIcons.Draw(slot)) continue;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot.DisplayName());
            drewAny = true;
        }
    }

    public void Dispose()
    {
        _panel.Dispose();
    }
}
