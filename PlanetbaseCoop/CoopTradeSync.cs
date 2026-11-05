using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Planetbase;

namespace PlanetbaseCoop
{
    public static class CoopTradeSync
    {
        private const int QueueCapacity = 64;
        private static readonly object QueueLock = new object();
        private static readonly Queue<CoopAuthoritativeCommandData> Pending =
            new Queue<CoopAuthoritativeCommandData>();
        private static readonly List<CoopAuthoritativeCommandData> Scheduled =
            new List<CoopAuthoritativeCommandData>();
        private static readonly CoopSequenceWatermarks Sequences =
            new CoopSequenceWatermarks();
        private static readonly HashSet<int> PendingAgreements = new HashSet<int>();
        private static long sLocalSequence;
        private static bool sApplying;

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
                Sequences.Reset();
                PendingAgreements.Clear();
                sLocalSequence = 0;
            }
            sApplying = false;
        }

        public static void QueueCommand(CoopAuthoritativeCommandData command)
        {
            SyncManager sync = SyncManager.Instance;
            long activeSessionId;
            CoopTradeCommandData ignored;
            if (command == null || command.Kind != CoopAuthoritativeCommandKind.Trade ||
                sync == null || !sync.TryGetRunningSessionId(out activeSessionId) ||
                !CoopCommandSessionPolicy.CanAccept(true, activeSessionId, command.SessionId) ||
                !CoopTradeCommandCodec.TryDecode(command.Payload, out ignored)) return;
            bool expected = SyncManager.Role == "host"
                ? command.Phase == CoopAuthoritativeCommandPhase.Request
                : command.Phase == CoopAuthoritativeCommandPhase.Commit;
            if (!expected) return;
            bool rejected;
            lock (QueueLock)
            {
                rejected = Pending.Count >= QueueCapacity;
                if (!rejected) Pending.Enqueue(command);
            }
            if (rejected)
            {
                CoopPlugin.Log.LogWarning("[Coop] trade queue full; command rejected: " +
                    command.Sequence);
                if (SyncManager.Role == "guest")
                    ReportFailure(command, CoopMutationFailureCode.ValidationRejected);
            }
        }

        public static bool InterceptLifecycle(
            MerchantShip ship,
            CoopTradeAction action)
        {
            return TrySubmit(ship, action, null, null);
        }

        public static bool InterceptAgreement(
            MerchantShip ship,
            ProductAmounts neededProducts,
            ProductAmounts providedProducts)
        {
            return TrySubmit(
                ship,
                CoopTradeAction.Agree,
                neededProducts,
                providedProducts);
        }

        public static bool BeforeSimulationTick(long currentTick)
        {
            if (!SyncManager.FullMode || SyncManager.Instance == null ||
                !SyncManager.Instance.GameLoaded) return true;
            while (true)
            {
                CoopAuthoritativeCommandData command = null;
                lock (QueueLock)
                {
                    if (Pending.Count > 0) command = Pending.Dequeue();
                }
                if (command == null) break;
                if (SyncManager.Role == "host")
                {
                    if (!HandleHostRequest(command, currentTick)) return false;
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

            List<CoopAuthoritativeCommandData> due =
                new List<CoopAuthoritativeCommandData>();
            lock (QueueLock)
            {
                for (int i = Scheduled.Count - 1; i >= 0; i--)
                {
                    if (Scheduled[i].ApplyTick < currentTick)
                    {
                        CoopAuthoritativeCommandData late = Scheduled[i];
                        Scheduled.RemoveAt(i);
                        ReportFailure(late, CoopMutationFailureCode.ApplyFailed);
                        return false;
                    }
                    if (Scheduled[i].ApplyTick == currentTick)
                    {
                        due.Add(Scheduled[i]);
                        Scheduled.RemoveAt(i);
                    }
                }
            }
            due.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            for (int i = 0; i < due.Count; i++)
            {
                if (!Apply(due[i]))
                {
                    ReportFailure(due[i], CoopMutationFailureCode.ApplyFailed);
                    return false;
                }
            }
            return true;
        }

        private static bool TrySubmit(
            MerchantShip ship,
            CoopTradeAction action,
            ProductAmounts neededProducts,
            ProductAmounts providedProducts)
        {
            if (!SyncManager.FullMode || sApplying) return false;
            if (SyncManager.Instance == null ||
                !SyncManager.Instance.IsSimulationActive() ||
                SyncManager.Instance.ShouldFreezeSim()) return true;
            long sessionId;
            if (ship == null || !SyncManager.Instance.TryGetRunningSessionId(out sessionId))
                return true;

            CoopTradeProductData[] needed = action == CoopTradeAction.Agree
                ? CaptureProducts(neededProducts)
                : new CoopTradeProductData[0];
            CoopTradeProductData[] provided = action == CoopTradeAction.Agree
                ? CaptureProducts(providedProducts)
                : new CoopTradeProductData[0];
            if (action == CoopTradeAction.Agree)
                UnmarkProducts(ship, needed);

            CoopTradeCommandData data = new CoopTradeCommandData(
                action,
                CoopObjectBridge.Capture(ship, true),
                needed,
                provided);
            byte[] payload;
            try { payload = CoopTradeCommandCodec.Encode(data); }
            catch (ArgumentException ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] trade request rejected locally: " + ex.Message);
                return true;
            }

            long local = Interlocked.Increment(ref sLocalSequence);
            long sequence = CoopPlacementRequestIds.For(
                SyncManager.Role == "guest" ? CoopRole.Guest : CoopRole.Host,
                local);
            CoopAuthoritativeCommandData request = new CoopAuthoritativeCommandData(
                sessionId,
                sequence,
                0,
                CoopAuthoritativeCommandKind.Trade,
                CoopAuthoritativeCommandPhase.Request,
                payload);
            if (SyncManager.Role == "guest")
            {
                if (!SyncManager.Instance.TrySendPacket(
                        CoopProtocol.CreateAuthoritativeCommandPacket(request)))
                {
                    CoopStatusOverlay.Notify("TICARET ISTEGI GONDERILEMEDI");
                }
            }
            else
            {
                HandleHostRequest(
                    request,
                    Interlocked.Read(ref SyncManager.Instance.TickIndex));
            }
            return true;
        }

        private static bool HandleHostRequest(
            CoopAuthoritativeCommandData request,
            long currentTick)
        {
            CoopTradeCommandData requested;
            MerchantShip ship;
            CoopTradeCommandData authoritative;
            long applyLeadTicks = CoopCommandLeadPolicy.ApplyLeadTicks(
                CoopTimeSync.CurrentScaleIndex,
                CoopNetworkTiming.CurrentBudgetMilliseconds);
            if (request.Phase != CoopAuthoritativeCommandPhase.Request ||
                currentTick > CoopProtocol.MaxWorldTick - applyLeadTicks ||
                !CoopTradeCommandCodec.TryDecode(request.Payload, out requested) ||
                !TryNormalizeAndValidate(requested, out ship, out authoritative))
            {
                CoopPlugin.Log.LogWarning("[Coop] invalid trade request rejected: " +
                    request.Sequence);
                return true;
            }

            if (authoritative.Action == CoopTradeAction.Agree)
            {
                lock (QueueLock)
                {
                    if (!PendingAgreements.Add(ship.getId()))
                    {
                        CoopPlugin.Log.LogWarning("[Coop] duplicate pending trade agreement rejected");
                        return true;
                    }
                }
            }
            bool ignoredStart;
            bool ignoredTerminal;
            long latestLifecycleTick;
            GetPendingLifecycle(
                ship.getId(),
                out ignoredStart,
                out ignoredTerminal,
                out latestLifecycleTick);
            long applyTick = CoopTradeLifecyclePolicy.GetApplyTick(
                currentTick,
                applyLeadTicks,
                authoritative.Action == CoopTradeAction.Start
                    ? 0
                    : latestLifecycleTick);
            if (applyTick <= 0)
            {
                ReleasePendingAgreement(authoritative);
                return true;
            }
            CoopAuthoritativeCommandData commit = new CoopAuthoritativeCommandData(
                request.SessionId,
                request.Sequence,
                applyTick,
                CoopAuthoritativeCommandKind.Trade,
                CoopAuthoritativeCommandPhase.Commit,
                CoopTradeCommandCodec.Encode(authoritative));
            if (!Schedule(commit))
            {
                ReleasePendingAgreement(authoritative);
                return true;
            }
            if (!SyncManager.Instance.TrySendPacket(
                    CoopProtocol.CreateAuthoritativeCommandPacket(commit)))
            {
                RemoveScheduled(commit.Sequence);
                ReleasePendingAgreement(authoritative);
                ReportFailure(commit, CoopMutationFailureCode.ApplyFailed);
                return false;
            }
            return true;
        }

        private static bool TryNormalizeAndValidate(
            CoopTradeCommandData requested,
            out MerchantShip ship,
            out CoopTradeCommandData authoritative)
        {
            ship = null;
            authoritative = null;
            CoopObjectResolution resolution;
            ship = CoopObjectBridge.ResolveSelectable(
                requested.ShipReference,
                out resolution) as MerchantShip;
            if (ship == null) return false;

            bool hasPendingStart;
            bool hasPendingTerminal;
            long ignoredLifecycleTick;
            GetPendingLifecycle(
                ship.getId(),
                out hasPendingStart,
                out hasPendingTerminal,
                out ignoredLifecycleTick);
            bool hasPendingAgreement;
            lock (QueueLock)
                hasPendingAgreement = PendingAgreements.Contains(ship.getId());
            if (!CoopTradeLifecyclePolicy.CanAccept(
                    requested.Action,
                    ship.isTrading(),
                    hasPendingStart,
                    hasPendingTerminal,
                    hasPendingAgreement)) return false;

            CoopTradeProductData[] needed = requested.NeededProducts;
            CoopTradeProductData[] provided = requested.ProvidedProducts;
            if (requested.Action == CoopTradeAction.Start &&
                !ship.canTrade()) return false;
            if (requested.Action == CoopTradeAction.Agree &&
                !ValidateAgreement(ship, needed, provided))
                return false;
            authoritative = new CoopTradeCommandData(
                requested.Action,
                CoopObjectBridge.Capture(ship, true),
                needed,
                provided);
            return true;
        }

        private static void GetPendingLifecycle(
            int shipId,
            out bool hasPendingStart,
            out bool hasPendingTerminal,
            out long latestApplyTick)
        {
            hasPendingStart = false;
            hasPendingTerminal = false;
            latestApplyTick = 0;
            lock (QueueLock)
            {
                for (int i = 0; i < Scheduled.Count; i++)
                {
                    CoopTradeCommandData pending;
                    if (!CoopTradeCommandCodec.TryDecode(
                            Scheduled[i].Payload, out pending) ||
                        pending.ShipReference.ObjectId != shipId) continue;
                    if (Scheduled[i].ApplyTick > latestApplyTick)
                        latestApplyTick = Scheduled[i].ApplyTick;
                    if (pending.Action == CoopTradeAction.Start)
                        hasPendingStart = true;
                    else if (pending.Action == CoopTradeAction.Cancel ||
                             pending.Action == CoopTradeAction.Dismiss)
                        hasPendingTerminal = true;
                }
            }
        }

        private static bool ValidateAgreement(
            MerchantShip ship,
            CoopTradeProductData[] needed,
            CoopTradeProductData[] provided)
        {
            try
            {
                ResourceAmounts available = Resource.calculateTradeableCounts();
                ProductAmounts stock = ship.getProductAmounts();
                if (available == null || stock == null) return false;
                int neededValue = 0;
                int providedValue = 0;
                int neededCargo = 0;
                int providedCargo = 0;
                for (int i = 0; i < needed.Length; i++)
                {
                    Product product = CreateProduct(needed[i]);
                    ProductResource resource = product as ProductResource;
                    if (resource == null ||
                        available.getAmount(resource.getResourceType()) < needed[i].Amount)
                        return false;
                    neededValue = checked(neededValue + product.getValue() * needed[i].Amount);
                    neededCargo = checked(neededCargo + product.getCargoSpace() * needed[i].Amount);
                }
                for (int i = 0; i < provided.Length; i++)
                {
                    Product product = CreateProduct(provided[i]);
                    if (product == null || stock.getProductCount(product) < provided[i].Amount)
                        return false;
                    providedValue = checked(providedValue + product.getValue() * provided[i].Amount);
                    providedCargo = checked(providedCargo + product.getCargoSpace() * provided[i].Amount);
                }
                int chargedValue = checked((100 + ship.getCommission()) * providedValue / 100);
                int cargoAfterTrade = checked(stock.getCargoSpace() - providedCargo + neededCargo);
                return neededValue > 0 && providedValue > 0 && neededValue >= chargedValue &&
                    cargoAfterTrade <= ship.getMaxCargoSpace();
            }
            catch (OverflowException) { return false; }
        }

        private static bool Schedule(CoopAuthoritativeCommandData command)
        {
            lock (QueueLock)
            {
                if (Scheduled.Count >= QueueCapacity ||
                    !Sequences.TryAccept(command.Sequence)) return false;
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
            CoopTradeCommandData data;
            if (!CoopTradeCommandCodec.TryDecode(command.Payload, out data)) return false;
            CoopObjectResolution resolution;
            MerchantShip ship = CoopObjectBridge.ResolveSelectable(
                data.ShipReference,
                out resolution) as MerchantShip;
            if (ship == null) return false;
            sApplying = true;
            try
            {
                switch (data.Action)
                {
                    case CoopTradeAction.Start:
                        if (!ship.canTrade()) return false;
                        ship.startTrading();
                        break;
                    case CoopTradeAction.Cancel:
                        ship.cancelTrading();
                        break;
                    case CoopTradeAction.Dismiss:
                        ship.dismiss();
                        break;
                    case CoopTradeAction.Agree:
                        if (!ship.isTrading()) return false;
                        if (!ApplyAgreement(ship, data.NeededProducts, data.ProvidedProducts))
                            return false;
                        break;
                    default:
                        return false;
                }
                CoopPlugin.Log.LogInfo("[Coop] authoritative trade applied: action=" +
                    data.Action + " seq=" + command.Sequence);
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] trade apply failed: " + ex.Message);
                return false;
            }
            finally
            {
                ReleasePendingAgreement(data);
                sApplying = false;
            }
        }


        private static bool ApplyAgreement(
            MerchantShip ship,
            CoopTradeProductData[] needed,
            CoopTradeProductData[] provided)
        {
            ProductAmounts neededAmounts = CreateAmounts(needed);
            ProductAmounts providedAmounts = CreateAmounts(provided);
            if (neededAmounts == null || providedAmounts == null) return false;
            List<Product> marked = new List<Product>();
            for (int i = 0; i < needed.Length; i++)
            {
                Product product = CreateProduct(needed[i]);
                if (product == null)
                {
                    RollbackMarks(ship, marked);
                    return false;
                }
                for (int amount = 0; amount < needed[i].Amount; amount++)
                {
                    if (!product.markForTrading(ship))
                    {
                        RollbackMarks(ship, marked);
                        return false;
                    }
                    marked.Add(product);
                }
            }
            ship.onTradeAgreed(neededAmounts, providedAmounts);
            return true;
        }

        private static ProductAmounts CreateAmounts(CoopTradeProductData[] products)
        {
            ProductAmounts amounts = new ProductAmounts();
            for (int i = 0; i < products.Length; i++)
            {
                Product product = CreateProduct(products[i]);
                if (product == null) return null;
                amounts.add(product, products[i].Amount);
            }
            return amounts;
        }

        private static Product CreateProduct(CoopTradeProductData data)
        {
            if (data == null) return null;
            if (data.Kind == CoopTradeProductKind.Resource)
            {
                ResourceType type = TypeList<ResourceType, ResourceTypeList>.find(data.ProductId);
                return type == null ? null : new ProductResource(type);
            }
            if (data.Kind == CoopTradeProductKind.Bot)
            {
                Specialization specialization =
                    TypeList<Specialization, SpecializationList>.find(data.ProductId);
                if (specialization == null ||
                    !SpecializationList.getBotSpecializations().Contains(specialization)) return null;
                return new ProductBot(specialization);
            }
            if (data.Kind == CoopTradeProductKind.Tech)
            {
                Tech tech = TypeList<Tech, TechList>.find(data.ProductId);
                return tech == null ? null : new ProductTech(tech);
            }
            return null;
        }

        private static CoopTradeProductData[] CaptureProducts(ProductAmounts amounts)
        {
            if (amounts == null) return new CoopTradeProductData[0];
            List<CoopTradeProductData> products = new List<CoopTradeProductData>();
            foreach (ProductAmount amount in amounts)
            {
                Product product = amount.getProduct();
                CoopTradeProductKind kind;
                if (product is ProductResource) kind = CoopTradeProductKind.Resource;
                else if (product is ProductBot) kind = CoopTradeProductKind.Bot;
                else if (product is ProductTech) kind = CoopTradeProductKind.Tech;
                else continue;
                products.Add(new CoopTradeProductData(
                    kind,
                    product.getId(),
                    amount.getAmount()));
            }
            products.Sort((left, right) =>
            {
                int kind = left.Kind.CompareTo(right.Kind);
                return kind != 0
                    ? kind
                    : StringComparer.Ordinal.Compare(left.ProductId, right.ProductId);
            });
            return products.ToArray();
        }

        private static void UnmarkProducts(
            MerchantShip ship,
            CoopTradeProductData[] products)
        {
            for (int i = 0; i < products.Length; i++)
            {
                Product product = CreateProduct(products[i]);
                if (product == null) continue;
                for (int amount = 0; amount < products[i].Amount; amount++)
                    product.unmarkForTrading(ship.getId());
            }
        }

        private static void RollbackMarks(MerchantShip ship, List<Product> marked)
        {
            for (int i = marked.Count - 1; i >= 0; i--)
                marked[i].unmarkForTrading(ship.getId());
        }

        private static void ReleasePendingAgreement(CoopTradeCommandData data)
        {
            if (data == null || data.Action != CoopTradeAction.Agree ||
                SyncManager.Role != "host") return;
            lock (QueueLock) PendingAgreements.Remove(data.ShipReference.ObjectId);
        }

        private static void ReportFailure(
            CoopAuthoritativeCommandData command,
            CoopMutationFailureCode code)
        {
            if (command == null) return;
            SyncManager.Instance?.ReportMutationFailure(
                CoopMutationKind.Trade,
                command.Sequence,
                code);
        }
    }

    [HarmonyPatch(typeof(ProductResource), "markForTrading")]
    public static class CoopTradeShadowMarkPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!SyncManager.FullMode || CoopTradeSync.Applying) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(ProductResource), "unmarkForTrading")]
    public static class CoopTradeShadowUnmarkPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !SyncManager.FullMode || CoopTradeSync.Applying;
        }
    }

    [HarmonyPatch(typeof(MerchantShip), "startTrading")]
    public static class CoopTradeStartPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MerchantShip __instance)
        {
            return !CoopTradeSync.InterceptLifecycle(__instance, CoopTradeAction.Start);
        }
    }

    [HarmonyPatch(typeof(MerchantShip), "cancelTrading")]
    public static class CoopTradeCancelPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MerchantShip __instance)
        {
            return !CoopTradeSync.InterceptLifecycle(__instance, CoopTradeAction.Cancel);
        }
    }

    [HarmonyPatch(typeof(MerchantShip), "dismiss")]
    public static class CoopTradeDismissPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MerchantShip __instance)
        {
            return !CoopTradeSync.InterceptLifecycle(__instance, CoopTradeAction.Dismiss);
        }
    }

    [HarmonyPatch(typeof(MerchantShip), "onTradeAgreed")]
    public static class CoopTradeAgreementPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(
            MerchantShip __instance,
            ProductAmounts neededProducts,
            ProductAmounts providedProducts)
        {
            return !CoopTradeSync.InterceptAgreement(
                __instance,
                neededProducts,
                providedProducts);
        }
    }
}
