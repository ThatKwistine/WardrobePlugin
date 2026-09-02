using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;
using WardrobePlugin.Services;
using WardrobePlugin.Ui;

namespace WardrobePlugin;

public sealed class Plugin : IDalamudPlugin
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService] public static ICommandManager  Commands     { get; private set; } = null!;
    [PluginService] public static IPluginLog       Log          { get; private set; } = null!;
    [PluginService] public static IClientState     ClientState  { get; private set; } = null!;
    [PluginService] public static IObjectTable     Objects      { get; private set; } = null!;
    [PluginService] public static ITextureProvider Textures     { get; private set; } = null!;
    [PluginService] public static IDataManager     DataManager  { get; private set; } = null!;
    [PluginService] public static IFramework       Framework    { get; private set; } = null!;

    public static PenumbraIpc         Penumbra     { get; private set; } = null!;
    public static GlamourerIpc        Glamourer    { get; private set; } = null!;
    public static CameraService       Camera       { get; private set; } = null!;
    public static SlotIconService     SlotIcons    { get; private set; } = null!;
    public static IconPackService     IconPacks    { get; private set; } = null!;
    public static ItemLookupService   ItemLookup   { get; private set; } = null!;
    public static EmoteLookupService  Emotes       { get; private set; } = null!;
    public static GlamourPlateService GlamourPlates { get; private set; } = null!;
    public static GameScreenshotService Shutter     { get; private set; } = null!;
    public static TextureCompressionFlagService TextureFlags { get; private set; } = null!;

    private readonly Configuration            _config;
    private readonly WardrobeService          _wardrobeService;
    private readonly ModAnalysisService       _analysisService;
    private readonly ItemLookupService        _itemLookup;
    private readonly ScreenshotSessionService _screenshotSession;
    private readonly LastWornService          _lastWorn;
    private readonly BackupService            _backupService;
    private readonly WardrobeProfileService   _profiles;
    private readonly WardrobeShareService     _shareService;
    private readonly HtmlExportService        _htmlExport;
    private readonly ItalicFontService        _italicFont;
    private readonly WindowSystem             _windowSystem;
    private readonly CropGuideOverlay          _cropGuide;
    private readonly PluginUi                _ui;
    private readonly MassImportPanel         _massImport;
    private readonly ChangelogWindow         _changelog;
    private readonly SettingsWindow          _settings;
    private readonly SharePanel              _share;
    private readonly LastWornWindow          _lastWornWindow;

    private const string CommandName  = "/wardrobe";
    private const string CommandAlias = "/wr";

    public Plugin(IDalamudPluginInterface pi)
    {
        PluginInterface = pi;
        pi.Inject(this);

        _config = pi.GetPluginConfig() as Configuration ?? new Configuration();

        // First of everything, ahead even of the presets: the items, outfits, bases and angles are
        // now read through whichever wardrobe is active, and reading one of those before the old
        // top-level data has been moved across would conjure an empty wardrobe and strand it.
        if (!_config.ProfilesMigrated) BackUpBeforeMigrating(pi);
        if (_config.MigrateProfiles()) _config.Save();

        // Before LoadPresets, which reads the path this moves onto the wardrobe
        if (_config.MigrateCameraPresetsPath()) _config.Save();

        _config.LoadPresets();
        if (_config.MigrateDateAdded()) _config.Save();
        if (_config.MigrateOnboarding()) _config.Save();
        if (_config.MigrateModOwnership()) _config.Save();

        // After MigrateDateAdded, which it uses to decide which member of a group is the original
        if (_config.MigrateVariantGroups()) _config.Save();

        if (_config.MigrateCameraPresets()) _config.Save();

        // After the outfits are loaded, which it judges a fresh install by
        if (_config.MigrateDesignTags()) _config.Save();

        // A repair rather than a migration, so it runs every load — see NormaliseBaseCharacters
        if (_config.NormaliseBaseCharacters()) _config.Save();

        // Glamourer state is not persisted across sessions, so nothing is worn on load
        if (_config.WornItems.Count > 0)
        {
            // Before the clear, which is what would otherwise throw the record away. Only reached on
            // the first load after the last-worn record arrived, or after a crash that took the game
            // down between a wear and the next capture — from then on LastWornService keeps a fuller
            // record than this, made while Glamourer was still there to be read.
            if (_config.LastWorn == null) _config.LastWorn = LastWornFromWornItems();

            _config.WornItems.Clear();
            _config.Save();
        }

        Penumbra  = new PenumbraIpc(pi, Log, _config);
        Glamourer = new GlamourerIpc(pi, Log, Objects);
        Camera    = new CameraService(Framework, Log);

        _analysisService   = new ModAnalysisService(Log);
        _itemLookup        = new ItemLookupService(DataManager);
        ItemLookup         = _itemLookup;

        // Builds its map on first use rather than here: only a wardrobe with animation items ever
        // asks it anything, and it is the panel that draws one that pays for it
        Emotes             = new EmoteLookupService(DataManager, Log);

        // After ItemLookup, which it resolves item names through when it logs its slot mapping
        GlamourPlates      = new GlamourPlateService(Log);

        Shutter            = new GameScreenshotService(Log);
        TextureFlags       = new TextureCompressionFlagService(Penumbra, Framework, Log);

        _wardrobeService   = new WardrobeService(Penumbra, Glamourer, _config, Log, Framework);
        _screenshotSession = new ScreenshotSessionService(_wardrobeService, _config, Framework, Log, Camera, Shutter);

        // After the session service, which it asks whether a shoot is running: a session dresses the
        // character one item at a time, and none of those is a look anybody chose
        _lastWorn          = new LastWornService(_config, _wardrobeService, _screenshotSession,
                                                 Objects, ClientState, Framework, Log);
        _profiles          = new WardrobeProfileService(_config, Objects, ClientState, Framework, Log);
        _backupService     = new BackupService(_config, Framework, Log);
        _shareService      = new WardrobeShareService(Penumbra, Log);

        // After ItemLookup and Emotes, which it reads dye and emote names through when it flattens
        // the wardrobe for the page
        _htmlExport        = new HtmlExportService(_config, _itemLookup, Emotes, Log);

        _windowSystem = new WindowSystem("WardrobePlugin");
        _italicFont = new ItalicFontService(Log);
        IconPacks   = new IconPackService(_config, Log);
        SlotIcons   = new SlotIconService(_config, Textures, _itemLookup, IconPacks, Log);

        var panel = new ItemImportPanel(_config, _wardrobeService, Penumbra, Glamourer, _analysisService, _itemLookup, _screenshotSession, Log, _italicFont);
        _massImport = new MassImportPanel(_config, Penumbra, _analysisService, _itemLookup, Log, _italicFont);
        _share = new SharePanel(_config, _wardrobeService, _shareService, Penumbra, Textures, Log);
        _ui = new PluginUi(_config, _wardrobeService, Textures, Log, panel, _screenshotSession, _backupService, _massImport, _share, _htmlExport, _lastWorn, _profiles);
        _changelog = new ChangelogWindow(_config);
        _ui.Changelog = _changelog;

        // Settings live in a window of their own rather than in the wardrobe's right-hand column.
        // The two know about each other: the View menu opens it, and it draws the wardrobe's own
        // settings body.
        _settings   = new SettingsWindow(_ui);
        _ui.Settings = _settings;

        // Opens itself when there is an offer outstanding — see LastWornWindow.PreOpenCheck — so
        // nothing here has to know when a character logged in
        _lastWornWindow = new LastWornWindow(_ui, _config, _lastWorn);

        _windowSystem.AddWindow(_ui);
        _windowSystem.AddWindow(_massImport);
        _windowSystem.AddWindow(_changelog);
        _windowSystem.AddWindow(_share);
        _windowSystem.AddWindow(_settings);
        _windowSystem.AddWindow(_lastWornWindow);

        ShowChangelogIfUpdated();

        // Drawn outside the window system: the guide has to be on screen while you frame a shot,
        // which is exactly when the wardrobe's own window is closed or pushed aside (#25).
        _cropGuide = new CropGuideOverlay(_config, _screenshotSession);

        pi.UiBuilder.DisableGposeUiHide = true;

        pi.UiBuilder.Draw        += _windowSystem.Draw;
        pi.UiBuilder.Draw        += _ui.DrawFileDialog;
        pi.UiBuilder.Draw        += _cropGuide.Draw;
        pi.UiBuilder.OpenMainUi   += OpenUi;

        // Dalamud's own settings button, on the plugin installer's entry for this plugin. It means
        // settings, so it opens settings rather than the wardrobe.
        pi.UiBuilder.OpenConfigUi += OpenSettings;

        Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Wardrobe window. /wardrobe restore puts back what you last wore."
        });

        Commands.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias of /wardrobe"
        });
    }

    /// <summary>
    /// A last-worn record built from the worn list a previous session left behind.
    /// </summary>
    /// <remarks>
    /// The thin version of what <see cref="LastWornService"/> writes, and only ever a fallback for
    /// the load that finds no record at all: the first one after this feature arrived, and the one
    /// after a crash that took the game down before the first capture. It holds the wardrobe items
    /// and nothing else — the dyes, the plain gear and the hat lived in Glamourer, which forgot them
    /// when the game closed, and there is nowhere left to read them from now.
    /// <para>
    /// No character is recorded, which is not an oversight: the worn list never said whose it was,
    /// and a record that named the wrong character would be refused for ever. An empty name matches
    /// anybody — see <see cref="WornSnapshot.Matches"/> — so the first login gets the offer and the
    /// capture that follows replaces this with the real thing.
    /// </para>
    /// </remarks>
    private WornSnapshot? LastWornFromWornItems()
    {
        var ids = _config.WornItems.Values.Distinct()
            .Where(id => _config.WardrobeItems.Any(i => i.Id == id))
            .ToList();

        if (ids.Count == 0) return null;

        Log.Information($"[Wardrobe] Remembering {ids.Count} item(s) from the last session, " +
                        $"from the worn list left behind.");

        return new WornSnapshot
        {
            Look = new Outfit { Name = "What you were wearing", ItemIds = ids },
        };
    }

    /// <summary>
    /// Shows what changed, the first time a new version runs.
    /// </summary>
    /// <remarks>
    /// A fresh install is marked as having seen the current version without being shown anything:
    /// someone installing for the first time is told what the plugin does by the setup that follows,
    /// and a list of what changed since a version they never ran is noise in front of it. Judged the
    /// same way <see cref="Configuration.MigrateOnboarding"/> judges it — an empty wardrobe that has
    /// not been set up is a new install, whatever version it happens to be.
    /// </remarks>
    private void ShowChangelogIfUpdated()
    {
        var fresh = !_config.OnboardingCompleted && _config.WardrobeItems.Count == 0
                    && string.IsNullOrEmpty(_config.LastSeenVersion);

        if (fresh)
        {
            _config.LastSeenVersion = Changelog.Current.ToString();
            _config.Save();
            return;
        }

        if (_changelog.OpenForUpdate())
            Log.Information($"[Wardrobe] Updated to {Changelog.Current} — showing what changed.");
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            OpenUi();
            return;
        }

        // Undocumented on purpose: a field finder, not a feature. It answers "which camera field does
        // this control actually move" with a memory diff, which is how the GPose FoV offset and the
        // pan pair were found — and what a report like issue #17 needs, since a screenshot of a
        // camera that went to the wrong place cannot say which field was wrong.
        if (trimmed.Equals("camdump", StringComparison.OrdinalIgnoreCase))
        {
            Camera.DumpOrDiff();
            return;
        }

        // Undocumented, same errand as camdump: a fault finder for "a mod stayed enabled when
        // something replaced it", which has several causes that look identical on screen
        if (trimmed.Equals("modstate", StringComparison.OrdinalIgnoreCase))
        {
            _wardrobeService.DumpModState();
            return;
        }

        if (trimmed.Equals("unequip", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var id in System.Linq.Enumerable.ToList(_config.WornItems.Values))
            {
                var item = _config.WardrobeItems.Find(x => x.Id == id);
                // restoreBase: false — the Clear() below wipes the record, so a base put back here
                // would leave its mods enabled with nothing saying so
                if (item != null) _wardrobeService.UnwearItem(item, save: false, restoreBase: false);
            }
            _config.WornItems.Clear();
            _config.Save();
            return;
        }

        // Documented, unlike the two above: it is the same thing the offer's button does, for anyone
        // who dismissed the offer, turned it off, or wants it in a macro
        if (trimmed.Equals("restore", StringComparison.OrdinalIgnoreCase))
        {
            if (_lastWorn.Remembered is { } snapshot)
                _lastWorn.Restore(snapshot);
            else
                Log.Warning("[Wardrobe] Nothing is remembered from a previous session.");
            return;
        }

        if (trimmed.StartsWith("wear ", StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[5..].Trim();
            var item = _config.WardrobeItems.Find(i =>
                i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item != null)
                _wardrobeService.WearItemLinked(item);
            else
                Log.Warning($"[Wardrobe] No item named '{name}' found.");
            return;
        }

        OpenUi();
    }

    /// <summary>
    /// Copies the config file aside, once, before the wardrobe is rearranged inside it.
    /// </summary>
    /// <remarks>
    /// The one migration that moves everything somebody owns from one shape to another. Every other
    /// one adds a field or fixes a value; this one relocates the items, the outfits and the bases,
    /// and it runs against configs written by versions that predate the idea. A copy costs a few
    /// hundred kilobytes and means a bad migration is an annoyance rather than a loss.
    /// <para>
    /// Never overwritten. If the file is already there, this has been through once before and the
    /// copy from that first run is the one worth keeping.
    /// </para>
    /// </remarks>
    private static void BackUpBeforeMigrating(IDalamudPluginInterface pi)
    {
        try
        {
            var source = pi.ConfigFile;
            if (!source.Exists) return;

            var backup = System.IO.Path.Combine(
                source.DirectoryName ?? string.Empty,
                System.IO.Path.GetFileNameWithoutExtension(source.Name) + ".pre-wardrobes.json");

            if (System.IO.File.Exists(backup)) return;

            source.CopyTo(backup);
            Log.Information($"[Wardrobe] Config copied to '{backup}' before the per-character migration");
        }
        catch (Exception ex)
        {
            // Not fatal. The migration itself is non-destructive within the file, and refusing to
            // start because a copy failed would be the worse outcome.
            Log.Warning(ex, "[Wardrobe] Could not copy the config before migrating");
        }
    }

    private void OpenUi() => _ui.IsOpen = true;

    private void OpenSettings() => _settings.IsOpen = true;

    public void Dispose()
    {
        Commands.RemoveHandler(CommandName);
        Commands.RemoveHandler(CommandAlias);
        PluginInterface.UiBuilder.Draw        -= _windowSystem.Draw;
        PluginInterface.UiBuilder.Draw        -= _ui.DrawFileDialog;
        PluginInterface.UiBuilder.Draw        -= _cropGuide.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;

        _ui.Dispose();
        _massImport.Dispose();
        _share.Dispose();
        _settings.Dispose();
        _lastWornWindow.Dispose();
        _italicFont.Dispose();
        _backupService.Dispose();
        _lastWorn.Dispose();
        _profiles.Dispose();
        _screenshotSession.Dispose();
        _wardrobeService.Dispose();
        Camera.Dispose();
        Penumbra.Dispose();
        Glamourer.Dispose();
    }
}
