using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class ShipAuthorityTests
    {
        [Theory]
        [InlineData(false, "", true)]
        [InlineData(true, "host", true)]
        [InlineData(true, "guest", false)]
        public void LocalShipControls_AreWritableOnlyByAuthoritativePeer(
            bool fullMode,
            string role,
            bool expected)
        {
            Assert.Equal(expected,
                CoopShipAuthorityPolicy.AllowLocalShipControl(fullMode, role));
        }

        [Theory]
        [InlineData(false, "", true)]
        [InlineData(true, "host", true)]
        [InlineData(true, "guest", false)]
        public void AutomaticShipSpawn_RunsOnlyOnAuthoritativePeer(
            bool fullMode,
            string role,
            bool expected)
        {
            Assert.Equal(expected,
                CoopShipAuthorityPolicy.AllowAutomaticSpawn(fullMode, role));
        }

        [Theory]
        [InlineData(false, "", true)]
        [InlineData(true, "host", true)]
        [InlineData(true, "guest", true)]
        public void MerchantTrading_IsAvailableWhenTradeCommandsAreSynchronized(
            bool fullMode,
            string role,
            bool expected)
        {
            Assert.Equal(expected,
                CoopShipAuthorityPolicy.AllowMerchantTrading(fullMode, role));
        }

        [Theory]
        [InlineData(true, 77L, 77L, true)]
        [InlineData(false, 77L, 77L, false)]
        [InlineData(true, 77L, 78L, false)]
        [InlineData(true, 77L, 0L, false)]
        public void AuthoritativeCommands_RequireTheActiveRunningSession(
            bool running,
            long activeSessionId,
            long commandSessionId,
            bool expected)
        {
            Assert.Equal(expected, CoopCommandSessionPolicy.CanAccept(
                running, activeSessionId, commandSessionId));
        }

        [Theory]
        [InlineData(0, true, false, true, true)]
        [InlineData(0, false, true, true, true)]
        [InlineData(1, false, true, true, true)]
        [InlineData(1, true, false, true, false)]
        [InlineData(0, true, false, false, false)]
        [InlineData(0, true, false, true, false, false)]
        public void LandingTarget_MustBeOperationalFreeAndLargeCapableWhenNeeded(
            int sizeIndex,
            bool landingPad,
            bool starport,
            bool operational,
            bool expected,
            bool free = true)
        {
            Assert.Equal(expected, CoopLandingTargetPolicy.IsValid(
                sizeIndex, landingPad, starport, operational, free));
        }
    }
}
