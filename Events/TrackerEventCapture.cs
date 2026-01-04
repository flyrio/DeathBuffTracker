using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DeathBuffTracker.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;
using Lumina.Excel.Sheets;
using ActionSheet = Lumina.Excel.Sheets.Action;
using StatusSheet = Lumina.Excel.Sheets.Status;

namespace DeathBuffTracker.Events;

public unsafe sealed class TrackerEventCapture : IDisposable {
    private const float StatusRefreshThresholdSeconds = 0.05f;
    private unsafe delegate void ProcessPacketActionEffectDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    private delegate void ProcessPacketEffectResultDelegate(uint targetId, IntPtr actionIntegrityData, byte isReplay);
    private delegate void ProcessPacketActorControlDelegate(
        uint entityId,
        uint category,
        uint param1,
        uint param2,
        uint param3,
        uint param4,
        uint param5,
        uint param6,
        uint param7,
        uint param8,
        ulong targetId,
        byte param9);

    private readonly DeathBuffTrackerPlugin plugin;
    private readonly Hook<ProcessPacketActionEffectDelegate> processPacketActionEffectHook;
    private readonly Hook<ProcessPacketEffectResultDelegate> processPacketEffectResultHook;
    private readonly Hook<ProcessPacketActorControlDelegate> processPacketActorControlHook;
    private readonly Dictionary<uint, ActorState> actorStates = new();
    private readonly Dictionary<uint, TimedDamageInfo> lastDamageByTarget = new();
    private readonly Dictionary<uint, DateTime> lastDeathByTarget = new();
    private readonly object damageGate = new();
    private readonly object deathGate = new();
    private readonly TimeSpan damageInfoTtl = TimeSpan.FromSeconds(10);
    private readonly TimeSpan deathDedupWindow = TimeSpan.FromSeconds(2);
    private HashSet<uint> trackedActorIds = new();
    private HashSet<uint> trackedStatusIds = new();
    private bool allowDamageCapture;
    private bool allowStatusCapture;
    private bool allowDeathCapture;
    private readonly bool useActorControlDeaths = true;
    private readonly TimeSpan scanInterval = TimeSpan.FromMilliseconds(250);
    private DateTime lastScanUtc = DateTime.MinValue;
    private uint lastTerritoryId;

    public TrackerEventCapture(DeathBuffTrackerPlugin plugin) {
        this.plugin = plugin;
        processPacketActionEffectHook =
            Service.GameInteropProvider.HookFromSignature<ProcessPacketActionEffectDelegate>(
                ActionEffectHandler.Addresses.Receive.String,
                ProcessPacketActionEffectDetour);
        processPacketActionEffectHook.Enable();
        processPacketEffectResultHook =
            Service.GameInteropProvider.HookFromSignature<ProcessPacketEffectResultDelegate>(
                "48 8B C4 44 88 40 18 89 48 08",
                ProcessPacketEffectResultDetour);
        processPacketEffectResultHook.Enable();
        processPacketActorControlHook =
            Service.GameInteropProvider.HookFromSignature<ProcessPacketActorControlDelegate>(
                "E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64",
                ProcessPacketActorControlDetour);
        processPacketActorControlHook.Enable();

        Service.Framework.Update += OnFrameworkUpdate;
        lastTerritoryId = Service.ClientState.TerritoryType;
    }

    public void Dispose() {
        Service.Framework.Update -= OnFrameworkUpdate;
        processPacketActionEffectHook.Dispose();
        processPacketEffectResultHook.Dispose();
        processPacketActorControlHook.Dispose();
    }

    public void RecordEvent(DeathBuffEventRecord record) {
        plugin.EventStore.Add(record);
    }

