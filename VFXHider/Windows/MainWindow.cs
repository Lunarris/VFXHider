using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class MainWindow : ThemedWindow, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("VFX Hider###VFXHiderMain")
    {
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    protected override void DrawContents()
    {
        var module = plugin.VfxFilter;

        if (!module.SignaturesTrusted)
            DrawVersionWarning(module);

        if (configuration.VfxFilter.Enabled)
            ImGui.TextUnformatted($"Effects hidden this session: {module.TotalHidden}");
        else
            ImGui.TextDisabled("VFX Hider is off. Turn it on in Settings, General.");

        ImGui.Separator();

        if (ImGui.Button("VFX Users"))
            plugin.ToggleVFXUsersUi();

        ImGui.SameLine();
        if (ImGui.Button("Blacklist"))
            plugin.ToggleBlacklistUi();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();
    }

    private void DrawVersionWarning(VfxFilterModule module)
    {
        var version = module.GameVersion.Length > 0 ? module.GameVersion : "unknown";

        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.2f, 1f)))
        {
            ImGui.TextWrapped($"Game version {version} is not the one this plugin was checked against ({VfxFilterModule.VerifiedGameVersion}).");
        }

        ImGui.TextWrapped("Effect hiding is switched off to avoid crashes. The mod users list and the blacklist itself still work.");
        ImGui.TextWrapped("Only confirm once VFXEditor has updated for this patch and its signatures still match the ones in this plugin.");

        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl || module.GameVersion.Length == 0))
        {
            if (ImGui.Button("Signatures checked, enable on this version"))
            {
                configuration.VfxFilter.ConfirmedGameVersion = module.GameVersion;
                configuration.Save();
                module.RefreshHooks();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(module.GameVersion.Length == 0 ? "The game version could not be read, so it cannot be confirmed." : "Hold Ctrl and click.");

        ImGui.Separator();
    }
}
