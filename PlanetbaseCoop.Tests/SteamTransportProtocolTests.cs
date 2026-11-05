using System;
using System.Collections.Generic;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class SteamTransportProtocolTests
    {
        [Fact]
        public void SmallControlPacketRoundTripsThroughSteamEnvelope()
        {
            byte[] payload = CoopProtocol.CreateTickPacket(1234);
            IReadOnlyList<byte[]> fragments = CoopSteamTransportProtocol.Fragment(
                payload, transferId: 7);

            Assert.Single(fragments);
            var assembler = new CoopSteamFragmentAssembler();
            Assert.Equal(CoopSteamFragmentResult.Complete,
                assembler.Accept(fragments[0], out byte[] rebuilt));
            Assert.Equal(payload, rebuilt);
        }

        [Theory]
        [InlineData(CoopProtocol.PktTick, CoopSteamDelivery.FastUnreliable)]
        [InlineData(CoopProtocol.PktHeartbeat, CoopSteamDelivery.FastUnreliable)]
        [InlineData(CoopProtocol.PktHash, CoopSteamDelivery.Reliable)]
        [InlineData(CoopProtocol.PktAuthoritativeCommand, CoopSteamDelivery.Reliable)]
        [InlineData(CoopProtocol.PktWorld, CoopSteamDelivery.Reliable)]
        public void SupersededTelemetryDoesNotBlockReliableGameplayTraffic(
            byte packetType,
            CoopSteamDelivery expected)
        {
            Assert.Equal(expected, CoopSteamDeliveryPolicy.ForPacketType(packetType));
        }

        [Fact]
        public void LargeSnapshotIsBoundedAndReassembledExactly()
        {
            byte[] payload = new byte[CoopSteamTransportProtocol.MaxChunkPayloadBytes * 2 + 17];
            new Random(480).NextBytes(payload);

            IReadOnlyList<byte[]> fragments = CoopSteamTransportProtocol.Fragment(
                payload, transferId: 99);
            Assert.Equal(3, fragments.Count);

            var assembler = new CoopSteamFragmentAssembler();
            byte[] rebuilt = null;
            for (int i = 0; i < fragments.Count; i++)
            {
                CoopSteamFragmentResult result = assembler.Accept(
                    fragments[i], out byte[] candidate);
                if (i + 1 < fragments.Count)
                    Assert.Equal(CoopSteamFragmentResult.Pending, result);
                else
                {
                    Assert.Equal(CoopSteamFragmentResult.Complete, result);
                    rebuilt = candidate;
                }
            }
            Assert.Equal(payload, rebuilt);
        }

        [Fact]
        public void OutOfOrderOrCorruptSteamFragmentsFailClosed()
        {
            byte[] payload = new byte[CoopSteamTransportProtocol.MaxChunkPayloadBytes + 1];
            IReadOnlyList<byte[]> fragments = CoopSteamTransportProtocol.Fragment(
                payload, transferId: 5);
            var assembler = new CoopSteamFragmentAssembler();

            Assert.Equal(CoopSteamFragmentResult.Invalid,
                assembler.Accept(fragments[1], out _));

            byte[] corrupt = (byte[])fragments[0].Clone();
            corrupt[0] ^= 0x7f;
            Assert.Equal(CoopSteamFragmentResult.Invalid,
                assembler.Accept(corrupt, out _));
        }

        [Theory]
        [InlineData("19", "480", 480U, true)]
        [InlineData("19", "403190", 403190U, true)]
        [InlineData("19", "403190", 480U, false)]
        [InlineData("18", "480", 480U, false)]
        [InlineData("19", "999", 480U, false)]
        [InlineData("garbage", "480", 480U, false)]
        public void LobbyMetadataRequiresExactProtocolAndSelectedApp(
            string protocol,
            string appId,
            uint expectedAppId,
            bool expected)
        {
            Assert.Equal(expected,
                CoopSteamLobbyPolicy.IsCompatible(protocol, appId,
                    expectedProtocol: 19,
                    expectedAppId: expectedAppId));
        }

        [Theory]
        [InlineData("403190", null, null, null, 403190U)]
        [InlineData(null, "403190", "480", "480", 403190U)]
        [InlineData(null, null, "403190", "480", 403190U)]
        [InlineData(null, null, null, "403190", 403190U)]
        [InlineData("999", null, null, null, 480U)]
        [InlineData(null, null, null, null, 480U)]
        public void SteamAppSelectionUsesSupportedValuesInPriorityOrder(
            string commandLineAppId,
            string steamAppId,
            string steamGameId,
            string appIdFile,
            uint expected)
        {
            string[] args = commandLineAppId == null
                ? new[] { "Planetbase.exe" }
                : new[] { "Planetbase.exe", "-coopSteamAppId", commandLineAppId };

            Assert.Equal(expected, CoopSteamAppPolicy.ResolveAppId(
                args, steamAppId, steamGameId, appIdFile));
        }

        [Theory]
        [InlineData(480U, "SPACEWAR")]
        [InlineData(403190U, "PLANETBASE")]
        public void SteamAppDisplayNameMatchesSelectedTransportApp(
            uint appId,
            string expected)
        {
            Assert.Equal(expected, CoopSteamAppPolicy.DisplayName(appId));
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        public void SteamInitializationCanRetryButNeverReenters(
            bool initialized,
            bool initializing,
            bool expected)
        {
            Assert.Equal(expected,
                CoopSteamLifecyclePolicy.ShouldAttemptInitialize(
                    initialized, initializing));
        }

        [Theory]
        [InlineData(CoopSteamAppPolicy.SpacewarAppId, true)]
        [InlineData(CoopSteamAppPolicy.PlanetbaseAppId, false)]
        public void QuitShutsDownSteamApiOnlyWhenTheModOwnsIt(uint appId, bool expected)
        {
            // Native Steam Planetbase'in SteamManager'i API'yi OnDestroy'da kapatir.
            Assert.Equal(expected,
                CoopSteamLifecyclePolicy.ShouldShutdownSteamApiOnQuit(appId));
        }

        [Theory]
        [InlineData(null, "STEAM ARKADAŞI")]
        [InlineData("   ", "STEAM ARKADAŞI")]
        [InlineData("  Ada\r\nLovelace  ", "Ada Lovelace")]
        public void SteamFriendNamesAreSafeForTheGameUi(string input, string expected)
        {
            Assert.Equal(expected,
                CoopSteamFriendPolicy.NormalizeDisplayName(input));
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(-1, 7, 0)]
        [InlineData(4, 7, 1)]
        [InlineData(99, 11, 2)]
        public void SteamFriendPagesStayInsideAvailableRows(
            int requestedPage,
            int friendCount,
            int expectedPage)
        {
            Assert.Equal(expectedPage,
                CoopSteamFriendPolicy.ClampPage(requestedPage, friendCount));
        }

        [Fact]
        public void AcceptedSteamInvitePassesEveryGuestSessionAdmissionGate()
        {
            const ulong lobbyId = 90000000000000001UL;
            const ulong hostId = 76561198000000001UL;
            const ulong guestId = 76561198000000002UL;

            CoopSteamInviteAdmission invite = CoopSteamJoinPolicy.DecideInvite(
                lobbyId, sessionActive: false, hasLobby: false);
            Assert.Equal(CoopSteamInviteAdmission.JoinLobby, invite);

            bool lobbyEntered = CoopSteamJoinPolicy.IsExpectedSuccessfulLobbyEnter(
                enteredLobbyId: lobbyId,
                pendingLobbyId: lobbyId,
                activeLobbyId: 0,
                creatingLobby: false,
                response: 1,
                successResponse: 1);
            Assert.True(lobbyEntered);

            bool metadataAccepted = CoopSteamJoinPolicy.TryResolveGuestHost(
                mod: "planetbase-coop",
                expectedMod: "planetbase-coop",
                protocol: "19",
                appId: "403190",
                hostText: hostId.ToString(),
                ownerSteamId: hostId,
                localSteamId: guestId,
                expectedProtocol: 19,
                expectedAppId: 403190,
                out ulong resolvedHostId);

            Assert.True(metadataAccepted);
            Assert.Equal(hostId, resolvedHostId);
        }

        [Theory]
        [InlineData(0UL, false, false, CoopSteamInviteAdmission.IgnoreInvalidLobby)]
        [InlineData(42UL, true, false, CoopSteamInviteAdmission.RejectActiveSession)]
        [InlineData(42UL, false, true, CoopSteamInviteAdmission.RejectActiveSession)]
        [InlineData(42UL, false, false, CoopSteamInviteAdmission.JoinLobby)]
        public void InviteAdmissionRejectsInvalidOrAlreadyActiveSessions(
            ulong lobbyId,
            bool sessionActive,
            bool hasLobby,
            CoopSteamInviteAdmission expected)
        {
            Assert.Equal(expected,
                CoopSteamJoinPolicy.DecideInvite(lobbyId, sessionActive, hasLobby));
        }

        [Theory]
        [InlineData(42UL, 42UL, 0UL, false, 1U, 1U, true)]
        [InlineData(43UL, 42UL, 0UL, false, 1U, 1U, false)]
        [InlineData(42UL, 42UL, 42UL, false, 1U, 1U, false)]
        [InlineData(42UL, 42UL, 0UL, true, 1U, 1U, false)]
        [InlineData(42UL, 42UL, 0UL, false, 2U, 1U, false)]
        public void LobbyEnterMustMatchTheAcceptedInvitation(
            ulong enteredLobbyId,
            ulong pendingLobbyId,
            ulong activeLobbyId,
            bool creatingLobby,
            uint response,
            uint successResponse,
            bool expected)
        {
            Assert.Equal(expected,
                CoopSteamJoinPolicy.IsExpectedSuccessfulLobbyEnter(
                    enteredLobbyId, pendingLobbyId, activeLobbyId,
                    creatingLobby, response, successResponse));
        }

        [Theory]
        [InlineData("other-mod", "19", "480", "100", 100UL, 200UL)]
        [InlineData("planetbase-coop", "18", "480", "100", 100UL, 200UL)]
        [InlineData("planetbase-coop", "19", "999", "100", 100UL, 200UL)]
        [InlineData("planetbase-coop", "19", "480", "100", 101UL, 200UL)]
        [InlineData("planetbase-coop", "19", "480", "100", 100UL, 100UL)]
        public void GuestAdmissionRejectsSpoofedOrIncompatibleLobbyMetadata(
            string mod,
            string protocol,
            string appId,
            string hostText,
            ulong ownerSteamId,
            ulong localSteamId)
        {
            Assert.False(CoopSteamJoinPolicy.TryResolveGuestHost(
                mod,
                "planetbase-coop",
                protocol,
                appId,
                hostText,
                ownerSteamId,
                localSteamId,
                expectedProtocol: 19,
                expectedAppId: 480,
                out ulong resolvedHostId));
            Assert.Equal(0UL, resolvedHostId);
        }
    }
}
