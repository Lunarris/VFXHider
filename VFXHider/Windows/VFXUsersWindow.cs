using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class VFXUsersWindow : ThemedWindow, IDisposable
{
    private const long RefreshMs = 250;

    private readonly record struct Row(
        string Key,
        string Label,
        string LastSeen,
        string Spawns,
        string Files,
        string LastVfx,
        bool Blacklisted,
        bool Whitelisted);

    private readonly Plugin plugin;
    private readonly List<VFXUser> users = [];
    private readonly List<Row> rows = [];
    private PlayerEntry[]? rowsForBlacklist;
    private PlayerEntry[]? rowsForWhitelist;
    private long rowsAt;

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

    public override void OnOpen() => rowsForBlacklist = null;

    protected override void DrawContents()
    {
        var config = plugin.Configuration.VfxFilter;
        var module = plugin.VfxFilter;

        RefreshRows(module);

        if (ImGui.Button("Clear list"))
        {
            module.VFXUsers.Clear();
            users.Clear();
            rows.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open Blacklist"))
            plugin.ToggleBlacklistUi();

        ImGui.SameLine();
        ImGui.Text(config.Enabled
            ? $"{rows.Count} players seen using modded VFX."
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

        ClippedRows.Draw(rows.Count, DrawRow);
    }

    private void DrawRow(int index)
    {
        var row = rows[index];

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.LastSeen);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Spawns);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Files);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.LastVfx);
        ImGui.TableNextColumn();
        if (ImGui.SmallButton($"{(row.Blacklisted ? "Un-blacklist" : "Blacklist")}##{row.Key}"))
            plugin.SetBlacklisted(row.Key, !row.Blacklisted);
        ImGui.SameLine();
        if (ImGui.SmallButton($"{(row.Whitelisted ? "Un-whitelist" : "Whitelist")}##{row.Key}"))
            plugin.SetWhitelisted(row.Key, !row.Whitelisted);
    }

    private void RefreshRows(VfxFilterModule module)
    {
        var now = Environment.TickCount64;
        var blacklist = module.Blacklist;
        var whitelist = module.Whitelist;
        if (ReferenceEquals(blacklist, rowsForBlacklist) && ReferenceEquals(whitelist, rowsForWhitelist) && now - rowsAt < RefreshMs)
            return;

        rowsForBlacklist = blacklist;
        rowsForWhitelist = whitelist;
        rowsAt = now;

        module.VFXUsers.CopyTo(users);
        users.RemoveAll(static user => user.VfxCount + user.VfxFileCount == 0);
        users.Sort(static (a, b) => b.LastVfxSeen.CompareTo(a.LastVfxSeen));

        rows.Clear();
        foreach (var user in users)
        {
            var (name, world) = PlayerKey.Split(user.Name);
            var blacklisted = PlayerKey.IsListed(blacklist, name, world);
            var whitelisted = PlayerKey.IsListed(whitelist, name, world);

            rows.Add(new Row(
                user.Name,
                blacklisted ? $"{user.Name} [blacklisted]" : whitelisted ? $"{user.Name} [whitelisted]" : user.Name,
                user.LastVfxSeen.ToString("HH:mm:ss"),
                user.VfxCount.ToString(),
                user.VfxFileCount.ToString(),
                user.LastVfx,
                blacklisted,
                whitelisted));
        }
    }
}
