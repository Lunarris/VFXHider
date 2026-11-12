using Dalamud.Configuration;
using System;
using VFXHider.VfxFilter;

namespace VFXHider;

public enum ThemeScheme
{
    Midnight,
    Slate,
    Ember,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public VfxFilterConfig VfxFilter { get; set; } = new();

    public bool UseTheme { get; set; } = false;

    public ThemeScheme ThemeScheme { get; set; } = ThemeScheme.Midnight;

    public void CopySettingsFrom(Configuration other)
    {
        UseTheme = other.UseTheme;
        ThemeScheme = other.ThemeScheme;
        VfxFilter.CopySettingsFrom(other.VfxFilter);
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
