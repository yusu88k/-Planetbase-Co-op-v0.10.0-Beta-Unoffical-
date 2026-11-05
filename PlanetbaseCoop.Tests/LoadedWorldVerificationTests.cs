using System;
using System.IO;
using System.Text;
using System.Xml;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class LoadedWorldVerificationTests
    {
        [Fact]
        public void AssertRestoredState_RejectsKnownPlanetbaseLoaderMutationsBeforeRestore()
        {
            byte[] authoritative = Xml(
                nextId: 111,
                cameraX: "0",
                blizzardTimer: "2939.812",
                solarFlareTimer: "4199.771",
                resourceCount: 5);
            byte[] loaded = Xml(
                nextId: 112,
                cameraX: "25",
                blizzardTimer: "2419.39",
                solarFlareTimer: "4431.468",
                resourceCount: 5);

            Assert.Throws<InvalidDataException>(() =>
                CoopLoadedWorldVerification.AssertRestoredState(loaded, authoritative));
        }

        [Fact]
        public void AssertRestoredState_PassesAfterRuntimeFieldsAreAuthoritative()
        {
            byte[] authoritative = Xml(111, "0", "2939.812", "4199.771", 5);
            byte[] loaded = Xml(111, "25", "2939.812", "4199.771", 6);

            CoopLoadedWorldVerification.AssertRestoredState(loaded, authoritative);

            Assert.NotEqual(CoopWorldHash.Compute(authoritative), CoopWorldHash.Compute(loaded));
        }

        [Fact]
        public void AssertRestoredState_RejectsDifferentLandingShipManagerClock()
        {
            byte[] authoritative = Xml(111, "0", "2939.812", "4199.771", 5, "0.45");
            byte[] loaded = Xml(111, "0", "2939.812", "4199.771", 5, "0.95");

            Assert.Throws<InvalidDataException>(() =>
                CoopLoadedWorldVerification.AssertRestoredState(loaded, authoritative));
        }

        [Fact]
        public void NormalizeForHash_RejectsMissingAuthoritativeRuntimeState()
        {
            byte[] incomplete = Encoding.UTF8.GetBytes(
                "<save-game><id-generator><next-id value=\"1\" /></id-generator></save-game>");

            Assert.Throws<InvalidDataException>(() =>
                CoopLoadedWorldVerification.AssertRestoredState(incomplete, incomplete));
        }

        [Fact]
        public void NormalizeForHash_RejectsDtdInput()
        {
            byte[] malicious = Encoding.UTF8.GetBytes(
                "<!DOCTYPE save-game [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
                "<save-game><id-generator>&xxe;</id-generator></save-game>");

            Assert.Throws<XmlException>(() =>
                CoopLoadedWorldVerification.AssertRestoredState(malicious, malicious));
        }

        [Fact]
        public void ComputeForLoadedWorld_ToleratesTinyConstructionTimeBuiltFloatNoise()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"7\" />" +
                "<time-built value=\"66.5599985122681\" /></construction></constructions></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"7\" />" +
                "<time-built value=\"66.5599975585938\" /></construction></constructions></save-game>");

            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(authoritative),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
        }

        [Fact]
        public void ComputeForLoadedWorld_ToleratesTimeBuiltNoiseAcrossTruncationBoundary()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"146\" />" +
                "<time-built value=\"238.079994678497\" /></construction></constructions></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"146\" />" +
                "<time-built value=\"238.080001831055\" /></construction></constructions></save-game>");

            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(authoritative),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
        }

        [Fact]
        public void ComputeForLoadedWorld_ToleratesObservedTimeBuiltNoiseAcrossFourDecimalBoundary()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"120\" />" +
                "<time-built value=\"696.31995844841\" /></construction></constructions></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"120\" />" +
                "<time-built value=\"696.319946289063\" /></construction></constructions></save-game>");

            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(authoritative),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
        }

        [Theory]
        // Canli soak (v0.10.0, tick 81925): 3 haneye yuvarlama "8829.44" ile
        // "8829.439" verip recovery'yi oldurdu. Vanilla double yazip float okur.
        [InlineData("8829.43951", "8829.439453125")]
        [InlineData("12.0004999", "12.000499725341797")]
        [InlineData("131071.99", "131071.9921875")]
        public void ComputeForLoadedWorld_MatchesVanillaFloatRoundTripOfTimeBuilt(
            string authoritativeValue, string loadedValue)
        {
            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(TimeBuiltWorld(authoritativeValue)),
                CoopWorldHash.ComputeForLoadedWorld(TimeBuiltWorld(loadedValue)));
        }

        [Fact]
        public void ComputeForLoadedWorld_StillDetectsARealTimeBuiltDifference()
        {
            Assert.NotEqual(
                CoopWorldHash.ComputeForLoadedWorld(TimeBuiltWorld("8829.44")),
                CoopWorldHash.ComputeForLoadedWorld(TimeBuiltWorld("8829.5")));
        }

        [Theory]
        [InlineData("<interaction-point value=\"0\" />", true)]
        [InlineData("", false)]
        public void ComputeForLoadedWorld_OnlyIgnoresPositionOfAnchoredCharacters(
            string anchor, bool expectedEqual)
        {
            string hashBefore = CoopWorldHash.ComputeForLoadedWorld(CharacterWorld("1117.1", anchor));
            string hashAfter = CoopWorldHash.ComputeForLoadedWorld(CharacterWorld("1117.4", anchor));

            Assert.Equal(expectedEqual, hashBefore == hashAfter);
        }

        private static byte[] CharacterWorld(string x, string anchor)
        {
            return Encoding.UTF8.GetBytes(
                "<save-game><characters><character><id value=\"112\" />" +
                "<position x=\"" + x + "\" y=\"0\" z=\"5\" /></character></characters>" +
                "<interactions><interaction type=\"InteractionWaitForHealing\">" +
                "<character id=\"112\" />" + anchor + "</interaction></interactions></save-game>");
        }

        private static byte[] TimeBuiltWorld(string timeBuilt)
        {
            return Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"9\" />" +
                "<time-built value=\"" + timeBuilt + "\" /></construction></constructions></save-game>");
        }

        [Fact]
        public void LoadedWorldDifferenceReportsTheFirstNormalizedSemanticChange()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><camera><height value=\"20\" /></camera>" +
                "<characters><human><id value=\"7\" /><state value=\"1\" />" +
                "<specialization value=\"Worker\" /></human></characters></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><camera><height value=\"99\" /></camera>" +
                "<characters><human><id value=\"7\" /><state value=\"0\" />" +
                "<specialization value=\"Biologist\" /></human></characters></save-game>");

            CoopXmlDifference difference =
                CoopWorldHash.FindFirstLoadedWorldDifference(authoritative, loaded);

            Assert.False(difference.AreEqual);
            Assert.Contains("specialization", difference.Path);
            Assert.Equal("Worker", difference.LeftValue);
            Assert.Equal("Biologist", difference.RightValue);
        }

        [Fact]
        public void ComputeForLoadedWorld_ToleratesVanillaIncompleteModuleStateTransition()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\">" +
                "<state value=\"1\" /><build-progress value=\"0.72\" />" +
                "<construction-materials><id value=\"5\" /><id value=\"38\" />" +
                "</construction-materials><id value=\"158\" /><time-built value=\"0\" />" +
                "<module-type value=\"ModuleTypeCanteen\" /></construction>" +
                "</constructions></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\">" +
                "<state value=\"2\" /><build-progress value=\"0.72\" />" +
                "<construction-materials><id value=\"5\" /><id value=\"38\" />" +
                "</construction-materials><id value=\"158\" /><time-built value=\"0\" />" +
                "<module-type value=\"ModuleTypeCanteen\" /></construction>" +
                "</constructions></save-game>");

            Assert.NotEqual(
                CoopWorldHash.Compute(authoritative),
                CoopWorldHash.Compute(loaded));
            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(authoritative),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
            Assert.Equal(
                CoopWorldHash.ComputeRuntime(authoritative),
                CoopWorldHash.ComputeRuntime(loaded));
        }

        [Fact]
        public void RuntimeHashToleratesObservedIncompleteConnectionLoaderTransition()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Connection\">" +
                "<state value=\"1\" /><build-progress value=\"0.1679999\" />" +
                "<construction-materials><id value=\"28\" /><id value=\"59\" />" +
                "</construction-materials><id value=\"138\" />" +
                "<time-built value=\"0\" /></construction></constructions></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Connection\">" +
                "<state value=\"2\" /><build-progress value=\"0.1679999\" />" +
                "<construction-materials><id value=\"28\" /><id value=\"59\" />" +
                "</construction-materials><id value=\"138\" />" +
                "<time-built value=\"0\" /></construction></constructions></save-game>");

            Assert.Equal(
                CoopWorldHash.ComputeRuntime(authoritative),
                CoopWorldHash.ComputeRuntime(loaded));
        }

        [Fact]
        public void RuntimeHashRemainsStableAcrossCombinedRecoveryLoaderNoise()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game>" +
                "<characters><human><id value=\"7\" /><state value=\"1\" />" +
                "<specialization value=\"Worker\" /></human></characters>" +
                "<constructions><construction type=\"Connection\">" +
                "<id value=\"138\" /><state value=\"1\" />" +
                "<build-progress value=\"0.1679999\" />" +
                "<construction-materials><id value=\"28\" /><id value=\"59\" />" +
                "</construction-materials><time-built value=\"0\" />" +
                "</construction><construction type=\"Module\"><id value=\"139\" />" +
                "<time-built value=\"238.079994678497\" />" +
                "</construction></constructions>" +
                "<resources><resource type=\"Metal\"><id value=\"53\" />" +
                "<position x=\"1143.268\" y=\"0.4560592\" z=\"1068.308\" />" +
                "<orientation x=\"1\" y=\"2\" z=\"3\" />" +
                "<state value=\"0\" /><location value=\"1\" />" +
                "</resource></resources></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game>" +
                "<characters><human><id value=\"7\" /><state value=\"0\" />" +
                "<specialization value=\"Worker\" /></human></characters>" +
                "<constructions><construction type=\"Connection\">" +
                "<id value=\"138\" /><state value=\"2\" />" +
                "<build-progress value=\"0.1679999\" />" +
                "<construction-materials><id value=\"28\" /><id value=\"59\" />" +
                "</construction-materials><time-built value=\"0\" />" +
                "</construction><construction type=\"Module\"><id value=\"139\" />" +
                "<time-built value=\"238.080001831055\" />" +
                "</construction></constructions>" +
                "<resources><resource type=\"Metal\"><id value=\"53\" />" +
                "<position x=\"1143.424\" y=\"0.1908141\" z=\"1068.403\" />" +
                "<orientation x=\"9\" y=\"8\" z=\"7\" />" +
                "<state value=\"0\" /><location value=\"1\" />" +
                "</resource></resources></save-game>");

            Assert.Equal(
                CoopWorldHash.ComputeRuntime(authoritative),
                CoopWorldHash.ComputeRuntime(loaded));
        }

        [Fact]
        public void RuntimeHashMatchesOptInRecoveryArtifacts()
        {
            string authoritativePath =
                Environment.GetEnvironmentVariable("PLANETBASE_COOP_AUTHORITATIVE_SAVE");
            string loadedPath =
                Environment.GetEnvironmentVariable("PLANETBASE_COOP_LOADED_SAVE");
            if (string.IsNullOrWhiteSpace(authoritativePath) ||
                string.IsNullOrWhiteSpace(loadedPath)) return;

            byte[] authoritative = File.ReadAllBytes(authoritativePath);
            byte[] loaded = File.ReadAllBytes(loadedPath);
            CoopXmlDifference difference =
                CoopWorldHash.FindFirstLoadedWorldDifference(authoritative, loaded);

            Assert.True(
                difference.AreEqual,
                difference.Path + ": " + difference.Description +
                " expected=" + difference.LeftValue +
                " actual=" + difference.RightValue);
            Assert.Equal(
                CoopWorldHash.ComputeRuntime(authoritative),
                CoopWorldHash.ComputeRuntime(loaded));
        }

        [Fact]
        public void ComputeForLoadedWorld_ToleratesGroundedResourceResettling()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"53\" />" +
                "<position x=\"1143.268\" y=\"0.4560592\" z=\"1068.308\" />" +
                "<state value=\"0\" /><location value=\"1\" />" +
                "</resource></resources></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"53\" />" +
                "<position x=\"1143.424\" y=\"0.1908141\" z=\"1068.403\" />" +
                "<state value=\"0\" /><location value=\"1\" />" +
                "</resource></resources></save-game>");

            Assert.NotEqual(CoopWorldHash.Compute(authoritative), CoopWorldHash.Compute(loaded));
            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(authoritative),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
        }

        [Fact]
        public void ComputeForLoadedWorld_ToleratesResourceStorageSlotResettling()
        {
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"30\" />" +
                "<resource-storage><slot><resource type=\"Metal\"><id value=\"53\" />" +
                "<position x=\"4\" y=\"0.2\" z=\"7\" />" +
                "<orientation x=\"0\" y=\"1\" z=\"0\" />" +
                "<state value=\"0\" /><location value=\"2\" />" +
                "</resource></slot></resource-storage></construction></constructions></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><id value=\"30\" />" +
                "<resource-storage><slot><resource type=\"Metal\"><id value=\"53\" />" +
                "<position x=\"4\" y=\"0.4\" z=\"7\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"1\" />" +
                "<state value=\"0\" /><location value=\"2\" />" +
                "</resource></slot></resource-storage></construction></constructions></save-game>");

            Assert.NotEqual(CoopWorldHash.Compute(authoritative), CoopWorldHash.Compute(loaded));
            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(authoritative),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
        }

        [Fact]
        public void StoredResourceNormalization_PreservesSemanticIdentityAndState()
        {
            byte[] metal = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><resource-storage><slot>" +
                "<resource type=\"Metal\"><id value=\"53\" /><position y=\"0.2\" />" +
                "<state value=\"0\" /><location value=\"2\" /></resource>" +
                "</slot></resource-storage></construction></constructions></save-game>");
            byte[] bioplastic = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction><resource-storage><slot>" +
                "<resource type=\"Bioplastic\"><id value=\"53\" /><position y=\"0.4\" />" +
                "<state value=\"0\" /><location value=\"2\" /></resource>" +
                "</slot></resource-storage></construction></constructions></save-game>");

            Assert.NotEqual(
                CoopWorldHash.ComputeForLoadedWorld(metal),
                CoopWorldHash.ComputeForLoadedWorld(bioplastic));
        }

        [Theory]
        [InlineData("1", "2", "0.5", true)]
        [InlineData("2", "3", "0", true)]
        [InlineData("1", "2", "0", false)]
        public void LoaderModuleStateNormalizationKeepsSemanticBoundaries(
            string authoritativeState,
            string loadedState,
            string timeBuilt,
            bool includeMaterials)
        {
            string materials = includeMaterials
                ? "<construction-materials><id value=\"5\" /></construction-materials>"
                : string.Empty;
            byte[] authoritative = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\">" +
                "<state value=\"" + authoritativeState + "\" /><build-progress value=\"0.72\" />" +
                materials + "<id value=\"158\" /><time-built value=\"" + timeBuilt + "\" />" +
                "</construction></constructions></save-game>");
            byte[] loaded = Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\">" +
                "<state value=\"" + loadedState + "\" /><build-progress value=\"0.72\" />" +
                materials + "<id value=\"158\" /><time-built value=\"" + timeBuilt + "\" />" +
                "</construction></constructions></save-game>");

            Assert.NotEqual(
                CoopWorldHash.ComputeForLoadedWorld(authoritative),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
        }

        [Fact]
        public void AssertFrameIndexRestored_RejectsFailedReflectionWrite()
        {
            Assert.Throws<InvalidDataException>(() =>
                CoopLoadedWorldVerification.AssertFrameIndexRestored(84, 83));
        }

        [Fact]
        public void AssertFrameIndexRestored_AcceptsExactAuthoritativeValue()
        {
            CoopLoadedWorldVerification.AssertFrameIndexRestored(84, 84);
        }

        [Fact]
        public void ShipManagerStateReadsLandingPermissionsForRecovery()
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<save-game><ship-manager><landing-permissions>" +
                "<colonists-allowed value=\"False\" /><merchants-allowed value=\"True\" />" +
                "<visitors-allowed value=\"False\" /><Worker-percentage value=\"40\" />" +
                "<Biologist-percentage value=\"30\" /><Engineer-percentage value=\"20\" />" +
                "<Medic-percentage value=\"10\" /><Guard-percentage value=\"0\" />" +
                "</landing-permissions><coop-tick-time value=\"0.0972001255\" /></ship-manager></save-game>");

            Assert.True(CoopShipManagerClock.TryReadLandingPermissions(
                document.DocumentElement, out CoopLandingPermissionsData permissions));
            Assert.False(permissions.ColonistsAllowed);
            Assert.True(permissions.MerchantsAllowed);
            Assert.False(permissions.VisitorsAllowed);
            Assert.Equal(new[] { 40, 30, 20, 10, 0 }, permissions.SpecializationPercentages);
        }

        private static byte[] Xml(
            int nextId,
            string cameraX,
            string blizzardTimer,
            string solarFlareTimer,
            int resourceCount,
            string shipTickTime = "0.6372")
        {
            string xml =
                "<save-game>" +
                "<id-generator><next-id value=\"" + nextId +
                "\" /><next-bot-id value=\"0\" /></id-generator>" +
                "<camera><orientation x=\"" + cameraX + "\" y=\"180\" z=\"0\" /></camera>" +
                Disaster("sandstorm", "4411.107") +
                Disaster("blizzard", blizzardTimer) +
                Disaster("solar-flare", solarFlareTimer) +
                "<ship-manager><coop-tick-time value=\"" + shipTickTime +
                "\" /></ship-manager>" +
                "<resources><resource type=\"Metal\"><count value=\"" + resourceCount +
                "\" /></resource></resources>" +
                "</save-game>";
            return Encoding.UTF8.GetBytes(xml);
        }

        private static string Disaster(string name, string timer)
        {
            return "<" + name + ">" +
                "<" + name + "-in-progress value=\"False\" />" +
                "<time-to-next-" + name + " value=\"" + timer + "\" />" +
                "<time value=\"0\" />" +
                "<" + name + "-time value=\"0\" />" +
                "</" + name + ">";
        }
    }
}
