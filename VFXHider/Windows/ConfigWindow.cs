using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VFXHider.VfxFilter;

namespace VFXHider.Windows;

public class ConfigWindow : ThemedWindow, IDisposable
{
    private static readonly string[] TabNames = ["General", "Players", "Rules", "Appearance", "Debug"];

    private readonly Configuration configuration;
    private readonly Configuration mutable = new();
    private readonly Plugin plugin;

    private int currentTab;
    private bool previewing;
    private string newScopePattern = string.Empty;
    private string newBlockPattern = string.Empty;

    public ConfigWindow(Plugin plugin) : base("VFX Hider Settings###VFXHiderConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(600, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public Configuration? Preview => previewing ? mutable : null;

    public void Dispose() { }

    public override void OnOpen()
    {
        mutable.CopySettingsFrom(configuration);
        newScopePattern = string.Empty;
        newBlockPattern = string.Empty;
        previewing = true;
    }

    public override void OnClose() => previewing = false;

    public void SyncEnabled(bool enabled) => mutable.VfxFilter.Enabled = enabled;

    protected override void DrawContents()
    {
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;

        using (var table = ImRaii.Table("##VFXHiderSettingsTable", 2))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("tab", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("settings", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextColumn();
                for (var i = 0; i < TabNames.Length; i++)
                {
                    if (ImGui.Selectable($"{TabNames[i]}###tab-{i}", currentTab == i))
                        currentTab = i;
                }

                ImGui.TableNextColumn();
                var height = ImGui.GetContentRegionAvail().Y - footerHeight;
                using var child = ImRaii.Child("##VFXHiderSettingsPage", new Vector2(-1, height));
                if (child.Success)
                {
                    switch (currentTab)
                    {
                        case 0: DrawGeneral(); break;
                        case 1: DrawPlayers(); break;
                        case 2: DrawRules(); break;
                        case 3: DrawAppearance(); break;
                        default: DrawDebug(); break;
                    }
                }
            }
        }

        ImGui.Separator();

        var save = ImGui.Button("Save");

        ImGui.SameLine();
        if (ImGui.Button("Save and Close"))
        {
            save = true;
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Discard"))
            IsOpen = false;

        if (save)
        {
            var wasEnabled = configuration.VfxFilter.Enabled;
            configuration.CopySettingsFrom(mutable);
            configuration.Save();
            plugin.VfxFilter.OnFilterSettingsChanged();

            if (configuration.VfxFilter.Enabled != wasEnabled)
                plugin.VfxFilter.SetEnabled(configuration.VfxFilter.Enabled);
        }
    }

    private void DrawGeneral()
    {
        var config = mutable.VfxFilter;
        var module = plugin.VfxFilter;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable VFX Hider", ref enabled))
            config.Enabled = enabled;
        Hint("Off removes every hook and shows anything that was hidden. \"/vfxhider on\" and \"/vfxhider off\" switch it straight away.");

        using (ImRaii.Disabled(!config.Enabled))
        {
            ImGui.Spacing();

            var exempt = config.ExemptPartyAndFriends;
            if (ImGui.Checkbox("Allow emotes for Friends and Party Members", ref exempt))
                config.ExemptPartyAndFriends = exempt;

            ImGui.Spacing();
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
                    }
                }

                if (!found && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("This emote was not found in the game data.");
            }

            ImGui.Spacing();
            var mute = config.MuteBlockedEmoteSounds;
            if (ImGui.Checkbox("Also mute players while they use these emotes", ref mute))
                config.MuteBlockedEmoteSounds = mute;
            Hint("Silences every sound from the player for as long as the emote lasts, modded or not.");
        }
    }

    private void DrawPlayers()
    {
        var config = mutable.VfxFilter;

        ImGui.Text("Whitelisted players (never hidden)");
        ImGui.TextDisabled("Whitelist and blacklist changes apply right away.");

        var whitelist = plugin.VfxFilter.Whitelist;
        if (whitelist.Length == 0)
            ImGui.TextDisabled("None. Add players from the VFX users list or by right-clicking them.");

        foreach (var entry in whitelist)
        {
            if (ImGui.SmallButton($"X##Whitelist{entry.Raw}"))
                plugin.SetWhitelisted(entry.Raw, false);

            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Raw);
        }

        if (ImGui.Button("Open Blacklist"))
            plugin.ToggleBlacklistUi();

        ImGui.Spacing();

