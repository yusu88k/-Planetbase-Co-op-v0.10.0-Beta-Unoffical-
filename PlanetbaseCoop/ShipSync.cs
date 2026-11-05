using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Planetbase;
using UnityEngine;
using PlanetbaseModule = Planetbase.Module;
using UnityRandom = UnityEngine.Random;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Host-authoritative landing-ship scheduler. Vanilla spawn decisions are
    /// made at their normal manager tick, but creation is moved to one shared
    /// future simulation tick on both peers.
    /// </summary>
    public static class CoopShipSync
    {
        private const int QueueCapacity = 128;

        private static readonly object QueueLock = new object();
        private static readonly CoopShipSpawnQueue Queue =
            new CoopShipSpawnQueue(QueueCapacity);
        private static readonly CoopShipLandingReservations Reservations =
            new CoopShipLandingReservations();
        private static readonly Queue<CoopAuthoritativeCommandData> VisitorEventRequests =
            new Queue<CoopAuthoritativeCommandData>();
        private static readonly CoopSequenceWatermarks VisitorRequestSequences =
            new CoopSequenceWatermarks();
        private static readonly BindingFlags InstanceFlags =
            BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo TimeToNextIntruderField =
            typeof(LandingShipManager).GetField("mTimeToNextIntruder", InstanceFlags);
        private static readonly MethodInfo ResetIntruderTimerMethod =
            typeof(LandingShipManager).GetMethod("resetIntruderTimer", InstanceFlags);

        private static long sNextSequence;
        private static long sNextVisitorRequestSequence;
        private static string sOfferedVisitorEventType = string.Empty;
        private static int sOfferedVisitorCount;

        public static void Initialize()
        {
            ResetForSession();
        }

        public static void ResetForSession()
        {
            lock (QueueLock)
            {
                Queue.Clear();
                Reservations.Clear();
                VisitorEventRequests.Clear();
                VisitorRequestSequences.Reset();
                sNextSequence = 0;
                sNextVisitorRequestSequence = 0;
                sOfferedVisitorEventType = string.Empty;
                sOfferedVisitorCount = 0;
            }
        }

        public static void RecordVisitorEventOffer(VisitorEvent visitorEvent)
        {
            if (!SyncManager.FullMode || visitorEvent == null ||
                visitorEvent.getVisitorEventType() == null) return;
            lock (QueueLock)
            {
                sOfferedVisitorEventType =
                    visitorEvent.getVisitorEventType().GetType().Name;
                sOfferedVisitorCount = visitorEvent.getVisitorCount();
            }
        }

        public static void QueueVisitorEventCommand(
            CoopAuthoritativeCommandData command)
        {
            SyncManager sync = SyncManager.Instance;
            long activeSessionId;
            CoopVisitorEventCommandData ignored;
            if (SyncManager.Role != "host" || command == null ||
                command.Kind != CoopAuthoritativeCommandKind.VisitorEventAccept ||
                command.Phase != CoopAuthoritativeCommandPhase.Request ||
                sync == null || !sync.TryGetRunningSessionId(out activeSessionId) ||
                !CoopCommandSessionPolicy.CanAccept(
                    true, activeSessionId, command.SessionId) ||
                !CoopVisitorEventCommandCodec.TryDecode(command.Payload, out ignored))
                return;

            bool rejected;
            lock (QueueLock)
            {
                rejected = VisitorEventRequests.Count >= QueueCapacity;
                if (!rejected) VisitorEventRequests.Enqueue(command);
            }
            if (rejected)
                CoopPlugin.Log.LogWarning(
                    "[Coop] visitor-event request queue full; newest request rejected: " +
                    command.Sequence);
        }

        public static bool RequestVisitorEvent(VisitorEvent visitorEvent)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return false;

            long sessionId;
            VisitorEventType eventType = visitorEvent == null
                ? null
                : visitorEvent.getVisitorEventType();
            if (eventType == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out sessionId))
            {
                CoopStatusOverlay.Notify("ZIYARETCI ETKINLIGI GONDERILEMEDI");
                return true;
            }

            byte[] payload;
            try
            {
                CoopVisitorEventCommandData data =
                    new CoopVisitorEventCommandData(
                        eventType.GetType().Name,
                        visitorEvent.getVisitorCount());
                payload = CoopVisitorEventCommandCodec.Encode(data);
                if (!MatchesOfferedVisitorEvent(
                        data,
                        SyncManager.Role == "guest"))
                {
                    CoopStatusOverlay.Notify("ZIYARETCI ETKINLIGI ARTIK GECERLI DEGIL");
                    return true;
                }
            }
            catch (ArgumentException ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] visitor event rejected locally: " + ex.Message);
                CoopStatusOverlay.Notify("GECERSIZ ZIYARETCI ETKINLIGI");
                return true;
            }

            long localSequence = Interlocked.Increment(
                ref sNextVisitorRequestSequence);
            CoopAuthoritativeCommandData request =
                new CoopAuthoritativeCommandData(
                    sessionId,
                    CoopPlacementRequestIds.For(
                        SyncManager.Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                        localSequence),
                    0,
                    CoopAuthoritativeCommandKind.VisitorEventAccept,
                    CoopAuthoritativeCommandPhase.Request,
                    payload);
            if (SyncManager.Role == "guest")
            {
                if (!SyncManager.Instance.TrySendPacket(
                        CoopProtocol.CreateAuthoritativeCommandPacket(request)))
                    CoopStatusOverlay.Notify("ZIYARETCI ISTEGI GONDERILEMEDI");
            }
            else
            {
                QueueVisitorEventCommand(request);
            }
            return true;
        }

        public static void QueueSpawnPacket(byte[] packet)
        {
            SyncManager sync = SyncManager.Instance;
            if (SyncManager.Role != "guest" || sync == null) return;

            CoopShipSpawnData spawn;
            if (!CoopProtocol.TryParseShipSpawnPacket(packet, out spawn))
            {
                Fail("invalid ship-spawn packet");
                return;
            }

            long activeSessionId;
            if (!sync.TryGetRunningSessionId(out activeSessionId) ||
                !CoopCommandSessionPolicy.CanAccept(
                    true, activeSessionId, spawn.SessionId))
            {
                Fail("ship-spawn packet does not belong to the running session");
                return;
            }

            long currentTick = Interlocked.Read(ref sync.TickIndex);
            bool accepted;
            lock (QueueLock)
            {
                accepted = spawn.ApplyTick > currentTick &&
                    Queue.TrySchedule(spawn, currentTick);
            }

            if (!accepted)
            {
                Fail("late, duplicate or overflowing ship-spawn packet");
                return;
            }

            CoopPlugin.Log.LogInfo(string.Format(
                "[Coop] gemi komutu alindi: seq={0} tick={1} kind={2} module={3}",
                spawn.Sequence, spawn.ApplyTick, spawn.Kind, spawn.ModuleId));
        }

        public static bool ApplyScheduled(long currentTick)
        {
            if (!SyncManager.FullMode) return true;

            if (SyncManager.Role == "host" &&
                !ProcessVisitorEventRequests(currentTick)) return false;

            var due = new List<CoopShipSpawnData>();
            lock (QueueLock)
            {
                if (Queue.HasLate(currentTick))
                    return Fail("ship-spawn command missed its apply tick");

                CoopShipSpawnData spawn;
                while (Queue.TryDequeueDue(currentTick, out spawn)) due.Add(spawn);
            }

            for (int i = 0; i < due.Count; i++)
            {
                if (!ApplySpawn(due[i])) return false;
            }
            return true;
        }

        public static bool AllowVanillaAutomaticSpawn(
            LandingShipManager manager,
            CoopShipKind kind)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return true;

            if (!CoopShipAuthorityPolicy.AllowAutomaticSpawn(
                    SyncManager.FullMode, SyncManager.Role)) return false;

            long sessionId;
            if (!SyncManager.Instance.TryGetRunningSessionId(out sessionId))
                return false;

            UnityEngine.Random.State savedRandomState = UnityRandom.state;
            try
            {
                LandingShip.Size size;
                PlanetbaseModule module;
                int visitorType = CoopShipSpawnValidator.NotAVisitor;
                bool intruders = false;

                if (kind == CoopShipKind.Visitor)
                {
                    ModuleType starport =
                        TypeList<ModuleType, ModuleTypeList>.find<ModuleTypeStarport>();
                    if (!PlanetbaseModule.isModuleTypeBuilt(starport)) return false;

                    size = UnityRandom.Range(0, 2) != 0
                        ? LandingShip.Size.Large
                        : LandingShip.Size.Regular;
                    module = LandingShipManager.findLandingModule(size);
                    if (module != null)
                    {
                        visitorType = UnityRandom.Range(0, 3) == 0
                            ? CoopShipSpawnValidator.MilitaryVisitor
                            : CoopShipSpawnValidator.CivilianVisitor;
                        intruders = PeekIntruderDecision(manager);
                    }
                }
                else
                {
                    size = LandingShip.Size.Regular;
                    if (LandingShipManager.findLandingModule(LandingShip.Size.Large) != null)
                    {
                        size = UnityRandom.Range(0, 2) != 0
                            ? LandingShip.Size.Large
                            : LandingShip.Size.Regular;
                    }
                    module = LandingShipManager.findLandingModule(size);
                    if (module != null && kind == CoopShipKind.Colonist)
                        intruders = PeekIntruderDecision(manager);
                }

                if (module != null)
                    ScheduleHostSpawn(
                        sessionId,
                        kind,
                        module,
                        size,
                        visitorType,
                        intruders,
                        string.Empty,
                        0);
            }
            catch (Exception ex)
            {
                Fail("ship spawn decision failed: " + ex.Message);
            }
            finally
            {
                UnityRandom.state = savedRandomState;
            }

            return false;
        }

        private static bool ProcessVisitorEventRequests(long currentTick)
        {
            while (true)
            {
                CoopAuthoritativeCommandData request = null;
                lock (QueueLock)
                {
                    if (VisitorEventRequests.Count > 0)
                        request = VisitorEventRequests.Dequeue();
                }
                if (request == null) return true;

                CoopVisitorEventCommandData data;
                if (!VisitorRequestSequences.TryAccept(request.Sequence) ||
                    !CoopVisitorEventCommandCodec.TryDecode(request.Payload, out data) ||
                    !MatchesOfferedVisitorEvent(data, true))
                {
                    CoopPlugin.Log.LogWarning(
                        "[Coop] invalid or replayed visitor-event request rejected: " +
                        request.Sequence);
                    continue;
                }

                VisitorEventType eventType =
                    TypeList<VisitorEventType, VisitorEventTypeList>.find(
                        data.EventTypeName);
                if (eventType == null)
                {
                    CoopPlugin.Log.LogWarning(
                        "[Coop] unknown visitor-event type rejected: " +
                        data.EventTypeName);
                    continue;
                }

                LandingShip.Size size = data.VisitorCount >= 9
                    ? LandingShip.Size.Large
                    : LandingShip.Size.Regular;
                UnityEngine.Random.State savedRandomState = UnityRandom.state;
                PlanetbaseModule module;
                try { module = LandingShipManager.findLandingModule(size); }
                finally { UnityRandom.state = savedRandomState; }
                int visitorType = (int)eventType.getShipType();
                if (module == null ||
                    (visitorType != CoopShipSpawnValidator.CivilianVisitor &&
                     visitorType != CoopShipSpawnValidator.MilitaryVisitor))
                {
                    CoopPlugin.Log.LogWarning(
                        "[Coop] visitor event has no valid landing site or ship type: " +
                        data.EventTypeName);
                    continue;
                }

                ScheduleHostSpawn(
                    request.SessionId,
                    CoopShipKind.Visitor,
                    module,
                    size,
                    visitorType,
                    false,
                    data.EventTypeName,
                    data.VisitorCount);
            }
        }

        private static bool MatchesOfferedVisitorEvent(
            CoopVisitorEventCommandData data,
            bool consume)
        {
            if (data == null) return false;
            lock (QueueLock)
            {
                bool matches = StringComparer.Ordinal.Equals(
                        sOfferedVisitorEventType, data.EventTypeName) &&
                    sOfferedVisitorCount == data.VisitorCount;
                if (matches && consume)
                {
                    sOfferedVisitorEventType = string.Empty;
                    sOfferedVisitorCount = 0;
                }
                return matches;
            }
        }

        private static bool PeekIntruderDecision(LandingShipManager manager)
        {
            if (manager == null || TimeToNextIntruderField == null)
                throw new MissingMemberException("LandingShipManager intruder state is unavailable");

            float remaining = (float)TimeToNextIntruderField.GetValue(manager);
            return remaining < 0f;
        }

        private static void ScheduleHostSpawn(
            long sessionId,
            CoopShipKind kind,
            PlanetbaseModule module,
            LandingShip.Size size,
            int visitorType,
            bool intruders,
            string visitorEventTypeName,
            int visitorCount)
        {
            SyncManager sync = SyncManager.Instance;
            if (sync == null || SyncManager.Role != "host") return;

            long currentTick = Interlocked.Read(ref sync.TickIndex);
            long spawnLeadTicks = CoopCommandLeadPolicy.ApplyLeadTicks(
                CoopTimeSync.CurrentScaleIndex,
                CoopNetworkTiming.CurrentBudgetMilliseconds);
            if (currentTick > CoopProtocol.MaxWorldTick - spawnLeadTicks)
            {
                Fail("ship-spawn apply tick overflow");
                return;
            }

            int moduleId = module.getId();
            lock (QueueLock)
            {
                if (!Reservations.TryReserve(moduleId))
                {
                    CoopPlugin.Log.LogInfo(
                        "[Coop] ayni inis alani icin bekleyen gemi oldugundan yeni gemi atlandi: module=" +
                        moduleId);
                    return;
                }
            }

            long sequence = Interlocked.Increment(ref sNextSequence);
            var spawn = new CoopShipSpawnData
            {
                SessionId = sessionId,
                Sequence = sequence,
                ApplyTick = currentTick + spawnLeadTicks,
                ModuleId = moduleId,
                ModuleReference = CoopObjectBridge.Capture(module, true),
                Kind = kind,
                SizeIndex = (int)size,
                VisitorType = visitorType,
                Intruders = intruders,
                VisitorEventTypeName = visitorEventTypeName ?? string.Empty,
                VisitorCount = visitorCount
            };

            bool accepted;
            lock (QueueLock)
            {
                accepted = Queue.TrySchedule(spawn, currentTick);
                if (!accepted) Reservations.Release(moduleId);
            }
            if (!accepted)
            {
                Fail("host ship-spawn queue rejected an authoritative command");
                return;
            }

            if (!sync.TrySendPacket(CoopProtocol.CreateShipSpawnPacket(spawn)))
            {
                lock (QueueLock)
                {
                    Queue.Clear();
                    Reservations.Clear();
                }
                Fail("authoritative ship-spawn packet could not be sent");
                return;
            }

            CoopPlugin.Log.LogInfo(string.Format(
                "[Coop] gemi planlandi: seq={0} tick={1} kind={2} module={3}",
                spawn.Sequence, spawn.ApplyTick, spawn.Kind, spawn.ModuleId));
        }

        private static bool ApplySpawn(CoopShipSpawnData spawn)
        {
            try
            {
                if (SyncManager.Role == "host")
                {
                    lock (QueueLock) Reservations.Release(spawn.ModuleId);
                }
                SyncManager sync = SyncManager.Instance;
                long activeSessionId;
                if (sync == null || !sync.TryGetRunningSessionId(out activeSessionId) ||
                    !CoopCommandSessionPolicy.CanAccept(
                        true, activeSessionId, spawn.SessionId))
                    return Fail("ship-spawn command does not belong to the running session");

                CoopObjectResolution moduleResolution;
                CoopObjectReference moduleReference = spawn.ModuleReference ??
                    new CoopObjectReference(spawn.ModuleId, "Module", 0f, 0f, 0f, false);
                PlanetbaseModule module = CoopObjectBridge.ResolveModule(
                    moduleReference, out moduleResolution);
                if (module == null || module.isDestroyed())
                    return MutationFail(
                        spawn,
                        moduleResolution == CoopObjectResolution.Ambiguous
                            ? CoopMutationFailureCode.TargetAmbiguous
                            : CoopMutationFailureCode.TargetMissing,
                        "ship-spawn target module is missing: " + spawn.ModuleId +
                        " resolution=" + moduleResolution);

                ModuleType moduleType = module.getModuleType();
                bool isLandingPad = moduleType ==
                    TypeList<ModuleType, ModuleTypeList>.find<ModuleTypeLandingPad>();
                bool isStarport = moduleType ==
                    TypeList<ModuleType, ModuleTypeList>.find<ModuleTypeStarport>();
                if (!CoopLandingTargetPolicy.IsValid(
                        spawn.SizeIndex,
                        isLandingPad,
                        isStarport,
                        module.isOperational(),
                        module.getTargeterCount() == 0))
                    return MutationFail(
                        spawn,
                        CoopMutationFailureCode.ValidationRejected,
                        "ship-spawn target module is not an available landing site: " +
                        spawn.ModuleId);

                if (spawn.Kind == CoopShipKind.Visitor &&
                    !PlanetbaseModule.isModuleTypeBuilt(
                        TypeList<ModuleType, ModuleTypeList>.find<ModuleTypeStarport>()))
                    return Fail("visitor ship requires an operational starport");

                LandingShipManager intruderManager = null;
                if (spawn.Intruders)
                {
                    intruderManager = Singleton<LandingShipManager>.getInstance();
                    if (intruderManager == null || ResetIntruderTimerMethod == null)
                        return Fail("LandingShipManager intruder reset is unavailable");
                }

                VisitorEventType specialEventType = null;
                if (!string.IsNullOrEmpty(spawn.VisitorEventTypeName))
                {
                    specialEventType =
                        TypeList<VisitorEventType, VisitorEventTypeList>.find(
                            spawn.VisitorEventTypeName);
                    if (specialEventType == null ||
                        (int)specialEventType.getShipType() != spawn.VisitorType)
                        return MutationFail(
                            spawn,
                            CoopMutationFailureCode.ValidationRejected,
                            "visitor-event type is missing or inconsistent: " +
                            spawn.VisitorEventTypeName);
                }

                LandingShip ship;
                LandingShip.Size size = (LandingShip.Size)spawn.SizeIndex;
                if (spawn.Kind == CoopShipKind.Merchant)
                {
                    ship = LandingShip.create<MerchantShip>(module, size);
                }
                else if (spawn.Kind == CoopShipKind.Colonist)
                {
                    ship = LandingShip.create<ColonistShip>(module, size);
                }
                else
                {
                    VisitorShip visitorShip = (VisitorShip)LandingShip.create<VisitorShip>(
                        module, size, (VisitorShipType)spawn.VisitorType);
                    ship = visitorShip;
                    if (specialEventType != null)
                        visitorShip.setVisitorEvent(
                            new VisitorEvent(specialEventType, spawn.VisitorCount));
                }

                if (spawn.Intruders)
                {
                    ResetIntruderTimerMethod.Invoke(intruderManager, null);
                    ship.setIntruders();
                }
                CoopPlugin.Log.LogInfo(string.Format(
                    "[Coop] gemi ortak tickte olusturuldu: tick={0} id={1} kind={2}",
                    spawn.ApplyTick, ship.getId(), spawn.Kind));
                return true;
            }
            catch (Exception ex)
            {
                return MutationFail(
                    spawn,
                    CoopMutationFailureCode.ApplyFailed,
                    "ship-spawn apply failed: " + ex.Message);
            }
        }

        private static bool MutationFail(
            CoopShipSpawnData spawn,
            CoopMutationFailureCode code,
            string reason)
        {
            CoopPlugin.Log.LogError("[Coop] " + reason);
            if (spawn != null)
            {
                SyncManager.Instance?.ReportMutationFailure(
                    CoopMutationKind.Ship,
                    spawn.Sequence,
                    code);
            }
            return false;
        }

        private static bool Fail(string reason)
        {
            SyncManager sync = SyncManager.Instance;
            if (sync != null && sync.ReportRuntimeInvariantFailure(reason))
                CoopPlugin.Log.LogError("[Coop] " + reason);
            return false;
        }
    }

    [HarmonyPatch(typeof(LandingShipManager), "trySpawnMerchantShip")]
    public static class CoopMerchantShipSpawnPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LandingShipManager __instance)
        {
            return CoopShipSync.AllowVanillaAutomaticSpawn(
                __instance, CoopShipKind.Merchant);
        }
    }

    [HarmonyPatch(typeof(LandingShipManager), "trySpawnColonistShip")]
    public static class CoopColonistShipSpawnPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LandingShipManager __instance)
        {
            return CoopShipSync.AllowVanillaAutomaticSpawn(
                __instance, CoopShipKind.Colonist);
        }
    }

    [HarmonyPatch(typeof(LandingShipManager), "trySpawnVisitorShip")]
    public static class CoopVisitorShipSpawnPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LandingShipManager __instance)
        {
            return CoopShipSync.AllowVanillaAutomaticSpawn(
                __instance, CoopShipKind.Visitor);
        }
    }

    [HarmonyPatch(typeof(GameStateGame), "onEventAccepted")]
    public static class CoopVisitorEventGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(GameStateGame __instance, object parameter)
        {
            if (!SyncManager.FullMode) return true;
            bool intercepted = CoopShipSync.RequestVisitorEvent(
                parameter as VisitorEvent);
            try
            {
                FieldInfo gameGuiField = typeof(GameStateGame).GetField(
                    "mGameGui",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                GameGui gameGui = gameGuiField == null
                    ? null
                    : gameGuiField.GetValue(__instance) as GameGui;
                gameGui?.setWindow(null);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] visitor event window could not be closed: " + ex.Message);
            }
            return !intercepted;
        }
    }

    [HarmonyPatch(typeof(VisitorEventManager), "onEventTriggered")]
    public static class CoopVisitorEventOfferPatch
    {
        [HarmonyPrefix]
        public static void Prefix(VisitorEvent visitorEvent)
        {
            CoopShipSync.RecordVisitorEventOffer(visitorEvent);
        }
    }
}
