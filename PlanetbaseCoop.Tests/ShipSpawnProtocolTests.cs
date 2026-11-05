using System;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class ShipSpawnProtocolTests
    {
        private const int SessionIdOffset = 1;
        private const int SequenceOffset = SessionIdOffset + sizeof(long);
        private const int ApplyTickOffset = SequenceOffset + sizeof(long);
        private const int ModuleIdOffset = ApplyTickOffset + sizeof(long);
        private const int KindOffset = ModuleIdOffset + sizeof(int);
        private const int SizeIndexOffset = KindOffset + sizeof(byte);
        private const int VisitorTypeOffset = SizeIndexOffset + sizeof(byte);
        private const int IntrudersOffset = VisitorTypeOffset + sizeof(byte);

        [Theory]
        [InlineData(CoopShipKind.Merchant, 0, 2)]
        [InlineData(CoopShipKind.Colonist, 1, 2)]
        [InlineData(CoopShipKind.Visitor, 0, 0)]
        [InlineData(CoopShipKind.Visitor, 1, 1)]
        public void ShipSpawnPacket_RoundTripsEverySupportedShipKind(
            CoopShipKind kind,
            int sizeIndex,
            int visitorType)
        {
            CoopShipSpawnData expected = Spawn(
                sequence: 41,
                applyTick: 2048,
                moduleId: 73,
                kind: kind,
                sizeIndex: sizeIndex,
                visitorType: visitorType);

            byte[] packet = CoopProtocol.CreateShipSpawnPacket(expected);

            Assert.True(CoopProtocol.TryParseShipSpawnPacket(packet, out CoopShipSpawnData actual));
            Assert.NotSame(expected, actual);
            Assert.Equal(expected.SessionId, actual.SessionId);
            Assert.Equal(expected.Sequence, actual.Sequence);
            Assert.Equal(expected.ApplyTick, actual.ApplyTick);
            Assert.Equal(expected.ModuleId, actual.ModuleId);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.SizeIndex, actual.SizeIndex);
            Assert.Equal(expected.VisitorType, actual.VisitorType);
            Assert.Equal(expected.Intruders, actual.Intruders);
        }

        [Fact]
        public void ShipSpawnPacket_RoundTripsStableLandingModuleReference()
        {
            CoopShipSpawnData expected = Spawn(moduleId: 73);
            expected.ModuleReference = new CoopObjectReference(
                73, "Module", 910f, 0f, 948f, true);

            byte[] packet = CoopProtocol.CreateShipSpawnPacket(expected);

            Assert.True(CoopProtocol.TryParseShipSpawnPacket(packet, out CoopShipSpawnData actual));
            Assert.Equal(73, actual.ModuleReference.ObjectId);
            Assert.Equal(910f, actual.ModuleReference.PositionX);
            Assert.True(actual.ModuleReference.HasPosition);
        }

        [Fact]
        public void ShipSpawnPacket_RoundTripsSpecialVisitorEvent()
        {
            CoopShipSpawnData expected = Spawn(
                kind: CoopShipKind.Visitor,
                sizeIndex: CoopShipSpawnValidator.LargeSize,
                visitorType: CoopShipSpawnValidator.CivilianVisitor,
                visitorEventTypeName: "VisitorEventInjuredMiners",
                visitorCount: 12);

            byte[] packet = CoopProtocol.CreateShipSpawnPacket(expected);

            Assert.True(CoopProtocol.TryParseShipSpawnPacket(packet, out CoopShipSpawnData actual));
            Assert.Equal("VisitorEventInjuredMiners", actual.VisitorEventTypeName);
            Assert.Equal(12, actual.VisitorCount);
        }

        [Theory]
        [InlineData("../VisitorEvent", 12, CoopShipSpawnValidator.LargeSize)]
        [InlineData("VisitorEventInjuredMiners", 4, CoopShipSpawnValidator.RegularSize)]
        [InlineData("VisitorEventInjuredMiners", 16, CoopShipSpawnValidator.LargeSize)]
        [InlineData("VisitorEventInjuredMiners", 12, CoopShipSpawnValidator.RegularSize)]
        public void ShipSpawnPacket_RejectsInvalidSpecialVisitorEvent(
            string eventTypeName,
            int visitorCount,
            int sizeIndex)
        {
            Assert.Throws<ArgumentException>(() => CoopProtocol.CreateShipSpawnPacket(
                Spawn(
                    kind: CoopShipKind.Visitor,
                    sizeIndex: sizeIndex,
                    visitorType: CoopShipSpawnValidator.CivilianVisitor,
                    visitorEventTypeName: eventTypeName,
                    visitorCount: visitorCount)));
        }

        [Fact]
        public void NonVisitorShipSpawnPacket_RejectsVisitorEventData()
        {
            Assert.Throws<ArgumentException>(() => CoopProtocol.CreateShipSpawnPacket(
                Spawn(
                    visitorEventTypeName: "VisitorEventInjuredMiners",
                    visitorCount: 8)));
        }

        [Fact]
        public void ShipSpawnPacket_RejectsMissingSessionId()
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(Spawn());
            Buffer.BlockCopy(BitConverter.GetBytes(0L), 0, packet, SessionIdOffset, sizeof(long));

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Fact]
        public void ShipSpawnPacket_RejectsTruncatedAndTrailingPayloads()
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(Spawn());
            byte[] truncated = new byte[packet.Length - 1];
            Buffer.BlockCopy(packet, 0, truncated, 0, truncated.Length);
            byte[] trailing = new byte[packet.Length + 1];
            Buffer.BlockCopy(packet, 0, trailing, 0, packet.Length);

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(truncated, out _));
            Assert.False(CoopProtocol.TryParseShipSpawnPacket(trailing, out _));
        }

        [Theory]
        [InlineData(SequenceOffset, 0L)]
        [InlineData(SequenceOffset, -1L)]
        [InlineData(ApplyTickOffset, -1L)]
        [InlineData(ApplyTickOffset, long.MaxValue)]
        public void ShipSpawnPacket_RejectsInvalidSequenceAndApplyTick(int offset, long invalidValue)
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(Spawn());
            Buffer.BlockCopy(BitConverter.GetBytes(invalidValue), 0, packet, offset, sizeof(long));

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ShipSpawnPacket_RejectsInvalidLandingModule(int invalidModuleId)
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(Spawn());
            Buffer.BlockCopy(BitConverter.GetBytes(invalidModuleId), 0, packet, ModuleIdOffset, sizeof(int));

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Fact]
        public void ShipSpawnPacket_RejectsUnknownShipKind()
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(Spawn());
            packet[KindOffset] = byte.MaxValue;

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(255)]
        public void ShipSpawnPacket_RejectsUnknownSize(int invalidSizeIndex)
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(Spawn());
            packet[SizeIndexOffset] = (byte)invalidSizeIndex;

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(255)]
        public void VisitorShipSpawnPacket_RejectsUnknownVisitorType(int invalidVisitorType)
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(
                Spawn(kind: CoopShipKind.Visitor, visitorType: 0));
            packet[VisitorTypeOffset] = (byte)invalidVisitorType;

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Theory]
        [InlineData(CoopShipKind.Merchant)]
        [InlineData(CoopShipKind.Colonist)]
        public void NonVisitorShipSpawnPacket_RequiresVisitorTypeSentinel(CoopShipKind kind)
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(
                Spawn(kind: kind, visitorType: 2));
            packet[VisitorTypeOffset] = 0;

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(255)]
        public void ShipSpawnPacket_RejectsInvalidIntruderFlag(int invalidFlag)
        {
            byte[] packet = CoopProtocol.CreateShipSpawnPacket(Spawn());
            packet[IntrudersOffset] = (byte)invalidFlag;

            Assert.False(CoopProtocol.TryParseShipSpawnPacket(packet, out _));
        }

        [Fact]
        public void MerchantShipSpawnPacket_RejectsIntruders()
        {
            Assert.Throws<ArgumentException>(() =>
                CoopProtocol.CreateShipSpawnPacket(
                    Spawn(kind: CoopShipKind.Merchant, intruders: true)));
        }

        [Fact]
        public void ShipSpawnQueue_AcceptsOnceAndRejectsDuplicateSequence()
        {
            CoopShipSpawnQueue queue = new CoopShipSpawnQueue(capacity: 4);
            CoopShipSpawnData spawn = Spawn(sequence: 1, applyTick: 103);

            Assert.True(queue.TrySchedule(spawn, currentTick: 100));
            Assert.False(queue.TrySchedule(spawn, currentTick: 100));
            Assert.Equal(1, queue.Count);
        }

        [Fact]
        public void ShipSpawnQueue_RequiresContiguousSequenceAndBoundedLead()
        {
            CoopShipSpawnQueue queue = new CoopShipSpawnQueue(capacity: 4);

            Assert.False(queue.TrySchedule(Spawn(sequence: 2, applyTick: 101), currentTick: 100));
            Assert.False(queue.TrySchedule(
                Spawn(sequence: 1, applyTick: 100 + CoopProtocol.MaxCommandLeadTicks + 1),
                currentTick: 100));
            Assert.True(queue.TrySchedule(Spawn(sequence: 1, applyTick: 101), currentTick: 100));
            Assert.False(queue.TrySchedule(Spawn(sequence: 3, applyTick: 102), currentTick: 100));
            Assert.True(queue.TrySchedule(Spawn(sequence: 2, applyTick: 102), currentTick: 100));
        }

        [Fact]
        public void ShipSpawnQueue_RejectsLateAndCapacityOverflow()
        {
            CoopShipSpawnQueue queue = new CoopShipSpawnQueue(capacity: 2);

            Assert.False(queue.TrySchedule(Spawn(sequence: 1, applyTick: 99), currentTick: 100));
            Assert.True(queue.TrySchedule(Spawn(sequence: 1, applyTick: 101), currentTick: 100));
            Assert.True(queue.TrySchedule(Spawn(sequence: 2, applyTick: 102), currentTick: 100));
            Assert.False(queue.TrySchedule(Spawn(sequence: 3, applyTick: 103), currentTick: 100));
            Assert.Equal(2, queue.Count);
        }

        [Fact]
        public void ShipSpawnQueue_ReleasesOnlyAtDueTickInDeterministicOrder()
        {
            CoopShipSpawnQueue queue = new CoopShipSpawnQueue(capacity: 4);
            Assert.True(queue.TrySchedule(Spawn(sequence: 1, applyTick: 103), currentTick: 100));
            Assert.True(queue.TrySchedule(Spawn(sequence: 2, applyTick: 104), currentTick: 100));

            Assert.False(queue.TryDequeueDue(currentTick: 102, out _));
            Assert.Equal(2, queue.Count);

            Assert.True(queue.TryDequeueDue(currentTick: 103, out CoopShipSpawnData first));
            Assert.Equal(1, first.Sequence);
            Assert.False(queue.TryDequeueDue(currentTick: 103, out _));

            Assert.True(queue.TryDequeueDue(currentTick: 104, out CoopShipSpawnData second));
            Assert.Equal(2, second.Sequence);
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void ShipSpawnQueue_RemembersReleasedSequenceAndRejectsReplay()
        {
            CoopShipSpawnQueue queue = new CoopShipSpawnQueue(capacity: 2);
            CoopShipSpawnData spawn = Spawn(sequence: 1, applyTick: 101);

            Assert.True(queue.TrySchedule(spawn, currentTick: 100));
            Assert.True(queue.TryDequeueDue(currentTick: 101, out _));
            Assert.False(queue.TrySchedule(spawn, currentTick: 101));
        }

        [Fact]
        public void ShipSpawnQueue_ReportsLateHeadWithoutDequeuingIt()
        {
            CoopShipSpawnQueue queue = new CoopShipSpawnQueue(capacity: 2);
            Assert.True(queue.TrySchedule(Spawn(sequence: 1, applyTick: 101), currentTick: 100));

            Assert.True(queue.HasLate(currentTick: 102));
            Assert.False(queue.TryDequeueDue(currentTick: 102, out _));
            Assert.Equal(1, queue.Count);
        }

        [Fact]
        public void ShipSpawnQueue_ClearRemovesPendingAndReplayWatermark()
        {
            CoopShipSpawnQueue queue = new CoopShipSpawnQueue(capacity: 2);
            CoopShipSpawnData spawn = Spawn(sequence: 1, applyTick: 101);
            Assert.True(queue.TrySchedule(spawn, currentTick: 100));
            Assert.True(queue.TryDequeueDue(currentTick: 101, out _));

            queue.Clear();

            Assert.Equal(0, queue.Count);
            Assert.True(queue.TrySchedule(spawn, currentTick: 100));
        }

        [Fact]
        public void LandingReservations_PreventTwoPendingShipsFromUsingOneModule()
        {
            CoopShipLandingReservations reservations =
                new CoopShipLandingReservations();

            Assert.True(reservations.TryReserve(10));
            Assert.False(reservations.TryReserve(10));
            Assert.True(reservations.TryReserve(11));

            reservations.Release(10);
            Assert.True(reservations.TryReserve(10));

            reservations.Clear();
            Assert.True(reservations.TryReserve(11));
        }

        private static CoopShipSpawnData Spawn(
            long sessionId = 77,
            long sequence = 1,
            long applyTick = 100,
            int moduleId = 10,
            CoopShipKind kind = CoopShipKind.Merchant,
            int sizeIndex = 0,
            int visitorType = 2,
            bool intruders = false,
            string visitorEventTypeName = "",
            int visitorCount = 0)
        {
            return new CoopShipSpawnData
            {
                SessionId = sessionId,
                Sequence = sequence,
                ApplyTick = applyTick,
                ModuleId = moduleId,
                Kind = kind,
                SizeIndex = sizeIndex,
                VisitorType = visitorType,
                Intruders = intruders,
                VisitorEventTypeName = visitorEventTypeName,
                VisitorCount = visitorCount
            };
        }
    }
}
