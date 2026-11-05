using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Reflection = System.Reflection;
using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    internal sealed class CoopConstructionIdComparer : IEqualityComparer<Construction>
    {
        public bool Equals(Construction left, Construction right)
        {
            if (ReferenceEquals(left, right)) return true;
            return left != null && right != null && left.getId() == right.getId();
        }

        public int GetHashCode(Construction construction)
        {
            return construction == null ? 0 : construction.getId();
        }
    }

    public static class CoopGridDeterminism
    {
        private static readonly Reflection.FieldInfo BalanceTimerField =
            typeof(Grid).GetField(
                "mTimeToCalculateBalance",
                Reflection.BindingFlags.Instance | Reflection.BindingFlags.NonPublic);

        /// <summary>Iki peer'in loglari ayni tick'te satir satir karsilastirilir.</summary>
        public static string Describe()
        {
            try
            {
                Grid grid = Grid.getLargest();
                if (grid == null) return "yok";
                Construction collector = grid.getCollector(GridResource.Power);
                return "balance=" + grid.getBalance(GridResource.Power).ToString("R", CultureInfo.InvariantCulture) +
                    " timer=" + ((float)BalanceTimerField.GetValue(grid)).ToString("R", CultureInfo.InvariantCulture) +
                    " collector=" + (collector == null ? 0 : collector.getId()) +
                    " stored=" + Module.getOverallPowerStorage();
            }
            catch (Exception ex)
            {
                return "okunamadi: " + ex.Message;
            }
        }

        private static readonly Reflection.FieldInfo ConstructionsField =
            typeof(Grid).GetField(
                "mConstructions",
                Reflection.BindingFlags.Instance | Reflection.BindingFlags.NonPublic);

        public static Construction FindAnchor(Grid grid)
        {
            HashSet<Construction> constructions = GetConstructions(grid);
            Construction anchor = null;
            if (constructions == null) return null;
            foreach (Construction construction in constructions)
            {
                if (construction == null || construction.isDestroyed()) continue;
                if (anchor == null || construction.getId() < anchor.getId())
                    anchor = construction;
            }
            return anchor;
        }

        public static Grid ResolveFromAnchor(Selectable selectable)
        {
            Construction construction = selectable as Construction;
            return construction == null ? null : construction.getGrid();
        }

        public static bool Normalize(Grid grid)
        {
            HashSet<Construction> current = GetConstructions(grid);
            if (current == null) return false;
            List<Construction> ordered = new List<Construction>(current);
            ordered.Sort((left, right) => left.getId().CompareTo(right.getId()));
            HashSet<Construction> stable =
                new HashSet<Construction>(new CoopConstructionIdComparer());
            for (int i = 0; i < ordered.Count; i++) stable.Add(ordered[i]);
            try
            {
                ConstructionsField.SetValue(grid, stable);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static HashSet<Construction> GetConstructions(Grid grid)
        {
            if (grid == null || ConstructionsField == null) return null;
            try
            {
                return ConstructionsField.GetValue(grid) as HashSet<Construction>;
            }
            catch (Exception) { return null; }
        }
    }

    public static class CoopGameplayControlSync
    {
        private const int QueueCapacity = 128;
        private static readonly object QueueLock = new object();
        private static readonly Queue<CoopAuthoritativeCommandData> Pending =
            new Queue<CoopAuthoritativeCommandData>();
        private static readonly List<CoopAuthoritativeCommandData> Scheduled =
            new List<CoopAuthoritativeCommandData>();
        private static readonly CoopSequenceWatermarks ScheduledSequences =
            new CoopSequenceWatermarks();
        private static long sLocalSequence;
        private static bool sApplying;
        private static readonly Reflection.FieldInfo AmountCurrentField =
            typeof(GuiAmountSelector).GetField(
                "mCurrent",
                Reflection.BindingFlags.Instance | Reflection.BindingFlags.NonPublic);
        private static readonly Reflection.FieldInfo GameMenuSystemField =
            typeof(GameStateGame).GetField(
                "mMenuSystem",
                Reflection.BindingFlags.Instance | Reflection.BindingFlags.NonPublic);

        public static bool Applying { get { return sApplying; } }

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
            sApplying = false;
        }

        public static void QueuePacket(byte[] packet)
        {
            CoopAuthoritativeCommandData command;
            if (!CoopProtocol.TryParseAuthoritativeCommandPacket(packet, out command)) return;
            SyncManager sync = SyncManager.Instance;
            long activeSessionId;
            if (sync == null || !sync.TryGetRunningSessionId(out activeSessionId) ||
                !CoopCommandSessionPolicy.CanAccept(true, activeSessionId, command.SessionId)) return;
            bool expected = SyncManager.Role == "host"
                ? command.Phase == CoopAuthoritativeCommandPhase.Request
                : command.Phase == CoopAuthoritativeCommandPhase.Commit;
            if (!expected || (SyncManager.Role == "host" &&
                !CoopSequencePolicy.IsFromRole(command.Sequence, CoopRole.Guest))) return;
            if (command.Kind == CoopAuthoritativeCommandKind.SetLandingPermissions)
            {
                CoopLandingPermissionsSync.QueueRequest(command);
                return;
            }
            if (command.Kind == CoopAuthoritativeCommandKind.DisasterEvent)
            {
                CoopDisasterSync.QueueCommand(command);
                return;
            }
            if (command.Kind == CoopAuthoritativeCommandKind.Trade)
            {
                CoopTradeSync.QueueCommand(command);
                return;
            }
            if (command.Kind == CoopAuthoritativeCommandKind.VisitorEventAccept)
            {
                CoopShipSync.QueueVisitorEventCommand(command);
                return;
            }
            if (command.Kind == CoopAuthoritativeCommandKind.SetTimeState)
            {
                CoopTimeSync.QueueCommand(command);
                return;
            }
            bool rejected;
            lock (QueueLock)
            {
                rejected = Pending.Count >= QueueCapacity;
                if (!rejected) Pending.Enqueue(command);
            }
            if (rejected)
            {
                CoopPlugin.Log.LogWarning("[Coop] gameplay control queue full; newest command rejected: " +
                    command.Sequence);
                if (SyncManager.Role == "guest")
                    ReportFailure(command, CoopMutationFailureCode.ValidationRejected);
            }
        }

        public static bool TrySubmitSelected(
            CoopAuthoritativeCommandKind kind,
            int value)
        {
            Selectable selected = Selection.getSelected();
            return TrySubmit(kind, selected, string.Empty, value);
        }

        public static bool TrySubmitTarget(
            CoopAuthoritativeCommandKind kind,
            Selectable target,
            int value)
        {
            return TrySubmit(kind, target, string.Empty, value);
        }

        public static bool TrySubmitGlobal(
            CoopAuthoritativeCommandKind kind,
            string key,
            int value)
        {
            return TrySubmit(kind, null, key, value);
        }

        public static bool TrySubmitGrid(Grid grid, int value)
        {
            Construction anchor = CoopGridDeterminism.FindAnchor(grid);
            return anchor != null
                ? TrySubmit(
                    CoopAuthoritativeCommandKind.SetGridEnabled,
                    anchor,
                    string.Empty,
                    value)
                : SyncManager.FullMode && !sApplying;
        }

        public static bool ShouldAllowAmountMutation()
        {
            if (!SyncManager.FullMode || sApplying) return true;
            SyncManager sync = SyncManager.Instance;
            return sync != null && sync.IsSimulationActive() &&
                !sync.ShouldFreezeSim() &&
                CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused);
        }

        public static int CaptureAmountSelectorValue(GuiAmountSelector selector)
        {
            long sessionId;
            if (!SyncManager.FullMode || sApplying || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded ||
                !SyncManager.Instance.TryGetRunningSessionId(out sessionId))
                return int.MinValue;
            RefInt current;
            return TryGetManufactureLimit(selector, out current, out _)
                ? current.get()
                : int.MinValue;
        }

        public static void OnAmountSelectorChanged(
            GuiAmountSelector selector,
            int previousValue)
        {
            if (previousValue == int.MinValue || sApplying) return;
            RefInt current;
            string key;
            if (!TryGetManufactureLimit(selector, out current, out key)) return;
            int requestedValue = current.get();
            if (requestedValue == previousValue) return;
            current.set(previousValue);
            TrySubmitGlobal(
                CoopAuthoritativeCommandKind.SetManufactureLimit,
                key,
                requestedValue);
        }

        private static bool TrySubmit(
            CoopAuthoritativeCommandKind kind,
            Selectable target,
            string key,
            int value)
        {
            SyncManager sync = SyncManager.Instance;
            long sessionId;
            if (!SyncManager.FullMode || sApplying) return false;
            if (PlacementSync.Instance != null && PlacementSync.Instance.ApplyingAuthoritative)
                return false;
            if (sync == null || !sync.IsSimulationActive() || sync.ShouldFreezeSim() ||
                !sync.TryGetRunningSessionId(out sessionId) ||
                !CoopInputPolicy.IsInputAllowed(true, CoopFocusState.IsFocused)) return true;

            CoopGameplayControlData control = new CoopGameplayControlData(
                target == null ? null : CoopObjectBridge.Capture(target, true),
                key,
                value);
            byte[] payload;
            try { payload = CoopGameplayControlCodec.Encode(kind, control); }
            catch (ArgumentException) { return true; }

            long local = Interlocked.Increment(ref sLocalSequence);
            long sequence = CoopPlacementRequestIds.For(
                SyncManager.Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                local);
            CoopAuthoritativeCommandData request = new CoopAuthoritativeCommandData(
                sessionId,
                sequence,
                0,
                kind,
                CoopAuthoritativeCommandPhase.Request,
                payload);
            if (SyncManager.Role == "guest")
            {
                if (!sync.TrySendPacket(CoopProtocol.CreateAuthoritativeCommandPacket(request)))
                    sync.ReportMutationFailure(
                        CoopMutationKind.GameplayControl,
                        sequence,
                        CoopMutationFailureCode.ApplyFailed);
            }
            else
            {
                HandleHostRequest(request, Interlocked.Read(ref sync.TickIndex), false);
            }
            return true;
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
                    if (Scheduled[i].ApplyTick < currentTick)
                    {
                        ReportFailure(Scheduled[i], CoopMutationFailureCode.ApplyFailed);
                        Scheduled.RemoveAt(i);
                        late = true;
                        continue;
                    }
                    if (Scheduled[i].ApplyTick == currentTick)
                    {
                        due.Add(Scheduled[i]);
                        Scheduled.RemoveAt(i);
                    }
                }
            }
            if (late) return false;

            for (int i = due.Count - 1; i >= 0; i--)
            {
                if (!Apply(due[i]))
                {
                    ReportFailure(due[i], CoopMutationFailureCode.ApplyFailed);
                    return false;
                }
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
            CoopGameplayControlData requested;
            CoopGameplayControlData authoritative;
            long applyLeadTicks = CoopCommandLeadPolicy.ApplyLeadTicks(
                CoopTimeSync.CurrentScaleIndex,
                CoopNetworkTiming.CurrentBudgetMilliseconds);
            if (request.Phase != CoopAuthoritativeCommandPhase.Request ||
                !CoopGameplayControlCodec.TryDecode(request.Kind, request.Payload, out requested) ||
                !TryNormalize(request.Kind, requested, out authoritative) ||
                currentTick > CoopProtocol.MaxWorldTick - applyLeadTicks)
            {
                CoopPlugin.Log.LogWarning("[Coop] rejected invalid gameplay control request: seq=" +
                    request.Sequence + " source=" + (fromGuestNetwork ? "guest" : "host"));
                return true;
            }
            CoopAuthoritativeCommandData commit = new CoopAuthoritativeCommandData(
                request.SessionId,
                request.Sequence,
                currentTick + applyLeadTicks,
                request.Kind,
                CoopAuthoritativeCommandPhase.Commit,
                CoopGameplayControlCodec.Encode(request.Kind, authoritative));
            if (!Schedule(commit))
            {
                CoopPlugin.Log.LogWarning("[Coop] rejected duplicate/overflowing gameplay control: seq=" +
                    commit.Sequence);
                return true;
            }
            if (!SyncManager.Instance.TrySendPacket(
                    CoopProtocol.CreateAuthoritativeCommandPacket(commit)))
            {
                RemoveScheduled(commit.Sequence);
                ReportFailure(commit, CoopMutationFailureCode.ApplyFailed);
                return false;
            }
            CoopPlugin.Log.LogInfo("[Coop] gameplay control scheduled: kind=" +
                commit.Kind + " seq=" + commit.Sequence + " tick=" + commit.ApplyTick);
            return true;
        }

        private static bool TryNormalize(
            CoopAuthoritativeCommandKind kind,
            CoopGameplayControlData requested,
            out CoopGameplayControlData authoritative)
        {
            authoritative = requested;
            if (requested.Target == null) return true;
            CoopObjectResolution resolution;
            Selectable target = CoopObjectBridge.ResolveSelectable(requested.Target, out resolution);
            if (target == null) return false;
            if (kind == CoopAuthoritativeCommandKind.SetEnabled && !(target is Buildable)) return false;
            if ((kind == CoopAuthoritativeCommandKind.SetLocked ||
                 kind == CoopAuthoritativeCommandKind.SetHighPriority) &&
                !(target is Construction)) return false;
            if (kind == CoopAuthoritativeCommandKind.SetProducedItem &&
                !(target is ConstructionComponent)) return false;
            if (kind == CoopAuthoritativeCommandKind.SetGridEnabled &&
                CoopGridDeterminism.ResolveFromAnchor(target) == null) return false;
            if (!IsTargetActionAllowed(kind, target)) return false;
            if (kind == CoopAuthoritativeCommandKind.SetGridEnabled)
            {
                Construction anchor = CoopGridDeterminism.FindAnchor(
                    CoopGridDeterminism.ResolveFromAnchor(target));
                if (anchor == null) return false;
                target = anchor;
            }
            authoritative = new CoopGameplayControlData(
                CoopObjectBridge.Capture(target, true),
                requested.Key,
                requested.Value);
            return true;
        }

        private static bool IsTargetActionAllowed(
            CoopAuthoritativeCommandKind kind,
            Selectable target)
        {
            if (kind == CoopAuthoritativeCommandKind.SetEnabled)
            {
                Buildable buildable = target as Buildable;
                return buildable != null && buildable.canBeDisabled() &&
                    target.isActionMenuEnabled();
            }
            if (kind == CoopAuthoritativeCommandKind.SetLocked)
            {
                bool ignored;
                return target != null && target.isLockable(out ignored) &&
                    target.isActionMenuEnabled();
            }
            if (kind == CoopAuthoritativeCommandKind.SetHighPriority)
            {
                Buildable buildable = target as Buildable;
                bool ignored;
                return buildable != null &&
                    buildable.hasPriorityControls(out ignored);
            }
            if (kind == CoopAuthoritativeCommandKind.SetProducedItem)
            {
                ConstructionComponent component = target as ConstructionComponent;
                return component != null && component.getComponentType() != null &&
                    component.getComponentType().getProduction() != null;
            }
            return kind == CoopAuthoritativeCommandKind.SetGridEnabled;
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

        private static bool Apply(CoopAuthoritativeCommandData command)
        {
            CoopGameplayControlData control;
            if (!CoopGameplayControlCodec.TryDecode(command.Kind, command.Payload, out control))
                return false;
            sApplying = true;
            try
            {
                Selectable target = null;
                if (control.Target != null)
                {
                    CoopObjectResolution resolution;
                    target = CoopObjectBridge.ResolveSelectable(control.Target, out resolution);
                    if (target == null) return false;
                }
                switch (command.Kind)
                {
                    case CoopAuthoritativeCommandKind.SetEnabled:
                        Buildable buildable = target as Buildable;
                        if (buildable == null) return false;
                        buildable.setEnabled(control.Value != 0);
                        break;
                    case CoopAuthoritativeCommandKind.SetLocked:
                        Construction construction = target as Construction;
                        if (construction == null) return false;
                        construction.setLocked(control.Value != 0);
                        break;
                    case CoopAuthoritativeCommandKind.SetHighPriority:
                        Construction priorityConstruction = target as Construction;
                        if (priorityConstruction == null) return false;
                        priorityConstruction.setHighPriority(control.Value != 0);
                        break;
                    case CoopAuthoritativeCommandKind.SetProducedItem:
                        ConstructionComponent component = target as ConstructionComponent;
                        if (component == null || component.getComponentType() == null ||
                            component.getComponentType().getProduction() == null ||
                            control.Value >= component.getComponentType().getProduction().Count)
                            return false;
                        component.setProducedItemIndex(control.Value);
                        break;
                    case CoopAuthoritativeCommandKind.SetAlertState:
                        Singleton<SecurityManager>.getInstance().setAlertState(
                            (AlertState)control.Value);
                        break;
                    case CoopAuthoritativeCommandKind.SetGridEnabled:
                        Grid grid = CoopGridDeterminism.ResolveFromAnchor(target);
                        if (grid == null) return false;
                        if (control.Value != 0) grid.enableAllConstructions();
                        else grid.disableAllNolVitalConstructions();
                        break;
                    case CoopAuthoritativeCommandKind.SetManufactureLimit:
                        if (!ApplyManufactureLimit(control.Key, control.Value)) return false;
                        break;
                    default:
                        return false;
                }
                RefreshLocalActionMenu(target);
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] gameplay control apply failed: " + ex.Message);
                return false;
            }
            finally
            {
                sApplying = false;
            }
        }

        private static void RefreshLocalActionMenu(Selectable target)
        {
            if (target == null || !ReferenceEquals(Selection.getSelected(), target) ||
                GameMenuSystemField == null) return;
            try
            {
                GameStateGame gameState =
                    GameManager.getInstance().getGameState() as GameStateGame;
                GuiMenuSystem menu = gameState == null
                    ? null
                    : GameMenuSystemField.GetValue(gameState) as GuiMenuSystem;
                menu?.setActionMenu();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning(
                    "[Coop] action menu refresh failed: " + ex.Message);
            }
        }

        private static void ReportFailure(
            CoopAuthoritativeCommandData command,
            CoopMutationFailureCode code)
        {
            if (command == null) return;
            SyncManager.Instance?.ReportMutationFailure(
                CoopMutationKind.GameplayControl,
                command.Sequence,
                code);
        }

        private static bool TryGetManufactureLimit(
            GuiAmountSelector selector,
            out RefInt current,
            out string key)
        {
            current = null;
            key = string.Empty;
            if (selector == null || AmountCurrentField == null) return false;
            try { current = AmountCurrentField.GetValue(selector) as RefInt; }
            catch (Exception) { return false; }
            if (current == null) return false;
            ManufactureLimits limits = Singleton<ManufactureLimits>.getInstance();
            if (limits == null) return false;
            List<ResourceType> resources = TypeList<ResourceType, ResourceTypeList>.get();
            for (int i = 0; i < resources.Count; i++)
            {
                ResourceType resource = resources[i];
                if (resource != null && resource.hasFlag(64) &&
                    ReferenceEquals(current, limits.getResourceLimit(resource)))
                {
                    key = "Resource_" + resource.GetType().Name;
                    return true;
                }
            }
            List<Specialization> bots = SpecializationList.getBotSpecializations();
            for (int i = 0; i < bots.Count; i++)
            {
                Specialization bot = bots[i];
                if (bot != null && ReferenceEquals(current, limits.getBotLimit(bot)))
                {
                    key = "Bot_" + bot.GetType().Name;
                    return true;
                }
            }
            return false;
        }

        private static bool ApplyManufactureLimit(string key, int value)
        {
            ManufactureLimits limits = Singleton<ManufactureLimits>.getInstance();
            if (limits == null || string.IsNullOrEmpty(key)) return false;
            const string resourcePrefix = "Resource_";
            const string botPrefix = "Bot_";
            if (key.StartsWith(resourcePrefix, StringComparison.Ordinal))
            {
                string typeName = key.Substring(resourcePrefix.Length);
                List<ResourceType> resources = TypeList<ResourceType, ResourceTypeList>.get();
                for (int i = 0; i < resources.Count; i++)
                {
                    ResourceType resource = resources[i];
                    if (resource != null && StringComparer.Ordinal.Equals(
                            resource.GetType().Name, typeName))
                    {
                        limits.getResourceLimit(resource).set(value);
                        return true;
                    }
                }
                return false;
            }
            if (!key.StartsWith(botPrefix, StringComparison.Ordinal)) return false;
            string botTypeName = key.Substring(botPrefix.Length);
            List<Specialization> bots = SpecializationList.getBotSpecializations();
            for (int i = 0; i < bots.Count; i++)
            {
                Specialization bot = bots[i];
                if (bot != null && StringComparer.Ordinal.Equals(
                        bot.GetType().Name, botTypeName))
                {
                    limits.getBotLimit(bot).set(value);
                    return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(GuiMenuSystem), "onButtonDisable")]
    internal static class CoopDisableControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitSelected(
                CoopAuthoritativeCommandKind.SetEnabled, 0);
        }
    }

    [HarmonyPatch(typeof(GuiMenuSystem), "onButtonEnable")]
    internal static class CoopEnableControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitSelected(
                CoopAuthoritativeCommandKind.SetEnabled, 1);
        }
    }

    [HarmonyPatch(typeof(GuiMenuSystem), "onButtonLock")]
    internal static class CoopLockControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitSelected(
                CoopAuthoritativeCommandKind.SetLocked, 1);
        }
    }

    [HarmonyPatch(typeof(GuiMenuSystem), "onButtonUnlock")]
    internal static class CoopUnlockControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitSelected(
                CoopAuthoritativeCommandKind.SetLocked, 0);
        }
    }

    [HarmonyPatch(typeof(GuiMenuSystem), "onButtonPriorityUp")]
    internal static class CoopPriorityUpControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitSelected(
                CoopAuthoritativeCommandKind.SetHighPriority, 1);
        }
    }

    [HarmonyPatch(typeof(GuiMenuSystem), "onButtonPriorityDown")]
    internal static class CoopPriorityDownControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitSelected(
                CoopAuthoritativeCommandKind.SetHighPriority, 0);
        }
    }

    [HarmonyPatch(typeof(ConstructionComponent), "setProducedItemIndex")]
    internal static class CoopProducedItemControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ConstructionComponent __instance, int index)
        {
            if (CoopGameplayControlSync.Applying) return true;
            return !CoopGameplayControlSync.TrySubmitTarget(
                CoopAuthoritativeCommandKind.SetProducedItem,
                __instance,
                index);
        }
    }

    [HarmonyPatch(typeof(GameStateGame), "onAlert")]
    internal static class CoopShortcutAlertControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(AlertState state)
        {
            return !CoopGameplayControlSync.TrySubmitGlobal(
                CoopAuthoritativeCommandKind.SetAlertState,
                string.Empty,
                (int)state);
        }
    }

    [HarmonyPatch(typeof(GuiSecurityWindow), "onNoAlert")]
    internal static class CoopNoAlertControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitGlobal(
                CoopAuthoritativeCommandKind.SetAlertState, string.Empty, (int)AlertState.NoAlert);
        }
    }

    [HarmonyPatch(typeof(GuiSecurityWindow), "onYellowAlert")]
    internal static class CoopYellowAlertControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitGlobal(
                CoopAuthoritativeCommandKind.SetAlertState, string.Empty, (int)AlertState.YellowAlert);
        }
    }

    [HarmonyPatch(typeof(GuiSecurityWindow), "onRedAlert")]
    internal static class CoopRedAlertControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CoopGameplayControlSync.TrySubmitGlobal(
                CoopAuthoritativeCommandKind.SetAlertState, string.Empty, (int)AlertState.RedAlert);
        }
    }

    [HarmonyPatch(typeof(GuiGridWindow), "onDisable")]
    internal static class CoopGridDisableControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(object parameter)
        {
            return !CoopGameplayControlSync.TrySubmitGrid(parameter as Grid, 0);
        }
    }

    [HarmonyPatch(typeof(GuiGridWindow), "onEnable")]
    internal static class CoopGridEnableControlPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(object parameter)
        {
            return !CoopGameplayControlSync.TrySubmitGrid(parameter as Grid, 1);
        }
    }

    [HarmonyPatch(typeof(Grid), "calculateBalance", new Type[] { })]
    internal static class CoopStableGridOrderPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Grid __instance)
        {
            if (SyncManager.FullMode)
                CoopGridDeterminism.Normalize(__instance);
        }
    }

    // Vanilla 2 sn sayaci ilk istekle baslar. Yerel bir istek (or. insa onizlemesi)
    // bir peer'in fazini kaydirir; balance farkli tick'lerde degisir, batarya sapar
    // (canli: timer 1.34/0.06, balance 11200/13200). Co-op'ta ortak tick belirler.
    [HarmonyPatch(typeof(Grid), "update")]
    internal static class CoopDeterministicGridBalancePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Grid __instance)
        {
            SyncManager sync = SyncManager.Instance;
            if (!SyncManager.FullMode || sync == null) return true;
            if (CoopGridBalancePolicy.ShouldRecalculate(sync.TickIndex))
                __instance.calculateBalance();
            return false;
        }
    }

    [HarmonyPatch(typeof(GuiAmountSelector), "onMinus")]
    internal static class CoopManufactureMinusPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GuiAmountSelector __instance, out int __state)
        {
            __state = CoopGameplayControlSync.CaptureAmountSelectorValue(__instance);
            return CoopGameplayControlSync.ShouldAllowAmountMutation();
        }

        [HarmonyPostfix]
        private static void Postfix(GuiAmountSelector __instance, int __state)
        {
            CoopGameplayControlSync.OnAmountSelectorChanged(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(GuiAmountSelector), "onPlus")]
    internal static class CoopManufacturePlusPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GuiAmountSelector __instance, out int __state)
        {
            __state = CoopGameplayControlSync.CaptureAmountSelectorValue(__instance);
            return CoopGameplayControlSync.ShouldAllowAmountMutation();
        }

        [HarmonyPostfix]
        private static void Postfix(GuiAmountSelector __instance, int __state)
        {
            CoopGameplayControlSync.OnAmountSelectorChanged(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(GuiAmountSelector), "setText")]
    internal static class CoopManufactureTextPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GuiAmountSelector __instance, out int __state)
        {
            __state = CoopGameplayControlSync.CaptureAmountSelectorValue(__instance);
            return CoopGameplayControlSync.ShouldAllowAmountMutation();
        }

        [HarmonyPostfix]
        private static void Postfix(GuiAmountSelector __instance, int __state)
        {
            CoopGameplayControlSync.OnAmountSelectorChanged(__instance, __state);
        }
    }
}
