using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class BlacklistWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly List<VFXUser> users = [];

    public BlacklistWindow(Plugin plugin) : base("VFX Hider Blacklist###VFXHiderBlacklist")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 220),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration.VfxFilter;
        var module = plugin.VfxFilter;

        ImGui.TextWrapped("Blacklisted players have every modded effect they spawn hidden, even if they are a friend or in your party. The player and their vanilla effects stay visible.");

        ImGui.TextWrapped("Add players from the VFX users list or by right-clicking them.");

        if (ImGui.Button("Open VFX Users"))
            plugin.ToggleVFXUsersUi();

        ImGui.SameLine();
        ImGui.Text($"{config.Blacklist.Count} blacklisted.");

        module.VFXUsers.CopyTo(users);

        using var table = ImRaii.Table("Blacklist", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Nearby", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Hidden", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Last hidden", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Last modded VFX", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableHeadersRow();

        foreach (var entry in config.Blacklist.ToArray())
        {
            var name = entry.Trim();
            var seen = users.Find(user => PlayerKey.Matches(name, user.Name));
            var nearby = module.IsHooked && module.IsPlayerNearby(name);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(name);
            ImGui.TableNextColumn();
            if (nearby)
                ImGui.Text("yes");
            else
                ImGui.TextDisabled("no");
            ImGui.TableNextColumn();
            ImGui.Text((seen?.HiddenCount ?? 0).ToString());
            ImGui.TableNextColumn();
            if (seen is { HiddenCount: > 0 })
                ImGui.Text(seen.LastHidden.ToString("HH:mm:ss"));
            else
                ImGui.TextDisabled("-");
            ImGui.TableNextColumn();
            ImGui.Text(seen?.LastVfx ?? string.Empty);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Remove##{name}"))
                plugin.SetBlacklisted(name, false);

            if (nearby)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Redraw##{name}"))
                    module.RedrawPlayer(name);
            }
        }
    }
}
