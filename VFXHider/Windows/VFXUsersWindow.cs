using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class VFXUsersWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly List<VFXUser> users = [];

    public VFXUsersWindow(Plugin plugin) : base("VFX Hider Mod Users###VFXHiderVFXUsers")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration.VfxFilter;
        var module = plugin.VfxFilter;

        module.VFXUsers.CopyTo(users);
        users.RemoveAll(static user => user.VfxCount + user.VfxFileCount == 0);
        users.Sort(static (a, b) => b.LastVfxSeen.CompareTo(a.LastVfxSeen));

        if (ImGui.Button("Clear list"))
        {
            module.VFXUsers.Clear();
            users.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open Blacklist"))
            plugin.ToggleBlacklistUi();

        ImGui.SameLine();
        ImGui.Text(config.Enabled
            ? $"{users.Count} players seen using modded VFX."
            : "VFX Hider is disabled, so nobody new is being picked up.");


        using var table = ImRaii.Table("VFXUsers", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Last VFX", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Spawns", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Files", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableSetupColumn("Last file", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableHeadersRow();

        foreach (var user in users)
        {
            var blacklisted = Plugin.IsListed(config.Blacklist, user.Name);
            var whitelisted = Plugin.IsListed(config.Whitelist, user.Name);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(blacklisted ? $"{user.Name} [blacklisted]" : whitelisted ? $"{user.Name} [whitelisted]" : user.Name);
            ImGui.TableNextColumn();
            ImGui.Text(user.LastVfxSeen.ToString("HH:mm:ss"));
            ImGui.TableNextColumn();
            ImGui.Text(user.VfxCount.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(user.VfxFileCount.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(user.LastVfx);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{(blacklisted ? "Un-blacklist" : "Blacklist")}##{user.Name}"))
                plugin.SetBlacklisted(user.Name, !blacklisted);
            ImGui.SameLine();
            if (ImGui.SmallButton($"{(whitelisted ? "Un-whitelist" : "Whitelist")}##{user.Name}"))
                plugin.SetWhitelisted(user.Name, !whitelisted);
        }
    }
}