        var redraw = config.RedrawOnListChange;
        if (ImGui.Checkbox("Redraw a player when their list status changes", ref redraw))
            config.RedrawOnListChange = redraw;
        Hint("Uses Penumbra to redraw the player when you blacklist, whitelist or un-list them, so their effects reload under the new rule. The player blinks out for a moment.");
    }

    private void DrawRules()
    {
        var config = mutable.VfxFilter;

        ImGui.Text("Additional rules (all off by default)");

        var advanced = config.HideEffects;
        if (ImGui.Checkbox("Use the rules below", ref advanced))
            config.HideEffects = advanced;
        Hint("Off leaves only the emote toggles in the main window and the blacklist active.");

        using (ImRaii.Disabled(!config.HideEffects))
        {
            var hideModded = config.HideModded;
            if (ImGui.Checkbox("Hide effects that are modded for that player", ref hideModded))
                config.HideModded = hideModded;
            Hint("Needs Penumbra. Vanilla effects are left alone.");

            DrawStringList("...but only for paths containing (empty = all modded effects)", "Scope", config.ModdedScopePatterns, ref newScopePattern, "vfx/emote");

            ImGui.Spacing();
            var hideMinion = config.HideModdedMinionEffects;
            if (ImGui.Checkbox("Hide modded effects from minions", ref hideMinion))
                config.HideModdedMinionEffects = hideMinion;
            Hint("Judged by the minion's owner. The minion itself stays visible.");

            DrawStringList("Always hide paths containing (modded or not)", "Block", config.BlockPatterns, ref newBlockPattern, "part of an .avfx path");

            ImGui.Spacing();
            var rateLimit = config.RateLimitPerSecond;
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Max effects per player per second", ref rateLimit))
                config.RateLimitPerSecond = Math.Clamp(rateLimit, 0, 1000);
            Hint("Effects beyond this are hidden. 0 turns the limit off.");
        }
    }

    private void DrawAppearance()
    {
        var useTheme = mutable.UseTheme;
        if (ImGui.Checkbox("Use dark theme", ref useTheme))
            mutable.UseTheme = useTheme;
        Hint("Off uses the standard Dalamud style.");

        using (ImRaii.Disabled(!mutable.UseTheme))
        {
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            using var combo = ImRaii.Combo("Color scheme", Theme.SchemeName(mutable.ThemeScheme));
            if (combo.Success)
            {
                foreach (var scheme in Enum.GetValues<ThemeScheme>())
                {
                    if (ImGui.Selectable(Theme.SchemeName(scheme), scheme == mutable.ThemeScheme))
                        mutable.ThemeScheme = scheme;
                }
            }
        }
    }

    private void DrawDebug()
    {
        var config = mutable.VfxFilter;
        var module = plugin.VfxFilter;

        if (ImGui.Button("Open Spawn Log"))
            plugin.ToggleVfxFilterUi();

        var includeSelf = config.IncludeSelfForTesting;
        if (ImGui.Checkbox("Also apply everything to my own character", ref includeSelf))
            config.IncludeSelfForTesting = includeSelf;

        var logToPluginLog = config.LogToPluginLog;
        if (ImGui.Checkbox("Also write spawns to /xllog", ref logToPluginLog))
            config.LogToPluginLog = logToPluginLog;
        Hint("Writes a line to dalamud.log on disk for every effect spawned. Turn it off when you are done debugging.");

        ImGui.Spacing();
        ImGui.TextDisabled($"Game version: {(module.GameVersion.Length > 0 ? module.GameVersion : "unknown")} (verified on {VfxFilterModule.VerifiedGameVersion})");
        ImGui.TextDisabled($"Hooks: {(module.IsHooked ? "active" : "off")} | signatures trusted: {(module.SignaturesTrusted ? "yes" : "no")}");
        ImGui.TextDisabled($"Spawns seen: {module.SpawnsSeen} | modded: {module.ModdedSpawnsSeen} | hidden: {module.TotalHidden}");
        ImGui.TextDisabled($"Penumbra file events: {module.PenumbraEvents} | vfx files: {module.PenumbraVfxFiles} | modded: {module.PenumbraModdedFiles} | matched to another player: {module.PenumbraFilesMatched}");
    }

    private static void Hint(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static void DrawStringList(string label, string id, List<string> entries, ref string input, string hint)
    {
        ImGui.Spacing();
        ImGui.Text(label);

        for (var i = 0; i < entries.Count; i++)
        {
            if (ImGui.SmallButton($"X##{id}{i}"))
            {
                entries.RemoveAt(i);
                break;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(entries[i]);
        }

        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint($"##{id}Input", hint, ref input, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        submitted |= ImGui.Button($"Add##{id}");

        if (submitted && input.Trim().Length > 0)
        {
            entries.Add(input.Trim());
            input = string.Empty;
        }
    }
}
