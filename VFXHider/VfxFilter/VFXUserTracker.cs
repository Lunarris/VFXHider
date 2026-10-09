using System;
using System.Collections.Generic;

namespace VFXHider.VfxFilter;

public enum VFXUse
{
    VfxSpawn,

    VfxFile,
}

public sealed class VFXUser
{
    public string Name { get; init; } = string.Empty;
    public int VfxCount { get; set; }
    public int VfxFileCount { get; set; }
    public DateTime LastVfxSeen { get; set; }
    public string LastVfx { get; set; } = string.Empty;

    public int HiddenCount { get; set; }
    public DateTime LastHidden { get; set; }
}

public sealed class VFXUserTracker
{
    private readonly Dictionary<string, VFXUser> users = new(StringComparer.OrdinalIgnoreCase);

    public void Note(string name, VFXUse use, string path)
    {
        if (name.Length == 0)
            return;

        lock (users)
        {
            var user = GetOrAdd(name);
            user.LastVfxSeen = DateTime.Now;
            user.LastVfx = path;

            if (use == VFXUse.VfxFile)
                user.VfxFileCount++;
            else
                user.VfxCount++;
        }
    }

    public void NoteHidden(string name)
    {
        if (name.Length == 0)
            return;

        lock (users)
        {
            var user = GetOrAdd(name);
            user.HiddenCount++;
            user.LastHidden = DateTime.Now;
        }
    }

    private VFXUser GetOrAdd(string name)
    {
        if (!users.TryGetValue(name, out var user))
            users[name] = user = new VFXUser { Name = name };

        return user;
    }

    public void Clear()
    {
        lock (users)
            users.Clear();
    }

    public void CopyTo(List<VFXUser> destination)
    {
        destination.Clear();
        lock (users)
            destination.AddRange(users.Values);
    }
}
