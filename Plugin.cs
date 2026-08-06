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

    public static PenumbraIpc       Penumbra   { get; private set; } = null!;
    public static GlamourerIpc      Glamourer  { get; private set; } = null!;
    public static CameraService     Camera     { get; private set; } = null!;
    public static SlotIconService   SlotIcons  { get; private set; } = null!;
    public static ItemLookupService ItemLookup { get; private set; } = null!;

    private readonly Configuration            _config;
    private readonly WardrobeService          _wardrobeService;
    private readonly ModAnalysisService       _analysisService;
    private readonly ItemLookupService        _itemLookup;
    private readonly ScreenshotSessionService _screenshotSession;
    private readonly BackupService            _backupService;
    private readonly ItalicFontService        _italicFont;
    private readonly WindowSystem             _windowSystem;
    private readonly PluginUi                _ui;

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

        // Glamourer state is not persisted across sessions, so nothing is worn on load
        if (_config.WornItems.Count > 0)
        {
            _config.WornItems.Clear();
            _config.Save();
        }

        Penumbra  = new PenumbraIpc(pi, Log);
        Glamourer = new GlamourerIpc(pi, Log, Objects);
        Camera    = new CameraService(Framework, Log);

        _analysisService   = new ModAnalysisService(Log);
        _itemLookup        = new ItemLookupService(DataManager);
        ItemLookup         = _itemLookup;
        _wardrobeService   = new WardrobeService(Penumbra, Glamourer, _config, Log, Framework);
        _screenshotSession = new ScreenshotSessionService(_wardrobeService, _config, Framework, Log, Camera);
        _backupService     = new BackupService(_config, Framework, Log);

        _windowSystem = new WindowSystem("WardrobePlugin");
        _italicFont = new ItalicFontService(Log);
        SlotIcons   = new SlotIconService(_config, Textures, _itemLookup);

        var panel = new ItemImportPanel(_config, _wardrobeService, Penumbra, Glamourer, _analysisService, _itemLookup, _screenshotSession, Log, _italicFont);
        _ui = new PluginUi(_config, _wardrobeService, Textures, Log, panel, _screenshotSession, _backupService);
        _windowSystem.AddWindow(_ui);

        pi.UiBuilder.DisableGposeUiHide = true;

        pi.UiBuilder.Draw        += _windowSystem.Draw;
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

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            OpenUi();
            return;
        }

        if (trimmed.Equals("unequip", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var id in System.Linq.Enumerable.ToList(_config.WornItems.Values))
            {
                var item = _config.WardrobeItems.Find(x => x.Id == id);
                if (item != null) _wardrobeService.UnwearItem(item, save: false);
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
                _wardrobeService.WearItem(item);
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
        PluginInterface.UiBuilder.OpenMainUi   -= OpenUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenUi;

        _ui.Dispose();
        _italicFont.Dispose();
        _backupService.Dispose();
        _screenshotSession.Dispose();
        _wardrobeService.Dispose();
        Camera.Dispose();
        Penumbra.Dispose();
        Glamourer.Dispose();
    }
}