    private void OnFrameworkUpdate(IFramework framework) {
        if (!Service.ClientState.IsLoggedIn || Service.ObjectTable.LocalPlayer == null) {
            ClearTrackingState();
            lastTerritoryId = 0;
            return;
        }

        var territoryId = Service.ClientState.TerritoryType;
        if (territoryId != lastTerritoryId) {
            ClearTrackingState();
            lastTerritoryId = territoryId;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - lastScanUtc < scanInterval) {
            return;
        }
        lastScanUtc = nowUtc;

        trackedActorIds = BuildTrackedActorIds();
        if (trackedActorIds.Count == 0) {
            ClearTrackingState();
            return;
        }

        var shouldRecord = !plugin.Configuration.TrackOnlyInInstances || IsInInstance();
        var trackDeaths = plugin.Configuration.TrackDeaths;
        var trackStatusGains = plugin.Configuration.TrackStatusGains;
        allowDamageCapture = shouldRecord && trackDeaths;
        if (!allowDamageCapture) {
            ClearDamageCache();
        }

        trackedStatusIds = trackStatusGains
            ? plugin.Configuration.TrackedStatuses.Select(status => status.Id).ToHashSet()
            : new HashSet<uint>();
        allowStatusCapture = shouldRecord && trackStatusGains && trackedStatusIds.Count > 0;
        allowDeathCapture = shouldRecord && trackDeaths;
        if (!allowDeathCapture) {
            ClearDeathCache();
        }

        var dutyContext = ResolveDutyContext(territoryId);
        var seenActors = new HashSet<uint>();

        var recordStatusRefresh = shouldRecord && trackStatusGains;
        foreach (var player in EnumerateTrackedPlayers(trackedActorIds, plugin.Configuration.TrackOtherPlayers)) {
            var actorId = player.EntityId;
            seenActors.Add(actorId);

            if (!actorStates.TryGetValue(actorId, out var state)) {
                state = new ActorState();
                actorStates[actorId] = state;
            }

            UpdateDeathState(player, state, shouldRecord && trackDeaths, dutyContext);
            UpdateStatusState(
                player,
                state,
                shouldRecord && trackStatusGains && !allowStatusCapture,
                recordStatusRefresh,
                trackedStatusIds,
                dutyContext);
        }

        if (seenActors.Count != actorStates.Count) {
            var staleActors = actorStates.Keys.Where(actorId => !seenActors.Contains(actorId)).ToList();
            foreach (var actorId in staleActors) {
                actorStates.Remove(actorId);
            }
        }

        PruneDamageInfo(nowUtc);
        PruneDeathInfo(nowUtc);
    }

    private void UpdateDeathState(IBattleChara player, ActorState state, bool recordEvent, DutyContext dutyContext) {
        var isDead = player.CurrentHp == 0;
        if (!useActorControlDeaths && recordEvent && isDead && !state.WasDead) {
            var damageInfo = ResolveDamageInfo(player);
            var worldInfo = ResolvePlayerWorldInfo(player);

            RecordEvent(new DeathBuffEventRecord {
                TerritoryId = dutyContext.TerritoryId,
                TerritoryName = dutyContext.TerritoryName,
                ContentId = dutyContext.ContentId,
                ContentName = dutyContext.ContentName,
                PlayerName = player.Name.ToString(),
                PlayerHomeWorldId = worldInfo.HomeWorldId,
                PlayerHomeWorldName = worldInfo.HomeWorldName,
                PlayerCurrentWorldId = worldInfo.CurrentWorldId,
                PlayerCurrentWorldName = worldInfo.CurrentWorldName,
                EventType = TrackerEventType.Death,
                DamageSourceId = damageInfo.SourceId,
                DamageSourceName = damageInfo.SourceName,
                DamageActionId = damageInfo.ActionId,
                DamageActionName = damageInfo.ActionName,
                DamageType = damageInfo.DamageType,
            });
        }

        state.WasDead = isDead;
    }

