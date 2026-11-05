using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopSequenceWatermarksTests
    {
        [Fact]
        public void AcceptsIncreasingHostAndGuestSequencesIndependently()
        {
            var watermarks = new CoopSequenceWatermarks();

            Assert.True(watermarks.TryAccept(CoopPlacementRequestIds.For(CoopRole.Host, 1)));
            Assert.True(watermarks.TryAccept(CoopPlacementRequestIds.For(CoopRole.Guest, 1)));
            Assert.True(watermarks.TryAccept(CoopPlacementRequestIds.For(CoopRole.Host, 2)));
            Assert.True(watermarks.TryAccept(CoopPlacementRequestIds.For(CoopRole.Guest, 2)));
        }

        [Fact]
        public void RejectsDuplicateAndOlderSequencesWithoutGrowingState()
        {
            var watermarks = new CoopSequenceWatermarks();
            long host = CoopPlacementRequestIds.For(CoopRole.Host, 8);
            long guest = CoopPlacementRequestIds.For(CoopRole.Guest, 12);

            Assert.True(watermarks.TryAccept(host));
            Assert.True(watermarks.TryAccept(guest));
            Assert.False(watermarks.TryAccept(host));
            Assert.False(watermarks.TryAccept(CoopPlacementRequestIds.For(CoopRole.Host, 7)));
            Assert.False(watermarks.TryAccept(guest));
            Assert.False(watermarks.TryAccept(CoopPlacementRequestIds.For(CoopRole.Guest, 11)));
            Assert.Equal(host, watermarks.LastHostSequence);
            Assert.Equal(guest, watermarks.LastGuestSequence);
        }

        [Fact]
        public void ResetAllowsFreshSessionSequences()
        {
            var watermarks = new CoopSequenceWatermarks();
            long host = CoopPlacementRequestIds.For(CoopRole.Host, 3);
            Assert.True(watermarks.TryAccept(host));

            watermarks.Reset();

            Assert.True(watermarks.TryAccept(CoopPlacementRequestIds.For(CoopRole.Host, 1)));
            Assert.Equal(0, watermarks.LastGuestSequence);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void RejectsInvalidSequence(long sequence)
        {
            Assert.False(new CoopSequenceWatermarks().TryAccept(sequence));
        }

        [Fact]
        public void RejectsForwardJumpWithoutPoisoningWatermark()
        {
            var watermarks = new CoopSequenceWatermarks();
            long first = CoopPlacementRequestIds.For(CoopRole.Guest, 1);
            Assert.True(watermarks.TryAccept(first));
            Assert.False(watermarks.TryAccept(
                first + CoopSequenceWatermarks.MaxForwardDistance + 2));
            Assert.Equal(first, watermarks.LastGuestSequence);
            Assert.True(watermarks.TryAccept(
                CoopPlacementRequestIds.For(CoopRole.Guest, 2)));
        }

        [Fact]
        public void NetworkRequestRoleMustMatchSequenceNamespace()
        {
            Assert.True(CoopSequencePolicy.IsFromRole(3, CoopRole.Guest));
            Assert.True(CoopSequencePolicy.IsFromRole(2, CoopRole.Host));
            Assert.False(CoopSequencePolicy.IsFromRole(2, CoopRole.Guest));
            Assert.False(CoopSequencePolicy.IsFromRole(3, CoopRole.Host));
            Assert.False(CoopSequencePolicy.IsFromRole(0, CoopRole.Host));
        }
    }
}
