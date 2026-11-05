using System;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopVisitorEventCommandTests
    {
        [Theory]
        [InlineData("VisitorEventInjuredMiners", 5)]
        [InlineData("VisitorEventSpatialAccidentVictims", 15)]
        public void VisitorEventPayloadRoundTrips(string eventTypeName, int visitorCount)
        {
            var expected = new CoopVisitorEventCommandData(eventTypeName, visitorCount);

            byte[] payload = CoopVisitorEventCommandCodec.Encode(expected);

            Assert.True(CoopVisitorEventCommandCodec.TryDecode(
                payload, out CoopVisitorEventCommandData actual));
            Assert.Equal(eventTypeName, actual.EventTypeName);
            Assert.Equal(visitorCount, actual.VisitorCount);
        }

        [Theory]
        [InlineData("", 10)]
        [InlineData("../VisitorEvent", 10)]
        [InlineData("VisitorEventInjuredMiners", 4)]
        [InlineData("VisitorEventInjuredMiners", 16)]
        public void VisitorEventPayloadRejectsInvalidValues(
            string eventTypeName,
            int visitorCount)
        {
            Assert.Throws<ArgumentException>(() => CoopVisitorEventCommandCodec.Encode(
                new CoopVisitorEventCommandData(eventTypeName, visitorCount)));
        }

        [Fact]
        public void VisitorEventPayloadRejectsEveryTruncationAndTrailingByte()
        {
            byte[] payload = CoopVisitorEventCommandCodec.Encode(
                new CoopVisitorEventCommandData("VisitorEventInjuredMiners", 10));

            for (int length = 0; length < payload.Length; length++)
            {
                var truncated = new byte[length];
                Buffer.BlockCopy(payload, 0, truncated, 0, length);
                Assert.False(CoopVisitorEventCommandCodec.TryDecode(truncated, out _));
            }

            Array.Resize(ref payload, payload.Length + 1);
            Assert.False(CoopVisitorEventCommandCodec.TryDecode(payload, out _));
        }

        [Fact]
        public void AuthoritativeEnvelopeAcceptsVisitorEventCommandKind()
        {
            var command = new CoopAuthoritativeCommandData(
                77,
                1,
                0,
                CoopAuthoritativeCommandKind.VisitorEventAccept,
                CoopAuthoritativeCommandPhase.Request,
                CoopVisitorEventCommandCodec.Encode(
                    new CoopVisitorEventCommandData("VisitorEventInjuredMiners", 10)));

            byte[] packet = CoopProtocol.CreateAuthoritativeCommandPacket(command);

            Assert.True(CoopProtocol.TryParseAuthoritativeCommandPacket(
                packet, out CoopAuthoritativeCommandData parsed));
            Assert.Equal(CoopAuthoritativeCommandKind.VisitorEventAccept, parsed.Kind);
        }
    }
}