    private unsafe void ProcessPacketActionEffectDetour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* effectHeader,
        ActionEffectHandler.TargetEffects* effectArray,
        GameObjectId* targetEntityIds
    ) {
        processPacketActionEffectHook.Original(casterEntityId, casterPtr, targetPos, effectHeader, effectArray, targetEntityIds);

        try {
            if (!allowDamageCapture) {
                return;
            }

            var trackedIds = trackedActorIds;
            if (trackedIds.Count == 0 || effectHeader->NumTargets == 0) {
                return;
            }

            var actionId = effectHeader->SpellId;
            var actionIdValue = actionId == 0 ? null : (uint?)actionId;
            string? actionName = null;
            var sourceId = casterEntityId == 0 ? null : (uint?)casterEntityId;
            string? sourceName = null;

            for (var i = 0; i < effectHeader->NumTargets; i++) {
                var actionTargetId = (uint)(targetEntityIds[i] & uint.MaxValue);
                if (!trackedIds.Contains(actionTargetId)) {
                    continue;
                }

                for (var j = 0; j < 8; j++) {
                    ref var actionEffect = ref effectArray[i].Effects[j];
                    if (!IsDamageEffect(actionEffect.Type)) {
                        continue;
                    }

                    actionName ??= actionIdValue.HasValue ? ResolveActionName(actionIdValue.Value) : null;
                    sourceName ??= ResolveSourceName(casterPtr, sourceId);
                    var damageType = ResolveDamageTypeName((byte)(actionEffect.Param1 & 0xF));

                    var info = new DamageInfo(sourceId, sourceName, actionIdValue, actionName, damageType);
                    lock (damageGate) {
                        lastDamageByTarget[actionTargetId] = new TimedDamageInfo(info, DateTime.UtcNow);
                    }

                    break;
                }
            }
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to process action effect");
        }
    }

    private void ProcessPacketActorControlDetour(
        uint entityId,
        uint category,
        uint param1,
        uint param2,
        uint param3,
        uint param4,
        uint param5,
        uint param6,
        uint param7,
        uint param8,
        ulong targetId,
        byte param9
    ) {
        processPacketActorControlHook.Original(entityId, category, param1, param2, param3, param4, param5, param6, param7, param8, targetId, param9);

        try {
            if (!useActorControlDeaths || !allowDeathCapture) {
                return;
            }

            if (category != (uint)ActorControlCategory.Death) {
                return;
            }

            var trackedIds = trackedActorIds;
            if (trackedIds.Count == 0 || !trackedIds.Contains(entityId)) {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            lock (deathGate) {
                if (lastDeathByTarget.TryGetValue(entityId, out var lastDeath) &&
                    nowUtc - lastDeath <= deathDedupWindow) {
                    return;
                }
                lastDeathByTarget[entityId] = nowUtc;
            }

            var target = Service.ObjectTable.SearchByEntityId(entityId);
            if (target is not IBattleChara battleTarget) {
                return;
            }

            var dutyContext = ResolveDutyContext(Service.ClientState.TerritoryType);
            var damageInfo = ResolveDamageInfo(battleTarget);
            var worldInfo = ResolvePlayerWorldInfo(target);

            RecordEvent(new DeathBuffEventRecord {
                TerritoryId = dutyContext.TerritoryId,
                TerritoryName = dutyContext.TerritoryName,
                ContentId = dutyContext.ContentId,
                ContentName = dutyContext.ContentName,
                PlayerName = target.Name.ToString(),
                PlayerHomeWorldId = worldInfo.HomeWorldId,
                PlayerHomeWorldName = worldInfo.HomeWorldName,
                PlayerCurrentWorldId = worldInfo.CurrentWorldId,
                PlayerCurrentWorldName = worldInfo.CurrentWorldName,
                EventType = TrackerEventType.Death,
                DamageSourceId = damageInfo.SourceId,
                DamageSourceName = damageInfo.SourceName,
                DamageActionId = damageInfo.ActionId,
                DamageActionName = damageInfo.ActionName,
                DamageType = damageInfo.DamageType,
            });
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to process actor control death");
        }
    }

    private unsafe void ProcessPacketEffectResultDetour(uint targetId, IntPtr actionIntegrityData, byte isReplay) {
        processPacketEffectResultHook.Original(targetId, actionIntegrityData, isReplay);

        try {
            if (!allowStatusCapture) {
                return;
            }

            var trackedIds = trackedActorIds;
            var trackedStatuses = trackedStatusIds;
            if (trackedIds.Count == 0 || trackedStatuses.Count == 0) {
                return;
            }

            if (!trackedIds.Contains(targetId)) {
                return;
            }

            if (actionIntegrityData == IntPtr.Zero) {
                return;
            }

            var target = Service.ObjectTable.SearchByEntityId(targetId);
            if (target == null) {
                return;
            }

            var worldInfo = ResolvePlayerWorldInfo(target);
            var message = (AddStatusEffect*)actionIntegrityData;
            var effects = (StatusEffectAddEntry*)message->Effects;
            var effectCount = Math.Min(message->EffectCount, 4u);
            if (effectCount == 0) {
                return;
            }

            var dutyContext = ResolveDutyContext(Service.ClientState.TerritoryType);

            for (uint j = 0; j < effectCount; j++) {
                var effect = effects[j];
                var effectId = effect.EffectId;
                if (effectId == 0) {
                    continue;
                }

                if (!trackedStatuses.Contains(effectId)) {
                    continue;
                }

                if (effect.Duration < 0) {
                    continue;
                }

                var statusName = ResolveStatusName(effectId);
                var sourceId = effect.SourceActorId == 0 ? null : (uint?)effect.SourceActorId;
                var sourceName = ResolveObjectName(sourceId);

                RecordEvent(new DeathBuffEventRecord {
                    TerritoryId = dutyContext.TerritoryId,
                    TerritoryName = dutyContext.TerritoryName,
                    ContentId = dutyContext.ContentId,
                    ContentName = dutyContext.ContentName,
                    PlayerName = target.Name.ToString(),
                    PlayerHomeWorldId = worldInfo.HomeWorldId,
                    PlayerHomeWorldName = worldInfo.HomeWorldName,
                    PlayerCurrentWorldId = worldInfo.CurrentWorldId,
                    PlayerCurrentWorldName = worldInfo.CurrentWorldName,
                    EventType = TrackerEventType.StatusGain,
                    StatusId = effectId,
                    StatusName = statusName,
                    StatusStackCount = effect.StackCount == 0 ? null : (uint?)effect.StackCount,
                    StatusDurationSeconds = effect.Duration <= 0 ? null : effect.Duration,
                    DamageSourceId = sourceId,
                    DamageSourceName = sourceName,
                    DamageActionId = effectId,
                    DamageActionName = statusName,
                    DamageType = "StatusGain",
                });
            }
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to process status effect result");
        }
    }

    private void UpdateStatusState(
        IBattleChara player,
        ActorState state,
        bool recordNewEvents,
        bool recordRefreshEvents,
        HashSet<uint> trackedStatusIds,
        DutyContext dutyContext
    ) {
        var currentStatuses = BuildTrackedStatusMap(player, trackedStatusIds);

        if (recordNewEvents || recordRefreshEvents) {
            var worldInfo = ResolvePlayerWorldInfo(player);
            foreach (var status in currentStatuses) {
                var statusId = status.Key;
                var snapshot = status.Value;
                var hasPrevious = state.StatusSnapshots.TryGetValue(statusId, out var previous);

                if (recordNewEvents && !hasPrevious) {
                    RecordStatusEvent(player, dutyContext, worldInfo, statusId, snapshot);
                    continue;
                }

                if (recordRefreshEvents && hasPrevious && IsStatusRefreshed(previous, snapshot)) {
                    RecordStatusEvent(player, dutyContext, worldInfo, statusId, snapshot);
                }
            }
        }

        state.StatusSnapshots = currentStatuses;
    }

    private static Dictionary<uint, StatusSnapshot> BuildTrackedStatusMap(IBattleChara player, HashSet<uint> trackedStatusIds) {
        var results = new Dictionary<uint, StatusSnapshot>();
        if (trackedStatusIds.Count == 0) {
            return results;
        }

        foreach (var status in player.StatusList) {
            if (status.StatusId == 0 || !trackedStatusIds.Contains(status.StatusId)) {
                continue;
            }

            if (!results.ContainsKey(status.StatusId)) {
                var remainingTime = (float)status.RemainingTime;
                results[status.StatusId] = new StatusSnapshot(status.SourceId, remainingTime);
            }
        }

        return results;
    }

    private static bool IsStatusRefreshed(StatusSnapshot previous, StatusSnapshot current) {
        if (current.RemainingTime <= 0 || previous.RemainingTime <= 0) {
            return false;
        }

        return current.RemainingTime - previous.RemainingTime >= StatusRefreshThresholdSeconds;
    }

    private void RecordStatusEvent(
        IBattleChara player,
        DutyContext dutyContext,
        PlayerWorldInfo worldInfo,
        uint statusId,
        StatusSnapshot snapshot
    ) {
        var statusName = ResolveStatusName(statusId);
        var sourceName = ResolveObjectName(snapshot.SourceId);
        float? durationSeconds = snapshot.RemainingTime <= 0 ? null : snapshot.RemainingTime;

        RecordEvent(new DeathBuffEventRecord {
            TerritoryId = dutyContext.TerritoryId,
            TerritoryName = dutyContext.TerritoryName,
            ContentId = dutyContext.ContentId,
            ContentName = dutyContext.ContentName,
            PlayerName = player.Name.ToString(),
            PlayerHomeWorldId = worldInfo.HomeWorldId,
            PlayerHomeWorldName = worldInfo.HomeWorldName,
            PlayerCurrentWorldId = worldInfo.CurrentWorldId,
            PlayerCurrentWorldName = worldInfo.CurrentWorldName,
            EventType = TrackerEventType.StatusGain,
            StatusId = statusId,
            StatusName = statusName,
            StatusDurationSeconds = durationSeconds,
            DamageSourceId = snapshot.SourceId == 0 ? null : snapshot.SourceId,
            DamageSourceName = sourceName,
            DamageActionId = statusId,
            DamageActionName = statusName,
            DamageType = "StatusGain",
        });
    }

    private HashSet<uint> BuildTrackedActorIds() {
        var result = new HashSet<uint>();

        if (plugin.Configuration.TrackOtherPlayers) {
            foreach (var player in Service.ObjectTable.PlayerObjects) {
                if (player.EntityId != 0) {
                    result.Add(player.EntityId);
                }
            }

            return result;
        }

        if (plugin.Configuration.TrackSelf && Service.ObjectTable.LocalPlayer != null) {
            result.Add(Service.ObjectTable.LocalPlayer.EntityId);
        }

        if (plugin.Configuration.TrackPartyMembers) {
            foreach (var member in Service.PartyList) {
                if (member.EntityId != 0) {
                    result.Add(member.EntityId);
                }
            }
        }

        return result;
    }

    private static IEnumerable<IBattleChara> EnumerateTrackedPlayers(HashSet<uint> trackedActorIds, bool includeAllVisible) {
        if (includeAllVisible) {
            foreach (var player in Service.ObjectTable.PlayerObjects) {
                if (player.EntityId != 0) {
                    yield return player;
                }
            }

            yield break;
        }

        foreach (var actorId in trackedActorIds) {
            if (Service.ObjectTable.SearchByEntityId(actorId) is IBattleChara player) {
                yield return player;
            }
        }
    }

    private static bool IsInInstance() {
        return IsConditionActive("BoundByDuty") ||
               IsConditionActive("BoundByDuty56") ||
               IsConditionActive("BoundByDuty95");
    }

    private static bool IsConditionActive(string flagName) {
        return Enum.TryParse(flagName, out ConditionFlag flag) && Service.Condition[flag];
    }

    private DutyContext ResolveDutyContext(uint territoryId) {
        var territoryName = string.Empty;
        uint? contentId = null;
        string? contentName = null;

        var territorySheet = Service.DataManager.GetExcelSheet<TerritoryType>();
        var territory = territorySheet?.GetRow(territoryId);
        if (territory != null) {
            if (territory.Value.PlaceName.RowId != 0) {
                territoryName = territory.Value.PlaceName.Value.Name.ToString();
            }

            if (territory.Value.ContentFinderCondition.RowId != 0) {
                var content = territory.Value.ContentFinderCondition.Value;
                contentId = content.RowId;
                contentName = content.Name.ToString();
            }
        }

        return new DutyContext(territoryId, territoryName, contentId, contentName);
    }

    private static PlayerWorldInfo ResolvePlayerWorldInfo(IGameObject target) {
        if (target is not IPlayerCharacter player) {
            return default;
        }

        var homeWorld = player.HomeWorld;
        var currentWorld = player.CurrentWorld;

        var homeWorldId = homeWorld.RowId == 0 ? null : (uint?)homeWorld.RowId;
        var currentWorldId = currentWorld.RowId == 0 ? null : (uint?)currentWorld.RowId;
        var homeWorldName = homeWorld.ValueNullable?.Name.ToString();
        var currentWorldName = currentWorld.ValueNullable?.Name.ToString();

        return new PlayerWorldInfo(homeWorldId, homeWorldName, currentWorldId, currentWorldName);
    }

    private string? ResolveStatusName(uint statusId) {
        var configured = plugin.Configuration.TrackedStatuses.FirstOrDefault(status => status.Id == statusId);
        if (configured != null && !string.IsNullOrWhiteSpace(configured.Name)) {
            return configured.Name;
        }

        var sheet = Service.DataManager.GetExcelSheet<StatusSheet>();
        var row = sheet?.GetRow(statusId);
        return row?.Name.ToString();
    }

    private string? ResolveActionName(uint actionId) {
        var sheet = Service.DataManager.GetExcelSheet<ActionSheet>();
        var row = sheet?.GetRow(actionId);
        return row?.Name.ToString();
    }

    private static unsafe string? ResolveSourceName(Character* casterPtr, uint? sourceId) {
        if (casterPtr != null) {
            var name = casterPtr->NameString;
            if (!string.IsNullOrWhiteSpace(name)) {
                return name;
            }
        }

        return ResolveObjectName(sourceId);
    }

    private static string? ResolveObjectName(uint? objectId) {
        if (!objectId.HasValue || objectId.Value == 0) {
            return null;
        }

        var match = Service.ObjectTable.SearchByEntityId(objectId.Value);
        return match?.Name.ToString();
    }

    private DamageInfo ResolveDamageInfo(IBattleChara player) {
        var nowUtc = DateTime.UtcNow;
        lock (damageGate) {
            if (lastDamageByTarget.TryGetValue(player.EntityId, out var stored) &&
                nowUtc - stored.TimestampUtc <= damageInfoTtl) {
                return stored.Info;
            }
        }

        var sourceId = TryGetObjectId(player, "LastAttackerId", "LastAttackId", "LastDamagerId", "LastDamagingObjectId");
        var sourceName = ResolveObjectName(sourceId) ?? TryGetObjectName(player, "LastAttacker", "LastDamager");
        var actionId = TryGetUint(player, "LastActionId", "LastAttackActionId", "LastHitActionId", "LastDamageActionId");
        var actionName = actionId.HasValue ? ResolveActionName(actionId.Value) : null;

        return new DamageInfo(sourceId, sourceName, actionId, actionName, "Death");
    }

    private static string? TryGetObjectName(object source, params string[] propertyNames) {
        foreach (var name in propertyNames) {
            var value = TryGetProperty(source, name);
            if (value is IGameObject gameObject) {
                return gameObject.Name.ToString();
            }
        }

        return null;
    }

    private static uint? TryGetObjectId(object source, params string[] propertyNames) {
        foreach (var name in propertyNames) {
            var value = TryGetProperty(source, name);
            if (value is uint id32 && id32 != 0) {
                return id32;
            }
        }

        return null;
    }

    private static uint? TryGetUint(object source, params string[] propertyNames) {
        foreach (var name in propertyNames) {
            var value = TryGetProperty(source, name);
            if (value is uint id && id != 0) {
                return id;
            }
        }

        return null;
    }

    private static object? TryGetProperty(object source, string propertyName) {
        var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(source);
    }

    private void PruneDamageInfo(DateTime nowUtc) {
        lock (damageGate) {
            if (lastDamageByTarget.Count == 0) {
                return;
            }

            var staleKeys = new List<uint>();
            foreach (var entry in lastDamageByTarget) {
                if (nowUtc - entry.Value.TimestampUtc > damageInfoTtl) {
                    staleKeys.Add(entry.Key);
                }
            }

            foreach (var key in staleKeys) {
                lastDamageByTarget.Remove(key);
            }
        }
    }

    private void PruneDeathInfo(DateTime nowUtc) {
        lock (deathGate) {
            if (lastDeathByTarget.Count == 0) {
                return;
            }

            var staleKeys = new List<uint>();
            foreach (var entry in lastDeathByTarget) {
                if (nowUtc - entry.Value > deathDedupWindow) {
                    staleKeys.Add(entry.Key);
                }
            }

            foreach (var key in staleKeys) {
                lastDeathByTarget.Remove(key);
            }
        }
    }

    private void ClearDamageCache() {
        lock (damageGate) {
            lastDamageByTarget.Clear();
        }
    }

    private void ClearDeathCache() {
        lock (deathGate) {
            lastDeathByTarget.Clear();
        }
    }

    private void ClearTrackingState() {
        actorStates.Clear();
        ClearDamageCache();
        ClearDeathCache();
        trackedActorIds = new HashSet<uint>();
        trackedStatusIds = new HashSet<uint>();
        allowDamageCapture = false;
        allowStatusCapture = false;
        allowDeathCapture = false;
    }

    private static bool IsDamageEffect(byte effectType) {
        return effectType == (byte)ActionEffectType.Damage ||
               effectType == (byte)ActionEffectType.BlockedDamage ||
               effectType == (byte)ActionEffectType.ParriedDamage;
    }

    private static string ResolveDamageTypeName(byte damageType) {
        return damageType switch {
            1 => "Slashing",
            2 => "Piercing",
            3 => "Blunt",
            4 => "Shot",
            5 => "Magic",
            6 => "Breath",
            7 => "Physical",
            8 => "LimitBreak",
            _ => "Unknown",
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct AddStatusEffect {
        public uint Unknown1;
        public uint RelatedActionSequence;
        public uint ActorId;
        public uint CurrentHp;
        public uint MaxHp;
        public ushort CurrentMp;
        public ushort Unknown3;
        public byte DamageShield;
        public byte EffectCount;
        public ushort Unknown6;
        public fixed byte Effects[64];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StatusEffectAddEntry {
        public byte EffectIndex;
        public byte Unknown1;
        public ushort EffectId;
        public ushort StackCount;
        public ushort Unknown3;
        public float Duration;
        public uint SourceActorId;
    }

    private sealed class ActorState {
        public bool WasDead;
        public Dictionary<uint, StatusSnapshot> StatusSnapshots { get; set; } = new();
    }

    private readonly record struct TimedDamageInfo(DamageInfo Info, DateTime TimestampUtc);

    private readonly record struct DutyContext(uint TerritoryId, string TerritoryName, uint? ContentId, string? ContentName);

    private readonly record struct StatusSnapshot(uint SourceId, float RemainingTime);

    private readonly record struct PlayerWorldInfo(
        uint? HomeWorldId,
        string? HomeWorldName,
        uint? CurrentWorldId,
        string? CurrentWorldName
    );

    private readonly record struct DamageInfo(
        uint? SourceId,
        string? SourceName,
        uint? ActionId,
        string? ActionName,
        string? DamageType
    );

    private enum ActionEffectType : byte {
        Damage = 3,
        BlockedDamage = 5,
        ParriedDamage = 6,
    }

    private enum ActorControlCategory : uint {
        Death = 0x6,
    }
}
