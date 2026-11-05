using System.Collections.Generic;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopObjectReferenceTests
    {
        [Fact]
        public void DifferentLocalIdFallsBackToUniqueTypeAndPosition()
        {
            var reference = new CoopObjectReference(169, "BioDome", 910.25f, 0.2f, 948.5f, true);
            var candidates = new List<CoopObjectCandidate>
            {
                new CoopObjectCandidate(170, "BioDome", 910.26f, 0.2f, 948.48f),
                new CoopObjectCandidate(171, "ProcessingPlant", 910.25f, 0.2f, 948.5f)
            };

            Assert.True(CoopObjectResolver.TryResolve(reference, candidates, out CoopObjectCandidate match, out CoopObjectResolution resolution));
            Assert.Equal(170, match.ObjectId);
            Assert.Equal(CoopObjectResolution.UniqueSpatialFallback, resolution);
        }

        [Fact]
        public void ExactIdMustAlsoMatchTypeAndPositionSignature()
        {
            var reference = new CoopObjectReference(169, "BioDome", 10f, 0f, 10f, true);
            var candidates = new List<CoopObjectCandidate>
            {
                new CoopObjectCandidate(169, "Mine", 50f, 0f, 50f),
                new CoopObjectCandidate(170, "BioDome", 10.05f, 0f, 10.05f)
            };

            Assert.True(CoopObjectResolver.TryResolve(reference, candidates, out CoopObjectCandidate match, out CoopObjectResolution resolution));
            Assert.Equal(170, match.ObjectId);
            Assert.Equal(CoopObjectResolution.UniqueSpatialFallback, resolution);
        }

        [Fact]
        public void AmbiguousSpatialFallbackFailsClosed()
        {
            var reference = new CoopObjectReference(169, "BioDome", 10f, 0f, 10f, true);
            var candidates = new List<CoopObjectCandidate>
            {
                new CoopObjectCandidate(170, "BioDome", 10.05f, 0f, 10.05f),
                new CoopObjectCandidate(171, "BioDome", 9.95f, 0f, 9.95f)
            };

            Assert.False(CoopObjectResolver.TryResolve(reference, candidates, out CoopObjectCandidate match, out CoopObjectResolution resolution));
            Assert.Null(match);
            Assert.Equal(CoopObjectResolution.Ambiguous, resolution);
        }

        [Fact]
        public void MovingReferenceWithoutPositionNeverUsesSpatialFallback()
        {
            var reference = new CoopObjectReference(44, "Colonist", 0f, 0f, 0f, false);
            var candidates = new List<CoopObjectCandidate>
            {
                new CoopObjectCandidate(45, "Colonist", 0f, 0f, 0f)
            };

            Assert.False(CoopObjectResolver.TryResolve(reference, candidates, out _, out CoopObjectResolution resolution));
            Assert.Equal(CoopObjectResolution.Missing, resolution);
        }

        [Fact]
        public void InvalidReferenceIsRejectedBeforeCandidateScan()
        {
            var reference = new CoopObjectReference(-1, "../BioDome", float.NaN, 0f, 0f, true);

            Assert.False(CoopObjectResolver.TryResolve(reference, new List<CoopObjectCandidate>(), out _, out CoopObjectResolution resolution));
            Assert.Equal(CoopObjectResolution.Invalid, resolution);
        }

        [Fact]
        public void ConnectionPacketRoundTripsStableEndpointReferences()
        {
            var command = new CoopConnectionCommandData
            {
                SessionId = 42,
                RequestId = 12,
                FirstModule = new CoopObjectReference(169, "BioDome", 10f, 0f, 20f, true),
                SecondModule = new CoopObjectReference(172, "ProcessingPlant", 25f, 0f, 20f, true)
            };

            byte[] packet = CoopProtocol.CreateConnectionRequestPacket(command);

            Assert.True(CoopProtocol.TryParseConnectionRequestPacket(packet, out CoopConnectionCommandData parsed));
            Assert.Equal(169, parsed.FirstModule.ObjectId);
            Assert.Equal("BioDome", parsed.FirstModule.TypeName);
            Assert.Equal(25f, parsed.SecondModule.PositionX);
            Assert.True(parsed.SecondModule.HasPosition);
        }

        [Fact]
        public void TruncatedStableReferencePacketIsRejected()
        {
            var command = new CoopConnectionCommandData
            {
                SessionId = 42,
                RequestId = 12,
                FirstModule = new CoopObjectReference(169, "BioDome", 10f, 0f, 20f, true),
                SecondModule = new CoopObjectReference(172, "ProcessingPlant", 25f, 0f, 20f, true)
            };
            byte[] packet = CoopProtocol.CreateConnectionRequestPacket(command);
            var truncated = new byte[packet.Length - 1];
            System.Buffer.BlockCopy(packet, 0, truncated, 0, truncated.Length);

            Assert.False(CoopProtocol.TryParseConnectionRequestPacket(truncated, out _));
        }

        [Fact]
        public void ComponentPlacementPacketRoundTripsStableParentReference()
        {
            var command = new CoopPlacementCommandData
            {
                SessionId = 42,
                RequestId = 18,
                Kind = CoopPlacementKind.Component,
                ParentId = 169,
                ParentReference = new CoopObjectReference(169, "Module", 10f, 0f, 20f, true),
                TypeName = "PotatoPad",
                SizeIndex = 0,
                PositionX = 10.5f,
                PositionY = 0.1f,
                PositionZ = 20.5f,
                RotationY = 0f,
                Valid = true
            };

            byte[] packet = CoopProtocol.CreatePlacementRequestPacket(command);

            Assert.True(CoopProtocol.TryParsePlacementRequestPacket(packet, out CoopPlacementCommandData parsed));
            Assert.Equal(169, parsed.ParentReference.ObjectId);
            Assert.Equal("Module", parsed.ParentReference.TypeName);
            Assert.True(parsed.ParentReference.HasPosition);
        }
    }
}
