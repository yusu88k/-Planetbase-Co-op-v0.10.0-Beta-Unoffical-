using System;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class NetworkLatencyTests
    {
        [Fact]
        public void HeartbeatRequestAndReplyRoundTripTheirTimingIdentity()
        {
            const long sequence = 42;
            long sentUtcTicks = DateTime.UtcNow.Ticks;

            byte[] request = CoopProtocol.CreateHeartbeatPacket(
                reply: false, sequence: sequence, sentUtcTicks: sentUtcTicks);
            Assert.True(CoopProtocol.TryParseHeartbeatPacket(
                request, out bool requestReply, out long requestSequence,
                out long requestTimestamp));
            Assert.False(requestReply);
            Assert.Equal(sequence, requestSequence);
            Assert.Equal(sentUtcTicks, requestTimestamp);

            byte[] reply = CoopProtocol.CreateHeartbeatPacket(
                reply: true, sequence: requestSequence,
                sentUtcTicks: requestTimestamp);
            Assert.True(CoopProtocol.TryParseHeartbeatPacket(
                reply, out bool isReply, out long replySequence,
                out long replyTimestamp));
            Assert.True(isReply);
            Assert.Equal(sequence, replySequence);
            Assert.Equal(sentUtcTicks, replyTimestamp);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-1)]
        public void InvalidHeartbeatPacketsAreRejected(int? invalidLength)
        {
            byte[] packet = invalidLength.HasValue
                ? new byte[Math.Max(0, invalidLength.Value)]
                : null;
            Assert.False(CoopProtocol.TryParseHeartbeatPacket(
                packet, out _, out _, out _));
        }

        [Fact]
        public void LatencyEstimatorAbsorbsOneFrameSpikeWithoutStickyInflation()
        {
            var estimator = new CoopLatencyEstimator();
            Assert.True(estimator.RecordSample(40));
            Assert.Equal(40, estimator.SmoothedRttMilliseconds);

            Assert.True(estimator.RecordSample(420));
            Assert.InRange(estimator.SmoothedRttMilliseconds, 30, 80);
            Assert.InRange(estimator.BudgetMilliseconds, 200, 300);
            int spikeBudget = estimator.BudgetMilliseconds;

            for (int i = 0; i < 6; i++)
                Assert.True(estimator.RecordSample(45));

            Assert.InRange(estimator.SmoothedRttMilliseconds, 45, 80);
            Assert.True(estimator.BudgetMilliseconds < spikeBudget);
            Assert.True(estimator.BudgetMilliseconds < 250);
            Assert.True(estimator.BudgetMilliseconds >= estimator.SmoothedRttMilliseconds);

            Assert.False(estimator.RecordSample(0));
            Assert.False(estimator.RecordSample(6000));
        }

        [Fact]
        public void LatencyEstimatorStillReportsSustainedHighLatency()
        {
            var estimator = new CoopLatencyEstimator();
            Assert.True(estimator.RecordSample(40));
            for (int i = 0; i < 10; i++)
                Assert.True(estimator.RecordSample(420));

            Assert.True(estimator.SmoothedRttMilliseconds >= 390);
            Assert.True(estimator.BudgetMilliseconds >= estimator.SmoothedRttMilliseconds);
            Assert.True(estimator.BudgetMilliseconds <= 600);
        }

        [Fact]
        public void StartupQueueSpikeIsDiscardedByTheNextStableSample()
        {
            var estimator = new CoopLatencyEstimator();
            Assert.True(estimator.RecordSample(504));
            Assert.True(estimator.RecordSample(176));

            Assert.Equal(176, estimator.SmoothedRttMilliseconds);
            Assert.Equal(176, estimator.BudgetMilliseconds);
        }

        [Theory]
        [InlineData(0, 180, 7, 13)]
        [InlineData(1, 180, 11, 24)]
        [InlineData(2, 180, 20, 48)]
        [InlineData(2, 400, 42, 64)]
        [InlineData(2, 1000, 58, 64)]
        public void NetworkBudgetExpandsGateWithoutConsumingCommandSafetyMargin(
            int scaleIndex,
            int budgetMilliseconds,
            int expectedGateTicks,
            int expectedCommandTicks)
        {
            int gate = CoopTickLeadPolicy.MaxLeadTicks(
                scaleIndex, budgetMilliseconds);
            int command = CoopCommandLeadPolicy.ApplyLeadTicks(
                scaleIndex, budgetMilliseconds);

            Assert.Equal(expectedGateTicks, gate);
            Assert.Equal(expectedCommandTicks, command);
            Assert.True(command - (gate + 1) >= 5);
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(179, false, false)]
        [InlineData(180, true, false)]
        [InlineData(349, true, false)]
        [InlineData(350, true, true)]
        public void NetworkQualityUsesStableWarningThresholds(
            int pingMilliseconds,
            bool high,
            bool critical)
        {
            Assert.Equal(high,
                CoopNetworkQuality.IsHighLatency(pingMilliseconds));
            Assert.Equal(critical,
                CoopNetworkQuality.IsCriticalLatency(pingMilliseconds));
        }

        [Theory]
        [InlineData(110, 100, 10, false, true, false)]
        [InlineData(111, 100, 10, false, true, true)]
        [InlineData(109, 100, 10, true, true, true)]
        [InlineData(108, 100, 10, true, true, false)]
        [InlineData(111, 100, 10, false, false, false)]
        [InlineData(100, -1, 10, false, true, false)]
        public void TickGateUsesHysteresisAndWaitsForFreshPeerSample(
            long localTick,
            long peerTick,
            int maxLeadTicks,
            bool currentlyFrozen,
            bool peerSampleReady,
            bool expected)
        {
            Assert.Equal(expected, CoopTickGatePolicy.ShouldFreeze(
                localTick, peerTick, maxLeadTicks, currentlyFrozen, peerSampleReady));
        }
    }
}
