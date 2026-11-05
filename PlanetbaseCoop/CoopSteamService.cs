using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Optional Steam lobby/invite and Datagram Relay bridge. Closed GOG tests
    /// use Spacewar 480; Steam-owned Planetbase uses its native App ID 403190.
    /// All Steam callbacks and native send/receive calls run on Unity's main
    /// thread; SyncManager's network thread communicates through bounded queues.
    /// </summary>
    public static class CoopSteamService
    {
        private const int Channel = 48016;
        private const int FastChannel = 48017;
        private const int MaxQueuedMessages = 128;
        private const int MaxQueuedBytes = 32 * 1024 * 1024;
        private const string LobbyModKey = "pbcoop_mod";
        private const string LobbyProtocolKey = "pbcoop_protocol";
        private const string LobbyAppKey = "pbcoop_app";
        private const string LobbyHostKey = "pbcoop_host";
        private const string LobbyModValue = "planetbase-coop";

        private static readonly object QueueLock = new object();
        private struct OutgoingMessage
        {
            public byte[] Fragment;
            public CoopSteamDelivery Delivery;
        }

        private static readonly Queue<OutgoingMessage> Outgoing =
            new Queue<OutgoingMessage>();
        private static readonly Queue<byte[]> Incoming = new Queue<byte[]>();
        private static readonly AutoResetEvent IncomingSignal = new AutoResetEvent(false);
        private static readonly CoopSteamFragmentAssembler Assembler =
            new CoopSteamFragmentAssembler();
        private static readonly CoopSteamFragmentAssembler FastAssembler =
            new CoopSteamFragmentAssembler();
        // PumpTransport can run more than once per rendered frame. Reusing the
        // native pointer buffers prevents that low-latency path from creating
        // avoidable managed garbage and triggering periodic GC hitches.
        private static readonly IntPtr[] IncomingMessages = new IntPtr[32];
        private static readonly IntPtr[] FastIncomingMessages = new IntPtr[32];

        private static Callback<GameLobbyJoinRequested_t> sJoinRequested;
        private static Callback<LobbyCreated_t> sLobbyCreated;
        private static Callback<LobbyEnter_t> sLobbyEntered;
        private static Callback<LobbyDataUpdate_t> sLobbyDataUpdated;
        private static Callback<LobbyChatUpdate_t> sLobbyChatUpdated;
        private static Callback<SteamNetworkingMessagesSessionRequest_t> sSessionRequested;
        private static Callback<SteamNetworkingMessagesSessionFailed_t> sSessionFailed;

        private static bool sInitializing;
        private static bool sInitialized;
        private static bool sCreatingLobby;
        private static ulong sLobbyId;
        private static ulong sExpectedPeerId;
        private static ulong sPendingJoinLobbyId;
        private static long sPendingJoinDeadlineUtcTicks;
        private static long sNextJoinMetadataCheckUtcTicks;
        private static long sTransferId;
        private static int sOutgoingBytes;
        private static int sIncomingBytes;
        private static int sEndRequested;
        private static uint sAppId = CoopSteamAppPolicy.SpacewarAppId;
        private static string sStatus = "STEAM HAZIRLANMADI";
        private static string sFailure = "";

        public static bool Available
        {
            get { return sInitialized; }
        }

        public static bool HasLobby
        {
            get { return sInitialized && Volatile.Read(ref sLobbyId) != 0; }
        }

        public static ulong LobbyId
        {
            get { return Volatile.Read(ref sLobbyId); }
        }

        public static ulong ExpectedPeerId
        {
            get { return Volatile.Read(ref sExpectedPeerId); }
        }

        public static string Status
        {
            get { return sStatus ?? ""; }
        }

        public static uint AppId
        {
            get { return sAppId; }
        }

        public static string AppDisplayName
        {
            get { return CoopSteamAppPolicy.DisplayName(sAppId); }
        }

        public static void Initialize()
        {
            if (!CoopSteamLifecyclePolicy.ShouldAttemptInitialize(
                    sInitialized, sInitializing)) return;
            sInitializing = true;
            bool steamApiInitialized = false;
            try
            {
                sAppId = ResolveConfiguredAppId();
                Environment.SetEnvironmentVariable("SteamAppId",
                    sAppId.ToString(CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable("SteamGameId",
                    sAppId.ToString(CultureInfo.InvariantCulture));
                if (!SteamAPI.Init())
                {
                    sStatus = "STEAM KULLANILAMIYOR: SteamAPI.Init basarisiz";
                    CoopPlugin.Log.LogWarning(
                        "[Coop] Steam baslatilamadi: SteamAPI.Init false dondu");
                    return;
                }
                steamApiInitialized = true;

                uint initializedAppId = SteamUtils.GetAppID().m_AppId;
                if (initializedAppId != sAppId)
                    throw new InvalidOperationException(
                        "Steam App ID uyusmuyor: beklenen=" + sAppId +
                        " gercek=" + initializedAppId);

                sJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
                sLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
                sLobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
                sLobbyDataUpdated = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
                sLobbyChatUpdated = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
                sSessionRequested = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(
                    OnSessionRequested);
                sSessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(
                    OnSessionFailed);
                SteamNetworkingUtils.InitRelayNetworkAccess();
                sInitialized = true;
                sStatus = ReadyStatus();
                CoopPlugin.Log.LogInfo("[Coop] Steam hazir: appId=" + sAppId +
                    " (" + CoopSteamAppPolicy.DisplayName(sAppId) + ") steamId=" +
                    SteamUser.GetSteamID().m_SteamID);
            }
            catch (Exception ex)
            {
                sInitialized = false;
                DisposeCallbacks();
                if (steamApiInitialized)
                {
                    try { SteamAPI.Shutdown(); }
                    catch (Exception shutdownEx)
                    {
                        CoopPlugin.Log.LogWarning(
                            "[Coop] basarisiz Steam init temizleme hatasi: " +
                            shutdownEx.Message);
                    }
                }
                sStatus = "STEAM KULLANILAMIYOR: " + ex.Message;
                CoopPlugin.Log.LogWarning("[Coop] Steam yuklenemedi: " + ex.Message);
            }
            finally
            {
                sInitializing = false;
            }
        }

        public static void Pump()
        {
            if (!sInitialized) return;
            try
            {
                SteamAPI.RunCallbacks();
                if (Interlocked.Exchange(ref sEndRequested, 0) != 0)
                {
                    EndSession();
                    return;
                }
                PumpPendingJoin();
                PumpTransport();
            }
            catch (Exception ex)
            {
                SetFailure("Steam callback/relay hatasi: " + ex.Message);
            }
        }

        /// <summary>
        /// Moves only relay traffic. It is safe to call before/after a fixed
        /// simulation step, avoiding a full-frame delay without repeatedly
        /// running lobby callbacks.
        /// </summary>
        public static void PumpTransport()
        {
            if (!sInitialized) return;
            try
            {
                // Receive first so a tick can release the gate in this same
                // simulation step; then flush any immediate heartbeat reply.
                PumpIncoming(FastChannel, FastAssembler, FastIncomingMessages, true);
                PumpIncoming(Channel, Assembler, IncomingMessages, false);
                PumpOutgoing();
            }
            catch (Exception ex)
            {
                SetFailure("Steam relay pompa hatasi: " + ex.Message);
            }
        }

        public static string CreateFriendsLobby()
        {
            if (!sInitialized) Initialize();
            CoopPlugin.Log.LogInfo("[Coop] Steam host dugmesi tiklandi: initialized=" +
                sInitialized + " status='" + Status + "'");
            if (!sInitialized) return Status;
            if (SyncManager.FullMode || HasLobby || sCreatingLobby)
                return "BİR OTURUM ZATEN AKTİF";
            try
            {
                sCreatingLobby = true;
                sStatus = "STEAM LOBİSİ OLUŞTURULUYOR...";
                SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
                return "";
            }
            catch (Exception ex)
            {
                sCreatingLobby = false;
                sStatus = "STEAM LOBİSİ AÇILAMADI";
                return ex.Message;
            }
        }

        public static string OpenInviteDialog()
        {
            if (!sInitialized || !HasLobby) return "STEAM LOBİSİ HENÜZ HAZIR DEĞİL";
            if (SyncManager.Role != "host") return "DAVETİ YALNIZ HOST AÇABİLİR";
            try
            {
                bool overlayEnabled = SteamUtils.IsOverlayEnabled();
                CoopPlugin.Log.LogInfo("[Coop] Steam overlay daveti istendi: enabled=" +
                    overlayEnabled + " lobby=" + LobbyId);
                if (!overlayEnabled)
                    return "STEAM OVERLAY BU SÜREÇTE ETKİN DEĞİL";
                SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(LobbyId));
                sStatus = "STEAM ARKADAŞ DAVETİ AÇILDI";
                return "";
            }
            catch (Exception ex)
            {
                return "STEAM DAVETİ AÇILAMADI: " + ex.Message;
            }
        }

        public static CoopSteamFriendInfo[] GetFriends(out string error)
        {
            error = "";
            if (!sInitialized)
            {
                error = "STEAM SERVİSİ HAZIR DEĞİL";
                return new CoopSteamFriendInfo[0];
            }
            try
            {
                const EFriendFlags flags = EFriendFlags.k_EFriendFlagImmediate;
                int count = SteamFriends.GetFriendCount(flags);
                if (count < 0)
                {
                    error = "STEAM ARKADAŞ LİSTESİ ALINAMADI";
                    return new CoopSteamFriendInfo[0];
                }
                var friends = new List<CoopSteamFriendInfo>(count);
                ulong localId = SteamUser.GetSteamID().m_SteamID;
                for (int i = 0; i < count; i++)
                {
                    CSteamID friendId = SteamFriends.GetFriendByIndex(i, flags);
                    if (friendId.m_SteamID == 0 || friendId.m_SteamID == localId ||
                        SteamFriends.GetFriendRelationship(friendId) !=
                            EFriendRelationship.k_EFriendRelationshipFriend) continue;
                    EPersonaState personaState =
                        SteamFriends.GetFriendPersonaState(friendId);
                    friends.Add(new CoopSteamFriendInfo
                    {
                        SteamId = friendId.m_SteamID,
                        DisplayName = CoopSteamFriendPolicy.NormalizeDisplayName(
                            SteamFriends.GetFriendPersonaName(friendId)),
                        Online = personaState != EPersonaState.k_EPersonaStateOffline &&
                            personaState != EPersonaState.k_EPersonaStateInvisible
                    });
                }
                friends.Sort((left, right) =>
                {
                    int onlineOrder = right.Online.CompareTo(left.Online);
                    return onlineOrder != 0
                        ? onlineOrder
                        : StringComparer.CurrentCultureIgnoreCase.Compare(
                            left.DisplayName, right.DisplayName);
                });
                CoopPlugin.Log.LogInfo("[Coop] Steam arkadaş listesi alındı: " +
                    friends.Count);
                return friends.ToArray();
            }
            catch (Exception ex)
            {
                error = "STEAM ARKADAŞ LİSTESİ HATASI: " + ex.Message;
                CoopPlugin.Log.LogWarning("[Coop] " + error);
                return new CoopSteamFriendInfo[0];
            }
        }

        public static string InviteFriend(ulong friendSteamId)
        {
            if (!sInitialized || !HasLobby)
                return "STEAM LOBİSİ HENÜZ HAZIR DEĞİL";
            if (SyncManager.Role != "host") return "DAVETİ YALNIZ HOST GÖNDEREBİLİR";
            if (friendSteamId == 0) return "GEÇERSİZ STEAM ARKADAŞI";
            try
            {
                CSteamID friendId = new CSteamID(friendSteamId);
                if (SteamFriends.GetFriendRelationship(friendId) !=
                    EFriendRelationship.k_EFriendRelationshipFriend)
                    return "SEÇİLEN KULLANICI STEAM ARKADAŞIN DEĞİL";
                string friendName = CoopSteamFriendPolicy.NormalizeDisplayName(
                    SteamFriends.GetFriendPersonaName(friendId));
                bool sent = SteamMatchmaking.InviteUserToLobby(
                    new CSteamID(LobbyId), friendId);
                if (!sent)
                {
                    CoopPlugin.Log.LogWarning("[Coop] Steam lobi daveti reddedildi: friend=" +
                        friendSteamId + " lobby=" + LobbyId);
                    return "STEAM DAVETİ GÖNDERİLEMEDİ";
                }
                sStatus = "DAVET GÖNDERİLDİ: " + friendName;
                CoopStatusOverlay.Notify(sStatus);
                CoopPlugin.Log.LogInfo("[Coop] Steam lobi daveti gönderildi: friend=" +
                    friendSteamId + " name='" + friendName + "' lobby=" + LobbyId);
                return "";
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Steam lobi davet hatası: " + ex.Message);
                return "STEAM DAVETİ HATASI: " + ex.Message;
            }
        }

        public static bool QueuePacket(byte[] payload)
        {
            if (!sInitialized || ExpectedPeerId == 0 || payload == null) return false;
            try
            {
                long transferId = Interlocked.Increment(ref sTransferId);
                IReadOnlyList<byte[]> fragments = CoopSteamTransportProtocol.Fragment(
                    payload, transferId);
                CoopSteamDelivery delivery =
                    CoopSteamDeliveryPolicy.ForPacketType(payload[0]);
                // Fast packets must always fit in one native datagram. Falling
                // back to reliable delivery preserves fragmented-message order.
                if (fragments.Count != 1)
                    delivery = CoopSteamDelivery.Reliable;
                int bytes = 0;
                for (int i = 0; i < fragments.Count; i++) bytes += fragments[i].Length;
                lock (QueueLock)
                {
                    if (Outgoing.Count + fragments.Count > MaxQueuedMessages ||
                        sOutgoingBytes > MaxQueuedBytes - bytes)
                    {
                        SetFailure("Steam gonderim kuyrugu doldu");
                        return false;
                    }
                    for (int i = 0; i < fragments.Count; i++)
                        Outgoing.Enqueue(new OutgoingMessage
                        {
                            Fragment = fragments[i],
                            Delivery = delivery
                        });
                    sOutgoingBytes += bytes;
                }
                return true;
            }
            catch (Exception ex)
            {
                SetFailure("Steam paketleme hatasi: " + ex.Message);
                return false;
            }
        }

        public static bool TryReceivePacket(out byte[] payload, int timeoutMilliseconds)
        {
            payload = null;
            if (!sInitialized) return false;
            if (TryDequeueIncoming(out payload)) return true;
            IncomingSignal.WaitOne(Math.Max(0, Math.Min(1000, timeoutMilliseconds)));
            return TryDequeueIncoming(out payload);
        }

        public static bool TryConsumeFailure(out string failure)
        {
            lock (QueueLock)
            {
                failure = sFailure;
                if (string.IsNullOrEmpty(failure)) return false;
                sFailure = "";
                return true;
            }
        }

        public static void ResetPeerForHost()
        {
            if (!sInitialized || SyncManager.Role != "host") return;
            ulong peerId = ExpectedPeerId;
            try
            {
                if (peerId != 0)
                {
                    SteamNetworkingIdentity identity = IdentityFor(peerId);
                    SteamNetworkingMessages.CloseSessionWithUser(ref identity);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Steam peer sifirlama hatasi: " + ex.Message);
            }
            lock (QueueLock)
            {
                Outgoing.Clear();
                Incoming.Clear();
                sOutgoingBytes = 0;
                sIncomingBytes = 0;
            }
            Assembler.Reset();
            FastAssembler.Reset();
            Volatile.Write(ref sExpectedPeerId, 0);
            IncomingSignal.Set();
        }

        public static void EndSession()
        {
            if (!sInitialized) return;
            try
            {
                ulong peerId = ExpectedPeerId;
                if (peerId != 0)
                {
                    SteamNetworkingIdentity identity = IdentityFor(peerId);
                    SteamNetworkingMessages.CloseSessionWithUser(ref identity);
                }
                ulong lobbyId = LobbyId;
                if (lobbyId != 0)
                    SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));
                SteamFriends.ClearRichPresence();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Steam oturumu kapatma hatasi: " + ex.Message);
            }
            finally
            {
                ResetSessionState();
                sStatus = ReadyStatus();
            }
        }

        public static void RequestEndSession()
        {
            if (!sInitialized) return;
            Interlocked.Exchange(ref sEndRequested, 1);
            IncomingSignal.Set();
        }

        public static void Shutdown()
        {
            if (!sInitialized)
            {
                sInitializing = false;
                sStatus = "STEAM KAPALI";
                return;
            }
            EndSession();
            DisposeCallbacks();
            bool ownsSteamApi = CoopSteamLifecyclePolicy.ShouldShutdownSteamApiOnQuit(sAppId);
            if (ownsSteamApi)
            {
                try { SteamAPI.Shutdown(); }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning("[Coop] Steam kapatma hatasi: " + ex.Message);
                }
            }
            sInitialized = false;
            sInitializing = false;
            sStatus = "STEAM KAPALI";
            CoopPlugin.Log.LogInfo(ownsSteamApi
                ? "[Coop] Steam servisi uygulama cikisinda kapatildi"
                : "[Coop] co-op Steam oturumu kapatildi; Steam API oyunun SteamManager'ina birakildi");
        }

        private static void DisposeCallbacks()
        {
            try
            {
                sJoinRequested?.Dispose();
                sLobbyCreated?.Dispose();
                sLobbyEntered?.Dispose();
                sLobbyDataUpdated?.Dispose();
                sLobbyChatUpdated?.Dispose();
                sSessionRequested?.Dispose();
                sSessionFailed?.Dispose();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Steam callback temizleme hatasi: " + ex.Message);
            }
            sJoinRequested = null;
            sLobbyCreated = null;
            sLobbyEntered = null;
            sLobbyDataUpdated = null;
            sLobbyChatUpdated = null;
            sSessionRequested = null;
            sSessionFailed = null;
        }

        private static uint ResolveConfiguredAppId()
        {
            string fileValue = null;
            try
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt");
                if (File.Exists(path)) fileValue = File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] steam_appid.txt okunamadi: " + ex.Message);
            }

            return CoopSteamAppPolicy.ResolveAppId(
                Environment.GetCommandLineArgs(),
                Environment.GetEnvironmentVariable("SteamAppId"),
                Environment.GetEnvironmentVariable("SteamGameId"),
                fileValue);
        }

        private static string ReadyStatus()
        {
            return "STEAM HAZIR — " + CoopSteamAppPolicy.DisplayName(sAppId);
        }

        private static void OnLobbyCreated(LobbyCreated_t value)
        {
            sCreatingLobby = false;
            if (value.m_eResult != EResult.k_EResultOK || value.m_ulSteamIDLobby == 0)
            {
                sStatus = "STEAM LOBİSİ AÇILAMADI: " + value.m_eResult;
                CoopStatusOverlay.Notify(sStatus);
                return;
            }

            CSteamID lobby = new CSteamID(value.m_ulSteamIDLobby);
            ulong localId = SteamUser.GetSteamID().m_SteamID;
            if (!SteamMatchmaking.SetLobbyData(lobby, LobbyModKey, LobbyModValue) ||
                !SteamMatchmaking.SetLobbyData(lobby, LobbyProtocolKey,
                    SyncManager.ProtocolVersion.ToString(CultureInfo.InvariantCulture)) ||
                !SteamMatchmaking.SetLobbyData(lobby, LobbyAppKey,
                    sAppId.ToString(CultureInfo.InvariantCulture)) ||
                !SteamMatchmaking.SetLobbyData(lobby, LobbyHostKey,
                    localId.ToString(CultureInfo.InvariantCulture)))
            {
                SteamMatchmaking.LeaveLobby(lobby);
                sStatus = "STEAM LOBİ BİLGİSİ YAZILAMADI";
                CoopStatusOverlay.Notify(sStatus);
                return;
            }
            SteamMatchmaking.SetLobbyMemberLimit(lobby, 2);
            SteamMatchmaking.SetLobbyJoinable(lobby, true);
            Volatile.Write(ref sLobbyId, value.m_ulSteamIDLobby);
            Volatile.Write(ref sExpectedPeerId, 0);
            SteamFriends.SetRichPresence("status", CoopText.T("Planetbase Co-op lobisinde"));
            SteamFriends.SetRichPresence("connect", "+connect_lobby " +
                value.m_ulSteamIDLobby.ToString(CultureInfo.InvariantCulture));

            string error = SyncManager.BeginSessionFromSteam(
                "host", value.m_ulSteamIDLobby, 0);
            if (!string.IsNullOrEmpty(error))
            {
                sStatus = error;
                EndSession();
                CoopStatusOverlay.Notify(error);
                return;
            }
            sStatus = "STEAM LOBİSİ HAZIR — ARKADAŞINI DAVET ET";
            CoopStatusOverlay.Notify(sStatus);
            CoopPlugin.Log.LogInfo("[Coop] Steam lobby olusturuldu: " +
                value.m_ulSteamIDLobby);
        }

        private static void OnJoinRequested(GameLobbyJoinRequested_t value)
        {
            ulong lobbyId = value.m_steamIDLobby.m_SteamID;
            CoopPlugin.Log.LogInfo("[Coop] Steam daveti kabul callback'i alindi: lobby=" +
                lobbyId + " friend=" + value.m_steamIDFriend.m_SteamID);
            BeginJoinLobby(lobbyId);
        }

        private static void BeginJoinLobby(ulong lobbyId)
        {
            CoopSteamInviteAdmission admission = CoopSteamJoinPolicy.DecideInvite(
                lobbyId, SyncManager.FullMode, HasLobby);
            if (admission == CoopSteamInviteAdmission.IgnoreInvalidLobby)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] gecersiz Steam davet lobby kimligi yok sayildi");
                return;
            }
            if (admission == CoopSteamInviteAdmission.RejectActiveSession)
            {
                CoopStatusOverlay.Notify("ÖNCE MEVCUT CO-OP OTURUMUNDAN AYRIL");
                CoopPlugin.Log.LogWarning("[Coop] Steam daveti aktif oturum nedeniyle " +
                    "reddedildi: lobby=" + lobbyId);
                return;
            }
            sStatus = "STEAM DAVET LOBİSİNE KATILINIYOR...";
            sPendingJoinLobbyId = lobbyId;
            sPendingJoinDeadlineUtcTicks = DateTime.UtcNow.AddSeconds(15).Ticks;
            sNextJoinMetadataCheckUtcTicks = 0;
            Interlocked.Exchange(ref sEndRequested, 0);
            CoopStatusOverlay.Notify(sStatus);
            CoopPlugin.Log.LogInfo("[Coop] Steam davet lobisine JoinLobby gonderildi: " +
                lobbyId);
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
        }

        private static void OnLobbyEntered(LobbyEnter_t value)
        {
            if (value.m_EChatRoomEnterResponse !=
                    (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                sStatus = "STEAM LOBİSİNE GİRİLEMEDİ: " + value.m_EChatRoomEnterResponse;
                sPendingJoinLobbyId = 0;
                CoopStatusOverlay.Notify(sStatus);
                CoopPlugin.Log.LogWarning("[Coop] Steam LobbyEnter basarisiz: lobby=" +
                    value.m_ulSteamIDLobby + " response=" +
                    value.m_EChatRoomEnterResponse);
                return;
            }
            if (!CoopSteamJoinPolicy.IsExpectedSuccessfulLobbyEnter(
                    value.m_ulSteamIDLobby,
                    sPendingJoinLobbyId,
                    LobbyId,
                    sCreatingLobby,
                    value.m_EChatRoomEnterResponse,
                    (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess))
            {
                CoopPlugin.Log.LogInfo("[Coop] Steam LobbyEnter mevcut/beklenmeyen " +
                    "lobi oldugu icin yok sayildi: entered=" +
                    value.m_ulSteamIDLobby + " pending=" + sPendingJoinLobbyId +
                    " active=" + LobbyId);
                return;
            }
            sPendingJoinLobbyId = value.m_ulSteamIDLobby;
            sPendingJoinDeadlineUtcTicks = DateTime.UtcNow.AddSeconds(15).Ticks;
            CoopPlugin.Log.LogInfo("[Coop] Steam LobbyEnter basarili; metadata " +
                "bekleniyor: lobby=" + value.m_ulSteamIDLobby);
            SteamMatchmaking.RequestLobbyData(new CSteamID(value.m_ulSteamIDLobby));
            TryFinalizeJoin(value.m_ulSteamIDLobby);
        }

        private static void OnLobbyDataUpdated(LobbyDataUpdate_t value)
        {
            if (value.m_bSuccess != 0 && value.m_ulSteamIDLobby == sPendingJoinLobbyId)
                TryFinalizeJoin(value.m_ulSteamIDLobby);
        }

        private static void PumpPendingJoin()
        {
            ulong pending = sPendingJoinLobbyId;
            if (pending == 0) return;
            long now = DateTime.UtcNow.Ticks;
            if (now >= sPendingJoinDeadlineUtcTicks)
            {
                SteamMatchmaking.LeaveLobby(new CSteamID(pending));
                sPendingJoinLobbyId = 0;
                sStatus = "STEAM LOBİ BİLGİSİ ZAMAN AŞIMINA UĞRADI";
                CoopStatusOverlay.Notify(sStatus);
                return;
            }
            if (now < sNextJoinMetadataCheckUtcTicks) return;
            sNextJoinMetadataCheckUtcTicks = now + TimeSpan.TicksPerMillisecond * 500;
            TryFinalizeJoin(pending);
        }

        private static void TryFinalizeJoin(ulong lobbyId)
        {
            if (lobbyId == 0 || lobbyId != sPendingJoinLobbyId) return;
            CSteamID lobby = new CSteamID(lobbyId);
            string mod = SteamMatchmaking.GetLobbyData(lobby, LobbyModKey);
            string protocol = SteamMatchmaking.GetLobbyData(lobby, LobbyProtocolKey);
            string app = SteamMatchmaking.GetLobbyData(lobby, LobbyAppKey);
            string hostText = SteamMatchmaking.GetLobbyData(lobby, LobbyHostKey);
            if (string.IsNullOrEmpty(mod) || string.IsNullOrEmpty(protocol) ||
                string.IsNullOrEmpty(app) || string.IsNullOrEmpty(hostText)) return;

            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
            ulong hostId;
            if (!CoopSteamJoinPolicy.TryResolveGuestHost(
                    mod,
                    LobbyModValue,
                    protocol,
                    app,
                    hostText,
                    owner.m_SteamID,
                    SteamUser.GetSteamID().m_SteamID,
                    SyncManager.ProtocolVersion,
                    sAppId,
                    out hostId))
            {
                SteamMatchmaking.LeaveLobby(lobby);
                sPendingJoinLobbyId = 0;
                sStatus = "UYUMSUZ VEYA GEÇERSİZ PLANETBASE CO-OP LOBİSİ";
                CoopStatusOverlay.Notify(sStatus);
                CoopPlugin.Log.LogWarning("[Coop] Steam lobby metadata/host " +
                    "dogrulamasi reddedildi: lobby=" + lobbyId + " owner=" +
                    owner.m_SteamID + " mod='" + mod + "' protocol='" + protocol +
                    "' app='" + app + "' host='" + hostText + "'");
                return;
            }

            CoopPlugin.Log.LogInfo("[Coop] Steam lobby metadata/host dogrulandi: lobby=" +
                lobbyId + " host=" + hostId);
            Volatile.Write(ref sLobbyId, lobbyId);
            Volatile.Write(ref sExpectedPeerId, hostId);
            sPendingJoinLobbyId = 0;
            SteamFriends.SetRichPresence("status", CoopText.T("Planetbase Co-op lobisinde"));
            string error = SyncManager.BeginSessionFromSteam("guest", lobbyId, hostId);
            if (!string.IsNullOrEmpty(error))
            {
                sStatus = error;
                EndSession();
                CoopStatusOverlay.Notify(error);
                return;
            }
            sStatus = "STEAM HOSTA BAĞLANILIYOR...";
            CoopStatusOverlay.Notify(sStatus);
            CoopPlugin.Log.LogInfo("[Coop] Steam davet lobisine katilindi: lobby=" +
                lobbyId + " host=" + hostId);
        }

        private static void OnSessionRequested(
            SteamNetworkingMessagesSessionRequest_t value)
        {
            ulong remoteId = value.m_identityRemote.GetSteamID64();
            ulong lobbyId = LobbyId;
            if (remoteId == 0 || lobbyId == 0 || !IsLobbyMember(lobbyId, remoteId))
            {
                CoopPlugin.Log.LogWarning("[Coop] lobby disi Steam relay istegi reddedildi: " +
                    remoteId);
                return;
            }
            ulong expected = ExpectedPeerId;
            if (expected != 0 && expected != remoteId) return;
            Volatile.Write(ref sExpectedPeerId, remoteId);
            SteamNetworkingIdentity identity = value.m_identityRemote;
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            IncomingSignal.Set();
            CoopPlugin.Log.LogInfo("[Coop] Steam relay oturumu kabul edildi: " + remoteId);
        }

        private static void OnSessionFailed(
            SteamNetworkingMessagesSessionFailed_t value)
        {
            ulong remoteId = value.m_info.m_identityRemote.GetSteamID64();
            if (ExpectedPeerId != 0 && remoteId != ExpectedPeerId) return;
            SetFailure("Steam relay bağlantısı koptu: " + value.m_info.m_eState +
                " / " + value.m_info.m_eEndReason + " / " + value.m_info.m_szEndDebug);
        }

        private static void OnLobbyChatUpdated(LobbyChatUpdate_t value)
        {
            ulong peer = ExpectedPeerId;
            if (value.m_ulSteamIDLobby != LobbyId || peer == 0 ||
                value.m_ulSteamIDUserChanged != peer) return;
            if (!IsLobbyMember(LobbyId, peer))
                SetFailure("Steam lobisindeki diğer oyuncu ayrıldı");
        }

        private static bool IsLobbyMember(ulong lobbyId, ulong steamId)
        {
            if (lobbyId == 0 || steamId == 0) return false;
            CSteamID lobby = new CSteamID(lobbyId);
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == steamId)
                    return true;
            }
            return false;
        }

        private static void PumpOutgoing()
        {
            ulong peerId = ExpectedPeerId;
            if (peerId == 0) return;
            SteamNetworkingIdentity identity = IdentityFor(peerId);
            for (int sent = 0; sent < 16; sent++)
            {
                OutgoingMessage message;
                lock (QueueLock)
                {
                    if (Outgoing.Count == 0) return;
                    message = Outgoing.Dequeue();
                    sOutgoingBytes -= message.Fragment.Length;
                }
                GCHandle pinned = default(GCHandle);
                try
                {
                    pinned = GCHandle.Alloc(message.Fragment, GCHandleType.Pinned);
                    int flags = (message.Delivery == CoopSteamDelivery.FastUnreliable
                        ? Constants.k_nSteamNetworkingSend_UnreliableNoDelay
                        : Constants.k_nSteamNetworkingSend_ReliableNoNagle) |
                        Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
                    int channel = message.Delivery == CoopSteamDelivery.FastUnreliable
                        ? FastChannel
                        : Channel;
                    EResult result = SteamNetworkingMessages.SendMessageToUser(
                        ref identity, pinned.AddrOfPinnedObject(),
                        (uint)message.Fragment.Length, flags, channel);
                    if (result != EResult.k_EResultOK)
                    {
                        SetFailure("Steam relay gönderimi başarısız: " + result);
                        return;
                    }
                }
                finally
                {
                    if (pinned.IsAllocated) pinned.Free();
                }
            }
        }

        private static void PumpIncoming(
            int channel,
            CoopSteamFragmentAssembler assembler,
            IntPtr[] messages,
            bool fastPath)
        {
            int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(
                channel, messages, messages.Length);
            for (int i = 0; i < count; i++)
            {
                IntPtr pointer = messages[i];
                try
                {
                    SteamNetworkingMessage_t message =
                        SteamNetworkingMessage_t.FromIntPtr(pointer);
                    ulong sender = message.m_identityPeer.GetSteamID64();
                    ulong expected = ExpectedPeerId;
                    if (sender == 0 || expected == 0 || sender != expected ||
                        message.m_cbSize <= 0 ||
                        message.m_cbSize > CoopSteamTransportProtocol.MaxNativeMessageBytes)
                        continue;
                    byte[] fragment = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, fragment, 0, fragment.Length);
                    byte[] packet;
                    CoopSteamFragmentResult result = assembler.Accept(fragment, out packet);
                    if (result == CoopSteamFragmentResult.Invalid)
                    {
                        SetFailure("Geçersiz Steam relay paket sırası");
                        continue;
                    }
                    if (result == CoopSteamFragmentResult.Complete)
                    {
                        SyncManager sync = SyncManager.Instance;
                        if (!fastPath || sync == null ||
                            !sync.TryProcessFastTransportPacket(packet))
                            EnqueueIncoming(packet);
                    }
                }
                finally
                {
                    if (pointer != IntPtr.Zero)
                        SteamNetworkingMessage_t.Release(pointer);
                }
            }
        }

        private static void EnqueueIncoming(byte[] packet)
        {
            lock (QueueLock)
            {
                if (Incoming.Count >= MaxQueuedMessages ||
                    sIncomingBytes > MaxQueuedBytes - packet.Length)
                {
                    SetFailure("Steam alım kuyruğu doldu");
                    return;
                }
                Incoming.Enqueue(packet);
                sIncomingBytes += packet.Length;
            }
            IncomingSignal.Set();
        }

        private static bool TryDequeueIncoming(out byte[] payload)
        {
            lock (QueueLock)
            {
                if (Incoming.Count == 0)
                {
                    payload = null;
                    return false;
                }
                payload = Incoming.Dequeue();
                sIncomingBytes -= payload.Length;
                return true;
            }
        }

        private static SteamNetworkingIdentity IdentityFor(ulong steamId)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(steamId);
            return identity;
        }

        private static void SetFailure(string failure)
        {
            if (string.IsNullOrWhiteSpace(failure)) return;
            lock (QueueLock)
            {
                if (string.IsNullOrEmpty(sFailure)) sFailure = failure.Trim();
            }
            IncomingSignal.Set();
            CoopPlugin.Log.LogWarning("[Coop] " + failure);
        }

        private static void ResetSessionState()
        {
            lock (QueueLock)
            {
                Outgoing.Clear();
                Incoming.Clear();
                sOutgoingBytes = 0;
                sIncomingBytes = 0;
                sFailure = "";
            }
            Assembler.Reset();
            FastAssembler.Reset();
            sCreatingLobby = false;
            sPendingJoinLobbyId = 0;
            sPendingJoinDeadlineUtcTicks = 0;
            sNextJoinMetadataCheckUtcTicks = 0;
            Volatile.Write(ref sLobbyId, 0);
            Volatile.Write(ref sExpectedPeerId, 0);
            IncomingSignal.Set();
        }
    }
}
