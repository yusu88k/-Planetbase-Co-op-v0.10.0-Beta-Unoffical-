using System.Text;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class SnapshotAdmissionTests
    {
        [Fact]
        public void Validate_AcceptsMinimalPlanetbaseSaveEnvelope()
        {
            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(ValidSnapshot()));

            Assert.True(result.IsValid, result.Message);
            Assert.Equal("valid", result.Code);
        }

        [Fact]
        public void Validate_RejectsWellFormedNonSaveXml()
        {
            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml("<world />"));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-root", result.Code);
        }

        [Fact]
        public void Validate_RejectsMissingRequiredTopLevelSection()
        {
            string snapshot = ValidSnapshot().Replace("<interactions />", string.Empty);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-section", result.Code);
            Assert.Equal("/save-game[1]/interactions[1]", result.Path);
        }

        [Fact]
        public void Validate_RejectsMissingOrDuplicateShipManager()
        {
            string valid = ValidSnapshot();
            string section = ValidShipManager();

            CoopSnapshotValidationResult missing = CoopSnapshotInvariant.Validate(
                Xml(valid.Replace(section, string.Empty)));
            CoopSnapshotValidationResult duplicate = CoopSnapshotInvariant.Validate(
                Xml(valid.Replace(section, section + section)));

            Assert.False(missing.IsValid);
            Assert.Equal("invalid-section", missing.Code);
            Assert.False(duplicate.IsValid);
            Assert.Equal("invalid-section", duplicate.Code);
        }

        [Theory]
        [InlineData("<coop-tick-time value=\"0.5\" />", "")]
        [InlineData("<coop-tick-time value=\"0.5\" />", "<coop-tick-time value=\"0.5\" /><coop-tick-time value=\"0.4\" />")]
        [InlineData("<coop-tick-time value=\"0.5\" />", "<coop-tick-time value=\"NaN\" />")]
        public void Validate_RejectsMissingDuplicateOrInvalidShipManagerClock(
            string clock,
            string replacement)
        {
            string snapshot = ValidSnapshot().Replace(clock, replacement);

            CoopSnapshotValidationResult result = CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-scalar", result.Code);
            Assert.Contains("coop-tick-time", result.Path);
        }

        [Theory]
        [InlineData("<time-to-next-intruder value=\"4200\" />", "")]
        [InlineData("<landing-permissions>", "<landing-permissions><landing-permissions>")]
        [InlineData("<merchants-allowed value=\"true\" />", "<merchants-allowed value=\"maybe\" />")]
        public void Validate_RejectsIncompleteShipManagerState(string value, string replacement)
        {
            string snapshot = ValidSnapshot().Replace(value, replacement);

            CoopSnapshotValidationResult result = CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
        }

        [Fact]
        public void Validate_RejectsUnsupportedSaveVersion()
        {
            string snapshot = ValidSnapshot().Replace("version=\"12\"", "version=\"11\"");

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-version", result.Code);
        }

        [Fact]
        public void Validate_RejectsEmptyLoaderCriticalSection()
        {
            string snapshot = ValidSnapshot().Replace(
                "<planet><planet-index value=\"0\" /></planet>", "<planet />");

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-scalar", result.Code);
            Assert.Contains("planet-index", result.Path);
        }

        [Fact]
        public void Validate_RejectsUnsupportedSerializedEntityType()
        {
            string fakeShip = "<ships><ship type=\"FakeShip\"><id value=\"8\" />" +
                "<position x=\"0\" y=\"0\" z=\"0\" /><orientation x=\"0\" y=\"0\" z=\"0\" />" +
                "<state value=\"0\" /></ship></ships>";
            string snapshot = ValidSnapshot().Replace("<ships />", fakeShip);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-entity-type", result.Code);
        }

        [Fact]
        public void Validate_RejectsNegativeEntityIdBeforeItCanEnterTheWorld()
        {
            string construction =
                "<constructions><construction type=\"Module\">" +
                "<id value=\"-1\" />" +
                "<position x=\"0\" y=\"0\" z=\"0\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"0\" />" +
                "<state value=\"1\" /><build-progress value=\"-1\" />" +
                "<condition value=\"1\" /><oxygen value=\"0\" />" +
                "<enabled value=\"True\" /><time-built value=\"0\" />" +
                "<locked value=\"False\" /><high-priority value=\"False\" />" +
                "</construction></constructions>";
            string snapshot = ValidSnapshot().Replace("<constructions />", construction);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-entity-id", result.Code);
            Assert.Contains("/id[1]/@value", result.Path);
            Assert.Equal(-1, result.EntityId);
        }

        [Fact]
        public void Validate_RejectsNegativeNestedComponentId()
        {
            string component =
                "<components><component type=\"ConstructionComponent\">" +
                "<enabled value=\"True\" /><state value=\"1\" />" +
                "<build-progress value=\"0\" /><id value=\"-1\" />" +
                "<component-type value=\"Bunk\" />" +
                "<position x=\"0\" y=\"0\" z=\"0\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"0\" />" +
                "<condition value=\"1\" /><production-progress value=\"0\" />" +
                "<time value=\"0\" /><produced-item-index value=\"0\" />" +
                "</component></components>";
            string constructions = "<constructions>" +
                Construction("Module", 1, component +
                    "<module-type value=\"ModuleTypeDorm\" /><size-index value=\"0\" />") +
                "</constructions>";
            string snapshot = ValidSnapshot()
                .Replace("<next-id value=\"1\" />", "<next-id value=\"2\" />")
                .Replace("<constructions />", constructions);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-entity-id", result.Code);
            Assert.Contains("/components[1]/component[1]/id[1]/@value", result.Path);
        }

        [Fact]
        public void Validate_RejectsNegativeConnectionLinkId()
        {
            string constructions = "<constructions>" +
                Construction("Module", 1,
                    "<module-type value=\"ModuleTypeAirlock\" /><size-index value=\"0\" />") +
                Construction("Module", 2,
                    "<module-type value=\"ModuleTypeDorm\" /><size-index value=\"0\" />") +
                Construction("Connection", 3,
                    "<links><id value=\"1\" /><id value=\"-1\" /></links>") +
                "</constructions>";
            string snapshot = ValidSnapshot()
                .Replace("<next-id value=\"1\" />", "<next-id value=\"4\" />")
                .Replace("<constructions />", constructions);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-reference", result.Code);
            Assert.Contains("/links[1]/id[2]/@value", result.Path);
        }

        [Fact]
        public void Validate_RejectsDuplicateSelectableIds()
        {
            string constructions = "<constructions>" +
                Construction("Module", 1,
                    "<module-type value=\"ModuleTypeAirlock\" /><size-index value=\"0\" />") +
                "</constructions>";
            string ship = "<ships><ship type=\"ColonyShip\"><id value=\"1\" />" +
                "<position x=\"0\" y=\"0\" z=\"0\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"0\" /><state value=\"0\" />" +
                "</ship></ships>";
            string snapshot = ValidSnapshot()
                .Replace("<next-id value=\"1\" />", "<next-id value=\"2\" />")
                .Replace("<constructions />", constructions)
                .Replace("<ships />", ship);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("duplicate-entity-id", result.Code);
            Assert.Equal(1, result.EntityId);
        }

        [Fact]
        public void Validate_RejectsAttributePrefixThatWouldChangeVanillaTypeDispatch()
        {
            string maliciousShip = "<ships><ship evil=\"FakeShip\" type=\"MerchantShip\">" +
                "<id value=\"8\" /><position x=\"0\" y=\"0\" z=\"0\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"0\" /><state value=\"0\" />" +
                "<trade-state value=\"0\" /><name value=\"Trader\" />" +
                "<merchant-category value=\"0\" /><commission value=\"0\" />" +
                "</ship></ships>";
            string snapshot = ValidSnapshot().Replace("<ships />", maliciousShip);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-attribute-shape", result.Code);
        }

        [Fact]
        public void Validate_RejectsNonFiniteVectorComponents()
        {
            string fakeShip = "<ships><ship type=\"ColonyShip\"><id value=\"8\" />" +
                "<position x=\"NaN\" y=\"0\" z=\"0\" /><orientation x=\"0\" y=\"0\" z=\"0\" />" +
                "<state value=\"0\" /></ship></ships>";
            string snapshot = ValidSnapshot().Replace("<ships />", fakeShip);

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(snapshot));

            Assert.False(result.IsValid);
            Assert.Equal("invalid-attribute-shape", result.Code);
        }

        [Fact]
        public void RuntimeCheckpoint_DefersUncommittedTradingStateRegardlessOfRole()
        {
            CoopSnapshotValidationResult validation = CoopSnapshotInvariant.Validate(
                Xml(TradingSnapshotWithoutCollections()));

            Assert.False(validation.IsValid);
            Assert.Equal("uncommitted-trading-state", validation.Code);
            Assert.True(CoopSnapshotAdmissionPolicy.IsDeferrableRuntimeTradingState(
                validation.Code));
        }

        [Theory]
        [InlineData(true, "uncommitted-trading-state", true)]
        [InlineData(false, "uncommitted-trading-state", false)]
        [InlineData(true, "missing-trading-collection", false)]
        public void LoadableSnapshot_DefersOnlyHostUncommittedTradingState(
            bool isHost,
            string validationCode,
            bool expected)
        {
            Assert.Equal(expected,
                CoopSnapshotAdmissionPolicy.IsDeferrableHostTradingState(
                    isHost, validationCode));
        }

        [Fact]
        public void StrictSnapshotValidation_RejectsTradingMerchantWithoutCollections()
        {
            CoopSnapshotValidationResult result = CoopSnapshotInvariant.Validate(
                Xml(TradingSnapshotWithoutCollections()));

            Assert.False(result.IsValid);
            Assert.Equal("uncommitted-trading-state", result.Code);
            Assert.Equal(172, result.EntityId);
            Assert.Equal(
                "/save-game[1]/ships[1]/ship[1]/needed-products[1]",
                result.Path);
        }

        [Theory]
        [InlineData("invalid-root")]
        [InlineData("invalid-trade-state")]
        [InlineData("malformed-xml")]
        [InlineData("missing-trading-collection")]
        public void RuntimeCheckpoint_DoesNotDeferOtherInvariantErrors(
            string validationCode)
        {
            Assert.False(CoopSnapshotAdmissionPolicy.IsDeferrableRuntimeTradingState(
                validationCode));
        }

        [Fact]
        public void Validate_RejectsSnapshotThatCannotFitInWorldPacket()
        {
            byte[] oversized = new byte[CoopSnapshotInvariant.MaxSnapshotBytes + 1];

            CoopSnapshotValidationResult result = CoopSnapshotInvariant.Validate(oversized);

            Assert.False(result.IsValid);
            Assert.Equal("snapshot-too-large", result.Code);
        }

        [Fact]
        public void Validate_RejectsExcessivelyDeepXml()
        {
            var xml = new StringBuilder("<save-game version=\"12\">");
            for (int i = 0; i < CoopDiagnostics.MaxXmlDepth + 1; i++) xml.Append("<n>");
            for (int i = 0; i < CoopDiagnostics.MaxXmlDepth + 1; i++) xml.Append("</n>");
            xml.Append("</save-game>");

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(xml.ToString()));

            Assert.False(result.IsValid);
            Assert.Equal("malformed-xml", result.Code);
            Assert.Contains("depth", result.Message);
        }

        [Fact]
        public void Validate_RejectsExcessivelyWideXmlBeforeBuildingDom()
        {
            var xml = new StringBuilder("<save-game version=\"12\"><ships>");
            for (int i = 0; i < CoopDiagnostics.MaxXmlElements; i++) xml.Append("<n />");
            xml.Append("</ships></save-game>");

            CoopSnapshotValidationResult result =
                CoopSnapshotInvariant.Validate(Xml(xml.ToString()));

            Assert.False(result.IsValid);
            Assert.Equal("malformed-xml", result.Code);
            Assert.Contains("element", result.Message);
        }

        private static string ValidSnapshot()
        {
            return "<save-game version=\"12\">" +
                "<id-generator><next-id value=\"1\" /><next-bot-id value=\"0\" /></id-generator>" +
                "<planet><planet-index value=\"0\" /></planet>" +
                "<terrain><seed value=\"123\" /></terrain>" +
                "<constructions /><characters />" +
                "<resources><inmaterial-resources /></resources>" +
                ValidShipManager() +
                "<ships /><interactions />" +
                "</save-game>";
        }

        private static string ValidShipManager()
        {
            return "<ship-manager>" +
                "<time-since-last-colonist-landing value=\"0\" />" +
                "<time-since-last-visitor-landing value=\"0\" />" +
                "<time-since-last-merchant-landing value=\"0\" />" +
                "<time-to-next-intruder value=\"4200\" />" +
                "<landing-permissions>" +
                "<colonists-allowed value=\"true\" />" +
                "<merchants-allowed value=\"true\" />" +
                "<visitors-allowed value=\"true\" />" +
                "</landing-permissions>" +
                "<coop-tick-time value=\"0.5\" />" +
                "</ship-manager>";
        }

        private static string TradingSnapshotWithoutCollections()
        {
            const string merchant =
                "<ships><ship type=\"MerchantShip\">" +
                "<id value=\"172\" />" +
                "<position x=\"0\" y=\"0\" z=\"0\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"0\" />" +
                "<state value=\"2\" />" +
                "<trade-state value=\"1\" />" +
                "<name value=\"Trader\" />" +
                "<merchant-category value=\"0\" />" +
                "<commission value=\"0\" />" +
                "</ship></ships>";
            return ValidSnapshot().Replace("<ships />", merchant);
        }

        private static string Construction(string type, int id, string extra)
        {
            return "<construction type=\"" + type + "\">" +
                "<enabled value=\"True\" /><state value=\"1\" />" +
                "<build-progress value=\"0\" /><condition value=\"1\" />" +
                "<oxygen value=\"0\" /><id value=\"" + id + "\" />" +
                "<position x=\"0\" y=\"0\" z=\"0\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"0\" />" +
                "<time-built value=\"0\" /><locked value=\"False\" />" +
                "<high-priority value=\"False\" />" + extra + "</construction>";
        }

        private static byte[] Xml(string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }
    }
}
