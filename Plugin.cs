using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
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
    private readonly BackupService            _backupService;
    private readonly ItalicFontService        _italicFont;
    private readonly WindowSystem             _windowSystem;
    private readonly CropGuideOverlay          _cropGuide;
    private readonly PluginUi                _ui;
    private readonly MassImportPanel         _massImport;
    private readonly ChangelogWindow         _changelog;

    private const string CommandName  = "/wardrobe";
    private const string CommandAlias = "/wr";

    public Plugin(IDalamudPluginInterface pi)
    {
        PluginInterface = pi;
        pi.Inject(this);

        _config = pi.GetPluginConfig() as Configuration ?? new Configuration();
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
        _backupService     = new BackupService(_config, Framework, Log);

        _windowSystem = new WindowSystem("WardrobePlugin");
        _italicFont = new ItalicFontService(Log);
        IconPacks   = new IconPackService(_config, Log);
        SlotIcons   = new SlotIconService(_config, Textures, _itemLookup, IconPacks, Log);

        var panel = new ItemImportPanel(_config, _wardrobeService, Penumbra, Glamourer, _analysisService, _itemLookup, _screenshotSession, Log, _italicFont);
        _massImport = new MassImportPanel(_config, Penumbra, _analysisService, _itemLookup, Log, _italicFont);
        _ui = new PluginUi(_config, _wardrobeService, Textures, Log, panel, _screenshotSession, _backupService, _massImport);
        _changelog = new ChangelogWindow(_config);
        _ui.Changelog = _changelog;

        _windowSystem.AddWindow(_ui);
        _windowSystem.AddWindow(_massImport);
        _windowSystem.AddWindow(_changelog);

        ShowChangelogIfUpdated();

        // Drawn outside the window system: the guide has to be on screen while you frame a shot,
        // which is exactly when the wardrobe's own window is closed or pushed aside (#25).
        _cropGuide = new CropGuideOverlay(_config, _screenshotSession);

        pi.UiBuilder.DisableGposeUiHide = true;

        pi.UiBuilder.Draw        += _windowSystem.Draw;
        pi.UiBuilder.Draw        += _cropGuide.Draw;
        pi.UiBuilder.OpenMainUi   += OpenUi;
        pi.UiBuilder.OpenConfigUi += OpenUi;

        Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Wardrobe window"
        });

        Commands.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias of /wardrobe"
        });
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

    private void OpenUi() => _ui.IsOpen = true;

    public void Dispose()
    {
        Commands.RemoveHandler(CommandName);
        Commands.RemoveHandler(CommandAlias);
        PluginInterface.UiBuilder.Draw        -= _windowSystem.Draw;
        PluginInterface.UiBuilder.Draw        -= _cropGuide.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenUi;

        _ui.Dispose();
        _massImport.Dispose();
        _italicFont.Dispose();
        _backupService.Dispose();
        _screenshotSession.Dispose();
        _wardrobeService.Dispose();
        Camera.Dispose();
        Penumbra.Dispose();
        Glamourer.Dispose();
    }
}
