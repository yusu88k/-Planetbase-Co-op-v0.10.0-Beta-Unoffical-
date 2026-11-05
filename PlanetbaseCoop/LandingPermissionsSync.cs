using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Converts host UI mutations of landing permissions into future-tick
    /// commands so LandingShipManager observes the same values on both peers.
    /// </summary>
    public static class CoopLandingPermissionsSync
    {
        private const int QueueCapacity = 64;

        private static readonly object QueueLock = new object();
        private static readonly CoopLandingPermissionsQueue Queue =
            new CoopLandingPermissionsQueue(QueueCapacity);
        private static readonly Queue<CoopAuthoritativeCommandData> PendingRequests =
            new Queue<CoopAuthoritativeCommandData>();
        private static readonly CoopSequenceWatermarks RequestSequences =
            new CoopSequenceWatermarks();

        private static CoopLandingPermissionsData sCommitted;
        private static long sNextSequence;
        private static long sNextRequestSequence;
        private static bool sApplying;

        public static bool Applying { get { return sApplying; } }

        public static bool ApplyAuthoritativeRecovery(CoopLandingPermissionsData permissions)
        {
            if (permissions == null || !Apply(permissions)) return false;
            lock (QueueLock) sCommitted = permissions.Clone();
            return true;
        }

        public static void Initialize()
        {
            ResetForSession();
        }

        public static void ResetForSession()
        {
            lock (QueueLock)
            {
                Queue.Clear();
                PendingRequests.Clear();
                RequestSequences.Reset();
                sCommitted = null;
                sNextSequence = 0;
                sNextRequestSequence = 0;
            }
            sApplying = false;
        }

        public static bool SeedCommittedFromCurrent(long sessionId, long currentTick)
        {
            CoopLandingPermissionsData current;
            if (!TryCaptureCurrent(sessionId, currentTick, out current)) return false;
            lock (QueueLock) sCommitted = current.Clone();
            return true;
        }

        public static void QueueRequest(CoopAuthoritativeCommandData request)
        {
            if (SyncManager.Role != "host" || request == null ||
                request.Kind != CoopAuthoritativeCommandKind.SetLandingPermissions ||
                request.Phase != CoopAuthoritativeCommandPhase.Request) return;
            CoopLandingPermissionsData ignored;
            if (!CoopLandingPermissionsCodec.TryDecode(request.Payload, out ignored))
            {
                CoopPlugin.Log.LogWarning("[Coop] invalid guest landing-permissions request rejected");
                return;
            }
            lock (QueueLock)
            {
                if (PendingRequests.Count >= QueueCapacity)
                {
                    CoopPlugin.Log.LogWarning("[Coop] guest landing-permissions request queue full");
                    return;
                }
                PendingRequests.Enqueue(request);
            }
        }

        public static void QueuePacket(byte[] packet)
        {
            SyncManager sync = SyncManager.Instance;
            if (SyncManager.Role != "guest" || sync == null) return;

            CoopLandingPermissionsData command;
            if (!CoopProtocol.TryParseLandingPermissionsPacket(packet, out command))
            {
                Fail("invalid landing-permissions packet");
                return;
            }

            long activeSessionId;
            if (!sync.TryGetRunningSessionId(out activeSessionId) ||
                !CoopCommandSessionPolicy.CanAccept(
                    true, activeSessionId, command.SessionId))
            {
                Fail("landing-permissions packet does not belong to the running session");
                return;
            }

            long currentTick = Interlocked.Read(ref sync.TickIndex);
            bool accepted;
            lock (QueueLock)
            {
                accepted = command.ApplyTick > currentTick &&
                    Queue.TrySchedule(command, currentTick);
            }
            if (!accepted)
            {
                Fail("late, duplicate or overflowing landing-permissions packet");
                return;
            }
        }

        public static bool BeforeSimulationTick(long currentTick)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return true;

            long activeSessionId;
            if (!SyncManager.Instance.TryGetRunningSessionId(out activeSessionId))
                return true;

            if (SyncManager.Role == "host")
            {
                if (!ProcessGuestRequests(activeSessionId, currentTick)) return false;
            }

            var due = new List<CoopLandingPermissionsData>();
            lock (QueueLock)
            {
                if (Queue.HasLate(currentTick))
                    return Fail("landing-permissions command missed its apply tick");

                CoopLandingPermissionsData command;
                while (Queue.TryDequeueDue(currentTick, out command)) due.Add(command);
            }

            for (int i = 0; i < due.Count; i++)
            {
                if (!CoopCommandSessionPolicy.CanAccept(
                        true, activeSessionId, due[i].SessionId) ||
                    !Apply(due[i])) return false;
                sCommitted = due[i].Clone();
            }

            CoopLandingPermissionsData current;
            if (!TryCaptureCurrent(activeSessionId, currentTick, out current))
                return Fail("landing permissions could not be captured");

            if (sCommitted == null)
            {
                sCommitted = current.Clone();
                return true;
            }

            if (SameValues(current, sCommitted)) return true;

            // The UI changed state between simulation ticks. Revert it now so
            // the host manager never observes it early, then publish one
            // command that both peers will apply on the same future tick.
            bool sameValuesAlreadyPending;
            lock (QueueLock)
                sameValuesAlreadyPending = Queue.HasPendingValues(current);
            if (!Apply(sCommitted)) return false;
            if (sameValuesAlreadyPending) return true;
            return SyncManager.Role == "host"
                ? ScheduleHost(current, currentTick)
                : SubmitGuestRequest(current, activeSessionId);
        }

        private static bool ProcessGuestRequests(long activeSessionId, long currentTick)
        {
            while (true)
            {
                CoopAuthoritativeCommandData request = null;
                lock (QueueLock)
                {
                    if (PendingRequests.Count > 0) request = PendingRequests.Dequeue();
                }
                if (request == null) return true;
                if (!CoopCommandSessionPolicy.CanAccept(
                        true, activeSessionId, request.SessionId) ||
                    !RequestSequences.TryAccept(request.Sequence))
                {
                    CoopPlugin.Log.LogWarning("[Coop] duplicate/stale landing-permissions request rejected: " +
                        request.Sequence);
                    continue;
                }
                CoopLandingPermissionsData requested;
                if (!CoopLandingPermissionsCodec.TryDecode(request.Payload, out requested))
                    continue;
                requested.SessionId = activeSessionId;
                requested.Sequence = 1;
                requested.ApplyTick = currentTick;
                if (!ScheduleHost(requested, currentTick)) return false;
            }
        }

        private static bool SubmitGuestRequest(
            CoopLandingPermissionsData requested,
            long activeSessionId)
        {
            SyncManager sync = SyncManager.Instance;
            if (sync == null || !CoopFocusState.IsFocused) return true;
            long localSequence = Interlocked.Increment(ref sNextRequestSequence);
            long sequence = CoopPlacementRequestIds.For(CoopRole.Guest, localSequence);
            CoopAuthoritativeCommandData request = new CoopAuthoritativeCommandData(
                activeSessionId,
                sequence,
                0,
                CoopAuthoritativeCommandKind.SetLandingPermissions,
                CoopAuthoritativeCommandPhase.Request,
                CoopLandingPermissionsCodec.Encode(requested));
            if (!sync.TrySendPacket(CoopProtocol.CreateAuthoritativeCommandPacket(request)))
            {
                CoopPlugin.Log.LogWarning("[Coop] guest landing-permissions request could not be sent");
                return true;
            }
            CoopStatusOverlay.Notify("INIS IZNI HOSTA GONDERILDI");
            return true;
        }

        private static bool ScheduleHost(
            CoopLandingPermissionsData requested,
            long currentTick)
        {
            long applyLeadTicks = CoopCommandLeadPolicy.ApplyLeadTicks(
                CoopTimeSync.CurrentScaleIndex,
                CoopNetworkTiming.CurrentBudgetMilliseconds);
            if (currentTick > CoopProtocol.MaxWorldTick - applyLeadTicks)
                return Fail("landing-permissions apply tick overflow");

            CoopLandingPermissionsData scheduled = requested.Clone();
            scheduled.Sequence = Interlocked.Increment(ref sNextSequence);
            scheduled.ApplyTick = currentTick + applyLeadTicks;

            bool accepted;
            lock (QueueLock)
            {
                accepted = Queue.TrySchedule(scheduled, currentTick);
            }
            if (!accepted)
                return Fail("host landing-permissions queue rejected a command");

            if (!SyncManager.Instance.TrySendPacket(
                    CoopProtocol.CreateLandingPermissionsPacket(scheduled)))
            {
                lock (QueueLock) Queue.Clear();
                return Fail("landing-permissions packet could not be sent");
            }

            CoopStatusOverlay.Notify("INIS IZINLERI ORTAK TICKE PLANLANDI");
            CoopPlugin.Log.LogInfo(string.Format(
                "[Coop] inis izinleri planlandi: seq={0} tick={1}",
                scheduled.Sequence, scheduled.ApplyTick));
            return true;
        }

        private static bool TryCaptureCurrent(
            long sessionId,
            long currentTick,
            out CoopLandingPermissionsData data)
        {
            data = null;
            try
            {
                LandingShipManager manager = Singleton<LandingShipManager>.getInstance();
                LandingPermissions permissions = manager == null
                    ? null
                    : manager.getLandingPermissions();
                if (permissions == null) return false;

                List<Specialization> specializations =
                    SpecializationList.getColonistSpecializations();
                if (specializations == null || specializations.Count < 1 ||
                    specializations.Count > CoopLandingPermissionsValidator.MaxSpecializationCount)
                    return false;

                int[] percentages = new int[specializations.Count];
                int total = 0;
                for (int i = 0; i < specializations.Count; i++)
                {
                    percentages[i] =
                        permissions.getSpecializationPercentage(specializations[i]).get();
                    total += percentages[i];
                }

                int correction =
                    CoopLandingPermissionsValidator.RequiredPercentageTotal - total;
                int correctedLast = percentages[percentages.Length - 1] + correction;
                if (correctedLast < 0 || correctedLast > 100) return false;
                percentages[percentages.Length - 1] = correctedLast;

                var candidate = new CoopLandingPermissionsData
                {
                    SessionId = sessionId,
                    Sequence = 1,
                    ApplyTick = currentTick,
                    ColonistsAllowed = permissions.areColonistsAllowed(),
                    MerchantsAllowed = permissions.areMerchantsAllowed(),
                    VisitorsAllowed = permissions.areVisitorsAllowed(),
                    SpecializationPercentages = percentages
                };
                return CoopLandingPermissionsValidator.TryValidate(candidate, out data);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] inis izinleri okunamadi: " + ex.Message);
                return false;
            }
        }

        private static bool Apply(CoopLandingPermissionsData data)
        {
            sApplying = true;
            try
            {
                CoopLandingPermissionsData normalized;
                if (!CoopLandingPermissionsValidator.TryValidate(data, out normalized))
                    return Fail("landing-permissions command failed validation at apply");

                LandingShipManager manager = Singleton<LandingShipManager>.getInstance();
                LandingPermissions permissions = manager == null
                    ? null
                    : manager.getLandingPermissions();
                List<Specialization> specializations =
                    SpecializationList.getColonistSpecializations();
                if (permissions == null || specializations == null ||
                    specializations.Count != normalized.SpecializationPercentages.Length)
                    return Fail("landing-permissions specialization layout differs");

                permissions.getColonistRefBool().set(normalized.ColonistsAllowed);
                permissions.getMerchantRefBool().set(normalized.MerchantsAllowed);
                permissions.getVisitorRefBool().set(normalized.VisitorsAllowed);
                for (int i = 0; i < specializations.Count; i++)
                {
                    permissions.getSpecializationPercentage(specializations[i])
                        .set(normalized.SpecializationPercentages[i]);
                }
                return true;
            }
            catch (Exception ex)
            {
                return Fail("landing-permissions apply failed: " + ex.Message);
            }
            finally { sApplying = false; }
        }

        public static bool IsLandingPermissionBool(RefBool value)
        {
            if (value == null) return false;
            try
            {
                LandingShipManager manager = Singleton<LandingShipManager>.getInstance();
                LandingPermissions permissions = manager == null
                    ? null
                    : manager.getLandingPermissions();
                return permissions != null &&
                    (ReferenceEquals(value, permissions.getColonistRefBool()) ||
                     ReferenceEquals(value, permissions.getMerchantRefBool()) ||
                     ReferenceEquals(value, permissions.getVisitorRefBool()));
            }
            catch (Exception) { return false; }
        }

        private static bool SameValues(
            CoopLandingPermissionsData left,
            CoopLandingPermissionsData right)
        {
            if (left == null || right == null ||
                left.ColonistsAllowed != right.ColonistsAllowed ||
                left.MerchantsAllowed != right.MerchantsAllowed ||
                left.VisitorsAllowed != right.VisitorsAllowed ||
                left.SpecializationPercentages == null ||
                right.SpecializationPercentages == null ||
                left.SpecializationPercentages.Length !=
                    right.SpecializationPercentages.Length)
                return false;

            for (int i = 0; i < left.SpecializationPercentages.Length; i++)
            {
                if (left.SpecializationPercentages[i] !=
                    right.SpecializationPercentages[i]) return false;
            }
            return true;
        }

        private static bool Fail(string reason)
        {
            SyncManager sync = SyncManager.Instance;
            if (sync != null && sync.ReportRuntimeInvariantFailure(reason))
                CoopPlugin.Log.LogError("[Coop] " + reason);
            return false;
        }
    }

    [HarmonyPatch(typeof(RefValue<bool>), "set", new Type[] { typeof(bool) })]
    internal static class CoopLandingPermissionBoolPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(RefValue<bool> __instance)
        {
            RefBool permission = __instance as RefBool;
            if (!SyncManager.FullMode || CoopLandingPermissionsSync.Applying ||
                permission == null ||
                !CoopLandingPermissionsSync.IsLandingPermissionBool(permission)) return true;
            SyncManager sync = SyncManager.Instance;
            return sync == null || !sync.IsRecoveryInProgress();
        }
    }

    [HarmonyPatch(typeof(LandingPermissions), "reset")]
    internal static class CoopLandingPermissionResetPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!SyncManager.FullMode || CoopLandingPermissionsSync.Applying) return true;
            SyncManager sync = SyncManager.Instance;
            return sync == null || !sync.IsRecoveryInProgress();
        }
    }
}
