using System;
using System.IO;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopLobbyNavigationTests
    {
        [Theory]
        [InlineData(100, 100, false)]
        [InlineData(99, 100, false)]
        [InlineData(101, 100, true)]
        public void UiInputGuard_BlocksClickThroughUntilNextFrame(
            int currentFrame,
            int blockedThroughFrame,
            bool expected)
        {
            Assert.Equal(expected, CoopLobbyPolicy.CanAcceptUiInput(
                currentFrame, blockedThroughFrame));
        }

        [Theory]
        [InlineData("host", true, true, false, CoopLobbyPhase.Selecting, true)]
        [InlineData("guest", true, true, false, CoopLobbyPhase.Selecting, true)]
        [InlineData("host", true, true, false, CoopLobbyPhase.Waiting, false)]
        [InlineData("guest", true, true, false, CoopLobbyPhase.Waiting, false)]
        [InlineData("host", false, true, false, CoopLobbyPhase.Selecting, false)]
        [InlineData("host", true, false, false, CoopLobbyPhase.Selecting, false)]
        [InlineData("host", true, true, true, CoopLobbyPhase.WorldCommitted, false)]
        public void ShouldOpenVanillaNewGameSelection_ReturnsExpected(
            string role,
            bool sessionActive,
            bool lobbyActive,
            bool lobbyStarted,
            CoopLobbyPhase phase,
            bool expected)
        {
            bool actual = CoopLobbyPolicy.ShouldOpenVanillaNewGameSelection(
                role,
                sessionActive,
                lobbyActive,
                lobbyStarted,
                phase);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("host", CoopLobbyPhase.Waiting, true, true, true)]
        [InlineData("host", CoopLobbyPhase.Waiting, false, true, false)]
        [InlineData("host", CoopLobbyPhase.Waiting, true, false, false)]
        [InlineData("guest", CoopLobbyPhase.Waiting, true, true, false)]
        [InlineData("host", CoopLobbyPhase.Selecting, true, true, false)]
        public void CanEnterSelection_RequiresReadyConnectedGuest(
            string role,
            CoopLobbyPhase phase,
            bool connected,
            bool ready,
            bool expected)
        {
            Assert.Equal(expected, CoopLobbyPolicy.CanEnterSelection(
                role, phase, connected, ready));
        }

        [Theory]
        [InlineData("host", CoopSelectionMutationOrigin.LocalInput, true)]
        [InlineData("host", CoopSelectionMutationOrigin.RemoteHost, false)]
        [InlineData("guest", CoopSelectionMutationOrigin.LocalInput, false)]
        [InlineData("guest", CoopSelectionMutationOrigin.RemoteHost, true)]
        public void CanMutateSelection_EnforcesHostAuthority(
            string role,
            CoopSelectionMutationOrigin origin,
            bool expected)
        {
            Assert.Equal(expected, CoopLobbyPolicy.CanMutateSelection(
                role, origin, CoopLobbyPhase.Selecting));
        }

        [Fact]
        public void CanCommitWorld_RequiresLoadedGuestAndLocationStage()
        {
            var planetView = ValidPlanetView(revision: 1);
            var locationView = ValidLocationView(revision: 2);

            Assert.False(CoopLobbyPolicy.CanCommitWorld(
                "host", CoopLobbyPhase.Selecting, true, false, planetView));
            Assert.False(CoopLobbyPolicy.CanCommitWorld(
                "host", CoopLobbyPhase.Selecting, true, true, planetView));
            Assert.False(CoopLobbyPolicy.CanCommitWorld(
                "host", CoopLobbyPhase.Selecting, false, true, locationView));
            Assert.False(CoopLobbyPolicy.CanCommitWorld(
                "guest", CoopLobbyPhase.Selecting, true, true, locationView));
            Assert.True(CoopLobbyPolicy.CanCommitWorld(
                "host", CoopLobbyPhase.Selecting, true, true, locationView));
        }

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(2, 1, false)]
        [InlineData(2, 2, true)]
        [InlineData(2, 3, false)]
        public void SelectionScreenReady_RequiresExactCurrentRevision(
            long currentRevision,
            long loadedRevision,
            bool expected)
        {
            Assert.Equal(expected, CoopLobbyPolicy.IsSelectionScreenReady(
                currentRevision, loadedRevision));
        }

        [Fact]
        public void GuestWorldCommit_RequiresSelectingPhaseAndMatchingAppliedView()
        {
            CoopLobbySelectionView view = ValidLocationView(revision: 8);
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = view.PlanetIndex,
                Seed = view.Seed,
                SaveName = string.Empty
            };

            Assert.True(CoopLobbyPolicy.CanAcceptWorldCommit(
                "guest", true, false, CoopLobbyPhase.Selecting,
                selection, view, appliedRevision: 8));
            Assert.False(CoopLobbyPolicy.CanAcceptWorldCommit(
                "guest", true, false, CoopLobbyPhase.Waiting,
                selection, view, appliedRevision: 8));
            Assert.False(CoopLobbyPolicy.CanAcceptWorldCommit(
                "guest", true, false, CoopLobbyPhase.Selecting,
                selection, view, appliedRevision: 7));

            selection.Seed++;
            Assert.False(CoopLobbyPolicy.CanAcceptWorldCommit(
                "guest", true, false, CoopLobbyPhase.Selecting,
                selection, view, appliedRevision: 8));
        }

        [Theory]
        [InlineData(CoopLobbyMode.NewGame)]
        [InlineData(CoopLobbyMode.Continue)]
        [InlineData(CoopLobbyMode.Load)]
        public void GuestWorldCommit_WaitsForHostSnapshotInsteadOfCreatingProvisionalWorld(
            CoopLobbyMode mode)
        {
            Assert.True(CoopLobbyPolicy.ShouldWaitForAuthoritativeSnapshot(
                "guest", mode));
            Assert.False(CoopLobbyPolicy.ShouldWaitForAuthoritativeSnapshot(
                "host", mode));
        }

        [Fact]
        public void SelectionViewChanges_DoNotResetWaitingLobbyReady()
        {
            Assert.False(CoopLobbyPolicy.ShouldResetGuestReady(
                CoopLobbyPhase.Selecting,
                ValidPlanetView(revision: 1),
                ValidLocationView(revision: 2)));
        }

        [Fact]
        public void WaitingLobbyAndVanillaSelectionRenderInSeparatePhases()
        {
            Assert.True(CoopLobbyPolicy.ShouldRenderWaitingLobby(CoopLobbyPhase.Waiting));
            Assert.False(CoopLobbyPolicy.ShouldRenderVanillaSelection(CoopLobbyPhase.Waiting));
            Assert.False(CoopLobbyPolicy.ShouldRenderWaitingLobby(CoopLobbyPhase.Selecting));
            Assert.True(CoopLobbyPolicy.ShouldRenderVanillaSelection(CoopLobbyPhase.Selecting));
        }

        [Theory]
        [InlineData(false, false, false, CoopLobbyPhase.Waiting, false)]
        [InlineData(true, false, false, CoopLobbyPhase.Waiting, true)]
        [InlineData(false, true, true, CoopLobbyPhase.Waiting, true)]
        [InlineData(false, true, true, CoopLobbyPhase.Selecting, false)]
        public void TitleMenuSuppression_CoversTheWholeCustomCoopScreen(
            bool customMenuOpen,
            bool sessionActive,
            bool lobbyActive,
            CoopLobbyPhase phase,
            bool expected)
        {
            Assert.Equal(expected, CoopLobbyPolicy.ShouldSuppressTitleMenu(
                customMenuOpen, sessionActive, lobbyActive, phase));
        }

        [Fact]
        public void SelectionViewPacket_RoundTripsPlanetAndLocationStages()
        {
            foreach (CoopLobbySelectionView expected in new[]
            {
                ValidPlanetView(revision: 7),
                ValidLocationView(revision: 8)
            })
            {
                byte[] packet = CoopLobbyProtocol.CreateSelectionViewPacket(expected);

                Assert.True(CoopLobbyProtocol.TryParseSelectionViewPacket(
                    packet, out CoopLobbySelectionView actual));
                Assert.Equal(expected.Revision, actual.Revision);
                Assert.Equal(expected.Stage, actual.Stage);
                Assert.Equal(expected.PlanetIndex, actual.PlanetIndex);
                Assert.Equal(expected.Latitude, actual.Latitude);
                Assert.Equal(expected.Longitude, actual.Longitude);
                Assert.Equal(expected.Seed, actual.Seed);
                Assert.Equal(expected.BaseName, actual.BaseName);
            }
        }

        [Fact]
        public void SelectionControlPackets_RequireExactShape()
        {
            Assert.Equal(
                new byte[] { CoopLobbyProtocol.PktLobbyEnterSelection },
                CoopLobbyProtocol.CreateEnterSelectionPacket());
            Assert.True(CoopLobbyProtocol.IsEnterSelectionPacket(
                new byte[] { CoopLobbyProtocol.PktLobbyEnterSelection }));
            Assert.False(CoopLobbyProtocol.IsEnterSelectionPacket(
                new byte[] { CoopLobbyProtocol.PktLobbyEnterSelection, 0 }));

            byte[] loaded = CoopLobbyProtocol.CreateSelectionLoadedPacket(42);
            Assert.True(CoopLobbyProtocol.TryParseSelectionLoadedPacket(
                loaded, out long revision));
            Assert.Equal(42, revision);
            Assert.False(CoopLobbyProtocol.TryParseSelectionLoadedPacket(
                new byte[] { CoopLobbyProtocol.PktLobbySelectionLoaded }, out _));
        }

        [Theory]
        [InlineData(CoopLobbyProtocol.PktLobbyEnterSelection, 1)]
        [InlineData(CoopLobbyProtocol.PktLobbySelectionView, 25)]
        [InlineData(CoopLobbyProtocol.PktLobbySelectionLoaded, 9)]
        public void FrameLengthPolicy_AcceptsNewLobbyPacketTypes(
            byte packetType,
            int packetLength)
        {
            Assert.True(CoopProtocol.IsValidFrameLengthForType(
                packetType, packetLength));
        }

        [Theory]
        [InlineData(CoopSelectionStage.Planet, 0, 0f, 0f, 0, "")]
        [InlineData(CoopSelectionStage.Location, -1, 0f, 0f, 0, "Base D1")]
        [InlineData(CoopSelectionStage.Location, 4, 0f, 0f, 0, "Base D1")]
        [InlineData(CoopSelectionStage.Location, 0, -61f, 0f, 0, "Base D1")]
        [InlineData(CoopSelectionStage.Location, 0, 0f, 181f, 181000, "Base D1")]
        [InlineData(CoopSelectionStage.Location, 0, 10f, 12f, 999, "Base D1")]
        [InlineData(CoopSelectionStage.Location, 0, 10f, 12f, 12010, "")]
        public void SelectionViewPacket_RejectsInvalidView(
            CoopSelectionStage stage,
            int planetIndex,
            float latitude,
            float longitude,
            int seed,
            string baseName)
        {
            var view = new CoopLobbySelectionView
            {
                Revision = 1,
                Stage = stage,
                PlanetIndex = planetIndex,
                Latitude = latitude,
                Longitude = longitude,
                Seed = seed,
                BaseName = baseName
            };

            Assert.Throws<System.ArgumentException>(() =>
                CoopLobbyProtocol.CreateSelectionViewPacket(view));
        }

        [Fact]
        public void SelectionViewTracker_RejectsDuplicateAndStaleRevisions()
        {
            var tracker = new CoopLobbySelectionViewTracker();

            Assert.True(tracker.TryAccept(ValidPlanetView(revision: 2)));
            Assert.False(tracker.TryAccept(ValidPlanetView(revision: 2)));
            Assert.False(tracker.TryAccept(ValidPlanetView(revision: 1)));
            Assert.True(tracker.TryAccept(ValidLocationView(revision: 3)));
            Assert.Equal(3, tracker.Current.Revision);
        }

        [Theory]
        [InlineData(145023)]
        [InlineData(-145023)]
        public void VanillaNewGameSelection_IsAValidLobbyStartSelection(int seed)
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = 2,
                Seed = seed,
                SaveName = string.Empty
            };

            Assert.True(CoopLobbyPolicy.IsValidSelection(selection, hasAnySave: false));
        }

        [Fact]
        public void ResolveNewGameSeed_PreservesExplicitSignedVanillaSeed()
        {
            int resolved = CoopNewGameSeedPolicy.Resolve(
                hasExplicitMenuSeed: true,
                configuredSeed: -145023,
                randomSeed: 777);

            Assert.Equal(-145023, resolved);
        }

        [Fact]
        public void ResolveNewGameSeed_UsesFreshRandomValueWithoutExplicitSeed()
        {
            int first = CoopNewGameSeedPolicy.Resolve(
                hasExplicitMenuSeed: false,
                configuredSeed: -1,
                randomSeed: 777);
            int second = CoopNewGameSeedPolicy.Resolve(
                hasExplicitMenuSeed: false,
                configuredSeed: -1,
                randomSeed: 888);

            Assert.Equal(777, first);
            Assert.Equal(888, second);
        }

        [Theory]
        [InlineData(CoopLobbyProtocol.PktLobbyState)]
        [InlineData(CoopLobbyProtocol.PktLobbyStart)]
        public void LobbyPacket_RoundTripsSignedVanillaSeed(byte packetType)
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = 1,
                Seed = -179060,
                SaveName = string.Empty
            };

            byte[] packet = CoopLobbyProtocol.CreateStatePacket(packetType, selection);

            Assert.True(CoopLobbyProtocol.TryParseStatePacket(packet, out var parsed));
            Assert.Equal(selection.PlanetIndex, parsed.PlanetIndex);
            Assert.Equal(selection.Seed, parsed.Seed);
        }

        [Fact]
        public void WorldCommitPacket_BindsSelectionToExactViewRevision()
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = 2,
                Seed = -145023,
                SaveName = string.Empty
            };

            byte[] packet = CoopLobbyProtocol.CreateWorldCommitPacket(
                selection, viewRevision: 42);

            Assert.True(CoopLobbyProtocol.TryParseWorldCommitPacket(
                packet, out CoopLobbySelection parsed, out long revision));
            Assert.Equal(42, revision);
            Assert.Equal(selection.PlanetIndex, parsed.PlanetIndex);
            Assert.Equal(selection.Seed, parsed.Seed);

            packet[1] = 0;
            packet[2] = 0;
            packet[3] = 0;
            packet[4] = 0;
            packet[5] = 0;
            packet[6] = 0;
            packet[7] = 0;
            packet[8] = 0;
            Assert.False(CoopLobbyProtocol.TryParseWorldCommitPacket(
                packet, out _, out _));
        }

        [Fact]
        public void SavedGameCommit_UsesZeroRevisionAndRoundTripsSaveName()
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.Load,
                PlanetIndex = 0,
                Seed = 0,
                SaveName = "save10"
            };

            byte[] packet = CoopLobbyProtocol.CreateWorldCommitPacket(selection, 0);

            Assert.True(CoopLobbyProtocol.TryParseWorldCommitPacket(
                packet, out CoopLobbySelection parsed, out long revision));
            Assert.Equal(0, revision);
            Assert.Equal(CoopLobbyMode.Load, parsed.Mode);
            Assert.Equal("save10", parsed.SaveName);
            Assert.True(CoopLobbyPolicy.IsCommitRevisionValid(
                parsed, revision, currentViewRevision: 0));
        }

        [Fact]
        public void NewGameCommit_RejectsZeroRevision()
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = 0,
                Seed = 42,
                SaveName = string.Empty
            };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CoopLobbyProtocol.CreateWorldCommitPacket(selection, 0));
        }

        [Fact]
        public void SavedGameCommit_IsAcceptedFromWaitingLobbyWithoutVanillaSelection()
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.Load,
                PlanetIndex = 0,
                Seed = 0,
                SaveName = "save10"
            };

            Assert.True(CoopLobbyPolicy.CanAcceptWorldCommit(
                "guest", true, false, CoopLobbyPhase.Waiting,
                selection, default(CoopLobbySelectionView), appliedRevision: 0));
            Assert.False(CoopLobbyPolicy.CanAcceptWorldCommit(
                "guest", true, false, CoopLobbyPhase.Selecting,
                selection, default(CoopLobbySelectionView), appliedRevision: 0));
        }

        [Fact]
        public void SaveCatalog_OrdersNewestFirstAndHidesCoopArtifacts()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "planetbase-coop-saves-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string older = Path.Combine(directory, "save1.sav");
                string newer = Path.Combine(directory, "auto1.sav");
                File.WriteAllText(older, "old");
                File.WriteAllText(newer, "new");
                File.WriteAllText(Path.Combine(directory, "coop_recv.sav"), "internal");
                File.WriteAllText(Path.Combine(directory, "oos_host_512.sav"), "debug");
                File.WriteAllText(Path.Combine(directory, "notes.txt"), "ignore");
                File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

                CoopSaveEntry[] entries = CoopSaveCatalog.List(directory);

                Assert.Equal(new[] { "auto1", "save1" },
                    Array.ConvertAll(entries, entry => entry.SaveName));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(4)]
        [InlineData(255)]
        public void LobbyPacket_RejectsUnsupportedPlanetIndex(int planetIndex)
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = planetIndex,
                Seed = 42,
                SaveName = string.Empty
            };

            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CoopLobbyProtocol.CreateStatePacket(
                    CoopLobbyProtocol.PktLobbyState,
                    selection));

            byte[] packet = CoopLobbyProtocol.CreateStatePacket(
                CoopLobbyProtocol.PktLobbyState,
                new CoopLobbySelection
                {
                    Mode = CoopLobbyMode.NewGame,
                    PlanetIndex = 0,
                    Seed = 42,
                    SaveName = string.Empty
                });
            packet[2] = unchecked((byte)planetIndex);

            Assert.False(CoopLobbyProtocol.TryParseStatePacket(packet, out _));
        }

        [Fact]
        public void SameVanillaSelection_DoesNotResetGuestReady()
        {
            var selection = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = 0,
                Seed = -92031,
                SaveName = string.Empty
            };

            Assert.False(CoopLobbyPolicy.ShouldResetGuestReady(selection, selection));
        }

        [Fact]
        public void ChangedVanillaLocation_ResetsGuestReady()
        {
            var previous = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = 0,
                Seed = -92031,
                SaveName = string.Empty
            };
            var current = previous;
            current.Seed = -92030;

            Assert.True(CoopLobbyPolicy.ShouldResetGuestReady(previous, current));
        }

        [Fact]
        public void ChangingFromNewGameToSave_ResetsGuestReady()
        {
            var previous = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.NewGame,
                PlanetIndex = 0,
                Seed = 0,
                SaveName = string.Empty
            };
            var current = new CoopLobbySelection
            {
                Mode = CoopLobbyMode.Load,
                PlanetIndex = 0,
                Seed = 0,
                SaveName = "save10"
            };

            Assert.True(CoopLobbyPolicy.ShouldResetGuestReady(previous, current));
        }

        private static CoopLobbySelectionView ValidPlanetView(long revision)
        {
            return new CoopLobbySelectionView
            {
                Revision = revision,
                Stage = CoopSelectionStage.Planet,
                PlanetIndex = -1,
                Latitude = 0f,
                Longitude = 0f,
                Seed = 0,
                BaseName = string.Empty
            };
        }

        private static CoopLobbySelectionView ValidLocationView(long revision)
        {
            return new CoopLobbySelectionView
            {
                Revision = revision,
                Stage = CoopSelectionStage.Location,
                PlanetIndex = 0,
                Latitude = -11.25f,
                Longitude = 121.75f,
                Seed = 120988,
                BaseName = "Üs D1"
            };
        }
    }
}
