using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    public static class CoopDisasterSync
    {
        private const int QueueCapacity = 32;
        private static readonly object QueueLock = new object();
        private static readonly Queue<CoopAuthoritativeCommandData> Pending =
            new Queue<CoopAuthoritativeCommandData>();
        private static readonly List<CoopAuthoritativeCommandData> Scheduled =
            new List<CoopAuthoritativeCommandData>();
        private static readonly HashSet<CoopDisasterEventKind> PendingKinds =
            new HashSet<CoopDisasterEventKind>();
        private static readonly CoopSequenceWatermarks Sequences =
            new CoopSequenceWatermarks();
        private static long sNextSequence;
        private static bool sApplying;

        public static bool Applying { get { return sApplying; } }

        public static void Initialize()
        {
            ResetForSession();
        }

        public static void ResetForSession()
        {
            lock (QueueLock)
            {
                Pending.Clear();
                Scheduled.Clear();
                PendingKinds.Clear();
                Sequences.Reset();
                sNextSequence = 0;
            }
            sApplying = false;
        }

        public static void QueueCommand(CoopAuthoritativeCommandData command)
        {
            SyncManager sync = SyncManager.Instance;
            long activeSessionId;
            CoopDisasterEventData ignored;
            if (SyncManager.Role != "guest" || command == null ||
                command.Kind != CoopAuthoritativeCommandKind.DisasterEvent ||
                command.Phase != CoopAuthoritativeCommandPhase.Commit ||
                sync == null || !sync.TryGetRunningSessionId(out activeSessionId) ||
                !CoopCommandSessionPolicy.CanAccept(true, activeSessionId, command.SessionId) ||
                !CoopDisasterEventCodec.TryDecode(command.Payload, out ignored)) return;
            bool rejected;
            lock (QueueLock)
            {
                rejected = Pending.Count >= QueueCapacity;
                if (!rejected) Pending.Enqueue(command);
            }
            if (rejected) ReportFailure(command, CoopMutationFailureCode.ValidationRejected);
        }

        public static bool ShouldSuppress(
            CoopDisasterEventKind kind,
            Vector3 target,
            bool hasTarget)
        {
            if (!SyncManager.FullMode || sApplying || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return false;
            if (SyncManager.Role != "host") return true;

            long sessionId;
            if (!SyncManager.Instance.TryGetRunningSessionId(out sessionId)) return true;
            lock (QueueLock)
            {
                if (RequiresPendingKindLock(kind) && PendingKinds.Contains(kind)) return true;
            }
            CoopDisasterEventData data = new CoopDisasterEventData(
                kind,
                hasTarget,
                hasTarget ? target.x : 0f,
                hasTarget ? target.y : 0f,
                hasTarget ? target.z : 0f);
            ScheduleHostEvent(sessionId, data);
            return true;
        }

        public static bool ShouldSuppressConstructionImpact(
            Construction construction,
            ImpactType impactType)
        {
            if (!SyncManager.FullMode || sApplying || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded ||
                (impactType != ImpactType.Meteor && impactType != ImpactType.Lightning))
                return false;
            if (SyncManager.Role == "guest") return true;
            long sessionId;
            if (construction == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out sessionId)) return true;
            CoopDisasterEventKind kind = impactType == ImpactType.Meteor
                ? CoopDisasterEventKind.MeteorConstructionImpact
                : CoopDisasterEventKind.LightningConstructionImpact;
            ScheduleHostEvent(
                sessionId,
                new CoopDisasterEventData(
                    kind,
                    false,
                    0f,
                    0f,
                    0f,
                    CoopObjectBridge.Capture(construction, true)));
            return true;
        }

        public static bool ShouldSuppressCharacterImpact(
            ImpactType impactType,
            Vector3 position)
        {
            if (!SyncManager.FullMode || sApplying || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded ||
                (impactType != ImpactType.Meteor && impactType != ImpactType.Lightning))
                return false;
            if (SyncManager.Role == "guest") return true;
            long sessionId;
            if (!SyncManager.Instance.TryGetRunningSessionId(out sessionId)) return true;
            CoopDisasterEventKind kind = impactType == ImpactType.Meteor
                ? CoopDisasterEventKind.MeteorCharacterImpact
                : CoopDisasterEventKind.LightningCharacterImpact;
            ScheduleHostEvent(
                sessionId,
                new CoopDisasterEventData(
                    kind, true, position.x, position.y, position.z));
            return true;
        }

        public static bool ShouldSuppressLightningRecharge(Module module)
        {
            if (!SyncManager.FullMode || sApplying || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return false;
            if (SyncManager.Role == "guest") return true;
            long sessionId;
            if (module == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out sessionId)) return true;
            ScheduleHostEvent(
                sessionId,
                new CoopDisasterEventData(
                    CoopDisasterEventKind.LightningRecharge,
                    false,
                    0f,
                    0f,
                    0f,
                    CoopObjectBridge.Capture(module, true)));
            return true;
        }

        private static bool ScheduleHostEvent(
            long sessionId,
            CoopDisasterEventData data)
        {
            SyncManager sync = SyncManager.Instance;
            if (sync == null || SyncManager.Role != "host") return false;
            long currentTick = Interlocked.Read(ref sync.TickIndex);
            long applyLeadTicks = CoopCommandLeadPolicy.ApplyLeadTicks(
                CoopTimeSync.CurrentScaleIndex,
                CoopNetworkTiming.CurrentBudgetMilliseconds);
            if (currentTick > CoopProtocol.MaxWorldTick - applyLeadTicks)
            {
                sync.ReportRuntimeInvariantFailure(
                    "disaster commit tick exceeded the protocol range");
                return false;
            }
            byte[] payload;
            try { payload = CoopDisasterEventCodec.Encode(data); }
            catch (ArgumentException ex)
            {
                sync.ReportRuntimeInvariantFailure(
                    "disaster event could not be encoded: " + ex.Message);
                return false;
            }
            long localSequence = Interlocked.Increment(ref sNextSequence);
            CoopAuthoritativeCommandData commit = new CoopAuthoritativeCommandData(
                sessionId,
                CoopPlacementRequestIds.For(CoopRole.Host, localSequence),
                currentTick + applyLeadTicks,
                CoopAuthoritativeCommandKind.DisasterEvent,
                CoopAuthoritativeCommandPhase.Commit,
                payload);
            if (!Schedule(commit))
            {
                ReportFailure(commit, CoopMutationFailureCode.ValidationRejected);
                return false;
            }
            if (sync.TrySendPacket(
                    CoopProtocol.CreateAuthoritativeCommandPacket(commit))) return true;
            RemoveScheduled(commit);
            ReportFailure(commit, CoopMutationFailureCode.ApplyFailed);
            return false;
        }

        public static bool BeforeSimulationTick(long currentTick)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return true;
            while (true)
            {
                CoopAuthoritativeCommandData command = null;
                lock (QueueLock)
                {
                    if (Pending.Count > 0) command = Pending.Dequeue();
                }
                if (command == null) break;
                if (command.ApplyTick <= currentTick ||
                    command.ApplyTick - currentTick > CoopProtocol.MaxCommandLeadTicks ||
                    !Schedule(command))
                {
                    ReportFailure(command, CoopMutationFailureCode.ValidationRejected);
                    return false;
                }
            }

            List<CoopAuthoritativeCommandData> due =
                new List<CoopAuthoritativeCommandData>();
            lock (QueueLock)
            {
                for (int i = Scheduled.Count - 1; i >= 0; i--)
                {
                    CoopAuthoritativeCommandData command = Scheduled[i];
                    if (command.ApplyTick < currentTick)
                    {
                        Scheduled.RemoveAt(i);
                        ReportFailure(command, CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                    if (command.ApplyTick == currentTick)
                    {
                        due.Add(command);
                        Scheduled.RemoveAt(i);
                    }
                }
            }
            due.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            for (int i = 0; i < due.Count; i++)
            {
                if (!Apply(due[i]))
                {
                    ReportFailure(due[i], CoopMutationFailureCode.ApplyFailed);
                    return false;
                }
            }
            return true;
        }

        private static bool Schedule(CoopAuthoritativeCommandData command)
        {
            CoopDisasterEventData data;
            if (command == null || command.Kind != CoopAuthoritativeCommandKind.DisasterEvent ||
                command.Phase != CoopAuthoritativeCommandPhase.Commit ||
                !CoopDisasterEventCodec.TryDecode(command.Payload, out data)) return false;
            lock (QueueLock)
            {
                if (Scheduled.Count >= QueueCapacity ||
                    (RequiresPendingKindLock(data.Kind) && PendingKinds.Contains(data.Kind)) ||
                    !Sequences.TryAccept(command.Sequence)) return false;
                Scheduled.Add(command);
                if (RequiresPendingKindLock(data.Kind)) PendingKinds.Add(data.Kind);
                return true;
            }
        }

        private static void RemoveScheduled(CoopAuthoritativeCommandData command)
        {
            CoopDisasterEventData data;
            if (command == null ||
                !CoopDisasterEventCodec.TryDecode(command.Payload, out data)) return;
            lock (QueueLock)
            {
                for (int i = Scheduled.Count - 1; i >= 0; i--)
                    if (Scheduled[i].Sequence == command.Sequence) Scheduled.RemoveAt(i);
                if (RequiresPendingKindLock(data.Kind)) PendingKinds.Remove(data.Kind);
            }
        }

        private static bool Apply(CoopAuthoritativeCommandData command)
        {
            CoopDisasterEventData data;
            if (!CoopDisasterEventCodec.TryDecode(command.Payload, out data)) return false;
            sApplying = true;
            try
            {
                Vector3 target = data.HasTarget
                    ? new Vector3(data.TargetX, data.TargetY, data.TargetZ)
                    : Vector3.zero;
                DisasterManager manager = Singleton<DisasterManager>.getInstance();
                switch (data.Kind)
                {
                    case CoopDisasterEventKind.Sandstorm:
                        manager.getSandstorm().trigger();
                        break;
                    case CoopDisasterEventKind.Blizzard:
                        manager.getBlizzard().trigger();
                        break;
                    case CoopDisasterEventKind.SolarFlare:
                        manager.getSolarFlare().trigger();
                        break;
                    case CoopDisasterEventKind.Meteor:
                        CoopDeterministicRuntime.ResetDisasterRandoms(command.Sequence);
                        Singleton<MeteorManager>.getInstance().spawnMeteor(target);
                        break;
                    case CoopDisasterEventKind.Thunder:
                        CoopDeterministicRuntime.ResetDisasterRandoms(command.Sequence);
                        Singleton<ThunderstormManager>.getInstance().spawnThunder(target);
                        break;
                    case CoopDisasterEventKind.MeteorConstructionImpact:
                    case CoopDisasterEventKind.LightningConstructionImpact:
                        CoopObjectResolution constructionResolution;
                        Construction construction = CoopObjectBridge.ResolveSelectable(
                            data.TargetReference,
                            out constructionResolution) as Construction;
                        if (construction == null) return false;
                        construction.onImpact(
                            data.Kind == CoopDisasterEventKind.MeteorConstructionImpact
                                ? ImpactType.Meteor
                                : ImpactType.Lightning);
                        break;
                    case CoopDisasterEventKind.MeteorCharacterImpact:
                    case CoopDisasterEventKind.LightningCharacterImpact:
                        Character.onImpact(
                            data.Kind == CoopDisasterEventKind.MeteorCharacterImpact
                                ? ImpactType.Meteor
                                : ImpactType.Lightning,
                            target);
                        break;
                    case CoopDisasterEventKind.LightningRecharge:
                        CoopObjectResolution moduleResolution;
                        Module module = CoopObjectBridge.ResolveModule(
                            data.TargetReference,
                            out moduleResolution);
                        if (module == null) return false;
                        module.rechargeGrid();
                        break;
                    default:
                        return false;
                }
                CoopPlugin.Log.LogInfo("[Coop] authoritative disaster event applied: " +
                    data.Kind + " seq=" + command.Sequence);
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] disaster event apply failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (RequiresPendingKindLock(data.Kind))
                {
                    lock (QueueLock) PendingKinds.Remove(data.Kind);
                }
                sApplying = false;
            }
        }

        private static bool RequiresPendingKindLock(CoopDisasterEventKind kind)
        {
            return kind >= CoopDisasterEventKind.Sandstorm &&
                kind <= CoopDisasterEventKind.Thunder;
        }

        private static void ReportFailure(
            CoopAuthoritativeCommandData command,
            CoopMutationFailureCode code)
        {
            if (command == null) return;
            SyncManager.Instance?.ReportMutationFailure(
                CoopMutationKind.Disaster,
                command.Sequence,
                code);
        }
    }

    [HarmonyPatch(typeof(Construction), "onImpact")]
    public static class CoopConstructionImpactPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Construction __instance, ImpactType impactType)
        {
            return !CoopDisasterSync.ShouldSuppressConstructionImpact(
                __instance, impactType);
        }
    }

    [HarmonyPatch(typeof(Module), "onImpact")]
    public static class CoopModuleImpactPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Module __instance, ImpactType impactType)
        {
            return !CoopDisasterSync.ShouldSuppressConstructionImpact(
                __instance, impactType);
        }
    }

    [HarmonyPatch(typeof(Connection), "onImpact")]
    public static class CoopConnectionImpactPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Connection __instance, ImpactType impactType)
        {
            return !CoopDisasterSync.ShouldSuppressConstructionImpact(
                __instance, impactType);
        }
    }

    [HarmonyPatch(
        typeof(Character),
        "onImpact",
        new Type[] { typeof(ImpactType), typeof(Vector3) })]
    public static class CoopCharacterImpactPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ImpactType impactType, Vector3 position)
        {
            return !CoopDisasterSync.ShouldSuppressCharacterImpact(
                impactType, position);
        }
    }

    [HarmonyPatch(typeof(Module), "rechargeGrid")]
    public static class CoopLightningRechargePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Module __instance)
        {
            return !CoopDisasterSync.ShouldSuppressLightningRecharge(__instance);
        }
    }

    [HarmonyPatch(typeof(Sandstorm), "trigger")]
    public static class CoopSandstormTriggerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CoopDisasterSync.ShouldSuppress(
                CoopDisasterEventKind.Sandstorm, Vector3.zero, false);
        }
    }

    [HarmonyPatch(typeof(Blizzard), "trigger")]
    public static class CoopBlizzardTriggerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CoopDisasterSync.ShouldSuppress(
                CoopDisasterEventKind.Blizzard, Vector3.zero, false);
        }
    }

    [HarmonyPatch(typeof(SolarFlare), "trigger")]
    public static class CoopSolarFlareTriggerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CoopDisasterSync.ShouldSuppress(
                CoopDisasterEventKind.SolarFlare, Vector3.zero, false);
        }
    }

    [HarmonyPatch(typeof(MeteorManager), "spawnMeteor")]
    public static class CoopMeteorSpawnPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Vector3 target)
        {
            return !CoopDisasterSync.ShouldSuppress(
                CoopDisasterEventKind.Meteor,
                target,
                target != Vector3.zero);
        }
    }

    [HarmonyPatch(typeof(ThunderstormManager), "spawnThunder")]
    public static class CoopThunderSpawnPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Vector3 target)
        {
            return !CoopDisasterSync.ShouldSuppress(
                CoopDisasterEventKind.Thunder,
                target,
                target != Vector3.zero);
        }
    }
}
