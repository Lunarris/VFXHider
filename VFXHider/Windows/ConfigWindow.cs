using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;

    private string newScopePattern = string.Empty;
    private string newBlockPattern = string.Empty;

    public ConfigWindow(Plugin plugin) : base("VFX Hider Advanced Settings###VFXHiderConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(460, 620);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = configuration.VfxFilter;

        ImGui.Text("Players");

        ImGui.Text("Whitelisted players (never hidden)");
        if (config.Whitelist.Count == 0)
            ImGui.TextDisabled("None. Add players from the VFX users list or by right-clicking them.");

        foreach (var entry in config.Whitelist.ToArray())
        {
            if (ImGui.SmallButton($"X##Whitelist{entry}"))
                plugin.SetWhitelisted(entry, false);

            ImGui.SameLine();
            ImGui.Text(entry);
        }

        if (ImGui.Button("Open Blacklist"))
            plugin.ToggleBlacklistUi();

        var redraw = config.RedrawOnListChange;
        if (ImGui.Checkbox("Redraw a player when their list status changes", ref redraw))
        {
            config.RedrawOnListChange = redraw;
            configuration.Save();
        }
        Hint("Uses Penumbra to redraw the player when you blacklist, whitelist or un-list them, so their effects reload under the new rule. The player blinks out for a moment.");


        ImGui.Separator();
        ImGui.Text("Additional rules (all off by default)");

        var advanced = config.HideEffects;
        if (ImGui.Checkbox("Use the rules below", ref advanced))
        {
            config.HideEffects = advanced;
            SaveFilter();
        }
        Hint("Off leaves only the emote toggles in the main window and the blacklist active.");

        using (ImRaii.Disabled(!config.HideEffects))
        {
            var hideModded = config.HideModded;
            if (ImGui.Checkbox("Hide effects that are modded for that player", ref hideModded))
            {
                config.HideModded = hideModded;
                SaveFilter();
            }
            Hint("Needs Penumbra. Vanilla effects are left alone.");

            DrawStringList("...but only for paths containing (empty = all modded effects)", "Scope", config.ModdedScopePatterns, ref newScopePattern, "vfx/emote");

            ImGui.Spacing();
            var hideMinion = config.HideModdedMinionEffects;
            if (ImGui.Checkbox("Hide modded effects from minions", ref hideMinion))
            {
                config.HideModdedMinionEffects = hideMinion;
                SaveFilter();
            }
            Hint("Judged by the minion's owner. The minion itself stays visible.");

            DrawStringList("Always hide paths containing (modded or not)", "Block", config.BlockPatterns, ref newBlockPattern, "part of an .avfx path");

            var rateLimit = config.RateLimitPerSecond;
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Max effects per player per second", ref rateLimit))
            {
                config.RateLimitPerSecond = Math.Clamp(rateLimit, 0, 1000);
                SaveFilter();
            }
            Hint("Effects beyond this are hidden. 0 turns the limit off.");
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("Debug"))
            DrawDebug();
    }

    private void DrawDebug()
    {
        var config = configuration.VfxFilter;
        var module = plugin.VfxFilter;

        if (ImGui.Button("Open Spawn Log"))
            plugin.ToggleVfxFilterUi();

        var includeSelf = config.IncludeSelfForTesting;
        if (ImGui.Checkbox("Also apply everything to my own character", ref includeSelf))
        {
            config.IncludeSelfForTesting = includeSelf;
            SaveFilter();
        }

        var logToPluginLog = config.LogToPluginLog;
        if (ImGui.Checkbox("Also write spawns to /xllog", ref logToPluginLog))
        {
            config.LogToPluginLog = logToPluginLog;
            configuration.Save();
        }

        ImGui.TextDisabled($"Game version: {(module.GameVersion.Length > 0 ? module.GameVersion : "unknown")} (verified on {VfxFilterModule.VerifiedGameVersion})");
        ImGui.TextDisabled($"Hooks: {(module.IsHooked ? "active" : "off")} | signatures trusted: {(module.SignaturesTrusted ? "yes" : "no")}");
        ImGui.TextDisabled($"Spawns seen: {module.SpawnsSeen} | modded: {module.ModdedSpawnsSeen} | hidden: {module.TotalHidden}");
        ImGui.TextDisabled($"Penumbra file events: {module.PenumbraEvents} | vfx files: {module.PenumbraVfxFiles} | modded: {module.PenumbraModdedFiles} | matched to another player: {module.PenumbraFilesMatched}");
    }

    private void SaveFilter()
    {
        configuration.Save();
        plugin.VfxFilter.OnFilterSettingsChanged();
    }

    private static void Hint(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private void DrawStringList(string label, string id, List<string> entries, ref string input, string hint)
    {
        ImGui.Spacing();
        ImGui.Text(label);

        for (var i = 0; i < entries.Count; i++)
        {
            if (ImGui.SmallButton($"X##{id}{i}"))
            {
                entries.RemoveAt(i);
                SaveFilter();
                break;
            }

            ImGui.SameLine();
            ImGui.Text(entries[i]);
        }

        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint($"##{id}Input", hint, ref input, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        submitted |= ImGui.Button($"Add##{id}");

        if (submitted && input.Trim().Length > 0)
        {
            entries.Add(input.Trim());
            input = string.Empty;
            SaveFilter();
        }
    }
}
