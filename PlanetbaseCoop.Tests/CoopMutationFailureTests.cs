using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopMutationFailureTests
    {
        [Fact]
        public void MutationFailurePacketRoundTripsBoundedEnumData()
        {
            var failure = new CoopMutationFailureData(
                77,
                123,
                CoopMutationKind.Connection,
                CoopMutationFailureCode.TargetMissing);

            byte[] packet = CoopProtocol.CreateMutationFailurePacket(failure);

            Assert.True(CoopProtocol.TryParseMutationFailurePacket(packet, out CoopMutationFailureData parsed));
            Assert.Equal(77, parsed.SessionId);
            Assert.Equal(123, parsed.CommandId);
            Assert.Equal(CoopMutationKind.Connection, parsed.Kind);
            Assert.Equal(CoopMutationFailureCode.TargetMissing, parsed.Code);
        }

        [Fact]
        public void MutationFailurePacketRejectsUnknownEnumsAndTrailingBytes()
        {
            byte[] packet = CoopProtocol.CreateMutationFailurePacket(new CoopMutationFailureData(
                77,
                123,
                CoopMutationKind.Placement,
                CoopMutationFailureCode.ApplyFailed));

            packet[17] = 255;
            Assert.False(CoopProtocol.TryParseMutationFailurePacket(packet, out _));

            packet = CoopProtocol.CreateMutationFailurePacket(new CoopMutationFailureData(
                77,
                123,
                CoopMutationKind.Placement,
                CoopMutationFailureCode.ApplyFailed));
            System.Array.Resize(ref packet, packet.Length + 1);
            Assert.False(CoopProtocol.TryParseMutationFailurePacket(packet, out _));
        }

        [Fact]
        public void FailureLedgerAcceptsFirstCurrentSessionFailureOnly()
        {
            var ledger = new CoopMutationFailureLedger();
            var failure = new CoopMutationFailureData(
                77,
                123,
                CoopMutationKind.Connection,
                CoopMutationFailureCode.TargetMissing);

            Assert.True(ledger.TryRecord(failure, 77));
            Assert.False(ledger.TryRecord(failure, 77));
            Assert.False(ledger.TryRecord(new CoopMutationFailureData(
                76,
                124,
                CoopMutationKind.Connection,
                CoopMutationFailureCode.TargetMissing), 77));
        }

        [Fact]
        public void FailureLedgerIsBoundedAndCanResetForNewSession()
        {
            var ledger = new CoopMutationFailureLedger();
            for (long id = 1; id <= 200; id++)
            {
                Assert.True(ledger.TryRecord(new CoopMutationFailureData(
                    77,
                    id,
                    CoopMutationKind.GameplayControl,
                    CoopMutationFailureCode.ApplyFailed), 77));
            }
            Assert.InRange(ledger.Count, 1, CoopMutationFailureLedger.MaxEntries);

            ledger.Reset();
            Assert.Equal(0, ledger.Count);
            Assert.True(ledger.TryRecord(new CoopMutationFailureData(
                88,
                1,
                CoopMutationKind.GameplayControl,
                CoopMutationFailureCode.ApplyFailed), 88));
        }

        [Fact]
        public void OutstandingLedgerConsumesOnlyAnActuallySentMatchingMutation()
        {
            var ledger = new CoopOutstandingMutationLedger();
            var matching = new CoopMutationFailureData(
                77,
                123,
                CoopMutationKind.Trade,
                CoopMutationFailureCode.ApplyFailed);

            ledger.Register(77, 123, CoopMutationKind.Trade);

            Assert.False(ledger.TryConsume(new CoopMutationFailureData(
                77,
                999,
                CoopMutationKind.Trade,
                CoopMutationFailureCode.ApplyFailed), 77));
            Assert.False(ledger.TryConsume(new CoopMutationFailureData(
                77,
                123,
                CoopMutationKind.Disaster,
                CoopMutationFailureCode.ApplyFailed), 77));
            Assert.False(ledger.TryConsume(new CoopMutationFailureData(
                76,
                123,
                CoopMutationKind.Trade,
                CoopMutationFailureCode.ApplyFailed), 77));
            Assert.True(ledger.TryConsume(matching, 77));
            Assert.False(ledger.TryConsume(matching, 77));
        }

        [Fact]
        public void OutstandingLedgerIsBoundedAndResetClearsEntries()
        {
            var ledger = new CoopOutstandingMutationLedger();
            for (long id = 1; id <= CoopOutstandingMutationLedger.MaxEntries + 20; id++)
                ledger.Register(77, id, CoopMutationKind.GameplayControl);

            Assert.Equal(CoopOutstandingMutationLedger.MaxEntries, ledger.Count);
            Assert.False(ledger.TryConsume(new CoopMutationFailureData(
                77,
                1,
                CoopMutationKind.GameplayControl,
                CoopMutationFailureCode.ApplyFailed), 77));

            ledger.Reset();
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void OutgoingCommitDescriptorMapsAuthoritativeKindAndRejectsRequests()
        {
            var commit = new CoopAuthoritativeCommandData(
                77,
                123,
                50,
                CoopAuthoritativeCommandKind.Trade,
                CoopAuthoritativeCommandPhase.Commit,
                new byte[0]);

            Assert.True(CoopProtocol.TryDescribeCommittedMutation(
                CoopProtocol.CreateAuthoritativeCommandPacket(commit),
                out long sessionId,
                out long commandId,
                out CoopMutationKind kind));
            Assert.Equal(77, sessionId);
            Assert.Equal(123, commandId);
            Assert.Equal(CoopMutationKind.Trade, kind);

            var request = new CoopAuthoritativeCommandData(
                77,
                125,
                0,
                CoopAuthoritativeCommandKind.Trade,
                CoopAuthoritativeCommandPhase.Request,
                new byte[0]);
            Assert.False(CoopProtocol.TryDescribeCommittedMutation(
                CoopProtocol.CreateAuthoritativeCommandPacket(request),
                out _,
                out _,
                out _));
        }
    }
}
