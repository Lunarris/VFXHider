using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using VFXHider.VfxFilter;
using VFXHider.Windows;

namespace VFXHider;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/vfxhider";

    public Configuration Configuration { get; init; }
    public VfxFilterModule VfxFilter { get; init; }

    public readonly WindowSystem WindowSystem = new("VFXHider");
    private MainWindow MainWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }
    private VfxFilterWindow VfxFilterWindow { get; init; }
    private VFXUsersWindow VFXUsersWindow { get; init; }
    private BlacklistWindow BlacklistWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        VfxFilter = new VfxFilterModule(Configuration.VfxFilter);

        MainWindow = new MainWindow(this);
        ConfigWindow = new ConfigWindow(this);
        VfxFilterWindow = new VfxFilterWindow(this);
        VFXUsersWindow = new VFXUsersWindow(this);
        BlacklistWindow = new BlacklistWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(VfxFilterWindow);
        WindowSystem.AddWindow(VFXUsersWindow);
        WindowSystem.AddWindow(BlacklistWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open VFX Hider. \"/vfxhider on\" and \"/vfxhider off\" switch it. \"users\", \"blacklist\" and \"config\" open those windows; \"log\" opens the debug spawn log."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    public void Dispose()
    {
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        ConfigWindow.Dispose();
        VfxFilterWindow.Dispose();
        VFXUsersWindow.Dispose();
        BlacklistWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        VfxFilter.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                SetVfxFilterEnabled(true);
                ChatGui.Print("VFX Hider enabled.");
                break;
            case "off":
                SetVfxFilterEnabled(false);
                ChatGui.Print("VFX Hider disabled.");
                break;
            case "config":
                ToggleConfigUi();
                break;
            case "log":
            case "logs":
                ToggleVfxFilterUi();
                break;
            case "users":
                ToggleVFXUsersUi();
                break;
            case "blacklist":
                ToggleBlacklistUi();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    public void ToggleMainUi() => MainWindow.Toggle();
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleVfxFilterUi() => VfxFilterWindow.Toggle();
    public void ToggleVFXUsersUi() => VFXUsersWindow.Toggle();
    public void ToggleBlacklistUi() => BlacklistWindow.Toggle();

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target)
            return;

        var name = target.TargetName;
        if (string.IsNullOrWhiteSpace(name))
            return;

        var isPlayer = target.TargetObject is IPlayerCharacter || (target.TargetObject == null && target.TargetContentId != 0);
        if (!isPlayer)
            return;

        var key = PlayerKey.Make(name, target.TargetHomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty);
        var whitelisted = IsListed(Configuration.VfxFilter.Whitelist, key);
        var blacklisted = IsListed(Configuration.VfxFilter.Blacklist, key);

        args.AddMenuItem(new MenuItem
        {
            Name = whitelisted ? "Remove from VFX Hider whitelist" : "Add to VFX Hider whitelist",
            PrefixChar = 'V',
            PrefixColor = 37,
            OnClicked = _ => SetWhitelisted(key, !whitelisted),
        });

        args.AddMenuItem(new MenuItem
        {
            Name = blacklisted ? "Remove from VFX Hider blacklist" : "Add to VFX Hider blacklist",
            PrefixChar = 'V',
            PrefixColor = 17,
            OnClicked = _ => SetBlacklisted(key, !blacklisted),
        });
    }

    public static bool IsListed(List<string> entries, string key)
        => PlayerKey.IsListed(entries, key);

    public void SetWhitelisted(string key, bool listed)
        => SetListed(Configuration.VfxFilter.Whitelist, Configuration.VfxFilter.Blacklist, key, listed);

    public void SetBlacklisted(string key, bool listed)
        => SetListed(Configuration.VfxFilter.Blacklist, Configuration.VfxFilter.Whitelist, key, listed);

    private void SetListed(List<string> list, List<string> other, string key, bool listed)
    {
        list.RemoveAll(entry => PlayerKey.Matches(entry, key));
        if (listed)
        {
            list.Add(key);
            other.RemoveAll(entry => PlayerKey.Matches(entry, key));
        }

        Configuration.Save();
        VfxFilter.OnFilterSettingsChanged();

        if (Configuration.VfxFilter.RedrawOnListChange)
            VfxFilter.RedrawPlayer(key);
    }

    public void SetVfxFilterEnabled(bool enabled)
    {
        Configuration.VfxFilter.Enabled = enabled;
        Configuration.Save();
        VfxFilter.SetEnabled(enabled);
    }
}
