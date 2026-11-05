using System.Text;
using System.Xml;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class DeterminismTests
    {
        [Fact]
        public void CanonicalDigest_IgnoresFormattingAndAttributeOrder()
        {
            byte[] left = Xml("<world><resource type=\"Food\" amount=\"3\" /></world>");
            byte[] right = Xml("<?xml version=\"1.0\"?><world>\n  <resource amount=\"3\" type=\"Food\"></resource>\n</world>");

            string leftDigest = CoopDiagnostics.ComputeCanonicalXmlDigest(left);
            string rightDigest = CoopDiagnostics.ComputeCanonicalXmlDigest(right);

            Assert.Equal(64, leftDigest.Length);
            Assert.Equal(leftDigest, rightDigest);
        }

        [Fact]
        public void Compare_ReportsFirstDifferentAttributePathAndValues()
        {
            byte[] host = Xml("<world><resources><food value=\"16\" /></resources></world>");
            byte[] guest = Xml("<world><resources><food value=\"14\" /></resources></world>");

            CoopXmlDifference difference = CoopDiagnostics.FindFirstDifference(host, guest);

            Assert.False(difference.AreEqual);
            Assert.Equal("/world[1]/resources[1]/food[1]/@value", difference.Path);
            Assert.Equal("16", difference.LeftValue);
            Assert.Equal("14", difference.RightValue);
            Assert.Contains("attribute", difference.Description);
        }

        [Fact]
        public void Compare_ReportsMissingTradingCollectionByItsHostPath()
        {
            byte[] host = Xml("<world><ships><ship type=\"MerchantShip\"><needed-products /></ship></ships></world>");
            byte[] guest = Xml("<world><ships><ship type=\"MerchantShip\" /></ships></world>");

            CoopXmlDifference difference = CoopDiagnostics.FindFirstDifference(host, guest);

            Assert.False(difference.AreEqual);
            Assert.Equal("/world[1]/ships[1]/ship[1]/needed-products[1]", difference.Path);
            Assert.Contains("missing", difference.Description);
        }

        [Fact]
        public void CanonicalDigest_RejectsMalformedXml()
        {
            Assert.Throws<XmlException>(() =>
                CoopDiagnostics.ComputeCanonicalXmlDigest(Xml("<world>")));
        }

        [Fact]
        public void CanonicalDigest_RejectsDtdAndExternalEntityDeclarations()
        {
            const string payload =
                "<!DOCTYPE world [<!ENTITY secret SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                "<world>&secret;</world>";

            Assert.Throws<XmlException>(() =>
                CoopDiagnostics.ComputeCanonicalXmlDigest(Xml(payload)));
        }

        private static byte[] Xml(string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }
    }
}
