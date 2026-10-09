using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class VfxFilterWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly List<VfxSpawnRecord> snapshot = new(VfxFilterModule.LogCapacity);
    private bool autoScroll = true;
    private bool onlyInteresting;
    private string search = string.Empty;

    public VfxFilterWindow(Plugin plugin) : base("VFX Hider Spawn Log###VfxFilterLog")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void OnOpen() => plugin.VfxFilter.CaptureSpawnLog = true;

    public override void OnClose() => plugin.VfxFilter.CaptureSpawnLog = false;

    public override void Draw()
    {
        var config = plugin.Configuration.VfxFilter;
        var module = plugin.VfxFilter;

        if (!config.Enabled)
            ImGui.TextDisabled("VFX Hider is disabled, so nothing new is being logged. Enable it in the main window.");

        DrawSpawnLog(module);
    }

    private void DrawSpawnLog(VfxFilterModule module)
    {
        var log = module.SpawnLog;
        log.CopyTo(snapshot);

        if (ImGui.Button("Clear"))
        {
            log.Clear();
            snapshot.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy log to clipboard"))
            ImGui.SetClipboardText(BuildClipboardText());

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref autoScroll);

        ImGui.SameLine();
        ImGui.Text($"Hooks: {(module.IsHooked ? "active" : "off")} | last 1s: {CountSince(1)} | last 10s: {CountSince(10)} | kept: {snapshot.Count}/{VfxFilterModule.LogCapacity} | logged: {log.TotalSpawns} | seen: {module.SpawnsSeen} | hidden: {module.TotalHidden}");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##VfxLogSearch", "Show only rows containing...", ref search, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Only modded or hidden rows", ref onlyInteresting);

        using var table = ImRaii.Table("VfxSpawns", 8,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Emote", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Hidden", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();

        foreach (var record in snapshot)
        {
            if (!IsShown(record))
                continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(record.Time.ToString("HH:mm:ss.fff"));
            ImGui.TableNextColumn();
            ImGui.Text(record.Kind.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(record.Path);
            ImGui.TableNextColumn();
            ImGui.Text(record.SourceText);
            ImGui.TableNextColumn();
            ImGui.Text(record.Target.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(record.EmoteName);
            ImGui.TableNextColumn();
            ImGui.Text(record.ModState.ToString());
            if (record.ResolvedPath.Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(record.ResolvedPath);
            ImGui.TableNextColumn();
            ImGui.Text(record.HideReason);
        }

        if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1)
            ImGui.SetScrollHereY(1.0f);
    }

    private bool IsShown(VfxSpawnRecord record)
    {
        if (onlyInteresting && record.ModState != VfxModState.Modded && record.HideReason.Length == 0)
            return false;

        var term = search.Trim();
        return term.Length == 0
            || record.Path.Contains(term, StringComparison.OrdinalIgnoreCase)
            || record.EmoteName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || record.Kind.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
            || record.Source.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || record.Owner.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || record.Target.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private int CountSince(double seconds)
    {
        var cutoff = DateTime.Now.AddSeconds(-seconds);
        var count = 0;
        for (var i = snapshot.Count - 1; i >= 0 && snapshot[i].Time >= cutoff; i--)
            count++;

        return count;
    }

    private string BuildClipboardText()
    {
        var builder = new StringBuilder("Time\tType\tPath\tSource\tTarget\tEmote\tMod\tHidden\tResolved\n");
        foreach (var record in snapshot)
        {
            if (IsShown(record))
                builder.Append(record).Append('\n');
        }

        return builder.ToString();
    }
}
