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

    // Free-text search: empty = show all. Deliberately not persisted.
    private string _search = string.Empty;

    // Favourites-only filter
    private bool _favoritesOnly;

    // Items the grid drew last frame, for the toolbar count. The toolbar draws before the grid,
    // so this trails by one frame — imperceptible, and avoids running the filters twice.
    private int _visibleCount;

    // Set when the user expands out of compact mode mid-session; cleared when the session ends,
    // so it overrides the setting for this session only rather than turning it off permanently.
    private bool _compactOverride;
    private bool _wasCompact;

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

    // Settings panel feedback
    private string _cameraLoadStatus  = string.Empty;
    private string _newAllowlistEntry = string.Empty;

    // Collections for the default-collection picker; loaded lazily so we don't hit IPC every frame
    private IList<string>? _settingsCollections;

    // Glamourer designs for the revert-design picker, loaded lazily for the same reason
    private IList<(Guid Id, string Name)>? _settingsDesigns;

    // First-run setup
    private int _onboardStep;
    private const int OnboardLastStep = 4;

    // Dependency probes, cached so the welcome step is not an IPC call every frame
    private (bool Available, string Version)? _penumbraCheck;
    private (bool Available, string Version)? _glamourerCheck;

    // Image browser state
    private string[] _browserImages      = Array.Empty<string>();
    private string   _lastBrowserFolder  = string.Empty;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp" };

    private const float CardWidth   = 180f;
    private const float CardPad     = 10f;
    private const float ThumbSize   = CardWidth - CardPad * 2; // 160 — square thumbnail
    private const float CardHeight  = 280f;

    // Outfit previews are full-body shots rather than close-ups, so they get more room by default
    private const float OutfitCardWidth  = 280f;
    private const float OutfitCardHeight = 400f;

    public PluginUi(Configuration config, WardrobeService wardrobe,
        ITextureProvider textures, IPluginLog log, ItemImportPanel panel,
        ScreenshotSessionService session, BackupService backup)
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


        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(1600, 1100),
        };
    }

    /// <summary>True while the main window should render as a compact session view.</summary>
    private bool CompactActive =>
        _config.CompactDuringSession && !_compactOverride && _session.State != SessionState.Idle;

    public override void PreDraw()
    {
        // Forget any manual expand once the session is over
        if (_session.State == SessionState.Idle) _compactOverride = false;

        var compact = CompactActive;
        if (compact != _wasCompact)
        {
            // Resize only on the transition, so the window stays user-resizable either side of it
            Size          = compact ? new Vector2(360, 200) : new Vector2(1000, 700);
            SizeCondition = ImGuiCond.Always;
            _wasCompact   = compact;
        }
        else
        {
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
                MaximumSize = new Vector2(1600, 1100),
            };
    }

    public override void Draw()
    {
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

        var totalW  = ImGui.GetContentRegionAvail().X;
        var totalH  = ImGui.GetContentRegionAvail().Y;
        var rightOpen = _panel.IsOpen || _showImageBrowser || _showSettings || _showTags
                        || _editingOutfit != null;
        var panelW    = rightOpen ? 360f : 0f;
        var gridW     = totalW - panelW - (rightOpen ? 8f : 0f);

        // ── Left: sticky header + scrolling grid only ─────────────────────────
        ImGui.BeginChild("##leftOuter", new Vector2(gridW, totalH), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        DrawToolbar();
        ImGui.Separator();
        DrawSlotFilter();
        ImGui.Separator();

        ImGui.BeginChild("##wardrobeGrid", new Vector2(-1, ImGui.GetContentRegionAvail().Y));
        if (_outfitsView) DrawOutfitsGrid();
        else              DrawGrid();
        ImGui.EndChild();

        ImGui.EndChild();

        // ── Right: tags + optional panel ──────────────────────────────────────
        if (rightOpen)
        {
            ImGui.SameLine();
            ImGui.BeginChild("##rightPanel", new Vector2(panelW, totalH), true,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);

            if (_panel.IsOpen)
                _panel.Draw();
            else if (_editingOutfit != null)
                DrawOutfitEditPanel();
            else if (_showTags)
                DrawTagFilter();
            else if (_showImageBrowser)
                DrawImageBrowser();
            else if (_showSettings)
                DrawSettings();

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
        ImGui.TextUnformatted("Welcome to Wardrobe");
        ImGui.SameLine();
        ImGui.TextDisabled($"·  step {_onboardStep + 1} of {OnboardLastStep + 1}");
        ImGui.Separator();
        ImGui.Spacing();

        var bodyH = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - 8f;
        if (ImGui.BeginChild("##onboardBody", new Vector2(-1, bodyH)))
        {
            switch (_onboardStep)
            {
                case 0: DrawOnboardIntro();       break;
                case 1: DrawOnboardCollection();  break;
                case 2: DrawOnboardImages();      break;
                case 3: DrawOnboardScreenshots(); break;
                case 4: DrawOnboardBackups();     break;
            }
        }
        ImGui.EndChild();

        ImGui.Separator();

        if (_onboardStep > 0)
        {
            if (ImGui.Button("Back", new Vector2(90, 0))) _onboardStep--;
            ImGui.SameLine();
        }

        if (_onboardStep < OnboardLastStep)
        {
            if (ImGui.Button("Next", new Vector2(90, 0))) _onboardStep++;
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.38f, 0.13f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.55f, 0.18f, 1f));
            if (ImGui.Button("Finish", new Vector2(90, 0)))
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
        ImGui.TextWrapped("This short setup covers the four settings that cause the most confusion " +
                          "when they are left unset. It takes about a minute, and nothing here is " +
                          "permanent — it can all be changed later in Settings.");

        // Re-running this is a normal thing to do, so say plainly that it cannot cost anything.
        // The wizard only ever writes those settings; it never touches items.
        if (_config.WardrobeItems.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f),
                $"Your {_config.WardrobeItems.Count} wardrobe item(s) are not affected.");
            ImGui.TextDisabled("Each step below already shows your current setting. Setup only");
            ImGui.TextDisabled("changes what you change, and never touches items or images.");
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
                          "if it is enabled in the collection that character actually uses. Enable " +
                          "it anywhere else and Penumbra reports success, the log looks clean, and " +
                          "nothing appears.");
        ImGui.Spacing();
        ImGui.TextDisabled("In Penumbra, see Collections → Your Character. Note that an");
        ImGui.TextDisabled("Individual Assignment for that character overrules it.");
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

        ImGui.SetNextItemWidth(300);
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

    private void DrawOnboardImages()
    {
        ImGui.TextUnformatted("Where should item images come from?");
        ImGui.Spacing();
        ImGui.TextWrapped("Pick a folder of images and they appear in the Image Browser, ready to " +
                          "drag onto item cards as previews. Optional — items work fine without one.");
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
    }

    private void DrawOnboardScreenshots()
    {
        ImGui.TextUnformatted("Where does FFXIV save your screenshots?");
        ImGui.Spacing();
        ImGui.TextWrapped("Wardrobe watches this folder during a screenshot session: it wears each " +
                          "item without a preview, waits for you to take a shot, then crops it and " +
                          "assigns it automatically.");
        ImGui.Spacing();

        if (string.IsNullOrEmpty(_config.ScreenshotsFolder))
            ImGui.TextDisabled("No folder configured.");
        else
            ImGui.TextUnformatted(_config.ScreenshotsFolder);

        var defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "FINAL FANTASY XIV - A Realm Reborn", "screenshots");

        ImGui.Spacing();
        if (Directory.Exists(defaultFolder) && _config.ScreenshotsFolder != defaultFolder)
        {
            if (ImGui.Button(" Use the usual location "))
            {
                _config.ScreenshotsFolder = defaultFolder;
                _config.Save();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(defaultFolder);
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

        ImGui.SameLine();
        ToggleButton("  Images  ", ref _showImageBrowser, onActivate: () =>
        {
            _showSettings = false;
            _showTags     = false;
            RefreshBrowserImages();
        });

        ImGui.SameLine();
        ToggleButton(" Settings ", ref _showSettings, onActivate: () =>
        {
            _showImageBrowser = false;
            _showTags         = false;
        });

        ImGui.SameLine();
        ToggleButton(" Tags ", ref _showTags, onActivate: () =>
        {
            _showImageBrowser = false;
            _showSettings     = false;
        });


        if (_session.CanStart)
        {
            ImGui.SameLine();
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
            ImGui.SameLine();
        }

        // Strip: force Emperor's New on every slot regardless of tracking
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.35f, 0.06f, 0.06f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.10f, 0.10f, 1f));
        if (ImGui.Button(" Strip "))
        {
            _wardrobe.StripAll();
            _detectedWorn.Clear();
            _scanStatus = string.Empty;
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force every equipment slot to Emperor's New in Glamourer\nand disable all worn mods.");

        ImGui.SameLine();

        // Refresh: Penumbra redraw local player
        if (ImGui.Button(" Refresh "))
            Plugin.Penumbra.RedrawPlayer();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tell Penumbra to redraw the local player character.");

        ImGui.SameLine();

        // Scan: detect worn state from enabled Penumbra mods
        if (ImGui.Button(" Scan "))
        {
            _detectedWorn.Clear();
            var added = _wardrobe.ScanAndSyncWorn();
            foreach (var id in added) _detectedWorn.Add(id);

            // Also mark items already in WornItems as detected
            foreach (var id in _config.WornItems.Values)
                _detectedWorn.Add(id);

            _scanStatus = added.Count > 0
                ? $"Detected {added.Count} new item(s) as worn."
                : _detectedWorn.Count > 0
                    ? "Wardrobe already in sync."
                    : "No wardrobe items detected as worn.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan Penumbra for enabled mods and mark matching\nwardrobe items as worn.");

        if (!string.IsNullOrEmpty(_scanStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_scanStatus);
        }
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
        ImGui.TextDisabled(text);

        if (_visibleCount != total && ImGui.IsItemHovered())
            ImGui.SetTooltip("Filtered by the current search, slot, tag or favourites selection.");
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

    private void DrawSlotFilter()
    {
        ImGui.Spacing();

        // Outfits is a view of its own rather than a slot filter, so it sits first and apart
        var outfitsActive = _outfitsView;
        if (outfitsActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
        }
        if (ImGui.Button("Outfits")) _outfitsView = !_outfitsView;
        var outfitsHovered = ImGui.IsItemHovered();
        if (outfitsActive) ImGui.PopStyleColor(2);
        if (outfitsHovered)
            ImGui.SetTooltip(outfitsActive
                ? "Showing saved outfits. Click to go back to items."
                : "Show saved outfits instead of items.");

        ImGui.SameLine();
        DrawFilterButton("All", null);

        ImGui.SameLine();

        // Capture the state up front: the button toggles _favoritesOnly, so guarding the pop on
        // the live value pops a style that was never pushed (or leaks one that was), corrupting
        // ImGui's stack and crashing inside cimgui on a later frame.
        var favActive = _favoritesOnly;
        if (favActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.15f, 0.28f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.72f, 0.2f, 0.36f, 1f));
        }
        if (ImGui.Button("♥")) _favoritesOnly = !_favoritesOnly;
        var favHovered = ImGui.IsItemHovered();
        if (favActive) ImGui.PopStyleColor(2);

        if (favHovered)
            ImGui.SetTooltip(favActive ? "Showing favourites only." : "Show favourites only.");

        foreach (var slot in EquipSlotEx.All)
        {
            ImGui.SameLine();
            DrawFilterButton(slot.DisplayName(), slot);
        }

        DrawSearchAndSort();
        ImGui.Spacing();
    }

    /// <summary>
    /// Search box and sort combo, right-aligned on the slot filter row. Falls back to its own
    /// line when the slot buttons leave too little room, rather than overlapping them.
    /// </summary>
    private void DrawSearchAndSort()
    {
        const float searchW = 200f;
        const float sortW   = 150f;

        var style = ImGui.GetStyle();

        // Right edge of the last slot button, in window-local coordinates
        var lastBtnRight = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;

        var clearW = string.IsNullOrEmpty(_search)
            ? 0f
            : ImGui.CalcTextSize("×").X + style.FramePadding.X * 2 + style.ItemSpacing.X;

        var contentRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var blockW       = searchW + clearW + style.ItemSpacing.X + sortW;
        var startX       = contentRight - blockW;

        // Only share the row if the controls clear the last slot button
        if (startX > lastBtnRight + style.ItemSpacing.X)
            ImGui.SameLine(startX);

        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##search", "Search name, tag, mod…", ref _search, 128);

        if (!string.IsNullOrEmpty(_search))
        {
            ImGui.SameLine();
            if (ImGui.Button("×##clearsearch")) _search = string.Empty;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear search.");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(sortW);

        // Order must match the ItemSortMode enum values
        var sortLabels = new[] { "Name (A–Z)", "Name (Z–A)", "Newest first", "Oldest first" };
        var sortIdx    = (int)_config.SortMode;
        if (ImGui.Combo("##sort", ref sortIdx, sortLabels, sortLabels.Length))
        {
            _config.SortMode = (ItemSortMode)sortIdx;
            _config.Save();
        }
    }

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
            clicked = ImGui.Button(label);
            hovered = ImGui.IsItemHovered();
        }

        if (clicked) _slotFilter = slot;
        if (active) ImGui.PopStyleColor(2);

        // The name is no longer on screen once icons are on, so the tooltip carries it
        if (useIcon && hovered)
            ImGui.SetTooltip(label);
    }

    /// <summary>
    /// Button whose face is a slot icon rather than text. Hover state is captured from the button
    /// itself, since the icon drawn over it becomes the "last item" afterwards.
    /// </summary>
    private static bool DrawIconButton(EquipSlot slot, out bool hovered)
    {
        var style = ImGui.GetStyle();
        var size  = SlotIconService.ScaledSize;
        var btn   = new Vector2(size + style.FramePadding.X * 2, size + style.FramePadding.Y * 2);

        ImGui.PushID((int)slot);

        bool clicked;
        if (Plugin.SlotIcons.TryGetGameIcon(slot, out var handle))
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
            clicked = ImGui.Button(slot.DisplayName());
        }

        hovered = ImGui.IsItemHovered();
        ImGui.PopID();
        return clicked;
    }

    // ── Tag filter tree ───────────────────────────────────────────────────────

    private sealed class TagNode
    {
        public string Segment  = string.Empty;
        public string FullPath = string.Empty;
        public SortedDictionary<string, TagNode> Children = new(StringComparer.OrdinalIgnoreCase);
    }

    private TagNode BuildTagTree()
    {
        var root = new TagNode();
        foreach (var item in _config.WardrobeItems)
        foreach (var tag in item.Tags)
        {
            var parts = tag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var node  = root;
            var path  = string.Empty;
            foreach (var part in parts)
            {
                path = path.Length == 0 ? part : $"{path}/{part}";
                if (!node.Children.TryGetValue(part, out var child))
                    node.Children[part] = child = new TagNode { Segment = part, FullPath = path };
                node = child;
            }
        }
        return root;
    }

    private void DrawTagTree(TagNode node)
    {
        foreach (var (_, child) in node.Children)
        {
            var active = _tagFilter.Contains(child.FullPath);
            if (active) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.78f, 0.58f, 1f, 1f));

            var isLeaf = child.Children.Count == 0;
            var flags  = ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isLeaf)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            else
                flags |= ImGuiTreeNodeFlags.OpenOnArrow; // expand/collapse only via the arrow

            var open = ImGui.TreeNodeEx($"##{child.FullPath}", flags, child.Segment);
            if (active) ImGui.PopStyleColor();

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
    }

    private void DrawTagFilter()
    {
        // Header first, so the panel stays closable even when there are no tags to show
        if (DrawPanelHeader("Tags"))
        {
            _showTags = false;
            return;
        }

        if (!_config.WardrobeItems.Any(i => i.Tags.Count > 0))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No tags yet. Add them when editing an item.");
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
            DrawTagTree(BuildTagTree());
            ImGui.Spacing();
        }
        ImGui.Spacing();
    }

    // ── Item grid ─────────────────────────────────────────────────────────────

    private void DrawGrid()
    {
        IEnumerable<WardrobeItem> query = _config.WardrobeItems;
        if (_favoritesOnly)
            query = query.Where(x => x.IsFavorite);
        if (_slotFilter != null)
            query = query.Where(x => x.Slot == _slotFilter);
        if (_tagFilter.Count > 0)
            query = query.Where(x => x.Tags.Any(t =>
                _tagFilter.Any(f =>
                    string.Equals(t, f, StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase))));

        if (!string.IsNullOrWhiteSpace(_search))
        {
            var q = _search.Trim();
            query = query.Where(x =>
                x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                x.Mods.Any(m => m.ModName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (x.GlamourerItemName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
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

        var items = query.ToList();
        _visibleCount = items.Count;

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

        if (badge.Length > 0) ImGui.TextUnformatted(badge);
        else                  ImGui.NewLine();
        ImGui.PopStyleColor();

        if (item.Tags.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("  ", item.Tags.Select(t => $"#{t}")));

        // Buttons
        var btnW = (CardWidth - CardPad * 2 - 6) / 2;

        // Customisation mods are not worn or removed — you always have hair. They are applied,
        // and "removing" one just turns the mod back off.
        var customization = item.Slot.IsCustomization();

        if (worn)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
            if (ImGui.Button(customization ? "Revert" : "Unequip", new Vector2(btnW, 0)))
                _wardrobe.UnwearItem(item);
            ImGui.PopStyleColor(2);
            if (customization && ImGui.IsItemHovered())
                ImGui.SetTooltip("Turn this mod back off, restoring the default appearance.");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.38f, 0.13f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.55f, 0.18f, 1f));
            if (ImGui.Button(customization ? "Apply" : "Wear", new Vector2(btnW, 0)))
                _wardrobe.WearItem(item);
            ImGui.PopStyleColor(2);
        }

        ImGui.SameLine();
        if (ImGui.Button("Edit", new Vector2((btnW - 26) / 1, 0)))
        {
            _imageCache.Remove(item.Id);
            _panel.OpenEdit(item);
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.Button("X", new Vector2(22, 0)))
            pendingDelete = item.Id;
        ImGui.PopStyleColor(2);

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopID();
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

        ImGui.TextDisabled(_config.ImagesFolder);
        ImGui.SameLine();
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
    }

    private void DrawSessionHud()
    {
        if (_session.State == SessionState.Idle) return;

        ImGui.SetNextWindowSize(new Vector2(340, 0), ImGuiCond.Always);
        ImGui.SetNextWindowPos(new Vector2(60, 60), ImGuiCond.Appearing);
        ImGui.SetNextWindowBgAlpha(0.92f);

        var open = true;
        ImGui.Begin("Screenshot Session###WardrobeSessionHud",
            ref open,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoBringToFrontOnFocus);

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
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Waiting for screenshot...");
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
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Processing image...");
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

        ImGui.End();

        if (!open) _session.Stop();
    }

    // ── Camera preset controls (used inside session HUD) ──────────────────────

    private void DrawCameraPresetControls(WardrobeItem? item)
    {
        if (item == null) return;

        var slotKey  = item.Slot.ToString();
        var hasPreset = _config.SlotCameraPresets.ContainsKey(slotKey);

        ImGui.TextDisabled($"Camera preset — {item.Slot.DisplayName()}");

        if (hasPreset)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 1f, 0.4f, 1f));
            ImGui.TextUnformatted("Saved");
            ImGui.PopStyleColor();

            ImGui.SameLine();
            if (ImGui.SmallButton("Apply"))
                Plugin.Camera.Apply(_config.SlotCameraPresets[slotKey]);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Snap the camera to the saved preset now.\nCamera control returns to you after about half a second.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Update"))
            {
                var captured = Plugin.Camera.Capture();
                if (captured != null)
                {
                    _config.SlotCameraPresets[slotKey] = captured;
                    _config.Save();
                    _config.SavePresets();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Overwrite the saved preset with the current camera position.");

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.SmallButton("Clear"))
            {
                _config.SlotCameraPresets.Remove(slotKey);
                _config.Save();
                _config.SavePresets();
            }
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove the camera preset for this slot.");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.TextUnformatted("None saved");
            ImGui.PopStyleColor();

            ImGui.SameLine();
            if (ImGui.SmallButton("Save Current Camera"))
            {
                var captured = Plugin.Camera.Capture();
                if (captured != null)
                {
                    _config.SlotCameraPresets[slotKey] = captured;
                    _config.Save();
                    _config.SavePresets();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Save the current GPose camera position as the preset\nfor all {item.Slot.DisplayName()} items.");
        }
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
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##outfitname", "Name this outfit…", ref _newOutfitName, 128);
        ImGui.SameLine();

        var canSave = wornCount > 0 && !string.IsNullOrWhiteSpace(_newOutfitName);
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button($"Save current look ({wornCount} item(s))"))
        {
            _wardrobe.SaveCurrentAsOutfit(_newOutfitName);
            _newOutfitName = string.Empty;
        }
        if (!canSave) ImGui.EndDisabled();

        if (wornCount == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Wear some items first.");
        }

        if (_session.CanStartOutfits)
        {
            ImGui.SameLine();
            if (ImGui.Button(" Screenshot Outfits "))
                _session.StartOutfits();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Wears each outfit that has no preview yet and waits for a\n" +
                                 "screenshot, exactly like an item session.");
        }

        ImGui.SameLine();
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
        var items = _wardrobe.ResolveOutfit(outfit);
        var worn  = _wardrobe.IsOutfitWorn(outfit);

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

        var btnW = (cardW - CardPad * 2 - 6) / 2;

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
        if (ImGui.Button("Only this", new Vector2(btnW, 0)))
            _wardrobe.WearOutfit(outfit, removeOthers: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Wear these items and remove everything else the wardrobe has on.");

        if (ImGui.SmallButton("Edit"))
            OpenOutfitEdit(outfit);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rename it, set a preview, take a photo, and add or remove items.");
        ImGui.SameLine();

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
        ImGui.Separator();

        var missing = outfit.ItemIds.Count - items.Count;

        ImGui.TextUnformatted($"Items  ({items.Count})");
        if (missing > 0)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                $"{missing} item(s) in this outfit no longer exist.");

        ImGui.Spacing();

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

            var customization = item.Slot.IsCustomization();
            var rowDye        = WardrobeService.GetDye(outfit, item.Id);

            if (worn)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.08f, 0.08f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
                if (ImGui.SmallButton(customization ? "Revert" : "Unequip"))
                    _wardrobe.UnwearItem(item);
                ImGui.PopStyleColor(2);
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.13f, 0.38f, 0.13f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.55f, 0.18f, 1f));
                if (ImGui.SmallButton(customization ? "Apply" : "Equip"))
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

            if (item.Slot.IsCustomization())
            {
                ImGui.TextDisabled("    Customisation mods cannot be dyed.");
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
    /// Dye picker for one channel, showing a colour swatch beside the dye name.
    /// </summary>
    /// <remarks>
    /// Colours come from the game's Stain sheet, so the swatch matches what the dye actually looks
    /// like rather than a guess. Returns true when a different dye was chosen.
    /// </remarks>
    private bool DrawDyePicker(string id, string label, byte current, float width, out byte picked)
    {
        picked = current;

        var stains = Plugin.ItemLookup.GetStains();
        var match  = stains.FirstOrDefault(s => s.Id == current);
        var name   = string.IsNullOrEmpty(match.Name) ? "Undyed" : match.Name;

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
        ImGui.TextDisabled("Revert customisation mods to");
        ImGui.TextDisabled("A Glamourer design holding your character's normal look. Only its");
        ImGui.TextDisabled("customisations are used, so its equipment is ignored entirely.");
        ImGui.Spacing();

        _settingsDesigns ??= Plugin.Glamourer.GetDesigns();

        var current = _config.RevertDesignId.HasValue
            ? (string.IsNullOrEmpty(_config.RevertDesignName) ? "(unnamed design)" : _config.RevertDesignName)
            : "(none — put back the previous hairstyle)";

        ImGui.SetNextItemWidth(260);
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
        ImGui.TextDisabled("Copies your wardrobe config to a folder once an hour,");
        ImGui.TextDisabled("but only when something has actually changed.");
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
        ImGui.SetNextItemWidth(120);
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

    private void DrawSettings()
    {
        if (DrawPanelHeader("Settings"))
        {
            _showSettings = false;
            return;
        }
        ImGui.Spacing();

        ImGui.TextUnformatted("Default Collection");
        ImGui.TextDisabled("Pre-selected when importing an item, for both the main");
        ImGui.TextDisabled("and supplementary mods. Set this to the collection your");
        ImGui.TextDisabled("character actually uses — mods enabled in a collection");
        ImGui.TextDisabled("the character isn't using have no visible effect.");
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

        ImGui.SetNextItemWidth(260);
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
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "This must match your character's collection.");
        ImGui.TextDisabled("In Penumbra, check Collections → Your Character to see which");
        ImGui.TextDisabled("collection applies (and whether an Individual Assignment");
        ImGui.TextDisabled("overrules it). A mod enabled in any other collection will");
        ImGui.TextDisabled("enable without error and simply never appear.");

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

        ImGui.Spacing();
        ImGui.Separator();
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBackupSettings();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Images Folder");
        ImGui.TextDisabled("Images from this folder appear in the Image Browser");
        ImGui.TextDisabled("and can be dragged onto wardrobe item cards.");
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("FFXIV Screenshots Folder");
        ImGui.TextDisabled("The folder FFXIV saves screenshots to.");
        ImGui.TextDisabled("Used by Screenshot Session to detect new screenshots.");
        ImGui.Spacing();

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

        ImGui.Spacing();

        var defaultSsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "FINAL FANTASY XIV - A Realm Reborn", "screenshots");

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
                ImGui.SetTooltip(defaultSsFolder);
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Camera Presets File");
        ImGui.TextDisabled("JSON file for saving and loading per-slot camera presets.");
        ImGui.TextDisabled("Presets are written here automatically whenever they change.");
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
                    _config.CameraPresetsPath = path;
                    _config.Save();
                    _config.SavePresets();
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Experimental");
        ImGui.TextDisabled("Unfinished features. Expect them to be incomplete or to change.");
        ImGui.Spacing();

        var experimental = _config.ExperimentalFeatures;
        if (ImGui.Checkbox("Enable experimental features", ref experimental))
        {
            _config.ExperimentalFeatures = experimental;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reveals Wardrobe Sharing and slot icons.\n\n" +
                             "The sharing backend does not exist yet, so nothing will connect.");

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
            ImGui.SetTooltip("Walks through the collection, folders and backups again.\n\n" +
                             "Every step starts from your current setting, and only what you\n" +
                             "change is changed. Wardrobe items and images are never touched.");

        if (!_config.ExperimentalFeatures) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSlotIconSettings();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawShareSettings();
    }

    private void DrawSlotIconSettings()
    {
        ImGui.TextUnformatted("Slot Icons");
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "Experimental");
        ImGui.TextDisabled("Show an icon instead of the slot name on cards and filters.");
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
        ImGui.SetNextItemWidth(220);

        // Order must match the SlotIconStyle enum values
        var styleLabels = new[] { "FFXIV game icons", "Font Awesome" };
        var styleIdx    = (int)_config.SlotIconStyle;
        if (ImGui.Combo("##sloticonstyle", ref styleIdx, styleLabels, styleLabels.Length))
        {
            _config.SlotIconStyle = (SlotIconStyle)styleIdx;
            _config.Save();
        }

        if (_config.SlotIconStyle == SlotIconStyle.GameIcons)
            ImGui.TextDisabled("Hair uses a character-creation icon. Face, tail, ears and\n" +
                               "skin have no game artwork, so they use Font Awesome.");

        // Live preview — also the quickest way to spot a wrong game-icon ID
        ImGui.Spacing();
        ImGui.TextDisabled("Preview");
        foreach (var slot in EquipSlotEx.All)
        {
            if (!Plugin.SlotIcons.Draw(slot)) continue;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot.DisplayName());
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    private void DrawShareSettings()
    {
        ImGui.TextUnformatted("Wardrobe Sharing");
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "Experimental");
        ImGui.TextDisabled("Lets another player view your wardrobe and dress you remotely.");
        ImGui.TextDisabled("Requires a backend server — not yet available.");
        ImGui.Spacing();

        // ── Connection ────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Server URL");
        ImGui.SetNextItemWidth(-1);
        var url = _config.ShareServerUrl;
        if (ImGui.InputText("##shareUrl", ref url, 256))
        {
            _config.ShareServerUrl = url;
            _config.Save();
        }

        ImGui.Spacing();

        var state = Plugin.Share.State;
        switch (state)
        {
            case ShareConnectionState.Disconnected:
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "● Disconnected");
                ImGui.SameLine();
                var canConnect = !string.IsNullOrWhiteSpace(_config.ShareServerUrl);
                if (!canConnect) ImGui.BeginDisabled();
                if (ImGui.Button("Connect")) Plugin.Share.Connect();
                if (!canConnect)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Enter a server URL first.");
                }
                break;

            case ShareConnectionState.Connecting:
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "● Connecting…");
                break;

            case ShareConnectionState.Connected:
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "● Connected");
                ImGui.SameLine();
                if (ImGui.Button("Disconnect")) Plugin.Share.Disconnect();

                ImGui.Spacing();
                ImGui.TextUnformatted("Share Code");
                var code = Plugin.Share.ShareCode ?? "—";
                ImGui.SetNextItemWidth(160);
                ImGui.InputText("##shareCode", ref code, 64, ImGuiInputTextFlags.ReadOnly);
                ImGui.SameLine();
                if (ImGui.Button("Copy Link"))
                    ImGui.SetClipboardText($"{_config.ShareServerUrl}/wardrobe/{Plugin.Share.ShareCode}");
                break;
        }

        // ── Allowed viewers ───────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Allowed Viewers");
        ImGui.TextDisabled("Only players on this list can send wear/unequip commands.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
        ImGui.InputText("##newViewer", ref _newAllowlistEntry, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_newAllowlistEntry))
        {
            var entry = _newAllowlistEntry.Trim();
            if (!_config.ShareAllowlist.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                _config.ShareAllowlist.Add(entry);
                _config.Save();
            }
            _newAllowlistEntry = string.Empty;
        }

        ImGui.Spacing();
        string? toRemove = null;
        foreach (var viewer in _config.ShareAllowlist)
        {
            ImGui.TextUnformatted($"  {viewer}");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.1f, 0.1f, 1f));
            if (ImGui.SmallButton($"Remove##{viewer}")) toRemove = viewer;
            ImGui.PopStyleColor(2);
        }
        if (toRemove != null)
        {
            _config.ShareAllowlist.Remove(toRemove);
            _config.Save();
        }

        if (_config.ShareAllowlist.Count == 0)
            ImGui.TextDisabled("  No viewers added yet.");
    }

    public void Dispose()
    {
        _panel.Dispose();
    }
}
