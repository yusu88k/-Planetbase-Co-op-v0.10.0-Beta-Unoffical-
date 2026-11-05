using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopDueTickPolicyTests
    {
        [Theory]
        [InlineData(101, 100, CoopDueTickState.Future)]
        [InlineData(100, 100, CoopDueTickState.Due)]
        [InlineData(99, 100, CoopDueTickState.Late)]
        [InlineData(0, 100, CoopDueTickState.Invalid)]
        public void ApplyTickIsClassifiedWithoutLateApply(
            long applyTick,
            long currentTick,
            CoopDueTickState expected)
        {
            Assert.Equal(expected, CoopDueTickPolicy.Classify(applyTick, currentTick));
        }
    }
}
