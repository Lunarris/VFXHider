using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class BlacklistWindow : ThemedWindow, IDisposable
{
    private const long RefreshMs = 500;

    private readonly record struct Row(string Entry, bool Nearby, string Hidden, string LastHidden, string LastVfx);

    private readonly Plugin plugin;
    private readonly List<VFXUser> users = [];
    private readonly List<(VFXUser User, string Name, string World)> splitUsers = [];
    private readonly List<(string Name, string World)> nearby = [];
    private readonly List<Row> rows = [];
    private PlayerEntry[]? rowsFor;
    private long rowsAt;

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

    public override void OnOpen() => rowsFor = null;

    protected override void DrawContents()
    {
        var config = plugin.Configuration.VfxFilter;
        var module = plugin.VfxFilter;

        ImGui.TextWrapped("Blacklisted players have every modded effect they spawn hidden, even if they are a friend or in your party. The player and their vanilla effects stay visible.");

        ImGui.TextWrapped("Add players from the VFX users list or by right-clicking them.");

        if (ImGui.Button("Open VFX Users"))
            plugin.ToggleVFXUsersUi();

        ImGui.SameLine();
        ImGui.Text($"{config.Blacklist.Count} blacklisted.");

        RefreshRows(module);

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

        ClippedRows.Draw(rows.Count, DrawRow);
    }

    private void DrawRow(int index)
    {
        var row = rows[index];

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Entry);
        ImGui.TableNextColumn();
        if (row.Nearby)
            ImGui.TextUnformatted("yes");
        else
            ImGui.TextDisabled("no");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Hidden);
        ImGui.TableNextColumn();
        if (row.LastHidden.Length > 0)
            ImGui.TextUnformatted(row.LastHidden);
        else
            ImGui.TextDisabled("-");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.LastVfx);
        ImGui.TableNextColumn();
        if (ImGui.SmallButton($"Remove##{row.Entry}"))
            plugin.SetBlacklisted(row.Entry, false);

        if (row.Nearby)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Redraw##{row.Entry}"))
                plugin.VfxFilter.RedrawPlayer(row.Entry);
        }
    }

    private void RefreshRows(VfxFilterModule module)
    {
        var now = Environment.TickCount64;
        var blacklist = module.Blacklist;
        if (ReferenceEquals(blacklist, rowsFor) && now - rowsAt < RefreshMs)
            return;

        rowsFor = blacklist;
        rowsAt = now;

        module.VFXUsers.CopyTo(users);
        splitUsers.Clear();
        foreach (var user in users)
        {
            var (name, world) = PlayerKey.Split(user.Name);
            splitUsers.Add((user, name, world));
        }

        if (module.IsHooked)
            module.CollectNearbyPlayers(nearby);
        else
            nearby.Clear();

        rows.Clear();
        foreach (var entry in blacklist)
        {
            if (entry.Raw.Length == 0)
                continue;

            VFXUser? seen = null;
            foreach (var user in splitUsers)
            {
                if (entry.Matches(user.Name, user.World))
                {
                    seen = user.User;
                    break;
                }
            }

            var isNearby = false;
            foreach (var player in nearby)
            {
                if (entry.Matches(player.Name, player.World))
                {
                    isNearby = true;
                    break;
                }
            }

            rows.Add(new Row(
                entry.Raw,
                isNearby,
                (seen?.HiddenCount ?? 0).ToString(),
                seen is { HiddenCount: > 0 } ? seen.LastHidden.ToString("HH:mm:ss") : string.Empty,
                seen?.LastVfx ?? string.Empty));
        }
    }
}
