using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class GameOverPolicyTests
    {
        [Fact]
        public void DelayedGuestRequestCannotRecoverAfterHostPublishedTerminalState()
        {
            CoopGameOverRequestDecision decision = CoopGameOverPolicy.DecideHostRequest(
                sessionMatches: true,
                sessionRunning: true,
                worldLoaded: true,
                activeGameState: true,
                recoveryPending: false,
                sessionTerminal: true,
                hostTerminalCondition: true);

            Assert.Equal(CoopGameOverRequestDecision.IgnoreTerminal, decision);
        }

        [Fact]
        public void HealthyRunningHostMayRecoverDivergedGuest()
        {
            CoopGameOverRequestDecision decision = CoopGameOverPolicy.DecideHostRequest(
                sessionMatches: true,
                sessionRunning: true,
                worldLoaded: true,
                activeGameState: true,
                recoveryPending: false,
                sessionTerminal: false,
                hostTerminalCondition: false);

            Assert.Equal(CoopGameOverRequestDecision.RecoverGuest, decision);
        }

        [Fact]
        public void HostAtVanillaGameOverConditionPublishesTerminalInsteadOfSnapshot()
        {
            CoopGameOverRequestDecision decision = CoopGameOverPolicy.DecideHostRequest(
                sessionMatches: true,
                sessionRunning: true,
                worldLoaded: true,
                activeGameState: true,
                recoveryPending: false,
                sessionTerminal: false,
                hostTerminalCondition: true);

            Assert.Equal(CoopGameOverRequestDecision.PublishTerminal, decision);
        }

        [Theory]
        [InlineData(false, true, true, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, true, false, true)]
        [InlineData(true, true, true, false)]
        public void RecoveryRequiresMatchingRunningLoadedGameSession(
            bool sessionMatches,
            bool sessionRunning,
            bool worldLoaded,
            bool activeGameState)
        {
            CoopGameOverRequestDecision decision = CoopGameOverPolicy.DecideHostRequest(
                sessionMatches,
                sessionRunning,
                worldLoaded,
                activeGameState,
                recoveryPending: false,
                sessionTerminal: false,
                hostTerminalCondition: false);

            Assert.Equal(CoopGameOverRequestDecision.IgnoreInvalidState, decision);
        }

        [Fact]
        public void RecoveryRequestIsIgnoredWhileAnotherRecoveryIsPending()
        {
            Assert.Equal(
                CoopGameOverRequestDecision.IgnoreBusy,
                CoopGameOverPolicy.DecideHostRequest(
                    sessionMatches: true,
                    sessionRunning: true,
                    worldLoaded: true,
                    activeGameState: true,
                    recoveryPending: true,
                    sessionTerminal: false,
                    hostTerminalCondition: false));
        }

        [Fact]
        public void HostTerminalConditionWinsOverPendingRecovery()
        {
            Assert.Equal(
                CoopGameOverRequestDecision.PublishTerminal,
                CoopGameOverPolicy.DecideHostRequest(
                    sessionMatches: true,
                    sessionRunning: true,
                    worldLoaded: true,
                    activeGameState: true,
                    recoveryPending: true,
                    sessionTerminal: false,
                    hostTerminalCondition: true));
        }

        [Theory]
        [InlineData(true, true, false, false, false, true)]
        [InlineData(true, true, true, false, false, false)]
        [InlineData(true, true, false, true, false, false)]
        [InlineData(true, true, false, false, true, false)]
        [InlineData(false, true, false, false, false, false)]
        public void GuestRequestIsSuppressedByTerminalOrInvalidSessionState(
            bool sessionRunning,
            bool worldLoaded,
            bool sessionTerminal,
            bool authoritativeStatePending,
            bool alreadySent,
            bool expected)
        {
            Assert.Equal(expected, CoopGameOverPolicy.ShouldSendGuestRequest(
                sessionRunning,
                worldLoaded,
                sessionTerminal,
                authoritativeStatePending,
                alreadySent));
        }

        [Fact]
        public void SnapshotInstallSuppressesOnlyNestedGuestTitleTransition()
        {
            Assert.False(CoopGameStateTransitionPolicy.ShouldAllowTitleTransition(
                fullMode: true,
                isGuest: true,
                snapshotInstallInProgress: true,
                authorizedTitleTransition: false));
            Assert.True(CoopGameStateTransitionPolicy.ShouldAllowTitleTransition(
                fullMode: true,
                isGuest: true,
                snapshotInstallInProgress: false,
                authorizedTitleTransition: false));
            Assert.True(CoopGameStateTransitionPolicy.ShouldAllowTitleTransition(
                fullMode: false,
                isGuest: true,
                snapshotInstallInProgress: true,
                authorizedTitleTransition: false));
            Assert.True(CoopGameStateTransitionPolicy.ShouldAllowTitleTransition(
                fullMode: true,
                isGuest: true,
                snapshotInstallInProgress: true,
                authorizedTitleTransition: true));
        }

        [Fact]
        public void TerminalMarkerAppliesOnlyToItsOriginalSession()
        {
            Assert.True(CoopGameOverPolicy.IsTerminalSession(
                terminalSessionId: 77,
                activeSessionId: 77));
            Assert.False(CoopGameOverPolicy.IsTerminalSession(
                terminalSessionId: 77,
                activeSessionId: 78));
            Assert.False(CoopGameOverPolicy.IsTerminalSession(
                terminalSessionId: 0,
                activeSessionId: 0));
        }

        [Fact]
        public void GuestRequestTickMustBeCloseToHostTick()
        {
            Assert.True(CoopGameOverPolicy.IsPlausibleRequestTick(1000, 1000));
            Assert.True(CoopGameOverPolicy.IsPlausibleRequestTick(488, 1000));
            Assert.False(CoopGameOverPolicy.IsPlausibleRequestTick(487, 1000));
            Assert.False(CoopGameOverPolicy.IsPlausibleRequestTick(1513, 1000));
        }

        [Fact]
        public void RecoveryWorldRequiresNewSessionAndActiveNonTerminalGame()
        {
            Assert.True(CoopRecoveryPolicy.CanAcceptIncomingWorld(
                firstSnapshot: true,
                sessionRunning: false,
                worldLoaded: false,
                startApplied: false,
                activeGameState: false,
                sessionTerminal: false,
                activeSessionId: 0,
                incomingSessionId: 77));
            Assert.True(CoopRecoveryPolicy.CanAcceptIncomingWorld(
                firstSnapshot: false,
                sessionRunning: true,
                worldLoaded: true,
                startApplied: true,
                activeGameState: true,
                sessionTerminal: false,
                activeSessionId: 77,
                incomingSessionId: 78));
            Assert.False(CoopRecoveryPolicy.CanAcceptIncomingWorld(
                false, true, true, true, true, true, 77, 78));
            Assert.False(CoopRecoveryPolicy.CanAcceptIncomingWorld(
                false, true, true, true, false, false, 77, 78));
            Assert.False(CoopRecoveryPolicy.CanAcceptIncomingWorld(
                false, true, true, true, true, false, 77, 77));
            Assert.False(CoopRecoveryPolicy.CanAcceptIncomingWorld(
                true, false, false, false, false, false, 0, 0));
        }

        [Fact]
        public void LeavingGameStateInvalidatesWorldEligibility()
        {
            Assert.True(CoopGameStateTransitionPolicy.ShouldInvalidateWorld(
                previousWasGameState: true,
                currentIsGameState: false));
            Assert.False(CoopGameStateTransitionPolicy.ShouldInvalidateWorld(
                previousWasGameState: false,
                currentIsGameState: false));
        }
    }
}
