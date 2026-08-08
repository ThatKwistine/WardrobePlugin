using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using WardrobePlugin;
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

    // Set when the user expands out of compact mode mid-session; cleared when the session ends,
    // so it overrides the setting for this session only rather than turning it off permanently.
    private bool _compactOverride;
    private bool _wasCompact;

    // The size the window had the last time it drew expanded, so leaving compact mode puts it back
    // where the user left it rather than at the default.
    private Vector2? _lastExpandedSize;

    // Right panel mode
    private bool _showImageBrowser;
    private bool _showSettings;
    private bool _showTags;
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

    private static float CardWidth => UiScale.S(BaseCardWidth);
    private static float CardPad   => UiScale.S(BaseCardPad);
    private static float ThumbSize => CardWidth - CardPad * 2; // square thumbnail

    /// <summary>
    /// Card height, grown to fit a slot icon larger than the size the card was designed around.
    /// </summary>
    /// <remarks>
    /// The icon sits on the badge row, so scaling it up pushes the button row towards the bottom
    /// edge — and past it, since a card clips rather than scrolls. Everything that lays the grid out
    /// reads this, so the rows grow with it and nothing overlaps.
    /// </remarks>
    private static float CardHeight =>
        UiScale.S(BaseCardHeight) + (Plugin.SlotIcons.Enabled
            ? Math.Max(0f, Plugin.SlotIcons.ScaledSize - SlotIconService.BaseScaledSize)
            : 0f);

    // Outfit previews are full-body shots rather than close-ups, so they get more room by default
    private static float OutfitCardWidth  => UiScale.S(280f);
    private static float OutfitCardHeight => UiScale.S(400f);

    // Window.Size and Window.SizeConstraints are scaled by Dalamud itself — see IWindow's docs — so
    // these stay in unscaled units. Putting them through UiScale would scale them twice.
    private static readonly Vector2 DefaultSize = new(1000, 700);
    private static readonly Vector2 CompactSize = new(360, 200);
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
            else if (_showSettings)
                DrawSettings();

            UiLayout.PopWrap();
            ImGui.EndChild();
        }

        // Must be drawn outside child windows so the dialog isn't clipped
        _fileDialog.Draw();

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
                             "on the character, so there is nothing to strip.");

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

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
        if (ImGui.Button($" Delete {items.Count} ", UiScale.S(140, 0)))
        {
            ApplyBulkDelete(items);
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor(2);

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

        // Outfits are filtered by none of the tag filters, so the dropdown would visibly do nothing
        // there. The filter itself is kept, so coming back to items restores what was showing.
        _frameStyles = _outfitsView ? Array.Empty<TagNode>() : styles;

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
        item.Tags.Any(t => filter.Any(f =>
            string.Equals(t, f, StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)));

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
            var active = _tagFilter.Contains(child.FullPath);
            if (active)            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.78f, 0.58f, 1f, 1f));
            else if (!child.InUse) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.45f, 0.52f, 1f));

            var isLeaf = child.Children.Count == 0;
            var flags  = ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isLeaf)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            else
                flags |= ImGuiTreeNodeFlags.OpenOnArrow; // expand/collapse only via the arrow

            var open = ImGui.TreeNodeEx($"##{child.FullPath}", flags, child.Segment);
            if (active || !child.InUse) ImGui.PopStyleColor();

            if (!child.InUse)
            {
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{child.FullPath}\n\nNo items have this tag yet.\n" +
                                     "Right-click to delete it.");

                if (ImGui.BeginPopupContextItem($"##tagctx_{child.FullPath}"))
                {
                    if (ImGui.MenuItem($"Delete '{child.Segment}'"))
                        deleted = child.FullPath;
                    ImGui.EndPopup();
                }
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
        DrawNewTagRow();

        ImGui.Spacing();
        DrawStylesSection();

        // Styles are tags, so the panel is never truly empty once one exists — but the tag tree
        // below it can still be, and says so rather than showing a bare header
        if (TagTree.Build(_config, includeStyles: false).Children.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No tags yet. Make one above, or add them when editing an item.");
            return;
        }

        ImGui.Spacing();
        var header = _tagFilter.Count > 0
            ? $"Tags  ·  {_tagFilter.Count} active###TagsHeader"
            : "Tags###TagsHeader";

        if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (_tagFilter.Count > 0)
            {
                ImGui.Spacing();
                if (ImGui.SmallButton("× Clear")) _tagFilter.Clear();
                ImGui.Separator();
            }
            DrawTagTree(TagTree.Build(_config, includeStyles: false));
            ImGui.Spacing();
        }
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

            if (i > 0) UiLayout.SameLineIfRoomForButton(style.Segment);

            if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
            }
            else if (!style.InUse)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.45f, 0.52f, 1f));
            }

            var clicked = ImGui.SmallButton($"{style.Segment}##style_{style.FullPath}");

            if (active)             ImGui.PopStyleColor(2);
            else if (!style.InUse)  ImGui.PopStyleColor();

            if (clicked && !_styleFilter.Remove(style.FullPath))
                _styleFilter.Add(style.FullPath);

            // Same rule as the tag tree: only a style nothing carries can be deleted, so removing
            // one can never take a style off an item behind the user's back
            if (!style.InUse)
            {
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("No items have this style yet.\nRight-click to delete it.");

                if (ImGui.BeginPopupContextItem($"##stylectx_{style.FullPath}"))
                {
                    if (ImGui.MenuItem($"Delete '{style.Segment}'")) deleted = style.FullPath;
                    ImGui.EndPopup();
                }
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
    /// error. Made here, they show up dimmed in the tree below and in every tag picker, ready to be
    /// applied — most usefully from Select → Edit Selected, which can tag a whole batch at once.
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

    private void DrawCard(WardrobeItem item, ref Guid? pendingDelete)
    {
        var worn = _wardrobe.IsItemWorn(item);

        ImGui.PushID(item.Id.ToString());
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            worn ? new Vector4(0.22f, 0.18f, 0.04f, 1f)
                 : new Vector4(0.11f, 0.11f, 0.13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,
            worn ? new Vector4(1f, 0.85f, 0.25f, 1f)
                 : new Vector4(0.28f, 0.28f, 0.33f, 1f));

        ImGui.BeginChild($"##card_{item.Id}", new Vector2(CardWidth, CardHeight),
            true, ImGuiWindowFlags.NoScrollbar);

        DrawItemImage(item);

        // Name + worn star / detected indicator
        var dispName = item.Name.Length > 20 ? item.Name[..18] + "…" : item.Name;
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

        ImGui.SameLine();
        if (ImGui.Button("Edit", new Vector2(btnW - editGap, 0)))
        {
            _imageCache.Remove(item.Id);
            _panel.OpenEdit(item);
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.Button("X", new Vector2(deleteW, 0)))
            pendingDelete = item.Id;
        ImGui.PopStyleColor(2);

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

        if (entry.Texture?.GetWrapOrDefault() is { } wrap)
        {
            ImageDraw.Square(wrap, ThumbSize);
            AcceptImageDrop(item);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.07f, 0.07f, 0.09f, 1f));
        ImGui.Button(item.Slot.DisplayName(), size);
        ImGui.PopStyleColor();
        AcceptImageDrop(item);
    }

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

        if (_session.State == SessionState.WaitingForShot)
        {
            if (ImGui.Button("Skip")) _session.Skip();
            ImGui.SameLine();
        }

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.Button("End Session")) _session.Stop();
        ImGui.PopStyleColor(2);
        UiLayout.PopWrap();
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

            ImGui.Spacing();
            switch (_session.State)
            {
                case SessionState.WaitingForShot:
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Waiting for screenshot…");
                    ImGui.TextDisabled("Position your character, then press your screenshot key.");
                    ImGui.Spacing();
                    DrawCameraPresetControls(item);
                    ImGui.Spacing();
                    if (ImGui.Button("Skip"))
                        _session.Skip();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Skip this item and move on to the next without taking a screenshot.");
                    ImGui.SameLine();
                    break;
                case SessionState.Processing:
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Processing image…");
                    ImGui.Spacing();
                    break;
            }

            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.Button("End Session")) _session.Stop();
            ImGui.PopStyleColor(2);
        }

        UiLayout.PopWrap();
        ImGui.End();

        if (!open) _session.Stop();
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
    private void DrawCameraPresetControls(WardrobeItem? item)
    {
        if (item == null) return;

        var slotKey = item.Slot.ToString();
        var presets = _config.PresetsFor(slotKey);

        ImGui.TextDisabled($"Camera presets — {item.Slot.DisplayName()}");

        if (presets.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.TextUnformatted("None saved");
            ImGui.PopStyleColor();
        }

        if (presets.Count > 1)
            ImGui.TextDisabled("The ticked one loads during screenshot sessions.");

        // Deferred so the list is not mutated while it is being walked
        var applyIdx   = -1;
        var updateIdx  = -1;
        var deleteIdx  = -1;
        var defaultIdx = -1;

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
                ImGui.SetTooltip("Load this one automatically during screenshot sessions.");

            ImGui.SameLine();

            var label = string.IsNullOrWhiteSpace(preset.Name) ? "(unnamed)" : preset.Name;
            if (ImGui.SmallButton(label)) applyIdx = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Snap the camera to this preset now.\n" +
                                 "Camera control returns to you after about half a second.\n\n" +
                                 "Right-click to rename.");

            if (ImGui.BeginPopupContextItem("##presetctx"))
            {
                if (ImGui.MenuItem("Rename"))
                {
                    _renamePresetSlot = slotKey;
                    _renamePresetIdx  = i;
                    _renamePresetBuf  = preset.Name;
                }

                ImGui.Separator();
                if (ImGui.MenuItem("Delete")) deleteIdx = i;
                ImGui.EndPopup();
            }

            // A button rather than only a menu item: re-aiming a preset is the thing you do most
            // after making one, and it is worth nothing if it is hidden behind a right-click
            UiLayout.SameLineIfRoomForButton("Update");
            if (ImGui.SmallButton("Update")) updateIdx = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Replace '{label}' with the camera as it is now.\n" +
                                 "Keeps the name.");

            UiLayout.SameLineIfRoomForButton("×");
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.SmallButton("×")) deleteIdx = i;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Delete '{label}'.");

            ImGui.PopID();
        }

        if (applyIdx >= 0) Plugin.Camera.Apply(presets[applyIdx]);
        if (updateIdx >= 0) OverwritePreset(slotKey, updateIdx);
        if (defaultIdx >= 0) SetPresetDefault(slotKey, defaultIdx);
        if (deleteIdx >= 0) DeletePreset(slotKey, deleteIdx);

        // Saving a new one. The name is optional — an unnamed save is numbered, so the camera can be
        // caught quickly in GPose without stopping to think of a word for it.
        ImGui.SetNextItemWidth(-UiLayout.ButtonWidth("Save Camera") - ImGui.GetStyle().ItemSpacing.X);
        var entered = ImGui.InputTextWithHint("##newpreset", "preset name (optional)",
            ref _newPresetName, 48, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (ImGui.SmallButton("Save Camera") || entered) SavePresetFromCamera(slotKey);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Save the current GPose camera as a new preset\n" +
                             $"for all {item.Slot.DisplayName()} items.");
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
        var captured = Plugin.Camera.Capture();
        if (captured == null)
        {
            _log.Warning("[Wardrobe] Save preset: no camera to capture.");
            return;
        }

        if (!_config.SlotCameraPresetLists.TryGetValue(slotKey, out var list))
            _config.SlotCameraPresetLists[slotKey] = list = new List<CameraPreset>();

        var name = _newPresetName.Trim();
        captured.Name = name.Length > 0 ? name : $"Preset {list.Count + 1}";
        list.Add(captured);

        _config.Save();
        _config.SavePresets();
        _log.Information($"[Wardrobe] Saved camera preset '{captured.Name}' for {slotKey}");
        _newPresetName = string.Empty;
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

        UiLayout.SameLineIfRoom(UiLayout.CheckboxWidth("Large cards"));
        var largeCards = _config.LargeOutfitCards;
        if (ImGui.Checkbox("Large cards", ref largeCards))
        {
            _config.LargeOutfitCards = largeCards;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Outfit previews are usually full-body shots.\n" +
                             "Turn this off to match the item grid's card size.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (_config.Outfits.Count == 0)
        {
            ImGui.TextDisabled("No outfits saved yet. Wear a look, name it above, then save.");
            return;
        }

        var outfits = _config.Outfits
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cardW = _config.LargeOutfitCards ? OutfitCardWidth  : CardWidth;
        var cardH = _config.LargeOutfitCards ? OutfitCardHeight : CardHeight;

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

    private void DrawOutfitCard(Outfit outfit, float cardW, float cardH, ref Outfit? pendingDelete)
    {
        var items  = _wardrobe.ResolveOutfit(outfit);
        var worn   = _wardrobe.IsOutfitWorn(outfit);
        var partly = _wardrobe.IsOutfitPartlyWorn(outfit);

        ImGui.PushID(outfit.Id.ToString());
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            worn ? new Vector4(0.22f, 0.18f, 0.04f, 1f)
                 : new Vector4(0.11f, 0.11f, 0.13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,
            worn ? new Vector4(1f, 0.85f, 0.25f, 1f)
                 : new Vector4(0.28f, 0.28f, 0.33f, 1f));

        ImGui.BeginChild($"##outfit_{outfit.Id}", new Vector2(cardW, cardH),
            true, ImGuiWindowFlags.NoScrollbar);

        DrawOutfitImage(outfit, cardW - CardPad * 2);

        var dispName = outfit.Name.Length > 20 ? outfit.Name[..18] + "…" : outfit.Name;
        ImGui.TextUnformatted(dispName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(items.Count > 0
                ? $"{outfit.Name}\n\n" + string.Join("\n", items.Select(i => $"{i.Slot.DisplayName()} — {i.Name}"))
                : outfit.Name);

        if (worn)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));
            ImGui.TextUnformatted("★");
            ImGui.PopStyleColor();
        }

        // A deleted item leaves a gap in the outfit; say so rather than quietly wearing fewer
        var missing = outfit.ItemIds.Count - items.Count;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.75f, 0.95f, 1f));
        ImGui.TextUnformatted(missing > 0
            ? $"{items.Count} items · {missing} missing"
            : $"{items.Count} items");
        ImGui.PopStyleColor();

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
                ImGui.SetTooltip("Wear these items, leaving anything else you have on in place.");
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

        if (ImGui.SmallButton("Edit"))
            OpenOutfitEdit(outfit);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rename it, set a preview, take a photo, and add or remove items.");
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

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.SmallButton("X"))
            pendingDelete = outfit;
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Deletes the outfit only. The items themselves are kept.");

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
                    ImageDraw.Square(wrap, previewW);
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

        var items = _wardrobe.ResolveOutfit(outfit);

        ImGui.Spacing();
        if (_session.FoldersReady && items.Count > 0)
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
                                 "then crops it to 1:1 and assigns it as the outfit's image.");
        }
        else if (items.Count == 0)
        {
            ImGui.TextDisabled("Add items before taking a screenshot.");
        }
        else
        {
            ImGui.TextDisabled("Set the images and screenshots folders to enable screenshots.");
        }

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
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Name);

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

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove from outfit"))
                removeId = item.Id;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Takes this item out of the outfit.\nThe item itself is kept.");

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawAddToOutfit(outfit);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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
    }

    private void OpenOutfitEdit(Outfit outfit)
    {
        _editingOutfit     = outfit;
        _editOutfitName    = outfit.Name;
        _editOutfitImage   = outfit.ImagePath ?? string.Empty;
        _addToOutfitSearch = string.Empty;
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

        if (entry.Texture?.GetWrapOrDefault() is { } wrap)
        {
            ImageDraw.Square(wrap, thumbSize);
            AcceptOutfitImageDrop(outfit);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.07f, 0.07f, 0.09f, 1f));
        ImGui.Button("Outfit", size);
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

        ImGui.SameLine();
        if (ImGui.Button("Refresh##designs"))
            _settingsDesigns = Plugin.Glamourer.GetDesigns();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-read the design list from Glamourer.");

        if (_settingsDesigns.Count == 0)
            ImGui.TextDisabled("No designs found — create one in Glamourer first.");
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
            ImGui.SameLine();
            if (ImGui.Button(" Back Up Now "))
                _backup.Run();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Write a backup immediately.\nStill skipped if nothing has changed since the last one.");

            ImGui.SameLine();
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
        DrawWearingSettings();
        SettingsBreak();
        DrawVariantSettings();
        SettingsBreak();
        DrawSlotIconSettings();
        SettingsBreak();
        DrawImageFolderSettings();
        SettingsBreak();
        DrawScreenshotSettings();
        SettingsBreak();
        DrawBackupSettings();
        SettingsBreak();
        DrawSetupSettings();
    }

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

        ImGui.SameLine();
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
            ImGui.SameLine();
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
            ImGui.SameLine();
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
        ImGui.TextUnformatted("During a session");
        ImGui.Spacing();

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

        // Not a setting, but a visible change to the character while a session runs
        ImGui.Spacing();
        ImGui.TextDisabled("Your weapon is hidden in Glamourer for each shot regardless of the " +
                           "above, unless the item being photographed is the weapon. It is put " +
                           "back as you had it when the session ends.");

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
            ImGui.SameLine();
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
        ImGui.TextDisabled("A folder of images named after their slots — Head.png, Body.png, " +
                           "RingRight.png. Any slot you supply uses your image; the rest stay on " +
                           "the set chosen above.");
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

        if (string.IsNullOrEmpty(folder)) return;

        // Files are added to the folder outside the game, so there has to be a way to pick them up
        // without changing the setting or restarting
        ImGui.SameLine();
        if (ImGui.Button(" Rescan ")) Plugin.SlotIcons.Rescan();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-reads the folder. Use after adding or renaming a file.");

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.Button(" Clear##customIcons "))
        {
            _config.CustomIconFolder = string.Empty;
            _config.Save();
            Plugin.SlotIcons.Rescan();
        }
        ImGui.PopStyleColor(2);

        DrawCustomIconCoverage();
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

        ImGui.TextDisabled("Either name works — the slot's own name, or its label without spaces.");
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
