using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopAuthoritativeCommandTests
    {
        [Fact]
        public void EnvelopeRoundTripsSessionSequenceTickKindPhaseAndPayload()
        {
            var command = new CoopAuthoritativeCommandData(
                77,
                9,
                512,
                CoopAuthoritativeCommandKind.SetEnabled,
                CoopAuthoritativeCommandPhase.Commit,
                new byte[] { 1, 2, 3 });

            byte[] packet = CoopProtocol.CreateAuthoritativeCommandPacket(command);

            Assert.True(CoopProtocol.TryParseAuthoritativeCommandPacket(packet, out CoopAuthoritativeCommandData parsed));
            Assert.Equal(77, parsed.SessionId);
            Assert.Equal(9, parsed.Sequence);
            Assert.Equal(512, parsed.ApplyTick);
            Assert.Equal(CoopAuthoritativeCommandKind.SetEnabled, parsed.Kind);
            Assert.Equal(CoopAuthoritativeCommandPhase.Commit, parsed.Phase);
            Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Payload);
        }

        [Fact]
        public void EnvelopeRejectsInvalidSessionSequencePhaseAndUnknownKind()
        {
            Assert.Throws<System.ArgumentException>(() => CoopProtocol.CreateAuthoritativeCommandPacket(
                new CoopAuthoritativeCommandData(0, 1, 0, CoopAuthoritativeCommandKind.SetEnabled,
                    CoopAuthoritativeCommandPhase.Request, new byte[0])));
            Assert.Throws<System.ArgumentException>(() => CoopProtocol.CreateAuthoritativeCommandPacket(
                new CoopAuthoritativeCommandData(1, 0, 0, CoopAuthoritativeCommandKind.SetEnabled,
                    CoopAuthoritativeCommandPhase.Request, new byte[0])));

            byte[] packet = CoopProtocol.CreateAuthoritativeCommandPacket(
                new CoopAuthoritativeCommandData(1, 1, 0, CoopAuthoritativeCommandKind.SetEnabled,
                    CoopAuthoritativeCommandPhase.Request, new byte[0]));
            packet[25] = 255;
            Assert.False(CoopProtocol.TryParseAuthoritativeCommandPacket(packet, out _));
        }

        [Fact]
        public void EnvelopeRejectsEveryTruncationAndTrailingByte()
        {
            byte[] packet = CoopProtocol.CreateAuthoritativeCommandPacket(
                new CoopAuthoritativeCommandData(1, 1, 0, CoopAuthoritativeCommandKind.SetAlertState,
                    CoopAuthoritativeCommandPhase.Request, new byte[] { 2 }));

            for (int length = 0; length < packet.Length; length++)
            {
                var truncated = new byte[length];
                System.Buffer.BlockCopy(packet, 0, truncated, 0, length);
                Assert.False(CoopProtocol.TryParseAuthoritativeCommandPacket(truncated, out _));
            }
            System.Array.Resize(ref packet, packet.Length + 1);
            Assert.False(CoopProtocol.TryParseAuthoritativeCommandPacket(packet, out _));
        }

        [Fact]
        public void EnvelopePayloadIsBoundedAndDefensivelyCopied()
        {
            var payload = new byte[] { 5, 6 };
            var command = new CoopAuthoritativeCommandData(
                1, 1, 0, CoopAuthoritativeCommandKind.SetEnabled,
                CoopAuthoritativeCommandPhase.Request, payload);
            payload[0] = 99;
            Assert.Equal(5, command.Payload[0]);

            Assert.Throws<System.ArgumentException>(() => new CoopAuthoritativeCommandData(
                1, 1, 0, CoopAuthoritativeCommandKind.SetEnabled,
                CoopAuthoritativeCommandPhase.Request,
                new byte[CoopAuthoritativeCommandData.MaxPayloadBytes + 1]));
        }

        [Fact]
        public void GameplayControlPayloadRoundTripsStableTargetAndValue()
        {
            var control = new CoopGameplayControlData(
                new CoopObjectReference(170, "ConstructionComponent", 10f, 0f, 20f, true),
                string.Empty,
                1);

            byte[] payload = CoopGameplayControlCodec.Encode(
                CoopAuthoritativeCommandKind.SetEnabled, control);

            Assert.True(CoopGameplayControlCodec.TryDecode(
                CoopAuthoritativeCommandKind.SetEnabled, payload, out CoopGameplayControlData parsed));
            Assert.Equal(170, parsed.Target.ObjectId);
            Assert.Equal(1, parsed.Value);
        }

        [Fact]
        public void GameplayControlRejectsInvalidBooleanAndUnsafeKey()
        {
            Assert.Throws<System.ArgumentException>(() => CoopGameplayControlCodec.Encode(
                CoopAuthoritativeCommandKind.SetEnabled,
                new CoopGameplayControlData(
                    new CoopObjectReference(170, "Construction", 10f, 0f, 20f, true),
                    string.Empty,
                    2)));
            Assert.Throws<System.ArgumentException>(() => CoopGameplayControlCodec.Encode(
                CoopAuthoritativeCommandKind.SetManufactureLimit,
                new CoopGameplayControlData(null, "../Metal", 20)));
        }

        [Fact]
        public void GridControlRequiresStableConstructionAnchor()
        {
            Assert.Throws<System.ArgumentException>(() => CoopGameplayControlCodec.Encode(
                CoopAuthoritativeCommandKind.SetGridEnabled,
                new CoopGameplayControlData(null, string.Empty, 1)));

            byte[] payload = CoopGameplayControlCodec.Encode(
                CoopAuthoritativeCommandKind.SetGridEnabled,
                new CoopGameplayControlData(
                    new CoopObjectReference(44, "Module", 10f, 0f, 20f, true),
                    string.Empty,
                    0));

            Assert.True(CoopGameplayControlCodec.TryDecode(
                CoopAuthoritativeCommandKind.SetGridEnabled,
                payload,
                out CoopGameplayControlData parsed));
            Assert.Equal(44, parsed.Target.ObjectId);
        }

        [Fact]
        public void GlobalControlsRejectUnexpectedKeysAndManufactureNamespaces()
        {
            Assert.Throws<System.ArgumentException>(() => CoopGameplayControlCodec.Encode(
                CoopAuthoritativeCommandKind.SetAlertState,
                new CoopGameplayControlData(null, "Yellow", 1)));
            Assert.Throws<System.ArgumentException>(() => CoopGameplayControlCodec.Encode(
                CoopAuthoritativeCommandKind.SetManufactureLimit,
                new CoopGameplayControlData(null, "Other_Metal", 10)));
        }
    }
}
