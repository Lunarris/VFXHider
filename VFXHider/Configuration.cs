using Dalamud.Configuration;
using System;
using VFXHider.VfxFilter;

namespace VFXHider;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public VfxFilterConfig VfxFilter { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
