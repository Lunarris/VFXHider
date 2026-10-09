using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace VFXHider.VfxFilter;

public enum VfxSpawnKind
{
    Actor,
    Static,
}

public enum VfxModState
{
    Unknown,
    Vanilla,
    Modded,

    Missing,
}

public readonly record struct VfxActorInfo(string Name, ObjectKind Kind, uint EntityId, bool IsLocalPlayer, bool IsPartyMember, bool IsFriend = false, int ObjectIndex = -1, ushort EmoteId = 0, string World = "")
{
    public static readonly VfxActorInfo None = new(string.Empty, ObjectKind.None, 0, false, false);

    public bool IsNone => EntityId == 0 && Name.Length == 0;

    public string Key => PlayerKey.Make(Name, World);

    public override string ToString()
    {
        if (IsNone)
            return "-";

        var flags = (IsLocalPlayer ? " [self]" : string.Empty) + (IsPartyMember ? " [party]" : string.Empty) + (IsFriend ? " [friend]" : string.Empty);
        return $"{(Name.Length == 0 ? "?" : Key)} ({Kind}, {EntityId:X8}){flags}";
    }
}

public sealed class VfxSpawnRecord
{
    public DateTime Time { get; init; }
    public VfxSpawnKind Kind { get; init; }
    public string Path { get; init; } = string.Empty;
    public nint Address { get; set; }

    public VfxActorInfo Source { get; set; } = VfxActorInfo.None;
    public VfxActorInfo Target { get; set; } = VfxActorInfo.None;

    public VfxActorInfo Owner { get; set; } = VfxActorInfo.None;

    public string SourceVia { get; set; } = string.Empty;

    public string SourceText
    {
        get
        {
            var text = Owner.IsNone ? Source.ToString() : $"{Source} of {Owner}";
            return SourceVia.Length == 0 ? text : $"{text} <via {SourceVia}>";
        }
    }

    public VfxModState ModState { get; set; } = VfxModState.Unknown;

    public string ResolvedPath { get; set; } = string.Empty;

    public string EmoteName { get; set; } = string.Empty;

    public int SourceRate { get; set; }

    public string HideReason { get; set; } = string.Empty;

    public override string ToString() => $"{Time:HH:mm:ss.fff}\t{Kind}\t{Path}\t{SourceText}\t{Target}\t{EmoteName}\t{ModState}\t{HideReason}\t{ResolvedPath}";
}

public sealed class VfxSpawnLog(int capacity)
{
    private readonly VfxSpawnRecord?[] buffer = new VfxSpawnRecord?[capacity];
    private readonly object sync = new();
    private int next;
    private int count;

    public long TotalSpawns { get; private set; }

    public void Add(VfxSpawnRecord record)
    {
        lock (sync)
        {
            buffer[next] = record;
            next = (next + 1) % buffer.Length;
            if (count < buffer.Length)
                count++;
            TotalSpawns++;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            Array.Clear(buffer);
            next = 0;
            count = 0;
            TotalSpawns = 0;
        }
    }

    public void CopyTo(List<VfxSpawnRecord> destination)
    {
        destination.Clear();
        lock (sync)
        {
            var start = (next - count + buffer.Length) % buffer.Length;
            for (var i = 0; i < count; i++)
                destination.Add(buffer[(start + i) % buffer.Length]!);
        }
    }
}
