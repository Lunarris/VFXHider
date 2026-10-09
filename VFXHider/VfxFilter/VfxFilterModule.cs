using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InteropGenerator.Runtime;
using Emote = Lumina.Excel.Sheets.Emote;
using World = Lumina.Excel.Sheets.World;

namespace VFXHider.VfxFilter;

public sealed unsafe class VfxFilterModule : IDisposable
{
    public const int LogCapacity = 500;

    private const string ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    private const string StaticVfxRemoveSig = "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";

    public const string VerifiedGameVersion = "2026.09.15.0000.0000";

    private delegate nint ActorVfxCreateDelegate(byte* path, GameObject* caster, GameObject* target, float a4, byte a5, ushort a6, byte a7);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    private sealed class LiveStaticVfx
    {
        public required VfxSpawnRecord Record;
        public nint VTable;
        public bool Pending = true;
        public bool HiddenByUs;

        public nint PenumbraObject;
        public string PenumbraPath = string.Empty;
        public string PenumbraVia = string.Empty;

        public FFXIVClientStructs.FFXIV.Common.Math.Vector4 OriginalColor;
        public FFXIVClientStructs.FFXIV.Common.Math.Vector3 OriginalScale;
    }

    private readonly VfxFilterConfig config;
    private readonly ICallGateSubscriber<string, int, string> penumbraResolveGameObjectPath;

    private Hook<ActorVfxCreateDelegate>? actorVfxCreateHook;
    private Hook<VfxObject.Delegates.Create>? staticVfxCreateHook;
    private Hook<StaticVfxRemoveDelegate>? staticVfxRemoveHook;

    private readonly Dictionary<nint, LiveStaticVfx> liveStatic = [];
    private readonly List<nint> staleScratch = [];
    private bool filterDirty;

    private const int CharacterSlotEnd = 200;


    private readonly ConcurrentDictionary<string, bool> gameFileExists = new(StringComparer.OrdinalIgnoreCase);

    private const long RecentResolveMs = 2000;
    private readonly (long Ticks, int Thread, nint GameObject, string GamePath, string LocalPath)[] recentResolves = new (long, int, nint, string, string)[64];
    private int nextRecentResolve;

    private readonly Dictionary<uint, (long Second, int Count)> spawnRates = [];

    private readonly ICallGateSubscriber<nint, string, string, object?> penumbraResourceResolved;
    private readonly ICallGateSubscriber<int, int, object?> penumbraRedrawObject;

    private readonly ConcurrentQueue<(nint GameObject, string GamePath, string LocalPath)> resolvedFiles = new();

    public VfxSpawnLog SpawnLog { get; } = new(LogCapacity);

    public VFXUserTracker VFXUsers { get; } = new();

    private long penumbraEvents;
    private long penumbraModdedFiles;
    private long penumbraVfxFiles;

    public long PenumbraVfxFiles => Interlocked.Read(ref penumbraVfxFiles);

    public long PenumbraEvents => Interlocked.Read(ref penumbraEvents);

    public long PenumbraModdedFiles => Interlocked.Read(ref penumbraModdedFiles);

    public long PenumbraFilesMatched { get; private set; }

    public long ModdedSpawnsSeen { get; private set; }

    public long SpawnsSeen { get; private set; }

    public bool CaptureSpawnLog { get; set; }

    private const long ModStateCacheMs = 2000;
    private const int ModStateCacheLimit = 2048;
    private readonly Dictionary<(int ObjectIndex, uint EntityId, string Path), (long Time, VfxModState State, string ResolvedPath)> modStateCache = [];

    private const float HiddenScale = 0.0001f;

    private static readonly string[] ProtectedPathPatterns = ["vfx/common/eff/pop_"];

    public static readonly string[] PresetEmoteNames = ["Dote", "Love Heart", "Throw", "Pet", "Blow Kiss"];

    public Dictionary<string, HashSet<uint>> PresetEmoteIds { get; } = [];

    private bool IsBlockedPresetEmote(uint emoteId)
    {
        foreach (var name in config.BlockedPresetEmotes)
        {
            if (PresetEmoteIds.TryGetValue(name, out var ids) && ids.Contains(emoteId))
                return true;
        }

        return false;
    }

    public Dictionary<uint, string> EmoteNames { get; } = [];

    private static readonly Dictionary<uint, string> WorldNames = [];

    public long TotalHidden { get; private set; }

    public bool IsHooked => actorVfxCreateHook != null || staticVfxCreateHook != null;

