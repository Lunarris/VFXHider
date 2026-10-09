using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class MainWindow : Window, IDisposable
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

    public override void Draw()
    {
        var config = configuration.VfxFilter;
        var module = plugin.VfxFilter;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable VFX Hider", ref enabled))
            plugin.SetVfxFilterEnabled(enabled);

        if (!module.SignaturesTrusted)
            DrawVersionWarning(module);

        using (ImRaii.Disabled(!config.Enabled))
        {
            ImGui.Separator();


            var exempt = config.ExemptPartyAndFriends;
            if (ImGui.Checkbox("Allow emotes for Friends and Party Members", ref exempt))
            {
                config.ExemptPartyAndFriends = exempt;
                Save();
            }

            ImGui.Separator();
            ImGui.Text("Enable blocks for modded VFX on below emotes:");

            foreach (var preset in VfxFilterModule.PresetEmoteNames)
            {
                var found = module.PresetEmoteIds.TryGetValue(preset, out var ids);

                var label = found && module.EmoteNames.TryGetValue(ids!.First(), out var localName) ? localName : preset;

                using (ImRaii.Disabled(!found))
                {
                    var blocked = config.BlockedPresetEmotes.Contains(preset);
                    if (ImGui.Checkbox($"{label}##Preset{preset}", ref blocked))
                    {
                        config.BlockedPresetEmotes.Remove(preset);
                        if (blocked)
                            config.BlockedPresetEmotes.Add(preset);

                        Save();
                    }
                }

                if (!found && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("This emote was not found in the game data.");
            }

            ImGui.Separator();
            ImGui.TextDisabled($"Effects hidden this session: {module.TotalHidden}");
        }

        ImGui.Separator();

        if (ImGui.Button("VFX Users"))
            plugin.ToggleVFXUsersUi();

        ImGui.SameLine();
        if (ImGui.Button("Blacklist"))
            plugin.ToggleBlacklistUi();


        ImGui.SameLine();
        if (ImGui.Button("Advanced Settings"))
            plugin.ToggleConfigUi();
    }

    private void DrawVersionWarning(VfxFilterModule module)
    {
        var version = module.GameVersion.Length > 0 ? module.GameVersion : "unknown";

        ImGui.Separator();
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

    private void Save()
    {
        configuration.Save();
        plugin.VfxFilter.OnFilterSettingsChanged();
    }
}
