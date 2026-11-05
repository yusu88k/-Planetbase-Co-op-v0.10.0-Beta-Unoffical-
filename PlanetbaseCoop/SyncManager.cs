using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// FULL SENKRON (lockstep) çekirdeği.
    /// Per-tick RNG tohumu ile determinizm, partner kamera transform paketleri,
    /// dusuk gecikmeli beacon (her tick), otomatik Waiting ilerlemesi.
    /// </summary>
    public class SyncManager
    {
        private sealed class PendingGameOverEvent
        {
            public readonly long SessionId;
            public readonly long Tick;

            public PendingGameOverEvent(long sessionId, long tick)
            {
                SessionId = sessionId;
                Tick = tick;
            }
        }

        public static SyncManager Instance;

        public const string Magic = "PBCOOP1";
        public const int ProtocolVersion = 21;
        public const int DefaultPort = 5560;
        public const int DefaultStartTimeoutSeconds = 120;

        public static string Role = "";
        public static string HostIp = "127.0.0.1";
        public static string Profile = "";
        public static int Port = DefaultPort;
        public static int NewGameSeed = -1;
        public static bool NewGameRequested;
        public static int StartTimeoutSeconds = DefaultStartTimeoutSeconds;
        public static bool Fullscreen;
        public static CoopTransportKind TransportKind = CoopTransportKind.Tcp;

        /// <summary>Menüden başlatılan oturumda CLI argümanları statikleri ezmesin.</summary>
        private static bool sMenuInitiated;

        /// <summary>SENKRON RNG TOHUMU — her fixedUpdate başında bu tohum + tickIndex ile yeniden tohumlanır.</summary>
        public const int SyncedSeed = 0x5EED1234;

        public static bool FullMode => Role.Length > 0;
        public static bool UsingSteamTransport =>
            TransportKind == CoopTransportKind.Steam;

        // Ağ
        private TcpListener mListener;
        private TcpClient mClient;
        private NetworkStream mStream;
        private Thread mNetThread;
        private volatile bool mRunning;
        private readonly object mSendLock = new object();
        private readonly object mStartupLock = new object();
        private readonly object mPendingWorldLock = new object();
        private readonly object mHashLock = new object();
        private readonly object mOosStateLock = new object();
        private readonly object mMutationFailureLock = new object();
        private readonly CoopTickHashMatcher mHashMatcher = new CoopTickHashMatcher();
        private readonly CoopMutationFailureLedger mMutationFailureLedger = new CoopMutationFailureLedger();
        private readonly CoopOutstandingMutationLedger mOutstandingMutations =
            new CoopOutstandingMutationLedger();
        private readonly Queue<CoopMutationFailureData> mPendingMutationFailures = new Queue<CoopMutationFailureData>();

        // Durum
        private volatile bool mHandshakeDone;
        private volatile bool mGuestConnected;
        public volatile bool GameLoaded;
        private volatile bool mInitialSaveSent;
        private volatile bool mInitialSaveReceived;
        private volatile bool mLoadQueued;
        private bool mLoadTriggered;
        private volatile bool mSnapshotVerificationPending;
        private bool mOnGameLoadedLogged;
        private bool mAutoContinueAttempted;
        private bool mHostFreshWorld;
        private bool mHostWorldOriginKnown;
        private bool mHostBootstrapStarted;
        private bool mHostWorldStable;
        private bool mGuestLandingReplay;
        private long mHostTickAtWorld;
        private byte[] mPendingWorldPacket;
        private object mObservedGameState;
        private CoopStartupCoordinator mStartup;
        private volatile bool mStartApplied;
        private volatile bool mHostStartAuthorizedPending;
        private volatile bool mAuthoritativePaused;
        private volatile int mAuthoritativeScaleIndex;
        private bool mStartupFailed;
        // FailStartup sonrasi oturum ana thread'de tamamen kapatilir; aksi halde
        // Role dolu kalir, FullMode yamalari (tick kapisi, per-tick RNG) tek
        // oyunculu oyunu ve sonraki oturumlari kilitler.
        private volatile bool mFailureTeardownPending;
        private volatile bool mAbortRequested;
        private volatile bool mConnectionLost;
        private volatile bool mInvalidWorldPacket;
        private readonly object mRuntimeInvariantLock = new object();
        private volatile bool mRuntimeInvariantFailurePending;
        private string mRuntimeInvariantFailure = "";
        private long mLastPeerPacketUtcTicks;
        private bool mWatchdogWarningShown;
        private DateTime? mHostWaitingSinceUtc;
        private bool mStartPacketSent;
        private bool mGameOverBroadcastSent;
        private PendingGameOverEvent mPendingGameOverRequest;
        private PendingGameOverEvent mPendingGameOverState;
        private bool mGameOverRequestSent;
        private long mTerminalSessionId;
        private volatile bool mRecoveryRequested;
        private volatile bool mRecoveryPending;
        private volatile bool mHostRecoveryReloadPending;
        private long mRecoverySessionId;
        private int mConsecutiveOosCount;
        private long mLastRecoveryUtcTicks;
        private const int RequiredConsecutiveOos = 2;
        private const int RecoveryCooldownSeconds = 30;

        // LOBI (menüden başlatılan oturumlar)
        private readonly object mLobbyLock = new object();
        private CoopLobbySelection mHostLobbySelection;
        private CoopLobbySelection mRemoteLobbySelection;
        private CoopLobbySelectionView mHostLobbySelectionView;
        private CoopLobbySelectionView mRemoteLobbySelectionView;
        private int mLobbyPhaseValue;
        private long mLobbySelectionRevision;
        private long mGuestSelectionLoadedRevision;
        private long mLocalSelectionLoadedRevision;
        private string mLobbyDisplayAddress = "127.0.0.1";
        private volatile bool mLobbyActive;
        private volatile bool mLobbyStarted;
        private volatile bool mGuestLobbyReady;
        private volatile bool mLocalLobbyReadySent;
        private volatile bool mRemoteLobbyStartSeen;
        private int mMenuPlanetIndex;
        private bool mMenuLoadRequested;
        private string mMenuLoadSavePath = "";

        private CoopLobbyPhase LobbyPhase
        {
            get { return (CoopLobbyPhase)Volatile.Read(ref mLobbyPhaseValue); }
            set { Volatile.Write(ref mLobbyPhaseValue, (int)value); }
        }

        // OOS dedektörü
        public long TickIndex;
        private const long BaseChecksumIntervalTicks =
            CoopCheckpointSchedule.DefaultBaseIntervalTicks;
        private long mNextChecksumTick = BaseChecksumIntervalTicks;
        private long mScheduledChecksumIntervalTicks = BaseChecksumIntervalTicks;
        public string LastHostHash = "";
        public string LastGuestHash = "";
        public volatile int OosCount;
        private readonly object mDiagnosticLock = new object();
        // Kok neden icin tek ornek yetmiyor; oturum basina sinirli sayida OOS
        // snapshot'i saklanir (her biri ~100 KB).
        private const int MaxOosArtifactsPerSession = 4;
        private int mOosArtifactsCaptured;
        private string mLastCanonicalDiagnosticDigest = "";
        private long mLastDiagnosticTick = -1;
        private byte[] mLastDiagnosticSnapshot;
        private bool mSnapshotAdmissionDeferred;
        private long mSnapshotRetryAfterUtcTicks;
        private long mSnapshotDeferStartedUtcTicks;

        // Paket tipleri
        public const byte PktWorld = 1;
        public const byte PktHash = CoopProtocol.PktHash;
        public const byte PktTick = CoopProtocol.PktTick;
        public const byte PktCamera = 4;
        public const byte PktReady = CoopProtocol.PktReady;
        public const byte PktStart = CoopProtocol.PktStart;
        public const byte PktAbort = CoopProtocol.PktAbort;
        public const byte PktPlacementPreview = CoopProtocol.PktPlacementPreview;
        public const byte PktPlacementRequest = CoopProtocol.PktPlacementRequest;
        public const byte PktPlacementResult = CoopProtocol.PktPlacementResult;
        public const byte PktSelection = CoopProtocol.PktSelection;
        public const byte PktConnectionRequest = CoopProtocol.PktConnectionRequest;
        public const byte PktConnectionResult = CoopProtocol.PktConnectionResult;
        public const byte PktHeartbeat = CoopProtocol.PktHeartbeat;
        public const byte PktRecycleRequest = CoopProtocol.PktRecycleRequest;
        public const byte PktRecycleResult = CoopProtocol.PktRecycleResult;
        public const byte PktGameOverRequest = CoopProtocol.PktGameOverRequest;
        public const byte PktGameOverState = CoopProtocol.PktGameOverState;
        public const byte PktShipSpawn = CoopProtocol.PktShipSpawn;
        public const byte PktLandingPermissions = CoopProtocol.PktLandingPermissions;
        public const byte PktMutationFailure = CoopProtocol.PktMutationFailure;
        public const byte PktAuthoritativeCommand = CoopProtocol.PktAuthoritativeCommand;
        public long PeerTick = -1;
        private long mLastHeartbeatSentUtcTicks;
        private long mHeartbeatSequence;
        private long mLastHeartbeatSequenceSent;
        private readonly object mLatencyLock = new object();
        private readonly CoopLatencyEstimator mLatencyEstimator = new CoopLatencyEstimator();
        private int mPingMilliseconds;
        private int mJitterMilliseconds;
        private int mLatencyWarningLevel;
        private bool mTickGateFrozen;
        private long mTickGateFrozenSinceUtcTicks;
        private volatile bool mTickGatePeerSampleReady;
        private readonly double[] mCheckpointDurationMilliseconds = new double[32];
        private int mCheckpointDurationCount;
        private int mCheckpointDurationCursor;
        private long mCheckpointMeasurementCount;

        private int mResolvedNewGameSeed = -1;
        private bool mNewGameSeedResolved;
        private bool mMenuNewGameSeedExplicit;

        // Waiting state detection
        private static System.Reflection.FieldInfo sGameStateField;
        private static System.Reflection.FieldInfo sStateField;
        private static System.Reflection.MethodInfo sOnStartUpdating;
        private static System.Reflection.FieldInfo sLoadGameFileField;
        private static System.Reflection.FieldInfo sTerrainSeedField;
        private static System.Reflection.FieldInfo sFrameIndexField;
        private static System.Reflection.FieldInfo sNextIdField;
        private static System.Reflection.FieldInfo sNextBotIdField;
        private static System.Reflection.FieldInfo sLandingShipTickTimeField;
        private static System.Reflection.FieldInfo sTimeToWindChangeField;
        private static System.Reflection.FieldInfo sWindOffsetField;
        private static System.Reflection.FieldInfo sWindDirectionField;
        private int mAuthoritativeNextId = -1;
        private int mAuthoritativeNextBotId = -1;
        private int mAuthoritativeFrameIndex = -1;
        private CoopDisasterState? mAuthoritativeSandstorm;
        private CoopDisasterState? mAuthoritativeBlizzard;
        private CoopDisasterState? mAuthoritativeSolarFlare;
        private float mAuthoritativeShipManagerTickTime = float.NaN;
        private CoopLandingPermissionsData mAuthoritativeLandingPermissions;
        private CoopEnvironmentWindState? mAuthoritativeEnvironment;

        public static void Create()
        {
            ParseArgs();
            Instance = new SyncManager();
            if (FullMode)
            {
                CoopPlugin.Log.LogInfo(string.Format(
                    "[Coop] FULL SENKRON v{0}: rol={1}, transport={2}, port={3}, host={4}, profil='{5}', timeout={6}s",
                    ProtocolVersion, Role, TransportKind, Port, HostIp, Profile,
                    StartTimeoutSeconds));
                Instance.Start();
                Instance.CacheReflection();
            }
        }

        /// <summary>
        /// Menüden co-op oturumu başlatır. Dönüş değeri: hata mesajı ya da "".
        /// CLI akışı (-coopRole) değişmeden çalışmaya devam eder.
        /// </summary>
        public static string BeginSessionFromMenu(string role, string ip, int port)
        {
            return BeginSession(role, ip, port, CoopTransportKind.Tcp, 0, 0);
        }

        internal static string BeginSessionFromSteam(
            string role,
            ulong lobbyId,
            ulong peerSteamId)
        {
            if (!CoopSteamService.Available || lobbyId == 0)
                return "STEAM/" + CoopSteamService.AppDisplayName + " HAZIR DEĞİL";
            if (role == "guest" && peerSteamId == 0)
                return "STEAM HOST KİMLİĞİ BULUNAMADI";
            return BeginSession(role, "steam", DefaultPort,
                CoopTransportKind.Steam, lobbyId, peerSteamId);
        }

        private static string BeginSession(
            string role,
            string ip,
            int port,
            CoopTransportKind transport,
            ulong steamLobbyId,
            ulong peerSteamId)
        {
            role = role == null ? "" : role.Trim().ToLowerInvariant();
            if (role != "host" && role != "guest") return "GECERSIZ ROL";
            if (port < 1 || port > 65535) return "GECERSIZ PORT";
            if (transport == CoopTransportKind.Tcp && role == "guest" &&
                string.IsNullOrWhiteSpace(ip)) return "HOST ADRESI GEREKLI";
            if (Instance != null && FullMode && Instance.mRunning) return "OTURUM ZATEN AKTIF";

            sMenuInitiated = true;
            TransportKind = transport;
            Role = role;
            CoopFocusState.EnableBackgroundProcessing();
            PartnerPreview.Disable();
            HostIp = transport == CoopTransportKind.Steam
                ? "steam"
                : (role == "guest" ? ip.Trim() : "127.0.0.1");
            Port = port;
            if (role == "guest" && Profile.Length == 0) Profile = "coop-guest-menu";

            // Plugin Awake runs before a menu role is selected, so
            // PlacementSync.Create() intentionally does nothing there. Create
            // the authoritative placement coordinator now that FullMode is
            // active; otherwise the old fail-open path commits -1 preview IDs
            // and the hardened path correctly but unusably blocks all input.
            if (CoopRuntimeSubsystemPolicy.RequiresPlacementCoordinator(
                    FullMode, PlacementSync.Instance != null))
            {
                PlacementSync.Create();
            }
            PlacementSync.Instance?.ResetForSession();

            if (Instance == null)
            {
                Create();
            }
            else
            {
                Instance.ResetForMenuSession();
                Instance.CacheReflection();
                Instance.Start();
            }
            if (!Instance.mRunning || Instance.mStartupFailed)
            {
                Role = "";
                return "AĞ OTURUMU BAŞLATILAMADI";
            }
            Instance.mLobbyActive = true;
            Instance.LobbyPhase = CoopLobbyPhase.Waiting;
            Instance.mLobbyDisplayAddress = transport == CoopTransportKind.Steam
                ? "STEAM RELAY / " + CoopSteamService.AppDisplayName
                : (role == "host" ? ResolveLocalLobbyAddress() : HostIp);
            CoopPlugin.Log.LogInfo(string.Format(
                "[Coop] menu oturumu basladi: rol={0} transport={1} port={2} host={3} steamPeer={4}",
                role, transport, port, HostIp, peerSteamId));
            return "";
        }

        /// <summary>Ağ bağlantısını kapatıp modu pasife alır; ana menüye güvenli dönüş.</summary>
        public static void EndMenuSession()
        {
            if (Instance == null) return;
            bool steamSession = TransportKind == CoopTransportKind.Steam;
            Instance.mLobbyActive = false;
            Instance.mLobbyStarted = false;
            Instance.LobbyPhase = CoopLobbyPhase.Waiting;
            Instance.mGuestLobbyReady = false;
            Instance.mLocalLobbyReadySent = false;
            Instance.mRemoteLobbyStartSeen = false;
            Instance.ResetLobbySelectionState();
            Instance.StopNetworkThread();
            Instance.OnTitleTransitionRequested();
            Role = "";
            TransportKind = CoopTransportKind.Tcp;
            if (steamSession) CoopSteamService.EndSession();
            CoopStatusOverlay.SetConnectionWarning(false);
            CoopPlugin.Log.LogInfo("[Coop] menu oturumu kapatildi");
        }

        /// <summary>
        /// Uygulama cikisi: ag thread'i Mono runtime temizliginden once durur.
        /// Steam servisi bundan sonra kapatilir.
        /// </summary>
        public static void ShutdownForApplicationQuit()
        {
            SyncManager instance = Instance;
            if (instance == null) return;
            try
            {
                instance.StopNetworkThread();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] cikista ag thread'i durdurulamadi: " + ex.Message);
            }
        }

        private void ResetForMenuSession()
        {
            StopNetworkThread();
            OnTitleTransitionRequested();
            mHandshakeDone = false;
            mGuestConnected = false;
            mAutoContinueAttempted = false;
            mStartupFailed = false;
            mConnectionLost = false;
            mAbortRequested = false;
            mWatchdogWarningShown = false;
            Interlocked.Exchange(ref PeerTick, -1);
            TickIndex = 0;
            LastHostHash = "";
            LastGuestHash = "";
            OosCount = 0;
            Interlocked.Exchange(ref mTerminalSessionId, 0);
            mHashMatcher.Clear();
            mResolvedNewGameSeed = -1;
            mNewGameSeedResolved = false;
            mMenuNewGameSeedExplicit = false;
            ResetLobbySelectionState();
            mLobbyActive = false;
            mLobbyStarted = false;
            LobbyPhase = CoopLobbyPhase.Waiting;
            mGuestLobbyReady = false;
            mLocalLobbyReadySent = false;
            mRemoteLobbyStartSeen = false;
            ResetNetworkTiming();
            CoopStatusOverlay.SetConnectionWarning(false);
        }

        private void ResetLobbySelectionState()
        {
            lock (mLobbyLock)
            {
                mHostLobbySelection = default(CoopLobbySelection);
                mRemoteLobbySelection = default(CoopLobbySelection);
                mHostLobbySelectionView = default(CoopLobbySelectionView);
                mRemoteLobbySelectionView = default(CoopLobbySelectionView);
                mLobbySelectionRevision = 0;
                mGuestSelectionLoadedRevision = 0;
                mLocalSelectionLoadedRevision = 0;
            }
            mMenuPlanetIndex = 0;
            mMenuLoadRequested = false;
            mMenuLoadSavePath = "";
        }

        private static string ResolveLocalLobbyAddress()
        {
            try
            {
                IPAddress[] addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
                for (int i = 0; i < addresses.Length; i++)
                {
                    IPAddress address = addresses[i];
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address)) return address.ToString();
                }
            }
            catch (SocketException) { }
            return "127.0.0.1";
        }

        // ==================== LOBI API ====================

        /// <summary>Eski menü seçim API'si; yalnız bekleme aşamasında hazır onayını sıfırlar.</summary>
        public string SetLobbySelection(CoopLobbySelection selection)
        {
            if (Role != "host") return "YALNIZ HOST SECIM YAPABILIR";
            if (!mLobbyActive || mLobbyStarted) return "LOBİ KAPALI";
            if (!CoopLobbyPolicy.IsValidSelection(
                    selection, SaveData.anySaveGames()))
                return "GEÇERSİZ DÜNYA SEÇİMİ";
            lock (mLobbyLock)
            {
                if (LobbyPhase == CoopLobbyPhase.Waiting &&
                    CoopLobbyPolicy.ShouldResetGuestReady(mHostLobbySelection, selection))
                    mGuestLobbyReady = false;
                mHostLobbySelection = selection;
            }
            if (mHandshakeDone && mGuestConnected &&
                !Send(CoopLobbyProtocol.CreateStatePacket(
                    CoopLobbyProtocol.PktLobbyState, selection)))
                return "SECIM GONDERILEMEDI";
            CoopPlugin.Log.LogInfo(string.Format(
                "[Coop] lobi secimi: mod={0} gezegen={1} seed={2} kayit='{3}'",
                selection.Mode, selection.PlanetIndex, selection.Seed, selection.SaveName));
            return "";
        }

        private void PublishCurrentLobbySelectionToGuest()
        {
            if (Role != "host" || !mLobbyActive || !mHandshakeDone ||
                !mGuestConnected) return;
            CoopLobbySelection selection;
            lock (mLobbyLock) selection = mHostLobbySelection;
            if (!CoopLobbyPolicy.IsValidSelection(
                    selection, SaveData.anySaveGames())) return;
            if (!Send(CoopLobbyProtocol.CreateStatePacket(
                    CoopLobbyProtocol.PktLobbyState, selection)))
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] mevcut lobi secimi yeni misafire gonderilemedi");
            }
        }

        /// <summary>
        /// Bekleme lobisindeki BAŞLAT. Yalnız seçim ekranını açar; dünyayı kurmaz.
        /// </summary>
        public string RequestLobbySelectionStart()
        {
            if (Role != "host") return "YALNIZ HOST BAŞLATIR";
            if (!mLobbyActive || mLobbyStarted) return "LOBİ DURUMU GEÇERSİZ";

            CoopLobbySelectionView initialView;
            lock (mLobbyLock)
            {
                if (!CoopLobbyPolicy.CanEnterSelection(
                        Role, LobbyPhase, mGuestConnected, mGuestLobbyReady))
                {
                    if (!mGuestConnected) return "MİSAFİR BEKLENİYOR";
                    if (!mGuestLobbyReady) return "MİSAFİR HAZIR DEĞİL";
                    return "SEÇİM ZATEN BAŞLADI";
                }
                initialView = new CoopLobbySelectionView
                {
                    Revision = ++mLobbySelectionRevision,
                    Stage = CoopSelectionStage.Planet,
                    PlanetIndex = -1,
                    Latitude = 0f,
                    Longitude = 0f,
                    Seed = 0,
                    BaseName = string.Empty
                };
            }

            if (!Send(CoopLobbyProtocol.CreateSelectionViewPacket(initialView)))
                return "SEÇİM EKRANI GÖNDERİLEMEDİ";
            lock (mLobbyLock)
            {
                mHostLobbySelectionView = initialView;
                mGuestSelectionLoadedRevision = 0;
                LobbyPhase = CoopLobbyPhase.Selecting;
            }
            // Guest ENTER paketinden sonra görünümü uygulayıp hemen ACK atabilir;
            // host state'i ENTER'dan önce Selecting olmalı ki bu ACK kaybolmasın.
            if (!Send(CoopLobbyProtocol.CreateEnterSelectionPacket()))
            {
                lock (mLobbyLock)
                {
                    if (mHostLobbySelectionView.Revision == initialView.Revision)
                    {
                        mHostLobbySelectionView = default(CoopLobbySelectionView);
                        mGuestSelectionLoadedRevision = 0;
                        LobbyPhase = CoopLobbyPhase.Waiting;
                    }
                }
                return "SEÇİM BAŞLATILAMADI";
            }
            CoopStatusOverlay.Notify("GEZEGEN SEÇİMİ BAŞLADI");
            CoopPlugin.Log.LogInfo("[Coop] host seçim ekranını başlattı");
            return "";
        }

        public string PublishLobbySelectionView(CoopLobbySelectionView view)
        {
            if (!CoopLobbyPolicy.CanMutateSelection(
                    Role, CoopSelectionMutationOrigin.LocalInput, LobbyPhase))
                return "SEÇİM YETKİSİ YOK";
            if (!mLobbyActive || mLobbyStarted) return "LOBİ KAPALI";

            byte[] packet;
            lock (mLobbyLock)
            {
                view.Revision = ++mLobbySelectionRevision;
                if (!CoopLobbyProtocol.IsValidSelectionView(view))
                    return "GEÇERSİZ SEÇİM GÖRÜNÜMÜ";
                packet = CoopLobbyProtocol.CreateSelectionViewPacket(view);
                mHostLobbySelectionView = view;
                if (view.Stage == CoopSelectionStage.Location)
                {
                    mHostLobbySelection = new CoopLobbySelection
                    {
                        Mode = CoopLobbyMode.NewGame,
                        PlanetIndex = view.PlanetIndex,
                        Seed = view.Seed,
                        SaveName = string.Empty
                    };
                }
            }

            if (!Send(packet)) return "SEÇİM GÖNDERİLEMEDİ";
            return "";
        }

        public void AcknowledgeLobbySelectionLoaded(long revision)
        {
            if (Role != "guest" || LobbyPhase != CoopLobbyPhase.Selecting || revision <= 0)
                return;
            lock (mLobbyLock)
            {
                if (revision > mRemoteLobbySelectionView.Revision ||
                    revision <= mLocalSelectionLoadedRevision) return;
                mLocalSelectionLoadedRevision = revision;
            }
            Send(CoopLobbyProtocol.CreateSelectionLoadedPacket(revision));
        }

        /// <summary>Vanilya Start Game: nihai seçimi sabitler ve ortak dünyayı kurar.</summary>
        public string RequestLobbyStart()
        {
            if (Role != "host") return "YALNIZ HOST BAŞLATIR";
            if (!mLobbyActive || mLobbyStarted) return "LOBİ DURUMU GEÇERSİZ";
            bool connected;
            CoopLobbySelection selection;
            CoopLobbySelectionView view;
            bool guestSelectionLoaded;
            lock (mLobbyLock)
            {
                connected = mGuestConnected;
                selection = mHostLobbySelection;
                view = mHostLobbySelectionView;
                guestSelectionLoaded = CoopLobbyPolicy.IsSelectionScreenReady(
                    view.Revision, mGuestSelectionLoadedRevision);
            }
            if (!connected) return "MİSAFİR BAĞLANTISI KOPTU";
            if (!guestSelectionLoaded) return "MİSAFİR SEÇİM EKRANI HAZIR DEĞİL";
            if (!CoopLobbyPolicy.CanCommitWorld(
                    Role, LobbyPhase, connected, guestSelectionLoaded, view) ||
                !CoopLobbyPolicy.IsValidSelection(selection, hasAnySave: false))
                return "GEÇERSİZ DÜNYA SEÇİMİ";

            NewGameRequested = true;
            NewGameSeed = selection.Seed;
            mMenuNewGameSeedExplicit = true;
            mResolvedNewGameSeed = selection.Seed;
            mNewGameSeedResolved = true;
            mMenuPlanetIndex = selection.PlanetIndex;
            if (!Send(CoopLobbyProtocol.CreateWorldCommitPacket(
                    selection, view.Revision)))
                return "START GONDERILEMEDI";
            mLobbyStarted = true;
            LobbyPhase = CoopLobbyPhase.WorldCommitted;
            CoopStatusOverlay.Notify("ORTAK DÜNYA BAŞLATILIYOR");
            CoopPlugin.Log.LogInfo("[Coop] host lobiden START yayinladi: mod=" + selection.Mode);
            return "";
        }

        /// <summary>
        /// Seçilen yerel kaydı hostta yükler; misafir geçici dünya kurmadan
        /// hostun doğrulanmış snapshot'ını bekler.
        /// </summary>
        public string RequestLobbySavedGameStart()
        {
            if (Role != "host") return "YALNIZ HOST BAŞLATIR";
            if (!mLobbyActive || mLobbyStarted || LobbyPhase != CoopLobbyPhase.Waiting)
                return "LOBİ DURUMU GEÇERSİZ";

            bool connected;
            bool ready;
            CoopLobbySelection selection;
            lock (mLobbyLock)
            {
                connected = mGuestConnected;
                ready = mGuestLobbyReady;
                selection = mHostLobbySelection;
            }
            bool selectionValid = selection.Mode != CoopLobbyMode.NewGame &&
                CoopLobbyPolicy.IsValidSelection(selection, SaveData.anySaveGames());
            if (!CoopLobbyPolicy.CanAuthorizeStart(
                    mLobbyActive, mLobbyStarted, connected, ready, selectionValid))
            {
                if (!connected) return "MİSAFİR BEKLENİYOR";
                if (!ready) return "MİSAFİR HAZIR DEĞİL";
                return "GEÇERSİZ KAYIT SEÇİMİ";
            }

            string loadPath = string.Empty;
            if (selection.Mode == CoopLobbyMode.Load)
            {
                loadPath = ResolveMenuSavePath(selection.SaveName);
                if (!File.Exists(loadPath)) return "KAYIT BULUNAMADI";
            }

            if (!Send(CoopLobbyProtocol.CreateWorldCommitPacket(selection, 0)))
                return "START GONDERILEMEDI";
            NewGameRequested = false;
            mMenuLoadRequested = selection.Mode == CoopLobbyMode.Load;
            mMenuLoadSavePath = loadPath;
            mMenuPlanetIndex = selection.PlanetIndex;
            mLobbyStarted = true;
            LobbyPhase = CoopLobbyPhase.WorldCommitted;
            CoopStatusOverlay.Notify("KAYITLI ORTAK DÜNYA BAŞLATILIYOR");
            CoopPlugin.Log.LogInfo(
                "[Coop] host kayittan START yayinladi: mod=" + selection.Mode +
                " kayit='" + selection.SaveName + "'");
            return "";
        }

        /// <summary>Misafir onayı; host tarafında START kapısını açan tek yetki.</summary>
        public void SetGuestLobbyReady(bool ready)
        {
            if (Role != "guest" || !mLobbyActive || mLobbyStarted ||
                !CoopLobbyPolicy.ShouldAcceptLobbyReady(LobbyPhase)) return;
            if (mLocalLobbyReadySent == ready) return;
            mLocalLobbyReadySent = ready;
            Send(CoopLobbyProtocol.CreateReadyPacket(ready));
            CoopStatusOverlay.Notify(ready ? "HOST ONAYI BEKLENIYOR" : "HAZIR İPTAL EDİLDİ");
        }

        public CoopLobbyInfo GetLobbyInfo()
        {
            CoopLobbyInfo info = new CoopLobbyInfo();
            info.Role = Role;
            info.SessionActive = FullMode && Instance != null && Instance.mRunning && !Instance.mStartupFailed;
            info.LobbyActive = mLobbyActive && !mLobbyStarted;
            info.Started = mLobbyStarted;
            info.Connected = mHandshakeDone &&
                (Role == "guest" || mGuestConnected);
            info.HasAnySave = SaveData.anySaveGames();
            info.Phase = LobbyPhase;
            info.HostAddress = mLobbyDisplayAddress;
            info.Port = Port;
            lock (mLobbyLock)
            {
                if (Role == "host")
                {
                    info.Selection = mHostLobbySelection;
                    info.ReadyVisible = mGuestLobbyReady;
                    info.SelectionView = mHostLobbySelectionView;
                    info.SelectionScreenReady = CoopLobbyPolicy.IsSelectionScreenReady(
                        mHostLobbySelectionView.Revision,
                        mGuestSelectionLoadedRevision);
                }
                else
                {
                    info.Selection = mRemoteLobbySelection;
                    info.ReadyVisible = mLocalLobbyReadySent;
                    info.StartSeen = mRemoteLobbyStartSeen;
                    info.SelectionView = mRemoteLobbySelectionView;
                    info.SelectionScreenReady = CoopLobbyPolicy.IsSelectionScreenReady(
                        mRemoteLobbySelectionView.Revision,
                        mLocalSelectionLoadedRevision);
                }
            }
            return info;
        }

        private static string ResolveMenuSavePath(string saveName)
        {
            return CoopSaveCatalog.ResolvePath(SaveData.FolderName, saveName);
        }

        private void CacheReflection()
        {
            try
            {
                Type gmType = typeof(GameManager);
                sGameStateField = gmType.GetField("mGameState",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sStateField = gmType.GetField("mState",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sOnStartUpdating = typeof(GameState).GetMethod("onStartUpdating",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                sLoadGameFileField = typeof(GameStateGame).GetField("mLoadGameFile",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sTerrainSeedField = typeof(TerrainGenerator).GetField("mSeed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sFrameIndexField = gmType.GetField("mFrameIndex",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sNextIdField = typeof(IdGenerator).GetField("mNextId",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sNextBotIdField = typeof(IdGenerator).GetField("mNextBotId",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sLandingShipTickTimeField = typeof(LandingShipManager).GetField("mTickTime",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                System.Reflection.BindingFlags envFlags =
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                sTimeToWindChangeField = typeof(EnvironmentManager).GetField("mTimeToWindChange", envFlags);
                sWindOffsetField = typeof(EnvironmentManager).GetField("mWindOffset", envFlags);
                sWindDirectionField = typeof(EnvironmentManager).GetField("mWindDirection", envFlags);
                if (sGameStateField == null || sStateField == null || sOnStartUpdating == null ||
                    sLoadGameFileField == null || sFrameIndexField == null ||
                    sLandingShipTickTimeField == null)
                {
                    throw new MissingMemberException("GameManager lifecycle fields/methods not found");
                }
                if (sTimeToWindChangeField == null || sWindOffsetField == null ||
                    sWindDirectionField == null)
                {
                    CoopPlugin.Log.LogWarning("[Coop] environment ruzgar fazi alanlari bulunamadi — deterministik ruzgar devre disi");
                }
                CoopPlugin.Log.LogInfo("[Coop] Reflection cache hazır — Waiting/Updating geçişi isimle çözümlenecek");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Reflection cache başarısız: " + ex.Message);
            }
        }

        private static void ParseArgs()
        {
            if (sMenuInitiated) return;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-coopRole" && i + 1 < args.Length) Role = args[i + 1].ToLowerInvariant();
                if (args[i] == "-coopPort" && i + 1 < args.Length) int.TryParse(args[i + 1], out Port);
                if (args[i] == "-coopHost" && i + 1 < args.Length) HostIp = args[i + 1];
                if (args[i] == "-coopProfile" && i + 1 < args.Length)
                {
                    string requestedProfile = args[i + 1];
                    if (CoopProtocol.IsSafeProfileName(requestedProfile))
                    {
                        Profile = requestedProfile;
                    }
                    else if (CoopPlugin.Log != null)
                    {
                        CoopPlugin.Log.LogWarning("[Coop] geçersiz -coopProfile değeri reddedildi");
                    }
                }
                if (args[i] == "-coopNewGame" && i + 1 < args.Length)
                {
                    NewGameRequested = true;
                    int? fixedSeed;
                    if (!CoopSeedArgument.TryParse(args[i + 1], out fixedSeed))
                    {
                        CoopPlugin.Log?.LogWarning("[Coop] gecersiz -coopNewGame degeri reddedildi");
                        NewGameRequested = false;
                        NewGameSeed = -1;
                    }
                    else
                    {
                        NewGameSeed = fixedSeed.HasValue ? fixedSeed.Value : -1;
                    }
                }
                if (args[i] == "-coopStartTimeout" && i + 1 < args.Length) int.TryParse(args[i + 1], out StartTimeoutSeconds);
                if (args[i] == "-coopFullscreen") Fullscreen = true;
                if (args[i] == "-coopWindowed") Fullscreen = false;
            }
            if (Role != "host" && Role != "guest") Role = "";
            if (Port < 1 || Port > 65535) Port = DefaultPort;
            if (StartTimeoutSeconds < 15 || StartTimeoutSeconds > 3600) StartTimeoutSeconds = DefaultStartTimeoutSeconds;
            if (Role == "guest" && Profile.Length == 0) Profile = "coop-guest";
        }

        private int ResolveNewGameSeed()
        {
            if (mNewGameSeedResolved) return mResolvedNewGameSeed;
            mNewGameSeedResolved = true;
            int randomSeed = -1;
            if (!mMenuNewGameSeedExplicit && NewGameSeed < 0)
            {
                byte[] bytes = new byte[4];
                using (System.Security.Cryptography.RandomNumberGenerator rng =
                    System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(bytes);
                }
                // Vanilla tohumu float yazip int okur; 10 milyon ustu "1.03E+09"
                // olarak kaydedilir ve o kayit bir daha acilamaz. Vanilla aralik: +-180000.
                randomSeed = (BitConverter.ToInt32(bytes, 0) & int.MaxValue) % 180000;
            }
            mResolvedNewGameSeed = CoopNewGameSeedPolicy.Resolve(
                mMenuNewGameSeedExplicit,
                NewGameSeed,
                randomSeed);
            if (!mMenuNewGameSeedExplicit && NewGameSeed < 0)
            {
                CoopPlugin.Log.LogInfo("[Coop] yeni dunya icin rastgele seed secildi: " +
                    mResolvedNewGameSeed);
            }
            return mResolvedNewGameSeed;
        }

        public static string GetFilesFolderOverride(string original)
        {
            if (Profile.Length > 0)
            {
                string dir = original + "/" + Profile;
                try { Directory.CreateDirectory(dir); } catch (Exception) { }
                return dir;
            }
            return original;
        }

        // ==================== RNG PER-TICK SEEDING ====================

        /// <summary>
        /// Her fixedUpdate başında GameManager.fixedUpdate prefix'inden çağrılır.
        /// Her tick kendi sabit RNG tohumunu üretir → tarihsel bağımsızlık.
        /// Yani önceki tick'te render kodu Random tüketti bile olsa, sim her tick'te
        /// bilinen bir tohumla başlar. Aynı tick = aynı tohum = aynı sonuçlar.
        /// </summary>
        public void SeedRngForTick()
        {
            int tickSeed = SyncedSeed + (int)TickIndex;
            UnityEngine.Random.InitState(tickSeed);
        }

        // ==================== AUTO-ADVANCE FROM WAITING ====================

        /// <summary>
        /// GameManager.fixedUpdate içinde State.Waiting durumunu tespit edip
        /// otomatik olarak onStartUpdating() çağırır. Böylece "please click" ekranı
        /// otomatik geçilir — misafir tarafında manuel tıklama gerekmez.
        /// </summary>
        private bool TryGetGameState(out GameManager gm, out GameState gameState, out string stateName)
        {
            gm = null;
            gameState = null;
            stateName = null;
            if (sStateField == null || sGameStateField == null) return false;
            try
            {
                gm = GameManager.getInstance();
                if (gm == null) return false;
                gameState = sGameStateField.GetValue(gm) as GameState;
                object state = sStateField.GetValue(gm);
                stateName = state == null ? null : state.ToString();
                return gameState != null && stateName != null;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] lifecycle okunamadı: " + ex.Message);
                return false;
            }
        }

        public bool IsWaitingForStart()
        {
            GameManager gm;
            GameState state;
            string stateName;
            return TryGetGameState(out gm, out state, out stateName) &&
                state is GameStateGame && StringComparer.Ordinal.Equals(stateName, "Waiting");
        }

        public bool ShouldBlockVanillaStart()
        {
            return !mStartupFailed && IsWaitingForStart() && !mStartApplied;
        }

        /// <summary>
        /// Lobi aktifken ve host START kararı verilmeden vanilya menü
        /// geçişlerini (yeni oyun/continue/load) engeller; böylece hiçbir
        /// taraf lobi kararı dışında tek başına dünya kuramaz.
        /// </summary>
        public static bool ShouldBlockVanillaMenuTransitions()
        {
            return Instance != null && FullMode &&
                Instance.mLobbyActive && !Instance.mLobbyStarted;
        }

        private static bool IsFreshGameState(GameState state)
        {
            if (!(state is GameStateGame) || sLoadGameFileField == null) return false;
            try
            {
                return string.IsNullOrEmpty(sLoadGameFileField.GetValue(state) as string);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] dunya kaynagi okunamadi: " + ex.Message);
                return false;
            }
        }

        private bool TryApplyAuthorizedStart()
        {
            if (mStartApplied || !TryGetGameState(out GameManager gm, out GameState gameState, out string stateName))
                return false;
            if (!(gameState is GameStateGame) || !StringComparer.Ordinal.Equals(stateName, "Waiting"))
                return false;
            CoopRole localRole = Role == "guest" ? CoopRole.Guest : CoopRole.Host;
            lock (mStartupLock)
            {
                if (mStartup == null ||
                    !CoopStartAuthorizationPolicy.CanApplyAuthorizedStart(
                        localRole,
                        mStartup.Phase)) return false;
            }
            try
            {
                CoopDeterministicRuntime.ResetSessionState();
                if (!ApplyAuthoritativeFrameIndex())
                    throw new InvalidOperationException("authoritative frame index could not be restored");
                if (CoopAuthorizedStartTimePolicy.ShouldRestore(
                        localRole, mRecoveryPending) &&
                    !CoopTimeSync.Apply(mAuthoritativePaused, mAuthoritativeScaleIndex))
                    throw new InvalidOperationException("authoritative time state could not be restored");
                sOnStartUpdating.Invoke(gameState, null);
                object updating = Enum.Parse(sStateField.FieldType, "Updating");
                sStateField.SetValue(gm, updating);
                mStartApplied = true;
                mHostWaitingSinceUtc = null;
                CoopPlugin.Log.LogInfo("[Coop] ortak start uygulandı: Waiting -> Updating");
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] ortak start uygulanamadı: " + ex.Message);
                FailStartup("start transition failed", true);
                return false;
            }
        }

        /// <summary>
        /// Misafir, hostun lobiden gelen START kararını uygular (tek seferlik).
        /// Guest geçici dünya üretmez; hostun ilk snapshot transferini bekler.
        /// Böylece ColonyShip parçacıkları canlıyken ikinci bir dünya yüklenmez.
        /// </summary>
        private void ApplyGuestLobbyStart()
        {
            CoopLobbySelection selection;
            CoopLobbySelectionView view;
            long appliedRevision;
            lock (mLobbyLock)
            {
                selection = mRemoteLobbySelection;
                view = mRemoteLobbySelectionView;
                appliedRevision = mLocalSelectionLoadedRevision;
            }
            if (!CoopLobbyPolicy.CanAcceptWorldCommit(
                    Role, mLobbyActive, mLobbyStarted, LobbyPhase,
                    selection, view, appliedRevision))
            {
                FailStartup("lobi start secimi veya revision gecersiz", true);
                return;
            }
            if (!CoopLobbyPolicy.ShouldWaitForAuthoritativeSnapshot(
                    Role, selection.Mode))
            {
                FailStartup("guest lobi start modu desteklenmiyor", true);
                return;
            }
            mLobbyStarted = true;
            LobbyPhase = CoopLobbyPhase.WorldCommitted;
            mMenuPlanetIndex = selection.PlanetIndex;
            CoopPlugin.Log.LogInfo(
                "[Coop] guest: lobiden start alindi; gecici dunya kurulmadan host snapshot bekleniyor (mod=" +
                selection.Mode + ")");
            CoopStatusOverlay.Notify("HOST DÜNYASI BEKLENİYOR");
        }

        private bool TryStartHostBootstrap(GameManager gm)
        {
            if (Role != "host" || !mHostFreshWorld || mHostBootstrapStarted || !IsWaitingForStart())
                return false;
            if (gm == null || sOnStartUpdating == null || sStateField == null)
                return false;
            try
            {
                mHostBootstrapStarted = true;
                mHostWorldStable = false;
                sOnStartUpdating.Invoke(gm.getGameState(), null);
                object updating = Enum.Parse(sStateField.FieldType, "Updating");
                sStateField.SetValue(gm, updating);
                CoopPlugin.Log.LogInfo("[Coop] host bootstrap basladi; dunya misafirden bagimsiz kuruluyor");
                return true;
            }
            catch (Exception ex)
            {
                mHostBootstrapStarted = false;
                CoopPlugin.Log.LogError("[Coop] host bootstrap baslatilamadi: " + ex.Message);
                FailStartup("host bootstrap transition failed", false);
                return false;
            }
        }

        private bool IsHostBootstrapWorldStable()
        {
            if (Role != "host" || !GameLoaded) return false;
            if (!mHostFreshWorld) return true;
            try
            {
                ColonyShip ship = Ship.getFirstOfType<ColonyShip>();
                CameraManager camera = CameraManager.getInstance();
                return CoopHostBootstrapPolicy.CanPublishInitialSnapshot(
                    isHost: true,
                    worldLoaded: GameLoaded,
                    isFreshWorld: true,
                    colonyShipPresent: ship != null,
                    colonyShipDone: ship != null && ship.isDone(),
                    characterCount: Character.getCount(),
                    cinematicActive: camera != null && camera.isCinematic(),
                    snapshotPublished: mInitialSaveSent);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] host dunya kararlilik kontrolu basarisiz: " + ex.Message);
                return false;
            }
        }

        // ==================== AĞ ====================

        private void Start()
        {
            if (mNetThread != null && mNetThread.IsAlive)
            {
                mStartupFailed = true;
                CoopPlugin.Log.LogError(
                    "[Coop] onceki ag thread'i kapanmadigi icin yeni oturum baslatilmadi");
                return;
            }
            mRunning = true;
            ThreadStart ts;
            if (UsingSteamTransport)
                ts = Role == "host"
                    ? new ThreadStart(SteamHostLoop)
                    : new ThreadStart(SteamGuestLoop);
            else
                ts = Role == "host"
                    ? new ThreadStart(HostLoop)
                    : new ThreadStart(GuestLoop);
            mNetThread = new Thread(ts);
            mNetThread.IsBackground = true;
            mNetThread.Name = "CoopSync";
            mNetThread.Start();
        }

        private void StopNetworkThread()
        {
            mRunning = false;
            TcpListener listener = mListener;
            NetworkStream stream = mStream;
            TcpClient client = mClient;
            try { listener?.Stop(); } catch (Exception) { }
            try { stream?.Close(); } catch (Exception) { }
            try { client?.Close(); } catch (Exception) { }

            Thread thread = mNetThread;
            if (thread != null && thread != Thread.CurrentThread && thread.IsAlive)
            {
                try
                {
                    if (!thread.Join(2500))
                    {
                        CoopPlugin.Log.LogWarning(
                            "[Coop] ag thread'i kapanma suresini asti");
                    }
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning(
                        "[Coop] ag thread'i beklenemedi: " + ex.Message);
                }
            }

            if (thread == null || !thread.IsAlive) mNetThread = null;
            if (ReferenceEquals(mListener, listener)) mListener = null;
            if (ReferenceEquals(mStream, stream)) mStream = null;
            if (ReferenceEquals(mClient, client)) mClient = null;
        }

        private void HostLoop()
        {
            try
            {
                mListener = new TcpListener(IPAddress.Any, Port);
                mListener.Start();
                CoopPlugin.Log.LogInfo("[Coop] SENKRON host dinliyor: port " + Port);
                while (mRunning)
                {
                    TcpClient client = mListener.AcceptTcpClient();
                    CoopPlugin.Log.LogInfo("[Coop] SENKRON misafir bağlandı: " + client.Client.RemoteEndPoint);
                    HandleClient(client);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] SENKRON host hatası: " + ex.Message);
            }
        }

        private static byte[] CreateSteamHandshakePacket()
        {
            byte[] magic = Encoding.ASCII.GetBytes(Magic);
            byte[] packet = new byte[magic.Length + 1];
            Buffer.BlockCopy(magic, 0, packet, 0, magic.Length);
            packet[packet.Length - 1] = (byte)ProtocolVersion;
            return packet;
        }

        private static bool IsValidSteamHandshake(byte[] packet)
        {
            byte[] magic = Encoding.ASCII.GetBytes(Magic);
            if (packet == null || packet.Length != magic.Length + 1 ||
                packet[packet.Length - 1] != ProtocolVersion) return false;
            for (int i = 0; i < magic.Length; i++)
                if (packet[i] != magic[i]) return false;
            return true;
        }

        private void SteamHostLoop()
        {
            CoopPlugin.Log.LogInfo("[Coop] Steam relay host misafir bekliyor");
            try
            {
                while (mRunning)
                {
                    byte[] hello;
                    if (!CoopSteamService.TryReceivePacket(out hello, 250)) continue;
                    ulong peerId = CoopSteamService.ExpectedPeerId;
                    if (peerId == 0 || !IsValidSteamHandshake(hello))
                        throw new InvalidDataException("Steam protocol/version mismatch");
                    if (!CoopSteamService.QueuePacket(CreateSteamHandshakePacket()))
                        throw new IOException("Steam handshake reply could not be queued");

                    mHandshakeDone = true;
                    mGuestConnected = true;
                    mConnectionLost = false;
                    ResetNetworkTiming();
                    Interlocked.Exchange(ref mLastPeerPacketUtcTicks, DateTime.UtcNow.Ticks);
                    CoopStatusOverlay.SetConnectionWarning(false);
                    if (mLobbyActive) CoopStatusOverlay.Notify("MİSAFİR STEAM ÜZERİNDEN BAĞLANDI");
                    CoopPlugin.Log.LogInfo("[Coop] Steam relay el sıkışması tamam: peer=" +
                        peerId + " protocol=v" + ProtocolVersion);
                    PublishCurrentLobbySelectionToGuest();

                    while (mRunning && CoopSteamService.ExpectedPeerId == peerId)
                    {
                        byte[] payload;
                        if (CoopSteamService.TryReceivePacket(out payload, 250))
                            ProcessPacket(payload);
                    }

                    mHandshakeDone = false;
                    mGuestConnected = false;
                    mGuestLobbyReady = false;
                    lock (mLobbyLock) mGuestSelectionLoadedRevision = 0;
                    if (mRunning && (IsSessionRunning() ||
                        (mLobbyActive && LobbyPhase != CoopLobbyPhase.Waiting)))
                        mConnectionLost = true;
                    else
                        ResetHostStartupForReconnect();
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Steam relay host kapandı: " + ex.Message);
                if (mRunning) mConnectionLost = true;
            }
            finally
            {
                mHandshakeDone = false;
                mGuestConnected = false;
            }
        }

        private void SteamGuestLoop()
        {
            CoopPlugin.Log.LogInfo("[Coop] Steam relay guest hosta bağlanıyor: " +
                CoopSteamService.ExpectedPeerId);
            try
            {
                byte[] hello = CreateSteamHandshakePacket();
                while (mRunning && !mHandshakeDone)
                {
                    if (!CoopSteamService.QueuePacket(hello))
                        throw new IOException("Steam handshake could not be queued");
                    DateTime retryAt = DateTime.UtcNow.AddSeconds(2);
                    while (mRunning && DateTime.UtcNow < retryAt)
                    {
                        byte[] reply;
                        if (!CoopSteamService.TryReceivePacket(out reply, 250)) continue;
                        if (!IsValidSteamHandshake(reply))
                            throw new InvalidDataException("Steam protocol/version mismatch");
                        mHandshakeDone = true;
                        mConnectionLost = false;
                        ResetNetworkTiming();
                        Interlocked.Exchange(ref mLastPeerPacketUtcTicks, DateTime.UtcNow.Ticks);
                        CoopStatusOverlay.SetConnectionWarning(false);
                        CoopPlugin.Log.LogInfo("[Coop] Steam relay el sıkışması tamam — protokol v" +
                            ProtocolVersion);
                        break;
                    }
                }
                while (mRunning && mHandshakeDone)
                {
                    byte[] payload;
                    if (CoopSteamService.TryReceivePacket(out payload, 250))
                        ProcessPacket(payload);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Steam relay guest kapandı: " + ex.Message);
                if (mRunning) mConnectionLost = true;
            }
            finally
            {
                mHandshakeDone = false;
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                mClient = client;
                client.NoDelay = true;
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                NetworkStream stream = client.GetStream();
                lock (mSendLock)
                {
                    byte[] magic = Encoding.ASCII.GetBytes(Magic);
                    stream.Write(magic, 0, magic.Length);
                    stream.WriteByte((byte)ProtocolVersion);
                    stream.Flush();
                }
                byte[] peerMagic = new byte[Magic.Length];
                if (ReadFully(stream, peerMagic, 0, peerMagic.Length) != peerMagic.Length ||
                    !StringComparer.Ordinal.Equals(Encoding.ASCII.GetString(peerMagic), Magic) ||
                    stream.ReadByte() != ProtocolVersion)
                {
                    throw new InvalidDataException("protocol/version mismatch");
                }
                mStream = stream;
                mHandshakeDone = true;
                mGuestConnected = true;
                mConnectionLost = false;
                ResetNetworkTiming();
                Interlocked.Exchange(ref mLastPeerPacketUtcTicks, DateTime.UtcNow.Ticks);
                CoopStatusOverlay.SetConnectionWarning(false);
                if (mLobbyActive) CoopStatusOverlay.Notify("MİSAFİR BAĞLANDI");
                CoopPlugin.Log.LogInfo("[Coop] el sıkışma tamam — protokol v" + ProtocolVersion);
                PublishCurrentLobbySelectionToGuest();
                byte[] lenBuf = new byte[4];
                while (mRunning && mStream != null)
                {
                    if (ReadFully(lenBuf, 0, 4) != 4) break;
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (!CoopProtocol.IsValidFrameLength(len)) break;
                    int firstByte = mStream.ReadByte();
                    if (firstByte < 0 ||
                        !CoopProtocol.IsValidFrameLengthForType((byte)firstByte, len)) break;
                    byte[] payload = new byte[len];
                    payload[0] = (byte)firstByte;
                    if (len > 1 && ReadFully(payload, 1, len - 1) != len - 1) break;
                    ProcessPacket(payload);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] misafir bağlantısı kapandı: " + ex.Message);
            }
            finally
            {
                mHandshakeDone = false;
                mGuestConnected = false;
                mGuestLobbyReady = false;
                lock (mLobbyLock) mGuestSelectionLoadedRevision = 0;
                lock (mSendLock)
                {
                    if (ReferenceEquals(mClient, client))
                    {
                        try { mStream?.Close(); } catch (Exception) { }
                        try { mClient?.Close(); } catch (Exception) { }
                        mStream = null;
                        mClient = null;
                    }
                }
                if (IsSessionRunning() ||
                    (mLobbyActive && LobbyPhase != CoopLobbyPhase.Waiting))
                    mConnectionLost = true;
                else ResetHostStartupForReconnect();
                CoopPlugin.Log.LogInfo("[Coop] misafir ayrıldı");
            }
        }

        private void GuestLoop()
        {
            CoopPlugin.Log.LogInfo("[Coop] SENKRON misafir bağlanıyor: " + HostIp + ":" + Port);
            while (mRunning)
            {
                bool connectedThisAttempt = false;
                bool stopAfterDisconnect = false;
                try
                {
                    mClient = new TcpClient();
                    mClient.NoDelay = true;
                    mClient.ReceiveTimeout = 10000;
                    mClient.SendTimeout = 10000;
                    mClient.Connect(HostIp, Port);
                    NetworkStream stream = mClient.GetStream();
                    byte[] magic = new byte[Magic.Length];
                    if (ReadFully(stream, magic, 0, Magic.Length) != Magic.Length ||
                        !StringComparer.Ordinal.Equals(Encoding.ASCII.GetString(magic), Magic))
                    {
                        CoopPlugin.Log.LogError("[Coop] el sıkışma hatası");
                        throw new Exception("handshake mismatch");
                    }
                    int ver = stream.ReadByte();
                    if (ver != ProtocolVersion)
                    {
                        throw new InvalidDataException("protocol version mismatch: " + ver);
                    }
                    byte[] responseMagic = Encoding.ASCII.GetBytes(Magic);
                    stream.Write(responseMagic, 0, responseMagic.Length);
                    stream.WriteByte((byte)ProtocolVersion);
                    stream.Flush();
                    mStream = stream;
                    mHandshakeDone = true;
                    connectedThisAttempt = true;
                    mConnectionLost = false;
                    ResetNetworkTiming();
                    Interlocked.Exchange(ref mLastPeerPacketUtcTicks, DateTime.UtcNow.Ticks);
                    CoopStatusOverlay.SetConnectionWarning(false);
                    CoopPlugin.Log.LogInfo("[Coop] el sıkışma tamam — protokol v" + ver);
                    byte[] lenBuf = new byte[4];
                    while (mRunning && mStream != null)
                    {
                        if (ReadFully(lenBuf, 0, 4) != 4) break;
                        int len = BitConverter.ToInt32(lenBuf, 0);
                        if (!CoopProtocol.IsValidFrameLength(len)) break;
                        int firstByte = mStream.ReadByte();
                        if (firstByte < 0 ||
                            !CoopProtocol.IsValidFrameLengthForType((byte)firstByte, len)) break;
                        byte[] payload = new byte[len];
                        payload[0] = (byte)firstByte;
                        if (len > 1 && ReadFully(payload, 1, len - 1) != len - 1) break;
                        ProcessPacket(payload);
                    }
                    if (connectedThisAttempt && mLobbyActive)
                    {
                        mConnectionLost = true;
                        stopAfterDisconnect = true;
                    }
                    else if (IsSessionRunning())
                    {
                        mConnectionLost = true;
                        stopAfterDisconnect = true;
                    }
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning("[Coop] bağlantı başarısız: " + ex.Message + " — 2 sn");
                    if (connectedThisAttempt && mLobbyActive)
                    {
                        mConnectionLost = true;
                        stopAfterDisconnect = true;
                    }
                }
                finally
                {
                    mHandshakeDone = false;
                    try { mStream?.Close(); } catch (Exception) { }
                    try { mClient?.Close(); } catch (Exception) { }
                    mStream = null;
                    mClient = null;
                }
                if (stopAfterDisconnect) break;
                if (mRunning) Thread.Sleep(2000);
            }
        }

        private static int ReadFully(byte[] buf, int offset, int count)
        {
            return ReadFully(Instance.mStream, buf, offset, count);
        }

        private static int ReadFully(NetworkStream stream, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buf, offset + total, count - total);
                if (n <= 0) return total;
                total += n;
            }
            return total;
        }

        private void ProcessPacket(byte[] payload)
        {
            if (payload == null || !CoopProtocol.IsValidFrameLength(payload.Length) || payload.Length < 1) return;
            if (TryProcessFastTransportPacket(payload)) return;
            MarkPeerPacketReceived();
            byte type = payload[0];
            if (type == PktWorld)
            {
                if (Role != "guest") return;
                if (payload.Length <= CoopSnapshotInvariant.WorldPacketHeaderBytes)
                {
                    mInvalidWorldPacket = true;
                    return;
                }
                lock (mPendingWorldLock)
                {
                    mPendingWorldPacket = (byte[])payload.Clone();
                }
            }
            else if (type == PktHash)
            {
                long tick;
                string hash;
                long localTick = Interlocked.Read(ref TickIndex);
                if (CoopProtocol.TryParseHashPacket(payload, out tick, out hash) &&
                    CoopProtocol.IsPlausibleHashTick(tick, localTick))
                {
                    OnHashReceived(tick, hash);
                }
            }
            else if (type == PktCamera)
            {
                // Partner camera/PIP is intentionally disabled in full lockstep mode.
            }
            else if (type == CoopLobbyProtocol.PktLobbyState)
            {
                CoopLobbySelection selection;
                if (Role == "guest" &&
                    CoopLobbyProtocol.TryParseStatePacket(payload, out selection))
                {
                    lock (mLobbyLock)
                    {
                        if (CoopLobbyPolicy.ShouldResetGuestReady(
                                mRemoteLobbySelection, selection))
                            mLocalLobbyReadySent = false;
                        mRemoteLobbySelection = selection;
                    }
                }
            }
            else if (type == CoopLobbyProtocol.PktLobbyReady)
            {
                bool ready;
                if (Role == "host" &&
                    LobbyPhase == CoopLobbyPhase.Waiting &&
                    CoopLobbyProtocol.TryParseReadyPacket(payload, out ready))
                {
                    mGuestLobbyReady = ready;
                    if (mLobbyActive)
                    {
                        CoopStatusOverlay.Notify(ready
                            ? "MİSAFİR HAZIR"
                            : "MİSAFİR HAZIRLIĞI İPTAL ETTİ");
                    }
                    CoopPlugin.Log.LogInfo("[Coop] misafir lobi hazirligi: " + ready);
                }
            }
            else if (type == CoopLobbyProtocol.PktLobbyStart)
            {
                CoopLobbySelection selection;
                long committedRevision;
                if (Role == "guest" &&
                    CoopLobbyProtocol.TryParseWorldCommitPacket(
                        payload, out selection, out committedRevision))
                {
                    CoopLobbySelectionView view;
                    long appliedRevision;
                    lock (mLobbyLock)
                    {
                        view = mRemoteLobbySelectionView;
                        appliedRevision = mLocalSelectionLoadedRevision;
                    }
                    if (CoopLobbyPolicy.CanAcceptWorldCommit(
                            Role, mLobbyActive, mLobbyStarted, LobbyPhase,
                            selection, view, appliedRevision) &&
                        CoopLobbyPolicy.IsCommitRevisionValid(
                            selection, committedRevision, view.Revision))
                    {
                        lock (mLobbyLock) mRemoteLobbySelection = selection;
                        mRemoteLobbyStartSeen = true;
                        CoopStatusOverlay.Notify("HOST ORTAK DÜNYAYI BAŞLATTI");
                    }
                    else
                    {
                        CoopPlugin.Log.LogWarning(
                            "[Coop] yanlış lobi phase/revision içindeki START reddedildi");
                    }
                }
            }
            else if (type == CoopLobbyProtocol.PktLobbySelectionView)
            {
                CoopLobbySelectionView view;
                if (Role == "guest" &&
                    CoopLobbyProtocol.TryParseSelectionViewPacket(payload, out view))
                {
                    lock (mLobbyLock)
                    {
                        if (view.Revision > mRemoteLobbySelectionView.Revision)
                            mRemoteLobbySelectionView = view;
                    }
                }
            }
            else if (type == CoopLobbyProtocol.PktLobbyEnterSelection)
            {
                if (Role == "guest" &&
                    CoopLobbyProtocol.IsEnterSelectionPacket(payload) &&
                    mLobbyActive && !mLobbyStarted)
                {
                    LobbyPhase = CoopLobbyPhase.Selecting;
                    CoopStatusOverlay.Notify("HOST GEZEGEN SEÇİMİNİ BAŞLATTI");
                }
            }
            else if (type == CoopLobbyProtocol.PktLobbySelectionLoaded)
            {
                long revision;
                if (Role == "host" && LobbyPhase == CoopLobbyPhase.Selecting &&
                    CoopLobbyProtocol.TryParseSelectionLoadedPacket(payload, out revision))
                {
                    lock (mLobbyLock)
                    {
                        if (revision == mHostLobbySelectionView.Revision &&
                            revision > mGuestSelectionLoadedRevision)
                            mGuestSelectionLoadedRevision = revision;
                    }
                }
            }
            else if (type == PktPlacementPreview)
            {
                PlacementSync.Instance?.QueuePreviewPacket(payload);
            }
            else if (type == PktPlacementRequest)
            {
                if (Role == "host") PlacementSync.Instance?.QueuePlacementRequest(payload);
            }
            else if (type == PktPlacementResult)
            {
                if (Role == "guest") PlacementSync.Instance?.QueuePlacementResult(payload);
            }
            else if (type == PktSelection)
            {
                PlacementSync.Instance?.QueueSelectionPacket(payload);
            }
            else if (type == PktConnectionRequest)
            {
                if (Role == "host") PlacementSync.Instance?.QueueConnectionRequest(payload);
            }
            else if (type == PktConnectionResult)
            {
                if (Role == "guest") PlacementSync.Instance?.QueueConnectionResult(payload);
            }
            else if (type == PktRecycleRequest)
            {
                if (Role == "host") PlacementSync.Instance?.QueueRecycleRequest(payload);
            }
            else if (type == PktRecycleResult)
            {
                if (Role == "guest") PlacementSync.Instance?.QueueRecycleResult(payload);
            }
            else if (type == PktGameOverRequest)
            {
                long session;
                long tick;
                long activeSession;
                if (Role == "host" &&
                    CoopProtocol.TryParseGameOverRequestPacket(payload, out session, out tick) &&
                    TryGetRunningSessionId(out activeSession) &&
                    CoopCommandSessionPolicy.CanAccept(true, activeSession, session))
                {
                    Interlocked.Exchange(
                        ref mPendingGameOverRequest,
                        new PendingGameOverEvent(session, tick));
                }
            }
            else if (type == PktGameOverState)
            {
                long session;
                long tick;
                long activeSession;
                if (Role == "guest" &&
                    CoopProtocol.TryParseGameOverStatePacket(payload, out session, out tick) &&
                    TryGetRunningSessionId(out activeSession) &&
                    CoopCommandSessionPolicy.CanAccept(true, activeSession, session))
                {
                    Interlocked.Exchange(
                        ref mPendingGameOverState,
                        new PendingGameOverEvent(session, tick));
                }
            }
            else if (type == PktShipSpawn)
            {
                if (Role == "guest") CoopShipSync.QueueSpawnPacket(payload);
            }
            else if (type == PktLandingPermissions)
            {
                if (Role == "guest") CoopLandingPermissionsSync.QueuePacket(payload);
            }
            else if (type == PktMutationFailure)
            {
                CoopMutationFailureData failure;
                long activeSessionId;
                if (Role == "host" &&
                    CoopProtocol.TryParseMutationFailurePacket(payload, out failure) &&
                    TryGetRunningSessionId(out activeSessionId) &&
                    failure.SessionId == activeSessionId)
                {
                    lock (mMutationFailureLock)
                    {
                        if (mPendingMutationFailures.Count < CoopMutationFailureLedger.MaxEntries)
                            mPendingMutationFailures.Enqueue(failure);
                    }
                }
            }
            else if (type == PktAuthoritativeCommand)
            {
                CoopGameplayControlSync.QueuePacket(payload);
            }
            else if (type == PktReady)
            {
                OnReadyReceived(payload);
            }
            else if (type == PktStart)
            {
                long session;
                long tick;
                bool paused;
                int scaleIndex;
                if (CoopProtocol.TryParseStartPacket(
                        payload, out session, out tick, out paused, out scaleIndex))
                {
                    lock (mStartupLock)
                    {
                        if (mStartup != null && mStartup.AcceptStart(session, tick))
                        {
                            mAuthoritativePaused = paused;
                            mAuthoritativeScaleIndex = scaleIndex;
                            CoopPlugin.Log.LogInfo("[Coop] host start paketi alındı");
                        }
                    }
                }
            }
            else if (type == PktAbort)
            {
                mAbortRequested = true;
            }
        }

        public bool TryProcessFastTransportPacket(byte[] payload)
        {
            if (payload == null || payload.Length < 1 ||
                !CoopProtocol.IsValidFrameLength(payload.Length)) return false;
            byte type = payload[0];
            if (type != PktTick && type != PktHeartbeat) return false;

            MarkPeerPacketReceived();
            if (type == PktTick)
            {
                long tick;
                long localTick = Interlocked.Read(ref TickIndex);
                long previousPeerTick = Interlocked.Read(ref PeerTick);
                if (CoopProtocol.TryParseTickPacket(payload, out tick) &&
                    CoopProtocol.IsPlausibleHashTick(tick, localTick) &&
                    tick >= previousPeerTick)
                {
                    Interlocked.Exchange(ref PeerTick, tick);
                    mTickGatePeerSampleReady = true;
                }
                return true;
            }

            if (type == PktHeartbeat)
            {
                bool reply;
                long sequence;
                long sentUtcTicks;
                if (!CoopProtocol.TryParseHeartbeatPacket(
                        payload, out reply, out sequence, out sentUtcTicks)) return true;
                if (!reply)
                {
                    if (!Send(CoopProtocol.CreateHeartbeatPacket(
                            true, sequence, sentUtcTicks)))
                        mConnectionLost = true;
                    return true;
                }

                long lastSentSequence = Interlocked.Read(ref mLastHeartbeatSequenceSent);
                long elapsedTicks = DateTime.UtcNow.Ticks - sentUtcTicks;
                if (sequence > lastSentSequence || sequence < lastSentSequence - 4 ||
                    elapsedTicks <= 0 || elapsedTicks > TimeSpan.TicksPerSecond * 10) return true;
                int sampleMilliseconds = (int)Math.Max(1,
                    (elapsedTicks + TimeSpan.TicksPerMillisecond / 2) /
                    TimeSpan.TicksPerMillisecond);
                RecordLatencySample(sampleMilliseconds);
            }
            return true;
        }

        private void MarkPeerPacketReceived()
        {
            Interlocked.Exchange(ref mLastPeerPacketUtcTicks, DateTime.UtcNow.Ticks);
            if (mWatchdogWarningShown) CoopStatusOverlay.SetConnectionWarning(false);
            mWatchdogWarningShown = false;
        }

        /// <summary>Herhangi bir paketi güvenle gönder (partner preview vb. için).</summary>
        public void SendPacket(byte[] data)
        {
            Send(data);
        }

        public bool TrySendPacket(byte[] data)
        {
            return Send(data);
        }

        public bool TryGetNetworkTiming(out int pingMilliseconds, out int jitterMilliseconds)
        {
            pingMilliseconds = Volatile.Read(ref mPingMilliseconds);
            jitterMilliseconds = Volatile.Read(ref mJitterMilliseconds);
            return mHandshakeDone && pingMilliseconds > 0;
        }

        public bool IsPeerConnected()
        {
            return mHandshakeDone && (Role != "host" || mGuestConnected);
        }

        private void ResetNetworkTiming()
        {
            lock (mLatencyLock) mLatencyEstimator.Reset();
            Interlocked.Exchange(ref mHeartbeatSequence, 0);
            Interlocked.Exchange(ref mLastHeartbeatSequenceSent, 0);
            Volatile.Write(ref mPingMilliseconds, 0);
            Volatile.Write(ref mJitterMilliseconds, 0);
            mLatencyWarningLevel = 0;
            mTickGateFrozen = false;
            mTickGateFrozenSinceUtcTicks = 0;
            mTickGatePeerSampleReady = false;
            CoopNetworkTiming.Reset();
        }

        private void RecordLatencySample(int sampleMilliseconds)
        {
            int ping;
            int jitter;
            int budget;
            lock (mLatencyLock)
            {
                if (!mLatencyEstimator.RecordSample(sampleMilliseconds)) return;
                ping = mLatencyEstimator.SmoothedRttMilliseconds;
                jitter = mLatencyEstimator.JitterMilliseconds;
                budget = mLatencyEstimator.BudgetMilliseconds;
            }
            Volatile.Write(ref mPingMilliseconds, ping);
            Volatile.Write(ref mJitterMilliseconds, jitter);
            CoopNetworkTiming.PublishBudget(budget);

            int warningLevel = CoopNetworkQuality.IsCriticalLatency(ping)
                ? 2
                : (CoopNetworkQuality.IsHighLatency(ping) ? 1 : 0);
            if (warningLevel > mLatencyWarningLevel)
            {
                CoopStatusOverlay.Notify(warningLevel == 2
                    ? "DİKKAT: PİNG ÇOK YÜKSEK (" + ping + " MS) — OYUN YAVAŞLAYABİLİR"
                    : "YÜKSEK PİNG: " + ping + " MS");
                CoopPlugin.Log.LogWarning("[Coop] ağ gecikmesi yükseldi: ping=" + ping +
                    "ms jitter=" + jitter + "ms budget=" + budget + "ms");
            }
            else if (warningLevel == 0 && mLatencyWarningLevel > 0)
            {
                CoopStatusOverlay.Notify("AĞ GECİKMESİ NORMALE DÖNDÜ");
                CoopPlugin.Log.LogInfo("[Coop] ağ gecikmesi toparlandı: ping=" + ping +
                    "ms jitter=" + jitter + "ms");
            }
            mLatencyWarningLevel = warningLevel;
        }

        private bool Send(byte[] data)
        {
            try
            {
                if (data == null || !CoopProtocol.IsValidFrameLength(data.Length)) return false;
                if (UsingSteamTransport)
                {
                    if (!CoopSteamService.QueuePacket(data)) return false;
                    RegisterCommittedMutation(data);
                    return true;
                }
                lock (mSendLock)
                {
                    if (mStream == null) return false;
                    byte[] len = BitConverter.GetBytes(data.Length);
                    mStream.Write(len, 0, 4);
                    mStream.Write(data, 0, data.Length);
                    mStream.Flush();
                    RegisterCommittedMutation(data);
                }
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] packet send failed: " + ex.Message);
                return false;
            }
        }

        private void RegisterCommittedMutation(byte[] data)
        {
            if (Role != "host") return;
            long sessionId;
            long commandId;
            CoopMutationKind mutationKind;
            if (CoopProtocol.TryDescribeCommittedMutation(
                    data, out sessionId, out commandId, out mutationKind))
                mOutstandingMutations.Register(sessionId, commandId, mutationKind);
        }

        // ==================== DÜNYA TRANSFERİ ====================

        public static string DumpWorld(string path)
        {
            string tmp = path + ".tmp";
            XmlNode xmlNode = Serialization.beginSave(tmp, "save-game");
            Singleton<IdGenerator>.getInstance().serialize(xmlNode, "id-generator");
            Singleton<PlanetManager>.getInstance().serialize(xmlNode, "planet");
            Singleton<MilestoneManager>.getInstance().serialize(xmlNode, "milestones");
            Singleton<TechManager>.getInstance().serialize(xmlNode, "techs");
            Singleton<EnvironmentManager>.getInstance().serialize(xmlNode, "environment");
            CoopEnvironmentWindState windState;
            if (TryReadEnvironmentWindState(out windState) && xmlNode is XmlElement rootElement)
                CoopEnvironmentState.Write(rootElement, windState);
            Singleton<TerrainGenerator>.getInstance().serialize(xmlNode, "terrain");
            NormalizeTerrainSeed(xmlNode["terrain"]);
            CameraManager.getInstance().serialize(xmlNode, "camera");
            Singleton<DisasterManager>.getInstance().serialize(xmlNode);
            Singleton<Colony>.getInstance().serialize(xmlNode, "colony");
            Singleton<LandingShipManager>.getInstance().serialize(xmlNode, "ship-manager");
            CoopShipManagerClock.Write(xmlNode, ReadShipManagerTickTime());
            Singleton<StatsCollector>.getInstance().serialize(xmlNode, "stats");
            Singleton<VisitorEventManager>.getInstance().serialize(xmlNode, "visitor-events");
            Singleton<GameHintManager>.getInstance().serialize(xmlNode, "game-hints");
            Singleton<MeteorManager>.getInstance().serialize(xmlNode, "meteor-manager");
            Singleton<ThunderstormManager>.getInstance().serialize(xmlNode, "thunderstorm-manager");
            Singleton<ManufactureLimits>.getInstance().serialize(xmlNode, "manufacture-limits");
            Singleton<ChallengeManager>.getInstance().serialize(xmlNode, "challenge-manager");
            Construction.serializeAll(xmlNode, "constructions");
            Character.serializeAll(xmlNode, "characters");
            Resource.serializeAll(xmlNode, "resources");
            Ship.serializeAll(xmlNode, "ships");
            Interaction.serializeAll(xmlNode, "interactions");
            Serialization.endSave();
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        private static void NormalizeTerrainSeed(XmlNode terrainNode)
        {
            if (terrainNode == null) return;
            XmlNode seedNode = terrainNode["seed"];
            XmlAttribute valueAttribute = seedNode == null ? null : seedNode.Attributes?["value"];
            if (valueAttribute == null) return;

            int seed;
            TerrainGenerator terrain = Singleton<TerrainGenerator>.getInstance();
            if (sTerrainSeedField != null && terrain != null)
            {
                object rawSeed = sTerrainSeedField.GetValue(terrain);
                if (rawSeed is int)
                {
                    seed = (int)rawSeed;
                    valueAttribute.Value = seed.ToString(CultureInfo.InvariantCulture);
                    return;
                }
            }

            if (int.TryParse(valueAttribute.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out seed))
            {
                valueAttribute.Value = seed.ToString(CultureInfo.InvariantCulture);
                return;
            }

            float legacySeed;
            if (float.TryParse(valueAttribute.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out legacySeed) &&
                !float.IsNaN(legacySeed) && !float.IsInfinity(legacySeed))
            {
                seed = Convert.ToInt32(Math.Round(legacySeed, MidpointRounding.AwayFromZero));
                valueAttribute.Value = seed.ToString(CultureInfo.InvariantCulture);
                CoopPlugin.Log.LogWarning("[Coop] terrain seed legacy float biciminden duzeltildi: " + seed);
            }
        }

        private static float ReadShipManagerTickTime()
        {
            LandingShipManager manager = Singleton<LandingShipManager>.getInstance();
            if (manager == null || sLandingShipTickTimeField == null)
                throw new InvalidDataException("LandingShipManager clock is unavailable");
            float value = (float)sLandingShipTickTimeField.GetValue(manager);
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f ||
                value > CoopShipManagerClock.MaximumTickTime)
                throw new InvalidDataException("LandingShipManager clock is invalid");
            return value;
        }

        private static bool TryReadEnvironmentWindState(out CoopEnvironmentWindState state)
        {
            state = default(CoopEnvironmentWindState);
            EnvironmentManager environment = Singleton<EnvironmentManager>.getInstance();
            if (environment == null || sTimeToWindChangeField == null ||
                sWindOffsetField == null || sWindDirectionField == null)
                return false;
            try
            {
                state.TimeToWindChange = Convert.ToSingle(
                    sTimeToWindChangeField.GetValue(environment), CultureInfo.InvariantCulture);
                state.WindOffset = Convert.ToSingle(
                    sWindOffsetField.GetValue(environment), CultureInfo.InvariantCulture);
                object rawDirection = sWindDirectionField.GetValue(environment);
                if (!(rawDirection is Vector3)) return false;
                Vector3 direction = (Vector3)rawDirection;
                state.WindDirectionX = direction.x;
                state.WindDirectionY = direction.y;
                state.WindDirectionZ = direction.z;
                return state.IsValid();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] environment ruzgar fazi okunamadi: " + ex.Message);
                return false;
            }
        }

        private void CaptureAuthoritativeEnvironment(byte[] xml)
        {
            mAuthoritativeEnvironment = null;
            try
            {
                XmlDocument document = CoopDiagnostics.LoadXmlDocument(xml);
                CoopEnvironmentWindState state;
                if (CoopEnvironmentState.TryRead(document.DocumentElement, out state))
                    mAuthoritativeEnvironment = state;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] authoritative environment durumu okunamadi: " + ex.Message);
            }
        }

        private bool ApplyAuthoritativeEnvironment()
        {
            if (!mAuthoritativeEnvironment.HasValue) return true;
            try
            {
                EnvironmentManager environment = Singleton<EnvironmentManager>.getInstance();
                if (environment == null || sTimeToWindChangeField == null ||
                    sWindOffsetField == null || sWindDirectionField == null)
                    throw new InvalidDataException("authoritative environment state is unavailable");

                CoopEnvironmentWindState state = mAuthoritativeEnvironment.Value;
                sTimeToWindChangeField.SetValue(environment, state.TimeToWindChange);
                sWindOffsetField.SetValue(environment, state.WindOffset);
                sWindDirectionField.SetValue(environment, new Vector3(
                    state.WindDirectionX, state.WindDirectionY, state.WindDirectionZ));

                CoopEnvironmentWindState verify;
                if (!TryReadEnvironmentWindState(out verify) ||
                    verify.TimeToWindChange != state.TimeToWindChange ||
                    verify.WindOffset != state.WindOffset ||
                    verify.WindDirectionX != state.WindDirectionX ||
                    verify.WindDirectionY != state.WindDirectionY ||
                    verify.WindDirectionZ != state.WindDirectionZ)
                {
                    throw new InvalidOperationException(
                        "authoritative environment wind state did not persist");
                }
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError(
                    "[Coop] authoritative environment durumu uygulanamadi: " + ex.Message);
                return false;
            }
        }

        public static string HashWorld(byte[] xmlBytes)
        {
            try
            {
                return CoopWorldHash.ComputeRuntime(xmlBytes);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] hash hatası: " + ex.Message);
                return "ERR";
            }
        }

        private bool TryAdmitSnapshot(
            byte[] bytes,
            string context,
            bool notifyPeer,
            bool allowHostTransientDeferral,
            out string worldHash)
        {
            worldHash = "";
            CoopSnapshotValidationResult validation = CoopSnapshotInvariant.Validate(bytes);
            if (!validation.IsValid)
            {
                if (allowHostTransientDeferral &&
                    CoopSnapshotAdmissionPolicy.IsDeferrableHostTradingState(
                        Role == "host", validation.Code))
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (mSnapshotDeferStartedUtcTicks == 0) mSnapshotDeferStartedUtcTicks = now;
                    if (now - mSnapshotDeferStartedUtcTicks <
                        TimeSpan.TicksPerSecond * StartTimeoutSeconds)
                    {
                        mSnapshotAdmissionDeferred = true;
                        mSnapshotRetryAfterUtcTicks = now + TimeSpan.TicksPerSecond;
                        CoopStatusOverlay.Notify("TICARET PENCERESINI KAPATIN");
                        CoopPlugin.Log.LogWarning("[Coop] " + context +
                            " host ticaret penceresi kapanana kadar ertelendi");
                        return false;
                    }
                }
                string reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "snapshot invariant failed ({0}, path={1}, ship={2})",
                    validation.Code,
                    validation.Path,
                    validation.EntityId);
                CoopPlugin.Log.LogError("[Coop] " + context + " reddedildi: " +
                    validation.Message + " path=" + validation.Path);
                CoopStatusOverlay.Notify("SNAPSHOT GECERSIZ");
                mRecoveryPending = false;
                FailStartup(reason, notifyPeer);
                return false;
            }

            mSnapshotDeferStartedUtcTicks = 0;
            mSnapshotRetryAfterUtcTicks = 0;

            worldHash = HashWorld(bytes);
            if (StringComparer.Ordinal.Equals(worldHash, "ERR"))
            {
                mRecoveryPending = false;
                FailStartup("snapshot hash could not be computed", notifyPeer);
                return false;
            }

            try
            {
                string canonical = CoopDiagnostics.ComputeCanonicalXmlDigest(bytes);
                CoopPlugin.Log.LogInfo("[Coop] " + context +
                    " dogrulandi: world=" + worldHash + " canonical=" + canonical);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] snapshot diagnostic digest hatasi: " + ex.Message);
            }
            return true;
        }

        private bool TryCreateAdmittedSnapshot(
            string context,
            out byte[] bytes,
            out string worldHash)
        {
            string path = GetDumpPath();
            string candidatePath = path + ".candidate";
            mSnapshotAdmissionDeferred = false;
            DumpWorld(candidatePath);
            bytes = File.ReadAllBytes(candidatePath);
            if (!TryAdmitSnapshot(bytes, context, true, true, out worldHash))
            {
                try { File.Delete(candidatePath); } catch (Exception) { }
                return false;
            }

            try
            {
                PublishSnapshotCandidate(candidatePath, path);
            }
            catch
            {
                try { File.Delete(candidatePath); } catch (Exception) { }
                throw;
            }
            return true;
        }

        private static void PublishSnapshotCandidate(string candidatePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                // The candidate has already passed all admission checks. Keep
                // the previous valid snapshot intact if publication fails.
                File.Replace(candidatePath, destinationPath, null);
                return;
            }

            File.Move(candidatePath, destinationPath);
        }

        private static void WriteAndPublishSnapshot(byte[] bytes, string destinationPath)
        {
            string candidatePath = destinationPath + ".candidate";
            try
            {
                File.WriteAllBytes(candidatePath, bytes);
                PublishSnapshotCandidate(candidatePath, destinationPath);
            }
            catch
            {
                try { File.Delete(candidatePath); } catch (Exception) { }
                throw;
            }
        }

        private bool TryValidateRuntimeSnapshot(byte[] bytes, string context, out string worldHash)
        {
            worldHash = "";
            CoopSnapshotValidationResult validation = CoopSnapshotInvariant.Validate(bytes);
            if (!validation.IsValid)
            {
                if (CoopSnapshotAdmissionPolicy.IsDeferrableRuntimeTradingState(
                        validation.Code))
                {
                    CoopStatusOverlay.Notify("TICARET BITINCE SENKRON KONTROL EDILECEK");
                    CoopPlugin.Log.LogWarning("[Coop] " + context +
                        " gecici ticaret durumu nedeniyle ertelendi");
                    return false;
                }
                string reason = context + " invariant failed: " + validation.Code +
                    " path=" + validation.Path + " entity=" + validation.EntityId;
                if (ReportRuntimeInvariantFailure(reason))
                    CoopPlugin.Log.LogError("[Coop] " + reason);
                return false;
            }


            worldHash = HashWorld(bytes);
            if (!StringComparer.Ordinal.Equals(worldHash, "ERR")) return true;
            string hashReason = context + " hash could not be computed";
            if (ReportRuntimeInvariantFailure(hashReason))
                CoopPlugin.Log.LogError("[Coop] " + hashReason);
            return false;
        }

        private bool TryCreateValidatedRuntimeSnapshot(
            string context,
            out byte[] bytes,
            out string worldHash)
        {
            string path = GetDumpPath();
            string candidatePath = path + ".runtime-candidate";
            DumpWorld(candidatePath);
            bytes = File.ReadAllBytes(candidatePath);
            if (!TryValidateRuntimeSnapshot(bytes, context, out worldHash))
            {
                try { File.Delete(candidatePath); } catch (Exception) { }
                return false;
            }

            try
            {
                PublishSnapshotCandidate(candidatePath, path);
            }
            catch
            {
                try { File.Delete(candidatePath); } catch (Exception) { }
                throw;
            }
            return true;
        }

        private string GetDumpPath()
        {
            string dir = Util.getFilesFolder() + "/coop";
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
            return dir + "/coop_world.sav";
        }

        public void TrySendInitialSave()
        {
            if (Role != "host" || mInitialSaveSent || !mHandshakeDone || !mHostWorldStable) return;
            if (DateTime.UtcNow.Ticks < mSnapshotRetryAfterUtcTicks) return;
            try
            {
                byte[] bytes;
                string hash;
                if (!TryCreateAdmittedSnapshot("initial host snapshot", out bytes, out hash)) return;
                bool paused;
                int scaleIndex;
                if (!CoopTimeSync.TryRead(out paused, out scaleIndex))
                {
                    FailStartup("initial time state could not be captured", true);
                    return;
                }
                mAuthoritativePaused = paused;
                mAuthoritativeScaleIndex = scaleIndex;
                PlacementSync.Instance?.ResetForSession();
                ResetSessionDiagnostics(-1);
                long sessionId = CreateSessionId();
                mHostTickAtWorld = TickIndex;
                lock (mStartupLock)
                {
                    mStartup = new CoopStartupCoordinator(CoopRole.Host, sessionId, hash, TickIndex);
                    mStartup.MarkLocalWorldReady();
                }
                if (!CoopLandingPermissionsSync.SeedCommittedFromCurrent(sessionId, TickIndex))
                {
                    FailStartup("initial landing permissions could not be captured", true);
                    return;
                }
                int frameIndex = ReadFrameIndex();
                mAuthoritativeFrameIndex = frameIndex;
                byte[] payload = new byte[1 + 8 + 8 + 4 + bytes.Length];
                payload[0] = PktWorld;
                Buffer.BlockCopy(BitConverter.GetBytes(sessionId), 0, payload, 1, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(TickIndex), 0, payload, 9, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(frameIndex), 0, payload, 17, 4);
                Buffer.BlockCopy(bytes, 0, payload, 21, bytes.Length);
                if (!Send(payload))
                {
                    FailStartup("initial snapshot transmission failed", false);
                    return;
                }
                mInitialSaveSent = true;
                mHostWaitingSinceUtc = DateTime.UtcNow;
                LastHostHash = hash;
                mStartPacketSent = false;
                CoopPlugin.Log.LogInfo(string.Format(
                    "[Coop] sabit snapshot gönderildi ({0} KB, hash={1}, tick={2}, frame={3}, session={4})",
                    bytes.Length / 1024, LastHostHash, TickIndex, frameIndex, sessionId));
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] dünya gönderilemedi: " + ex.Message);
                FailStartup("initial snapshot creation failed: " + ex.Message, true);
            }
        }

        private void BeginWorldRecovery()
        {
            GameManager manager = GameManager.getInstance();
            bool activeGameState = manager != null && manager.getGameState() is GameStateGame;
            bool hostTerminalCondition = activeGameState && IsHostTerminalCondition();
            long activeSessionId;
            bool sessionRunning = TryGetRunningSessionId(out activeSessionId);
            CoopGameOverRequestDecision decision = CoopGameOverPolicy.DecideHostRequest(
                sessionMatches: true,
                sessionRunning: sessionRunning,
                worldLoaded: GameLoaded && mStartApplied && mHandshakeDone && mGuestConnected,
                activeGameState: activeGameState,
                recoveryPending: mRecoveryPending,
                sessionTerminal: CoopGameOverPolicy.IsTerminalSession(
                    Interlocked.Read(ref mTerminalSessionId), activeSessionId),
                hostTerminalCondition: hostTerminalCondition);
            if (decision == CoopGameOverRequestDecision.PublishTerminal)
            {
                BroadcastGameOver();
                if (!CoopGameOverBridge.InvokeAuthorized())
                    FailStartup("host game-over state could not be applied", true);
                return;
            }
            if (Role != "host" || decision != CoopGameOverRequestDecision.RecoverGuest)
                return;
            if (DateTime.UtcNow.Ticks < mSnapshotRetryAfterUtcTicks)
            {
                mRecoveryRequested = true;
                return;
            }
            try
            {
                mRecoveryPending = true;
                // Keep the already-running host simulation at the captured
                // tick while the guest reloads the authoritative snapshot.
                // The host remains in Updating; only the guest performs the
                // Waiting -> Updating start transition after it is ready.
                byte[] bytes;
                string hash;
                if (!TryCreateAdmittedSnapshot("recovery host snapshot", out bytes, out hash))
                {
                    if (mSnapshotAdmissionDeferred)
                    {
                        mRecoveryPending = false;
                        mRecoveryRequested = true;
                    }
                    return;
                }
                bool paused;
                int scaleIndex;
                if (!CoopTimeSync.TryRead(out paused, out scaleIndex))
                {
                    FailStartup("recovery time state could not be captured", true);
                    return;
                }
                mAuthoritativePaused = paused;
                mAuthoritativeScaleIndex = scaleIndex;
                // Do not reset deterministic/session state until the recovery
                // snapshot has actually passed admission. A legitimate open
                // trade window can defer capture; resetting on every retry
                // would advance the host from a different RNG/queue baseline
                // while the guest still has no replacement snapshot.
                PlacementSync.Instance?.ResetForSession();
                ResetSessionDiagnostics(TickIndex);
                ResetDeterministicRecoveryState(TickIndex);
                long sessionId = CreateSessionId();
                mRecoverySessionId = sessionId;
                mHostTickAtWorld = TickIndex;
                int frameIndex = ReadFrameIndex();
                mAuthoritativeFrameIndex = frameIndex;
                lock (mStartupLock)
                {
                    // Dual reload: host does NOT mark itself ready here. It
                    // must stage and verify the same coop_recv.sav checkpoint
                    // the guest loads before START can be authorized.
                    mStartup = new CoopStartupCoordinator(CoopRole.Host, sessionId, hash, TickIndex);
                }
                if (!CoopLandingPermissionsSync.SeedCommittedFromCurrent(sessionId, TickIndex))
                {
                    FailStartup("recovery landing permissions could not be captured", true);
                    return;
                }
                byte[] payload = new byte[1 + 8 + 8 + 4 + bytes.Length];
                payload[0] = PktWorld;
                Buffer.BlockCopy(BitConverter.GetBytes(sessionId), 0, payload, 1, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(TickIndex), 0, payload, 9, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(frameIndex), 0, payload, 17, 4);
                Buffer.BlockCopy(bytes, 0, payload, 21, bytes.Length);
                if (!Send(payload))
                {
                    FailStartup("recovery snapshot transmission failed", false);
                    return;
                }
                mStartPacketSent = false;
                LastHostHash = hash;
                ScheduleNextChecksumBoundary(TickIndex);
                // Dual reload: stage the identical checkpoint locally so the
                // host rebuilds the same loader-normalized world as the guest.
                try
                {
                    string dir = SaveData.FolderName;
                    Directory.CreateDirectory(dir);
                    WriteAndPublishSnapshot(bytes, dir + "/coop_recv.sav");
                    CaptureAuthoritativeIds(bytes);
                    CaptureAuthoritativeDisasters(bytes);
                    CaptureAuthoritativeShipManagerClock(bytes);
                    CaptureAuthoritativeEnvironment(bytes);
                    mGuestLandingReplay = false;
                    mLoadQueued = true;
                    mLoadTriggered = false;
                    mHostRecoveryReloadPending = true;
                    CoopPlugin.Log.LogInfo("[Coop] host recovery checkpoint yerel yukleme icin kuyruya alindi: tick=" + TickIndex);
                }
                catch (Exception ex)
                {
                    mRecoveryPending = false;
                    CoopPlugin.Log.LogError("[Coop] host recovery checkpoint yazilamadi: " + ex.Message);
                    FailStartup("host recovery local reload failed: " + ex.Message, true);
                    return;
                }
                CoopPlugin.Log.LogWarning("[Coop] OOS recovery snapshot gonderildi: tick=" + TickIndex + " session=" + sessionId);
                CoopStatusOverlay.Notify("SENKRON YENILENIYOR");
            }
            catch (Exception ex)
            {
                mRecoveryPending = false;
                CoopPlugin.Log.LogError("[Coop] OOS recovery snapshot gonderilemedi: " + ex.Message);
                FailStartup("recovery snapshot creation failed: " + ex.Message, true);
            }
        }

        private void OnWorldReceived(byte[] packet, int offset)
        {
            if (Role != "guest") return;
            if (packet == null || packet.Length < offset + 20)
            {
                FailStartup("invalid world packet length", true);
                return;
            }
            try
            {
                long sessionId = BitConverter.ToInt64(packet, offset);
                long hostTick = BitConverter.ToInt64(packet, offset + 8);
                int hostFrameIndex = BitConverter.ToInt32(packet, offset + 16);
                bool firstSnapshot = !mInitialSaveReceived;
                long activeSessionId;
                bool sessionRunning = TryGetRunningSessionId(out activeSessionId);
                bool activeGameState = GameManager.getInstance().getGameState() is GameStateGame;
                bool sessionTerminal = CoopGameOverPolicy.IsTerminalSession(
                    Interlocked.Read(ref mTerminalSessionId), activeSessionId);
                if (!CoopRecoveryPolicy.CanAcceptIncomingWorld(
                    firstSnapshot,
                    sessionRunning,
                    GameLoaded,
                    mStartApplied,
                    activeGameState,
                    sessionTerminal,
                    activeSessionId,
                    sessionId))
                {
                    FailStartup("unexpected world session transition", true);
                    return;
                }
                if (!CoopProtocol.IsValidWorldTick(
                        hostTick, Interlocked.Read(ref TickIndex), mInitialSaveReceived))
                {
                    FailStartup("invalid world tick", true);
                    return;
                }
                if (hostFrameIndex < 0 || hostFrameIndex > 63)
                {
                    FailStartup("invalid world frame index", true);
                    return;
                }
                int dataStart = offset + 20;
                byte[] xml = new byte[packet.Length - dataStart];
                Buffer.BlockCopy(packet, dataStart, xml, 0, xml.Length);
                string admittedHash;
                if (!TryAdmitSnapshot(
                        xml, "incoming guest snapshot", true, false, out admittedHash)) return;
                string dir = SaveData.FolderName;
                Directory.CreateDirectory(dir);
                string path = dir + "/coop_recv.sav";
                WriteAndPublishSnapshot(xml, path);
                mGuestLandingReplay = firstSnapshot && IsLandingReplaySnapshot(xml);
                if (!mGuestLandingReplay)
                    ClearGuestCinematic("snapshot alindi");
                ResetSessionDiagnostics(hostTick);
                ResetDeterministicRecoveryState(hostTick);
                LastGuestHash = admittedHash;
                CaptureAuthoritativeIds(xml);
                CaptureAuthoritativeDisasters(xml);
                CaptureAuthoritativeShipManagerClock(xml);
                CaptureAuthoritativeEnvironment(xml);
                mAuthoritativeFrameIndex = hostFrameIndex;
                lock (mStartupLock)
                {
                    mStartup = new CoopStartupCoordinator(CoopRole.Guest, sessionId, LastGuestHash, hostTick);
                }
                mStartPacketSent = false;
                mHostTickAtWorld = hostTick;
                mInitialSaveReceived = true;
                // Invalidate the previous running session immediately. The
                // next fixed update can otherwise serialize a half-loaded
                // guest world (notably an IdGenerator with next-id=0).
                GameLoaded = false;
                mStartApplied = false;
                mLoadQueued = true;
                mLoadTriggered = false;
                PlacementSync.Instance?.RequestSessionReset();
                CoopPlugin.Log.LogInfo(string.Format(
                    "[Coop] snapshot kuyruğa alındı ({0} KB, hash={1}, tick={2}, frame={3}, session={4})",
                    xml.Length / 1024, LastGuestHash, hostTick, hostFrameIndex, sessionId));

                lock (mPendingWorldLock)
                {
                    mPendingWorldPacket = null;
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] dünya kaydedilemedi: " + ex.Message);
                FailStartup("incoming snapshot processing failed: " + ex.Message, true);
            }
        }

        private void CaptureAuthoritativeIds(byte[] xml)
        {
            try
            {
                XmlDocument document = CoopDiagnostics.LoadXmlDocument(xml);
                XmlNode idNode = document.DocumentElement?["id-generator"];
                int nextId;
                int nextBotId;
                if (int.TryParse(idNode?["next-id"]?.Attributes?["value"]?.Value, out nextId))
                    mAuthoritativeNextId = nextId;
                if (int.TryParse(idNode?["next-bot-id"]?.Attributes?["value"]?.Value, out nextBotId))
                    mAuthoritativeNextBotId = nextBotId;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] authoritative ID okunamadı: " + ex.Message);
            }
        }

        private void CaptureAuthoritativeDisasters(byte[] xml)
        {
            mAuthoritativeSandstorm = null;
            mAuthoritativeBlizzard = null;
            mAuthoritativeSolarFlare = null;
            try
            {
                XmlDocument document = CoopDiagnostics.LoadXmlDocument(xml);
                CoopDisasterState state;
                if (CoopDisasterSnapshot.TryRead(document.DocumentElement, "sandstorm", out state))
                    mAuthoritativeSandstorm = state;
                if (CoopDisasterSnapshot.TryRead(document.DocumentElement, "blizzard", out state))
                    mAuthoritativeBlizzard = state;
                if (CoopDisasterSnapshot.TryRead(document.DocumentElement, "solar-flare", out state))
                    mAuthoritativeSolarFlare = state;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] authoritative disaster durumu okunamadi: " + ex.Message);
            }
        }

        private void CaptureAuthoritativeShipManagerClock(byte[] xml)
        {
            mAuthoritativeShipManagerTickTime = float.NaN;
            mAuthoritativeLandingPermissions = null;
            try
            {
                XmlDocument document = CoopDiagnostics.LoadXmlDocument(xml);
                float value;
                if (CoopShipManagerClock.TryRead(document.DocumentElement, out value))
                    mAuthoritativeShipManagerTickTime = value;
                CoopLandingPermissionsData permissions;
                if (CoopShipManagerClock.TryReadLandingPermissions(
                        document.DocumentElement, out permissions))
                    mAuthoritativeLandingPermissions = permissions;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] authoritative ship-manager saati okunamadi: " + ex.Message);
            }
        }

        private bool ApplyAuthoritativeShipManagerClock()
        {
            if (float.IsNaN(mAuthoritativeShipManagerTickTime) ||
                float.IsInfinity(mAuthoritativeShipManagerTickTime) ||
                mAuthoritativeLandingPermissions == null)
                return Role != "guest";
            try
            {
                LandingShipManager manager = Singleton<LandingShipManager>.getInstance();
                if (manager == null || sLandingShipTickTimeField == null ||
                    float.IsNaN(mAuthoritativeShipManagerTickTime) ||
                    float.IsInfinity(mAuthoritativeShipManagerTickTime))
                    throw new InvalidDataException(
                        "authoritative ship-manager clock is incomplete");

                sLandingShipTickTimeField.SetValue(
                    manager, mAuthoritativeShipManagerTickTime);
                float actual = (float)sLandingShipTickTimeField.GetValue(manager);
                if (actual != mAuthoritativeShipManagerTickTime)
                    throw new InvalidOperationException(
                        "authoritative ship-manager clock did not persist");
                if (!CoopLandingPermissionsSync.ApplyAuthoritativeRecovery(
                        mAuthoritativeLandingPermissions))
                    throw new InvalidOperationException(
                        "authoritative landing permissions did not persist");
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError(
                    "[Coop] authoritative ship-manager saati uygulanamadi: " + ex.Message);
                return false;
            }
        }

        private bool ApplyAuthoritativeDisasters()
        {
            if (!mAuthoritativeSandstorm.HasValue ||
                !mAuthoritativeBlizzard.HasValue || !mAuthoritativeSolarFlare.HasValue)
                return Role != "guest";
            try
            {
                DisasterManager manager = Singleton<DisasterManager>.getInstance();
                if (manager == null || !mAuthoritativeSandstorm.HasValue ||
                    !mAuthoritativeBlizzard.HasValue || !mAuthoritativeSolarFlare.HasValue)
                {
                    throw new InvalidDataException("authoritative disaster state is incomplete");
                }
                ApplyDisasterState(manager.getSandstorm(), "Sandstorm", mAuthoritativeSandstorm);
                ApplyDisasterState(manager.getBlizzard(), "Blizzard", mAuthoritativeBlizzard);
                ApplyDisasterState(manager.getSolarFlare(), "SolarFlare", mAuthoritativeSolarFlare);
                CoopPlugin.Log.LogInfo("[Coop] authoritative disaster sayaclari uygulandi");
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] authoritative disaster durumu uygulanamadi: " + ex.Message);
                return false;
            }
        }

        private static void ApplyDisasterState(object disaster, string suffix, CoopDisasterState? pending)
        {
            if (disaster == null || !pending.HasValue)
                throw new InvalidDataException("missing authoritative " + suffix + " state");
            Type type = disaster.GetType();
            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            CoopDisasterState state = pending.Value;
            System.Reflection.FieldInfo inProgress = RequireRuntimeField(type, "m" + suffix + "InProgress", flags);
            System.Reflection.FieldInfo timeToNext = RequireRuntimeField(type, "mTimeToNext" + suffix, flags);
            System.Reflection.FieldInfo time = RequireRuntimeField(type, "mTime", flags);
            System.Reflection.FieldInfo duration = RequireRuntimeField(type, "m" + suffix + "Time", flags);
            inProgress.SetValue(disaster, state.InProgress);
            timeToNext.SetValue(disaster, state.TimeToNext);
            time.SetValue(disaster, state.Time);
            duration.SetValue(disaster, state.Duration);

            if ((bool)inProgress.GetValue(disaster) != state.InProgress ||
                (float)timeToNext.GetValue(disaster) != state.TimeToNext ||
                (float)time.GetValue(disaster) != state.Time ||
                (float)duration.GetValue(disaster) != state.Duration)
            {
                throw new InvalidOperationException("authoritative " + suffix + " state did not persist");
            }
        }

        private static System.Reflection.FieldInfo RequireRuntimeField(
            Type type,
            string name,
            System.Reflection.BindingFlags flags)
        {
            System.Reflection.FieldInfo field = type.GetField(name, flags);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private bool ApplyAuthoritativeIds()
        {
            // Dual reload: host da ayni checkpoint'i yukledigi icin ayni
            // force-back dogrulamasindan gecer. Capture yoksa yalnizca host
            // bootstrap tolere edilir; guest icin eksiklik hatadir.
            if (mAuthoritativeNextId < 0 || mAuthoritativeNextBotId < 0)
                return Role != "guest";
            try
            {
                IdGenerator generator = Singleton<IdGenerator>.getInstance();
                if (generator == null || sNextIdField == null || sNextBotIdField == null ||
                    mAuthoritativeNextId < 0 || mAuthoritativeNextBotId < 0)
                {
                    throw new InvalidDataException("authoritative id-generator state is incomplete");
                }
                sNextIdField.SetValue(generator, mAuthoritativeNextId);
                sNextBotIdField.SetValue(generator, mAuthoritativeNextBotId);
                if ((int)sNextIdField.GetValue(generator) != mAuthoritativeNextId ||
                    (int)sNextBotIdField.GetValue(generator) != mAuthoritativeNextBotId)
                {
                    throw new InvalidOperationException("authoritative id-generator state did not persist");
                }
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] authoritative ID uygulanamadi: " + ex.Message);
                return false;
            }
        }

        private static int ReadFrameIndex()
        {
            try
            {
                GameManager gm = GameManager.getInstance();
                if (gm != null && sFrameIndexField != null)
                    return (int)sFrameIndexField.GetValue(gm);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] host frame index okunamadı: " + ex.Message);
            }
            return 0;
        }

        private bool ApplyAuthoritativeFrameIndex()
        {
            try
            {
                if (mAuthoritativeFrameIndex < 0 || sFrameIndexField == null)
                    throw new InvalidOperationException("authoritative frame state is unavailable");
                GameManager gm = GameManager.getInstance();
                if (gm == null)
                    throw new InvalidOperationException("GameManager is unavailable");
                sFrameIndexField.SetValue(gm, mAuthoritativeFrameIndex);
                object actualValue = sFrameIndexField.GetValue(gm);
                int actualFrameIndex = Convert.ToInt32(actualValue, CultureInfo.InvariantCulture);
                CoopLoadedWorldVerification.AssertFrameIndexRestored(
                    mAuthoritativeFrameIndex, actualFrameIndex);
                CoopPlugin.Log.LogInfo("[Coop] authoritative frame index uygulandı: " + mAuthoritativeFrameIndex);
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] authoritative frame index uygulanamadı: " + ex.Message);
                return false;
            }
        }

        private static void ResetDeterministicRecoveryState(long tick)
        {
            try
            {
                CoopDeterministicRuntime.ResetSessionState();
                UnityEngine.Random.InitState(SyncedSeed + (int)tick);
                RngTrace.Count = 0;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] recovery deterministik state reseti tamamlanamadi: " + ex.Message);
            }
        }

        // ==================== OOS DED EKTEORU ====================

        private void OnHashReceived(long peerTick, string hash)
        {
            if (Role == "host") LastGuestHash = hash; else LastHostHash = hash;
            CoopHashMatch result;
            lock (mHashLock) result = mHashMatcher.RecordPeer(peerTick, hash);
            LogHashResult(peerTick, result);
        }

        private void RecordLocalHash(long tick, string hash)
        {
            CoopHashMatch result;
            lock (mHashLock) result = mHashMatcher.RecordLocal(tick, hash);
            LogHashResult(tick, result);
        }

        private void LogHashResult(long tick, CoopHashMatch result)
        {
            if (result == CoopHashMatch.Pending) return;
            string verdict = result == CoopHashMatch.Match ? "SYNC OK ✓" : "OOS ⚠";
            string localHash = Role == "host" ? LastHostHash : LastGuestHash;
            string peerHash = Role == "host" ? LastGuestHash : LastHostHash;
            CoopPlugin.Log.LogInfo(string.Format(
                "[Coop] KONTROL tick {0}: {1} (local={2}, peer={3})",
                tick, verdict, localHash, peerHash));
            lock (mOosStateLock)
            {
                if (result == CoopHashMatch.Mismatch)
                {
                    OosCount++;
                    mConsecutiveOosCount++;
                    long now = DateTime.UtcNow.Ticks;
                    if (Role == "host" && GameLoaded && !mRecoveryPending &&
                        mConsecutiveOosCount >= RequiredConsecutiveOos &&
                        now - Interlocked.Read(ref mLastRecoveryUtcTicks) >=
                            TimeSpan.TicksPerSecond * RecoveryCooldownSeconds)
                    {
                        Interlocked.Exchange(ref mLastRecoveryUtcTicks, now);
                        mConsecutiveOosCount = 0;
                        mRecoveryRequested = true;
                    }
                }
                else if (result == CoopHashMatch.Match)
                {
                    mConsecutiveOosCount = 0;
                }
            }
            if (result == CoopHashMatch.Mismatch)
                CaptureOosDiagnostic(tick, localHash, peerHash);
        }

        // ==================== TICK KAPISI ====================

        public bool ShouldFreezeSim()
        {
            if (mRuntimeInvariantFailurePending || mInvalidWorldPacket || mConnectionLost) return true;
            int maxLeadTicks = CoopTickLeadPolicy.MaxLeadTicks(
                CoopTimeSync.CurrentScaleIndex,
                CoopNetworkTiming.CurrentBudgetMilliseconds);
            if (mRecoveryRequested || mRecoveryPending)
            {
                UpdateTickGateState(false, maxLeadTicks);
                return true;
            }
            bool networkFreeze = false;
            long peerTick = Interlocked.Read(ref PeerTick);
            if (Role == "host")
            {
                networkFreeze = mHandshakeDone && mGuestConnected && GameLoaded &&
                    mInitialSaveSent && CoopTickGatePolicy.ShouldFreeze(
                        TickIndex, peerTick, maxLeadTicks, mTickGateFrozen,
                        mTickGatePeerSampleReady);
                UpdateTickGateState(networkFreeze, maxLeadTicks);
                return (mHostWorldStable && !mStartApplied) ||
                    networkFreeze;
            }
            if (Role == "guest")
            {
                if (mSnapshotVerificationPending) return true;
                networkFreeze = mInitialSaveReceived &&
                    CoopTickGatePolicy.ShouldFreeze(
                        TickIndex, peerTick, maxLeadTicks, mTickGateFrozen,
                        mTickGatePeerSampleReady);
                UpdateTickGateState(networkFreeze, maxLeadTicks);
                return networkFreeze;
            }
            return false;
        }

        private void UpdateTickGateState(bool frozen, int maxLeadTicks)
        {
            if (frozen == mTickGateFrozen) return;
            mTickGateFrozen = frozen;
            long now = DateTime.UtcNow.Ticks;
            long delta = Math.Abs(TickIndex - Interlocked.Read(ref PeerTick));
            int ping = Volatile.Read(ref mPingMilliseconds);
            if (frozen)
            {
                mTickGateFrozenSinceUtcTicks = now;
                CoopPlugin.Log.LogInfo("[Coop] ağ tick kapısı bekliyor: rol=" + Role +
                    " fark=" + delta + " limit=" + maxLeadTicks + " ping=" + ping + "ms");
                return;
            }

            long durationTicks = now - mTickGateFrozenSinceUtcTicks;
            long durationMilliseconds = durationTicks <= 0
                ? 0
                : durationTicks / TimeSpan.TicksPerMillisecond;
            CoopPlugin.Log.LogInfo("[Coop] ağ tick kapısı toparlandı: rol=" + Role +
                " bekleme=" + durationMilliseconds + "ms fark=" + delta +
                " limit=" + maxLeadTicks + " ping=" + ping + "ms");
            if (durationMilliseconds >= 1000)
                CoopStatusOverlay.Notify("AĞ BEKLEMESİ BİTTİ — OYUN TOPARLANDI");
            mTickGateFrozenSinceUtcTicks = 0;
        }

        public bool IsWorldTransferPending()
        {
            bool hostInitialWorldLoading =
                Role == "host" && mHostWorldOriginKnown && !GameLoaded;
            return CoopWorldTransferPolicy.IsPending(
                mLoadQueued,
                mRecoveryPending,
                hostInitialWorldLoading,
                mSnapshotVerificationPending);
        }

        public bool IsSnapshotVerificationPending()
        {
            return Role == "guest" && mSnapshotVerificationPending;
        }

        public bool IsHostRecoveryPending()
        {
            return Role == "host" && mRecoveryPending;
        }

        /// <summary>
        /// Dual reload: host, recovery sirasinda kendi checkpoint'ini yukler.
        /// GameManager.fixedUpdate bu pencerede staged loader'ı ilerletir;
        /// GameStateGame eski dunyayi ilerletmez.
        /// </summary>
        public bool IsLocalSnapshotLoadPending()
        {
            return Role == "host" && mRecoveryPending && mLoadQueued;
        }

        public bool IsRecoveryInProgress()
        {
            return mRecoveryRequested || mRecoveryPending;
        }

        public bool IsSimulationActive()
        {
            return GameLoaded && mStartApplied && !IsWorldTransferPending();
        }

        public bool IsHostBootstrapActive()
        {
            return Role == "host" && mHostBootstrapStarted && !mHostWorldStable && !mStartApplied;
        }

        public bool IsHostWorldStable()
        {
            return Role == "host" && mHostWorldStable;
        }

        public bool ShouldFreezeInitialCinematic()
        {
            return Role == "host" && mHostFreshWorld && mHostWorldStable && !mStartApplied;
        }

        /// <summary>Her sim tick'inde çağrılır (GameManager.fixedUpdate postfix).</summary>
        private static long CurrentChecksumIntervalTicks()
        {
            return CoopCheckpointSchedule.IntervalTicksForScale(
                BaseChecksumIntervalTicks,
                CoopTimeSync.CurrentScaleIndex);
        }

        private void ScheduleNextChecksumBoundary(long currentTick)
        {
            long interval = CurrentChecksumIntervalTicks();
            mNextChecksumTick = CoopCheckpointSchedule.NextBoundary(
                currentTick,
                interval);
            mScheduledChecksumIntervalTicks = interval;
        }

        private void RebaseChecksumDeadlineForCurrentScale()
        {
            long interval = CurrentChecksumIntervalTicks();
            mNextChecksumTick = CoopCheckpointSchedule.RebaseDeadline(
                TickIndex,
                mNextChecksumTick,
                mScheduledChecksumIntervalTicks,
                interval);
            mScheduledChecksumIntervalTicks = interval;
        }

        public void OnSimTick(bool simulationAdvanced)
        {
            TickIndex = CoopTickProgressPolicy.NextTick(TickIndex, simulationAdvanced && mStartApplied);
            if (mHandshakeDone && !Send(CoopProtocol.CreateTickPacket(TickIndex)))
            {
                mConnectionLost = true;
                return;
            }

            if (!simulationAdvanced || !mStartApplied) return;

            // Keep full-world XML serialization at a stable real-time cadence.
            // FixedUpdate runs 1x/2x/4x faster with the selected game speed.
            RebaseChecksumDeadlineForCurrentScale();
            if (Role == "host" && TickIndex >= mNextChecksumTick && mHandshakeDone)
            {
                ScheduleNextChecksumBoundary(TickIndex);
                long checkpointStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    byte[] bytes;
                    string checkpointHash;
                    if (!TryCreateValidatedRuntimeSnapshot(
                            "host checkpoint", out bytes, out checkpointHash)) return;
                    StoreCheckpointDiagnostic(TickIndex, bytes);
                    LastHostHash = checkpointHash;
                    RecordLocalHash(TickIndex, LastHostHash);
                    if (!SendHashPacket(TickIndex, LastHostHash)) mConnectionLost = true;
                    RecordCheckpointPerformance(TickIndex, bytes.Length, checkpointStarted);
                }
                catch (Exception ex)
                {
                    string reason = "host checkpoint creation failed: " + ex.Message;
                    if (ReportRuntimeInvariantFailure(reason))
                        CoopPlugin.Log.LogError("[Coop] " + reason);
                }
            }
            else if (Role == "guest" && TickIndex >= mNextChecksumTick && mInitialSaveReceived)
            {
                ScheduleNextChecksumBoundary(TickIndex);
                long checkpointStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    byte[] bytes;
                    string checkpointHash;
                    if (!TryCreateValidatedRuntimeSnapshot(
                            "guest checkpoint", out bytes, out checkpointHash)) return;
                    StoreCheckpointDiagnostic(TickIndex, bytes);
                    LastGuestHash = checkpointHash;
                    RecordLocalHash(TickIndex, LastGuestHash);
                    if (!SendHashPacket(TickIndex, LastGuestHash)) mConnectionLost = true;
                    RecordCheckpointPerformance(TickIndex, bytes.Length, checkpointStarted);
                }
                catch (Exception ex)
                {
                    string reason = "guest checkpoint creation failed: " + ex.Message;
                    if (ReportRuntimeInvariantFailure(reason))
                        CoopPlugin.Log.LogError("[Coop] " + reason);
                }
            }
        }

        private void RecordCheckpointPerformance(
            long tick,
            int snapshotBytes,
            long startedTimestamp)
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp;
            double milliseconds = elapsed * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            mCheckpointDurationMilliseconds[mCheckpointDurationCursor] = milliseconds;
            mCheckpointDurationCursor =
                (mCheckpointDurationCursor + 1) % mCheckpointDurationMilliseconds.Length;
            if (mCheckpointDurationCount < mCheckpointDurationMilliseconds.Length)
                mCheckpointDurationCount++;
            mCheckpointMeasurementCount++;

            if (milliseconds >= 25.0)
            {
                CoopPlugin.Log.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "[Coop] checkpoint performans: rol={0} tick={1} sure={2:F1}ms boyut={3}KB",
                    Role, tick, milliseconds, snapshotBytes / 1024));
            }

            if (mCheckpointDurationCount < 8 || mCheckpointMeasurementCount % 8 != 0)
                return;
            double[] samples = new double[mCheckpointDurationCount];
            Array.Copy(mCheckpointDurationMilliseconds, samples, samples.Length);
            Array.Sort(samples);
            double p50 = samples[(samples.Length - 1) * 50 / 100];
            double p95 = samples[(samples.Length - 1) * 95 / 100];
            double p99 = samples[(samples.Length - 1) * 99 / 100];
            CoopPlugin.Log.LogInfo(string.Format(
                CultureInfo.InvariantCulture,
                "[Coop] checkpoint dagilimi: n={0} p50={1:F1}ms p95={2:F1}ms p99={3:F1}ms",
                samples.Length, p50, p95, p99));
        }

        public bool ReportRuntimeInvariantFailure(string reason)
        {
            lock (mRuntimeInvariantLock)
            {
                if (mRuntimeInvariantFailurePending) return false;
                mRuntimeInvariantFailure = reason ?? "runtime invariant failure";
                mRuntimeInvariantFailurePending = true;
                return true;
            }
        }

        private void StoreCheckpointDiagnostic(long tick, byte[] bytes)
        {
            CoopPlugin.Log.LogInfo("[Coop] sebeke tick=" + tick + " " + CoopGridDeterminism.Describe());
            try
            {
                string canonical = CoopDiagnostics.ComputeCanonicalXmlDigest(bytes);
                lock (mDiagnosticLock)
                {
                    mLastCanonicalDiagnosticDigest = canonical;
                    mLastDiagnosticTick = tick;
                    mLastDiagnosticSnapshot = (byte[])bytes.Clone();
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] checkpoint diagnostic digest hatasi: " + ex.Message);
            }
        }

        private void CaptureOosDiagnostic(long reportedTick, string localHash, string peerHash)
        {
            byte[] snapshot;
            string canonical;
            long capturedTick;
            lock (mDiagnosticLock)
            {
                if (mOosArtifactsCaptured >= MaxOosArtifactsPerSession) return;
                mOosArtifactsCaptured++;
                snapshot = mLastDiagnosticSnapshot == null
                    ? null
                    : (byte[])mLastDiagnosticSnapshot.Clone();
                canonical = mLastCanonicalDiagnosticDigest;
                capturedTick = mLastDiagnosticTick;
            }

            try
            {
                string directory = Path.GetDirectoryName(GetDumpPath());
                Directory.CreateDirectory(directory);
                string artifactPath = Path.Combine(
                    directory,
                    "oos_" + Role + "_" + capturedTick.ToString(CultureInfo.InvariantCulture) + ".sav");
                if (snapshot != null) File.WriteAllBytes(artifactPath, snapshot);

                string json = "{\"utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"role\":\"" + JsonEscape(Role) +
                    "\",\"reportedTick\":" + reportedTick.ToString(CultureInfo.InvariantCulture) +
                    ",\"capturedTick\":" + capturedTick.ToString(CultureInfo.InvariantCulture) +
                    ",\"localHash\":\"" + JsonEscape(localHash) +
                    "\",\"peerHash\":\"" + JsonEscape(peerHash) +
                    "\",\"canonicalDigest\":\"" + JsonEscape(canonical) +
                    "\",\"artifact\":\"" + JsonEscape(snapshot == null ? "" : artifactPath) + "\"}";
                File.AppendAllText(
                    Path.Combine(directory, "coop_diagnostics.jsonl"),
                    json + Environment.NewLine,
                    new UTF8Encoding(false));
                CoopPlugin.Log.LogWarning("[Coop] OOS diagnostic kaydedildi: " + artifactPath);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] OOS diagnostic kaydedilemedi: " + ex.Message);
            }
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private void ResetSessionDiagnostics(long peerTick)
        {
            CoopShipSync.ResetForSession();
            CoopLandingPermissionsSync.ResetForSession();
            CoopGameplayControlSync.ResetForSession();
            CoopDisasterSync.ResetForSession();
            CoopTradeSync.ResetForSession();
            CoopTimeSync.ResetForSession();
            lock (mHashLock) mHashMatcher.Clear();
            lock (mDiagnosticLock)
            {
                mOosArtifactsCaptured = 0;
                mLastCanonicalDiagnosticDigest = "";
                mLastDiagnosticTick = -1;
                mLastDiagnosticSnapshot = null;
            }
            LastHostHash = "";
            LastGuestHash = "";
            lock (mOosStateLock)
            {
                OosCount = 0;
                mRecoveryRequested = false;
                mConsecutiveOosCount = 0;
            }
            Interlocked.Exchange(ref PeerTick, peerTick);
            mTickGatePeerSampleReady = false;
            mTickGateFrozen = false;
            mTickGateFrozenSinceUtcTicks = 0;
            ScheduleNextChecksumBoundary(peerTick >= 0 ? peerTick : TickIndex);
            Interlocked.Exchange(ref mLastPeerPacketUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref mLastHeartbeatSentUtcTicks, 0);
            mWatchdogWarningShown = false;
            mGameOverBroadcastSent = false;
            Interlocked.Exchange(ref mPendingGameOverRequest, null);
            Interlocked.Exchange(ref mPendingGameOverState, null);
            mGameOverRequestSent = false;
            lock (mMutationFailureLock) mPendingMutationFailures.Clear();
            mMutationFailureLedger.Reset();
            mOutstandingMutations.Reset();
        }

        public void ReportMutationFailure(
            CoopMutationKind kind,
            long commandId,
            CoopMutationFailureCode code)
        {
            long sessionId;
            if (!FullMode || commandId <= 0 || !TryGetRunningSessionId(out sessionId)) return;
            CoopMutationFailureData failure = new CoopMutationFailureData(
                sessionId, commandId, kind, code);
            if (!mMutationFailureLedger.TryRecord(failure, sessionId)) return;

            CoopPlugin.Log.LogError("[Coop] authoritative mutation failed: kind=" + kind +
                " command=" + commandId + " code=" + code);
            CoopStatusOverlay.Notify("SENKRON MUTASYONU BASARISIZ - YENILENIYOR");
            if (Role == "guest")
            {
                mRecoveryPending = true;
                if (!Send(CoopProtocol.CreateMutationFailurePacket(failure)))
                    mConnectionLost = true;
                return;
            }
            RequestMutationRecovery();
        }

        private void ProcessPendingMutationFailures()
        {
            if (Role != "host") return;
            while (true)
            {
                CoopMutationFailureData failure = null;
                lock (mMutationFailureLock)
                {
                    if (mPendingMutationFailures.Count > 0)
                        failure = mPendingMutationFailures.Dequeue();
                }
                if (failure == null) return;
                long activeSessionId;
                if (!TryGetRunningSessionId(out activeSessionId) ||
                    !mOutstandingMutations.TryConsume(failure, activeSessionId) ||
                    !mMutationFailureLedger.TryRecord(failure, activeSessionId)) continue;
                CoopPlugin.Log.LogError("[Coop] guest mutation failure: kind=" + failure.Kind +
                    " command=" + failure.CommandId + " code=" + failure.Code);
                RequestMutationRecovery();
            }
        }

        private void RequestMutationRecovery()
        {
            if (Role != "host" || mRecoveryPending || mStartupFailed) return;
            Interlocked.Exchange(ref mLastRecoveryUtcTicks, DateTime.UtcNow.Ticks);
            mRecoveryRequested = true;
        }

        private bool SendHashPacket(long tick, string hash)
        {
            return Send(CoopProtocol.CreateHashPacket(tick, hash));
        }

        /// <summary>Her render karesinde çağrılır (GameBehaviour.Update postfix).</summary>
        public void OnFrame()
        {
            GameManager gm = GameManager.getInstance();
            if (gm == null) return;
            if (TryCompleteFailureTeardown(gm)) return;
            CoopTestHooks.OnFrame(this);

            if (UsingSteamTransport)
            {
                string steamFailure;
                if (CoopSteamService.TryConsumeFailure(out steamFailure))
                {
                    if (Role == "host" && mLobbyActive && !mLobbyStarted &&
                        LobbyPhase == CoopLobbyPhase.Waiting)
                    {
                        mHandshakeDone = false;
                        mGuestConnected = false;
                        mGuestLobbyReady = false;
                        CoopSteamService.ResetPeerForHost();
                        CoopStatusOverlay.Notify(
                            "STEAM MİSAFİRİ AYRILDI — LOBİ YENİ DAVETE AÇIK");
                        CoopPlugin.Log.LogWarning("[Coop] " + steamFailure +
                            " — host lobisi acik tutuldu");
                    }
                    else
                    {
                        CoopPlugin.Log.LogWarning("[Coop] " + steamFailure);
                        mConnectionLost = true;
                    }
                }
            }

            if (mRuntimeInvariantFailurePending)
            {
                string reason;
                lock (mRuntimeInvariantLock)
                {
                    reason = mRuntimeInvariantFailure;
                    mRuntimeInvariantFailure = "";
                    mRuntimeInvariantFailurePending = false;
                }
                CoopStatusOverlay.Notify("OTURUM DURUMU GECERSIZ");
                FailStartup(reason, true);
            }
            if (mStartupFailed) return;

            if (mInvalidWorldPacket)
            {
                mInvalidWorldPacket = false;
                FailStartup("invalid world packet length", true);
            }
            if (mStartupFailed) return;

            if (mHandshakeDone && (mStream != null || UsingSteamTransport))
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - Interlocked.Read(ref mLastHeartbeatSentUtcTicks) >
                    TimeSpan.TicksPerSecond / 2)
                {
                    Interlocked.Exchange(ref mLastHeartbeatSentUtcTicks, now);
                    long sequence = Interlocked.Increment(ref mHeartbeatSequence);
                    Interlocked.Exchange(ref mLastHeartbeatSequenceSent, sequence);
                    Send(CoopProtocol.CreateHeartbeatPacket(
                        false, sequence, now));
                }
                long lastPeer = Interlocked.Read(ref mLastPeerPacketUtcTicks);
                if (lastPeer > 0 && now - lastPeer > TimeSpan.TicksPerSecond * 5 && !mWatchdogWarningShown)
                {
                    mWatchdogWarningShown = true;
                    CoopStatusOverlay.SetConnectionWarning(true);
                    CoopPlugin.Log.LogWarning("[Coop] peer heartbeat timeout");
                }
            }

            ApplyPendingWorldPacket();
            if (!IsWorldTransferPending() && IsSessionRunning())
                PlacementSync.Instance?.OnFrame();
            ProcessPendingMutationFailures();

            if (Role == "host" && mRecoveryRequested && !mRecoveryPending &&
                DateTime.UtcNow.Ticks >= mSnapshotRetryAfterUtcTicks)
            {
                mRecoveryRequested = false;
                BeginWorldRecovery();
            }

            PendingGameOverEvent gameOverRequest = Interlocked.Exchange(
                ref mPendingGameOverRequest, null);
            if (gameOverRequest != null)
            {
                HandleGameOverRequest(
                    gameOverRequest.SessionId,
                    gameOverRequest.Tick);
            }

            PendingGameOverEvent gameOverState = Interlocked.Exchange(
                ref mPendingGameOverState, null);
            if (gameOverState != null)
            {
                long activeSession;
                if (TryGetRunningSessionId(out activeSession) &&
                    CoopCommandSessionPolicy.CanAccept(
                        true, activeSession, gameOverState.SessionId))
                {
                    mRecoveryRequested = false;
                    if (!CoopGameOverBridge.InvokeAuthorized())
                    {
                        FailStartup("authoritative game-over state could not be applied", true);
                        return;
                    }
                    Interlocked.Exchange(ref mTerminalSessionId, gameOverState.SessionId);
                    CoopStatusOverlay.Notify("OYUN BILGISI: KOLONI SONLANDI");
                    CoopPlugin.Log.LogInfo("[Coop] host game-over durumu uygulandi tick=" + gameOverState.Tick);
                }
            }

            if (mAbortRequested)
            {
                mAbortRequested = false;
                FailStartup("peer aborted startup", false);
            }
            if (mConnectionLost)
            {
                mConnectionLost = false;
                CoopStatusOverlay.SetConnectionWarning(true);
                FailStartup("peer connection lost", false);
            }
            if (mStartupFailed) return;

            if (Role == "host")
            {
                // Lobiden baslatilan oturumda dunya, host START verdikten sonra
                // kurulur; CLI akisi davranisini korur.
                if (!mAutoContinueAttempted && (!mLobbyActive || mLobbyStarted))
                {
                    mAutoContinueAttempted = true;
                    if (gm.getGameState() is GameStateGame) { }
                    else if (NewGameRequested)
                    {
                        int seed = ResolveNewGameSeed();
                        mHostFreshWorld = true;
                        mHostWorldOriginKnown = true;
                        CoopPlugin.Log.LogInfo("[Coop] host: sifirdan oyun (tohum=" + seed +
                            ", gezegen=" + mMenuPlanetIndex + ")");
                        gm.setGameStateGameNew(seed, mMenuPlanetIndex, false, null);
                    }
                    else if (mMenuLoadRequested)
                    {
                        mMenuLoadRequested = false;
                        string loadPath = mMenuLoadSavePath;
                        if (!File.Exists(loadPath))
                        {
                            FailStartup("lobi kaydi bulunamadi: " + loadPath, true);
                            return;
                        }
                        try
                        {
                            SaveData menuSave = new SaveData(loadPath, DateTime.Now);
                            int menuPlanet = menuSave.getPlanetIndex();
                            Challenge menuChallenge = menuSave.createChallenge();
                            mHostFreshWorld = false;
                            mHostWorldOriginKnown = true;
                            CoopPlugin.Log.LogInfo("[Coop] host: lobiden kayit yukle (" + loadPath + ")");
                            gm.setGameStateGameLoad(loadPath, menuPlanet, menuChallenge);
                        }
                        catch (Exception ex)
                        {
                            FailStartup("lobi kaydi yuklenemedi: " + ex.Message, true);
                            return;
                        }
                    }
                    else if (SaveData.anySaveGames())
                    {
                        mHostFreshWorld = false;
                        mHostWorldOriginKnown = true;
                        CoopPlugin.Log.LogInfo("[Coop] host: son kayda devam");
                        gm.setGameStateGameContinue();
                    }
                    else
                    {
                        mHostFreshWorld = true;
                        mHostWorldOriginKnown = true;
                        CoopPlugin.Log.LogInfo("[Coop] host: kayıt yok — menüden başlat");
                    }
                }
            }
            else if (Role == "guest")
            {
                // Lobiden baslatilan oturumda misafir, hostun START kararini
                // uygulamadan once hicbir dunya kurulumu yapmaz; CLI akisi
                // davranisi korunur.
                if (mLobbyActive && mRemoteLobbyStartSeen && !mLobbyStarted)
                {
                    ApplyGuestLobbyStart();
                }
            }

            GameState currentState = gm.getGameState();
            if (!ReferenceEquals(mObservedGameState, currentState))
            {
                bool previousWasGameState = mObservedGameState is GameStateGame;
                mObservedGameState = currentState;
                if (currentState is GameStateGame)
                {
                    if (Role == "host")
                    {
                        if (sLoadGameFileField != null)
                        {
                            mHostFreshWorld = IsFreshGameState(currentState);
                            mHostWorldOriginKnown = true;
                        }
                        else if (!mHostWorldOriginKnown)
                        {
                            mHostFreshWorld = true;
                            CoopPlugin.Log.LogWarning("[Coop] dunya kaynagi okunamadi; fresh bootstrap kapisi kullaniliyor");
                        }
                    }
                    GameLoaded = false;
                    mStartApplied = false;
                    mHostStartAuthorizedPending = false;
                    mHostBootstrapStarted = false;
                    mHostWorldStable = false;
                    mOnGameLoadedLogged = false;
                    if (Role == "host") mHostWaitingSinceUtc = null;
                }
                else if (CoopGameStateTransitionPolicy.ShouldInvalidateWorld(
                    previousWasGameState, currentIsGameState: false))
                {
                    GameLoaded = false;
                    mStartApplied = false;
                    mHostStartAuthorizedPending = false;
                    mHostWorldStable = false;
                    mRecoveryRequested = false;
                    mRecoveryPending = false;
                    CoopPlugin.Log.LogInfo("[Coop] aktif oyun durumu kapandi; snapshot uygunlugu iptal edildi");
                }
            }

            if (Role == "host" && mHostFreshWorld && !mHostBootstrapStarted &&
                !mHostRecoveryReloadPending)
            {
                TryStartHostBootstrap(gm);
            }

            if (Role == "host" && !mHostFreshWorld && IsWaitingForStart())
            {
                mHostWorldStable = true;
            }

            if (Role == "host" && mHostBootstrapStarted && !mHostWorldStable && IsHostBootstrapWorldStable())
            {
                mHostWorldStable = true;
                CoopStatusOverlay.Notify("ORTAK INIS HAZIR");
                CoopPlugin.Log.LogInfo("[Coop] ColonyShip inis baslangici donduruldu; ortak snapshot hazir");
            }

            if (mLoadQueued) TryStartLocalSnapshotLoad(gm);

            if (Role == "host" && CoopHostBootstrapPolicy.CanTransmitInitialSnapshot(
                    mHostWorldStable, mGuestConnected, mHandshakeDone, mInitialSaveSent))
            {
                TrySendInitialSave();
            }

            if (IsWaitingForStart())
            {
                if (Role == "host")
                {
                    if (mHostRecoveryReloadPending)
                    {
                        MarkHostRecoveryReady();
                    }
                    else
                    {
                        if (mGuestConnected && !mInitialSaveSent && !mHostFreshWorld)
                        {
                            TrySendInitialSave();
                            return;
                        }
                        if (mInitialSaveSent)
                        {
                            MarkHostReady();
                        }
                    }
                }
                else if (mInitialSaveReceived || mStartup != null)
                {
                    MarkGuestReady();
                }
                if (Role == "guest") TryApplyAuthorizedStart();
            }

            // READY arrives on the network thread, but Unity lifecycle
            // transitions must be applied on the main thread.
            if (Role == "host" && mHostStartAuthorizedPending)
            {
                ApplyAuthorizedHostStart();
            }

            CheckStartupTimeout();
        }

        private void ApplyPendingWorldPacket()
        {
            byte[] packet = null;
            lock (mPendingWorldLock)
            {
                if (mPendingWorldPacket != null)
                {
                    packet = mPendingWorldPacket;
                    mPendingWorldPacket = null;
                }
            }
            if (packet != null) OnWorldReceived(packet, 1);
        }

        private void TryStartLocalSnapshotLoad(GameManager gm)
        {
            if (!mLoadQueued || mLoadTriggered) return;
            mLoadTriggered = true;
            string path = SaveData.FolderName + "/coop_recv.sav";
            if (!File.Exists(path))
            {
                CoopPlugin.Log.LogError("[Coop] coop_recv.sav yok: " + path);
                mLoadTriggered = false;
                return;
            }
            try
            {
                SaveData sd = new SaveData(path, DateTime.Now);
                int planetIndex = sd.getPlanetIndex();
                Challenge challenge = sd.createChallenge();
                CoopPlugin.Log.LogInfo("[Coop] " + Role + " snapshot yukluyor (gezegen=" + planetIndex + ")...");
                if (Role == "guest" && !mGuestLandingReplay)
                    ClearGuestCinematic("snapshot yukleme");
                CoopGuestSnapshotTransition.Begin();
                gm.setGameStateGameLoad(path, planetIndex, challenge);
            }
            catch (Exception ex)
            {
                CoopGuestSnapshotTransition.End();
                CoopPlugin.Log.LogError("[Coop] yükleme hatası: " + ex.Message);
                mLoadTriggered = false;
                FailStartup("guest snapshot load failed: " + ex.Message, true);
            }
        }

        private void MarkGuestReady()
        {
            lock (mStartupLock)
            {
                if (mStartup == null || mStartup.LocalWorldReady) return;
            }

            string loadedWorldHash;
            if (!TryVerifyLoadedWorld(out loadedWorldHash)) return;

            byte[] ready = null;
            lock (mStartupLock)
            {
                if (mStartup == null || mStartup.LocalWorldReady) return;
                mStartup.MarkLocalWorldReady();
                TickIndex = mStartup.WorldTick;
                ready = CoopProtocol.CreateReadyPacket(
                    mStartup.SessionId, loadedWorldHash, mStartup.WorldTick);
            }
            if (!Send(ready))
            {
                FailStartup("READY transmission failed", false);
                return;
            }
            CoopPlugin.Log.LogInfo("[Coop] misafir READY gönderdi — dünya kuruldu");
            mSnapshotVerificationPending = false;
            mLoadQueued = false;
        }

        /// <summary>
        /// Dual reload: host, guest ile ayni coop_recv.sav checkpoint'ini
        /// yukledikten sonra ayni dogrulamayi kosar; iki taraf READY olmadan
        /// START authorize edilmez (CoopRecoveryReloadPolicy).
        /// </summary>
        private void MarkHostRecoveryReady()
        {
            lock (mStartupLock)
            {
                if (mStartup == null || mStartup.LocalWorldReady) return;
            }

            string loadedWorldHash;
            if (!TryVerifyLoadedWorld(out loadedWorldHash)) return;

            byte[] start = null;
            lock (mStartupLock)
            {
                if (mStartup == null || mStartup.LocalWorldReady) return;
                mStartup.MarkLocalWorldReady();
                TickIndex = mStartup.WorldTick;
            }
            mSnapshotVerificationPending = false;
            mLoadQueued = false;
            mLoadTriggered = false;
            mHostRecoveryReloadPending = false;
            CoopPlugin.Log.LogInfo("[Coop] host recovery checkpoint dogrulandi — START kapisinda");

            start = TryAuthorizeHostStart();
            if (start != null) DispatchAuthorizedHostStart(start, false);
        }

        private bool TryVerifyLoadedWorld(out string loadedWorldHash)
        {
            loadedWorldHash = "";
            string expectedHash;
            lock (mStartupLock)
            {
                if (mStartup == null) return false;
                expectedHash = mStartup.WorldHash;
            }

            // Planetbase mutates ID/disaster fields while deserializing. Apply
            // the captured host values before READY, then hash the real
            // re-serialized world. A projected hash is never accepted.
            if (!ApplyAuthoritativeFrameIndex() ||
                !ApplyAuthoritativeIds() ||
                !ApplyAuthoritativeDisasters() ||
                !ApplyAuthoritativeShipManagerClock() ||
                !ApplyAuthoritativeEnvironment())
            {
                FailStartup("authoritative loader state could not be restored before READY", true);
                return false;
            }

            byte[] loadedBytes;
            string rawLoadedWorldHash;
            if (!TryCreateAdmittedSnapshot("loaded " + Role + " world", out loadedBytes, out rawLoadedWorldHash))
                return false;

            byte[] receivedBytes;
            try
            {
                string receivedPath = SaveData.FolderName + "/coop_recv.sav";
                receivedBytes = File.ReadAllBytes(receivedPath);
                CoopLoadedWorldVerification.AssertRestoredState(loadedBytes, receivedBytes);
                string expectedLoadedHash = CoopWorldHash.ComputeForLoadedWorld(receivedBytes);
                string actualLoadedHash = CoopWorldHash.ComputeForLoadedWorld(loadedBytes);
                if (!StringComparer.Ordinal.Equals(expectedLoadedHash, actualLoadedHash))
                {
                    CoopXmlDifference difference =
                        CoopWorldHash.FindFirstLoadedWorldDifference(
                            receivedBytes, loadedBytes);
                    throw new InvalidDataException(
                        "loaded snapshot differs after loader normalization: expected=" +
                        expectedLoadedHash + " actual=" + actualLoadedHash +
                        " first=" + difference.Path + " " + difference.Description +
                        " expected='" + difference.LeftValue + "' actual='" +
                        difference.RightValue + "'");
                }
                loadedWorldHash = expectedHash;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] " + Role + " loader restore dogrulamasi basarisiz: " + ex.Message);
                FailStartup("loaded " + Role + " world restore verification failed: " + ex.Message, true);
                return false;
            }

            if (StringComparer.Ordinal.Equals(loadedWorldHash, "ERR"))
            {
                FailStartup("loaded " + Role + " world hash failed", true);
                return false;
            }
            if (Role == "host") LastHostHash = loadedWorldHash;
            else LastGuestHash = loadedWorldHash;
            if (StringComparer.Ordinal.Equals(expectedHash, loadedWorldHash)) return true;

            string detail = "unknown loaded XML difference";
            try
            {
                CoopXmlDifference difference = CoopDiagnostics.FindFirstDifference(
                    receivedBytes, loadedBytes);
                detail = difference.AreEqual
                    ? "world hash differs although loaded XML is equal"
                    : difference.Path + " (received='" + difference.LeftValue +
                        "', loaded='" + difference.RightValue + "')";
            }
            catch (Exception ex)
            {
                detail = "diagnostic comparison failed: " + ex.Message;
            }

            CoopPlugin.Log.LogError("[Coop] " + Role + " loaded world does not match authoritative snapshot: expected=" +
                expectedHash + " loaded=" + loadedWorldHash + " first=" + detail);
            CoopStatusOverlay.Notify("SNAPSHOT YUKLEME DOGRULANAMADI");
            FailStartup("loaded " + Role + " world mismatch at " + detail, true);
            return false;
        }

        private byte[] TryAuthorizeHostStart()
        {
            lock (mStartupLock)
            {
                if (mStartup == null || mStartPacketSent || !mStartup.CanStart)
                    return null;
                if (!mStartup.AuthorizeStart())
                    return null;
                mStartPacketSent = true;
                return CoopProtocol.CreateStartPacket(
                    mStartup.SessionId,
                    mStartup.WorldTick,
                    mAuthoritativePaused,
                    mAuthoritativeScaleIndex);
            }
        }

        private void MarkHostReady()
        {
            lock (mStartupLock)
            {
                if (mStartup == null || mStartup.LocalWorldReady)
                    return;
                mStartup.MarkLocalWorldReady();
            }

            byte[] start = TryAuthorizeHostStart();
            if (start != null) DispatchAuthorizedHostStart(start, true);
        }

        private void OnReadyReceived(byte[] packet)
        {
            if (Role != "host") return;
            long session;
            long tick;
            string hash;
            if (!CoopProtocol.TryParseReadyPacket(packet, out session, out hash, out tick)) return;

            byte[] start = null;
            lock (mStartupLock)
            {
                if (mStartup == null || !mStartup.AcceptPeerReady(session, hash, tick)) return;
            }
            start = TryAuthorizeHostStart();
            if (start != null)
            {
                DispatchAuthorizedHostStart(start, false);
            }
            else
            {
                CoopPlugin.Log.LogInfo("[Coop] misafir READY alindi - host snapshot reload bekleniyor");
            }
        }

        private bool DispatchAuthorizedHostStart(byte[] start, bool failStartupOnError)
        {
            bool localStartPending;
            if (!CoopHostStartDispatch.TrySend(start, Send, out localStartPending))
            {
                if (failStartupOnError)
                    FailStartup("START transmission failed", false);
                else
                    mConnectionLost = true;
                return false;
            }

            if (localStartPending)
            {
                mHostStartAuthorizedPending = true;
                CoopPlugin.Log.LogInfo("[Coop] host READY dogruladi - START yayinlandi; yerel START kuruldu");
            }
            return true;
        }

        private void ApplyAuthorizedHostStart()
        {
            if (Role != "host" || !mHostStartAuthorizedPending)
                return;

            // Dual reload: host, recovery sirasinda kendi checkpoint'ini yukleyip
            // Waiting ekranina ulasir. Eski tek-tarafli "freeze release" yolu
            // kaldirildi; START her zaman asagidaki Waiting -> Updating akisindan
            // gecer (guest ile ayni gecis).
            if (mStartApplied)
            {
                mHostStartAuthorizedPending = false;
                return;
            }

            // Continue/save hosts still need Waiting -> Updating. Fresh
            // bootstrap and recovery are already in Updating.
            if (IsWaitingForStart() && !TryApplyAuthorizedStart())
                return;

            if (!IsWaitingForStart())
            {
                mHostStartAuthorizedPending = false;
                mRecoveryPending = false;
                mStartApplied = true;
                GameLoaded = true;
                mHostWaitingSinceUtc = null;
                CoopPlugin.Log.LogInfo("[Coop] host ortak simi ana thread'de serbest birakti");
            }
        }

        private void CheckStartupTimeout()
        {
            if (!CoopRecoveryPolicy.ShouldCheckStartupTimeout(
                    mStartApplied, mRecoveryPending, mStartupFailed)) return;
            TimeSpan timeout = TimeSpan.FromSeconds(StartTimeoutSeconds);
            bool timedOut = false;
            if (Role == "host" && mHostWaitingSinceUtc.HasValue)
            {
                timedOut = DateTime.UtcNow - mHostWaitingSinceUtc.Value >= timeout;
            }
            lock (mStartupLock)
            {
                if (mStartup != null && mStartup.HasTimedOut(DateTime.UtcNow, timeout)) timedOut = true;
            }
            if (timedOut) FailStartup("startup ready timeout", true);
        }

        private void FailStartup(string reason, bool notifyPeer)
        {
            if (mStartupFailed) return;
            mStartupFailed = true;
            // Lobi kapisini de kapat; yoksa vanilya menu gecisleri kilitli kalir.
            mLobbyActive = false;
            mLobbyStarted = false;
            LobbyPhase = CoopLobbyPhase.Waiting;
            PlacementSync.Instance?.RequestSessionReset();
            lock (mStartupLock)
            {
                mStartup?.Fail();
            }
            if (notifyPeer) Send(new[] { PktAbort });
            CoopPlugin.Log.LogError("[Coop] oturum başlatılamadı: " + reason + " — ana menüye dönülüyor");
            CoopGuestSnapshotTransition.BeginAuthorizedTitle();
            try { GameManager.getInstance().setGameStateTitle(); } catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] ana menüye dönüş hatası: " + ex.Message);
            }
            finally
            {
                CoopGuestSnapshotTransition.EndAuthorizedTitle();
                CoopGuestSnapshotTransition.End();
            }
            mRunning = false;
            if (UsingSteamTransport) CoopSteamService.RequestEndSession();
            try { mListener?.Stop(); } catch (Exception) { }
            try { mStream?.Close(); } catch (Exception) { }
            try { mClient?.Close(); } catch (Exception) { }
            if (sMenuInitiated)
            {
                CoopStatusOverlay.Notify("CO-OP OTURUMU KAPANDI — ANA MENÜ");
                mFailureTeardownPending = true;
            }
        }

        /// <summary>
        /// Basarisiz menu oturumunu ana thread'de kapatir: ag thread'i durur,
        /// Role temizlenir (FullMode=false) ve oyun ana menude degilse oraya
        /// gonderilir. Boylece oyunu yeniden baslatmadan yeni oturum acilabilir.
        /// </summary>
        private bool TryCompleteFailureTeardown(GameManager gm)
        {
            if (!mFailureTeardownPending) return false;
            mFailureTeardownPending = false;
            CoopPlugin.Log.LogInfo("[Coop] basarisiz oturum kapatiliyor; co-op yamalari devre disi");
            EndMenuSession();
            try
            {
                if (!(gm.getGameState() is GameStateTitle)) gm.setGameStateTitle();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] ana menüye dönüş hatası: " + ex.Message);
            }
            return true;
        }

        private bool IsSessionRunning()
        {
            lock (mStartupLock)
            {
                return mStartup != null && mStartup.Phase == CoopStartupPhase.Running;
            }
        }

        public void OnTitleTransitionRequested()
        {
            lock (mStartupLock)
            {
                mStartup?.Fail();
                mStartup = null;
            }
            GameLoaded = false;
            mStartApplied = false;
            mInitialSaveSent = false;
            mInitialSaveReceived = false;
            mLoadQueued = false;
            mLoadTriggered = false;
            mStartPacketSent = false;
            mGameOverBroadcastSent = false;
            mGameOverRequestSent = false;
            mHostStartAuthorizedPending = false;
            mHostBootstrapStarted = false;
            mHostFreshWorld = false;
            mHostWorldOriginKnown = false;
            mHostWorldStable = false;
            mHostWaitingSinceUtc = null;
            mRecoveryRequested = false;
            mRecoveryPending = false;
            mHostRecoveryReloadPending = false;
            mRecoverySessionId = 0;
            Interlocked.Exchange(ref mPendingGameOverRequest, null);
            Interlocked.Exchange(ref mPendingGameOverState, null);
            lock (mMutationFailureLock) mPendingMutationFailures.Clear();
            mMutationFailureLedger.Reset();
            mOutstandingMutations.Reset();
        }

        public bool TryGetRunningSessionId(out long sessionId)
        {
            lock (mStartupLock)
            {
                if (mStartup != null && mStartup.Phase == CoopStartupPhase.Running &&
                    mStartup.SessionId != 0)
                {
                    sessionId = mStartup.SessionId;
                    return true;
                }
            }
            sessionId = 0;
            return false;
        }

        private void ResetHostStartupForReconnect()
        {
            if (Role != "host" || mStartupFailed) return;
            lock (mStartupLock)
            {
                mStartup = null;
            }
            mInitialSaveSent = false;
            mStartPacketSent = false;
            mHostStartAuthorizedPending = false;
            mHostWaitingSinceUtc = null;
        }

        private static long CreateSessionId()
        {
            long value = DateTime.UtcNow.Ticks ^ ((long)Environment.TickCount << 32);
            return value == 0 ? 1 : value;
        }

        private static void ClearGuestCinematic(string reason)
        {
            if (Role != "guest") return;
            try
            {
                CameraManager camera = CameraManager.getInstance();
                if (camera != null && camera.isCinematic())
                {
                    camera.setCinematic(null);
                    CoopPlugin.Log.LogInfo("[Coop] misafir sinematik kamera temizlendi: " + reason);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] sinematik kamera temizlenemedi: " + ex.Message);
            }
        }

        private static bool IsLandingReplaySnapshot(byte[] xml)
        {
            if (xml == null || xml.Length == 0) return false;
            try
            {
                XmlDocument document = CoopDiagnostics.LoadXmlDocument(xml);
                XmlNode ships = document.DocumentElement?["ships"];
                if (ships == null) return false;
                foreach (XmlNode ship in ships.ChildNodes)
                {
                    if (!StringComparer.Ordinal.Equals(ship.Name, "ship")) continue;
                    if (!StringComparer.Ordinal.Equals(ship.Attributes?["type"]?.Value, "ColonyShip")) continue;
                    return StringComparer.Ordinal.Equals(ship["state"]?.Attributes?["value"]?.Value, "0");
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] landing snapshot durumu okunamadi: " + ex.Message);
            }
            return false;
        }

        /// <summary>Oyun yüklemesi tamamlandığında çağrılır.</summary>
        public void OnGameLoaded()
        {
            bool localWorldReady = false;
            CoopStartupPhase startupPhase = CoopStartupPhase.WaitingForLocalWorld;
            if (mStartupFailed || !mRunning)
            {
                // Basarisiz oturumdan sonra baslayan dunya co-op RNG/tick
                // durumuna baglanmaz; teardown OnFrame'de tamamlanir.
                CoopPlugin.Log.LogWarning("[Coop] kapanmis oturumda dunya basladi; co-op baglanmadi");
                return;
            }
            if (!mOnGameLoadedLogged)
            {
                mOnGameLoadedLogged = true;
                CoopPlugin.Log.LogInfo("[Coop] ortak simülasyon başladı (rol=" + Role + ")");
            }
            lock (mStartupLock)
            {
                if (mStartup != null)
                {
                    localWorldReady = mStartup.LocalWorldReady;
                    startupPhase = mStartup.Phase;
                    TickIndex = mStartup.WorldTick;
                    if (mStartup.Phase == CoopStartupPhase.ReadyToStart) mStartup.MarkStarted();
                }
                else
                {
                    TickIndex = Role == "guest" && mInitialSaveReceived ? mHostTickAtWorld : 0;
                }
            }
            ScheduleNextChecksumBoundary(TickIndex);
            GameLoaded = true;
            mSnapshotVerificationPending = CoopSnapshotVerificationPolicy.ShouldRemainPendingOnGameLoaded(
                Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                localWorldReady,
                startupPhase);
            long runningSessionId;
            if (Role == "guest" &&
                (!TryGetRunningSessionId(out runningSessionId) ||
                 !CoopLandingPermissionsSync.SeedCommittedFromCurrent(
                     runningSessionId, TickIndex)))
            {
                FailStartup("guest landing permissions baseline could not be restored", true);
                return;
            }
            if (Role == "guest" && !mGuestLandingReplay)
                ClearGuestCinematic("snapshot yukleme tamamlandi");
            bool frameApplied = Role != "guest" || ApplyAuthoritativeFrameIndex();
            bool idsApplied = ApplyAuthoritativeIds();
            bool disastersApplied = ApplyAuthoritativeDisasters();
            bool shipClockApplied = ApplyAuthoritativeShipManagerClock();
            bool environmentApplied = ApplyAuthoritativeEnvironment();
            if ((!frameApplied || !idsApplied || !disastersApplied ||
                !shipClockApplied || !environmentApplied))
            {
                // Host dual-reload ve guest yukleme ayni fail-closed kapidan gecer.
                FailStartup("authoritative loader state could not be restored at start", true);
                return;
            }

            if (Role == "guest")
                CoopGuestSnapshotTransition.End();

            // İlk seed — sonraki seed'ler per-tick olacak
            UnityEngine.Random.InitState(SyncedSeed + (int)TickIndex);
            RngTrace.Count = 0;

            CoopPlugin.Log.LogInfo(string.Format(
                "[Coop] RNG tohumlandı: SyncedSeed+{0} = {1} (per-tick aktif)",
                TickIndex, SyncedSeed + (int)TickIndex));
        }

        public void RequestGameOver()
        {
            long sessionId;
            bool sessionRunning = TryGetRunningSessionId(out sessionId);
            if (Role != "guest" || !CoopGameOverPolicy.ShouldSendGuestRequest(
                    sessionRunning,
                    GameLoaded,
                    CoopGameOverPolicy.IsTerminalSession(
                        Interlocked.Read(ref mTerminalSessionId), sessionId),
                    Interlocked.CompareExchange(
                        ref mPendingGameOverState, null, null) != null,
                    mGameOverRequestSent))
                return;
            mGameOverRequestSent = true;
            Send(CoopProtocol.CreateGameOverRequestPacket(sessionId, TickIndex));
            CoopStatusOverlay.Notify("MISAFIR DURUMU HOSTA BILDIRILDI");
        }

        private void HandleGameOverRequest(long requestSessionId, long requestTick)
        {
            if (Role != "host") return;
            if (!CoopGameOverPolicy.IsPlausibleRequestTick(requestTick, TickIndex))
            {
                CoopPlugin.Log.LogWarning("[Coop] eski veya gelecekteki misafir game-over istegi reddedildi");
                return;
            }
            long activeSessionId;
            bool sessionRunning = TryGetRunningSessionId(out activeSessionId);
            bool activeGameState = GameManager.getInstance().getGameState() is GameStateGame;
            CoopGameOverRequestDecision decision = CoopGameOverPolicy.DecideHostRequest(
                sessionMatches: sessionRunning && activeSessionId == requestSessionId,
                sessionRunning: sessionRunning,
                worldLoaded: GameLoaded && mStartApplied,
                activeGameState: activeGameState,
                recoveryPending: mRecoveryPending,
                sessionTerminal: CoopGameOverPolicy.IsTerminalSession(
                    Interlocked.Read(ref mTerminalSessionId), activeSessionId),
                hostTerminalCondition: activeGameState && IsHostTerminalCondition());

            if (decision == CoopGameOverRequestDecision.IgnoreTerminal)
            {
                CoopPlugin.Log.LogInfo("[Coop] terminal oturumdaki gecikmis misafir game-over istegi yok sayildi");
                return;
            }
            if (decision == CoopGameOverRequestDecision.PublishTerminal)
            {
                CoopPlugin.Log.LogInfo("[Coop] misafir game-over istegi host terminal kosulunu dogruladi tick=" + requestTick);
                BroadcastGameOver();
                if (!CoopGameOverBridge.InvokeAuthorized())
                    FailStartup("host game-over state could not be applied", true);
                return;
            }
            if (decision != CoopGameOverRequestDecision.RecoverGuest)
            {
                CoopPlugin.Log.LogWarning("[Coop] gecersiz veya yogun oturumdaki misafir game-over istegi reddedildi");
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            long lastRecovery = Interlocked.Read(ref mLastRecoveryUtcTicks);
            if (lastRecovery > 0 && now - lastRecovery <
                TimeSpan.TicksPerSecond * RecoveryCooldownSeconds)
            {
                CoopPlugin.Log.LogWarning("[Coop] misafir game-over recovery istegi soguma suresinde reddedildi");
                return;
            }
            Interlocked.Exchange(ref mLastRecoveryUtcTicks, now);

            mRecoveryRequested = false;
            CoopStatusOverlay.Notify("MISAFIR DURUMU YENILENIYOR");
            CoopPlugin.Log.LogWarning("[Coop] misafir game-over istegi alindi; host oyunu devam ediyor, guncel snapshot gonderiliyor");
            BeginWorldRecovery();
        }

        public void BroadcastGameOver()
        {
            if (Role != "host" || mGameOverBroadcastSent) return;
            long sessionId;
            if (!TryGetRunningSessionId(out sessionId)) return;
            Interlocked.Exchange(ref mTerminalSessionId, sessionId);
            mRecoveryRequested = false;
            if (!Send(CoopProtocol.CreateGameOverStatePacket(sessionId, TickIndex)))
            {
                CoopPlugin.Log.LogError("[Coop] game-over durumu misafire gonderilemedi");
                mConnectionLost = true;
                return;
            }
            mGameOverBroadcastSent = true;
            CoopStatusOverlay.Notify("OYUNU HOST SONLANDIRDI");
            CoopPlugin.Log.LogInfo("[Coop] game-over host tarafindan yayinlandi tick=" + TickIndex);
        }

        private static bool IsHostTerminalCondition()
        {
            try
            {
                Colony colony = Singleton<Colony>.getInstance();
                return colony != null && colony.getColonistCount() == 0 &&
                    colony.getGameTime() > 60.0;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] host terminal kosulu okunamadi: " + ex.Message);
                return false;
            }
        }
    }
}
