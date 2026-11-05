using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopDisasterEventTests
    {
        [Theory]
        [InlineData(CoopDisasterEventKind.Sandstorm)]
        [InlineData(CoopDisasterEventKind.Blizzard)]
        [InlineData(CoopDisasterEventKind.SolarFlare)]
        [InlineData(CoopDisasterEventKind.Meteor)]
        [InlineData(CoopDisasterEventKind.Thunder)]
        public void EventPayloadRoundTripsEverySupportedKind(CoopDisasterEventKind kind)
        {
            bool targeted = kind == CoopDisasterEventKind.Meteor ||
                kind == CoopDisasterEventKind.Thunder;
            var expected = new CoopDisasterEventData(
                kind,
                targeted,
                targeted ? 12.5f : 0f,
                targeted ? 0.25f : 0f,
                targeted ? -8f : 0f);

            byte[] payload = CoopDisasterEventCodec.Encode(expected);

            Assert.True(CoopDisasterEventCodec.TryDecode(
                payload, out CoopDisasterEventData actual));
            Assert.Equal(kind, actual.Kind);
            Assert.Equal(targeted, actual.HasTarget);
            Assert.Equal(expected.TargetX, actual.TargetX);
            Assert.Equal(expected.TargetZ, actual.TargetZ);
        }

        [Fact]
        public void EventPayloadRejectsUnknownKindInvalidTargetAndMalformedLength()
        {
            Assert.Throws<System.ArgumentException>(() => CoopDisasterEventCodec.Encode(
                new CoopDisasterEventData(
                    CoopDisasterEventKind.Sandstorm, true, 1f, 0f, 1f)));
            Assert.Throws<System.ArgumentException>(() => CoopDisasterEventCodec.Encode(
                new CoopDisasterEventData(
                    CoopDisasterEventKind.Meteor, true, float.NaN, 0f, 1f)));

            byte[] payload = CoopDisasterEventCodec.Encode(
                new CoopDisasterEventData(
                    CoopDisasterEventKind.Meteor, true, 1f, 2f, 3f));
            payload[0] = 255;
            Assert.False(CoopDisasterEventCodec.TryDecode(payload, out _));

            payload = CoopDisasterEventCodec.Encode(
                new CoopDisasterEventData(
                    CoopDisasterEventKind.Thunder, false, 0f, 0f, 0f));
            System.Array.Resize(ref payload, payload.Length + 1);
            Assert.False(CoopDisasterEventCodec.TryDecode(payload, out _));
        }

        [Theory]
        [InlineData(CoopDisasterEventKind.MeteorConstructionImpact, "Module")]
        [InlineData(CoopDisasterEventKind.LightningConstructionImpact, "Connection")]
        [InlineData(CoopDisasterEventKind.LightningRecharge, "Module")]
        public void ObjectEffectRoundTripsStableTarget(
            CoopDisasterEventKind kind,
            string targetType)
        {
            var expected = new CoopDisasterEventData(
                kind,
                false,
                0f,
                0f,
                0f,
                new CoopObjectReference(91, targetType, 12f, 0f, 18f, true));

            byte[] payload = CoopDisasterEventCodec.Encode(expected);

            Assert.True(CoopDisasterEventCodec.TryDecode(
                payload, out CoopDisasterEventData actual));
            Assert.Equal(91, actual.TargetReference.ObjectId);
            Assert.Equal(targetType, actual.TargetReference.TypeName);
        }

        [Theory]
        [InlineData(CoopDisasterEventKind.MeteorCharacterImpact)]
        [InlineData(CoopDisasterEventKind.LightningCharacterImpact)]
        public void CharacterImpactRequiresAuthoritativePosition(
            CoopDisasterEventKind kind)
        {
            Assert.Throws<System.ArgumentException>(() => CoopDisasterEventCodec.Encode(
                new CoopDisasterEventData(kind, false, 0f, 0f, 0f)));

            var data = new CoopDisasterEventData(kind, true, 10f, 0f, 20f);
            byte[] payload = CoopDisasterEventCodec.Encode(data);
            Assert.True(CoopDisasterEventCodec.TryDecode(payload, out _));
        }

        [Fact]
        public void DisasterEffectRejectsWrongReferenceCombination()
        {
            Assert.Throws<System.ArgumentException>(() => CoopDisasterEventCodec.Encode(
                new CoopDisasterEventData(
                    CoopDisasterEventKind.LightningRecharge,
                    false,
                    0f,
                    0f,
                    0f,
                    new CoopObjectReference(91, "Connection", 12f, 0f, 18f, true))));
            Assert.Throws<System.ArgumentException>(() => CoopDisasterEventCodec.Encode(
                new CoopDisasterEventData(
                    CoopDisasterEventKind.Sandstorm,
                    false,
                    0f,
                    0f,
                    0f,
                    new CoopObjectReference(91, "Module", 12f, 0f, 18f, true))));
        }
    }
}
