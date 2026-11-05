using System;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class LandingPermissionsProtocolTests
    {
        [Fact]
        public void LandingPermissionsPayload_RoundTripsGuestRequestValues()
        {
            CoopLandingPermissionsData expected = Permissions(
                colonistsAllowed: false,
                merchantsAllowed: true,
                visitorsAllowed: false,
                specializationPercentages: new[] { 30, 20, 20, 20, 10 });

            byte[] payload = CoopLandingPermissionsCodec.Encode(expected);

            Assert.True(CoopLandingPermissionsCodec.TryDecode(
                payload, out CoopLandingPermissionsData actual));
            Assert.False(actual.ColonistsAllowed);
            Assert.True(actual.MerchantsAllowed);
            Assert.False(actual.VisitorsAllowed);
            Assert.Equal(new[] { 30, 20, 20, 20, 10 }, actual.SpecializationPercentages);
        }

        [Fact]
        public void LandingPermissionsPayload_RejectsTruncationTrailingAndInvalidFlags()
        {
            byte[] payload = CoopLandingPermissionsCodec.Encode(Permissions());
            for (int length = 0; length < payload.Length; length++)
            {
                var truncated = new byte[length];
                System.Buffer.BlockCopy(payload, 0, truncated, 0, length);
                Assert.False(CoopLandingPermissionsCodec.TryDecode(truncated, out _));
            }

            var trailing = new byte[payload.Length + 1];
            System.Buffer.BlockCopy(payload, 0, trailing, 0, payload.Length);
            Assert.False(CoopLandingPermissionsCodec.TryDecode(trailing, out _));

            payload[0] = 2;
            Assert.False(CoopLandingPermissionsCodec.TryDecode(payload, out _));
        }

        private const int SessionIdOffset = 1;
        private const int SequenceOffset = SessionIdOffset + sizeof(long);
        private const int ApplyTickOffset = SequenceOffset + sizeof(long);
        private const int ColonistsAllowedOffset = ApplyTickOffset + sizeof(long);
        private const int MerchantsAllowedOffset = ColonistsAllowedOffset + sizeof(byte);
        private const int VisitorsAllowedOffset = MerchantsAllowedOffset + sizeof(byte);
        private const int SpecializationCountOffset = VisitorsAllowedOffset + sizeof(byte);
        private const int SpecializationsOffset = SpecializationCountOffset + sizeof(byte);

        [Fact]
        public void LandingPermissionsData_CloneOwnsIndependentSpecializationArray()
        {
            CoopLandingPermissionsData source = Permissions(
                specializationPercentages: new[] { 40, 30, 20, 10 });

            CoopLandingPermissionsData clone = source.Clone();
            source.SpecializationPercentages[0] = 100;

            Assert.NotSame(source, clone);
            Assert.NotSame(source.SpecializationPercentages, clone.SpecializationPercentages);
            Assert.Equal(new[] { 40, 30, 20, 10 }, clone.SpecializationPercentages);
        }

        [Fact]
        public void LandingPermissionsPacket_RoundTripsFlagsAndSpecializations()
        {
            CoopLandingPermissionsData expected = Permissions(
                sequence: 41,
                applyTick: 2048,
                colonistsAllowed: true,
                merchantsAllowed: false,
                visitorsAllowed: true,
                specializationPercentages: new[] { 35, 25, 20, 10, 10 });

            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(expected);

            Assert.Equal(CoopProtocol.PktLandingPermissions, packet[0]);
            Assert.Equal(SpecializationsOffset + expected.SpecializationPercentages.Length, packet.Length);
            Assert.True(CoopProtocol.TryParseLandingPermissionsPacket(
                packet,
                out CoopLandingPermissionsData actual));
            Assert.NotSame(expected, actual);
            Assert.Equal(expected.SessionId, actual.SessionId);
            Assert.Equal(expected.Sequence, actual.Sequence);
            Assert.Equal(expected.ApplyTick, actual.ApplyTick);
            Assert.Equal(expected.ColonistsAllowed, actual.ColonistsAllowed);
            Assert.Equal(expected.MerchantsAllowed, actual.MerchantsAllowed);
            Assert.Equal(expected.VisitorsAllowed, actual.VisitorsAllowed);
            Assert.NotSame(expected.SpecializationPercentages, actual.SpecializationPercentages);
            Assert.Equal(expected.SpecializationPercentages, actual.SpecializationPercentages);
        }

        [Fact]
        public void LandingPermissionsPacket_RejectsMissingSessionId()
        {
            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(Permissions());
            Buffer.BlockCopy(BitConverter.GetBytes(0L), 0, packet, SessionIdOffset, sizeof(long));

            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(packet, out _));
        }

        [Fact]
        public void LandingPermissionsPacket_UsesReservedPacketType22()
        {
            Assert.Equal((byte)22, CoopProtocol.PktLandingPermissions);
        }

        [Fact]
        public void LandingPermissionsPacket_RejectsNullWrongTypeTruncatedAndTrailingPayloads()
        {
            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(Permissions());
            byte[] wrongType = (byte[])packet.Clone();
            wrongType[0] = CoopProtocol.PktShipSpawn;
            byte[] truncated = new byte[packet.Length - 1];
            Buffer.BlockCopy(packet, 0, truncated, 0, truncated.Length);
            byte[] trailing = new byte[packet.Length + 1];
            Buffer.BlockCopy(packet, 0, trailing, 0, packet.Length);

            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(null, out _));
            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(wrongType, out _));
            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(truncated, out _));
            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(trailing, out _));
        }

        [Theory]
        [InlineData(SequenceOffset, 0L)]
        [InlineData(SequenceOffset, -1L)]
        [InlineData(SequenceOffset, 2147483647L)]
        [InlineData(ApplyTickOffset, -1L)]
        [InlineData(ApplyTickOffset, 2147483647L)]
        public void LandingPermissionsPacket_RejectsInvalidSequenceAndApplyTick(int offset, long invalidValue)
        {
            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(Permissions());
            Buffer.BlockCopy(BitConverter.GetBytes(invalidValue), 0, packet, offset, sizeof(long));

            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(packet, out _));
        }

        [Theory]
        [InlineData(ColonistsAllowedOffset)]
        [InlineData(MerchantsAllowedOffset)]
        [InlineData(VisitorsAllowedOffset)]
        public void LandingPermissionsPacket_RejectsNonBooleanFlags(int offset)
        {
            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(Permissions());
            packet[offset] = 2;

            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(packet, out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(17)]
        [InlineData(255)]
        public void LandingPermissionsPacket_RejectsOutOfRangeSpecializationCount(int count)
        {
            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(Permissions());
            packet[SpecializationCountOffset] = (byte)count;

            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(packet, out _));
        }

        [Fact]
        public void LandingPermissionsPacket_RejectsSpecializationAbove100()
        {
            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(Permissions());
            packet[SpecializationsOffset] = 101;

            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(packet, out _));
        }

        [Fact]
        public void LandingPermissionsPacket_RejectsSpecializationsWhoseTotalIsNot100()
        {
            byte[] packet = CoopProtocol.CreateLandingPermissionsPacket(Permissions());
            packet[SpecializationsOffset] = 49;

            Assert.False(CoopProtocol.TryParseLandingPermissionsPacket(packet, out _));
        }

        [Theory]
        [MemberData(nameof(InvalidCreateData))]
        public void CreateLandingPermissionsPacket_RejectsMalformedData(
            CoopLandingPermissionsData invalid)
        {
            Assert.Throws<ArgumentException>(() =>
                CoopProtocol.CreateLandingPermissionsPacket(invalid));
        }

        [Fact]
        public void LandingPermissionsQueue_AcceptsOnceAndRejectsDuplicateSequence()
        {
            CoopLandingPermissionsQueue queue = new CoopLandingPermissionsQueue(capacity: 4);
            CoopLandingPermissionsData command = Permissions(sequence: 1, applyTick: 103);

            Assert.True(queue.TrySchedule(command, currentTick: 100));
            command.SpecializationPercentages[0] = 100;

            Assert.False(queue.TrySchedule(command, currentTick: 100));
            Assert.Equal(1, queue.Count);
            Assert.True(queue.TryDequeueDue(103, out CoopLandingPermissionsData due));
            Assert.Equal(new[] { 50, 50 }, due.SpecializationPercentages);
        }

        [Fact]
        public void LandingPermissionsQueue_DetectsRepeatedPendingValues()
        {
            CoopLandingPermissionsQueue queue = new CoopLandingPermissionsQueue(capacity: 4);
            CoopLandingPermissionsData pending = Permissions(sequence: 1, applyTick: 103);
            Assert.True(queue.TrySchedule(pending, currentTick: 100));

            CoopLandingPermissionsData repeated = Permissions(sequence: 99, applyTick: 999);
            Assert.True(queue.HasPendingValues(repeated));

            repeated.MerchantsAllowed = !repeated.MerchantsAllowed;
            Assert.False(queue.HasPendingValues(repeated));
        }

        [Fact]
        public void LandingPermissionsQueue_RequiresContiguousSequenceAndBoundedLead()
        {
            CoopLandingPermissionsQueue queue = new CoopLandingPermissionsQueue(capacity: 4);

            Assert.False(queue.TrySchedule(Permissions(sequence: 2, applyTick: 101), currentTick: 100));
            Assert.False(queue.TrySchedule(
                Permissions(sequence: 1, applyTick: 100 + CoopProtocol.MaxCommandLeadTicks + 1),
                currentTick: 100));
            Assert.True(queue.TrySchedule(Permissions(sequence: 1, applyTick: 101), currentTick: 100));
            Assert.False(queue.TrySchedule(Permissions(sequence: 3, applyTick: 102), currentTick: 100));
            Assert.True(queue.TrySchedule(Permissions(sequence: 2, applyTick: 102), currentTick: 100));
        }

        [Fact]
        public void LandingPermissionsQueue_RejectsLateAndCapacityOverflow()
        {
            CoopLandingPermissionsQueue queue = new CoopLandingPermissionsQueue(capacity: 2);

            Assert.False(queue.TrySchedule(Permissions(sequence: 1, applyTick: 99), currentTick: 100));
            Assert.True(queue.TrySchedule(Permissions(sequence: 1, applyTick: 101), currentTick: 100));
            Assert.True(queue.TrySchedule(Permissions(sequence: 2, applyTick: 102), currentTick: 100));
            Assert.False(queue.TrySchedule(Permissions(sequence: 3, applyTick: 103), currentTick: 100));
            Assert.Equal(2, queue.Count);
        }

        [Fact]
        public void LandingPermissionsQueue_ReleasesOnlyAtExactTickInDeterministicOrder()
        {
            CoopLandingPermissionsQueue queue = new CoopLandingPermissionsQueue(capacity: 4);
            Assert.True(queue.TrySchedule(Permissions(sequence: 1, applyTick: 103), currentTick: 100));
            Assert.True(queue.TrySchedule(Permissions(sequence: 2, applyTick: 104), currentTick: 100));

            Assert.False(queue.TryDequeueDue(currentTick: 102, out _));
            Assert.True(queue.TryDequeueDue(currentTick: 103, out CoopLandingPermissionsData first));
            Assert.Equal(1, first.Sequence);
            Assert.False(queue.TryDequeueDue(currentTick: 103, out _));
            Assert.True(queue.TryDequeueDue(currentTick: 104, out CoopLandingPermissionsData second));
            Assert.Equal(2, second.Sequence);
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void LandingPermissionsQueue_ReportsLateHeadAndRejectsReleasedReplay()
        {
            CoopLandingPermissionsQueue queue = new CoopLandingPermissionsQueue(capacity: 2);
            CoopLandingPermissionsData command = Permissions(sequence: 1, applyTick: 101);
            Assert.True(queue.TrySchedule(command, currentTick: 100));

            Assert.True(queue.HasLate(currentTick: 102));
            Assert.False(queue.TryDequeueDue(currentTick: 102, out _));
            Assert.Equal(1, queue.Count);

            queue.Clear();
            Assert.True(queue.TrySchedule(command, currentTick: 100));
            Assert.True(queue.TryDequeueDue(currentTick: 101, out _));
            Assert.False(queue.TrySchedule(command, currentTick: 101));
        }

        [Fact]
        public void LandingPermissionsQueue_ClearRemovesPendingAndReplayWatermark()
        {
            CoopLandingPermissionsQueue queue = new CoopLandingPermissionsQueue(capacity: 2);
            CoopLandingPermissionsData command = Permissions(sequence: 1, applyTick: 101);
            Assert.True(queue.TrySchedule(command, currentTick: 100));
            Assert.True(queue.TryDequeueDue(currentTick: 101, out _));

            queue.Clear();

            Assert.Equal(0, queue.Count);
            Assert.True(queue.TrySchedule(command, currentTick: 100));
        }

        public static TheoryData<CoopLandingPermissionsData> InvalidCreateData => new()
        {
            { null },
            { Permissions(sequence: 0) },
            { Permissions(applyTick: -1) },
            { PermissionsWithNullSpecializations() },
            { Permissions(specializationPercentages: Array.Empty<int>()) },
            { Permissions(specializationPercentages: new int[17]) },
            { Permissions(specializationPercentages: new[] { -1, 101 }) },
            { Permissions(specializationPercentages: new[] { 40, 40 }) }
        };

        private static CoopLandingPermissionsData Permissions(
            long sessionId = 77,
            long sequence = 1,
            long applyTick = 100,
            bool colonistsAllowed = true,
            bool merchantsAllowed = true,
            bool visitorsAllowed = false,
            int[] specializationPercentages = null)
        {
            return new CoopLandingPermissionsData
            {
                SessionId = sessionId,
                Sequence = sequence,
                ApplyTick = applyTick,
                ColonistsAllowed = colonistsAllowed,
                MerchantsAllowed = merchantsAllowed,
                VisitorsAllowed = visitorsAllowed,
                SpecializationPercentages = specializationPercentages ?? new[] { 50, 50 }
            };
        }

        private static CoopLandingPermissionsData PermissionsWithNullSpecializations()
        {
            CoopLandingPermissionsData permissions = Permissions();
            permissions.SpecializationPercentages = null;
            return permissions;
        }
    }
}
