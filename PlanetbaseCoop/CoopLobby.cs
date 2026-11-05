using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlanetbaseCoop
{
    /// <summary>Lobi modu: host tek yetkili, misafir yalnizca hazir onayi verir.</summary>
    public enum CoopLobbyMode : byte
    {
        NewGame = 0,
        Continue = 1,
        Load = 2
    }

    public enum CoopLobbyPhase : byte
    {
        Waiting = 0,
        Selecting = 1,
        WorldCommitted = 2
    }

    public enum CoopSelectionStage : byte
    {
        Planet = 0,
        Location = 1
    }

    public enum CoopSelectionMutationOrigin : byte
    {
        LocalInput = 0,
        RemoteHost = 1
    }

    public struct CoopLobbySelection
    {
        public CoopLobbyMode Mode;
        public int PlanetIndex;
        public int Seed;
        public string SaveName;
    }

    public struct CoopLobbySelectionView
    {
        public long Revision;
        public CoopSelectionStage Stage;
        public int PlanetIndex;
        public float Latitude;
        public float Longitude;
        public int Seed;
        public string BaseName;
    }

    public struct CoopSaveEntry
    {
        public string SaveName;
        public DateTime LastWriteTime;
    }

    public static class CoopSaveCatalog
    {
        private const int MaxEntries = 128;
        private const string SaveExtension = ".sav";

        /// <summary>
        /// Katalogun sundugu adi yalnizca ayni klasordeki "ad.sav" dosyasina
        /// cozer. Uzantisiz dosya, ekran goruntusu veya co-op ic snapshot'i
        /// asla kayit olarak yuklenmez; bulunamazsa bos dondurur.
        /// </summary>
        public static string ResolvePath(string folder, string saveName)
        {
            if (string.IsNullOrWhiteSpace(folder) ||
                !CoopLobbyProtocol.IsNameSafeForStorage(saveName) ||
                IsInternalArtifact(saveName)) return string.Empty;
            try
            {
                string path = Path.Combine(folder, saveName + SaveExtension);
                return File.Exists(path) ? path : string.Empty;
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
            catch (ArgumentException) { return string.Empty; }
        }

        public static CoopSaveEntry[] List(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return new CoopSaveEntry[0];
            try
            {
                var entries = new List<CoopSaveEntry>();
                FileInfo[] files = new DirectoryInfo(folder).GetFiles("*" + SaveExtension);
                for (int i = 0; i < files.Length; i++)
                {
                    // Windows "*.sav" deseni "*.save" gibi uzantilari da dondurur;
                    // ResolvePath'in yukleyemeyecegi bir ad listelenmemeli.
                    if (!StringComparer.OrdinalIgnoreCase.Equals(
                            files[i].Extension, SaveExtension)) continue;
                    string name = Path.GetFileNameWithoutExtension(files[i].Name);
                    if (!CoopLobbyProtocol.IsNameSafeForStorage(name) ||
                        IsInternalArtifact(name)) continue;
                    entries.Add(new CoopSaveEntry
                    {
                        SaveName = name,
                        LastWriteTime = files[i].LastWriteTime
                    });
                }
                entries.Sort(delegate(CoopSaveEntry left, CoopSaveEntry right)
                {
                    int time = right.LastWriteTime.CompareTo(left.LastWriteTime);
                    return time != 0
                        ? time
                        : StringComparer.OrdinalIgnoreCase.Compare(left.SaveName, right.SaveName);
                });
                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                return entries.ToArray();
            }
            catch (IOException)
            {
                return new CoopSaveEntry[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new CoopSaveEntry[0];
            }
        }

        private static bool IsInternalArtifact(string name)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(name, "coop_recv") ||
                StringComparer.OrdinalIgnoreCase.Equals(name, "coop_world") ||
                name.StartsWith("oos_", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>UI'in thread-safe okuduğu lobi anlık görüntüsü.</summary>
    public struct CoopLobbyInfo
    {
        public string Role;
        public bool SessionActive;
        public bool LobbyActive;
        public bool Started;
        public bool StartSeen;
        public bool Connected;
        public bool ReadyVisible;
        public bool HasAnySave;
        public CoopLobbyPhase Phase;
        public bool SelectionScreenReady;
        public string HostAddress;
        public int Port;
        public CoopLobbySelection Selection;
        public CoopLobbySelectionView SelectionView;
    }

    /// <summary>
    /// Lobi paketleri. PktLobbyState hostun guncel secimini tasir; her degisiklik
    /// misafirin hazir durumunu gecersiz kilar. PktLobbyReady misafir onayidir.
    /// PktLobbyStart hostun nihai karari ve yayinlanan start komutudur.
    /// </summary>
    public static class CoopLobbyProtocol
    {
        public const byte PktLobbyState = 25;
        public const byte PktLobbyReady = 26;
        public const byte PktLobbyStart = 27;
        public const byte PktLobbyEnterSelection = 28;
        public const byte PktLobbySelectionView = 29;
        public const byte PktLobbySelectionLoaded = 30;

        public const int MaxSaveNameBytes = 96;
        public const int MaxBaseNameBytes = 96;
        private const int StateHeaderBytes = 9;
        private const int ReadyPacketBytes = 2;
        private const int SelectionViewHeaderBytes = 25;
        private const int SelectionLoadedPacketBytes = 9;
        private const int WorldCommitRevisionBytes = 8;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool IsLobbyStateType(byte packetType)
        {
            return packetType == PktLobbyState || packetType == PktLobbyStart;
        }

        public static bool TryEncodeSaveName(string saveName, out byte[] encoded)
        {
            encoded = null;
            if (string.IsNullOrEmpty(saveName))
            {
                encoded = new byte[0];
                return true;
            }
            if (saveName.Length > MaxSaveNameBytes) return false;
            try
            {
                // Strict: eslesmemis surrogate '?' ile degistirilip misafire
                // farkli bir ad olarak gitmez, kodlama reddedilir.
                encoded = StrictUtf8.GetBytes(saveName);
                if (encoded.Length <= MaxSaveNameBytes) return true;
                encoded = null;
                return false;
            }
            catch (ArgumentException)
            {
                encoded = null;
                return false;
            }
        }

        public static byte[] CreateStatePacket(byte packetType, in CoopLobbySelection selection)
        {
            if (!IsLobbyStateType(packetType))
                throw new ArgumentException("packet type is not a lobby state", "packetType");
            if (selection.PlanetIndex < 0 || selection.PlanetIndex > 3)
                throw new ArgumentOutOfRangeException(
                    "selection",
                    "planet index is outside the supported range");
            byte[] nameBytes;
            if (!TryEncodeSaveName(selection.SaveName, out nameBytes))
                throw new ArgumentException("save name is invalid", "selection");
            byte[] packet = new byte[StateHeaderBytes + nameBytes.Length];
            packet[0] = packetType;
            packet[1] = (byte)selection.Mode;
            packet[2] = (byte)selection.PlanetIndex;
            Buffer.BlockCopy(BitConverter.GetBytes(selection.Seed), 0, packet, 3, 4);
            packet[7] = (byte)(nameBytes.Length & 0xFF);
            packet[8] = (byte)((nameBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(nameBytes, 0, packet, StateHeaderBytes, nameBytes.Length);
            return packet;
        }

        public static bool TryParseStatePacket(
            byte[] packet, out CoopLobbySelection selection)
        {
            selection = default(CoopLobbySelection);
            if (packet == null || packet.Length < StateHeaderBytes ||
                !IsLobbyStateType(packet[0])) return false;
            if (packet[2] > 3) return false;
            int nameLength = packet[7] | (packet[8] << 8);
            if (nameLength < 0 || nameLength > MaxSaveNameBytes ||
                packet.Length != StateHeaderBytes + nameLength) return false;
            try
            {
                selection.Mode = (CoopLobbyMode)packet[1];
                selection.PlanetIndex = packet[2];
                selection.Seed = BitConverter.ToInt32(packet, 3);
                selection.SaveName = nameLength == 0
                    ? string.Empty
                    : StrictUtf8.GetString(packet, StateHeaderBytes, nameLength);
                return Enum.IsDefined(typeof(CoopLobbyMode), selection.Mode);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static byte[] CreateWorldCommitPacket(
            in CoopLobbySelection selection,
            long viewRevision)
        {
            if ((selection.Mode == CoopLobbyMode.NewGame && viewRevision <= 0) ||
                (selection.Mode != CoopLobbyMode.NewGame && viewRevision != 0))
                throw new ArgumentOutOfRangeException("viewRevision");
            byte[] state = CreateStatePacket(PktLobbyStart, selection);
            byte[] packet = new byte[state.Length + WorldCommitRevisionBytes];
            packet[0] = PktLobbyStart;
            Buffer.BlockCopy(BitConverter.GetBytes(viewRevision), 0, packet, 1, 8);
            Buffer.BlockCopy(state, 1, packet, 9, state.Length - 1);
            return packet;
        }

        public static bool TryParseWorldCommitPacket(
            byte[] packet,
            out CoopLobbySelection selection,
            out long viewRevision)
        {
            selection = default(CoopLobbySelection);
            viewRevision = 0;
            if (packet == null || packet.Length < StateHeaderBytes + WorldCommitRevisionBytes ||
                packet[0] != PktLobbyStart) return false;

            viewRevision = BitConverter.ToInt64(packet, 1);
            byte[] state = new byte[packet.Length - WorldCommitRevisionBytes];
            state[0] = PktLobbyStart;
            Buffer.BlockCopy(packet, 9, state, 1, packet.Length - 9);
            return TryParseStatePacket(state, out selection) &&
                ((selection.Mode == CoopLobbyMode.NewGame && viewRevision > 0) ||
                 (selection.Mode != CoopLobbyMode.NewGame && viewRevision == 0));
        }

        public static byte[] CreateReadyPacket(bool ready)
        {
            return new[] { PktLobbyReady, ready ? (byte)1 : (byte)0 };
        }

        public static bool TryParseReadyPacket(byte[] packet, out bool ready)
        {
            ready = false;
            if (packet == null || packet.Length != ReadyPacketBytes ||
                packet[0] != PktLobbyReady || packet[1] > 1) return false;
            ready = packet[1] == 1;
            return true;
        }

        public static byte[] CreateEnterSelectionPacket()
        {
            return new[] { PktLobbyEnterSelection };
        }

        public static bool IsEnterSelectionPacket(byte[] packet)
        {
            return packet != null && packet.Length == 1 &&
                packet[0] == PktLobbyEnterSelection;
        }

        public static byte[] CreateSelectionViewPacket(
            in CoopLobbySelectionView view)
        {
            byte[] nameBytes;
            if (!IsValidSelectionView(view) ||
                !TryEncodeBaseName(view.BaseName, out nameBytes))
            {
                throw new ArgumentException("selection view is invalid", "view");
            }

            byte[] packet = new byte[SelectionViewHeaderBytes + nameBytes.Length];
            packet[0] = PktLobbySelectionView;
            Buffer.BlockCopy(BitConverter.GetBytes(view.Revision), 0, packet, 1, 8);
            packet[9] = (byte)view.Stage;
            packet[10] = view.PlanetIndex < 0 ? byte.MaxValue : (byte)view.PlanetIndex;
            Buffer.BlockCopy(BitConverter.GetBytes(view.Latitude), 0, packet, 11, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(view.Longitude), 0, packet, 15, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(view.Seed), 0, packet, 19, 4);
            packet[23] = (byte)(nameBytes.Length & 0xFF);
            packet[24] = (byte)((nameBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(nameBytes, 0, packet, SelectionViewHeaderBytes, nameBytes.Length);
            return packet;
        }

        public static bool TryParseSelectionViewPacket(
            byte[] packet,
            out CoopLobbySelectionView view)
        {
            view = default(CoopLobbySelectionView);
            if (packet == null || packet.Length < SelectionViewHeaderBytes ||
                packet[0] != PktLobbySelectionView) return false;
            int nameLength = packet[23] | (packet[24] << 8);
            if (nameLength < 0 || nameLength > MaxBaseNameBytes ||
                packet.Length != SelectionViewHeaderBytes + nameLength) return false;
            try
            {
                view.Revision = BitConverter.ToInt64(packet, 1);
                view.Stage = (CoopSelectionStage)packet[9];
                view.PlanetIndex = packet[10] == byte.MaxValue ? -1 : packet[10];
                view.Latitude = BitConverter.ToSingle(packet, 11);
                view.Longitude = BitConverter.ToSingle(packet, 15);
                view.Seed = BitConverter.ToInt32(packet, 19);
                view.BaseName = nameLength == 0
                    ? string.Empty
                    : StrictUtf8.GetString(packet, SelectionViewHeaderBytes, nameLength);
                return IsValidSelectionView(view);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static byte[] CreateSelectionLoadedPacket(long revision)
        {
            if (revision <= 0) throw new ArgumentOutOfRangeException("revision");
            byte[] packet = new byte[SelectionLoadedPacketBytes];
            packet[0] = PktLobbySelectionLoaded;
            Buffer.BlockCopy(BitConverter.GetBytes(revision), 0, packet, 1, 8);
            return packet;
        }

        public static bool TryParseSelectionLoadedPacket(
            byte[] packet,
            out long revision)
        {
            revision = 0;
            if (packet == null || packet.Length != SelectionLoadedPacketBytes ||
                packet[0] != PktLobbySelectionLoaded) return false;
            revision = BitConverter.ToInt64(packet, 1);
            return revision > 0;
        }

        public static bool IsValidSelectionView(in CoopLobbySelectionView view)
        {
            if (view.Revision <= 0 ||
                !Enum.IsDefined(typeof(CoopSelectionStage), view.Stage) ||
                float.IsNaN(view.Latitude) || float.IsInfinity(view.Latitude) ||
                float.IsNaN(view.Longitude) || float.IsInfinity(view.Longitude))
            {
                return false;
            }

            string baseName = view.BaseName ?? string.Empty;
            if (view.Stage == CoopSelectionStage.Planet)
            {
                return view.PlanetIndex == -1 && view.Latitude == 0f &&
                    view.Longitude == 0f && view.Seed == 0 && baseName.Length == 0;
            }

            if (view.PlanetIndex < 0 || view.PlanetIndex > 3 ||
                view.Latitude < -60f || view.Latitude > 60f ||
                view.Longitude < -180f || view.Longitude > 180f ||
                baseName.Length == 0 ||
                view.Seed != (int)Math.Floor(view.Longitude) * 1000 +
                    (int)Math.Floor(view.Latitude))
            {
                return false;
            }

            byte[] ignored;
            return TryEncodeBaseName(baseName, out ignored);
        }

        private static bool TryEncodeBaseName(string baseName, out byte[] encoded)
        {
            encoded = null;
            if (baseName == null) return false;
            for (int i = 0; i < baseName.Length; i++)
            {
                if (char.IsControl(baseName[i])) return false;
            }
            try
            {
                encoded = StrictUtf8.GetBytes(baseName);
                return encoded.Length <= MaxBaseNameBytes;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static bool IsNameSafeForStorage(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length > MaxSaveNameBytes) return false;
            foreach (char c in name)
            {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|' || c == '.' ||
                    char.IsControl(c)) return false;
            }
            // Karakter sayisi degil, misafire giden UTF-8 bayt sayisi sinirdir.
            byte[] ignored;
            return TryEncodeSaveName(name, out ignored);
        }
    }

    /// <summary>
    /// Host son karari verir: START yalnizca misafir bagli ve hazirken
    /// authorize edilir. Secim her degistiginde misafir hazirligi sifirlanir.
    /// </summary>
    public static class CoopLobbyPolicy
    {
        public static bool CanAcceptUiInput(
            int currentFrame,
            int blockedThroughFrame)
        {
            return currentFrame > blockedThroughFrame;
        }

        public static bool ShouldOpenVanillaNewGameSelection(
            string role,
            bool sessionActive,
            bool lobbyActive,
            bool lobbyStarted,
            CoopLobbyPhase phase)
        {
            bool supportedRole = string.Equals(role, "host", StringComparison.Ordinal) ||
                string.Equals(role, "guest", StringComparison.Ordinal);
            return supportedRole && sessionActive && lobbyActive && !lobbyStarted &&
                phase == CoopLobbyPhase.Selecting;
        }

        public static bool CanEnterSelection(
            string role,
            CoopLobbyPhase phase,
            bool guestConnected,
            bool guestReady)
        {
            return string.Equals(role, "host", StringComparison.Ordinal) &&
                phase == CoopLobbyPhase.Waiting && guestConnected && guestReady;
        }

        public static bool CanMutateSelection(
            string role,
            CoopSelectionMutationOrigin origin,
            CoopLobbyPhase phase)
        {
            if (phase != CoopLobbyPhase.Selecting) return false;
            if (string.Equals(role, "host", StringComparison.Ordinal))
                return origin == CoopSelectionMutationOrigin.LocalInput;
            if (string.Equals(role, "guest", StringComparison.Ordinal))
                return origin == CoopSelectionMutationOrigin.RemoteHost;
            return false;
        }

        public static bool CanCommitWorld(
            string role,
            CoopLobbyPhase phase,
            bool guestConnected,
            bool guestSelectionLoaded,
            in CoopLobbySelectionView view)
        {
            return string.Equals(role, "host", StringComparison.Ordinal) &&
                phase == CoopLobbyPhase.Selecting && guestConnected &&
                guestSelectionLoaded && view.Stage == CoopSelectionStage.Location &&
                CoopLobbyProtocol.IsValidSelectionView(view);
        }

        public static bool IsSelectionScreenReady(
            long currentRevision,
            long loadedRevision)
        {
            return currentRevision > 0 && loadedRevision == currentRevision;
        }

        public static bool CanAcceptWorldCommit(
            string role,
            bool lobbyActive,
            bool lobbyStarted,
            CoopLobbyPhase phase,
            in CoopLobbySelection selection,
            in CoopLobbySelectionView view,
            long appliedRevision)
        {
            if (!string.Equals(role, "guest", StringComparison.Ordinal) ||
                !lobbyActive || lobbyStarted) return false;
            if (selection.Mode != CoopLobbyMode.NewGame)
                return phase == CoopLobbyPhase.Waiting &&
                    IsValidSelection(selection, hasAnySave: true);
            return phase == CoopLobbyPhase.Selecting &&
                IsSelectionScreenReady(view.Revision, appliedRevision) &&
                view.Stage == CoopSelectionStage.Location &&
                selection.PlanetIndex == view.PlanetIndex &&
                selection.Seed == view.Seed &&
                IsValidSelection(selection, hasAnySave: false) &&
                CoopLobbyProtocol.IsValidSelectionView(view);
        }

        public static bool IsCommitRevisionValid(
            in CoopLobbySelection selection,
            long committedRevision,
            long currentViewRevision)
        {
            return selection.Mode == CoopLobbyMode.NewGame
                ? committedRevision > 0 && committedRevision == currentViewRevision
                : committedRevision == 0;
        }

        /// <summary>
        /// Menü co-op'unda guest hiçbir geçici dünya üretmez. Hostun commit'i
        /// yalnız snapshot transferini yetkilendirir; gerçek dünya tek seferde
        /// host snapshot'ından yüklenir.
        /// </summary>
        public static bool ShouldWaitForAuthoritativeSnapshot(
            string role,
            CoopLobbyMode mode)
        {
            return string.Equals(role, "guest", StringComparison.Ordinal) &&
                Enum.IsDefined(typeof(CoopLobbyMode), mode);
        }

        public static bool ShouldRenderWaitingLobby(CoopLobbyPhase phase)
        {
            return phase == CoopLobbyPhase.Waiting;
        }

        public static bool ShouldRenderVanillaSelection(CoopLobbyPhase phase)
        {
            return phase == CoopLobbyPhase.Selecting;
        }

        public static bool ShouldAcceptLobbyReady(CoopLobbyPhase phase)
        {
            return phase == CoopLobbyPhase.Waiting;
        }

        public static bool ShouldSuppressTitleMenu(
            bool customMenuOpen,
            bool sessionActive,
            bool lobbyActive,
            CoopLobbyPhase phase)
        {
            return customMenuOpen ||
                (sessionActive && lobbyActive && phase == CoopLobbyPhase.Waiting);
        }

        public static bool IsValidSelection(
            CoopLobbySelection selection, bool hasAnySave)
        {
            if (selection.PlanetIndex < 0 || selection.PlanetIndex > 3) return false;
            switch (selection.Mode)
            {
                case CoopLobbyMode.NewGame:
                    return string.IsNullOrEmpty(selection.SaveName);
                case CoopLobbyMode.Continue:
                    return hasAnySave && string.IsNullOrEmpty(selection.SaveName);
                case CoopLobbyMode.Load:
                    return hasAnySave &&
                        !string.IsNullOrEmpty(selection.SaveName) &&
                        CoopLobbyProtocol.IsNameSafeForStorage(selection.SaveName);
                default:
                    return false;
            }
        }

        public static bool ShouldResetGuestReady(
            in CoopLobbySelection previous,
            in CoopLobbySelection current)
        {
            return previous.Mode != current.Mode ||
                previous.PlanetIndex != current.PlanetIndex ||
                previous.Seed != current.Seed ||
                !StringComparer.Ordinal.Equals(previous.SaveName, current.SaveName);
        }

        public static bool ShouldResetGuestReady(
            CoopLobbyPhase phase,
            in CoopLobbySelectionView previous,
            in CoopLobbySelectionView current)
        {
            if (phase != CoopLobbyPhase.Waiting) return false;
            return previous.Revision != current.Revision ||
                previous.Stage != current.Stage ||
                previous.PlanetIndex != current.PlanetIndex ||
                previous.Latitude != current.Latitude ||
                previous.Longitude != current.Longitude ||
                previous.Seed != current.Seed ||
                !StringComparer.Ordinal.Equals(previous.BaseName, current.BaseName);
        }

        public static bool CanAuthorizeStart(
            bool lobbyActive,
            bool lobbyAlreadyStarted,
            bool guestConnected,
            bool guestReady,
            bool selectionValid)
        {
            return lobbyActive && !lobbyAlreadyStarted &&
                guestConnected && guestReady && selectionValid;
        }
    }

    public sealed class CoopLobbySelectionViewTracker
    {
        private CoopLobbySelectionView mCurrent;

        public CoopLobbySelectionView Current
        {
            get { return mCurrent; }
        }

        public bool TryAccept(in CoopLobbySelectionView candidate)
        {
            if (!CoopLobbyProtocol.IsValidSelectionView(candidate) ||
                candidate.Revision <= mCurrent.Revision) return false;
            mCurrent = candidate;
            return true;
        }
    }

    public static class CoopNewGameSeedPolicy
    {
        public static int Resolve(
            bool hasExplicitMenuSeed,
            int configuredSeed,
            int randomSeed)
        {
            if (hasExplicitMenuSeed || configuredSeed >= 0) return configuredSeed;
            return randomSeed;
        }
    }
}