    public string GameVersion { get; } = ReadGameVersion();

    public bool SignaturesTrusted
        => GameVersion.Length > 0 && (GameVersion == VerifiedGameVersion || GameVersion == config.ConfirmedGameVersion);

    private static string ReadGameVersion()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
            return directory == null ? string.Empty : System.IO.File.ReadAllText(System.IO.Path.Combine(directory, "ffxivgame.ver")).Trim();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "VfxFilter: could not read the game version.");
            return string.Empty;
        }
    }

    public void RefreshHooks()
    {
        if (!config.Enabled)
            return;

        Disable();
        Enable();
    }

    public VfxFilterModule(VfxFilterConfig config)
    {
        this.config = config;

        penumbraResolveGameObjectPath = Plugin.PluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath");

        penumbraResourceResolved = Plugin.PluginInterface.GetIpcSubscriber<nint, string, string, object?>("Penumbra.GameObjectResourcePathResolved");
        penumbraRedrawObject = Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");

        if (WorldNames.Count == 0)
        {
            foreach (var world in Plugin.DataManager.GetExcelSheet<World>())
            {
                var worldName = world.Name.ExtractText();
                if (worldName.Length > 0)
                    WorldNames[world.RowId] = worldName;
            }
        }

        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            var name = emote.Name.ExtractText();
            if (name.Length > 0)
                EmoteNames[emote.RowId] = name;
        }

        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>(ClientLanguage.English))
        {
            var english = emote.Name.ExtractText();
            var preset = Array.Find(PresetEmoteNames, name => name.Equals(english, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                continue;

            if (!PresetEmoteIds.TryGetValue(preset, out var ids))
                PresetEmoteIds[preset] = ids = [];

            ids.Add(emote.RowId);
        }

        if (config.Enabled)
            Enable();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
            Enable();
        else
            Disable();
    }

    public void OnFilterSettingsChanged()
    {
        filterDirty = true;
        modStateCache.Clear();
    }

    private static string WorldOf(GameObject* obj)
        => obj != null && obj->ObjectKind == ObjectKind.Pc && WorldNames.TryGetValue(((Character*)obj)->HomeWorld, out var world)
            ? world
            : string.Empty;

    private static bool IsPlayer(GameObject* obj, string key)
        => obj != null && obj->ObjectKind == ObjectKind.Pc && PlayerKey.Matches(key, obj->NameString, WorldOf(obj));

    public bool IsPlayerNearby(string key)
    {
        var objects = GameObjectManager.Instance()->Objects.IndexSorted;
        for (var i = 0; i < CharacterSlotEnd; i += 2)
        {
            if (IsPlayer(objects[i].Value, key))
                return true;
        }

        return false;
    }

    public bool RedrawPlayer(string key)
    {
        if (!IsHooked)
            return false;

        var objects = GameObjectManager.Instance()->Objects.IndexSorted;
        for (var i = 0; i < CharacterSlotEnd; i += 2)
        {
            if (!IsPlayer(objects[i].Value, key))
                continue;

            try
            {
                penumbraRedrawObject.InvokeAction(i, 0);
                return true;
            }
            catch (IpcError)
            {
                return false;
            }
        }

        return false;
    }

    private void Enable()
    {
        if (IsHooked)
            return;

        try
        {
            var trusted = SignaturesTrusted;
            if (!trusted)
                Plugin.Log.Warning($"VfxFilter: game version is \"{GameVersion}\" but the signatures were verified on {VerifiedGameVersion}. Actor VFX and effect hiding stay off until confirmed in the main window.");

            if (trusted)
            {
                if (Plugin.SigScanner.TryScanText(ActorVfxCreateSig, out var actorCreate))
                    actorVfxCreateHook = Plugin.GameInterop.HookFromAddress<ActorVfxCreateDelegate>(actorCreate, ActorVfxCreateDetour);
                else
                    Plugin.Log.Error("VfxFilter: actor VFX create signature not found, actor VFX will not be handled.");
            }

            var staticCreate = (nint)VfxObject.Addresses.Create.Value;
            if (staticCreate != nint.Zero)
                staticVfxCreateHook = Plugin.GameInterop.HookFromAddress<VfxObject.Delegates.Create>(staticCreate, StaticVfxCreateDetour);
            else
                Plugin.Log.Error("VfxFilter: VfxObject.Create is unresolved, static VFX will not be handled.");

            if (staticVfxCreateHook != null && trusted)
            {
                if (Plugin.SigScanner.TryScanText(StaticVfxRemoveSig, out var staticRemove))
                    staticVfxRemoveHook = Plugin.GameInterop.HookFromAddress<StaticVfxRemoveDelegate>(staticRemove, StaticVfxRemoveDetour);
                else
                    Plugin.Log.Warning("VfxFilter: static VFX remove signature not found, static VFX will be logged without source/target and never hidden.");
            }

            Plugin.Framework.Update += OnFrameworkUpdate;
            penumbraResourceResolved.Subscribe(OnPenumbraResourceResolved);

            staticVfxRemoveHook?.Enable();
            staticVfxCreateHook?.Enable();
            actorVfxCreateHook?.Enable();

            Plugin.Log.Information($"VfxFilter: hooks enabled (actor create {Describe(actorVfxCreateHook)}, static create {Describe(staticVfxCreateHook)}, static remove {Describe(staticVfxRemoveHook)}).");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "VfxFilter: failed to set up hooks.");
            Disable();
        }
    }

    private void Disable()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        penumbraResourceResolved.Unsubscribe(OnPenumbraResourceResolved);
        resolvedFiles.Clear();

        var wasHooked = IsHooked;

        actorVfxCreateHook?.Dispose();
        actorVfxCreateHook = null;
        staticVfxCreateHook?.Dispose();
        staticVfxCreateHook = null;
        staticVfxRemoveHook?.Dispose();
        staticVfxRemoveHook = null;

        lock (liveStatic)
        {
            foreach (var (address, live) in liveStatic)
            {
                if (live.HiddenByUs && IsStillAlive(address, live))
                    Show((VfxObject*)address, live);
            }

            liveStatic.Clear();
        }

        lock (spawnRates)
            spawnRates.Clear();

        modStateCache.Clear();


        if (wasHooked)
            Plugin.Log.Information("VfxFilter: hooks disposed.");
    }

    public void Dispose() => Disable();

    private static string Describe<T>(Hook<T>? hook) where T : Delegate
        => hook == null ? "missing" : $"0x{hook.Address:X}";

    private nint ActorVfxCreateDetour(byte* path, GameObject* caster, GameObject* target, float a4, byte a5, ushort a6, byte a7)
    {
        VfxSpawnRecord? record = null;

        try
        {
            record = new VfxSpawnRecord
            {
                Time = DateTime.Now,
                Kind = VfxSpawnKind.Actor,
                Path = Marshal.PtrToStringUTF8((nint)path) ?? string.Empty,
                Source = Resolve(caster),
                Target = Resolve(target),
            };

            Classify(record);
            record.HideReason = Evaluate(record);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "VfxFilter: error while inspecting actor VFX.");
        }

        if (record is { HideReason.Length: > 0 })
        {
            TotalHidden++;
            Record(record);
            return nint.Zero;
        }

        var vfx = actorVfxCreateHook!.Original(path, caster, target, a4, a5, a6, a7);

        if (record != null)
        {
            record.Address = vfx;
            Record(record);
        }

        return vfx;
    }

    private VfxObject* StaticVfxCreateDetour(CStringPointer path, CStringPointer pool)
    {
        var started = Stopwatch.GetTimestamp();
        var vfx = staticVfxCreateHook!.Original(path, pool);

        try
        {
            var record = new VfxSpawnRecord
            {
                Time = DateTime.Now,
                Kind = VfxSpawnKind.Static,
                Path = Marshal.PtrToStringUTF8((nint)(byte*)path) ?? string.Empty,
                Address = (nint)vfx,
            };

            if (vfx != null && staticVfxRemoveHook != null)
            {
                lock (liveStatic)
                {
                    var live = new LiveStaticVfx { Record = record, VTable = *(nint*)vfx };
                    FindPenumbraOwner(record.Path, started, live);
                    liveStatic[(nint)vfx] = live;
                }
            }
            else
            {
                Record(record);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "VfxFilter: error while logging static VFX.");
        }

        return vfx;
    }

    private nint StaticVfxRemoveDetour(VfxObject* vfx)
    {
        try
        {
            LiveStaticVfx? live;
            lock (liveStatic)
                liveStatic.Remove((nint)vfx, out live);

            if (live is { Pending: true })
            {
                ResolveStatic(vfx, live);
                Record(live.Record);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "VfxFilter: error while handling static VFX removal.");
        }

        return staticVfxRemoveHook!.Original(vfx);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            DrainResolvedFiles();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "VfxFilter: error in per-frame update.");
        }

        lock (liveStatic)
        {
            if (liveStatic.Count == 0)
            {
                filterDirty = false;
                return;
            }

            var reevaluate = filterDirty;
            filterDirty = false;

            foreach (var (address, live) in liveStatic)
            {
                if (!IsStillAlive(address, live))
                {
                    staleScratch.Add(address);
                    if (live.Pending)
                        Record(live.Record);

                    continue;
                }

                var vfx = (VfxObject*)address;
                var record = live.Record;

                if (live.Pending)
                {
                    live.Pending = false;
                    ResolveStatic(vfx, live);
                    record.HideReason = Evaluate(record);
                    if (record.HideReason.Length > 0)
                        TotalHidden++;

                    Record(record);
                }
                else if (reevaluate)
                {
                    record.HideReason = Evaluate(record);
                }

                if (record.HideReason.Length > 0)
                    Hide(vfx, live);
                else if (live.HiddenByUs)
                    Show(vfx, live);
            }

            foreach (var address in staleScratch)
                liveStatic.Remove(address);

            staleScratch.Clear();
        }
    }


    private void OnPenumbraResourceResolved(nint gameObject, string gamePath, string localPath)
    {
        Interlocked.Increment(ref penumbraEvents);

        if (!gamePath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
            return;

        Interlocked.Increment(ref penumbraVfxFiles);

        if (gameObject == nint.Zero || resolvedFiles.Count >= 4096)
            return;

        if (localPath.Length > 0 && localPath[0] == '|')
        {
            var end = localPath.IndexOf('|', 1);
            if (end > 0)
                localPath = localPath[(end + 1)..];
        }

        if (localPath.Length == 0 || string.Equals(localPath.Replace('\\', '/'), gamePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return;

        Interlocked.Increment(ref penumbraModdedFiles);
        resolvedFiles.Enqueue((gameObject, gamePath, localPath));

        lock (recentResolves)
        {
            recentResolves[nextRecentResolve] = (Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, gameObject, gamePath, localPath);
            nextRecentResolve = (nextRecentResolve + 1) % recentResolves.Length;
        }
    }

    private void FindPenumbraOwner(string path, long started, LiveStaticVfx live)
    {
        if (path.Length == 0)
            return;

        (long Ticks, int Thread, nint GameObject, string GamePath, string LocalPath) best = default;
        var bestDuring = false;
        var earliest = started - (RecentResolveMs * Stopwatch.Frequency / 1000);
        var thread = Environment.CurrentManagedThreadId;

        lock (recentResolves)
        {
            foreach (var resolve in recentResolves)
            {
                if (resolve.GamePath == null || resolve.Ticks < earliest
                    || !string.Equals(resolve.GamePath.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    continue;

                var during = resolve.Ticks >= started && resolve.Thread == thread;
                if (best.GamePath == null || (during && !bestDuring) || (during == bestDuring && resolve.Ticks > best.Ticks))
                {
                    best = resolve;
                    bestDuring = during;
                }
            }
        }

        if (best.GamePath == null)
            return;

        live.PenumbraObject = best.GameObject;
        live.PenumbraPath = best.LocalPath ?? string.Empty;
        live.PenumbraVia = bestDuring
            ? "Penumbra, during create"
            : $"Penumbra, {Math.Max(0, (started - best.Ticks) * 1000 / Stopwatch.Frequency)} ms before";
    }

    private void DrainResolvedFiles()
    {
        if (resolvedFiles.IsEmpty)
            return;

        var objects = GameObjectManager.Instance()->Objects.IndexSorted;

        while (resolvedFiles.TryDequeue(out var file))
        {
            var slot = -1;
            for (var i = 0; i < CharacterSlotEnd; i++)
            {
                if ((nint)objects[i].Value == file.GameObject)
                {
                    slot = i;
                    break;
                }
            }

            if (slot < 0)
                continue;

            var source = Resolve(objects[slot].Value);
            var owner = ResolveMinionOwner(source);
            var player = owner.IsNone ? source : owner;
            if (player.Kind != ObjectKind.Pc || (player.IsLocalPlayer && !config.IncludeSelfForTesting))
                continue;

            PenumbraFilesMatched++;
            VFXUsers.Note(player.Key, VFXUse.VfxFile, file.GamePath);
        }
    }

    private static void Hide(VfxObject* vfx, LiveStaticVfx live)
    {
        if (!live.HiddenByUs)
        {
            live.OriginalColor = vfx->Color;
            live.OriginalScale = vfx->Scale;
            live.HiddenByUs = true;
        }

        var color = vfx->Color;
        color.W = 0;
        vfx->Color = color;
        vfx->IsVisible = false;

        vfx->Scale = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(HiddenScale, HiddenScale, HiddenScale);
        vfx->IsTransformChanged = true;
    }

    private static void Show(VfxObject* vfx, LiveStaticVfx live)
    {
        vfx->Color = live.OriginalColor;
        vfx->Scale = live.OriginalScale;
        vfx->IsTransformChanged = true;
        vfx->IsVisible = true;
        live.HiddenByUs = false;
    }

    private static bool IsStillAlive(nint address, LiveStaticVfx live) => *(nint*)address == live.VTable;

    private void ResolveStatic(VfxObject* vfx, LiveStaticVfx live)
    {
        var record = live.Record;
        record.Source = Resolve(vfx->StaticCaster);
        record.Target = Resolve(vfx->StaticTarget);

        if (record.Source.IsNone && record.Target.IsNone && live.PenumbraObject != nint.Zero)
        {
            var objects = GameObjectManager.Instance()->Objects.IndexSorted;
            for (var i = 0; i < CharacterSlotEnd; i++)
            {
                if ((nint)objects[i].Value != live.PenumbraObject)
                    continue;

                record.Source = Resolve(objects[i].Value);
                record.SourceVia = live.PenumbraVia;
                break;
            }
        }

        Classify(record);

        if (record.SourceVia.Length > 0 && record.ModState != VfxModState.Modded)
        {
            record.ModState = VfxModState.Modded;
            record.ResolvedPath = live.PenumbraPath;
        }
    }

    private void Record(VfxSpawnRecord record)
    {
        SpawnsSeen++;
        if (CaptureSpawnLog)
            SpawnLog.Add(record);

        var player = record.Owner.IsNone ? record.Source : record.Owner;
        if (record.HideReason.Length > 0 && player.Kind == ObjectKind.Pc)
            VFXUsers.NoteHidden(player.Key);

        if (record.ModState == VfxModState.Modded)
        {
            ModdedSpawnsSeen++;
            if (player.Kind == ObjectKind.Pc && (!player.IsLocalPlayer || config.IncludeSelfForTesting))
                VFXUsers.Note(player.Key, VFXUse.VfxSpawn, record.Path);
        }

        if (config.LogToPluginLog)
        {
            var hidden = record.HideReason.Length > 0 ? $" | HIDDEN ({record.HideReason})" : string.Empty;
            Plugin.Log.Information($"VfxFilter: [{record.Kind}] {record.Path} | src: {record.SourceText} | tgt: {record.Target} | {record.ModState} {record.ResolvedPath}{hidden}");
        }
    }

    private void Classify(VfxSpawnRecord record)
    {
        record.Owner = ResolveMinionOwner(record.Source);
        ResolveModState(record);
        record.SourceRate = CountSpawn(record.Source);

        if (record.Source.EmoteId != 0)
            record.EmoteName = EmoteNames.TryGetValue(record.Source.EmoteId, out var emote) ? emote : $"#{record.Source.EmoteId}";
    }

    private string Evaluate(VfxSpawnRecord record)
    {
        var viaMinion = record.Source.Kind == ObjectKind.Companion;
        var source = viaMinion ? record.Owner : record.Source;

        foreach (var pattern in ProtectedPathPatterns)
        {
            if (record.Path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
        }

        var blamed = source.Kind == ObjectKind.Pc ? source : record.Target;
        if (record.ModState == VfxModState.Modded && blamed.Kind == ObjectKind.Pc && !blamed.IsLocalPlayer
            && PlayerKey.IsListed(config.Blacklist, blamed.Name, blamed.World))
            return "blacklist";

        if (source.Kind != ObjectKind.Pc)
            return string.Empty;

        if (source.IsLocalPlayer && !config.IncludeSelfForTesting)
            return string.Empty;

        if (!source.IsLocalPlayer && config.ExemptPartyAndFriends && (source.IsPartyMember || source.IsFriend))
            return string.Empty;

        if (PlayerKey.IsListed(config.Whitelist, source.Name, source.World))
            return string.Empty;

        if (!viaMinion && record.ModState == VfxModState.Modded && source.EmoteId != 0 && IsBlockedPresetEmote(source.EmoteId))
            return "emote";

        if (!config.HideEffects)
            return string.Empty;

        if (MatchesAny(config.BlockPatterns, record.Path))
            return "blocklist";

        if (viaMinion)
            return config.HideModdedMinionEffects && record.ModState == VfxModState.Modded ? "minion" : string.Empty;

        if (config.HideModded && record.ModState == VfxModState.Modded
            && (config.ModdedScopePatterns.Count == 0 || MatchesAny(config.ModdedScopePatterns, record.Path)))
            return "modded";

        if (config.RateLimitPerSecond > 0 && record.SourceRate > config.RateLimitPerSecond)
            return "rate limit";

        return string.Empty;
    }

    private static bool MatchesAny(List<string> patterns, string path)
    {
        foreach (var pattern in patterns)
        {
            var trimmed = pattern.Trim();
            if (trimmed.Length > 0 && path.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private int CountSpawn(VfxActorInfo source)
    {
        if (source.EntityId is 0 or 0xE0000000)
            return 0;

        var second = Environment.TickCount64 / 1000;
        lock (spawnRates)
        {
            if (spawnRates.Count > 2048)
                spawnRates.Clear();

            var count = spawnRates.TryGetValue(source.EntityId, out var rate) && rate.Second == second ? rate.Count + 1 : 1;
            spawnRates[source.EntityId] = (second, count);
            return count;
        }
    }

    private void ResolveModState(VfxSpawnRecord record)
    {
        var actor = record.Source.ObjectIndex >= 0 ? record.Source : record.Target;
        if (actor.ObjectIndex < 0 || record.Path.Length == 0 || !Plugin.Framework.IsInFrameworkUpdateThread)
            return;

        var key = (actor.ObjectIndex, actor.EntityId, record.Path);
        var now = Environment.TickCount64;
        if (modStateCache.TryGetValue(key, out var cached) && now - cached.Time < ModStateCacheMs)
        {
            record.ModState = cached.State;
            record.ResolvedPath = cached.ResolvedPath;
            return;
        }

        try
        {
            var resolved = penumbraResolveGameObjectPath.InvokeFunc(record.Path, actor.ObjectIndex);
            if (string.Equals(resolved, record.Path, StringComparison.OrdinalIgnoreCase))
            {
                var exists = gameFileExists.GetOrAdd(record.Path, static path => Plugin.DataManager.FileExists(path));
                record.ModState = exists ? VfxModState.Vanilla : VfxModState.Missing;
            }
            else
            {
                record.ModState = VfxModState.Modded;
                record.ResolvedPath = resolved;
            }
        }
        catch (IpcError)
        {
            return;
        }

        if (modStateCache.Count >= ModStateCacheLimit)
            modStateCache.Clear();

        modStateCache[key] = (now, record.ModState, record.ResolvedPath);
    }

    private static VfxActorInfo Resolve(GameObject* obj)
    {
        if (obj == null)
            return VfxActorInfo.None;

        var entityId = obj->EntityId;
        var isLocal = obj == (GameObject*)Control.GetLocalPlayer();

        var isParty = false;
        var groupManager = GroupManager.Instance();
        if (!isLocal && groupManager != null && entityId != 0 && entityId != 0xE0000000)
            isParty = groupManager->MainGroup.IsEntityIdInParty(entityId);

        var isPlayer = obj->ObjectKind == ObjectKind.Pc;
        var isFriend = isPlayer && ((Character*)obj)->IsFriend;
        var emoteId = isPlayer ? ((Character*)obj)->EmoteController.EmoteId : (ushort)0;

        return new VfxActorInfo(obj->NameString, obj->ObjectKind, entityId, isLocal, isParty, isFriend, obj->ObjectIndex, emoteId, WorldOf(obj));
    }

    private static VfxActorInfo ResolveMinionOwner(VfxActorInfo source)
    {
        if (source.Kind != ObjectKind.Companion || source.ObjectIndex is < 1 or >= 200 || source.ObjectIndex % 2 == 0)
            return VfxActorInfo.None;

        return Resolve(GameObjectManager.Instance()->Objects.IndexSorted[source.ObjectIndex - 1].Value);
    }

    private static VfxActorInfo Resolve(int id)
    {
        if (id <= 0)
            return VfxActorInfo.None;

        var obj = GameObjectManager.Instance()->Objects.GetObjectByEntityId((uint)id);
        return obj != null ? Resolve(obj) : new VfxActorInfo(string.Empty, ObjectKind.None, (uint)id, false, false);
    }
}
