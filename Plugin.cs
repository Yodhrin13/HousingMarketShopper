using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HousingMarketShopper.Services;
using HousingMarketShopper.Windows;

namespace HousingMarketShopper;

/// <summary>Plugin entry point.</summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "HousingMarketShopper";

    private const string CommandName = "/hms";

    // ── Static accessors used by windows (avoids passing everything around) ──
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    internal static NavigationService       NavigationService { get; private set; } = null!;
    internal static ConfirmPurchaseWindow   ConfirmWindow     { get; private set; } = null!;

    // ── Plugin internals ──────────────────────────────────────────────────────
    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager         _commands;
    private readonly IPluginLog              _log;

    private readonly Configuration       _config;
    private readonly ItemResolverService _resolver;
    private readonly UniversalisService  _universalis;
    private readonly ShoppingListService _shopList;
    private readonly MarketboardService  _marketboard;
    private readonly NavigationService   _navSvc;

    private readonly WindowSystem          _windowSystem = new("HousingMarketShopper");
    private readonly MainWindow            _mainWindow;
    private readonly ConfigWindow          _configWindow;
    private readonly ConfirmPurchaseWindow _confirmWindow;

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager         commands,
        IGameGui                gameGui,
        IFramework              framework,
        IClientState            clientState,
        ITargetManager          targetManager,
        IObjectTable            objects,
        IMarketBoard            marketBoard,
        IPluginLog              log)
    {
        _pi       = pi;
        _commands = commands;
        _log      = log;

        PluginInterface = pi;

        // Load or create config
        _config = pi.GetPluginConfig() as Configuration ?? new Configuration();

        // Services (order matters — MarketboardService before NavigationService)
        _resolver    = new ItemResolverService(pi, log);
        _universalis = new UniversalisService(log);
        _marketboard = new MarketboardService(
            gameGui, framework, targetManager, objects, marketBoard, _config, log);
        _navSvc      = new NavigationService(pi, commands, framework, _marketboard, objects, clientState, _config, log);
        _shopList    = new ShoppingListService(_resolver, _universalis, _config, log);

        NavigationService = _navSvc;

        // Windows
        _confirmWindow = new ConfirmPurchaseWindow(_config);
        _configWindow  = new ConfigWindow(_config);
        _mainWindow    = new MainWindow(_config, _shopList, _navSvc, objects, log);

        ConfirmWindow = _confirmWindow;

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_confirmWindow);

        pi.UiBuilder.Draw        += _windowSystem.Draw;
        pi.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        pi.UiBuilder.OpenMainUi   += OnOpenMainUi;

        commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Housing Market Shopper window. Use /hms config to open settings.",
        });

        log.Information("[HMS] Plugin loaded.");
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
            _configWindow.IsOpen = !_configWindow.IsOpen;
        else
            _mainWindow.IsOpen = !_mainWindow.IsOpen;
    }

    private void OnOpenMainUi()   => _mainWindow.IsOpen   = true;
    private void OnOpenConfigUi() => _configWindow.IsOpen = true;

    public void Dispose()
    {
        _pi.UiBuilder.Draw        -= _windowSystem.Draw;
        _pi.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        _pi.UiBuilder.OpenMainUi   -= OnOpenMainUi;

        _commands.RemoveHandler(CommandName);

        _mainWindow.Dispose();
        _windowSystem.RemoveAllWindows();

        _navSvc.Dispose();
        _marketboard.Dispose();
        _resolver.Dispose();
        _universalis.Dispose();

        _log.Information("[HMS] Plugin unloaded.");
    }
}
