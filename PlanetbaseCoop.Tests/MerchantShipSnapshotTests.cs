using System.Text;
using System.Xml;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class MerchantShipSnapshotTests
    {
        [Fact]
        public void Validate_AcceptsTradingMerchantWithAllProductCollections()
        {
            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(Xml(HostTradingMerchant));

            Assert.True(result.IsValid, result.Message);
            Assert.Equal(1, result.MerchantShipCount);
            Assert.Equal("valid", result.Code);
        }

        [Theory]
        [InlineData("needed-products")]
        [InlineData("contained-products")]
        [InlineData("provided-products")]
        public void Validate_RejectsTradingMerchantWhenRequiredCollectionIsMissing(string collectionName)
        {
            var document = new XmlDocument();
            document.LoadXml(HostTradingMerchant);
            XmlNode collection = document.SelectSingleNode("//" + collectionName);
            collection.ParentNode.RemoveChild(collection);

            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(Xml(document.OuterXml));

            Assert.False(result.IsValid);
            Assert.Equal("missing-trading-collection", result.Code);
            Assert.Equal(828, result.MerchantShipId);
            Assert.Equal(collectionName, result.MissingNodeName);
            Assert.Contains("828", result.Message);
            Assert.Contains(collectionName, result.Message);
            Assert.EndsWith("/" + collectionName + "[1]", result.Path);
        }

        [Fact]
        public void Validate_ReportsUncommittedTradingState_WhenAllTradeCollectionsAreAbsent()
        {
            const string snapshot =
                "<world><ships><ship type=\"MerchantShip\"><id value=\"172\" />" +
                "<trade-state value=\"1\" /></ship></ships></world>";

            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("uncommitted-trading-state", result.Code);
            Assert.Equal(172, result.MerchantShipId);
        }

        [Fact]
        public void Validate_AllowsNonTradingMerchantWithoutTradeCollections()
        {
            const string snapshot =
                "<world><ships><ship type=\"MerchantShip\"><id value=\"12\" />" +
                "<trade-state value=\"0\" /></ship></ships></world>";

            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(Xml(snapshot));

            Assert.True(result.IsValid, result.Message);
            Assert.Equal(1, result.MerchantShipCount);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("3")]
        [InlineData("999")]
        public void Validate_RejectsOutOfRangeTradeState(string tradeState)
        {
            string snapshot =
                "<world><ships><ship type=\"MerchantShip\"><id value=\"12\" />" +
                "<trade-state value=\"" + tradeState + "\" /></ship></ships></world>";

            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-trade-state", result.Code);
            Assert.Equal("/world[1]/ships[1]/ship[1]/trade-state[1]/@value", result.Path);
        }

        [Fact]
        public void Validate_XmlDocumentOverloadReturnsSameResult()
        {
            var document = new XmlDocument();
            document.LoadXml(HostTradingMerchant);

            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(document);

            Assert.True(result.IsValid, result.Message);
            Assert.Equal(1, result.MerchantShipCount);
        }

        [Fact]
        public void Validate_ReturnsDescriptiveResultForMalformedXml()
        {
            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(Xml("<world>"));

            Assert.False(result.IsValid);
            Assert.Equal("malformed-xml", result.Code);
            Assert.Contains("XML", result.Message);
        }

        [Fact]
        public void Validate_RejectsDtdBeforeAnyExternalEntityCanBeResolved()
        {
            const string payload =
                "<!DOCTYPE world [<!ENTITY secret SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                "<world><ships>&secret;</ships></world>";

            MerchantShipSnapshotValidationResult result =
                MerchantShipSnapshotInvariant.Validate(Xml(payload));

            Assert.False(result.IsValid);
            Assert.Equal("malformed-xml", result.Code);
        }

        private const string HostTradingMerchant =
            "<world><ships><ship type=\"MerchantShip\">" +
            "<id value=\"828\" /><trade-state value=\"1\" />" +
            "<products><name value=\"\" /></products>" +
            "<needed-products><name value=\"Pending resources\" /><product-amount>" +
            "<product type=\"ProductResource\"><resource-type value=\"Bioplastic\" /></product>" +
            "<amount value=\"3\" /></product-amount></needed-products>" +
            "<contained-products><name value=\"\" /><product-amount>" +
            "<product type=\"ProductResource\"><resource-type value=\"Bioplastic\" /></product>" +
            "<amount value=\"2\" /></product-amount></contained-products>" +
            "<provided-products><name value=\"\" /><product-amount>" +
            "<product type=\"ProductResource\"><resource-type value=\"Coins\" /></product>" +
            "<amount value=\"34\" /></product-amount></provided-products>" +
            "</ship></ships></world>";

        private static byte[] Xml(string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }
    }
}
