using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopTradeLifecyclePolicyTests
    {
        [Fact]
        public void AgreementCanFollowAStartThatIsAlreadyScheduled()
        {
            Assert.True(CoopTradeLifecyclePolicy.CanAccept(
                CoopTradeAction.Agree,
                isTrading: false,
                hasPendingStart: true,
                hasPendingTerminal: false,
                hasPendingAgreement: false));
        }

        [Fact]
        public void DuplicateStartAndAgreementAreRejected()
        {
            Assert.False(CoopTradeLifecyclePolicy.CanAccept(
                CoopTradeAction.Start, false, true, false, false));
            Assert.False(CoopTradeLifecyclePolicy.CanAccept(
                CoopTradeAction.Agree, true, false, false, true));
        }

        [Theory]
        [InlineData(CoopTradeAction.Cancel)]
        [InlineData(CoopTradeAction.Dismiss)]
        public void TerminalActionCanClosePendingStartButNotIdleTrade(
            CoopTradeAction action)
        {
            Assert.True(CoopTradeLifecyclePolicy.CanAccept(
                action, false, true, false, false));
            Assert.False(CoopTradeLifecyclePolicy.CanAccept(
                action, false, false, false, false));
        }

        [Fact]
        public void PendingTerminalBlocksFurtherTradeMutations()
        {
            Assert.False(CoopTradeLifecyclePolicy.CanAccept(
                CoopTradeAction.Agree, true, false, true, false));
            Assert.False(CoopTradeLifecyclePolicy.CanAccept(
                CoopTradeAction.Cancel, true, false, true, false));
            Assert.True(CoopTradeLifecyclePolicy.CanAccept(
                CoopTradeAction.Dismiss, true, false, true, false));
        }

        [Theory]
        [InlineData(100, 8, 0, 108)]
        [InlineData(100, 8, 108, 109)]
        [InlineData(100, 8, 120, 121)]
        public void DependentCommitRunsAfterPendingLifecycle(
            long currentTick,
            long leadTicks,
            long prerequisiteTick,
            long expected)
        {
            Assert.Equal(expected, CoopTradeLifecyclePolicy.GetApplyTick(
                currentTick, leadTicks, prerequisiteTick));
        }
    }
}
