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

public class VfxFilterWindow : ThemedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly List<VfxSpawnRecord> snapshot = new(VfxFilterModule.LogCapacity);
    private readonly List<VfxSpawnRecord> shown = new(VfxFilterModule.LogCapacity);
    private long snapshotVersion = -1;
    private string shownSearch = string.Empty;
    private bool shownOnlyInteresting;
    private long shownVersion = -1;
    private long shownAt;
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

    protected override void DrawContents()
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
        if (log.Version != snapshotVersion)
        {
            snapshotVersion = log.Version;
            log.CopyTo(snapshot);
        }

        if (ImGui.Button("Clear"))
        {
            log.Clear();
            snapshot.Clear();
            shown.Clear();
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

        RefreshShown();

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

        ClippedRows.Draw(shown.Count, DrawRow);

        if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1)
            ImGui.SetScrollHereY(1.0f);
    }

    private void DrawRow(int index)
    {
        var record = shown[index];

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.TimeText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.KindText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.Path);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.SourceText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.TargetText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.EmoteName);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.ModStateText);
        if (record.ResolvedPath.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(record.ResolvedPath);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.HideReason);
    }

    private void RefreshShown()
    {
        var now = Environment.TickCount64;
        if (shownVersion == snapshotVersion && shownSearch == search && shownOnlyInteresting == onlyInteresting && now - shownAt < 1000)
            return;

        shownVersion = snapshotVersion;
        shownSearch = search;
        shownOnlyInteresting = onlyInteresting;
        shownAt = now;

        var term = search.Trim();
        shown.Clear();
        foreach (var record in snapshot)
        {
            if (IsShown(record, term))
                shown.Add(record);
        }
    }

    private bool IsShown(VfxSpawnRecord record, string term)
    {
        if (onlyInteresting && record.ModState != VfxModState.Modded && record.HideReason.Length == 0)
            return false;

        return term.Length == 0
            || record.Path.Contains(term, StringComparison.OrdinalIgnoreCase)
            || record.EmoteName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || record.KindText.Contains(term, StringComparison.OrdinalIgnoreCase)
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
        foreach (var record in shown)
            builder.Append(record).Append('\n');

        return builder.ToString();
    }
}
