using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlanetbaseCoop
{
    public enum CoopTransportKind
    {
        Tcp,
        Steam
    }

    public enum CoopSteamFragmentResult
    {
        Invalid,
        Pending,
        Complete
    }

    public enum CoopSteamDelivery
    {
        Reliable,
        FastUnreliable
    }

    public static class CoopSteamDeliveryPolicy
    {
        public static CoopSteamDelivery ForPacketType(byte packetType)
        {
            // These packets are snapshots of newer state, not commands. A lost
            // tick/heartbeat is superseded by the next one and must never hold
            // construction, time, disaster, or snapshot traffic behind Steam's
            // reliable head-of-line queue.
            return packetType == CoopProtocol.PktTick ||
                packetType == CoopProtocol.PktHeartbeat
                ? CoopSteamDelivery.FastUnreliable
                : CoopSteamDelivery.Reliable;
        }
    }

    /// <summary>
    /// SteamNetworkingMessages has a 512 KiB send limit. Co-op snapshots may
    /// be larger, so every reliable Steam message carries one bounded fragment.
    /// </summary>
    public static class CoopSteamTransportProtocol
    {
        private const byte Version = 1;
        private const int HeaderBytes = 26;
        private const byte Magic0 = (byte)'P';
        private const byte Magic1 = (byte)'B';
        private const byte Magic2 = (byte)'S';
        private const byte Magic3 = (byte)'T';
        public const int MaxNativeMessageBytes = 480 * 1024;
        public const int MaxChunkPayloadBytes = MaxNativeMessageBytes - HeaderBytes;
        public const int MaxFragments = 64;

        public static IReadOnlyList<byte[]> Fragment(byte[] payload, long transferId)
        {
            if (payload == null || !CoopProtocol.IsValidFrameLength(payload.Length))
                throw new ArgumentException("Steam payload length is invalid", "payload");
            if (transferId <= 0) throw new ArgumentOutOfRangeException("transferId");

            int partCount = (payload.Length + MaxChunkPayloadBytes - 1) /
                MaxChunkPayloadBytes;
            if (partCount <= 0 || partCount > MaxFragments)
                throw new ArgumentException("Steam payload needs too many fragments", "payload");

            var fragments = new List<byte[]>(partCount);
            for (int partIndex = 0; partIndex < partCount; partIndex++)
            {
                int sourceOffset = partIndex * MaxChunkPayloadBytes;
                int chunkLength = Math.Min(MaxChunkPayloadBytes,
                    payload.Length - sourceOffset);
                byte[] fragment = new byte[HeaderBytes + chunkLength];
                fragment[0] = Magic0;
                fragment[1] = Magic1;
                fragment[2] = Magic2;
                fragment[3] = Magic3;
                fragment[4] = Version;
                fragment[5] = 0;
                Buffer.BlockCopy(BitConverter.GetBytes(transferId), 0, fragment, 6, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(partIndex), 0, fragment, 14, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(partCount), 0, fragment, 18, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, fragment, 22, 4);
                Buffer.BlockCopy(payload, sourceOffset, fragment, HeaderBytes, chunkLength);
                fragments.Add(fragment);
            }
            return fragments;
        }

        internal static bool TryReadHeader(
            byte[] fragment,
            out long transferId,
            out int partIndex,
            out int partCount,
            out int totalLength,
            out int chunkLength)
        {
            transferId = 0;
            partIndex = 0;
            partCount = 0;
            totalLength = 0;
            chunkLength = 0;
            if (fragment == null || fragment.Length <= HeaderBytes ||
                fragment.Length > MaxNativeMessageBytes ||
                fragment[0] != Magic0 || fragment[1] != Magic1 ||
                fragment[2] != Magic2 || fragment[3] != Magic3 ||
                fragment[4] != Version || fragment[5] != 0) return false;

            transferId = BitConverter.ToInt64(fragment, 6);
            partIndex = BitConverter.ToInt32(fragment, 14);
            partCount = BitConverter.ToInt32(fragment, 18);
            totalLength = BitConverter.ToInt32(fragment, 22);
            chunkLength = fragment.Length - HeaderBytes;
            if (transferId <= 0 || partCount <= 0 || partCount > MaxFragments ||
                partIndex < 0 || partIndex >= partCount ||
                !CoopProtocol.IsValidFrameLength(totalLength)) return false;

            int expectedPartCount = (totalLength + MaxChunkPayloadBytes - 1) /
                MaxChunkPayloadBytes;
            int expectedChunkLength = partIndex + 1 < partCount
                ? MaxChunkPayloadBytes
                : totalLength - MaxChunkPayloadBytes * (partCount - 1);
            return partCount == expectedPartCount &&
                chunkLength == expectedChunkLength;
        }

        internal static int HeaderLength
        {
            get { return HeaderBytes; }
        }
    }

    public sealed class CoopSteamFragmentAssembler
    {
        private long mTransferId;
        private int mPartCount;
        private int mNextPart;
        private byte[] mPayload;

        public CoopSteamFragmentResult Accept(byte[] fragment, out byte[] payload)
        {
            payload = null;
            long transferId;
            int partIndex;
            int partCount;
            int totalLength;
            int chunkLength;
            if (!CoopSteamTransportProtocol.TryReadHeader(
                    fragment, out transferId, out partIndex, out partCount,
                    out totalLength, out chunkLength))
            {
                Reset();
                return CoopSteamFragmentResult.Invalid;
            }

            if (partIndex == 0)
            {
                if (mPayload != null)
                {
                    Reset();
                    return CoopSteamFragmentResult.Invalid;
                }
                mTransferId = transferId;
                mPartCount = partCount;
                mNextPart = 0;
                mPayload = new byte[totalLength];
            }

            if (mPayload == null || transferId != mTransferId ||
                partCount != mPartCount || partIndex != mNextPart ||
                totalLength != mPayload.Length)
            {
                Reset();
                return CoopSteamFragmentResult.Invalid;
            }

            Buffer.BlockCopy(fragment, CoopSteamTransportProtocol.HeaderLength,
                mPayload, partIndex * CoopSteamTransportProtocol.MaxChunkPayloadBytes,
                chunkLength);
            mNextPart++;
            if (mNextPart < mPartCount) return CoopSteamFragmentResult.Pending;

            payload = mPayload;
            Reset();
            return CoopSteamFragmentResult.Complete;
        }

        public void Reset()
        {
            mTransferId = 0;
            mPartCount = 0;
            mNextPart = 0;
            mPayload = null;
        }
    }

    public static class CoopSteamAppPolicy
    {
        public const uint SpacewarAppId = 480;
        public const uint PlanetbaseAppId = 403190;

        public static uint ResolveAppId(
            string[] commandLineArgs,
            string steamAppId,
            string steamGameId,
            string appIdFile)
        {
            string commandLineAppId = null;
            if (commandLineArgs != null)
            {
                for (int i = 0; i + 1 < commandLineArgs.Length; i++)
                {
                    if (string.Equals(commandLineArgs[i], "-coopSteamAppId",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        commandLineAppId = commandLineArgs[i + 1];
                        break;
                    }
                }
            }

            string[] candidates =
                { commandLineAppId, steamAppId, steamGameId, appIdFile };
            for (int i = 0; i < candidates.Length; i++)
            {
                uint appId;
                if (uint.TryParse(candidates[i], NumberStyles.None,
                        CultureInfo.InvariantCulture, out appId) &&
                    IsSupported(appId))
                    return appId;
            }
            return SpacewarAppId;
        }

        public static bool IsSupported(uint appId)
        {
            return appId == SpacewarAppId || appId == PlanetbaseAppId;
        }

        public static string DisplayName(uint appId)
        {
            return appId == PlanetbaseAppId ? "PLANETBASE" : "SPACEWAR";
        }
    }

    public static class CoopSteamLobbyPolicy
    {

        public static bool IsCompatible(
            string protocol,
            string appId,
            int expectedProtocol,
            uint expectedAppId)
        {
            int actualProtocol;
            uint actualAppId;
            return int.TryParse(protocol, NumberStyles.None,
                       CultureInfo.InvariantCulture, out actualProtocol) &&
                uint.TryParse(appId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out actualAppId) &&
                actualProtocol == expectedProtocol &&
                CoopSteamAppPolicy.IsSupported(expectedAppId) &&
                actualAppId == expectedAppId;
        }
    }

    public static class CoopSteamLifecyclePolicy
    {
        public static bool ShouldAttemptInitialize(bool initialized, bool initializing)
        {
            return !initialized && !initializing;
        }

        /// <summary>
        /// Native Steam Planetbase (403190) Steam API'yi kendi SteamManager'i ile
        /// baslatir ve OnDestroy'da kapatir; mod onu erken kapatmamalidir.
        /// GOG/Spacewar oturumunda Steam API'yi mod baslattigi icin mod kapatir.
        /// </summary>
        public static bool ShouldShutdownSteamApiOnQuit(uint appId)
        {
            return appId != CoopSteamAppPolicy.PlanetbaseAppId;
        }
    }

    public enum CoopSteamInviteAdmission
    {
        IgnoreInvalidLobby,
        RejectActiveSession,
        JoinLobby
    }

    /// <summary>
    /// Pure admission rules shared by the live Steam callbacks and automated
    /// tests. Keeping the invite-to-guest gates here prevents the test suite
    /// from modelling a different join flow than the game actually runs.
    /// </summary>
    public static class CoopSteamJoinPolicy
    {
        public static CoopSteamInviteAdmission DecideInvite(
            ulong lobbyId,
            bool sessionActive,
            bool hasLobby)
        {
            if (lobbyId == 0) return CoopSteamInviteAdmission.IgnoreInvalidLobby;
            if (sessionActive || hasLobby)
                return CoopSteamInviteAdmission.RejectActiveSession;
            return CoopSteamInviteAdmission.JoinLobby;
        }

        public static bool IsExpectedSuccessfulLobbyEnter(
            ulong enteredLobbyId,
            ulong pendingLobbyId,
            ulong activeLobbyId,
            bool creatingLobby,
            uint response,
            uint successResponse)
        {
            return response == successResponse &&
                !creatingLobby &&
                enteredLobbyId != 0 &&
                enteredLobbyId != activeLobbyId &&
                (pendingLobbyId == 0 || enteredLobbyId == pendingLobbyId);
        }

        public static bool TryResolveGuestHost(
            string mod,
            string expectedMod,
            string protocol,
            string appId,
            string hostText,
            ulong ownerSteamId,
            ulong localSteamId,
            int expectedProtocol,
            uint expectedAppId,
            out ulong hostSteamId)
        {
            hostSteamId = 0;
            ulong parsedHostId;
            if (!string.Equals(mod, expectedMod, StringComparison.Ordinal) ||
                !CoopSteamLobbyPolicy.IsCompatible(
                    protocol, appId, expectedProtocol, expectedAppId) ||
                !ulong.TryParse(hostText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out parsedHostId) ||
                parsedHostId == 0 ||
                ownerSteamId != parsedHostId ||
                localSteamId == parsedHostId)
                return false;

            hostSteamId = parsedHostId;
            return true;
        }
    }

    public struct CoopSteamFriendInfo
    {
        public ulong SteamId;
        public string DisplayName;
        public bool Online;
    }

    public static class CoopSteamFriendPolicy
    {
        public const int PageSize = 5;

        public static string NormalizeDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "STEAM ARKADAŞI";
            string normalized = value.Trim()
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
            while (normalized.Contains("  "))
                normalized = normalized.Replace("  ", " ");
            if (normalized.Length > 36)
                normalized = normalized.Substring(0, 35) + "…";
            return normalized;
        }

        public static int PageCount(int friendCount)
        {
            if (friendCount <= 0) return 1;
            return (friendCount + PageSize - 1) / PageSize;
        }

        public static int ClampPage(int page, int friendCount)
        {
            return Math.Max(0, Math.Min(PageCount(friendCount) - 1, page));
        }
    }
}
