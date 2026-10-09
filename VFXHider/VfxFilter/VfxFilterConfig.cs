using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VFXHider.VfxFilter;

[Serializable]
public class VfxFilterConfig
{
    public bool Enabled { get; set; } = true;

    public bool LogToPluginLog { get; set; } = false;

    public bool HideEffects { get; set; } = false;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> BlockedPresetEmotes { get; set; } = [.. VfxFilterModule.PresetEmoteNames];

    public bool ExemptPartyAndFriends { get; set; } = true;

    public List<string> Whitelist { get; set; } = [];

    public List<string> Blacklist { get; set; } = [];

    public bool HideModded { get; set; } = false;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> ModdedScopePatterns { get; set; } = ["vfx/emote"];

    public bool HideModdedMinionEffects { get; set; } = false;

    public List<string> BlockPatterns { get; set; } = [];

    public int RateLimitPerSecond { get; set; } = 0;

    public bool RedrawOnListChange { get; set; } = true;

    public string ConfirmedGameVersion { get; set; } = string.Empty;

    public bool IncludeSelfForTesting { get; set; } = false;
}
