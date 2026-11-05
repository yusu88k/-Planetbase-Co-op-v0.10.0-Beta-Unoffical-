using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class ProtocolTests
    {
        [Fact]
        public void ReadyPacketRoundTripsWithBoundedHash()
        {
            byte[] packet = CoopProtocol.CreateReadyPacket(42, "world-hash", 7);
            Assert.True(CoopProtocol.TryParseReadyPacket(packet, out long session, out string hash, out long tick));
            Assert.Equal(42, session);
            Assert.Equal("world-hash", hash);
            Assert.Equal(7, tick);
        }

        [Fact]
        public void InvalidPacketIsRejected()
        {
            Assert.False(CoopProtocol.TryParseReadyPacket(new byte[] { CoopProtocol.PktReady, 1, 2 }, out _, out _, out _));
            Assert.False(CoopProtocol.IsValidFrameLength(0));
            Assert.False(CoopProtocol.IsValidFrameLength(CoopProtocol.MaxPacketBytes + 1));
            Assert.True(CoopProtocol.IsValidFrameLengthForType(
                CoopProtocol.PktWorld,
                CoopSnapshotInvariant.WorldPacketHeaderBytes +
                    CoopSnapshotInvariant.MaxSnapshotBytes));
            Assert.False(CoopProtocol.IsValidFrameLengthForType(255, 32));
            Assert.False(CoopProtocol.IsValidFrameLengthForType(
                CoopProtocol.PktAuthoritativeCommand,
                CoopProtocol.MaxControlPacketBytes + 1));
        }

        [Fact]
        public void StartPacketCarriesAuthoritativeTimeBaseline()
        {
            byte[] packet = CoopProtocol.CreateStartPacket(42, 128, true, 2);
            Assert.True(CoopProtocol.TryParseStartPacket(
                packet,
                out long sessionId,
                out long tick,
                out bool paused,
                out int scaleIndex));
            Assert.Equal(42, sessionId);
            Assert.Equal(128, tick);
            Assert.True(paused);
            Assert.Equal(2, scaleIndex);

            packet[17] = 2;
            Assert.False(CoopProtocol.TryParseStartPacket(
                packet, out _, out _, out _, out _));
        }

        [Fact]
        public void ProfileNamesArePathSafe()
        {
            Assert.True(CoopProtocol.IsSafeProfileName("coop-guest_smoke2"));
            Assert.False(CoopProtocol.IsSafeProfileName("..\\escape"));
            Assert.False(CoopProtocol.IsSafeProfileName("guest/profile"));
            Assert.False(CoopProtocol.IsSafeProfileName(""));
        }

        [Fact]
        public void WorldHashIgnoresLocalCameraAndTinyWeatherTimerDriftButDetectsEnvironmentDivergence()
        {
            byte[] host = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><camera><height value=\"21\" /></camera><sandstorm><time-to-next-sandstorm value=\"4408.545\" /></sandstorm><characters><human><position x=\"900.1231\" y=\"0.00001\" z=\"901.4561\" /><orientation y=\"34.3242\" /></human></characters><environment><time-of-day value=\"0.1\" /><wind-indicator value=\"0.72\" /></environment></save-game>");
            byte[] localOnlyDrift = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><camera><height value=\"99\" /></camera><sandstorm><time-to-next-sandstorm value=\"4408.544\" /></sandstorm><characters><human><position x=\"900.1234\" y=\"0.00002\" z=\"901.4563\" /><orientation y=\"34.3251\" /></human></characters><environment><time-of-day value=\"0.1\" /><wind-indicator value=\"0.72\" /></environment></save-game>");
            byte[] environmentDiverged = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><camera><height value=\"99\" /></camera><sandstorm><time-to-next-sandstorm value=\"4408.544\" /></sandstorm><characters><human><position x=\"900.1234\" y=\"0.00002\" z=\"901.4563\" /><orientation y=\"34.3251\" /></human></characters><environment><time-of-day value=\"0.8\" /><wind-indicator value=\"0.84\" /></environment></save-game>");
            byte[] diverged = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><camera><height value=\"99\" /></camera><sandstorm><time-to-next-sandstorm value=\"4408.544\" /></sandstorm><characters><human><position x=\"900.3234\" y=\"0.00002\" z=\"901.4563\" /><orientation y=\"34.3251\" /></human></characters><environment><time-of-day value=\"0.2\" /></environment></save-game>");

            Assert.Equal(CoopWorldHash.Compute(host), CoopWorldHash.Compute(localOnlyDrift));
            Assert.NotEqual(CoopWorldHash.Compute(host), CoopWorldHash.Compute(environmentDiverged));
            Assert.NotEqual(CoopWorldHash.Compute(host), CoopWorldHash.Compute(diverged));
        }

        [Fact]
        public void WorldHashIgnoresSubTenthSpatialBoundaryDriftButDetectsMeaningfulDrift()
        {
            byte[] left = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><position x=\"900.049\" y=\"0\" z=\"901.049\" /><orientation y=\"34.949\" /></character></characters></save-game>");
            byte[] right = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><position x=\"900.051\" y=\"0\" z=\"901.051\" /><orientation y=\"34.951\" /></character></characters></save-game>");
            byte[] diverged = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><position x=\"900.201\" y=\"0\" z=\"901.201\" /><orientation y=\"35.101\" /></character></characters></save-game>");

            Assert.Equal(CoopWorldHash.Compute(left), CoopWorldHash.Compute(right));
            Assert.NotEqual(CoopWorldHash.Compute(left), CoopWorldHash.Compute(diverged));
        }

        [Fact]
        public void WorldHashIgnoresCharacterNeedFloatNoiseButDetectsMeaningfulNeedDrift()
        {
            byte[] left = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><Hydration value=\"0.8687978\" /><Nutrition value=\"0.8687978\" /><Oxygen value=\"0.4506696\" /><Sleep value=\"0.9153811\" /></character></characters></save-game>");
            byte[] right = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><Hydration value=\"0.8687977\" /><Nutrition value=\"0.8687977\" /><Oxygen value=\"0.4506697\" /><Sleep value=\"0.9153810\" /></character></characters></save-game>");
            byte[] diverged = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><Hydration value=\"0.8700000\" /><Nutrition value=\"0.8687977\" /><Oxygen value=\"0.4506697\" /><Sleep value=\"0.9153810\" /></character></characters></save-game>");

            Assert.Equal(CoopWorldHash.Compute(left), CoopWorldHash.Compute(right));
            Assert.NotEqual(CoopWorldHash.Compute(left), CoopWorldHash.Compute(diverged));
        }

        [Fact]
        public void WorldHashIgnoresGeneratedCharacterAndShipNames()
        {
            byte[] left = System.Text.Encoding.UTF8.GetBytes("<save-game><characters><human><id value=\"111\" /><name value=\"Ernie T. Day\" /></human></characters><ships><ship><id value=\"207\" /><name value=\"Mira Stone\" /></ship></ships></save-game>");
            byte[] right = System.Text.Encoding.UTF8.GetBytes("<save-game><characters><human><id value=\"111\" /><name value=\"Keagan Heisse\" /></human></characters><ships><ship><id value=\"207\" /><name value=\"Alex North\" /></ship></ships></save-game>");

            Assert.Equal(CoopWorldHash.Compute(left), CoopWorldHash.Compute(right));
        }

        [Fact]
        public void WorldHashIgnoresLoaderReorderingOfConstructionEntities()
        {
            byte[] received = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions>" +
                "<construction type=\"Module\"><id value=\"145\" /></construction>" +
                "<construction type=\"Connection\"><id value=\"147\" /></construction>" +
                "<construction type=\"Module\"><id value=\"148\" /></construction>" +
                "</constructions></save-game>");
            byte[] loaded = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions>" +
                "<construction type=\"Module\"><id value=\"145\" /></construction>" +
                "<construction type=\"Module\"><id value=\"148\" /></construction>" +
                "<construction type=\"Connection\"><id value=\"147\" /></construction>" +
                "</constructions></save-game>");

            Assert.Equal(CoopWorldHash.Compute(received), CoopWorldHash.Compute(loaded));
        }

        [Fact]
        public void LoadedWorldHashIgnoresVanillaLoaderTransientFields()
        {
            byte[] received = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><id value=\"111\" /><state value=\"1\" /></character></characters>" +
                "<resources><resource><id value=\"122\" /><position x=\"1\" y=\"2\" z=\"3\" />" +
                "<orientation x=\"0\" y=\"0\" z=\"0\" /><state value=\"1\" /></resource></resources></save-game>");
            byte[] loaded = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><characters><character><id value=\"111\" /><state value=\"0\" /></character></characters>" +
                "<resources><resource><id value=\"122\" /><position x=\"9\" y=\"8\" z=\"7\" />" +
                "<orientation x=\"1\" y=\"2\" z=\"3\" /><state value=\"1\" /></resource></resources></save-game>");

            Assert.NotEqual(CoopWorldHash.Compute(received), CoopWorldHash.Compute(loaded));
            Assert.Equal(
                CoopWorldHash.ComputeForLoadedWorld(received),
                CoopWorldHash.ComputeForLoadedWorld(loaded));
        }

        [Fact]
        public void WorldHashIgnoresCarriedResourceAnchorDriftButDetectsMeaningfulResourceChanges()
        {
            byte[] carriedHost = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"122\" />" +
                "<position x=\"12.5\" y=\"0.03674248\" z=\"8.25\" />" +
                "<orientation x=\"0\" y=\"45\" z=\"0\" /><state value=\"1\" />" +
                "</resource></resources></save-game>");
            byte[] carriedGuest = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"122\" />" +
                "<position x=\"12.5\" y=\"0.2652453\" z=\"8.25\" />" +
                "<orientation x=\"1\" y=\"52\" z=\"3\" /><state value=\"1\" />" +
                "</resource></resources></save-game>");
            byte[] changedState = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"122\" />" +
                "<position x=\"12.5\" y=\"0.03674248\" z=\"8.25\" />" +
                "<orientation x=\"0\" y=\"45\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");
            byte[] groundedMoved = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"122\" />" +
                "<position x=\"12.5\" y=\"2.0\" z=\"8.25\" />" +
                "<orientation x=\"0\" y=\"45\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");
            byte[] changedIdentity = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"123\" />" +
                "<position x=\"12.5\" y=\"0.03674248\" z=\"8.25\" />" +
                "<orientation x=\"0\" y=\"45\" z=\"0\" /><state value=\"1\" />" +
                "</resource></resources></save-game>");

            Assert.Equal(CoopWorldHash.Compute(carriedHost), CoopWorldHash.Compute(carriedGuest));
            Assert.NotEqual(CoopWorldHash.Compute(carriedHost), CoopWorldHash.Compute(changedState));
            Assert.NotEqual(CoopWorldHash.Compute(changedState), CoopWorldHash.Compute(groundedMoved));
            Assert.NotEqual(CoopWorldHash.Compute(carriedHost), CoopWorldHash.Compute(changedIdentity));
        }

        [Fact]
        public void WorldHashIgnoresBusyResourceTransformDriftButDetectsStateIdentityAndLocationChanges()
        {
            byte[] busyHost = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"54\" />" +
                "<position x=\"1073.951\" y=\"0.1500006\" z=\"1066.958\" />" +
                "<orientation x=\"0\" y=\"323.8907\" z=\"0\" /><state value=\"3\" />" +
                "</resource></resources></save-game>");
            byte[] busyGuest = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"54\" />" +
                "<position x=\"1073.961\" y=\"0.1499996\" z=\"1066.949\" />" +
                "<orientation x=\"0\" y=\"324.017\" z=\"0\" /><state value=\"3\" />" +
                "</resource></resources></save-game>");
            byte[] changedState = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"54\" />" +
                "<position x=\"1073.951\" y=\"0.1500006\" z=\"1066.958\" />" +
                "<orientation x=\"0\" y=\"323.8907\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");
            byte[] changedIdentity = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"55\" />" +
                "<position x=\"1073.951\" y=\"0.1500006\" z=\"1066.958\" />" +
                "<orientation x=\"0\" y=\"323.8907\" z=\"0\" /><state value=\"3\" />" +
                "</resource></resources></save-game>");
            byte[] groundedHost = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"54\" />" +
                "<position x=\"1073.951\" y=\"0.1500006\" z=\"1066.958\" />" +
                "<orientation x=\"0\" y=\"323.8907\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");
            byte[] groundedMoved = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Metal\"><id value=\"54\" />" +
                "<position x=\"1078.951\" y=\"0.1500006\" z=\"1066.958\" />" +
                "<orientation x=\"0\" y=\"323.8907\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");

            Assert.Equal(CoopWorldHash.Compute(busyHost), CoopWorldHash.Compute(busyGuest));
            Assert.NotEqual(CoopWorldHash.Compute(busyHost), CoopWorldHash.Compute(changedState));
            Assert.NotEqual(CoopWorldHash.Compute(busyHost), CoopWorldHash.Compute(changedIdentity));
            Assert.NotEqual(CoopWorldHash.Compute(groundedHost), CoopWorldHash.Compute(groundedMoved));
        }

        [Fact]
        public void WorldHashIgnoresGroundedResourceOrientationJitterButDetectsPositionMovement()
        {
            byte[] host = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Bioplastic\"><id value=\"56\" />" +
                "<position x=\"1036.139\" y=\"0.2000022\" z=\"1055.566\" />" +
                "<orientation x=\"0\" y=\"228.3345\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");
            byte[] guest = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Bioplastic\"><id value=\"56\" />" +
                "<position x=\"1036.106\" y=\"0.2000022\" z=\"1055.502\" />" +
                "<orientation x=\"0\" y=\"227.4464\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");
            byte[] moved = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><resources><resource type=\"Bioplastic\"><id value=\"56\" />" +
                "<position x=\"1041.139\" y=\"0.2000022\" z=\"1055.566\" />" +
                "<orientation x=\"0\" y=\"228.3345\" z=\"0\" /><state value=\"0\" />" +
                "</resource></resources></save-game>");

            Assert.Equal(CoopWorldHash.Compute(host), CoopWorldHash.Compute(guest));
            Assert.NotEqual(CoopWorldHash.Compute(host), CoopWorldHash.Compute(moved));
        }

        [Fact]
        public void WorldHashIgnoresNestedStoredResourceOrientationButKeepsIdentity()
        {
            byte[] host = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\"><id value=\"158\" />" +
                "<components><component><resource-container><resource type=\"Metal\">" +
                "<id value=\"187\" /><orientation x=\"345.8383\" y=\"174.2513\" z=\"1.078935\" />" +
                "</resource></resource-container></component></components></construction></constructions></save-game>");
            byte[] guest = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\"><id value=\"158\" />" +
                "<components><component><resource-container><resource type=\"Metal\">" +
                "<id value=\"187\" /><orientation x=\"345.8318\" y=\"174.3106\" z=\"1.041908\" />" +
                "</resource></resource-container></component></components></construction></constructions></save-game>");
            byte[] changedIdentity = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(guest).Replace("187", "188"));

            Assert.Equal(CoopWorldHash.Compute(host), CoopWorldHash.Compute(guest));
            Assert.NotEqual(CoopWorldHash.Compute(host), CoopWorldHash.Compute(changedIdentity));
        }

        [Fact]
        public void WorldHashIgnoresObservedContinuousSimulationJitterButDetectsMeaningfulMovement()
        {
            byte[] host = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\"><id value=\"150\" />" +
                "<state value=\"3\" /><power-storage value=\"7898832\" />" +
                "<water-storage value=\"2040560\" /></construction></constructions>" +
                "<characters><character type=\"Colonist\"><id value=\"117\" />" +
                "<position x=\"1162.267\" y=\"0.199993\" z=\"1058.643\" />" +
                "<orientation x=\"0\" y=\"326.2994\" z=\"0\" /><state value=\"3\" />" +
                "</character></characters><resources><resource type=\"Metal\">" +
                "<id value=\"8\" /><position x=\"1148.042\" y=\"0\" z=\"1042.619\" />" +
                "<state value=\"0\" /><location value=\"1\" /></resource></resources></save-game>");
            byte[] guest = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\"><id value=\"150\" />" +
                "<state value=\"3\" /><power-storage value=\"7898272\" />" +
                "<water-storage value=\"2044320\" /></construction></constructions>" +
                "<characters><character type=\"Colonist\"><id value=\"117\" />" +
                "<position x=\"1162.267\" y=\"0.199993\" z=\"1058.643\" />" +
                "<orientation x=\"0\" y=\"326.3\" z=\"0\" /><state value=\"3\" />" +
                "</character></characters><resources><resource type=\"Metal\">" +
                "<id value=\"8\" /><position x=\"1147.857\" y=\"0\" z=\"1042.63\" />" +
                "<state value=\"0\" /><location value=\"1\" /></resource></resources></save-game>");
            byte[] meaningfullyMoved = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(guest)
                    .Replace("1147.857", "1148.857"));
            byte[] materiallyDifferentStorage = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(guest)
                    .Replace("7898272", "8898272"));

            Assert.Equal(CoopWorldHash.Compute(host), CoopWorldHash.Compute(guest));
            Assert.NotEqual(CoopWorldHash.Compute(host), CoopWorldHash.Compute(meaningfullyMoved));
            Assert.NotEqual(CoopWorldHash.Compute(host), CoopWorldHash.Compute(materiallyDifferentStorage));
        }

        [Fact]
        public void WorldHashNormalizesScientificStorageJitterButKeepsMaterialDifference()
        {
            byte[] host = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\"><id value=\"150\" />" +
                "<state value=\"3\" /><power-storage value=\"1.045858E+07\" />" +
                "</construction></constructions></save-game>");
            byte[] guest = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\"><id value=\"150\" />" +
                "<state value=\"3\" /><power-storage value=\"1.046472E+07\" />" +
                "</construction></constructions></save-game>");
            byte[] materiallyDifferent = System.Text.Encoding.UTF8.GetBytes(
                "<save-game><constructions><construction type=\"Module\"><id value=\"150\" />" +
                "<state value=\"3\" /><power-storage value=\"1.145858E+07\" />" +
                "</construction></constructions></save-game>");

            Assert.Equal(CoopWorldHash.Compute(host), CoopWorldHash.Compute(guest));
            Assert.NotEqual(CoopWorldHash.Compute(host), CoopWorldHash.Compute(materiallyDifferent));
        }

        [Fact]
        public void HashesMatchEvenWhenPeerPacketArrivesFirst()
        {
            var matcher = new CoopTickHashMatcher();

            Assert.Equal(CoopHashMatch.Pending, matcher.RecordPeer(128, "abc"));
            Assert.Equal(CoopHashMatch.Match, matcher.RecordLocal(128, "abc"));
            Assert.Equal(CoopHashMatch.Pending, matcher.RecordLocal(256, "left"));
            Assert.Equal(CoopHashMatch.Mismatch, matcher.RecordPeer(256, "right"));
        }

        [Fact]
        public void HashPacketRoundTripsOnlyFixedLowercaseHexDigest()
        {
            byte[] packet = CoopProtocol.CreateHashPacket(128, "0123456789abcdef");

            long tick;
            string hash;
            Assert.True(CoopProtocol.TryParseHashPacket(packet, out tick, out hash));
            Assert.Equal(128, tick);
            Assert.Equal("0123456789abcdef", hash);
            Assert.False(CoopProtocol.TryParseHashPacket(
                new byte[] { CoopProtocol.PktHash, 0 }, out tick, out hash));
            Assert.Throws<System.ArgumentException>(() =>
                CoopProtocol.CreateHashPacket(128, "not-a-world-hash"));
            Assert.Throws<System.ArgumentException>(() =>
                CoopProtocol.CreateHashPacket(128, "0123456789ABCDEf"));
        }

        [Fact]
        public void HashTickMustStayWithinBoundedWindow()
        {
            Assert.True(CoopProtocol.IsPlausibleHashTick(512, 1024));
            Assert.True(CoopProtocol.IsPlausibleHashTick(1536, 1024));
            Assert.False(CoopProtocol.IsPlausibleHashTick(511, 1024));
            Assert.False(CoopProtocol.IsPlausibleHashTick(1537, 1024));
            Assert.False(CoopProtocol.IsPlausibleHashTick(-1, 0));
        }

        [Fact]
        public void TickPacketRequiresExactNonNegativeInt64Payload()
        {
            byte[] packet = CoopProtocol.CreateTickPacket(42);
            long tick;

            Assert.True(CoopProtocol.TryParseTickPacket(packet, out tick));
            Assert.Equal(42, tick);
            Assert.False(CoopProtocol.TryParseTickPacket(
                new byte[] { CoopProtocol.PktTick }, out tick));
            Assert.False(CoopProtocol.TryParseTickPacket(
                new byte[] { CoopProtocol.PktTick, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, out tick));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CoopProtocol.CreateTickPacket(-1));
        }

        [Fact]
        public void WorldTickRejectsNegativeOverflowAndImplausibleRecoveryValues()
        {
            Assert.True(CoopProtocol.IsValidWorldTick(0, 0, recovery: false));
            Assert.True(CoopProtocol.IsValidWorldTick(CoopProtocol.MaxWorldTick, 0, recovery: false));
            Assert.False(CoopProtocol.IsValidWorldTick(-1, 0, recovery: false));
            Assert.False(CoopProtocol.IsValidWorldTick(long.MaxValue, 0, recovery: false));
            Assert.True(CoopProtocol.IsValidWorldTick(1512, 1000, recovery: true));
            Assert.False(CoopProtocol.IsValidWorldTick(1513, 1000, recovery: true));
        }

        [Fact]
        public void HashMatcherBoundsUnpairedTicksAndEvictsCompletedPairs()
        {
            var matcher = new CoopTickHashMatcher();

            for (long tick = 0; tick < 1000; tick++)
                Assert.Equal(CoopHashMatch.Pending, matcher.RecordPeer(tick, "0123456789abcdef"));

            Assert.InRange(matcher.PendingEntryCount, 1, 32);
            Assert.Equal(CoopHashMatch.Match,
                matcher.RecordLocal(999, "0123456789abcdef"));
            Assert.InRange(matcher.PendingEntryCount, 0, 32);
        }

        [Fact]
        public void PlacementPreviewPacketRoundTrips()
        {
            var preview = new CoopPlacementPreviewData
            {
                Active = true,
                Source = CoopRole.Guest,
                Kind = CoopPlacementKind.Module,
                Sequence = 12,
                ParentId = 0,
                TypeName = "ModuleTypeHabitat",
                SizeIndex = 2,
                PositionX = 910.25f,
                PositionY = 0.2f,
                PositionZ = 948.5f,
                RotationY = 90f,
                Valid = true
            };

            byte[] packet = CoopProtocol.CreatePreviewPacket(preview);
            Assert.True(CoopProtocol.TryParsePreviewPacket(packet, out CoopPlacementPreviewData parsed));
            Assert.Equal(preview.TypeName, parsed.TypeName);
            Assert.Equal(CoopRole.Guest, parsed.Source);
            Assert.Equal(preview.Sequence, parsed.Sequence);
            Assert.Equal(preview.PositionX, parsed.PositionX);
            Assert.True(parsed.Valid);
        }

        [Fact]
        public void MalformedPlacementPreviewPacketIsRejected()
        {
            var preview = new CoopPlacementPreviewData
            {
                Active = true,
                Kind = CoopPlacementKind.Module,
                Sequence = 1,
                TypeName = "ModuleTypeHabitat",
                SizeIndex = 0,
                PositionX = 1f,
                PositionY = 0f,
                PositionZ = 1f,
                RotationY = 0f,
                Valid = true
            };
            byte[] packet = CoopProtocol.CreatePreviewPacket(preview);
            byte[] truncated = new byte[packet.Length - 2];
            System.Buffer.BlockCopy(packet, 0, truncated, 0, truncated.Length);

            Assert.False(CoopProtocol.TryParsePreviewPacket(truncated, out _));
        }

        [Fact]
        public void PlacementValidatorRejectsUnsafeOrOutOfBoundsRequests()
        {
            var request = new CoopPlacementCommandData
            {
                RequestId = 4,
                Kind = CoopPlacementKind.Module,
                TypeName = "ModuleType/../../escape",
                SizeIndex = 0,
                PositionX = 99999f,
                PositionY = 0f,
                PositionZ = 0f,
                RotationY = 0f,
                Valid = true
            };

            Assert.False(CoopPlacementValidator.TryValidate(request, out _, out _));
        }

        [Fact]
        public void PlacementValidatorRejectsClientMarkedInvalidRequest()
        {
            var request = new CoopPlacementCommandData
            {
                RequestId = 5,
                Kind = CoopPlacementKind.Module,
                TypeName = "ModuleTypeHabitat",
                SizeIndex = 0,
                PositionX = 1f,
                PositionY = 0f,
                PositionZ = 1f,
                RotationY = 0f,
                Valid = false
            };

            Assert.False(CoopPlacementValidator.TryValidate(request, out _, out string reason));
            Assert.Equal("placement is marked invalid", reason);
        }

        [Fact]
        public void PlacementValidatorRequiresParentForComponents()
        {
            var request = new CoopPlacementCommandData
            {
                RequestId = 6,
                Kind = CoopPlacementKind.Component,
                TypeName = "Table",
                SizeIndex = 0,
                PositionX = 1f,
                PositionY = 0f,
                PositionZ = 1f,
                RotationY = 0f,
                Valid = true
            };

            Assert.False(CoopPlacementValidator.TryValidate(request, out _, out string reason));
            Assert.Equal("component parent id is required", reason);
        }

        [Fact]
        public void PlacementResultPacketRoundTrips()
        {
            var command = new CoopPlacementCommandData
            {
                SessionId = 42,
                RequestId = 88,
                Kind = CoopPlacementKind.Component,
                ParentId = 123,
                TypeName = "Table",
                SizeIndex = 0,
                PositionX = 1f,
                PositionY = 0.2f,
                PositionZ = 2f,
                RotationY = 180f,
                Valid = true
            };

            byte[] packet = CoopProtocol.CreatePlacementResultPacket(true, 144, command);
            Assert.True(CoopProtocol.TryParsePlacementResultPacket(packet, out bool accepted, out long tick, out CoopPlacementCommandData parsed));
            Assert.True(accepted);
            Assert.Equal(144, tick);
            Assert.Equal(command.RequestId, parsed.RequestId);
            Assert.Equal(command.ParentId, parsed.ParentId);
        }

        [Fact]
        public void RecycleResultPacketRoundTrips()
        {
            var command = new CoopRecycleCommandData
            {
                SessionId = 42,
                RequestId = 91,
                ObjectId = 314,
                TypeName = "Mine",
                TargetReference = new CoopObjectReference(
                    314, "Mine", 12.5f, 0.25f, -8f, true)
            };

            byte[] packet = CoopProtocol.CreateRecycleResultPacket(true, 177, command);
            Assert.True(CoopProtocol.TryParseRecycleResultPacket(packet, out bool accepted, out long tick, out CoopRecycleCommandData parsed));
            Assert.True(accepted);
            Assert.Equal(177, tick);
            Assert.Equal(command.RequestId, parsed.RequestId);
            Assert.Equal(command.ObjectId, parsed.ObjectId);
            Assert.Equal(command.TypeName, parsed.TypeName);
            Assert.True(parsed.TargetReference.HasPosition);
            Assert.Equal(12.5f, parsed.TargetReference.PositionX);
            Assert.Equal(-8f, parsed.TargetReference.PositionZ);
        }

        [Fact]
        public void RecycleRequestRejectsTruncatedStableTargetReference()
        {
            var command = new CoopRecycleCommandData
            {
                SessionId = 42,
                RequestId = 92,
                ObjectId = 315,
                TypeName = "Module",
                TargetReference = new CoopObjectReference(
                    315, "Module", 1f, 2f, 3f, true)
            };
            byte[] packet = CoopProtocol.CreateRecycleRequestPacket(command);
            System.Array.Resize(ref packet, packet.Length - 1);

            Assert.False(CoopProtocol.TryParseRecycleRequestPacket(packet, out _));
        }

        [Fact]
        public void GameOverPacketsRoundTripAndRejectMalformedFrames()
        {
            byte[] request = CoopProtocol.CreateGameOverRequestPacket(77, 4095);
            Assert.True(CoopProtocol.TryParseGameOverRequestPacket(
                request, out long requestSession, out long requestTick));
            Assert.Equal(77, requestSession);
            Assert.Equal(4095, requestTick);
            Assert.False(CoopProtocol.TryParseGameOverRequestPacket(
                new byte[] { CoopProtocol.PktGameOverRequest }, out _, out _));

            byte[] state = CoopProtocol.CreateGameOverStatePacket(77, 4096);
            Assert.True(CoopProtocol.TryParseGameOverStatePacket(
                state, out long stateSession, out long tick));
            Assert.Equal(77, stateSession);
            Assert.Equal(4096, tick);

            state[1] = 0;
            Assert.False(CoopProtocol.TryParseGameOverStatePacket(state, out _, out _));

            byte[] trailing = new byte[request.Length + 1];
            System.Buffer.BlockCopy(request, 0, trailing, 0, request.Length);
            Assert.False(CoopProtocol.TryParseGameOverRequestPacket(
                trailing, out _, out _));

            request[2] = 0;
            request[3] = 0;
            request[4] = 0;
            request[5] = 0;
            request[6] = 0;
            request[7] = 0;
            request[8] = 0;
            request[9] = 0;
            Assert.False(CoopProtocol.TryParseGameOverRequestPacket(
                request, out _, out _));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, -1)]
        [InlineData(1, 2147483647)]
        public void GameOverPacketsRejectInvalidSessionAndTick(long sessionId, long tick)
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CoopProtocol.CreateGameOverRequestPacket(sessionId, tick));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CoopProtocol.CreateGameOverStatePacket(sessionId, tick));
        }

        [Fact]
        public void TimeStatePacketRoundTripsAndBoundsScale()
        {
            byte[] packet = CoopProtocol.CreateTimeStatePacket(true, 99, CoopTimeActor.Guest);
            Assert.True(CoopProtocol.TryParseTimeStatePacket(packet, out bool paused, out int scaleIndex, out CoopTimeActor actor));
            Assert.True(paused);
            Assert.Equal(2, scaleIndex);
            Assert.Equal(CoopTimeActor.Guest, actor);
            Assert.False(CoopProtocol.TryParseTimeStatePacket(new byte[] { CoopProtocol.PktTimeState, 0, 3, (byte)CoopTimeActor.Host }, out _, out _, out _));
            Assert.False(CoopProtocol.TryParseTimeStatePacket(new byte[] { CoopProtocol.PktTimeState, 0, 1, 99 }, out _, out _, out _));
        }

        [Fact]
        public void TimeStateApplyIsDeferredUntilWorldIsLoaded()
        {
            Assert.False(CoopTimeStatePolicy.ShouldApply(false, true));
            Assert.True(CoopTimeStatePolicy.ShouldApply(true, true));
            Assert.False(CoopTimeStatePolicy.ShouldApply(true, false));
        }

        [Theory]
        [InlineData(CoopTimeOperation.IncreaseSpeed, false, 0, false, 1)]
        [InlineData(CoopTimeOperation.IncreaseSpeed, false, 2, false, 2)]
        [InlineData(CoopTimeOperation.DecreaseSpeed, false, 0, false, 0)]
        [InlineData(CoopTimeOperation.NormalSpeed, false, 2, false, 0)]
        [InlineData(CoopTimeOperation.Pause, false, 1, true, 1)]
        [InlineData(CoopTimeOperation.Unpause, true, 1, false, 1)]
        public void TimeOperationProducesExactAuthoritativeState(
            CoopTimeOperation operation,
            bool paused,
            int scaleIndex,
            bool expectedPaused,
            int expectedScaleIndex)
        {
            CoopTimeStateData state = CoopTimeStateTransition.Apply(
                paused,
                scaleIndex,
                operation,
                CoopTimeActor.Guest);

            Assert.Equal(expectedPaused, state.Paused);
            Assert.Equal(expectedScaleIndex, state.ScaleIndex);
            Assert.Equal(CoopTimeActor.Guest, state.Actor);
        }

        [Fact]
        public void TimeCommandCodecSeparatesRequestsFromCommits()
        {
            byte[] request = CoopTimeCommandCodec.EncodeRequest(CoopTimeOperation.Pause);
            Assert.True(CoopTimeCommandCodec.TryDecodeRequest(
                request,
                out CoopTimeOperation operation));
            Assert.Equal(CoopTimeOperation.Pause, operation);
            Assert.False(CoopTimeCommandCodec.TryDecodeCommit(request, out _));

            var state = new CoopTimeStateData(true, 2, CoopTimeActor.Host);
            byte[] commit = CoopTimeCommandCodec.EncodeCommit(state);
            Assert.True(CoopTimeCommandCodec.TryDecodeCommit(commit, out CoopTimeStateData decoded));
            Assert.True(decoded.Paused);
            Assert.Equal(2, decoded.ScaleIndex);
            Assert.Equal(CoopTimeActor.Host, decoded.Actor);
            Assert.False(CoopTimeCommandCodec.TryDecodeRequest(commit, out _));

            byte[] baseline = CoopTimeCommandCodec.EncodeCommit(
                new CoopTimeStateData(false, 0, CoopTimeActor.System));
            Assert.True(CoopTimeCommandCodec.TryDecodeCommit(
                baseline, out CoopTimeStateData decodedBaseline));
            Assert.Equal(CoopTimeActor.System, decodedBaseline.Actor);

            byte[] operationCommit = CoopTimeCommandCodec.EncodeOperationCommit(
                CoopTimeOperation.IncreaseSpeed,
                CoopTimeActor.Guest);
            Assert.True(CoopTimeCommandCodec.TryDecodeOperationCommit(
                operationCommit,
                out CoopTimeOperation committedOperation,
                out CoopTimeActor committedActor));
            Assert.Equal(CoopTimeOperation.IncreaseSpeed, committedOperation);
            Assert.Equal(CoopTimeActor.Guest, committedActor);
            Assert.False(CoopTimeCommandCodec.TryDecodeCommit(operationCommit, out _));
        }

        [Fact]
        public void TimeCommandCodecRejectsMalformedValuesAndTrailingBytes()
        {
            Assert.False(CoopTimeCommandCodec.TryDecodeRequest(
                new byte[] { 99 }, out _));
            Assert.False(CoopTimeCommandCodec.TryDecodeRequest(
                new byte[] { (byte)CoopTimeOperation.Pause, 0 }, out _));
            Assert.False(CoopTimeCommandCodec.TryDecodeCommit(
                new byte[] { 2, 3, (byte)CoopTimeActor.Host }, out _));
            Assert.False(CoopTimeCommandCodec.TryDecodeCommit(
                new byte[] { 2, 1, 99 }, out _));
        }

        [Fact]
        public void SelectionPacketRoundTripsPrimaryAndLinkTarget()
        {
            var selection = new CoopSelectionData
            {
                Active = true,
                Sequence = 17,
                ObjectId = 42,
                TypeName = "Module",
                HasLinkTarget = true,
                LinkObjectId = 84,
                LinkTypeName = "Module"
            };

            byte[] packet = CoopProtocol.CreateSelectionPacket(selection);

            Assert.True(CoopProtocol.TryParseSelectionPacket(packet, out CoopSelectionData parsed));
            Assert.True(parsed.Active);
            Assert.Equal(17, parsed.Sequence);
            Assert.Equal(42, parsed.ObjectId);
            Assert.Equal("Module", parsed.TypeName);
            Assert.True(parsed.HasLinkTarget);
            Assert.Equal(84, parsed.LinkObjectId);
            Assert.Equal("Module", parsed.LinkTypeName);
        }

        [Fact]
        public void SelectionPacketRejectsOversizedTypeNames()
        {
            var selection = new CoopSelectionData
            {
                Active = true,
                Sequence = 1,
                ObjectId = 1,
                TypeName = new string('A', 129)
            };

            Assert.Throws<System.ArgumentException>(() => CoopProtocol.CreateSelectionPacket(selection));
        }

        [Fact]
        public void SelectionIdentityTreatsLinkPairAsUnordered()
        {
            var first = new CoopSelectionData
            {
                Active = true,
                ObjectId = 101,
                TypeName = "Module",
                HasLinkTarget = true,
                LinkObjectId = 202,
                LinkTypeName = "Module"
            };
            var reversed = new CoopSelectionData
            {
                Active = true,
                ObjectId = 202,
                TypeName = "Module",
                HasLinkTarget = true,
                LinkObjectId = 101,
                LinkTypeName = "Module"
            };

            Assert.True(CoopSelectionIdentity.Same(first, reversed));
        }

        [Fact]
        public void ConnectionRequestAndResultPacketsRoundTrip()
        {
            var command = new CoopConnectionCommandData
            {
                SessionId = 42,
                RequestId = 33,
                FirstModuleId = 101,
                SecondModuleId = 202
            };

            byte[] request = CoopProtocol.CreateConnectionRequestPacket(command);
            Assert.True(CoopProtocol.TryParseConnectionRequestPacket(request, out CoopConnectionCommandData parsedRequest));
            Assert.Equal(33, parsedRequest.RequestId);
            Assert.Equal(101, parsedRequest.FirstModuleId);
            Assert.Equal(202, parsedRequest.SecondModuleId);

            byte[] result = CoopProtocol.CreateConnectionResultPacket(true, 512, command);
            Assert.True(CoopProtocol.TryParseConnectionResultPacket(result, out bool accepted, out long tick, out CoopConnectionCommandData parsedResult));
            Assert.True(accepted);
            Assert.Equal(512, tick);
            Assert.Equal(202, parsedResult.SecondModuleId);
        }

        [Fact]
        public void ConnectionPacketsRejectSelfLinksAndTrailingData()
        {
            var selfLink = new CoopConnectionCommandData
            {
                SessionId = 42,
                RequestId = 1,
                FirstModuleId = 9,
                SecondModuleId = 9
            };
            Assert.Throws<System.ArgumentException>(() => CoopProtocol.CreateConnectionRequestPacket(selfLink));

            var valid = new CoopConnectionCommandData
            {
                SessionId = 42,
                RequestId = 2,
                FirstModuleId = 9,
                SecondModuleId = 10
            };
            byte[] packet = CoopProtocol.CreateConnectionRequestPacket(valid);
            System.Array.Resize(ref packet, packet.Length + 1);
            Assert.False(CoopProtocol.TryParseConnectionRequestPacket(packet, out _));
        }

        [Fact]
        public void InputIsBlockedWhenUnfocusedInFullMode()
        {
            Assert.False(CoopInputPolicy.IsInputAllowed(true, false));
            Assert.True(CoopInputPolicy.IsInputAllowed(true, true));
            Assert.True(CoopInputPolicy.IsInputAllowed(false, false));
        }

        [Fact]
        public void PlacementRequestIdsAreUniqueAcrossRoles()
        {
            Assert.Equal(2, CoopPlacementRequestIds.For(CoopRole.Host, 1));
            Assert.Equal(3, CoopPlacementRequestIds.For(CoopRole.Guest, 1));
            Assert.NotEqual(
                CoopPlacementRequestIds.For(CoopRole.Host, 42),
                CoopPlacementRequestIds.For(CoopRole.Guest, 42));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => CoopPlacementRequestIds.For(CoopRole.Guest, 0));
        }

        [Fact]
        public void DisasterSeedsAreStableAndSeparatedByManager()
        {
            Assert.Equal(
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.MeteorManager),
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.MeteorManager));
            // Sonradan olusan InteractionWork'ler: ayni sira -> ayni seed,
            // farkli sira -> farkli zar akisi (tum isler ayni anda kaza gecirmez).
            Assert.Equal(
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.InteractionWork, 7),
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.InteractionWork, 7));
            Assert.NotEqual(
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.InteractionWork, 1),
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.InteractionWork, 2));
            // Sebeke dengesi yalniz ortak tick'e baglidir; yerel istek fazi kaydiramaz.
            Assert.True(CoopGridBalancePolicy.ShouldRecalculate(200));
            Assert.False(CoopGridBalancePolicy.ShouldRecalculate(201));
            // Ruzgar tick'e bagli kendi akisini kullanir; baska sistemin Random
            // tuketimi iki peer'de farkli olsa da ayni tick ayni seed'i verir.
            Assert.Equal(
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.Wind, 122880),
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.Wind, 122880));
            Assert.NotEqual(
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.Wind, 122880),
                CoopDeterministicSeeds.ForSequence(CoopDeterministicSeeds.Wind, 122881));
            Assert.NotEqual(
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.MeteorManager),
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.ThunderstormManager));
            Assert.NotEqual(
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.MeteorManager),
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.NameGenerator));
        }
    }
}
