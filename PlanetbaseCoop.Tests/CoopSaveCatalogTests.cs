using System;
using System.IO;
using PlanetbaseCoop;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopSaveCatalogTests : IDisposable
    {
        private readonly string mDirectory = Path.Combine(Path.GetTempPath(),
            "planetbase-coop-save-catalog-" + Guid.NewGuid().ToString("N"));

        public CoopSaveCatalogTests()
        {
            Directory.CreateDirectory(mDirectory);
        }

        [Fact]
        public void ResolvePath_LoadsSelectedSaveEvenWhenAnExtensionlessFileHasTheSameName()
        {
            File.WriteAllText(Path.Combine(mDirectory, "save10"), "not a save");
            string selected = Path.Combine(mDirectory, "save10.sav");
            File.WriteAllText(selected, "save data");

            Assert.Equal(selected, CoopSaveCatalog.ResolvePath(mDirectory, "save10"));
        }

        [Fact]
        public void ResolvePath_DoesNotLoadAScreenshotIfTheSelectedSaveWasDeleted()
        {
            File.WriteAllText(Path.Combine(mDirectory, "save10.png"), "screenshot");

            Assert.Equal(string.Empty, CoopSaveCatalog.ResolvePath(mDirectory, "save10"));
        }

        [Theory]
        [InlineData("coop_recv")]
        [InlineData("COOP_WORLD")]
        [InlineData("oos_host_1024")]
        [InlineData("../save10")]
        public void ResolvePath_RejectsInternalSnapshotsAndPaths(string name)
        {
            if (!name.Contains("/"))
                File.WriteAllText(Path.Combine(mDirectory, name + ".sav"), "internal");

            Assert.Equal(string.Empty, CoopSaveCatalog.ResolvePath(mDirectory, name));
        }

        [Fact]
        public void SaveCatalog_DoesNotOfferANameThatCannotBeSentToTheGuest()
        {
            string name = new string('\u00fc', 49);
            File.WriteAllText(Path.Combine(mDirectory, name + ".sav"), "save data");

            Assert.Empty(CoopSaveCatalog.List(mDirectory));
            Assert.False(CoopLobbyPolicy.IsValidSelection(new CoopLobbySelection
            {
                Mode = CoopLobbyMode.Load,
                PlanetIndex = 0,
                SaveName = name
            }, hasAnySave: true));
        }

        [Fact]
        public void SaveCatalog_ListsOnlyNamesThatResolvePathCanLoad()
        {
            File.WriteAllText(Path.Combine(mDirectory, "backup.save"), "not a save");
            File.WriteAllText(Path.Combine(mDirectory, "coop_recv.sav"), "internal");
            File.WriteAllText(Path.Combine(mDirectory, "save3.sav"), "save data");

            CoopSaveEntry[] entries = CoopSaveCatalog.List(mDirectory);

            Assert.Single(entries);
            Assert.Equal("save3", entries[0].SaveName);
            Assert.Equal(Path.Combine(mDirectory, "save3.sav"),
                CoopSaveCatalog.ResolvePath(mDirectory, entries[0].SaveName));
        }

        [Fact]
        public void SavePacket_RejectsInvalidUtf8InsteadOfChangingTheChosenName()
        {
            byte[] packet = CoopLobbyProtocol.CreateStatePacket(
                CoopLobbyProtocol.PktLobbyState,
                new CoopLobbySelection
                {
                    Mode = CoopLobbyMode.Load,
                    PlanetIndex = 0,
                    SaveName = "a"
                });
            packet[packet.Length - 1] = 0xff;

            Assert.False(CoopLobbyProtocol.TryParseStatePacket(packet, out _));
            Assert.False(CoopLobbyProtocol.TryEncodeSaveName("\ud800", out _));
        }

        public void Dispose()
        {
            Directory.Delete(mDirectory, true);
        }
    }
}
