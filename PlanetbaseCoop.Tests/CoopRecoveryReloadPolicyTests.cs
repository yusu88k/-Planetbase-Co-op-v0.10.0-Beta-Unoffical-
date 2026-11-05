using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopRecoveryReloadPolicyTests
    {
        [Theory]
        [InlineData(CoopRole.Host)]
        [InlineData(CoopRole.Guest)]
        public void RecoveryRequiresLocalReloadAndVerificationForBothPeers(CoopRole role)
        {
            Assert.True(CoopRecoveryReloadPolicy.RequiresLocalReload(
                role,
                recoveryPending: true));
            Assert.False(CoopRecoveryReloadPolicy.RequiresLocalReload(
                role,
                recoveryPending: false));
        }

        [Fact]
        public void HostLoaderMayAdvanceWhileTheOldSimulationRemainsBlocked()
        {
            Assert.True(CoopRecoveryReloadPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: true,
                recoveryPending: true,
                localSnapshotLoadPending: true,
                snapshotVerificationPending: false));
            Assert.False(CoopFixedUpdatePolicy.ShouldRunGameStateUpdate(
                fullMode: true,
                worldTransferPending: true,
                allowSimulation: true));
            Assert.False(CoopRecoveryReloadPolicy.ShouldRunGameManagerFixedUpdate(
                isHost: true,
                recoveryPending: true,
                localSnapshotLoadPending: false,
                snapshotVerificationPending: false));
        }

        [Fact]
        public void EarlyGuestReadyCannotAuthorizeRecoveryStartBeforeHostReload()
        {
            Assert.False(CoopRecoveryReloadPolicy.CanAuthorizeRecoveryStart(
                hostLocalReady: false,
                guestReady: true,
                startAlreadyAuthorized: false));
        }

        [Fact]
        public void RecoveryStartIsAuthorizedOnceAfterBothPeersAreReady()
        {
            Assert.True(CoopRecoveryReloadPolicy.CanAuthorizeRecoveryStart(
                hostLocalReady: true,
                guestReady: true,
                startAlreadyAuthorized: false));
            Assert.False(CoopRecoveryReloadPolicy.CanAuthorizeRecoveryStart(
                hostLocalReady: true,
                guestReady: true,
                startAlreadyAuthorized: true));
        }

        [Theory]
        [InlineData(CoopRole.Host)]
        [InlineData(CoopRole.Guest)]
        public void BothPeersUseTheSameCheckpointLoadPath(CoopRole role)
        {
            Assert.Equal(
                "coop_recv.sav",
                CoopRecoveryReloadPolicy.GetCheckpointFileName(role));
        }

        [Fact]
        public void StartupCoordinatorKeepsEarlyGuestReadyUntilHostReloadCompletes()
        {
            var coordinator = new CoopStartupCoordinator(
                CoopRole.Host,
                sessionId: 99,
                worldHash: "checkpoint-hash",
                worldTick: 4096);

            Assert.True(coordinator.AcceptPeerReady(
                sessionId: 99,
                worldHash: "checkpoint-hash",
                tick: 4096));
            Assert.False(coordinator.AuthorizeStart());

            coordinator.MarkLocalWorldReady();

            Assert.True(coordinator.AuthorizeStart());
            Assert.False(coordinator.AuthorizeStart());
        }
    }
}
