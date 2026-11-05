using System.Text;
using System.Xml;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class ShipManagerClockTests
    {
        [Fact]
        public void TryRead_AcceptsFiniteVanillaTickRemainder()
        {
            XmlDocument document = Xml("0.6372");

            Assert.True(CoopShipManagerClock.TryRead(document.DocumentElement, out float value));
            Assert.Equal(0.6372f, value, 4);
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-0.1")]
        [InlineData("1.2")]
        public void TryRead_RejectsUnsafeTickRemainder(string value)
        {
            Assert.False(CoopShipManagerClock.TryRead(
                Xml(value).DocumentElement, out _));
        }

        [Fact]
        public void Write_UpsertsSingleAuthoritativeClockNode()
        {
            XmlDocument document = Xml("0.5");

            CoopShipManagerClock.Write(document.DocumentElement, 1.0f);
            CoopShipManagerClock.Write(document.DocumentElement, 0.25f);

            Assert.Single(document.SelectNodes("/save-game/ship-manager/coop-tick-time"));
            Assert.True(CoopShipManagerClock.TryRead(document.DocumentElement, out float value));
            Assert.Equal(0.25f, value, 4);
        }

        private static XmlDocument Xml(string value)
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<save-game><ship-manager><coop-tick-time value=\"" +
                value + "\" /></ship-manager></save-game>");
            return document;
        }
    }
}
