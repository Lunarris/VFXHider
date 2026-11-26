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
    private const string PlaySoundSig = "F3 0F 11 54 24 ?? 55 56 57 41 56 41 57 48 81 EC ?? ?? ?? ?? 80 79 ?? 00 45 8B F9 48 8B F2 48 8B F9";
    public const int SoundRecordCapacity = 200;
    private const float MutedSoundRadius = 1f;

    public const string VerifiedGameVersion = "2026.11.03.0000.0000";

    private delegate nint ActorVfxCreateDelegate(byte* path, GameObject* caster, GameObject* target, float a4, byte a5, ushort a6, byte a7);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    private delegate nint PlaySoundDelegate(nint manager, nint data, float volume, int a4, float x, float y, float z, float a8,
        int a9, int a10, byte a11, int category, byte a13, int a14, byte a15, byte a16, byte a17, byte a18);

    private sealed class LiveStaticVfx
    {
        public required VfxSpawnRecord Record;
        public nint VTable;
        public bool Pending = true;
        public bool HiddenByUs;
        public bool RunBlocked;

        public nint PenumbraObject;
        public string PenumbraPath = string.Empty;
        public string PenumbraVia = string.Empty;

        public FFXIVClientStructs.FFXIV.Common.Math.Vector4 OriginalColor;
        public FFXIVClientStructs.FFXIV.Common.Math.Vector3 OriginalScale;
    }

    private sealed class MutedCharacter
    {
        public uint EntityId;
        public byte Category;
        public byte CategoryOverride;
        public bool Seen;
    }

    private readonly VfxFilterConfig config;
    private readonly ICallGateSubscriber<string, int, string> penumbraResolveGameObjectPath;

    private Hook<ActorVfxCreateDelegate>? actorVfxCreateHook;
    private Hook<VfxObject.Delegates.Create>? staticVfxCreateHook;
    private Hook<StaticVfxRemoveDelegate>? staticVfxRemoveHook;
    private Hook<VfxObject.Delegates.Update>? staticVfxRunHook;
    private Hook<PlaySoundDelegate>? playSoundHook;

    private readonly LinkedList<SoundRecord> soundRecords = [];

    private const int ModdedEmoteLimit = 2048;
    private readonly HashSet<(uint EntityId, ushort EmoteId)> moddedEmotes = [];

    private readonly Dictionary<nint, LiveStaticVfx> liveStatic = [];
    private readonly List<nint> staleScratch = [];
    private readonly List<nint> unblockScratch = [];
    private bool filterDirty;

    private const byte NoPlayCategory = (byte)FFXIVClientStructs.FFXIV.Client.Sound.SoundVolumeCategory.NoPlay;
    private readonly Dictionary<nint, MutedCharacter> mutedCharacters = [];
    private readonly List<nint> unmutedScratch = [];

    private const int CharacterSlotEnd = 200;


    private readonly ConcurrentDictionary<string, bool> gameFileExists = new(StringComparer.OrdinalIgnoreCase);

    private const long RecentResolveMs = 2000;
    private readonly (long Ticks, int Thread, nint GameObject, string GamePath, string LocalPath)[] recentResolves = new (long, int, nint, string, string)[64];
    private int nextRecentResolve;

    private readonly Dictionary<uint, (long Second, int Count)> spawnRates = [];

    private readonly Dictionary<nint, int> slotScratch = [];

    private readonly ErrorThrottle errors = new();

    public PlayerEntry[] Blacklist { get; private set; } = [];

    public PlayerEntry[] Whitelist { get; private set; } = [];

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

    public long RunsBlocked { get; private set; }

    public bool IsRunHooked => staticVfxRunHook != null;

    public long SoundsStopped { get; private set; }

    public bool RecordSounds { get; set; }

    public bool IsPlaySoundHooked => playSoundHook != null;

    public SoundRecord[] SoundRecords
    {
        get
        {
            lock (soundRecords)
                return [.. soundRecords];
        }
    }

    public void ClearSoundRecords()
    {
        lock (soundRecords)
            soundRecords.Clear();
    }

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
        catch (Exception)
        {
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
        ParseLists();

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
        ParseLists();
        filterDirty = true;
        modStateCache.Clear();
    }

    private void ParseLists()
    {
        Blacklist = PlayerKey.Parse(config.Blacklist);
        Whitelist = PlayerKey.Parse(config.Whitelist);
    }

    public bool IsBlacklisted(string key)
    {
        var (name, world) = PlayerKey.Split(key);
        return PlayerKey.IsListed(Blacklist, name, world);
    }

    public bool IsWhitelisted(string key)
    {
        var (name, world) = PlayerKey.Split(key);
        return PlayerKey.IsListed(Whitelist, name, world);
    }

    public void CollectNearbyPlayers(List<(string Name, string World)> destination)
    {
        destination.Clear();
        var objects = GameObjectManager.Instance()->Objects.IndexSorted;
        for (var i = 0; i < CharacterSlotEnd; i += 2)
        {
            var obj = objects[i].Value;
            if (obj != null && obj->ObjectKind == ObjectKind.Pc)
                destination.Add((obj->NameString, WorldOf(obj)));
        }
    }

    private static string WorldOf(GameObject* obj)
        => obj != null && obj->ObjectKind == ObjectKind.Pc && WorldNames.TryGetValue(((Character*)obj)->HomeWorld, out var world)
            ? world
            : string.Empty;

    private static bool IsPlayer(GameObject* obj, string key)
        => obj != null && obj->ObjectKind == ObjectKind.Pc && PlayerKey.Matches(key, obj->NameString, WorldOf(obj));

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
            if (!trusted && config.LogToPluginLog)
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
                else if (config.LogToPluginLog)
                    Plugin.Log.Warning("VfxFilter: static VFX remove signature not found, static VFX will be logged without source/target and never hidden.");
            }

            if (staticVfxRemoveHook != null)
            {
                var staticRun = (nint)VfxObject.Addresses.Update.Value;
                if (staticRun != nint.Zero)
                    staticVfxRunHook = Plugin.GameInterop.HookFromAddress<VfxObject.Delegates.Update>(staticRun, StaticVfxRunDetour);
            }

            if (trusted)
            {
                if (Plugin.SigScanner.TryScanText(PlaySoundSig, out var playSound))
                    playSoundHook = Plugin.GameInterop.HookFromAddress<PlaySoundDelegate>(playSound, PlaySoundDetour);
                else if (config.LogToPluginLog)
                    Plugin.Log.Warning("VfxFilter: sound play signature not found, emote sounds are muted once per frame only.");
            }

            Plugin.Framework.Update += OnFrameworkUpdate;
            penumbraResourceResolved.Subscribe(OnPenumbraResourceResolved);

            playSoundHook?.Enable();
            staticVfxRunHook?.Enable();
            staticVfxRemoveHook?.Enable();
            staticVfxCreateHook?.Enable();
            actorVfxCreateHook?.Enable();

            if (config.LogToPluginLog)
                Plugin.Log.Information($"VfxFilter: hooks enabled (actor create {Describe(actorVfxCreateHook)}, static create {Describe(staticVfxCreateHook)}, static run {Describe(staticVfxRunHook)}, static remove {Describe(staticVfxRemoveHook)}, sound play {Describe(playSoundHook)}).");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "VfxFilter: failed to set up hooks.");
            Disable();
        }
    }

    private void Disable()
    {
        playSoundHook?.Dispose();
        playSoundHook = null;
        ClearSoundRecords();

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
                if (!IsStillAlive(address, live))
                    continue;

                if (live.HiddenByUs)
                    Show((VfxObject*)address, live);

                if (live.RunBlocked)
                    Unblock((VfxObject*)address, live);
            }

            liveStatic.Clear();
        }

        staticVfxRunHook?.Dispose();
        staticVfxRunHook = null;

        lock (moddedEmotes)
            moddedEmotes.Clear();


        UnmuteAll();

        lock (spawnRates)
            spawnRates.Clear();

        modStateCache.Clear();


        if (wasHooked && config.LogToPluginLog)
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
            MuteEarly(record.Source);
        }
        catch (Exception ex)
        {
            errors.Error(ex, "VfxFilter: error while inspecting actor VFX.");
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
            errors.Error(ex, "VfxFilter: error while logging static VFX.");
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
            errors.Error(ex, "VfxFilter: error while handling static VFX removal.");
        }

        return staticVfxRemoveHook!.Original(vfx);
    }

    private void StaticVfxRunDetour(VfxObject* vfx, float deltaSeconds, int flags)
    {
        try
        {
            if (BlockRun(vfx))
                return;
        }
        catch (Exception ex)
        {
            errors.Error(ex, "VfxFilter: error while checking a static VFX before it runs.");
        }

        staticVfxRunHook!.Original(vfx, deltaSeconds, flags);
    }

    private bool BlockRun(VfxObject* vfx)
    {
        if (vfx == null || !Plugin.Framework.IsInFrameworkUpdateThread)
            return false;

        lock (liveStatic)
        {
            if (!liveStatic.TryGetValue((nint)vfx, out var live) || !IsStillAlive((nint)vfx, live))
                return false;

            var record = live.Record;
            if (live.Pending)
            {
                if (vfx->StaticCaster <= 0 && vfx->StaticTarget <= 0 && live.PenumbraObject == nint.Zero)
                    return false;

                live.Pending = false;
                ResolveStatic(vfx, live);
                record.HideReason = Evaluate(record);
                if (record.HideReason.Length > 0)
                    TotalHidden++;

                Record(record);
                MuteEarly(record.Source);
            }

            if (record.HideReason.Length == 0)
                return false;

            Hide(vfx, live);
            if (!live.RunBlocked)
                RunsBlocked++;

            live.RunBlocked = true;
            return true;
        }
    }

    private void Unblock(VfxObject* vfx, LiveStaticVfx live)
    {
        live.RunBlocked = false;
        staticVfxRunHook?.Original(vfx, 0, -1);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            DrainResolvedFiles();
        }
        catch (Exception ex)
        {
            errors.Error(ex, "VfxFilter: error in per-frame update.");
        }

        try
        {
            UpdateEmoteMutes();
        }
        catch (Exception ex)
        {
            errors.Error(ex, "VfxFilter: error while muting emote sounds.");
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
                {
                    Hide(vfx, live);
                }
                else
                {
                    if (live.HiddenByUs)
                        Show(vfx, live);

                    if (live.RunBlocked)
                        unblockScratch.Add(address);
                }
            }

            foreach (var address in staleScratch)
                liveStatic.Remove(address);

            staleScratch.Clear();

            foreach (var address in unblockScratch)
            {
                if (liveStatic.TryGetValue(address, out var live) && IsStillAlive(address, live))
                    Unblock((VfxObject*)address, live);
            }

            unblockScratch.Clear();
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

        slotScratch.Clear();
        for (var i = 0; i < CharacterSlotEnd; i++)
        {
            var obj = (nint)objects[i].Value;
            if (obj != nint.Zero)
                slotScratch.TryAdd(obj, i);
        }

        while (resolvedFiles.TryDequeue(out var file))
        {
            if (!slotScratch.TryGetValue(file.GameObject, out var slot))
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

    private void UpdateEmoteMutes()
    {
        foreach (var entry in mutedCharacters.Values)
            entry.Seen = false;

        var objects = GameObjectManager.Instance()->Objects.IndexSorted;
        for (var i = 0; i < CharacterSlotEnd; i += 2)
        {
            var obj = objects[i].Value;
            if (obj != null && obj->ObjectKind == ObjectKind.Pc)
                UpdateMute((Character*)obj);
        }

        foreach (var (address, entry) in mutedCharacters)
        {
            if (!entry.Seen)
                unmutedScratch.Add(address);
        }

        foreach (var address in unmutedScratch)
            mutedCharacters.Remove(address);

        unmutedScratch.Clear();
    }

    private void MuteEarly(VfxActorInfo source)
    {
        if (source.Kind != ObjectKind.Pc || source.EmoteId == 0 || source.ObjectIndex is < 0 or >= CharacterSlotEnd
            || !Plugin.Framework.IsInFrameworkUpdateThread)
            return;

        var obj = GameObjectManager.Instance()->Objects.IndexSorted[source.ObjectIndex].Value;
        if (obj != null && obj->ObjectKind == ObjectKind.Pc && obj->EntityId == source.EntityId)
            UpdateMute((Character*)obj);
    }

    private nint PlaySoundDetour(nint manager, nint data, float volume, int a4, float x, float y, float z, float a8,
        int a9, int a10, byte a11, int category, byte a13, int a14, byte a15, byte a16, byte a17, byte a18)
    {
        var stopped = false;
        try
        {
            if (category != NoPlayCategory && IsAtMutedPlayer(x, y, z))
            {
                category = NoPlayCategory;
                stopped = true;
                SoundsStopped++;
            }
        }
        catch (Exception ex)
        {
            errors.Error(ex, "VfxFilter: error while checking where a sound plays.");
        }

        if (RecordSounds)
        {
            try
            {
                RecordSound(data, category, x, y, z, stopped);
            }
            catch (Exception ex)
            {
                errors.Error(ex, "VfxFilter: error while recording a sound.");
            }
        }

        return playSoundHook!.Original(manager, data, volume, a4, x, y, z, a8, a9, a10, a11, category, a13, a14, a15, a16, a17, a18);
    }

    private bool IsAtMutedPlayer(float x, float y, float z)
    {
        if (mutedCharacters.Count == 0 || !Plugin.Framework.IsInFrameworkUpdateThread)
            return false;

        var objects = GameObjectManager.Instance()->Objects.IndexSorted;
        for (var i = 0; i < CharacterSlotEnd; i += 2)
        {
            var obj = objects[i].Value;
            if (obj == null || obj->ObjectKind != ObjectKind.Pc
                || !mutedCharacters.TryGetValue((nint)obj, out var muted) || muted.EntityId != obj->EntityId)
                continue;

            var dx = obj->Position.X - x;
            var dy = obj->Position.Y - y;
            var dz = obj->Position.Z - z;
            if (dx * dx + dy * dy + dz * dz <= MutedSoundRadius * MutedSoundRadius)
                return true;
        }

        return false;
    }

    private void RecordSound(nint data, int category, float x, float y, float z, bool stopped)
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread)
        {
            AddSoundRecord(new SoundRecord(DateTime.Now, category, stopped, "other thread", 0, data, x, y, z));
            return;
        }

        var nearest = string.Empty;
        var nearestDistance = float.MaxValue;
        var objects = GameObjectManager.Instance()->Objects.IndexSorted;
        for (var i = 0; i < CharacterSlotEnd; i += 2)
        {
            var obj = objects[i].Value;
            if (obj == null || obj->ObjectKind != ObjectKind.Pc)
                continue;

            var dx = obj->Position.X - x;
            var dy = obj->Position.Y - y;
            var dz = obj->Position.Z - z;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = DescribeObject(obj);
            }
        }

        AddSoundRecord(new SoundRecord(DateTime.Now, category, stopped, nearest, nearestDistance, data, x, y, z));
    }

    private string DescribeObject(GameObject* obj)
    {
        var info = Resolve(obj);
        var text = info.IsLocalPlayer ? $"{info.Name} [self]" : info.Name;
        if (info.EmoteId != 0)
            text += $" ({(EmoteNames.TryGetValue(info.EmoteId, out var emote) ? emote : $"#{info.EmoteId}")})";

        if (mutedCharacters.ContainsKey((nint)obj))
            text += " [muted]";

        return text;
    }

    private void AddSoundRecord(SoundRecord record)
    {
        lock (soundRecords)
        {
            soundRecords.AddFirst(record);
            while (soundRecords.Count > SoundRecordCapacity)
                soundRecords.RemoveLast();
        }
    }

    private void UpdateMute(Character* character)
    {
        var obj = (GameObject*)character;
        var known = mutedCharacters.TryGetValue((nint)obj, out var muted) && muted.EntityId == obj->EntityId;

        if (!ShouldMute(character))
        {
            if (known)
                Unmute(character, muted!);

            return;
        }

        if (!known)
        {
            mutedCharacters[(nint)obj] = muted = new MutedCharacter
            {
                EntityId = obj->EntityId,
                Category = character->SoundVolumeCategory,
                CategoryOverride = character->SoundVolumeCategoryOverride,
            };
        }
        else
        {
            if (character->SoundVolumeCategory != NoPlayCategory)
                muted!.Category = character->SoundVolumeCategory;

            if (character->SoundVolumeCategoryOverride != NoPlayCategory)
                muted!.CategoryOverride = character->SoundVolumeCategoryOverride;
        }

        muted!.Seen = true;
        character->SoundVolumeCategory = NoPlayCategory;
        character->SoundVolumeCategoryOverride = NoPlayCategory;
    }

    private bool ShouldMute(Character* character)
    {
        if (!config.MuteBlockedEmoteSounds)
            return false;

        var emoteId = character->EmoteController.EmoteId;
        if (emoteId == 0 || !IsBlockedPresetEmote(emoteId))
            return false;

        var player = Resolve((GameObject*)character);
        if (player.IsLocalPlayer)
            return config.IncludeSelfForTesting;

        if (PlayerKey.IsListed(Blacklist, player.Name, player.World))
            return true;

        if (config.ExemptPartyAndFriends && (player.IsPartyMember || player.IsFriend))
            return false;

        return !PlayerKey.IsListed(Whitelist, player.Name, player.World);
    }

    private static void Unmute(Character* character, MutedCharacter muted)
    {
        if (character->SoundVolumeCategory == NoPlayCategory)
            character->SoundVolumeCategory = muted.Category;

        if (character->SoundVolumeCategoryOverride == NoPlayCategory)
            character->SoundVolumeCategoryOverride = muted.CategoryOverride;
    }

    private void UnmuteAll()
    {
        if (mutedCharacters.Count == 0)
            return;

        var objects = GameObjectManager.Instance()->Objects.IndexSorted;
        for (var i = 0; i < CharacterSlotEnd; i += 2)
        {
            var obj = objects[i].Value;
            if (obj != null && obj->ObjectKind == ObjectKind.Pc
                && mutedCharacters.TryGetValue((nint)obj, out var muted) && muted.EntityId == obj->EntityId)
                Unmute((Character*)obj, muted);
        }

        mutedCharacters.Clear();
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

        var blockedEmote = !viaMinion && source.Kind == ObjectKind.Pc && source.EmoteId != 0 && IsBlockedPresetEmote(source.EmoteId);
        if (blockedEmote && record.ModState == VfxModState.Modded)
            NoteModdedEmote(source);

        var blamed = source.Kind == ObjectKind.Pc ? source : record.Target;
        if (record.ModState == VfxModState.Modded && blamed.Kind == ObjectKind.Pc && !blamed.IsLocalPlayer
            && PlayerKey.IsListed(Blacklist, blamed.Name, blamed.World))
            return "blacklist";

        if (source.Kind != ObjectKind.Pc)
            return string.Empty;

        if (source.IsLocalPlayer && !config.IncludeSelfForTesting)
            return string.Empty;

        if (!source.IsLocalPlayer && config.ExemptPartyAndFriends && (source.IsPartyMember || source.IsFriend))
            return string.Empty;

        if (PlayerKey.IsListed(Whitelist, source.Name, source.World))
            return string.Empty;

        if (blockedEmote && (record.ModState == VfxModState.Modded || (IsEmotePath(record.Path) && HasModdedEmote(source))))
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

    private static bool IsEmotePath(string path) => path.StartsWith("vfx/emote", StringComparison.OrdinalIgnoreCase);

    private void NoteModdedEmote(VfxActorInfo source)
    {
        lock (moddedEmotes)
        {
            if (moddedEmotes.Count >= ModdedEmoteLimit)
                moddedEmotes.Clear();

            if (moddedEmotes.Add((source.EntityId, source.EmoteId)))
                filterDirty = true;
        }
    }

    private bool HasModdedEmote(VfxActorInfo source)
    {
        lock (moddedEmotes)
            return moddedEmotes.Contains((source.EntityId, source.EmoteId));
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

public readonly record struct SoundRecord(DateTime Time, int Category, bool Stopped, string Nearest, float Distance, nint Data, float X, float Y, float Z);
