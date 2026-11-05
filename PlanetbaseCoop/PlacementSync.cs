using System;
using System.Collections.Generic;
using Reflection = System.Reflection;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Host-authoritative placement preview and commit path.
    /// Preview packets are ephemeral; only an approved command scheduled for a
    /// shared simulation tick is allowed to create a real construction.
    /// </summary>
    public sealed class PlacementSync
    {
        public static PlacementSync Instance;

        private readonly object mPreviewLock = new object();
        private readonly object mCommandLock = new object();
        private readonly object mSelectionLock = new object();
        private CoopPlacementPreviewData mPendingRemotePreview;
        private CoopPlacementPreviewData mLocalGuestPreview;
        private CoopPlacementPreviewData mLastSentPreview;
        private long mLocalSequence;
        private long mNextRequestId;
        private long mLastGuestRequestId;
        private long mAwaitingGuestRequestId;
        private long mPendingLocalPlacementPreviewRequestId;
        private long mNextConnectionRequestId;
        private long mAwaitingConnectionRequestId;
        private long mNextRecycleRequestId;
        private long mAwaitingRecycleRequestId;
        private float mGuestRequestSentAt = -100f;
        private float mConnectionRequestSentAt = -100f;
        private float mRecycleRequestSentAt = -100f;
        private readonly Queue<byte[]> mPendingRequests = new Queue<byte[]>();
        private readonly Queue<byte[]> mPendingResults = new Queue<byte[]>();
        private readonly Queue<byte[]> mPendingConnectionRequests = new Queue<byte[]>();
        private readonly Queue<byte[]> mPendingConnectionResults = new Queue<byte[]>();
        private readonly Queue<byte[]> mPendingRecycleRequests = new Queue<byte[]>();
        private readonly Queue<byte[]> mPendingRecycleResults = new Queue<byte[]>();
        private const int MaxPendingCommandPackets = 128;
        private readonly List<ScheduledPlacement> mScheduled = new List<ScheduledPlacement>();
        private readonly List<ScheduledConnection> mScheduledConnections = new List<ScheduledConnection>();
        private readonly List<ScheduledRecycle> mScheduledRecycles = new List<ScheduledRecycle>();
        private readonly CoopSequenceWatermarks mScheduledRequestIds =
            new CoopSequenceWatermarks();
        private readonly CoopSequenceWatermarks mScheduledConnectionIds =
            new CoopSequenceWatermarks();
        private readonly CoopSequenceWatermarks mScheduledRecycleIds =
            new CoopSequenceWatermarks();
        private static readonly Reflection.FieldInfo GameMenuSystemField =
            typeof(GameStateGame).GetField(
                "mMenuSystem",
                Reflection.BindingFlags.Instance | Reflection.BindingFlags.NonPublic);

        private CoopPlacementPreviewData mRemotePreview;
        private GameObject mRemoteGhost;
        private MeshRenderer mRemoteGhostRenderer;
        private Material mRemoteGhostMaterial;
        private string mRemoteGhostKey;
        private GameObject mRemotePlacementRing;
        private LineRenderer mRemotePlacementRingRenderer;
        private long mLastRemoteSequence;
        private CoopSelectionData mPendingRemoteSelection;
        private CoopSelectionData mLastSentSelection;
        private long mLocalSelectionSequence;
        private readonly CoopRemoteSelectionTracker mRemoteSelectionTracker = new CoopRemoteSelectionTracker();
        private CoopObjectReference mRemotePrimaryReference;
        private CoopObjectReference mRemoteLinkReference;
        private Selectable mRemotePrimaryTarget;
        private Selectable mRemoteLinkTarget;
        private GameObject mRemotePrimaryMarker;
        private GameObject mRemoteLinkMarker;
        private LineRenderer mRemotePrimaryMarkerRenderer;
        private LineRenderer mRemoteLinkMarkerRenderer;
        private GameObject mLocalGhost;
        private Material mLocalGhostMaterial;
        private bool mApplyingAuthoritative;
        private volatile bool mSessionResetRequested;
        private float mLastPreviewSendTime = -100f;
        private const float PreviewSendInterval = 0.04f;
        private sealed class ScheduledPlacement
        {
            public long ApplyTick;
            public CoopPlacementCommandData Command;
        }

        private sealed class ScheduledConnection
        {
            public long ApplyTick;
            public CoopConnectionCommandData Command;
        }

        private sealed class ScheduledRecycle
        {
            public long ApplyTick;
            public CoopRecycleCommandData Command;
        }

        public static void Create()
        {
            if (!SyncManager.FullMode) return;
            Instance = new PlacementSync();
            CoopPlugin.Log.LogInfo("[Coop] PlacementSync hazir - host-authoritative preview");
        }

        public bool ApplyingAuthoritative => mApplyingAuthoritative;
        public bool ShouldFreezeLocalPreview =>
            CoopPlacementPreviewLifecycle.ShouldFreeze(
                mAwaitingGuestRequestId,
                mPendingLocalPlacementPreviewRequestId);

        public void OnFrame()
        {
            if (!SyncManager.FullMode) return;
            try
            {
                if (mSessionResetRequested)
                {
                    mSessionResetRequested = false;
                    ResetForSession();
                }
                ProcessPendingNetworkData();
                CheckPendingRequestTimeouts();
                ApplyRemoteSelection();
                CaptureAndSendSelection();
                CaptureAndSendLocalPreview();
                ApplyLocalGuestPreview();
                ApplyRemotePreview();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] placement sync hatasi: " + ex.Message);
            }
        }

        public void PublishGuestPreview(CoopPlacementPreviewData preview)
        {
            lock (mPreviewLock)
            {
                mLocalGuestPreview = preview == null ? null : ClonePreview(preview);
            }
        }

        public void QueuePreviewPacket(byte[] packet)
        {
            CoopPlacementPreviewData preview;
            if (!CoopProtocol.TryParsePreviewPacket(packet, out preview)) return;
            lock (mPreviewLock)
            {
                if (preview.Sequence <= mLastRemoteSequence) return;
                mLastRemoteSequence = preview.Sequence;
                mPendingRemotePreview = preview;
            }
        }

        public void QueuePlacementRequest(byte[] packet)
        {
            CoopPlacementCommandData command;
            long activeSessionId;
            if (SyncManager.Role != "host" ||
                !CoopProtocol.TryParsePlacementRequestPacket(packet, out command) ||
                SyncManager.Instance == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out activeSessionId) ||
                command.SessionId != activeSessionId ||
                !CoopSequencePolicy.IsFromRole(command.RequestId, CoopRole.Guest)) return;
            bool queued;
            lock (mCommandLock) queued = EnqueueBounded(mPendingRequests, packet);
            if (!queued) LogQueueOverflow("placement request", command.RequestId);
        }

        public void QueuePlacementResult(byte[] packet)
        {
            bool accepted;
            long applyTick;
            CoopPlacementCommandData command;
            long activeSessionId;
            if (SyncManager.Role != "guest" ||
                !CoopProtocol.TryParsePlacementResultPacket(
                    packet, out accepted, out applyTick, out command) ||
                SyncManager.Instance == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out activeSessionId) ||
                command.SessionId != activeSessionId) return;
            bool queued;
            lock (mCommandLock) queued = EnqueueBounded(mPendingResults, packet);
            if (!queued) ReportResultQueueOverflow(
                "placement result", accepted, CoopMutationKind.Placement, command.RequestId);
        }

        public void QueueSelectionPacket(byte[] packet)
        {
            CoopSelectionData selection;
            if (!CoopProtocol.TryParseSelectionPacket(packet, out selection)) return;
            lock (mSelectionLock)
            {
                if (!mRemoteSelectionTracker.TryAccept(selection)) return;
                mPendingRemoteSelection = mRemoteSelectionTracker.Current;
            }
        }

        public void QueueConnectionRequest(byte[] packet)
        {
            CoopConnectionCommandData command;
            long activeSessionId;
            if (SyncManager.Role != "host" ||
                !CoopProtocol.TryParseConnectionRequestPacket(packet, out command) ||
                SyncManager.Instance == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out activeSessionId) ||
                command.SessionId != activeSessionId ||
                !CoopSequencePolicy.IsFromRole(command.RequestId, CoopRole.Guest)) return;
            bool queued;
            lock (mCommandLock) queued = EnqueueBounded(mPendingConnectionRequests, packet);
            if (!queued) LogQueueOverflow("connection request", command.RequestId);
        }

        public void QueueConnectionResult(byte[] packet)
        {
            bool accepted;
            long applyTick;
            CoopConnectionCommandData command;
            long activeSessionId;
            if (SyncManager.Role != "guest" ||
                !CoopProtocol.TryParseConnectionResultPacket(
                    packet, out accepted, out applyTick, out command) ||
                SyncManager.Instance == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out activeSessionId) ||
                command.SessionId != activeSessionId) return;
            bool queued;
            lock (mCommandLock) queued = EnqueueBounded(mPendingConnectionResults, packet);
            if (!queued) ReportResultQueueOverflow(
                "connection result", accepted, CoopMutationKind.Connection, command.RequestId);
        }

        public void QueueRecycleRequest(byte[] packet)
        {
            CoopRecycleCommandData command;
            long activeSessionId;
            if (SyncManager.Role != "host" ||
                !CoopProtocol.TryParseRecycleRequestPacket(packet, out command) ||
                SyncManager.Instance == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out activeSessionId) ||
                command.SessionId != activeSessionId ||
                !CoopSequencePolicy.IsFromRole(command.RequestId, CoopRole.Guest)) return;
            bool queued;
            lock (mCommandLock) queued = EnqueueBounded(mPendingRecycleRequests, packet);
            if (!queued) LogQueueOverflow("recycle request", command.RequestId);
        }

        public void QueueRecycleResult(byte[] packet)
        {
            bool accepted;
            long applyTick;
            CoopRecycleCommandData command;
            long activeSessionId;
            if (SyncManager.Role != "guest" ||
                !CoopProtocol.TryParseRecycleResultPacket(
                    packet, out accepted, out applyTick, out command) ||
                SyncManager.Instance == null ||
                !SyncManager.Instance.TryGetRunningSessionId(out activeSessionId) ||
                command.SessionId != activeSessionId) return;
            bool queued;
            lock (mCommandLock) queued = EnqueueBounded(mPendingRecycleResults, packet);
            if (!queued) ReportResultQueueOverflow(
                "recycle result", accepted, CoopMutationKind.Recycle, command.RequestId);
        }

        private static bool EnqueueBounded(Queue<byte[]> queue, byte[] packet)
        {
            if (queue.Count >= MaxPendingCommandPackets) return false;
            queue.Enqueue((byte[])packet.Clone());
            return true;
        }

        private static void LogQueueOverflow(string queueName, long commandId)
        {
            CoopPlugin.Log.LogWarning(
                "[Coop] " + queueName +
                " queue full; newest packet rejected: " + commandId);
        }

        private static void ReportResultQueueOverflow(
            string queueName,
            bool accepted,
            CoopMutationKind mutationKind,
            long commandId)
        {
            LogQueueOverflow(queueName, commandId);
            if (accepted)
                SyncManager.Instance?.ReportMutationFailure(
                    mutationKind,
                    commandId,
                    CoopMutationFailureCode.ValidationRejected);
        }

        public void ResetForSession()
        {
            DestroyGhost();
            DestroyLocalGhost();
            DestroyRemoteSelectionMarkers();
            lock (mPreviewLock)
            {
                mPendingRemotePreview = null;
                mLocalGuestPreview = null;
                mLastSentPreview = null;
            }
            lock (mSelectionLock)
            {
                mPendingRemoteSelection = null;
                mLastSentSelection = null;
                mRemoteSelectionTracker.Reset();
            }
            lock (mCommandLock)
            {
                mPendingRequests.Clear();
                mPendingResults.Clear();
                mPendingConnectionRequests.Clear();
                mPendingConnectionResults.Clear();
                mPendingRecycleRequests.Clear();
                mPendingRecycleResults.Clear();
                mScheduled.Clear();
                mScheduledConnections.Clear();
                mScheduledRecycles.Clear();
                mScheduledRequestIds.Reset();
                mScheduledConnectionIds.Reset();
                mScheduledRecycleIds.Reset();
            }
            mRemotePreview = null;
            mRemoteGhostKey = null;
            mLastRemoteSequence = 0;
            mLocalSelectionSequence = 0;
            mLastPreviewSendTime = -100f;
            mLocalSequence = 0;
            mNextRequestId = 0;
            mLastGuestRequestId = 0;
            mAwaitingGuestRequestId = 0;
            mPendingLocalPlacementPreviewRequestId = 0;
            mNextConnectionRequestId = 0;
            mAwaitingConnectionRequestId = 0;
            mNextRecycleRequestId = 0;
            mAwaitingRecycleRequestId = 0;
            mGuestRequestSentAt = -100f;
            mConnectionRequestSentAt = -100f;
            mRecycleRequestSentAt = -100f;
        }

        public void RequestSessionReset()
        {
            mSessionResetRequested = true;
        }

        public bool InterceptPlacement(GameStateGame game, bool componentMode)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null || mApplyingAuthoritative)
                return false;
            if (!CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused))
                return true;

            CoopPlacementPreviewData preview = null;
            GuestPlacement.TryGetLocalPreview(out preview);

            if (preview == null || !preview.Active || preview.Kind != (componentMode ? CoopPlacementKind.Component : CoopPlacementKind.Module))
            {
                return true;
            }

            if (!preview.Valid)
            {
                // Vanilla rejects an invalid click locally. Do not send a
                // request that the host can never accept.
                GuestPlacement.CancelSynchronizedPreview();
                return true;
            }

            CoopPlacementCommandData command = ToCommand(preview);
            long sessionId;
            if (!SyncManager.Instance.TryGetRunningSessionId(out sessionId)) return true;
            command.SessionId = sessionId;
            command.RequestId = CoopPlacementRequestIds.For(
                SyncManager.Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                ++mNextRequestId);
            if (SyncManager.Role == "guest")
            {
                if (mAwaitingGuestRequestId != 0 ||
                    mPendingLocalPlacementPreviewRequestId != 0)
                {
                    CoopPlugin.Log.LogInfo("[Coop] guest placement bekliyor - yinelenen tiklama yok sayildi");
                    return true;
                }
                mAwaitingGuestRequestId = command.RequestId;
                mGuestRequestSentAt = Time.realtimeSinceStartup;
                SyncManager.Instance.SendPacket(CoopProtocol.CreatePlacementRequestPacket(command));
                CoopPlugin.Log.LogInfo("[Coop] guest placement request gonderdi: " + command.TypeName);
            }
            else
            {
                if (mPendingLocalPlacementPreviewRequestId != 0) return true;
                mPendingLocalPlacementPreviewRequestId = command.RequestId;
                if (!HandleHostRequest(command, false))
                {
                    mPendingLocalPlacementPreviewRequestId = 0;
                    GuestPlacement.CancelSynchronizedPreview();
                }
            }
            return true;
        }

        public bool ApplyScheduledPlacements(long currentTick)
        {
            if (!SyncManager.FullMode) return true;
            List<CoopPlacementCommandData> due = new List<CoopPlacementCommandData>();
            lock (mCommandLock)
            {
                for (int i = mScheduled.Count - 1; i >= 0; i--)
                {
                    CoopDueTickState state = CoopDueTickPolicy.Classify(
                        mScheduled[i].ApplyTick, currentTick);
                    if (state == CoopDueTickState.Late || state == CoopDueTickState.Invalid)
                    {
                        CoopPlacementCommandData late = mScheduled[i].Command;
                        mScheduled.RemoveAt(i);
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Placement, late.RequestId,
                            CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                    if (state == CoopDueTickState.Due)
                    {
                        due.Add(mScheduled[i].Command);
                        mScheduled.RemoveAt(i);
                    }
                }
            }

            for (int i = due.Count - 1; i >= 0; i--)
            {
                CoopPlacementCommandData command = due[i];
                mApplyingAuthoritative = true;
                try
                {
                    CoopRole localRole = SyncManager.Role == "guest"
                        ? CoopRole.Guest
                        : CoopRole.Host;
                    if (CoopPlacementPreviewLifecycle.IsLocalInitiatedRequest(
                            localRole,
                            command.RequestId,
                            mPendingLocalPlacementPreviewRequestId))
                    {
                        GuestPlacement.CancelSynchronizedPreview();
                        mPendingLocalPlacementPreviewRequestId = 0;
                    }
                    CoopPlugin.Log.LogInfo("[Coop] placement due tick=" + currentTick +
                        " request=" + command.RequestId + " type=" + command.TypeName);
                    if (!GuestPlacement.TryApplyAuthoritativePlacement(command))
                    {
                        CoopPlugin.Log.LogError("[Coop] scheduled placement uygulanamadi: " + command.TypeName);
                        CoopStatusOverlay.Notify("BINA YERLESTIRILEMEDI");
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Placement, command.RequestId,
                            CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                    CoopPlugin.Log.LogInfo("[Coop] scheduled placement uygulandi: request=" +
                        command.RequestId + " type=" + command.TypeName);
                }
                finally { mApplyingAuthoritative = false; }
            }

            List<CoopConnectionCommandData> dueConnections =
                new List<CoopConnectionCommandData>();
            lock (mCommandLock)
            {
                for (int i = mScheduledConnections.Count - 1; i >= 0; i--)
                {
                    CoopDueTickState state = CoopDueTickPolicy.Classify(
                        mScheduledConnections[i].ApplyTick, currentTick);
                    if (state == CoopDueTickState.Late || state == CoopDueTickState.Invalid)
                    {
                        CoopConnectionCommandData late = mScheduledConnections[i].Command;
                        mScheduledConnections.RemoveAt(i);
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Connection, late.RequestId,
                            CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                    if (state == CoopDueTickState.Due)
                    {
                        dueConnections.Add(mScheduledConnections[i].Command);
                        mScheduledConnections.RemoveAt(i);
                    }
                }
            }
            for (int i = dueConnections.Count - 1; i >= 0; i--)
            {
                CoopConnectionCommandData command = dueConnections[i];
                mApplyingAuthoritative = true;
                try
                {
                    CoopObjectResolution firstResolution;
                    CoopObjectResolution secondResolution;
                    Module first = CoopObjectBridge.ResolveModule(
                        command.GetFirstModuleReference(), out firstResolution);
                    Module second = CoopObjectBridge.ResolveModule(
                        command.GetSecondModuleReference(), out secondResolution);
                    if (first == null || second == null)
                    {
                        CoopPlugin.Log.LogWarning("[Coop] scheduled connection gecersiz: " +
                            command.RequestId + " first=" + firstResolution +
                            " second=" + secondResolution);
                        CoopStatusOverlay.Notify("YOL HEDEFI ARTIK GECERLI DEGIL");
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Connection,
                            command.RequestId,
                            firstResolution == CoopObjectResolution.Ambiguous ||
                            secondResolution == CoopObjectResolution.Ambiguous
                                ? CoopMutationFailureCode.TargetAmbiguous
                                : CoopMutationFailureCode.TargetMissing);
                        return false;
                    }
                    try
                    {
                        if (first.isLinkedTo(second) || !Connection.canLink(first, second))
                            throw new InvalidOperationException(
                                "connection eligibility changed before apply");
                        GameStateGame gameState =
                            GameManager.getInstance().getGameState() as GameStateGame;
                        Module.linkModules(
                            first,
                            second,
                            gameState == null || gameState.shouldRenderTops());
                        if (!first.isLinkedTo(second))
                            throw new InvalidOperationException(
                                "connection creation completed without a link");
                        CoopPlugin.Log.LogInfo("[Coop] scheduled connection uygulandi: request=" +
                            command.RequestId);
                    }
                    catch (Exception ex)
                    {
                        CoopPlugin.Log.LogWarning("[Coop] scheduled connection uygulanamadi: request=" +
                            command.RequestId + " error=" + ex.Message);
                        CoopStatusOverlay.Notify("YOL OLUSTURULAMADI");
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Connection, command.RequestId,
                            CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                }
                finally { mApplyingAuthoritative = false; }
            }

            List<CoopRecycleCommandData> dueRecycles =
                new List<CoopRecycleCommandData>();
            lock (mCommandLock)
            {
                for (int i = mScheduledRecycles.Count - 1; i >= 0; i--)
                {
                    CoopDueTickState state = CoopDueTickPolicy.Classify(
                        mScheduledRecycles[i].ApplyTick, currentTick);
                    if (state == CoopDueTickState.Late || state == CoopDueTickState.Invalid)
                    {
                        CoopRecycleCommandData late = mScheduledRecycles[i].Command;
                        mScheduledRecycles.RemoveAt(i);
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Recycle, late.RequestId,
                            CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                    if (state == CoopDueTickState.Due)
                    {
                        dueRecycles.Add(mScheduledRecycles[i].Command);
                        mScheduledRecycles.RemoveAt(i);
                    }
                }
            }
            for (int i = dueRecycles.Count - 1; i >= 0; i--)
            {
                CoopRecycleCommandData command = dueRecycles[i];
                mApplyingAuthoritative = true;
                try
                {
                    CoopObjectResolution resolution;
                    Selectable target = ResolveRecycleTarget(command, out resolution);
                    if (target == null)
                    {
                        CoopPlugin.Log.LogWarning("[Coop] scheduled recycle gecersiz: " +
                            command.RequestId);
                        CoopStatusOverlay.Notify("GERI DONUSUM HEDEFI BULUNAMADI");
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Recycle,
                            command.RequestId,
                            resolution == CoopObjectResolution.Ambiguous
                                ? CoopMutationFailureCode.TargetAmbiguous
                                : CoopMutationFailureCode.TargetMissing);
                        return false;
                    }
                    bool clearLocalSelection =
                        ReferenceEquals(Selection.getSelected(), target) ||
                        ReferenceEquals(Selection.getLinkTarget(), target);
                    target.recycle();
                    target.destroy();
                    if (clearLocalSelection) Selection.clear();
                    CoopPlugin.Log.LogInfo("[Coop] scheduled recycle uygulandi: request=" +
                        command.RequestId);
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning("[Coop] scheduled recycle uygulanamadi: request=" +
                        command.RequestId + " error=" + ex.Message);
                    SyncManager.Instance?.ReportMutationFailure(
                        CoopMutationKind.Recycle, command.RequestId,
                        CoopMutationFailureCode.ApplyFailed);
                    return false;
                }
                finally { mApplyingAuthoritative = false; }
            }
            return true;
        }

        public bool InterceptConnection(GameStateGame game)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null || mApplyingAuthoritative) return false;
            if (!CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused)) return true;
            Module first = Selection.getSelected() as Module;
            Module second = Selection.getLinkTarget() as Module;
            if (first == null || second == null || first == second) return true;
            if (first.getId() <= 0 || second.getId() <= 0)
            {
                SyncManager.Instance.ReportRuntimeInvariantFailure(
                    "connection input referenced a non-authoritative module id");
                CompleteLocalConnectionInput(game, first);
                return true;
            }
            long sessionId;
            if (!SyncManager.Instance.TryGetRunningSessionId(out sessionId)) return true;

            CoopConnectionCommandData command = new CoopConnectionCommandData
            {
                SessionId = sessionId,
                RequestId = CoopPlacementRequestIds.For(
                    SyncManager.Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                    ++mNextConnectionRequestId),
                FirstModuleId = first.getId(),
                SecondModuleId = second.getId(),
                FirstModule = CoopObjectBridge.Capture(first, true),
                SecondModule = CoopObjectBridge.Capture(second, true)
            };
            if (SyncManager.Role == "guest")
            {
                if (mAwaitingConnectionRequestId != 0) return true;
                mAwaitingConnectionRequestId = command.RequestId;
                mConnectionRequestSentAt = Time.realtimeSinceStartup;
                if (!SyncManager.Instance.TrySendPacket(
                        CoopProtocol.CreateConnectionRequestPacket(command)))
                {
                    mAwaitingConnectionRequestId = 0;
                    SyncManager.Instance.ReportRuntimeInvariantFailure(
                        "connection request could not be sent");
                    return true;
                }
                CompleteLocalConnectionInput(game, first);
            }
            else
            {
                if (HandleHostConnectionRequest(command, false))
                    CompleteLocalConnectionInput(game, first);
            }
            return true;
        }

        private static void CompleteLocalConnectionInput(
            GameStateGame game,
            Module source)
        {
            try
            {
                Selection.clear();
                GuiMenuSystem menu = game == null || GameMenuSystemField == null
                    ? null
                    : GameMenuSystemField.GetValue(game) as GuiMenuSystem;
                menu?.setMainMenu();
                source?.playSound(SoundList.getInstance().ConnectionPlace);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] connection UI cleanup failed: " + ex.Message);
            }
        }

        public bool InterceptRecycle()
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null || mApplyingAuthoritative) return false;
            if (!CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused)) return true;

            Selectable target = Selection.getSelected();
            bool enabled = false;
            if (target == null || !Selection.isDeleteable(out enabled) || !enabled) return true;
            long sessionId;
            if (!SyncManager.Instance.TryGetRunningSessionId(out sessionId)) return true;

            CoopRecycleCommandData command = new CoopRecycleCommandData
            {
                SessionId = sessionId,
                RequestId = CoopPlacementRequestIds.For(
                    SyncManager.Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                    ++mNextRecycleRequestId),
                ObjectId = target.getId(),
                TypeName = target.GetType().Name,
                TargetReference = CoopObjectBridge.Capture(target, true)
            };
            if (!CoopRecyclePolicy.IsValidObjectId(command.ObjectId, command.TypeName) || String.IsNullOrEmpty(command.TypeName)) return true;

            if (SyncManager.Role == "guest")
            {
                if (mAwaitingRecycleRequestId != 0) return true;
                mAwaitingRecycleRequestId = command.RequestId;
                mRecycleRequestSentAt = Time.realtimeSinceStartup;
                SyncManager.Instance.SendPacket(CoopProtocol.CreateRecycleRequestPacket(command));
            }
            else
            {
                HandleHostRecycleRequest(command, false);
            }
            return true;
        }

        public void OnGUI()
        {
            if (!SyncManager.FullMode || mRemotePreview == null || !mRemotePreview.Active) return;
            try
            {
                string type = mRemotePreview.TypeName ?? "unknown";
                string side = SyncManager.Role == "host" ? "GUEST" : "HOST";
                GUI.Label(new Rect(10, 10, 380, 24), side + " PREVIEW: " + type);
            }
            catch (Exception) { }
        }

        public void Destroy()
        {
            DestroyGhost();
            DestroyLocalGhost();
            DestroyRemoteSelectionMarkers();
            lock (mPreviewLock)
            {
                mPendingRemotePreview = null;
                mLocalGuestPreview = null;
                mLastSentPreview = null;
            }
        }

        private void CaptureAndSendLocalPreview()
        {
            CoopPlacementPreviewData current = null;
            if (SyncManager.Role == "host")
            {
                GuestPlacement.TryGetLocalPreview(out current);
            }
            else
            {
                lock (mPreviewLock)
                {
                    current = mLocalGuestPreview == null ? null : ClonePreview(mLocalGuestPreview);
                }
            }

            if (SamePreview(current, mLastSentPreview)) return;
            if (current != null && current.Active &&
                Time.unscaledTime - mLastPreviewSendTime < PreviewSendInterval) return;
            CoopPlacementPreviewData packetPreview = current == null
                ? new CoopPlacementPreviewData { Active = false, Kind = CoopPlacementKind.Module, TypeName = string.Empty }
                : ClonePreview(current);
            packetPreview.Source = SyncManager.Role == "host" ? CoopRole.Host : CoopRole.Guest;
            packetPreview.Sequence = ++mLocalSequence;
            mLastSentPreview = ClonePreview(packetPreview);
            mLastPreviewSendTime = Time.unscaledTime;
            SyncManager.Instance.SendPacket(CoopProtocol.CreatePreviewPacket(packetPreview));
        }

        private void ProcessPendingNetworkData()
        {
            CoopPlacementPreviewData preview = null;
            lock (mPreviewLock)
            {
                if (mPendingRemotePreview != null)
                {
                    preview = mPendingRemotePreview;
                    mPendingRemotePreview = null;
                }
            }
            if (preview != null)
            {
                if (SyncManager.Role == "host" && preview.Source == CoopRole.Guest)
                {
                    HandleGuestPreview(preview);
                }
                else if (SyncManager.Role == "guest" && preview.Source == CoopRole.Host)
                {
                    mRemotePreview = ClonePreview(preview);
                }
            }

            while (true)
            {
                byte[] request = null;
                lock (mCommandLock)
                {
                    if (mPendingRequests.Count > 0) request = mPendingRequests.Dequeue();
                }
                if (request == null) break;
                CoopPlacementCommandData command;
                if (CoopProtocol.TryParsePlacementRequestPacket(request, out command) && SyncManager.Role == "host")
                {
                    CoopPlugin.Log.LogInfo("[Coop] host placement request aldi: request=" + command.RequestId + " type=" + command.TypeName);
                    if (command.RequestId <= mLastGuestRequestId)
                    {
                        CoopPlugin.Log.LogWarning("[Coop] duplicate placement request engellendi: request=" + command.RequestId);
                        continue;
                    }
                    mLastGuestRequestId = command.RequestId;
                    HandleHostRequest(command, true);
                }
            }

            while (true)
            {
                byte[] result = null;
                lock (mCommandLock)
                {
                    if (mPendingResults.Count > 0) result = mPendingResults.Dequeue();
                }
                if (result == null) break;
                bool accepted;
                long applyTick;
                CoopPlacementCommandData command;
                if (!CoopProtocol.TryParsePlacementResultPacket(result, out accepted, out applyTick, out command))
                    continue;
                if (SyncManager.Role != "guest") continue;
                CoopPlugin.Log.LogInfo("[Coop] guest placement result aldi: accepted=" + accepted + " request=" + command.RequestId + " applyTick=" + applyTick + " type=" + command.TypeName);
                if (!accepted)
                {
                    CoopStatusOverlay.Notify("BINA HOST TARAFINDAN REDDEDILDI");
                    if (command.RequestId == mAwaitingGuestRequestId)
                    {
                        GuestPlacement.CancelSynchronizedPreview();
                        mAwaitingGuestRequestId = 0;
                    }
                    continue;
                }
                if (!Schedule(applyTick, command))
                {
                    SyncManager.Instance?.ReportMutationFailure(
                        CoopMutationKind.Placement,
                        command.RequestId,
                        CoopMutationFailureCode.ValidationRejected);
                    if (command.RequestId == mAwaitingGuestRequestId)
                    {
                        GuestPlacement.CancelSynchronizedPreview();
                        mAwaitingGuestRequestId = 0;
                    }
                    continue;
                }
                long pendingPreviewRequestId =
                    CoopPlacementPreviewLifecycle.PendingAfterAccepted(
                        CoopRole.Guest,
                        command.RequestId,
                        mAwaitingGuestRequestId);
                if (pendingPreviewRequestId > 0)
                    mPendingLocalPlacementPreviewRequestId = pendingPreviewRequestId;
                if (command.RequestId == mAwaitingGuestRequestId) mAwaitingGuestRequestId = 0;
            }

            while (true)
            {
                byte[] request = null;
                lock (mCommandLock)
                {
                    if (mPendingConnectionRequests.Count > 0) request = mPendingConnectionRequests.Dequeue();
                }
                if (request == null) break;
                CoopConnectionCommandData command;
                if (SyncManager.Role == "host" && CoopProtocol.TryParseConnectionRequestPacket(request, out command))
                {
                    HandleHostConnectionRequest(command, true);
                }
            }

            while (true)
            {
                byte[] result = null;
                lock (mCommandLock)
                {
                    if (mPendingConnectionResults.Count > 0) result = mPendingConnectionResults.Dequeue();
                }
                if (result == null) break;
                bool accepted;
                long applyTick;
                CoopConnectionCommandData command;
                if (SyncManager.Role == "guest" && CoopProtocol.TryParseConnectionResultPacket(result, out accepted, out applyTick, out command))
                {
                    if (!accepted)
                    {
                        CoopStatusOverlay.Notify("YOL HOST TARAFINDAN REDDEDILDI");
                    }
                    else if (!ScheduleConnection(applyTick, command))
                    {
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Connection,
                            command.RequestId,
                            CoopMutationFailureCode.ValidationRejected);
                    }
                    if (command.RequestId == mAwaitingConnectionRequestId) mAwaitingConnectionRequestId = 0;
                }
            }

            while (true)
            {
                byte[] request = null;
                lock (mCommandLock)
                {
                    if (mPendingRecycleRequests.Count > 0) request = mPendingRecycleRequests.Dequeue();
                }
                if (request == null) break;
                CoopRecycleCommandData command;
                if (SyncManager.Role == "host" && CoopProtocol.TryParseRecycleRequestPacket(request, out command))
                {
                    HandleHostRecycleRequest(command, true);
                }
            }

            while (true)
            {
                byte[] result = null;
                lock (mCommandLock)
                {
                    if (mPendingRecycleResults.Count > 0) result = mPendingRecycleResults.Dequeue();
                }
                if (result == null) break;
                bool accepted;
                long applyTick;
                CoopRecycleCommandData command;
                if (SyncManager.Role == "guest" && CoopProtocol.TryParseRecycleResultPacket(result, out accepted, out applyTick, out command))
                {
                    if (!accepted)
                    {
                        CoopStatusOverlay.Notify("GERI DONUSUM HOST TARAFINDAN REDDEDILDI");
                    }
                    else if (!ScheduleRecycle(applyTick, command))
                    {
                        SyncManager.Instance?.ReportMutationFailure(
                            CoopMutationKind.Recycle,
                            command.RequestId,
                            CoopMutationFailureCode.ValidationRejected);
                    }
                    if (command.RequestId == mAwaitingRecycleRequestId) mAwaitingRecycleRequestId = 0;
                }
            }
        }

        private void CheckPendingRequestTimeouts()
        {
            if (SyncManager.Role != "guest") return;
            float now = Time.realtimeSinceStartup;
            const float timeout = 8f;
            if (mAwaitingGuestRequestId != 0 && now - mGuestRequestSentAt >= timeout)
            {
                CoopStatusOverlay.Notify("INSaat istegi zaman asimina ugradi");
                CoopPlugin.Log.LogWarning("[Coop] guest placement istegi zaman asimina ugradi: " + mAwaitingGuestRequestId);
                GuestPlacement.CancelSynchronizedPreview();
                mAwaitingGuestRequestId = 0;
            }
            if (mAwaitingConnectionRequestId != 0 && now - mConnectionRequestSentAt >= timeout)
            {
                CoopStatusOverlay.Notify("YOL ISTEGI ZAMAN ASIMINA UGRADI");
                CoopPlugin.Log.LogWarning("[Coop] guest connection istegi zaman asimina ugradi: " + mAwaitingConnectionRequestId);
                mAwaitingConnectionRequestId = 0;
            }
            if (mAwaitingRecycleRequestId != 0 && now - mRecycleRequestSentAt >= timeout)
            {
                CoopStatusOverlay.Notify("GERI DONUSUM ISTEGI ZAMAN ASIMINA UGRADI");
                CoopPlugin.Log.LogWarning("[Coop] guest recycle istegi zaman asimina ugradi: " + mAwaitingRecycleRequestId);
                mAwaitingRecycleRequestId = 0;
            }
        }

        private bool HandleHostConnectionRequest(CoopConnectionCommandData command, bool fromGuest)
        {
            CoopObjectResolution firstResolution;
            CoopObjectResolution secondResolution;
            Module first = CoopObjectBridge.ResolveModule(
                command.GetFirstModuleReference(), out firstResolution);
            Module second = CoopObjectBridge.ResolveModule(
                command.GetSecondModuleReference(), out secondResolution);
            bool accepted = first != null && second != null && first != second &&
                !HasScheduledConnection(first.getId(), second.getId()) &&
                !first.isLinkedTo(second) && Connection.canLink(first, second);
            if (!accepted)
                CoopPlugin.Log.LogWarning("[Coop] host yol istegini reddetti: request=" + command.RequestId +
                    " first=" + firstResolution + " second=" + secondResolution);
            CoopConnectionCommandData authoritative = command.Clone();
            if (accepted)
            {
                authoritative.FirstModuleId = first.getId();
                authoritative.SecondModuleId = second.getId();
                authoritative.FirstModule = CoopObjectBridge.Capture(first, true);
                authoritative.SecondModule = CoopObjectBridge.Capture(second, true);
            }
            long applyTick = accepted ? NextConstructionMutationTick() : 0;
            if (accepted && !ScheduleConnection(applyTick, authoritative))
            {
                accepted = false;
                applyTick = 0;
            }
            if (fromGuest || SyncManager.Role == "host")
            {
                if (!SyncManager.Instance.TrySendPacket(
                        CoopProtocol.CreateConnectionResultPacket(
                            accepted, applyTick, authoritative)) && accepted)
                {
                    RemoveScheduledConnection(authoritative.RequestId);
                    SyncManager.Instance.ReportRuntimeInvariantFailure(
                        "authoritative connection commit could not be sent");
                    accepted = false;
                }
            }
            return accepted;
        }

        private bool ScheduleConnection(long applyTick, CoopConnectionCommandData command)
        {
            if (applyTick <= 0 || command == null) return false;
            lock (mCommandLock)
            {
                if (mScheduledConnections.Count >= MaxPendingCommandPackets ||
                    !mScheduledConnectionIds.TryAccept(command.RequestId)) return false;
                mScheduledConnections.Add(new ScheduledConnection { ApplyTick = applyTick, Command = command.Clone() });
                return true;
            }
        }

        private bool HasScheduledConnection(int firstModuleId, int secondModuleId)
        {
            lock (mCommandLock)
            {
                for (int i = 0; i < mScheduledConnections.Count; i++)
                {
                    CoopConnectionCommandData scheduled = mScheduledConnections[i].Command;
                    if ((scheduled.FirstModuleId == firstModuleId && scheduled.SecondModuleId == secondModuleId) ||
                        (scheduled.FirstModuleId == secondModuleId && scheduled.SecondModuleId == firstModuleId))
                        return true;
                }
            }
            return false;
        }

        private void HandleHostRecycleRequest(CoopRecycleCommandData command, bool fromGuest)
        {
            CoopObjectResolution resolution;
            Selectable target = ResolveRecycleTarget(command, out resolution);
            bool enabled = false;
            bool accepted = target != null && target.isDeleteable(out enabled) && enabled &&
                !HasScheduledRecycle(target.getId(), target.GetType().Name);
            if (accepted)
            {
                command = command.Clone();
                command.ObjectId = target.getId();
                command.TypeName = target.GetType().Name;
                command.TargetReference = CoopObjectBridge.Capture(target, true);
            }
            long applyTick = accepted ? NextConstructionMutationTick() : 0;
            if (accepted && !ScheduleRecycle(applyTick, command))
            {
                accepted = false;
                applyTick = 0;
            }
            if (fromGuest || SyncManager.Role == "host")
            {
                if (!SyncManager.Instance.TrySendPacket(
                        CoopProtocol.CreateRecycleResultPacket(
                            accepted, applyTick, command)) && accepted)
                {
                    RemoveScheduledRecycle(command.RequestId);
                    SyncManager.Instance.ReportRuntimeInvariantFailure(
                        "authoritative recycle commit could not be sent");
                }
            }
        }

        private bool HasScheduledRecycle(int objectId, string typeName)
        {
            lock (mCommandLock)
            {
                for (int i = 0; i < mScheduledRecycles.Count; i++)
                {
                    CoopRecycleCommandData scheduled = mScheduledRecycles[i].Command;
                    if (scheduled.ObjectId == objectId && StringComparer.Ordinal.Equals(scheduled.TypeName, typeName)) return true;
                }
            }
            return false;
        }

        private bool ScheduleRecycle(long applyTick, CoopRecycleCommandData command)
        {
            if (applyTick <= 0 || command == null) return false;
            lock (mCommandLock)
            {
                if (mScheduledRecycles.Count >= MaxPendingCommandPackets ||
                    !mScheduledRecycleIds.TryAccept(command.RequestId)) return false;
                mScheduledRecycles.Add(new ScheduledRecycle { ApplyTick = applyTick, Command = command.Clone() });
                return true;
            }
        }

        private void CaptureAndSendSelection()
        {
            CoopSelectionData current = CaptureLocalSelection();
            if (SameSelection(current, mLastSentSelection)) return;
            current.Sequence = ++mLocalSelectionSequence;
            mLastSentSelection = CloneSelection(current);
            SyncManager.Instance.SendPacket(CoopProtocol.CreateSelectionPacket(current));
        }

        private void ApplyRemoteSelection()
        {
            CoopSelectionData selection = null;
            lock (mSelectionLock)
            {
                if (mPendingRemoteSelection != null)
                {
                    selection = CloneSelection(mPendingRemoteSelection);
                    mPendingRemoteSelection = null;
                }
            }
            if (selection != null)
            {
                if (!selection.Active)
                {
                    mRemotePrimaryReference = null;
                    mRemoteLinkReference = null;
                    mRemotePrimaryTarget = null;
                    mRemoteLinkTarget = null;
                }
                else
                {
                    mRemotePrimaryReference = selection.GetPrimaryReference()?.Clone();
                    mRemoteLinkReference = selection.GetLinkReference()?.Clone();
                    CoopObjectResolution primaryResolution;
                    CoopObjectResolution linkResolution = CoopObjectResolution.Missing;
                    mRemotePrimaryTarget = CoopObjectBridge.ResolveSelectable(
                        mRemotePrimaryReference, out primaryResolution);
                    mRemoteLinkTarget = mRemoteLinkReference == null
                        ? null
                        : CoopObjectBridge.ResolveSelectable(mRemoteLinkReference, out linkResolution);
                    CoopPlugin.Log.LogDebug("[Coop] uzak secim gorseli: primary=" + primaryResolution +
                        " link=" + (mRemoteLinkReference == null ? "none" : linkResolution.ToString()));
                }
            }
            UpdateRemoteSelectionMarkers();
        }

        private static CoopSelectionData CaptureLocalSelection()
        {
            try
            {
                Selectable selected = Selection.getSelected();
                if (selected == null) return InactiveSelection();

                CoopObjectReference primary = CoopObjectBridge.Capture(
                    selected,
                    !(selected is Character));
                if (!CoopObjectReferenceValidator.IsValid(primary))
                    return InactiveSelection();

                Selectable linkTarget = Selection.getLinkTarget();
                CoopObjectReference link = null;
                if (linkTarget != null && linkTarget.getId() > 0)
                {
                    link = CoopObjectBridge.Capture(linkTarget, true);
                    if (!CoopObjectReferenceValidator.IsValid(link)) link = null;
                }

                return new CoopSelectionData
                {
                    Active = true,
                    ObjectId = primary.ObjectId,
                    TypeName = primary.TypeName,
                    PrimaryReference = primary,
                    HasLinkTarget = link != null,
                    LinkObjectId = link == null ? 0 : link.ObjectId,
                    LinkTypeName = link == null ? string.Empty : link.TypeName,
                    LinkReference = link
                };
            }
            catch (Exception)
            {
                // Planetbase can leave a just-dismissed merchant ship selected
                // for one or more render frames. Treat that stale UI reference
                // as a cleared selection; it must not stop placement previews.
                return InactiveSelection();
            }
        }

        private static CoopSelectionData InactiveSelection()
        {
            return new CoopSelectionData
            {
                Active = false,
                TypeName = string.Empty,
                LinkTypeName = string.Empty
            };
        }

        private static Selectable ResolveSelectable(int objectId, string typeName)
        {
            if (!CoopRecyclePolicy.IsValidObjectId(objectId, typeName) || String.IsNullOrEmpty(typeName)) return null;
            try
            {
                if (StringComparer.Ordinal.Equals(typeName, typeof(Character).Name)) return Character.find(objectId);
                if (StringComparer.Ordinal.Equals(typeName, typeof(ConstructionComponent).Name)) return ConstructionComponent.find(objectId);
                if (StringComparer.Ordinal.Equals(typeName, typeof(Resource).Name)) return Resource.find(objectId);
                if (StringComparer.Ordinal.Equals(typeName, typeof(Ship).Name)) return Ship.find(objectId);
                if (StringComparer.Ordinal.Equals(typeName, typeof(Construction).Name) ||
                    StringComparer.Ordinal.Equals(typeName, typeof(Module).Name) ||
                    StringComparer.Ordinal.Equals(typeName, "Connection"))
                    return Construction.find(objectId);

                // Derived construction/resource types are common in the game.
                Selectable construction = Construction.find(objectId);
                if (construction != null && StringComparer.Ordinal.Equals(construction.GetType().Name, typeName)) return construction;
                Selectable component = ConstructionComponent.find(objectId);
                if (component != null && StringComparer.Ordinal.Equals(component.GetType().Name, typeName)) return component;
                Selectable character = Character.find(objectId);
                if (character != null && StringComparer.Ordinal.Equals(character.GetType().Name, typeName)) return character;
                Selectable resource = Resource.find(objectId);
                if (resource != null && StringComparer.Ordinal.Equals(resource.GetType().Name, typeName)) return resource;
                Selectable ship = Ship.find(objectId);
                if (ship != null && StringComparer.Ordinal.Equals(ship.GetType().Name, typeName)) return ship;
            }
            catch (Exception) { }
            return null;
        }

        private static Selectable ResolveRecycleTarget(
            CoopRecycleCommandData command,
            out CoopObjectResolution resolution)
        {
            resolution = CoopObjectResolution.Invalid;
            if (command == null) return null;
            if (command.ObjectId == 0 &&
                StringComparer.Ordinal.Equals(command.TypeName, "ColonyShip"))
            {
                Selectable colonyShip = ResolveSelectable(command.ObjectId, command.TypeName);
                resolution = colonyShip == null
                    ? CoopObjectResolution.Missing
                    : CoopObjectResolution.ExactId;
                return colonyShip;
            }
            return CoopObjectBridge.ResolveSelectable(
                command.GetTargetReference(),
                out resolution);
        }

        private static CoopSelectionData CloneSelection(CoopSelectionData selection)
        {
            return CoopRemoteSelectionTracker.Clone(selection);
        }

        private static bool SameSelection(CoopSelectionData left, CoopSelectionData right)
        {
            if (left == null || right == null) return left == null && right == null;
            return CoopSelectionIdentity.Same(left, right);
        }

        private void HandleGuestPreview(CoopPlacementPreviewData preview)
        {
            if (!preview.Active)
            {
                mRemotePreview = null;
                return;
            }
            mRemotePreview = ClonePreview(preview);
            CoopPlugin.Log.LogDebug("[Coop] remote placement preview alindi: source=" + preview.Source + " sequence=" + preview.Sequence + " active=" + preview.Active + " type=" + (preview.TypeName ?? string.Empty));
        }

        private bool HandleHostRequest(CoopPlacementCommandData command, bool fromGuest)
        {
            CoopPlacementCommandData normalized;
            string reason;
            bool accepted = GuestPlacement.TryValidateAuthoritativePlacement(command, out normalized, out reason);
            long applyTick = accepted ? NextConstructionMutationTick() : 0;
            if (accepted)
            {
                accepted = Schedule(applyTick, normalized);
                if (accepted)
                    CoopPlugin.Log.LogInfo("[Coop] host placement onaylandi: request=" + normalized.RequestId + " type=" + normalized.TypeName + " tick=" + applyTick);
                else
                    applyTick = 0;
            }
            else
            {
                CoopPlugin.Log.LogWarning("[Coop] host placement reddetti: " + reason);
            }

            if (fromGuest || SyncManager.Role == "host")
            {
                CoopPlacementCommandData resultCommand = accepted ? normalized : command;
                if (!SyncManager.Instance.TrySendPacket(
                        CoopProtocol.CreatePlacementResultPacket(
                            accepted, applyTick, resultCommand)) && accepted)
                {
                    RemoveScheduledPlacement(resultCommand.RequestId);
                    SyncManager.Instance.ReportRuntimeInvariantFailure(
                        "authoritative placement commit could not be sent");
                }
            }
            return accepted;
        }

        private void RemoveScheduledPlacement(long requestId)
        {
            lock (mCommandLock)
            {
                for (int i = mScheduled.Count - 1; i >= 0; i--)
                    if (mScheduled[i].Command.RequestId == requestId)
                        mScheduled.RemoveAt(i);
            }
        }

        private void RemoveScheduledConnection(long requestId)
        {
            lock (mCommandLock)
            {
                for (int i = mScheduledConnections.Count - 1; i >= 0; i--)
                    if (mScheduledConnections[i].Command.RequestId == requestId)
                        mScheduledConnections.RemoveAt(i);
            }
        }

        private void RemoveScheduledRecycle(long requestId)
        {
            lock (mCommandLock)
            {
                for (int i = mScheduledRecycles.Count - 1; i >= 0; i--)
                    if (mScheduledRecycles[i].Command.RequestId == requestId)
                        mScheduledRecycles.RemoveAt(i);
            }
        }

        // Never apply two construction commands in the same simulation tick.
        // Vanilla placement mutates navigation, construction and resource
        // registries in one call; serializing rapid clicks prevents the second
        // call from observing the first call half-way through its update.
        private long NextConstructionMutationTick()
        {
            long tick = SyncManager.Instance.TickIndex +
                CoopCommandLeadPolicy.ApplyLeadTicks(
                    CoopTimeSync.CurrentScaleIndex,
                    CoopNetworkTiming.CurrentBudgetMilliseconds);
            lock (mCommandLock)
            {
                for (int i = 0; i < mScheduled.Count; i++)
                {
                    if (mScheduled[i].ApplyTick >= tick)
                    {
                        tick = mScheduled[i].ApplyTick + 1;
                    }
                }
                for (int i = 0; i < mScheduledConnections.Count; i++)
                {
                    if (mScheduledConnections[i].ApplyTick >= tick) tick = mScheduledConnections[i].ApplyTick + 1;
                }
                for (int i = 0; i < mScheduledRecycles.Count; i++)
                {
                    if (mScheduledRecycles[i].ApplyTick >= tick) tick = mScheduledRecycles[i].ApplyTick + 1;
                }
            }
            return tick;
        }

        private bool Schedule(long applyTick, CoopPlacementCommandData command)
        {
            if (applyTick <= 0 || command == null) return false;
            lock (mCommandLock)
            {
                if (mScheduled.Count >= MaxPendingCommandPackets ||
                    !mScheduledRequestIds.TryAccept(command.RequestId))
                {
                    CoopPlugin.Log.LogWarning("[Coop] duplicate placement schedule engellendi: request=" + command.RequestId + " tick=" + applyTick);
                    return false;
                }
                mScheduled.Add(new ScheduledPlacement { ApplyTick = applyTick, Command = command.Clone() });
            }
            CoopPlugin.Log.LogInfo("[Coop] placement planlandi: request=" + command.RequestId + " tick=" + applyTick + " type=" + command.TypeName);
            return true;
        }

        private void ApplyRemotePreview()
        {
            if (mRemotePreview == null || !mRemotePreview.Active)
            {
                DestroyGhost();
                return;
            }
            EnsureGhost(mRemotePreview);
            ApplyPreviewToGhost(mRemotePreview, mRemoteGhost, mRemoteGhostRenderer);
        }

        private void ApplyLocalGuestPreview()
        {
            // Full mode guest preview is rendered by the game's own
            // GameStateGame.tryPlace* path. A second synthetic cube would
            // diverge from the vanilla footprint and show duplicate geometry.
            DestroyLocalGhost();
        }

        private void ApplyPreviewToGhost(CoopPlacementPreviewData preview, GameObject ghost, MeshRenderer renderer)
        {
            if (preview == null || ghost == null) return;
            ghost.transform.position = new Vector3(preview.PositionX, preview.PositionY, preview.PositionZ);
            ghost.transform.rotation = Quaternion.Euler(0f, preview.RotationY, 0f);
            UpdatePlacementRing(preview);
            if (mRemoteGhostKey == "fallback")
            {
                float size = preview.Kind == CoopPlacementKind.Module
                    ? Module.ValidSizes[Mathf.Clamp(preview.SizeIndex, 0, Module.ValidSizes.Length - 1)]
                    : 2.5f;
                ghost.transform.localScale = preview.Kind == CoopPlacementKind.Module
                    ? new Vector3(size, 0.35f, size)
                    : new Vector3(2f, 0.22f, 2f);
            }
            if (renderer != null && mRemoteGhostKey == "fallback")
            {
                Color color = preview.Valid ? new Color(0.2f, 1f, 0.3f, 0.28f) : new Color(1f, 0.2f, 0.15f, 0.28f);
                renderer.material.color = color;
            }
            else
            {
                Color color = preview.Valid
                    ? new Color(0.5f, 1f, 1f, 0.5f)
                    : new Color(1f, 0f, 0f, 0.5f);
                ghost.setColorRecursive(color);
            }
        }

        private void EnsureGhost(CoopPlacementPreviewData preview)
        {
            string key = preview == null ? string.Empty : preview.Kind + ":" + (preview.TypeName ?? string.Empty) + ":" + preview.SizeIndex;
            if (mRemoteGhost != null && StringComparer.Ordinal.Equals(mRemoteGhostKey, key)) return;
            DestroyGhost();
            GameObject model = null;
            try
            {
                if (preview != null && preview.Kind == CoopPlacementKind.Module)
                {
                    ModuleType type = FindModuleType(preview.TypeName);
                    if (type != null && preview.SizeIndex >= type.getMinSize() && preview.SizeIndex <= type.getMaxSize())
                    {
                        model = type.createModel(preview.SizeIndex);
                    }
                }
                else if (preview != null && preview.Kind == CoopPlacementKind.Component)
                {
                    ComponentType type = FindComponentType(preview.TypeName);
                    if (type != null) model = type.createModel();
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] remote vanilla preview modeli olusturulamadi: " + ex.Message);
            }

            if (model == null)
            {
                model = GameObject.CreatePrimitive(PrimitiveType.Cube);
                model.name = "Coop Remote Placement Preview (fallback)";
                Collider collider = model.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.Destroy(collider);
                mRemoteGhostRenderer = model.GetComponent<MeshRenderer>();
                Shader shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader != null)
                {
                    mRemoteGhostMaterial = new Material(shader);
                    mRemoteGhostMaterial.color = new Color(0.5f, 1f, 1f, 0.5f);
                    mRemoteGhostMaterial.SetFloat("_Mode", 3f);
                    mRemoteGhostMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mRemoteGhostMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mRemoteGhostMaterial.SetInt("_ZWrite", 0);
                    mRemoteGhostMaterial.DisableKeyword("_ALPHATEST_ON");
                    mRemoteGhostMaterial.EnableKeyword("_ALPHABLEND_ON");
                    mRemoteGhostMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mRemoteGhostMaterial.renderQueue = 3000;
                    mRemoteGhostRenderer.material = mRemoteGhostMaterial;
                }
                mRemoteGhostKey = key;
            }
            else
            {
                model.name = "Coop Remote Placement Preview";
                DisableRemoteComponents(model);
                model.setVisibleRecursive(true);
                mRemoteGhostRenderer = null;
                mRemoteGhostMaterial = null;
                mRemoteGhostKey = key;
            }
            mRemoteGhost = model;
            UnityEngine.Object.DontDestroyOnLoad(mRemoteGhost);
            EnsurePlacementRing();
        }

        private void DestroyGhost()
        {
            if (mRemoteGhost != null) UnityEngine.Object.Destroy(mRemoteGhost);
            if (mRemoteGhostMaterial != null) UnityEngine.Object.Destroy(mRemoteGhostMaterial);
            mRemoteGhost = null;
            mRemoteGhostRenderer = null;
            mRemoteGhostMaterial = null;
            mRemoteGhostKey = null;
            if (mRemotePlacementRing != null) UnityEngine.Object.Destroy(mRemotePlacementRing);
            mRemotePlacementRing = null;
            mRemotePlacementRingRenderer = null;
        }

        private static void DisableRemoteComponents(GameObject model)
        {
            if (model == null) return;
            try
            {
                Collider[] colliders = model.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++) colliders[i].enabled = false;
                Rigidbody[] bodies = model.GetComponentsInChildren<Rigidbody>(true);
                for (int i = 0; i < bodies.Length; i++)
                {
                    bodies[i].isKinematic = true;
                    bodies[i].detectCollisions = false;
                }
                Behaviour[] behaviours = model.GetComponentsInChildren<Behaviour>(true);
                for (int i = 0; i < behaviours.Length; i++) behaviours[i].enabled = false;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] uzak onizleme fizik temizligi basarisiz: " + ex.Message);
            }
        }

        private static ModuleType FindModuleType(string runtimeName)
        {
            if (String.IsNullOrEmpty(runtimeName)) return null;
            List<ModuleType> types = TypeList<ModuleType, ModuleTypeList>.get();
            for (int i = 0; i < types.Count; i++)
            {
                ModuleType type = types[i];
                if (type != null && StringComparer.Ordinal.Equals(type.GetType().Name, runtimeName)) return type;
            }
            return TypeList<ModuleType, ModuleTypeList>.find(runtimeName);
        }

        private static ComponentType FindComponentType(string runtimeName)
        {
            if (String.IsNullOrEmpty(runtimeName)) return null;
            List<ComponentType> types = TypeList<ComponentType, ComponentTypeList>.get();
            for (int i = 0; i < types.Count; i++)
            {
                ComponentType type = types[i];
                if (type != null && StringComparer.Ordinal.Equals(type.GetType().Name, runtimeName)) return type;
            }
            return TypeList<ComponentType, ComponentTypeList>.find(runtimeName);
        }

        private void EnsurePlacementRing()
        {
            if (mRemotePlacementRing != null) return;
            mRemotePlacementRing = new GameObject("Coop Remote Placement Radius");
            mRemotePlacementRingRenderer = mRemotePlacementRing.AddComponent<LineRenderer>();
            mRemotePlacementRingRenderer.useWorldSpace = true;
            mRemotePlacementRingRenderer.loop = true;
            mRemotePlacementRingRenderer.positionCount = 32;
            mRemotePlacementRingRenderer.widthMultiplier = 0.045f;
            mRemotePlacementRingRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mRemotePlacementRingRenderer.receiveShadows = false;
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                Material material = new Material(shader);
                material.color = new Color(0.25f, 1f, 1f, 0.9f);
                mRemotePlacementRingRenderer.material = material;
            }
            UnityEngine.Object.DontDestroyOnLoad(mRemotePlacementRing);
        }

        private void UpdateRemoteSelectionMarkers()
        {
            UpdateRemoteSelectionMarker(
                ref mRemotePrimaryMarker,
                ref mRemotePrimaryMarkerRenderer,
                mRemotePrimaryTarget,
                mRemotePrimaryReference,
                "Coop Remote Primary Selection",
                new Color(0.2f, 1f, 1f, 0.95f));
            UpdateRemoteSelectionMarker(
                ref mRemoteLinkMarker,
                ref mRemoteLinkMarkerRenderer,
                mRemoteLinkTarget,
                mRemoteLinkReference,
                "Coop Remote Link Selection",
                new Color(1f, 0.8f, 0.15f, 0.95f));
        }

        private static void UpdateRemoteSelectionMarker(
            ref GameObject marker,
            ref LineRenderer renderer,
            Selectable target,
            CoopObjectReference reference,
            string name,
            Color color)
        {
            if (reference == null)
            {
                if (marker != null) marker.SetActive(false);
                return;
            }
            if (marker == null)
            {
                marker = new GameObject(name);
                renderer = marker.AddComponent<LineRenderer>();
                renderer.useWorldSpace = true;
                renderer.loop = true;
                renderer.positionCount = 36;
                renderer.widthMultiplier = 0.065f;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                Shader shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    Material material = new Material(shader);
                    material.color = color;
                    renderer.material = material;
                }
                UnityEngine.Object.DontDestroyOnLoad(marker);
            }
            if (renderer == null)
            {
                marker.SetActive(false);
                return;
            }
            marker.SetActive(true);
            Vector3 position = new Vector3(
                reference.PositionX, reference.PositionY, reference.PositionZ);
            if (target != null)
            {
                try { position = target.getPosition(); }
                catch (Exception) { }
            }
            float radius = 0.75f;
            if (target != null)
            {
                try { radius = Mathf.Max(radius, target.getRadius() * 1.25f); }
                catch (Exception) { }
            }
            position.y += 0.12f;
            for (int i = 0; i < renderer.positionCount; i++)
            {
                float angle = Mathf.PI * 2f * i / renderer.positionCount;
                renderer.SetPosition(i, position + new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius));
            }
        }

        private void DestroyRemoteSelectionMarkers()
        {
            if (mRemotePrimaryMarker != null) UnityEngine.Object.Destroy(mRemotePrimaryMarker);
            if (mRemoteLinkMarker != null) UnityEngine.Object.Destroy(mRemoteLinkMarker);
            mRemotePrimaryMarker = null;
            mRemoteLinkMarker = null;
            mRemotePrimaryMarkerRenderer = null;
            mRemoteLinkMarkerRenderer = null;
            mRemotePrimaryTarget = null;
            mRemoteLinkTarget = null;
            mRemotePrimaryReference = null;
            mRemoteLinkReference = null;
        }

        private void UpdatePlacementRing(CoopPlacementPreviewData preview)
        {
            EnsurePlacementRing();
            if (mRemotePlacementRingRenderer == null) return;
            float radius = preview.Kind == CoopPlacementKind.Module
                ? Module.ValidSizes[Mathf.Clamp(preview.SizeIndex, 0, Module.ValidSizes.Length - 1)] * 0.68f
                : 1.55f;
            Vector3 center = new Vector3(preview.PositionX, preview.PositionY + 0.08f, preview.PositionZ);
            for (int i = 0; i < mRemotePlacementRingRenderer.positionCount; i++)
            {
                float angle = (Mathf.PI * 2f * i) / mRemotePlacementRingRenderer.positionCount;
                mRemotePlacementRingRenderer.SetPosition(i, center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
            }
            Color color = preview.Valid ? new Color(0.25f, 1f, 1f, 0.9f) : new Color(1f, 0.15f, 0.1f, 0.9f);
            if (mRemotePlacementRingRenderer.material != null) mRemotePlacementRingRenderer.material.color = color;
        }

        private void DestroyLocalGhost()
        {
            if (mLocalGhost != null) UnityEngine.Object.Destroy(mLocalGhost);
            if (mLocalGhostMaterial != null) UnityEngine.Object.Destroy(mLocalGhostMaterial);
            mLocalGhost = null;
            mLocalGhostMaterial = null;
        }

        private static CoopPlacementCommandData ToCommand(CoopPlacementPreviewData preview)
        {
            return new CoopPlacementCommandData
            {
                Kind = preview.Kind,
                ParentId = preview.ParentId,
                ParentReference = preview.ParentReference == null ? null : preview.ParentReference.Clone(),
                TypeName = preview.TypeName,
                SizeIndex = preview.SizeIndex,
                PositionX = preview.PositionX,
                PositionY = preview.PositionY,
                PositionZ = preview.PositionZ,
                RotationY = preview.RotationY,
                Valid = preview.Valid
            };
        }

        private static CoopPlacementPreviewData FromCommand(CoopPlacementCommandData command, long sequence)
        {
            return new CoopPlacementPreviewData
            {
                Active = true,
                Source = SyncManager.Role == "host" ? CoopRole.Host : CoopRole.Guest,
                Kind = command.Kind,
                Sequence = sequence,
                ParentId = command.ParentId,
                ParentReference = command.ParentReference == null ? null : command.ParentReference.Clone(),
                TypeName = command.TypeName,
                SizeIndex = command.SizeIndex,
                PositionX = command.PositionX,
                PositionY = command.PositionY,
                PositionZ = command.PositionZ,
                RotationY = command.RotationY,
                Valid = command.Valid
            };
        }

        private static CoopPlacementPreviewData ClonePreview(CoopPlacementPreviewData preview)
        {
            if (preview == null) return null;
            return new CoopPlacementPreviewData
            {
                Active = preview.Active,
                Source = preview.Source,
                Kind = preview.Kind,
                Sequence = preview.Sequence,
                ParentId = preview.ParentId,
                ParentReference = preview.ParentReference == null ? null : preview.ParentReference.Clone(),
                TypeName = preview.TypeName,
                SizeIndex = preview.SizeIndex,
                PositionX = preview.PositionX,
                PositionY = preview.PositionY,
                PositionZ = preview.PositionZ,
                RotationY = preview.RotationY,
                Valid = preview.Valid
            };
        }

        private static bool SamePreview(CoopPlacementPreviewData left, CoopPlacementPreviewData right)
        {
            if (left == null || right == null) return left == null && right == null;
            return left.Active == right.Active && left.Source == right.Source && left.Kind == right.Kind && left.ParentId == right.ParentId &&
                StringComparer.Ordinal.Equals(left.TypeName, right.TypeName) && left.SizeIndex == right.SizeIndex &&
                Mathf.Abs(left.PositionX - right.PositionX) < 0.01f &&
                Mathf.Abs(left.PositionY - right.PositionY) < 0.01f &&
                Mathf.Abs(left.PositionZ - right.PositionZ) < 0.01f &&
                Mathf.Abs(left.RotationY - right.RotationY) < 0.5f && left.Valid == right.Valid;
        }
    }
}
