using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Planetbase Co-op modu v0.10.0 — deterministik is/isim RNG, guvenli cikis ve hata sonrasi temiz kapanis.
    /// </summary>
    [BepInPlugin("com.planetbasecoop.core", "Planetbase Co-op", "0.10.0")]
    public class CoopPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static ConfigEntry<string> sLanguageSetting;

        private static int sQuitHandled;

        private void Awake()
        {
            Log = Logger;
            sLanguageSetting = Config.Bind("General", "Language", "auto",
                "Co-op UI language: auto (Windows language), tr or en.");
            CoopText.Language = CoopText.Resolve(sLanguageSetting.Value,
                Application.systemLanguage == SystemLanguage.Turkish);
            Logger.LogInfo("[Coop] PlanetbaseCoop v0.10.0 yuklendi - deterministik RNG + temiz kapanis");
            Application.quitting += OnApplicationQuitting;
            CoopProcessExit.Install();

            var harmony = new Harmony("com.planetbasecoop.core");
            harmony.PatchAll(typeof(CoopPlugin).Assembly);
            Logger.LogInfo("[Coop] Harmony patch'leri uygulandı");

            CoopSteamService.Initialize();
            SyncManager.Create();
            CoopShipSync.Initialize();
            CoopLandingPermissionsSync.Initialize();
            CoopGameplayControlSync.Initialize();
            CoopDisasterSync.Initialize();
            CoopTradeSync.Initialize();
            CoopTimeSync.Initialize();
            if (SyncManager.FullMode)
            {
                PartnerPreview.Disable();
                CoopFocusState.EnableBackgroundProcessing();
            }
            if (!SyncManager.FullMode)
            {
                // Eski video/takip modu artik opsiyonel: -coopLegacyVideo ile
                // acilir. Argumansiz acilislar temiz baslar (lobi UI yeter).
                if (LegacyVideoRequested())
                {
                    NetHost.Create();
                }
            }
            GuestPlacement.Init();
            if (!SyncManager.FullMode && LegacyVideoRequested())
            {
                GuestCameraController.Create();
            }
            PlacementSync.Create();
            Logger.LogInfo(SyncManager.FullMode
                ? "[Coop] Modüller hazır: SyncNet, GuestPlacement, PlacementSync (legacy camera/video kapalı)"
                : "[Coop] Modüller hazır: NetHost, GuestCamera, GuestPlacement");
        }

        internal static void ToggleLanguage()
        {
            CoopText.Language = CoopText.Language == CoopLanguage.Turkish
                ? CoopLanguage.English
                : CoopLanguage.Turkish;
            if (sLanguageSetting != null)
                sLanguageSetting.Value = CoopText.ConfigValue(CoopText.Language);
        }

        private void OnApplicationQuit()
        {
            OnApplicationQuitting();
        }

        // BepInEx manager nesnesi bu oyunda MonoBehaviour mesajlarini almiyor
        // (OnGUI gibi OnApplicationQuit de tetiklenmedi). Application.quitting
        // nesneden bagimsiz statik olaydir ve OnDestroy'lardan once calisir.
        private static void OnApplicationQuitting()
        {
            if (Interlocked.Exchange(ref sQuitHandled, 1) != 0) return;
            SyncManager.ShutdownForApplicationQuit();
            CoopSteamService.Shutdown();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            CoopFocusState.Report(hasFocus);
        }

        // BepInEx zincir objesi tüm sahnelerde yaşadığı için lobi arayüzü
        // buradan çizilir; başlık ekranında GameBehaviour.OnGUI garantisi yok.
        private void OnGUI()
        {
            // Ölü kanca: BepInEx nesnesinin OnGUI'si bu oyunda tetiklenmiyor
            // (empirik olarak doğrulandı). Lobi çizimi yalnızca GameGuiPatch'ten
            // yapılır; çift çizimi önlemek için burada çağrı yoktur.
        }

        private static bool LegacyVideoRequested()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-coopLegacyVideo") return true;
            }
            return false;
        }
    }

    /// <summary>
    /// BepInEx yuklu Planetbase, cikista mono_jit_cleanup -> mono_domain_free
    /// icinde cokuyor. Olcum (cikis denemesi): vanilla 0/3, plugin'siz BepInEx
    /// 4/6, yani plugin'den bagimsiz. ProcessExit, Unity nesneleri ve Steam
    /// kapandiktan sonra, cokmeden hemen once calisir; burada kalan is yalniz
    /// isletim sisteminin zaten geri alacagi bellek temizligidir.
    /// </summary>
    internal static class CoopProcessExit
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        public static void Install()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private static void OnProcessExit(object sender, EventArgs args)
        {
            try
            {
                CoopPlugin.Log.LogInfo("[Coop] oyun kapandi; Mono temizligi atlaniyor");
                foreach (ILogListener listener in new List<ILogListener>(BepInEx.Logging.Logger.Listeners))
                    listener.Dispose();
            }
            catch (Exception) { }
            TerminateProcess(GetCurrentProcess(), 0);
        }
    }

    internal static class CoopFocusState
    {
        private static readonly System.Reflection.FieldInfo CameraInstanceField =
            AccessTools.Field(typeof(Planetbase.CameraManager), "mInstance");
        private static int sCallbackReceived;
        private static int sCallbackFocused = 1;
        private static int sBackgroundEnabled;

        public static bool IsFocused
        {
            get
            {
                return CoopApplicationFocusPolicy.Resolve(
                    Application.isFocused,
                    Volatile.Read(ref sCallbackReceived) != 0,
                    Volatile.Read(ref sCallbackFocused) != 0);
            }
        }

        public static void EnableBackgroundProcessing()
        {
            if (CoopRuntimeSubsystemPolicy.RequiresBackgroundProcessing(
                    SyncManager.FullMode, Application.runInBackground))
                Application.runInBackground = true;
            if (Interlocked.Exchange(ref sBackgroundEnabled, 1) == 0)
                CoopPlugin.Log.LogInfo("[Coop] arka plan simülasyonu etkin — " +
                    "Alt+Tab Steam callback/tick pompasını durdurmayacak");
        }

        public static void Report(bool focused)
        {
            Volatile.Write(ref sCallbackFocused, focused ? 1 : 0);
            Volatile.Write(ref sCallbackReceived, 1);
            if (!SyncManager.FullMode) return;

            CoopPlugin.Log.LogInfo("[Coop] pencere odagi degisti: " +
                (focused ? "aktif" : "arka plan"));
            try
            {
                // getInstance() tembel olusturur: odak olayi ilk sahneden once
                // gelirse kameralar sahne yuklenince yok edilir ve vanilla
                // CameraManager.update her karede NRE atip ErrorManager ile
                // oyunu dondurur. Yalniz zaten var olan yoneticiye dokun.
                Planetbase.CameraManager manager =
                    CameraInstanceField?.GetValue(null) as Planetbase.CameraManager;
                if (manager != null) FullModeCameraFocusPatch.ClearInput(manager);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] odak gecisi kamera sifirlama hatasi: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// The host owns pause and simulation speed. A second Planetbase process may
    /// still receive keyboard focus, so guest speed buttons are consumed locally
    /// and only the host's exact TimeManager state is applied.
    /// </summary>
    public static class CoopTimeSync
    {
        private const int QueueCapacity = 64;
        private static readonly object QueueLock = new object();
        private static readonly Queue<CoopAuthoritativeCommandData> Pending =
            new Queue<CoopAuthoritativeCommandData>();
        private static readonly List<CoopAuthoritativeCommandData> Scheduled =
            new List<CoopAuthoritativeCommandData>();
        private static readonly CoopSequenceWatermarks ScheduledSequences =
            new CoopSequenceWatermarks();
        private static readonly System.Reflection.BindingFlags InstanceFlags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        private static readonly System.Reflection.FieldInfo PausedField =
            typeof(Planetbase.TimeManager).GetField("mPaused", InstanceFlags);
        private static readonly System.Reflection.FieldInfo ScaleIndexField =
            typeof(Planetbase.TimeManager).GetField("mScaleIndex", InstanceFlags);
        private static readonly System.Reflection.MethodInfo ChangedMethod =
            typeof(Planetbase.TimeManager).GetMethod("onTimeScaleChanged", InstanceFlags);
        private static long sLocalSequence;
        private static int sCurrentScaleIndex;
        private static bool sApplying;

        public static bool Applying { get { return sApplying; } }
        public static int CurrentScaleIndex { get { return Volatile.Read(ref sCurrentScaleIndex); } }

        public static void Initialize()
        {
            ResetForSession();
        }

        public static void ResetForSession()
        {
            lock (QueueLock)
            {
                Pending.Clear();
                Scheduled.Clear();
                ScheduledSequences.Reset();
                sLocalSequence = 0;
            }
            Volatile.Write(ref sCurrentScaleIndex, 0);
            sApplying = false;
        }

        public static void QueueCommand(CoopAuthoritativeCommandData command)
        {
            SyncManager sync = SyncManager.Instance;
            long activeSessionId;
            bool expected = command != null && (SyncManager.Role == "host"
                ? command.Phase == CoopAuthoritativeCommandPhase.Request
                : command.Phase == CoopAuthoritativeCommandPhase.Commit);
            if (!expected || command.Kind != CoopAuthoritativeCommandKind.SetTimeState ||
                sync == null || !sync.TryGetRunningSessionId(out activeSessionId) ||
                !CoopCommandSessionPolicy.CanAccept(true, activeSessionId, command.SessionId)) return;
            bool rejected;
            lock (QueueLock)
            {
                rejected = Pending.Count >= QueueCapacity;
                if (!rejected) Pending.Enqueue(command);
            }
            if (rejected && SyncManager.Role == "guest")
                ReportFailure(command, CoopMutationFailureCode.ValidationRejected);
        }

        public static bool GuestRequest(byte operation)
        {
            if (sApplying || CoopDisasterSync.Applying || CoopTradeSync.Applying)
                return true;
            if (!SyncManager.FullMode) return true;
            if (operation > (byte)CoopTimeOperation.Unpause) return false;

            SyncManager sync = SyncManager.Instance;
            long sessionId;
            if (sync == null || !sync.GameLoaded ||
                !sync.TryGetRunningSessionId(out sessionId)) return false;
            long local = Interlocked.Increment(ref sLocalSequence);
            long sequence = CoopPlacementRequestIds.For(
                SyncManager.Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                local);
            CoopAuthoritativeCommandData request = new CoopAuthoritativeCommandData(
                sessionId,
                sequence,
                0,
                CoopAuthoritativeCommandKind.SetTimeState,
                CoopAuthoritativeCommandPhase.Request,
                CoopTimeCommandCodec.EncodeRequest((CoopTimeOperation)operation));
            if (SyncManager.Role == "guest")
            {
                if (!sync.TrySendPacket(CoopProtocol.CreateAuthoritativeCommandPacket(request)))
                    sync.ReportMutationFailure(
                        CoopMutationKind.Time,
                        sequence,
                        CoopMutationFailureCode.ApplyFailed);
            }
            else
            {
                HandleHostRequest(request, Interlocked.Read(ref sync.TickIndex), false);
            }
            return false;
        }

        public static bool TryRead(out bool paused, out int scaleIndex)
        {
            paused = false;
            scaleIndex = 0;
            try
            {
                Planetbase.TimeManager manager = Planetbase.Singleton<Planetbase.TimeManager>.getInstance();
                if (manager == null || PausedField == null || ScaleIndexField == null) return false;
                paused = (bool)PausedField.GetValue(manager);
                scaleIndex = (int)ScaleIndexField.GetValue(manager);
                Volatile.Write(ref sCurrentScaleIndex,
                    System.Math.Max(0, System.Math.Min(2, scaleIndex)));
                return true;
            }
            catch (System.Exception) { return false; }
        }

        public static bool Apply(bool paused, int scaleIndex)
        {
            try
            {
                Planetbase.TimeManager manager = Planetbase.Singleton<Planetbase.TimeManager>.getInstance();
                if (manager == null || PausedField == null || ScaleIndexField == null) return false;
                int clampedScaleIndex = System.Math.Max(0, System.Math.Min(2, scaleIndex));
                PausedField.SetValue(manager, paused);
                ScaleIndexField.SetValue(manager, clampedScaleIndex);
                ChangedMethod?.Invoke(manager, null);
                Volatile.Write(ref sCurrentScaleIndex, clampedScaleIndex);
                return true;
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] host hiz durumu uygulanamadi: " + ex.Message);
                return false;
            }
        }

        public static bool BeforeSimulationTick(long currentTick)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return true;
            if (!ProcessPending(currentTick)) return false;

            List<CoopAuthoritativeCommandData> due = new List<CoopAuthoritativeCommandData>();
            bool late = false;
            lock (QueueLock)
            {
                for (int i = Scheduled.Count - 1; i >= 0; i--)
                {
                    CoopAuthoritativeCommandData command = Scheduled[i];
                    if (command.ApplyTick < currentTick)
                    {
                        ReportFailure(command, CoopMutationFailureCode.ApplyFailed);
                        Scheduled.RemoveAt(i);
                        late = true;
                    }
                    else if (command.ApplyTick == currentTick)
                    {
                        due.Add(command);
                        Scheduled.RemoveAt(i);
                    }
                }
            }
            if (late) return false;
            due.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            for (int i = 0; i < due.Count; i++)
            {
                CoopTimeStateData state;
                if (!CoopTimeCommandCodec.TryDecodeCommit(due[i].Payload, out state))
                {
                    CoopTimeOperation operation;
                    CoopTimeActor actor;
                    bool paused;
                    int scaleIndex;
                    if (!CoopTimeCommandCodec.TryDecodeOperationCommit(
                            due[i].Payload, out operation, out actor) ||
                        !TryRead(out paused, out scaleIndex))
                    {
                        ReportFailure(due[i], CoopMutationFailureCode.ValidationRejected);
                        return false;
                    }
                    state = CoopTimeStateTransition.Apply(
                        paused, scaleIndex, operation, actor);
                }
                sApplying = true;
                try
                {
                    if (!Apply(state.Paused, state.ScaleIndex))
                    {
                        ReportFailure(due[i], CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                }
                finally { sApplying = false; }
                CoopStatusOverlay.Notify(TimeStateText(state.Actor, state.Paused, state.ScaleIndex));
                CoopPlugin.Log.LogInfo("[Coop] ortak hiz durumu uygulandi: paused=" +
                    state.Paused + " scale=" + state.ScaleIndex + " tick=" + currentTick);
            }
            return true;
        }

        private static bool ProcessPending(long currentTick)
        {
            while (true)
            {
                CoopAuthoritativeCommandData command = null;
                lock (QueueLock)
                {
                    if (Pending.Count > 0) command = Pending.Dequeue();
                }
                if (command == null) return true;
                if (SyncManager.Role == "host")
                {
                    if (!HandleHostRequest(command, currentTick, true)) return false;
                    continue;
                }
                if (command.ApplyTick <= currentTick ||
                    command.ApplyTick - currentTick > CoopProtocol.MaxCommandLeadTicks ||
                    !Schedule(command))
                {
                    ReportFailure(command, CoopMutationFailureCode.ValidationRejected);
                    return false;
                }
            }
        }

        private static bool HandleHostRequest(
            CoopAuthoritativeCommandData request,
            long currentTick,
            bool fromGuestNetwork)
        {
            CoopTimeOperation operation;
            long applyLeadTicks = CoopCommandLeadPolicy.ApplyLeadTicks(
                CurrentScaleIndex,
                CoopNetworkTiming.CurrentBudgetMilliseconds);
            if (request == null || request.Phase != CoopAuthoritativeCommandPhase.Request ||
                !CoopTimeCommandCodec.TryDecodeRequest(request.Payload, out operation) ||
                currentTick > CoopProtocol.MaxWorldTick - applyLeadTicks) return true;

            long latestTick = currentTick;
            lock (QueueLock)
            {
                for (int i = 0; i < Scheduled.Count; i++)
                    if (Scheduled[i].ApplyTick > latestTick)
                        latestTick = Scheduled[i].ApplyTick;
            }
            CoopTimeActor actor = fromGuestNetwork ? CoopTimeActor.Guest : CoopTimeActor.Host;
            long applyTick = Math.Max(currentTick + applyLeadTicks, latestTick + 1);
            if (applyTick - currentTick > CoopProtocol.MaxCommandLeadTicks ||
                applyTick > CoopProtocol.MaxWorldTick) return true;
            CoopAuthoritativeCommandData commit = new CoopAuthoritativeCommandData(
                request.SessionId,
                request.Sequence,
                applyTick,
                CoopAuthoritativeCommandKind.SetTimeState,
                CoopAuthoritativeCommandPhase.Commit,
                CoopTimeCommandCodec.EncodeOperationCommit(operation, actor));
            if (!Schedule(commit)) return true;
            if (!SyncManager.Instance.TrySendPacket(
                    CoopProtocol.CreateAuthoritativeCommandPacket(commit)))
            {
                RemoveScheduled(commit.Sequence);
                SyncManager.Instance.ReportRuntimeInvariantFailure(
                    "time state commit could not be sent");
                return false;
            }
            return true;
        }

        private static bool Schedule(CoopAuthoritativeCommandData command)
        {
            lock (QueueLock)
            {
                if (Scheduled.Count >= QueueCapacity ||
                    !ScheduledSequences.TryAccept(command.Sequence)) return false;
                Scheduled.Add(command);
                return true;
            }
        }

        private static void RemoveScheduled(long sequence)
        {
            lock (QueueLock)
            {
                for (int i = Scheduled.Count - 1; i >= 0; i--)
                    if (Scheduled[i].Sequence == sequence) Scheduled.RemoveAt(i);
            }
        }

        private static void ReportFailure(
            CoopAuthoritativeCommandData command,
            CoopMutationFailureCode code)
        {
            if (command == null || SyncManager.Instance == null) return;
            if (SyncManager.Role == "guest")
                SyncManager.Instance.ReportMutationFailure(
                    CoopMutationKind.Time,
                    command.Sequence,
                    code);
            else
                SyncManager.Instance.ReportRuntimeInvariantFailure(
                    "authoritative time command failed: " + command.Sequence);
        }

        private static string TimeStateText(CoopTimeActor actor, bool paused, int scaleIndex)
        {
            string who = actor == CoopTimeActor.Guest ? "MISAFIR" :
                actor == CoopTimeActor.Host ? "HOST" : "SISTEM";
            if (paused) return who + " OYUNU DURAKLATTI";
            return who + " OYUNU " + (scaleIndex == 2 ? "4X" :
                scaleIndex == 1 ? "2X" : "1X") + " HIZA ALDI";
        }
    }

    [HarmonyPatch(typeof(Planetbase.TimeManager), "increaseSpeed")]
    public static class CoopTimeIncreasePatch
    {
        [HarmonyPrefix] public static bool Prefix() { return CoopTimeSync.GuestRequest(0); }
    }

    [HarmonyPatch(typeof(Planetbase.TimeManager), "decreaseSpeed")]
    public static class CoopTimeDecreasePatch
    {
        [HarmonyPrefix] public static bool Prefix() { return CoopTimeSync.GuestRequest(1); }
    }

    [HarmonyPatch(typeof(Planetbase.TimeManager), "setNormalSpeed")]
    public static class CoopTimeNormalPatch
    {
        [HarmonyPrefix] public static bool Prefix() { return CoopTimeSync.GuestRequest(2); }
    }

    [HarmonyPatch(typeof(Planetbase.TimeManager), "pause")]
    public static class CoopTimePausePatch
    {
        [HarmonyPrefix] public static bool Prefix() { return CoopTimeSync.GuestRequest(3); }
    }

    [HarmonyPatch(typeof(Planetbase.TimeManager), "unpause")]
    public static class CoopTimeUnpausePatch
    {
        [HarmonyPrefix] public static bool Prefix() { return CoopTimeSync.GuestRequest(4); }
    }

    // ==================== GAME MANAGER FIXED UPDATE ====================

    /// <summary>
    /// PER-TICK RNG + TICK KAPI + AUTO-ADVANCE + ON SIM TICK
    ///
    /// GameManager.fixedUpdate prefix: her tick başında RNG'yi sabit tohumla yeniden tohumla.
    /// Bu, rendering/UI kodunun Random tüketimini tamamen önemsiz kılar — aynı tick = aynı tohum.
    ///
    /// GameStateGame.fixedUpdate prefix: tick kapısı (sim dondurulacaksa atla).
    ///
    /// GameManager.fixedUpdate postfix: tick sayacı + beacon + checksum + partner preview.
    /// </summary>
    [HarmonyPatch(typeof(Planetbase.GameManager), "fixedUpdate")]
    public static class SimTickPatch
    {
        [ThreadStatic]
        private static bool sSkippedBySyncGate;
        [ThreadStatic]
        private static bool sSimulationAdvanced;
        [ThreadStatic]
        private static bool sAllowNestedGameStateUpdate;

        internal static bool AllowNestedGameStateUpdate => sAllowNestedGameStateUpdate;

        internal static void MarkGameStateAdvanced()
        {
            sSimulationAdvanced = true;
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            sSkippedBySyncGate = false;
            sSimulationAdvanced = false;
            sAllowNestedGameStateUpdate = true;
            if (SyncManager.Instance == null || !SyncManager.FullMode)
            {
                return true;
            }

            if (SyncManager.UsingSteamTransport)
                CoopSteamService.PumpTransport();

            if (SyncManager.Instance.IsHostBootstrapActive())
            {
                return true;
            }

            if (!CoopRecoveryPolicy.ShouldRunGameManagerFixedUpdate(
                    SyncManager.Role == "host",
                    SyncManager.Instance.IsHostRecoveryPending(),
                    SyncManager.Instance.IsSnapshotVerificationPending(),
                    SyncManager.Instance.IsLocalSnapshotLoadPending()))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }

            // Let vanilla finish a snapshot loader, but never let input bypass
            // the coordinator once the loader has reached the Waiting screen.
            bool waitingForStart = SyncManager.Instance.IsWaitingForStart();
            bool worldTransferPending = SyncManager.Instance.IsWorldTransferPending();
            if (!CoopFixedUpdatePolicy.ShouldRunVanilla(
                    waitingForStart,
                    SyncManager.Instance.ShouldBlockVanillaStart(),
                    worldTransferPending))
            {
                sAllowNestedGameStateUpdate = false;
                return false;
            }

            // Do not let co-op time/RNG/placement code run while a guest
            // snapshot is being installed or while the host is holding
            // recovery. Vanilla GameManager.fixedUpdate must still run so its
            // staged loader can reach Waiting/READY.
            if (worldTransferPending)
            {
                sAllowNestedGameStateUpdate = false;
                return true;
            }

            if (CoopFixedUpdatePolicy.ShouldSkipSimulation(
                    fullMode: true,
                    worldTransferPending: false,
                    shouldFreeze: SyncManager.Instance.ShouldFreezeSim()))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }

            if (!CoopTimeSync.BeforeSimulationTick(SyncManager.Instance.TickIndex))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }
            SyncManager.Instance.SeedRngForTick();
            if (!CoopTradeSync.BeforeSimulationTick(
                    SyncManager.Instance.TickIndex))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }
            if (!CoopDisasterSync.BeforeSimulationTick(
                    SyncManager.Instance.TickIndex))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }
            if (!CoopGameplayControlSync.BeforeSimulationTick(
                    SyncManager.Instance.TickIndex))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }
            if (!CoopLandingPermissionsSync.BeforeSimulationTick(
                    SyncManager.Instance.TickIndex))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }
            if (!CoopShipSync.ApplyScheduled(SyncManager.Instance.TickIndex))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }
            if (PlacementSync.Instance != null &&
                !PlacementSync.Instance.ApplyScheduledPlacements(
                    SyncManager.Instance.TickIndex))
            {
                sSkippedBySyncGate = true;
                sAllowNestedGameStateUpdate = false;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (sSkippedBySyncGate)
            {
                sSkippedBySyncGate = false;
                // Keep the peer tick beacon alive while the simulation is
                // gated; OnSimTick detects the frozen state and does not
                // advance the authoritative tick or emit a checksum.
                if (SyncManager.Instance != null && SyncManager.FullMode)
                {
                    SyncManager.Instance.OnSimTick(simulationAdvanced: false);
                    if (SyncManager.UsingSteamTransport)
                        CoopSteamService.PumpTransport();
                }
                return;
            }
            if (SyncManager.Instance != null && SyncManager.FullMode)
            {
                SyncManager.Instance.OnSimTick(sSimulationAdvanced);
                if (SyncManager.UsingSteamTransport)
                    CoopSteamService.PumpTransport();
            }
        }
    }

    /// <summary>
    /// TICK KAPISI: sim dondurulacaksa GameStateGame.fixedUpdate atlanır.
    /// </summary>
    [HarmonyPatch(typeof(Planetbase.GameStateGame), "fixedUpdate")]
    public static class SimGatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __state)
        {
            __state = false;
            if (!SyncManager.FullMode) return true;
            SyncManager coordinator = SyncManager.Instance;

            __state = CoopFixedUpdatePolicy.ShouldRunGameStateUpdate(
                fullMode: true,
                coordinatorAvailable: coordinator != null,
                worldTransferPending: coordinator != null && coordinator.IsWorldTransferPending(),
                allowSimulation: SimTickPatch.AllowNestedGameStateUpdate);
            return __state;
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (__state && SyncManager.Instance != null && SyncManager.FullMode)
                SimTickPatch.MarkGameStateAdvanced();
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "updateUi")]
    public static class FullModeUiFocusPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !SyncManager.FullMode || (SyncManager.Instance != null &&
                CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused));
        }
    }

    // ==================== GAME LOADED ====================

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "onStartUpdating")]
    public static class GameLoadedPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (SyncManager.Instance != null && SyncManager.FullMode)
            {
                CoopWindowMode.Apply();
                SyncManager.Instance.OnGameLoaded();
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "update")]
    public static class FullModeGameUpdateFocusPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !SyncManager.FullMode || (SyncManager.Instance != null &&
                CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused));
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "isSaveAllowed")]
    public static class GuestSavePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (SyncManager.Role == "guest")
            {
                __result = false;
            }
        }
    }

    // ==================== UPDATE + ONGUI ====================

    [HarmonyPatch(typeof(Planetbase.GameBehaviour), "Update")]
    public static class GameUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            // Steam davet callback'leri ana menüde de çalışmalıdır; bu yüzden
            // FullMode kapısından önce pompalanır.
            CoopSteamService.Pump();
            if (!SyncManager.FullMode && GuestCameraController.Instance != null)
            {
                GuestCameraController.Instance.Tick();
            }
            if (SyncManager.Instance != null && SyncManager.FullMode)
            {
                SyncManager.Instance.OnFrame();
                if (SyncManager.UsingSteamTransport)
                    CoopSteamService.PumpTransport();
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameBehaviour), "OnGUI")]
    public static class GameGuiPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            // Eski misafir görüntüsü (video modunda)
            if (!SyncManager.FullMode && GuestCameraController.Instance != null)
            {
                GuestCameraController.Instance.DrawGUI();
            }
            if (SyncManager.FullMode && PlacementSync.Instance != null)
            {
                PlacementSync.Instance.OnGUI();
            }
            CoopStatusOverlay.OnGUI();
            // Lobi arayüzünün TEK çizim kancası. GameBehaviour useGUILayout=true
            // açıldığından (GameBehaviourLayoutEnablerPatch) GUILayout tabanlı
            // lobi panelleri burada doğru biçimde render edilir.
            CoopLobbyUI.OnGUI("game");
        }
    }

    // GameBehaviour.Start içinde useGUILayout=false set edilir; bu, OnGUI'ye
    // EventType.Layout event'lerinin gelmesini engeller ve GUILayout.Window
    // tabanlı arayüzler (lobi paneli) ekranda GÖRÜNMEZ olur. Layout'u geri
    // açarak IMGUI layout pipeline'ını devreye sokuyoruz.
    [HarmonyPatch(typeof(Planetbase.GameBehaviour), "Start")]
    public static class GameBehaviourLayoutEnablerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MonoBehaviour __instance)
        {
            __instance.useGUILayout = true;
        }
    }

    // ==================== CAMERA UPDATE (eski video modu) ====================

    [HarmonyPatch(typeof(Planetbase.CameraManager), "update")]
    public static class CameraUpdatePatch
    {
        private static bool sLoggedOnce;
        [HarmonyPrefix]
        public static bool Prefix(Planetbase.CameraManager __instance)
        {
            if (!sLoggedOnce) { sLoggedOnce = true; CoopPlugin.Log.LogInfo("[Coop] CameraManager.update hook aktif"); }
            if (SyncManager.Instance != null && SyncManager.Instance.ShouldFreezeInitialCinematic())
            {
                return false;
            }
            if (!SyncManager.FullMode || CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused))
            {
                return true;
            }
            try
            {
                FullModeCameraFocusPatch.ClearInput(__instance);
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] odak disi kamera update girdisi temizlenemedi: " + ex.Message);
            }
            // Free-camera input must stay disabled while this process is in the
            // background, but an active cinematic still needs update frames so
            // that it can finish and release its object references normally.
            return CoopCameraUpdatePolicy.ShouldRun(
                SyncManager.FullMode,
                CoopFocusState.IsFocused,
                __instance.isCinematic());
        }
    }

    /// <summary>
    /// IntroCinemetic may retain a ColonyShip after authoritative recycling or
    /// world replacement destroys it. Vanilla then dereferences the ship's null
    /// object. Stop only that stale intro frame; valid cinematics keep vanilla
    /// behavior on both full-mode peers.
    /// </summary>
    [HarmonyPatch(typeof(Planetbase.IntroCinemetic), "update")]
    public static class GuestIntroCinematicSafetyPatch
    {
        private static readonly System.Reflection.FieldInfo ColonyShipField =
            typeof(Planetbase.IntroCinemetic).GetField("mColonyShip",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.IntroCinemetic __instance)
        {
            if (!SyncManager.FullMode || ColonyShipField == null) return true;
            try
            {
                Planetbase.ColonyShip ship =
                    ColonyShipField.GetValue(__instance) as Planetbase.ColonyShip;
                if (CoopIntroCinematicPolicy.ShouldRun(
                        SyncManager.FullMode,
                        shipFieldAvailable: true,
                        shipPresent: ship != null,
                        shipDestroyed: ship != null && ship.isDestroyed()))
                    return true;

                ClearStaleCinematic();
                CoopPlugin.Log.LogWarning("[Coop] gecersiz intro sinematigi temizlendi");
                return false;
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] intro sinematik guvenlik kontrolu basarisiz: " + ex.Message);
                return true;
            }
        }

        private static void ClearStaleCinematic()
        {
            try
            {
                Planetbase.CameraManager manager = Planetbase.CameraManager.getInstance();
                if (manager != null) manager.setCinematic(null);
            }
            catch (System.Exception clearException)
            {
                CoopPlugin.Log.LogDebug("[Coop] stale intro temizleme atlandi: " + clearException.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.ParticleManager), "clear")]
    public static class GuestRecoveryParticleCleanupPatch
    {
        private static readonly System.Reflection.FieldInfo ParticleSystemsField =
            typeof(Planetbase.ParticleManager).GetField("mParticleSystems",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        [HarmonyPrefix]
        public static void Prefix(Planetbase.ParticleManager __instance)
        {
            if (!CoopRecoveryPolicy.ShouldCleanupParticlesBeforeWorldClear(
                    SyncManager.FullMode))
                return;

            try
            {
                var particles = ParticleSystemsField?.GetValue(__instance)
                    as System.Collections.Generic.IEnumerable<Planetbase.ParticleSystemData>;
                if (particles == null) return;

                var staleParticles = new System.Collections.Generic.List<Planetbase.ParticleSystemData>(particles);
                if (staleParticles.Count == 0) return;

                __instance.destroy(staleParticles);
                CoopPlugin.Log.LogInfo("[Coop] world clear eski dunya parcaciklarini temizledi: " + staleParticles.Count);
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] recovery parcacik temizligi basarisiz: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.CameraManager), "fixedUpdate")]
    public static class FullModeCameraFocusPatch
    {
        private static readonly System.Reflection.FieldInfo AccelerationField =
            typeof(Planetbase.CameraManager).GetField("mAcceleration",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo RotationAccelerationField =
            typeof(Planetbase.CameraManager).GetField("mRotationAcceleration",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo ZoomAxisField =
            typeof(Planetbase.CameraManager).GetField("mZoomAxis",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo PreviousMouseXField =
            typeof(Planetbase.CameraManager).GetField("mPreviousMouseX",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.CameraManager __instance)
        {
            if (!SyncManager.FullMode || CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused))
            {
                return true;
            }
            try
            {
                ClearInput(__instance);
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] odak disi kamera girdisi temizlenemedi: " + ex.Message);
            }
            return false;
        }

        internal static void ClearInput(Planetbase.CameraManager instance)
        {
            AccelerationField?.SetValue(instance, Vector3.zero);
            RotationAccelerationField?.SetValue(instance, 0f);
            ZoomAxisField?.SetValue(instance, 0f);
            PreviousMouseXField?.SetValue(instance, Input.mousePosition.x);
        }
    }

    // ==================== PROFIL KLASORU ====================

    [HarmonyPatch(typeof(Planetbase.Util), "getFilesFolder")]
    public static class FilesFolderPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref string __result)
        {
            if (!SyncManager.FullMode ||
                (SyncManager.Role != "guest" && !CoopTestHooks.HostUsesProfile))
            {
                return true;
            }
            string original = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.MyDocuments) + "/Planetbase";
            __result = SyncManager.GetFilesFolderOverride(original);
            return false;
        }
    }

    // ==================== RNG TRACE (genişletilmiş: 200 çekiliş) ====================

    /// <summary>
    /// RNG tanılama: her iki tarafın Random çekilişlerini loglayıp karşılaştırmak için.
    /// Per-tick seeding aktifken, her tick'in ilk çekilişlerinin aynı olmasını bekleriz.
    /// </summary>
    public static class RngTrace
    {
        public static int Count;
        public const int Limit = 200;

        [HarmonyPatch(typeof(UnityEngine.Random), "Range", new[] { typeof(float), typeof(float) })]
        public static class RangeFloatPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ref float __result, float minInclusive, float maxInclusive)
            {
                if (SyncManager.FullMode && Count < Limit)
                {
                    Count++;
                    long t = SyncManager.Instance != null ? SyncManager.Instance.TickIndex : -1;
                    // Sadece her 10. çekilişi logla (performans) — ilk 20 ve her 50. çekiliş her zaman
                    if (Count <= 20 || Count % 50 == 0)
                    {
                        CoopPlugin.Log.LogInfo(string.Format(
                            "[Coop] RNG #{0} Range({1:F2},{2:F2})={3:F4} tick={4}",
                            Count, minInclusive, maxInclusive, __result, t));
                    }
                }
            }
        }

        [HarmonyPatch(typeof(UnityEngine.Random), "value", HarmonyLib.MethodType.Getter)]
        public static class ValuePatch
        {
            [HarmonyPostfix]
            public static void Postfix(ref float __result)
            {
                if (SyncManager.FullMode && Count < Limit)
                {
                    Count++;
                    long t = SyncManager.Instance != null ? SyncManager.Instance.TickIndex : -1;
                    if (Count <= 20 || Count % 50 == 0)
                    {
                        CoopPlugin.Log.LogInfo(string.Format(
                            "[Coop] RNG #{0} value={1:F4} tick={2}", Count, __result, t));
                    }
                }
            }
        }
    }

    public static class CoopDeterministicRuntime
    {
        public static void ResetDisasterRandoms(long sequence)
        {
            int suffix = unchecked((int)sequence);
            TryReset("meteor event", delegate
            {
                SetInstanceRandom(typeof(Planetbase.MeteorManager),
                    Planetbase.Singleton<Planetbase.MeteorManager>.getInstance(),
                    "mSeedRandom", CoopDeterministicSeeds.ForStream(
                        CoopDeterministicSeeds.MeteorManager ^ suffix));
            });
            TryReset("thunderstorm event", delegate
            {
                SetInstanceRandom(typeof(Planetbase.ThunderstormManager),
                    Planetbase.Singleton<Planetbase.ThunderstormManager>.getInstance(),
                    "mSeedRandom", CoopDeterministicSeeds.ForStream(
                        CoopDeterministicSeeds.ThunderstormManager ^ suffix));
            });
        }

        public static void ResetSessionState()
        {
            TryReset("meteor", delegate
            {
                SetInstanceRandom(typeof(Planetbase.MeteorManager),
                    Planetbase.Singleton<Planetbase.MeteorManager>.getInstance(),
                    "mSeedRandom", CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.MeteorManager));
            });
            TryReset("thunderstorm", delegate
            {
                SetInstanceRandom(typeof(Planetbase.ThunderstormManager),
                    Planetbase.Singleton<Planetbase.ThunderstormManager>.getInstance(),
                    "mSeedRandom", CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.ThunderstormManager));
            });
            TryReset("names", ResetNameGenerator);
            TryReset("work interactions", ResetInteractionWorkRandoms);
            Interlocked.Exchange(ref sInteractionWorkSequence, 0);
        }

        private static long sInteractionWorkSequence;

        /// <summary>
        /// Oturum sirasinda olusan her InteractionWork icin deterministik Random.
        /// Vanilla saat tabanli Random, 1800 sn oyun suresinden sonra her 30 sn'de
        /// bir maden kazasi zari atar; iki PC'de farkli sonuc kalici desync uretir.
        /// </summary>
        public static System.Random NextInteractionWorkRandom()
        {
            long sequence = Interlocked.Increment(ref sInteractionWorkSequence);
            return new System.Random(CoopDeterministicSeeds.ForSequence(
                CoopDeterministicSeeds.InteractionWork, sequence));
        }

        public static System.Random CreateNameGeneratorRandom()
        {
            return new System.Random(
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.NameGenerator));
        }

        private static void TryReset(string subsystem, Action reset)
        {
            try { reset(); }
            catch (System.Exception ex)
            {
                System.Exception detail = ex is System.Reflection.TargetInvocationException &&
                    ex.InnerException != null ? ex.InnerException : ex;
                CoopPlugin.Log.LogWarning("[Coop] deterministik " + subsystem +
                    " reseti tamamlanamadi: " + detail.Message);
            }
        }

        private static void ResetNameGenerator()
        {
            System.Type type = AccessTools.TypeByName("Planetbase.NameGenerator");
            if (type == null) return;
            System.Reflection.MethodInfo getInstance = type.GetMethod("getInstance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            object instance = getInstance == null ? null : getInstance.Invoke(null, null);
            SetInstanceRandom(type, instance, "mRandom",
                CoopDeterministicSeeds.ForStream(CoopDeterministicSeeds.NameGenerator));
        }

        private static void ResetInteractionWorkRandoms()
        {
            System.Type interactionType = typeof(Planetbase.Interaction);
            System.Reflection.FieldInfo listField = interactionType.GetField("mInteractions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            System.Collections.IEnumerable interactions = listField == null ? null : listField.GetValue(null) as System.Collections.IEnumerable;
            if (interactions == null) return;
            int index = 0;
            foreach (object interaction in interactions)
            {
                if (interaction == null || interaction.GetType().Name != "InteractionWork") continue;
                SetInstanceRandom(interaction.GetType(), interaction, "mRandom",
                    CoopDeterministicSeeds.ForStream(0x1A2B3C4D + index++));
            }
        }

        private static void SetInstanceRandom(System.Type type, object instance, string fieldName, int seed)
        {
            if (instance == null) return;
            System.Reflection.FieldInfo field = null;
            for (System.Type current = type; current != null && field == null; current = current.BaseType)
            {
                field = current.GetField(fieldName,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            if (field != null && field.FieldType == typeof(System.Random))
            {
                field.SetValue(instance, new System.Random(seed));
            }
        }
    }

    // Vanilla InteractionWork constructor'i new System.Random() (saat seed'i)
    // kullanir. Oturum basindaki reset yalniz o anda var olan etkilesimleri
    // kapsar; sonradan olusanlar burada deterministik seed alir.
    [HarmonyPatch(typeof(Planetbase.InteractionWork), MethodType.Constructor, new System.Type[0])]
    public static class InteractionWorkDeterministicRandomPatch
    {
        private static readonly System.Reflection.FieldInfo RandomField =
            AccessTools.Field(typeof(Planetbase.InteractionWork), "mRandom");

        [HarmonyPostfix]
        public static void Postfix(Planetbase.InteractionWork __instance)
        {
            if (!SyncManager.FullMode || RandomField == null ||
                RandomField.FieldType != typeof(System.Random)) return;
            RandomField.SetValue(__instance, CoopDeterministicRuntime.NextInteractionWorkRandom());
        }
    }

    // Ruzgar uzun omurlu bir rastgele yuruyustur. Ayni tick'te oncesinde cekilen
    // tek bir fazla/eksik Random, ruzgari iki peer'de ayirir; turbin uretimi ve
    // batarya sapar (canli: host wind=0, guest wind=0.986). Kendi akisini kullanir.
    [HarmonyPatch(typeof(Planetbase.EnvironmentManager), "updateWind")]
    public static class CoopWindDeterminismPatch
    {
        [HarmonyPrefix]
        public static void Prefix(out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            if (SyncManager.Instance != null && SyncManager.FullMode)
                UnityEngine.Random.InitState(CoopDeterministicSeeds.ForSequence(
                    CoopDeterministicSeeds.Wind, SyncManager.Instance.TickIndex));
        }

        [HarmonyPostfix]
        public static void Postfix(UnityEngine.Random.State __state)
        {
            UnityEngine.Random.state = __state;
        }
    }

    // Animasyon secimi gorseldir ama ortak Random akisini kullanir. Animasyon suresi
    // peer'ler arasinda farkli olunca fazladan cekim sonraki tum kararlari kaydirir
    // (canli: tick 275'te Character.updateInteracting). Akis geri yuklenir.
    [HarmonyPatch]
    public static class CoopAnimationRandomIsolationPatch
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            string[] names = { "updateInteracting", "updateIdle", "playAnimation", "playIdleAnimation" };
            foreach (System.Reflection.MethodInfo method in
                AccessTools.GetDeclaredMethods(typeof(Planetbase.Character)))
            {
                if (Array.IndexOf(names, method.Name) >= 0) yield return method;
            }
        }

        [HarmonyPrefix]
        public static void Prefix(out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
        }

        [HarmonyPostfix]
        public static void Postfix(UnityEngine.Random.State __state)
        {
            UnityEngine.Random.state = __state;
        }
    }

    // NameGenerator (internal) tembel olusur; co-op'ta ilk kez olusursa da
    // iki peer ayni isim akisini kullanir.
    [HarmonyPatch]
    public static class NameGeneratorDeterministicRandomPatch
    {
        private static readonly System.Type GeneratorType =
            AccessTools.TypeByName("Planetbase.NameGenerator");
        private static readonly System.Reflection.FieldInfo RandomField =
            GeneratorType == null ? null : AccessTools.Field(GeneratorType, "mRandom");

        public static bool Prepare()
        {
            return GeneratorType != null && RandomField != null &&
                RandomField.FieldType == typeof(System.Random) &&
                TargetMethod() != null;
        }

        public static System.Reflection.MethodBase TargetMethod()
        {
            // AccessTools.Constructor static .cctor ile birlikte arayip
            // AmbiguousMatchException atar ve PatchAll'u yarida birakir.
            return GeneratorType.GetConstructor(
                System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                null, System.Type.EmptyTypes, null);
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            if (!SyncManager.FullMode) return;
            RandomField.SetValue(__instance, CoopDeterministicRuntime.CreateNameGeneratorRandom());
        }
    }

    // ==================== OYUN BUG DÜZELTMESI ====================

    [HarmonyPatch(typeof(Planetbase.AiRuleTrade), "update")]
    public static class MerchantShipTradeInvariantGuardPatch
    {
        private static readonly System.Reflection.BindingFlags InstanceFlags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        private static readonly System.Reflection.FieldInfo NeededProductsField =
            typeof(Planetbase.MerchantShip).GetField("mNeededProducts", InstanceFlags);
        private static readonly System.Reflection.FieldInfo ContainedProductsField =
            typeof(Planetbase.MerchantShip).GetField("mContainedProducts", InstanceFlags);
        private static readonly System.Reflection.FieldInfo ProvidedProductsField =
            typeof(Planetbase.MerchantShip).GetField("mProvidedProducts", InstanceFlags);

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.Character character, ref bool __result)
        {
            if (!SyncManager.FullMode || character == null) return true;

            try
            {
                Planetbase.Resource resource = character.getLoadedResource();
                Planetbase.MerchantShip merchant = character.getTargetSelectable() as Planetbase.MerchantShip;
                if (resource == null || !resource.isTraded() || merchant == null || !merchant.isTrading())
                    return true;

                bool valid = NeededProductsField != null && ContainedProductsField != null &&
                    ProvidedProductsField != null && NeededProductsField.GetValue(merchant) != null &&
                    ContainedProductsField.GetValue(merchant) != null &&
                    ProvidedProductsField.GetValue(merchant) != null;
                if (valid) return true;

                __result = false;
                string reason = "MerchantShip runtime invariant failed for ship id=" + merchant.getId();
                if (SyncManager.Instance != null &&
                    SyncManager.Instance.ReportRuntimeInvariantFailure(reason))
                {
                    CoopPlugin.Log.LogError("[Coop] " + reason + "; tradeResource cagrisi engellendi");
                }
                return false;
            }
            catch (System.Exception ex)
            {
                __result = false;
                string reason = "MerchantShip runtime invariant check failed: " + ex.Message;
                if (SyncManager.Instance != null &&
                    SyncManager.Instance.ReportRuntimeInvariantFailure(reason))
                {
                    CoopPlugin.Log.LogError("[Coop] " + reason);
                }
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.Serialization), "deserializeType")]
    public static class DeserializeTypePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref string __result, System.Xml.XmlNode node)
        {
            if (node == null || node.Attributes == null || node.Attributes.Count == 0)
            {
                __result = "";
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Planetbase.VisitorEventManager), "deserialize")]
    public static class VisitorEventManagerDeserializePatch
    {
        private static readonly System.Reflection.FieldInfo NextEventField =
            typeof(Planetbase.VisitorEventManager).GetField("mNextEvent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo NextEventTimeField =
            typeof(Planetbase.VisitorEventManager).GetField("mNextEventTime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.VisitorEventManager __instance, System.Xml.XmlNode node)
        {
            if (node != null && node["visitor-event"] != null)
            {
                return true;
            }
            try
            {
                NextEventField?.SetValue(__instance, null);
                NextEventTimeField?.SetValue(__instance, 0f);
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] visitor-event temizleme hatası: " + ex.Message);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Planetbase.VisitorEvent), "deserialize")]
    public static class VisitorEventDeserializePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(System.Xml.XmlNode node)
        {
            return node != null;
        }
    }

    [HarmonyPatch(typeof(Planetbase.ColonyShip), "update")]
    public static class ColonyShipLandingGuardPatch
    {
        private static System.Reflection.FieldInfo sEngineParticles;
        private static System.Reflection.FieldInfo sModel;
        private static System.Reflection.FieldInfo sState;
        private static System.Reflection.FieldInfo sStateTimer;
        private static System.Reflection.FieldInfo sTargetPosition;
        private static System.Reflection.MethodInfo sGetSpeed;
        private static System.Reflection.MethodInfo sUpdateRotation;
        private static System.Reflection.MethodInfo sSetState;
        private static System.Reflection.MethodInfo sOnLanded;
        private static System.Reflection.MethodInfo sOnSpawnDone;
        private static System.Reflection.FieldInfo sSpawnList;
        private static System.Reflection.FieldInfo sSpawnIndex;
        private static System.Reflection.FieldInfo sSpawnTimer;
        private static bool sInitialized;
        private static bool sWarned;

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.ColonyShip __instance, float timeStep)
        {
            try
            {
                EnsureInitialized();
                var particles = sEngineParticles?.GetValue(__instance) as System.Collections.Generic.List<Planetbase.ParticleSystemData>;
                int state = sState == null ? -1 : System.Convert.ToInt32(sState.GetValue(__instance));

                if (state == 3)
                {
                    var spawnList = sSpawnList?.GetValue(__instance) as System.Collections.IList;
                    int spawnIndex = sSpawnIndex == null ? -1 : System.Convert.ToInt32(sSpawnIndex.GetValue(__instance));
                    int spawnCount = spawnList == null ? 0 : spawnList.Count;
                    float spawnTimer = sSpawnTimer == null
                        ? 0f
                        : System.Convert.ToSingle(sSpawnTimer.GetValue(__instance));
                    if (CoopColonyShipPolicy.ShouldCompleteExhaustedSpawnUpdate(
                        state, spawnList != null, spawnIndex, spawnCount, spawnTimer))
                    {
                        if (sSetState == null || sOnSpawnDone == null)
                        {
                            return true;
                        }
                        object afterSpawnState = System.Enum.ToObject(sState.FieldType, 4);
                        sSetState.Invoke(__instance, new[] { afterSpawnState });
                        sOnSpawnDone.Invoke(__instance, null);
                        if (!sWarned)
                        {
                            sWarned = true;
                            CoopPlugin.Log.LogWarning("[Coop] ColonyShip spawn listesi vanilla tamamlanma penceresinde kapatildi");
                        }
                        return false;
                    }
                    if (spawnList == null)
                    {
                        // Damaged recovery state must not dereference a
                        // missing list. Preserve vanilla timer progression and
                        // finish only in the normal completion window.
                        if (sSpawnTimer != null)
                            sSpawnTimer.SetValue(__instance, spawnTimer - timeStep);
                        return false;
                    }
                }

                if (state != 0 || particles == null || particles.Count > 0)
                {
                    return true;
                }

                var model = sModel?.GetValue(__instance) as UnityEngine.GameObject;
                if (model != null)
                {
                    Planetbase.ColonyShip.addColonyEngineParticles(model, particles);
                    if (particles.Count > 0)
                    {
                        return true;
                    }
                }

                if (!sWarned)
                {
                    sWarned = true;
                    CoopPlugin.Log.LogWarning("[Coop] ColonyShip motor parcacigi yok; guvenli landing yolu kullaniliyor");
                }

                float timer = sStateTimer == null ? 0f : (float)sStateTimer.GetValue(__instance);
                sStateTimer?.SetValue(__instance, timer + timeStep);
                UnityEngine.Vector3 target = (UnityEngine.Vector3)sTargetPosition.GetValue(__instance);
                UnityEngine.Vector3 position = __instance.getPosition();
                float distance = (position - target).magnitude;
                float landingStep = timeStep * 0.5f;
                if (distance > landingStep)
                {
                    UnityEngine.Vector3 direction = (target - position).normalized;
                    float speed = (float)sGetSpeed.Invoke(__instance, null);
                    __instance.getTransform().position += speed * timeStep * direction;
                    sUpdateRotation.Invoke(__instance, null);
                }
                else
                {
                    object landedState = System.Enum.ToObject(sState.FieldType, 1);
                    sSetState.Invoke(__instance, new[] { landedState });
                    sOnLanded.Invoke(__instance, null);
                }
                return false;
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] ColonyShip landing guard hatasi: " + ex.Message);
                return false;
            }
        }

        private static void EnsureInitialized()
        {
            if (sInitialized) return;
            System.Type type = typeof(Planetbase.ColonyShip);
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            sEngineParticles = type.GetField("mEngineParticles", fields);
            sModel = type.GetField("mModel", fields);
            sState = type.GetField("mState", fields);
            sStateTimer = type.GetField("mStateTimer", fields);
            sTargetPosition = type.GetField("mTargetPosition", fields);
            sGetSpeed = type.GetMethod("getSpeed", fields);
            sUpdateRotation = type.GetMethod("updateRotation", fields);
            sSetState = type.GetMethod("setState", fields);
            sOnLanded = type.GetMethod("onLanded", fields);
            sOnSpawnDone = type.GetMethod("onSpawnDone", fields);
            sSpawnList = type.GetField("mSpawnList", fields);
            sSpawnIndex = type.GetField("mSpawnIndex", fields);
            sSpawnTimer = type.GetField("mSpawnTimer", fields);
            sInitialized = true;
        }
    }

    [HarmonyPatch(typeof(Planetbase.ColonyShip), "updateSpawnInitialCharacters")]
    public static class ColonyShipSpawnGuardPatch
    {
        private static System.Reflection.FieldInfo sSpawnList;
        private static System.Reflection.FieldInfo sSpawnIndex;
        private static bool sInitialized;
        private static bool sWarned;

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.ColonyShip __instance)
        {
            try
            {
                if (!sInitialized)
                {
                    System.Type type = typeof(Planetbase.ColonyShip);
                    sSpawnList = type.GetField("mSpawnList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    sSpawnIndex = type.GetField("mSpawnIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    sInitialized = true;
                }

                var list = sSpawnList?.GetValue(__instance) as System.Collections.IList;
                int index = sSpawnIndex == null ? -1 : (int)sSpawnIndex.GetValue(__instance);
                if (list != null && index >= 0 && index < list.Count)
                {
                    return true;
                }

                if (!sWarned)
                {
                    sWarned = true;
                    CoopPlugin.Log.LogWarning("[Coop] ColonyShip spawn listesi boş/tükendi; vanilla index hatası engellendi");
                }
                return false;
            }
            catch (System.Exception ex)
            {
                if (!sWarned)
                {
                    sWarned = true;
                    CoopPlugin.Log.LogWarning("[Coop] ColonyShip spawn guard hatası: " + ex.Message);
                }
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.ColonyShip), "serialize")]
    public static class ColonyShipRecoveryStateSerializePatch
    {
        private static readonly System.Reflection.BindingFlags InstanceFlags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        private static readonly System.Reflection.FieldInfo SpawnIndexField =
            typeof(Planetbase.ColonyShip).GetField("mSpawnIndex", InstanceFlags);
        private static readonly System.Reflection.FieldInfo SpawnTimerField =
            typeof(Planetbase.ColonyShip).GetField("mSpawnTimer", InstanceFlags);
        private static readonly System.Reflection.FieldInfo StateTimerField =
            typeof(Planetbase.ColonyShip).GetField("mStateTimer", InstanceFlags);
        private static readonly System.Reflection.FieldInfo SpawnListField =
            typeof(Planetbase.ColonyShip).GetField("mSpawnList", InstanceFlags);

        [HarmonyPostfix]
        public static void Postfix(Planetbase.ColonyShip __instance, System.Xml.XmlNode parent)
        {
            if (__instance == null || parent == null || parent.LastChild == null) return;
            try
            {
                float stateTimer = StateTimerField == null ? 0f : System.Convert.ToSingle(StateTimerField.GetValue(__instance));
                Planetbase.Serialization.serializeFloat(parent.LastChild, "coop-state-timer", stateTimer);
                var spawnList = SpawnListField?.GetValue(__instance) as System.Collections.IList;
                if (spawnList == null) return;
                int spawnIndex = SpawnIndexField == null ? 0 : System.Convert.ToInt32(SpawnIndexField.GetValue(__instance));
                float spawnTimer = SpawnTimerField == null ? 0f : System.Convert.ToSingle(SpawnTimerField.GetValue(__instance));
                Planetbase.Serialization.serializeInt(parent.LastChild, "coop-spawn-index", spawnIndex);
                Planetbase.Serialization.serializeFloat(parent.LastChild, "coop-spawn-timer", spawnTimer);
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] ColonyShip spawn durumu kaydedilemedi: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.ColonyShip), "deserialize")]
    public static class ColonyShipRecoveryStateDeserializePatch
    {
        private static readonly System.Reflection.BindingFlags InstanceFlags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        private static readonly System.Reflection.FieldInfo SpawnIndexField =
            typeof(Planetbase.ColonyShip).GetField("mSpawnIndex", InstanceFlags);
        private static readonly System.Reflection.FieldInfo SpawnTimerField =
            typeof(Planetbase.ColonyShip).GetField("mSpawnTimer", InstanceFlags);
        private static readonly System.Reflection.FieldInfo StateTimerField =
            typeof(Planetbase.ColonyShip).GetField("mStateTimer", InstanceFlags);
        private static readonly System.Reflection.FieldInfo SpawnListField =
            typeof(Planetbase.ColonyShip).GetField("mSpawnList", InstanceFlags);

        [HarmonyPostfix]
        public static void Postfix(Planetbase.ColonyShip __instance, System.Xml.XmlNode node)
        {
            if (__instance == null || node == null) return;
            try
            {
                System.Xml.XmlNode stateTimerNode = node["coop-state-timer"];
                if (stateTimerNode != null)
                    StateTimerField?.SetValue(__instance, Planetbase.Serialization.deserializeFloat(stateTimerNode));

                System.Xml.XmlNode indexNode = node["coop-spawn-index"];
                System.Xml.XmlNode timerNode = node["coop-spawn-timer"];
                if (indexNode == null || timerNode == null) return;

                var spawnList = new System.Collections.Generic.List<Planetbase.Specialization>();
                Planetbase.Planet planet = Planetbase.PlanetManager.getCurrentPlanet();
                var starting = planet == null ? null : planet.getStartingSpecializations();
                if (starting != null)
                {
                    for (int i = 0; i < starting.Count; i++)
                    {
                        Planetbase.SpecializationCount count = starting[i];
                        if (count == null || count.getSpecialization() == null) continue;
                        for (int j = 0; j < count.getCount(); j++) spawnList.Add(count.getSpecialization());
                    }
                }

                int spawnIndex = Planetbase.Serialization.deserializeInt(indexNode);
                float spawnTimer = Planetbase.Serialization.deserializeFloat(timerNode);
                spawnIndex = CoopColonyShipPolicy.ClampSpawnIndex(spawnIndex, spawnList.Count);
                SpawnListField?.SetValue(__instance, spawnList);
                SpawnIndexField?.SetValue(__instance, spawnIndex);
                SpawnTimerField?.SetValue(__instance, spawnTimer);
                CoopPlugin.Log.LogInfo(string.Format(
                    "[Coop] ColonyShip spawn durumu geri yuklendi: index={0}/{1} timer={2:F2}",
                    spawnIndex, spawnList.Count, spawnTimer));
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] ColonyShip spawn durumu geri yuklenemedi: " + ex.Message);
            }
        }
    }

    // ==================== INSAAT ONIZLEME (eski video modu) ====================

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "tryPlaceModule")]
    public static class TryPlaceModulePatch
    {
        private static int sPreviewNextId = -1;

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.GameStateGame __instance)
        {
            if (SyncManager.FullMode && !CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused))
            {
                sPreviewNextId = -2;
                if (SyncManager.Role == "guest") GuestPlacement.ClearNetworkPreview();
                return false;
            }
            if (SyncManager.FullMode &&
                PlacementSync.Instance != null &&
                PlacementSync.Instance.ShouldFreezeLocalPreview)
            {
                sPreviewNextId = -2;
                return false;
            }
            if (SyncManager.FullMode)
            {
                // Vanilla owns the hologram on both instances. Restore the
                // temporary construction ID in the postfix so it cannot alter
                // the authoritative ID sequence while only previewing.
                sPreviewNextId = GuestPlacement.ReadNextId();
            }
            if (!SyncManager.FullMode && GuestPlacement.Active && !GuestPlacement.ComponentMode)
            {
                return GuestPlacement.TryPlaceModuleWithGuestRay(__instance);
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(Planetbase.GameStateGame __instance)
        {
            if (!SyncManager.FullMode) return;
            if (sPreviewNextId == -2)
            {
                sPreviewNextId = -1;
                return;
            }
            GuestPlacement.RestorePreviewId(sPreviewNextId);
            sPreviewNextId = -1;
            if (SyncManager.Role == "guest")
            {
                GuestPlacement.CaptureVanillaPreview();
            }
            GuestPlacement.NeutralizeLocalPreviewIds(__instance);
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "tryPlaceComponent")]
    public static class TryPlaceComponentPatch
    {
        private static int sPreviewNextId = -1;

        [HarmonyPrefix]
        public static bool Prefix(Planetbase.GameStateGame __instance)
        {
            if (SyncManager.FullMode && !CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused))
            {
                sPreviewNextId = -2;
                if (SyncManager.Role == "guest") GuestPlacement.ClearNetworkPreview();
                return false;
            }
            if (SyncManager.FullMode &&
                PlacementSync.Instance != null &&
                PlacementSync.Instance.ShouldFreezeLocalPreview)
            {
                sPreviewNextId = -2;
                return false;
            }
            if (SyncManager.FullMode)
            {
                sPreviewNextId = GuestPlacement.ReadNextId();
            }
            if (!SyncManager.FullMode && GuestPlacement.Active && GuestPlacement.ComponentMode)
            {
                return GuestPlacement.TryPlaceComponentWithGuestRay(__instance);
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(Planetbase.GameStateGame __instance)
        {
            if (!SyncManager.FullMode) return;
            if (sPreviewNextId == -2)
            {
                sPreviewNextId = -1;
                return;
            }
            GuestPlacement.RestorePreviewId(sPreviewNextId);
            sPreviewNextId = -1;
            if (SyncManager.Role == "guest")
            {
                GuestPlacement.CaptureVanillaPreview();
            }
            GuestPlacement.NeutralizeLocalPreviewIds(__instance);
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "cancelModulePlacement")]
    public static class GuestCancelModulePlacementPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (SyncManager.FullMode && SyncManager.Role == "guest")
            {
                GuestPlacement.ClearNetworkPreview();
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "cancelComponentPlacement")]
    public static class GuestCancelComponentPlacementPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (SyncManager.FullMode && SyncManager.Role == "guest")
            {
                GuestPlacement.ClearNetworkPreview();
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "placeModule")]
    public static class GuestPlaceModuleCleanupPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (SyncManager.FullMode && SyncManager.Role == "guest")
            {
                GuestPlacement.ClearNetworkPreview();
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "placeComponent")]
    public static class GuestPlaceComponentCleanupPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (SyncManager.FullMode && SyncManager.Role == "guest")
            {
                GuestPlacement.ClearNetworkPreview();
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "placeModule")]
    public static class AuthoritativePlaceModulePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Planetbase.GameStateGame __instance)
        {
            if (!SyncManager.FullMode)
            {
                return true;
            }
            PlacementSync coordinator = PlacementSync.Instance;
            if (coordinator == null || SyncManager.Instance == null) return false;
            if (coordinator.ApplyingAuthoritative) return true;
            if (!CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused))
            {
                return false;
            }
            return !coordinator.InterceptPlacement(__instance, false);
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "placeComponent")]
    public static class AuthoritativePlaceComponentPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Planetbase.GameStateGame __instance)
        {
            if (!SyncManager.FullMode)
            {
                return true;
            }
            PlacementSync coordinator = PlacementSync.Instance;
            if (coordinator == null || SyncManager.Instance == null) return false;
            if (coordinator.ApplyingAuthoritative) return true;
            if (!CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused))
            {
                return false;
            }
            return !coordinator.InterceptPlacement(__instance, true);
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "onButtonLink")]
    public static class AuthoritativeConnectionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Planetbase.GameStateGame __instance)
        {
            if (!SyncManager.FullMode) return true;
            PlacementSync coordinator = PlacementSync.Instance;
            if (coordinator == null || SyncManager.Instance == null) return false;
            return !coordinator.InterceptConnection(__instance);
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "onRecycle")]
    public static class AuthoritativeRecyclePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!SyncManager.FullMode) return true;
            PlacementSync coordinator = PlacementSync.Instance;
            if (coordinator == null || SyncManager.Instance == null) return false;
            return !coordinator.InterceptRecycle();
        }
    }

    [HarmonyPatch(typeof(Planetbase.Selection), "deleteSelected")]
    public static class AuthoritativeDeleteSelectedPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            PlacementSync coordinator = PlacementSync.Instance;
            bool coordinatorAvailable = coordinator != null && SyncManager.Instance != null;
            bool applying = coordinatorAvailable && coordinator.ApplyingAuthoritative;
            return CoopAuthoritativeMutationPolicy.CanRunDirectly(
                SyncManager.FullMode, coordinatorAvailable, applying);
        }
    }

    public static class CoopGuestSnapshotTransition
    {
        [System.ThreadStatic] private static int sDepth;
        [System.ThreadStatic] private static int sAuthorizedTitleDepth;

        public static bool Installing => sDepth > 0;
        public static bool AuthorizingTitle => sAuthorizedTitleDepth > 0;

        public static void Begin()
        {
            sDepth++;
        }

        public static void End()
        {
            if (sDepth > 0) sDepth--;
        }

        public static void BeginAuthorizedTitle()
        {
            sAuthorizedTitleDepth++;
        }

        public static void EndAuthorizedTitle()
        {
            if (sAuthorizedTitleDepth > 0) sAuthorizedTitleDepth--;
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameManager), "setGameStateTitle")]
    public static class GuestSnapshotTitleTransitionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            bool allow = CoopGameStateTransitionPolicy.ShouldAllowTitleTransition(
                SyncManager.FullMode,
                SyncManager.Role == "guest" ||
                    (SyncManager.Instance != null && SyncManager.Instance.IsHostRecoveryPending()),
                CoopGuestSnapshotTransition.Installing,
                CoopGuestSnapshotTransition.AuthorizingTitle);
            if (!allow)
            {
                CoopPlugin.Log.LogInfo("[Coop] snapshot kurulurken ic ice ana-menu gecisi engellendi");
                return false;
            }
            SyncManager.Instance?.OnTitleTransitionRequested();
            return allow;
        }
    }

    /// <summary>
    /// Game over is a world mutation. The guest may report the condition, but
    /// only the host publishes the final state so both windows show the same
    /// result and neither peer continues simulating alone.
    /// </summary>
    public static class CoopGameOverBridge
    {
        private static readonly System.Reflection.MethodInfo OnGameOverMethod =
            typeof(Planetbase.GameStateGame).GetMethod("onGameOver",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo GameOverField =
            typeof(Planetbase.GameStateGame).GetField("mGameOver",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        [System.ThreadStatic] private static bool sAuthorized;

        public static bool ApplyingAuthorized => sAuthorized;

        public static bool InvokeAuthorized()
        {
            try
            {
                Planetbase.GameManager manager = Planetbase.GameManager.getInstance();
                Planetbase.GameStateGame state = manager == null ? null : manager.getGameState() as Planetbase.GameStateGame;
                if (state == null || OnGameOverMethod == null) return false;
                if (GameOverField != null && (bool)GameOverField.GetValue(state)) return true;
                sAuthorized = true;
                OnGameOverMethod.Invoke(state, null);
                return true;
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] game-over uygulanamadi: " + ex.Message);
                return false;
            }
            finally
            {
                sAuthorized = false;
            }
        }
    }

    [HarmonyPatch(typeof(Planetbase.GameStateGame), "onGameOver")]
    public static class AuthoritativeGameOverPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null || CoopGameOverBridge.ApplyingAuthorized)
                return true;
            if (SyncManager.Role == "guest")
            {
                SyncManager.Instance.RequestGameOver();
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (SyncManager.FullMode && SyncManager.Role == "host")
                SyncManager.Instance?.BroadcastGameOver();
        }
    }
}
