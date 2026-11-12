using System;
using System.Collections.Generic;

namespace VFXHider.VfxFilter;

public readonly record struct PlayerEntry(string Raw, string Name, string World)
{
    public bool Matches(string name, string world)
        => Name.Length > 0
            && Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (World.Length == 0 || world.Length == 0 || World.Equals(world, StringComparison.OrdinalIgnoreCase));
}

public static class PlayerKey
{
    public static PlayerEntry[] Parse(List<string> entries)
    {
        var parsed = new PlayerEntry[entries.Count];
        for (var i = 0; i < parsed.Length; i++)
        {
            var (name, world) = Split(entries[i]);
            parsed[i] = new PlayerEntry(entries[i].Trim(), name, world);
        }

        return parsed;
    }

    public static bool IsListed(PlayerEntry[] entries, string name, string world)
    {
        foreach (var entry in entries)
        {
            if (entry.Matches(name, world))
                return true;
        }

        return false;
    }

    public static string Make(string name, string world)
        => world.Length == 0 ? name : $"{name}@{world}";

    public static (string Name, string World) Split(string key)
    {
        var at = key.IndexOf('@');
        return at < 0 ? (key.Trim(), string.Empty) : (key[..at].Trim(), key[(at + 1)..].Trim());
    }

    public static bool Matches(string entry, string name, string world)
    {
        var (entryName, entryWorld) = Split(entry);
        return entryName.Length > 0
            && entryName.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (entryWorld.Length == 0 || world.Length == 0 || entryWorld.Equals(world, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Matches(string entry, string key)
    {
        var (name, world) = Split(key);
        return Matches(entry, name, world);
    }

    public static bool IsListed(List<string> entries, string name, string world)
    {
        foreach (var entry in entries)
        {
            if (Matches(entry, name, world))
                return true;
        }

        return false;
    }

    public static bool IsListed(List<string> entries, string key)
    {
        var (name, world) = Split(key);
        return IsListed(entries, name, world);
    }
}
