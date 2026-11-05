using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopCheckpointScheduleTests
    {
        [Theory]
        [InlineData(0, 128, 128)]
        [InlineData(1, 128, 128)]
        [InlineData(127, 128, 128)]
        [InlineData(128, 128, 256)]
        [InlineData(255, 128, 256)]
        public void NextCheckpointUsesSharedAbsoluteBoundaries(long currentTick, long interval, long expected)
        {
            Assert.Equal(expected, CoopCheckpointSchedule.NextBoundary(currentTick, interval));
        }

        [Theory]
        [InlineData(128, 128, true)]
        [InlineData(256, 128, true)]
        [InlineData(255, 128, false)]
        [InlineData(0, 128, false)]
        public void CheckpointOnlyRunsOnPositiveCommonBoundary(long tick, long interval, bool expected)
        {
            Assert.Equal(expected, CoopCheckpointSchedule.IsBoundary(tick, interval));
        }

        [Theory]
        [InlineData(0, 512)]
        [InlineData(1, 1024)]
        [InlineData(2, 2048)]
        public void DefaultCheckpointCadenceAvoidsFrequentMainThreadSerialization(
            int scaleIndex,
            long expectedInterval)
        {
            Assert.Equal(expectedInterval,
                CoopCheckpointSchedule.IntervalTicksForScale(
                    CoopCheckpointSchedule.DefaultBaseIntervalTicks, scaleIndex));
        }

        [Theory]
        [InlineData(10, 512, 512, 128, 128)]
        [InlineData(10, 128, 128, 512, 512)]
        [InlineData(10, 512, 512, 512, 512)]
        [InlineData(512, 512, 512, 128, 512)]
        [InlineData(513, 512, 512, 128, 512)]
        public void CheckpointDeadlineRebasesWhenSpeedIntervalChanges(
            long currentTick,
            long currentDeadline,
            long scheduledInterval,
            long currentInterval,
            long expectedDeadline)
        {
            Assert.Equal(expectedDeadline,
                CoopCheckpointSchedule.RebaseDeadline(
                    currentTick,
                    currentDeadline,
                    scheduledInterval,
                    currentInterval));
        }

        [Fact]
        public void InvalidCheckpointArgumentsFailFast()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => CoopCheckpointSchedule.NextBoundary(-1, 128));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => CoopCheckpointSchedule.NextBoundary(1, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CoopCheckpointSchedule.IntervalTicksForScale(128, -1));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CoopCheckpointSchedule.IntervalTicksForScale(128, 3));
        }
    }
}
