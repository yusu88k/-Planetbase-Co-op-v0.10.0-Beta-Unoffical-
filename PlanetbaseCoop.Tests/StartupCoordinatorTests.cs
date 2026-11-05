using System;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class StartupCoordinatorTests
    {
        [Fact]
        public void HostCannotStartBeforeGuestReady()
        {
            var coordinator = new CoopStartupCoordinator(CoopRole.Host, 42, "world-hash", 7);

            coordinator.MarkLocalWorldReady();

            Assert.False(coordinator.CanStart);
            Assert.False(coordinator.AcceptPeerReady(41, "world-hash", 7));
            Assert.True(coordinator.AcceptPeerReady(42, "world-hash", 7));
            Assert.True(coordinator.AuthorizeStart());
            Assert.True(coordinator.CanStart);
        }

        [Fact]
        public void HostKeepsEarlyGuestReadyUntilLocalSnapshotReloadCompletes()
        {
            var coordinator = new CoopStartupCoordinator(CoopRole.Host, 42, "world-hash", 7);

            Assert.True(coordinator.AcceptPeerReady(42, "world-hash", 7));
            Assert.True(coordinator.PeerReady);
            Assert.False(coordinator.CanStart);

            coordinator.MarkLocalWorldReady();

            Assert.True(coordinator.CanStart);
            Assert.True(coordinator.AuthorizeStart());
        }

        [Fact]
        public void EarlyGuestReadyDispatchArmsHostAfterLocalRecoveryReloadCompletes()
        {
            var coordinator = new CoopStartupCoordinator(CoopRole.Host, 42, "world-hash", 7);

            Assert.True(coordinator.AcceptPeerReady(42, "world-hash", 7));
            coordinator.MarkLocalWorldReady();
            Assert.True(coordinator.AuthorizeStart());

            byte[] startPacket = { 1, 2, 3 };
            bool packetSent = false;
            bool localStartPending;

            Assert.True(CoopHostStartDispatch.TrySend(
                startPacket,
                packet =>
                {
                    packetSent = ReferenceEquals(startPacket, packet);
                    return true;
                },
                out localStartPending));
            Assert.True(packetSent);
            Assert.True(localStartPending);
        }

        [Fact]
        public void FailedStartTransmissionDoesNotReleaseHostRecoveryGate()
        {
            bool localStartPending;

            Assert.False(CoopHostStartDispatch.TrySend(
                new byte[] { 1 },
                packet => false,
                out localStartPending));
            Assert.False(localStartPending);
        }

        [Fact]
        public void GuestRejectsMismatchedReadyAndStartPackets()
        {
            var coordinator = new CoopStartupCoordinator(CoopRole.Guest, 42, "world-hash", 7);
            coordinator.MarkLocalWorldReady();

            Assert.False(coordinator.AcceptPeerReady(42, "other-hash", 7));
            Assert.False(coordinator.AcceptStart(41, 7));
            Assert.False(coordinator.CanStart);
            Assert.True(coordinator.AcceptStart(42, 7));
            Assert.True(coordinator.CanStart);
        }

        [Fact]
        public void TimeoutFailsStartupAndPreventsStart()
        {
            var coordinator = new CoopStartupCoordinator(CoopRole.Host, 42, "world-hash", 7);
            coordinator.MarkLocalWorldReady();

            Assert.True(coordinator.HasTimedOut(DateTime.UtcNow.AddSeconds(121), TimeSpan.FromSeconds(120)));
            coordinator.Fail();

            Assert.Equal(CoopStartupPhase.Failed, coordinator.Phase);
            Assert.False(coordinator.CanStart);
        }

        [Fact]
        public void RecoveryWaitingScreenBlocksVanillaStartUntilCoordinatorAuthorizesIt()
        {
            Assert.True(CoopFixedUpdatePolicy.ShouldSkipSimulation(
                fullMode: true,
                worldTransferPending: false,
                shouldFreeze: true));
            Assert.False(CoopFixedUpdatePolicy.ShouldSkipSimulation(
                fullMode: true,
                worldTransferPending: true,
                shouldFreeze: true));
            Assert.False(CoopFixedUpdatePolicy.ShouldRunGameStateUpdate(
                fullMode: true,
                worldTransferPending: true,
                allowSimulation: true));
            Assert.False(CoopFixedUpdatePolicy.ShouldRunGameStateUpdate(
                fullMode: true,
                coordinatorAvailable: false,
                worldTransferPending: false,
                allowSimulation: true));
            Assert.True(CoopFixedUpdatePolicy.ShouldRunGameStateUpdate(
                fullMode: true,
                worldTransferPending: false,
                allowSimulation: true));

            Assert.False(CoopFixedUpdatePolicy.ShouldRunVanilla(
                waitingForStart: true,
                shouldBlockVanillaStart: true,
                worldTransferPending: true));

            Assert.True(CoopFixedUpdatePolicy.ShouldRunVanilla(
                waitingForStart: false,
                shouldBlockVanillaStart: false,
                worldTransferPending: true));
        }

        [Fact]
        public void HostInitialLoaderIsProtectedFromPerTickRngReseeding()
        {
            Assert.True(CoopWorldTransferPolicy.IsPending(
                loadQueued: false,
                recoveryPending: false,
                hostInitialWorldLoading: true));

            Assert.False(CoopWorldTransferPolicy.IsPending(
                loadQueued: false,
                recoveryPending: false,
                hostInitialWorldLoading: false));
            Assert.True(CoopWorldTransferPolicy.IsPending(
                loadQueued: false,
                recoveryPending: false,
                hostInitialWorldLoading: false,
                snapshotVerificationPending: true));
        }

        [Fact]
        public void FrozenFixedUpdateDoesNotAdvanceTheNetworkTick()
        {
            Assert.Equal(42, CoopTickProgressPolicy.NextTick(42, simulationAdvanced: false));
            Assert.Equal(43, CoopTickProgressPolicy.NextTick(42, simulationAdvanced: true));
        }

        [Theory]
        [InlineData(0, 4, 12)]
        [InlineData(1, 8, 24)]
        [InlineData(2, 16, 48)]
        public void TickLeadBudgetScalesWithSpeedWithoutConsumingCommandMargin(
            int scaleIndex,
            int expectedLeadTicks,
            int expectedCommandLeadTicks)
        {
            Assert.Equal(expectedLeadTicks,
                CoopTickLeadPolicy.MaxLeadTicks(scaleIndex));
            Assert.Equal(expectedCommandLeadTicks,
                CoopCommandLeadPolicy.ApplyLeadTicks(scaleIndex));
            Assert.True(expectedCommandLeadTicks - (expectedLeadTicks + 1) >= 5,
                "Tick gate must retain a usable authoritative-command delivery margin.");
        }

        [Fact]
        public void GuestRecoveryPreparesStaleWorldObjectsBeforeReplacingTheWorld()
        {
            Assert.True(CoopRecoveryPolicy.ShouldPrepareWorldTransfer(
                isGuest: true,
                worldTransferPending: true));
            Assert.False(CoopRecoveryPolicy.ShouldPrepareWorldTransfer(
                isGuest: false,
                worldTransferPending: true));
        }

        [Fact]
        public void FullModeWorldClearAlwaysRemovesStaleParticles()
        {
            Assert.True(CoopRecoveryPolicy.ShouldCleanupParticlesBeforeWorldClear(
                fullMode: true));
            Assert.False(CoopRecoveryPolicy.ShouldCleanupParticlesBeforeWorldClear(
                fullMode: false));
        }

        [Fact]
        public void AuthorizedRecoveryRestoresCapturedTimeOnBothPeers()
        {
            Assert.True(CoopAuthorizedStartTimePolicy.ShouldRestore(
                CoopRole.Host,
                recoveryPending: true));
            Assert.True(CoopAuthorizedStartTimePolicy.ShouldRestore(
                CoopRole.Guest,
                recoveryPending: false));
            Assert.False(CoopAuthorizedStartTimePolicy.ShouldRestore(
                CoopRole.Host,
                recoveryPending: false));
        }

        [Fact]
        public void RecoveryStillChecksTimeoutAfterInitialStartWasApplied()
        {
            Assert.True(CoopRecoveryPolicy.ShouldCheckStartupTimeout(
                startApplied: true,
                recoveryPending: true,
                startupFailed: false));
            Assert.False(CoopRecoveryPolicy.ShouldCheckStartupTimeout(
                startApplied: true,
                recoveryPending: false,
                startupFailed: false));
            Assert.False(CoopRecoveryPolicy.ShouldCheckStartupTimeout(
                startApplied: false,
                recoveryPending: true,
                startupFailed: true));
        }

        [Fact]
        public void HostRecoveryStopsEntireGameManagerFixedUpdate()
        {
            Assert.False(CoopRecoveryPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: true,
                recoveryPending: true));
            Assert.True(CoopRecoveryPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: false,
                recoveryPending: true));
            Assert.True(CoopRecoveryPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: true,
                recoveryPending: false));
        }

        [Fact]
        public void GuestSnapshotVerificationStopsFixedUpdateAfterLoaderCompletes()
        {
            Assert.False(CoopRecoveryPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: false,
                recoveryPending: false,
                snapshotVerificationPending: true));
            Assert.True(CoopRecoveryPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: false,
                recoveryPending: false,
                snapshotVerificationPending: false));
        }

        [Fact]
        public void ExhaustedColonyShipSpawnCompletesOnlyInVanillaTimerWindow()
        {
            Assert.False(CoopColonyShipPolicy.ShouldCompleteExhaustedSpawnUpdate(
                state: 3,
                hasSpawnList: false,
                spawnIndex: 0,
                spawnCount: 0,
                spawnTimer: 1.21f));
            Assert.True(CoopColonyShipPolicy.ShouldCompleteExhaustedSpawnUpdate(
                state: 3,
                hasSpawnList: true,
                spawnIndex: 2,
                spawnCount: 2,
                spawnTimer: 0.49f));
            Assert.False(CoopColonyShipPolicy.ShouldCompleteExhaustedSpawnUpdate(
                state: 3,
                hasSpawnList: true,
                spawnIndex: 1,
                spawnCount: 2,
                spawnTimer: 0.1f));
            Assert.False(CoopColonyShipPolicy.ShouldCompleteExhaustedSpawnUpdate(
                state: 2,
                hasSpawnList: false,
                spawnIndex: 0,
                spawnCount: 0,
                spawnTimer: 0.1f));
        }

        [Fact]
        public void ColonyShipSpawnProgressIsBoundedByRebuiltList()
        {
            Assert.Equal(0, CoopColonyShipPolicy.ClampSpawnIndex(-1, 7));
            Assert.Equal(4, CoopColonyShipPolicy.ClampSpawnIndex(4, 7));
            Assert.Equal(7, CoopColonyShipPolicy.ClampSpawnIndex(9, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => CoopColonyShipPolicy.ClampSpawnIndex(0, -1));
        }

        [Fact]
        public void ColonyShipIdZeroIsValidForRecycleButOtherObjectsNeedPositiveIds()
        {
            Assert.True(CoopRecyclePolicy.IsValidObjectId(0, "ColonyShip"));
            Assert.False(CoopRecyclePolicy.IsValidObjectId(-1, "ColonyShip"));
            Assert.False(CoopRecyclePolicy.IsValidObjectId(0, "Module"));
            Assert.True(CoopRecyclePolicy.IsValidObjectId(17, "Module"));
        }

        [Fact]
        public void FullModeNeverFallsBackToAnUnsynchronizedVanillaMutation()
        {
            Assert.True(CoopAuthoritativeMutationPolicy.CanRunDirectly(
                fullMode: false,
                coordinatorAvailable: false,
                applyingAuthoritative: false));
            Assert.False(CoopAuthoritativeMutationPolicy.CanRunDirectly(
                fullMode: true,
                coordinatorAvailable: false,
                applyingAuthoritative: false));
            Assert.False(CoopAuthoritativeMutationPolicy.CanRunDirectly(
                fullMode: true,
                coordinatorAvailable: true,
                applyingAuthoritative: false));
            Assert.True(CoopAuthoritativeMutationPolicy.CanRunDirectly(
                fullMode: true,
                coordinatorAvailable: true,
                applyingAuthoritative: true));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        public void MenuSessionCreatesMissingPlacementCoordinator(
            bool fullMode,
            bool coordinatorAvailable,
            bool expected)
        {
            Assert.Equal(expected,
                CoopRuntimeSubsystemPolicy.RequiresPlacementCoordinator(
                    fullMode, coordinatorAvailable));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void FullModeEnablesBackgroundProcessingEvenWhenStartedFromMenu(
            bool fullMode,
            bool alreadyEnabled,
            bool expected)
        {
            Assert.Equal(expected,
                CoopRuntimeSubsystemPolicy.RequiresBackgroundProcessing(
                    fullMode, alreadyEnabled));
        }

        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, true, true)]
        [InlineData(true, true, false, true)]
        public void FocusRecoveryUsesEitherUnitySignal(
            bool applicationFocused,
            bool callbackReceived,
            bool callbackFocused,
            bool expected)
        {
            Assert.Equal(expected,
                CoopApplicationFocusPolicy.Resolve(
                    applicationFocused, callbackReceived, callbackFocused));
        }

        [Theory]
        [InlineData(120, 120, 121, true)]
        [InlineData(120, -1, 121, false)]
        [InlineData(120, 120, 120, false)]
        [InlineData(120, 121, 122, false)]
        [InlineData(-1, -1, 0, false)]
        public void AuthoritativePlacementConsumesExactlyOneExpectedId(
            int previousNextId,
            int entityId,
            int nextId,
            bool expected)
        {
            Assert.Equal(expected, CoopAuthoritativeIdPolicy.IsExpectedAdvance(
                previousNextId, entityId, nextId));
        }

        [Theory]
        [InlineData(CoopRole.Guest, 3, 3, true)]
        [InlineData(CoopRole.Guest, 2, 0, false)]
        [InlineData(CoopRole.Guest, 5, 3, false)]
        [InlineData(CoopRole.Host, 2, 2, true)]
        public void LocalPlacementIdentityMatchesOnlyItsInitiatorsPreview(
            CoopRole localRole,
            long commandRequestId,
            long awaitingLocalRequestId,
            bool expected)
        {
            Assert.Equal(expected,
                CoopPlacementPreviewLifecycle.IsLocalInitiatedRequest(
                    localRole, commandRequestId, awaitingLocalRequestId));
        }

        [Fact]
        public void AcceptedLocalPlacementTransfersFromAwaitingToFrozenPreview()
        {
            Assert.Equal(3,
                CoopPlacementPreviewLifecycle.PendingAfterAccepted(
                    CoopRole.Guest, commandRequestId: 3, awaitingLocalRequestId: 3));
            Assert.Equal(0,
                CoopPlacementPreviewLifecycle.PendingAfterAccepted(
                    CoopRole.Guest, commandRequestId: 2, awaitingLocalRequestId: 3));
            Assert.True(CoopPlacementPreviewLifecycle.ShouldFreeze(
                awaitingLocalRequestId: 3, pendingLocalRequestId: 0));
            Assert.True(CoopPlacementPreviewLifecycle.ShouldFreeze(
                awaitingLocalRequestId: 0, pendingLocalRequestId: 3));
            Assert.False(CoopPlacementPreviewLifecycle.ShouldFreeze(
                awaitingLocalRequestId: 0, pendingLocalRequestId: 0));
        }

        [Fact]
        public void DisasterSnapshotReaderRestoresAuthoritativeTimers()
        {
            var document = new System.Xml.XmlDocument();
            document.LoadXml(
                "<save-game><blizzard><blizzard-in-progress value=\"False\" />" +
                "<time-to-next-blizzard value=\"2939.812\" /><time value=\"0\" />" +
                "<blizzard-time value=\"0\" /></blizzard></save-game>");

            Assert.True(CoopDisasterSnapshot.TryRead(document.DocumentElement, "blizzard", out CoopDisasterState state));
            Assert.False(state.InProgress);
            Assert.Equal(2939.812f, state.TimeToNext, 3);
            Assert.Equal(0f, state.Time);
            Assert.Equal(0f, state.Duration);
        }

        [Fact]
        public void HostPublishesFreshWorldWhileColonyShipIsStillLanding()
        {
            Assert.True(CoopHostBootstrapPolicy.CanPublishInitialSnapshot(
                isHost: true,
                worldLoaded: true,
                isFreshWorld: true,
                colonyShipPresent: true,
                colonyShipDone: false,
                characterCount: 0,
                cinematicActive: true,
                snapshotPublished: false));
        }

        [Fact]
        public void HostStillAcceptsCompletedLandingAsFallbackSnapshot()
        {
            Assert.True(CoopHostBootstrapPolicy.CanPublishInitialSnapshot(
                isHost: true,
                worldLoaded: true,
                isFreshWorld: true,
                colonyShipPresent: true,
                colonyShipDone: true,
                characterCount: 7,
                cinematicActive: false,
                snapshotPublished: false));
        }

        [Fact]
        public void HostBootstrapRunsLocallyUntilStableThenFreezesForPeer()
        {
            Assert.True(CoopHostBootstrapPolicy.ShouldAdvanceLocalSimulation(
                isHost: true,
                bootstrapStarted: true,
                worldStable: false,
                sharedSessionStarted: false));
            Assert.False(CoopHostBootstrapPolicy.ShouldAdvanceLocalSimulation(
                isHost: true,
                bootstrapStarted: true,
                worldStable: true,
                sharedSessionStarted: false));
            Assert.True(CoopHostBootstrapPolicy.ShouldFreezeStableWorld(
                isHost: true,
                bootstrapStarted: true,
                worldStable: true,
                sharedSessionStarted: false));
        }

        [Fact]
        public void VerifiedGuestWorldDoesNotRearmSnapshotGateWhenStartBegins()
        {
            bool pending = CoopSnapshotVerificationPolicy.ShouldRemainPendingOnGameLoaded(
                CoopRole.Guest,
                localWorldReady: true,
                phase: CoopStartupPhase.ReadyToStart);

            Assert.False(pending);
            Assert.True(CoopRecoveryPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: false,
                recoveryPending: false,
                snapshotVerificationPending: pending));
        }

        [Fact]
        public void UnverifiedGuestWorldKeepsSnapshotGateClosed()
        {
            bool pending = CoopSnapshotVerificationPolicy.ShouldRemainPendingOnGameLoaded(
                CoopRole.Guest,
                localWorldReady: false,
                phase: CoopStartupPhase.WaitingForLocalWorld);

            Assert.True(pending);
            Assert.False(CoopSnapshotVerificationPolicy.ShouldRemainPendingOnGameLoaded(
                CoopRole.Host,
                localWorldReady: false,
                phase: CoopStartupPhase.WaitingForLocalWorld));
        }

        [Fact]
        public void CompletedGuestSessionCannotAuthorizeAReenteredUnverifiedWorld()
        {
            Assert.True(CoopSnapshotVerificationPolicy.ShouldRemainPendingOnGameLoaded(
                CoopRole.Guest,
                localWorldReady: true,
                phase: CoopStartupPhase.Running));
            Assert.False(CoopStartAuthorizationPolicy.CanApplyAuthorizedStart(
                CoopRole.Guest,
                CoopStartupPhase.Running));
            Assert.True(CoopStartAuthorizationPolicy.CanApplyAuthorizedStart(
                CoopRole.Guest,
                CoopStartupPhase.ReadyToStart));
            Assert.True(CoopStartAuthorizationPolicy.CanApplyAuthorizedStart(
                CoopRole.Host,
                CoopStartupPhase.Running));
        }

        [Fact]
        public void UnfocusedCameraStillAdvancesAnActiveCinematic()
        {
            Assert.True(CoopCameraUpdatePolicy.ShouldRun(
                fullMode: true,
                applicationFocused: false,
                cinematicActive: true));
            Assert.False(CoopCameraUpdatePolicy.ShouldRun(
                fullMode: true,
                applicationFocused: false,
                cinematicActive: false));
            Assert.True(CoopCameraUpdatePolicy.ShouldRun(
                fullMode: true,
                applicationFocused: true,
                cinematicActive: false));
        }

        [Fact]
        public void FullModeIntroStopsBeforeDereferencingAStaleColonyShip()
        {
            Assert.False(CoopIntroCinematicPolicy.ShouldRun(
                fullMode: true,
                shipFieldAvailable: true,
                shipPresent: false,
                shipDestroyed: false));
            Assert.False(CoopIntroCinematicPolicy.ShouldRun(
                fullMode: true,
                shipFieldAvailable: true,
                shipPresent: true,
                shipDestroyed: true));
            Assert.True(CoopIntroCinematicPolicy.ShouldRun(
                fullMode: true,
                shipFieldAvailable: true,
                shipPresent: true,
                shipDestroyed: false));
        }

        [Fact]
        public void IntroSafetyPreservesVanillaBehaviorWhenReflectionFieldIsUnavailable()
        {
            Assert.True(CoopIntroCinematicPolicy.ShouldRun(
                fullMode: true,
                shipFieldAvailable: false,
                shipPresent: false,
                shipDestroyed: false));
            Assert.True(CoopIntroCinematicPolicy.ShouldRun(
                fullMode: false,
                shipFieldAvailable: true,
                shipPresent: false,
                shipDestroyed: true));
        }

        [Fact]
        public void StableHostWaitsForPeerBeforeTransmittingSnapshot()
        {
            Assert.False(CoopHostBootstrapPolicy.CanTransmitInitialSnapshot(
                worldStable: true,
                guestConnected: false,
                handshakeComplete: false,
                snapshotPublished: false));
            Assert.True(CoopHostBootstrapPolicy.CanTransmitInitialSnapshot(
                worldStable: true,
                guestConnected: true,
                handshakeComplete: true,
                snapshotPublished: false));
            Assert.False(CoopHostBootstrapPolicy.CanTransmitInitialSnapshot(
                worldStable: true,
                guestConnected: true,
                handshakeComplete: true,
                snapshotPublished: true));
        }

        [Fact]
        public void StableBootstrapCanPublishAtNetworkTickZero()
        {
            Assert.True(CoopHostBootstrapPolicy.CanPublishInitialSnapshot(
                isHost: true,
                worldLoaded: true,
                isFreshWorld: false,
                colonyShipPresent: false,
                colonyShipDone: false,
                characterCount: 0,
                cinematicActive: false,
                snapshotPublished: false));
        }

        [Fact]
        public void GuestNeverPublishesOrCreatesAnInitialWorld()
        {
            Assert.False(CoopHostBootstrapPolicy.CanPublishInitialSnapshot(
                isHost: false,
                worldLoaded: true,
                isFreshWorld: false,
                colonyShipPresent: false,
                colonyShipDone: false,
                characterCount: 0,
                cinematicActive: false,
                snapshotPublished: false));
            Assert.True(CoopGuestBootstrapPolicy.ShouldWaitForHostWorld(
                CoopRole.Guest,
                hostSnapshotReceived: false));
            Assert.False(CoopGuestBootstrapPolicy.ShouldWaitForHostWorld(
                CoopRole.Guest,
                hostSnapshotReceived: true));
        }

        [Fact]
        public void RandomHostSeedIsGeneratedOnceAndThenRemainsStable()
        {
            int calls = 0;
            CoopSessionSeedSelection selection = CoopSessionSeedSelection.Create(
                CoopRole.Host,
                newGameRequested: true,
                fixedSeed: null,
                randomSeedFactory: () =>
                {
                    calls++;
                    return 7654321;
                });

            Assert.True(selection.HasSeed);
            Assert.Equal(7654321, selection.Seed);
            Assert.Equal(7654321, selection.Seed);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void ExplicitHostSeedIsPreservedWithoutCallingRandomSource()
        {
            int calls = 0;
            CoopSessionSeedSelection selection = CoopSessionSeedSelection.Create(
                CoopRole.Host,
                newGameRequested: true,
                fixedSeed: 24683,
                randomSeedFactory: () =>
                {
                    calls++;
                    return 1;
                });

            Assert.True(selection.HasSeed);
            Assert.Equal(24683, selection.Seed);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void GuestDoesNotResolveANewGameSeed()
        {
            int calls = 0;
            CoopSessionSeedSelection selection = CoopSessionSeedSelection.Create(
                CoopRole.Guest,
                newGameRequested: true,
                fixedSeed: null,
                randomSeedFactory: () =>
                {
                    calls++;
                    return 99;
                });

            Assert.False(selection.HasSeed);
            Assert.Equal(0, calls);
        }

        [Theory]
        [InlineData("random", true, null)]
        [InlineData("RANDOM", true, null)]
        [InlineData("24683", true, 24683)]
        [InlineData("-1", false, null)]
        [InlineData("invalid", false, null)]
        public void NewGameSeedArgumentIsValidated(string value, bool expected, int? expectedSeed)
        {
            bool parsed = CoopSeedArgument.TryParse(value, out int? fixedSeed);

            Assert.Equal(expected, parsed);
            Assert.Equal(expectedSeed, fixedSeed);
        }
    }
}
