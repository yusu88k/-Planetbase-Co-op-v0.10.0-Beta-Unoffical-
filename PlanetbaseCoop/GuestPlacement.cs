using System;
using System.Collections.Generic;
using System.Reflection;
using Planetbase;
using UnityEngine;
using Module = Planetbase.Module;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Milestone 4 — Misafir inşaat yerleştirme.
    ///
    /// Misafir, B ile inşaat moduna girer (modül), C ile seçili modüle bileşen yerleştirir.
    /// , ve . tuşları tür değiştirir; sol tık yerleştirir, sağ tık iptal eder.
    ///
    /// Yerleştirme önizlemesi misafir kamerasının ışınıyla çalışır: GameStateGame'in
    /// tryPlaceModule/tryPlaceComponent metodları Harmony prefix'iyle yönlendirilir
    /// (Plugin.cs) — misafir inşaatı aktifken ışın misafir kameradan alınır.
    ///
    /// Oyunun özel metodlarına (startPlacingModule, placeModule, cancelModulePlacement,
    /// placeComponent, cancelComponentPlacement) ve özel alanlarına reflection ile
    /// erişilir — değişkenler bir kez cache'lenir.
    /// </summary>
    public static class GuestPlacement
    {
        // --- GameStateGame özel alanları (cache) ---
        private static FieldInfo fActiveModule;
        private static FieldInfo fCurrentModuleSize;
        private static FieldInfo fPlacedModuleType;
        private static FieldInfo fRenderTops;
        private static FieldInfo fCost;
        private static FieldInfo fPlacedComponent;
        private static FieldInfo fPlacedComponentType;
        private static FieldInfo fValidComponentPosition;
        private static FieldInfo fValidComponentRotation;
        private static FieldInfo fMode;
        private static FieldInfo fNextId;
        private static FieldInfo fModuleId;
        private static FieldInfo fComponentId;

        // --- GameStateGame özel metodları (cache) ---
        private static MethodInfo mPlaceModule;
        private static MethodInfo mCancelModulePlacement;
        private static MethodInfo mPlaceComponent;
        private static MethodInfo mCancelComponentPlacement;

        private static bool sInit;
        private static bool sInitOk = true;

        /// <summary>Misafir inşaat modu aktif mi (modül veya bileşen)?</summary>
        public static bool Active;

        private static GameStateGame sGame;
        private static int sTypeIndex;
        private static bool sComponentMode;

        public static bool ComponentMode => sComponentMode;

        public static void ClearNetworkPreview()
        {
            PlacementSync.Instance?.PublishGuestPreview(null);
        }

        public static string CurrentTypeName
        {
            get
            {
                try
                {
                    if (sComponentMode)
                    {
                        List<ComponentType> list = TypeList<ComponentType, ComponentTypeList>.get();
                        return list.Count > 0 ? StringList.get(list[Mathf.Clamp(sTypeIndex, 0, list.Count - 1)].getName()) : "—";
                    }
                    List<ModuleType> mods = TypeList<ModuleType, ModuleTypeList>.get();
                    return mods.Count > 0 ? StringList.get(mods[Mathf.Clamp(sTypeIndex, 0, mods.Count - 1)].getName()) : "—";
                }
                catch (Exception)
                {
                    return "—";
                }
            }
        }

        public static void Init()
        {
            if (sInit)
            {
                return;
            }
            Type t = typeof(GameStateGame);
            fActiveModule = GetField(t, "mActiveModule");
            fCurrentModuleSize = GetField(t, "mCurrentModuleSize");
            fPlacedModuleType = GetField(t, "mPlacedModuleType");
            fRenderTops = GetField(t, "mRenderTops");
            fCost = GetField(t, "mCost");
            fPlacedComponent = GetField(t, "mPlacedComponent");
            fPlacedComponentType = GetField(t, "mPlacedComponentType");
            fValidComponentPosition = GetField(t, "mValidComponentPosition");
            fValidComponentRotation = GetField(t, "mValidComponentRotation");
            fMode = GetField(t, "mMode");
            fNextId = typeof(IdGenerator).GetField("mNextId", BindingFlags.NonPublic | BindingFlags.Instance);
            fModuleId = typeof(Module).GetField("mId", BindingFlags.NonPublic | BindingFlags.Instance);
            fComponentId = typeof(ConstructionComponent).GetField("mId", BindingFlags.NonPublic | BindingFlags.Instance);
            mPlaceModule = GetMethod(t, "placeModule");
            mCancelModulePlacement = GetMethod(t, "cancelModulePlacement");
            mPlaceComponent = GetMethod(t, "placeComponent");
            mCancelComponentPlacement = GetMethod(t, "cancelComponentPlacement");
            // tüm alanlar bulunamazsa inşaat özelliğini güvenle kapat
            sInitOk = fActiveModule != null && fCurrentModuleSize != null && fPlacedModuleType != null &&
                      fRenderTops != null && fCost != null && fPlacedComponent != null &&
                      fPlacedComponentType != null && fValidComponentPosition != null &&
                      fValidComponentRotation != null && fMode != null && mPlaceModule != null &&
                      mCancelModulePlacement != null && mPlaceComponent != null && mCancelComponentPlacement != null &&
                      fNextId != null && fModuleId != null && fComponentId != null;
            if (!sInitOk)
            {
                CoopPlugin.Log.LogWarning("[Coop] inşaat reflection eksik — inşaat özelliği kapalı");
            }
            sInit = true;
        }

        private static FieldInfo GetField(Type t, string name)
        {
            return t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private static MethodInfo GetMethod(Type t, string name)
        {
            return t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private static GameStateGame GetGame()
        {
            try
            {
                GameManager gm = GameManager.getInstance();
                if (gm == null || gm.getGameState() == null)
                {
                    return null;
                }
                if (sGame == null || !sGame.Equals(gm.getGameState()))
                {
                    sGame = gm.getGameState() as GameStateGame;
                }
            }
            catch (Exception)
            {
                sGame = null;
            }
            return sGame;
        }

        private static string ModeName()
        {
            try { return fMode.GetValue(GetGame())?.ToString() ?? "null"; }
            catch (Exception) { return "?"; }
        }

        public static bool TryGetLocalPreview(out CoopPlacementPreviewData preview)
        {
            preview = null;
            if (!sInit || !sInitOk) return false;
            GameStateGame game = GetGame();
            if (game == null) return false;

            try
            {
                string mode = ModeName();
                if (mode == "PlacingModule")
                {
                    Module module = fActiveModule.GetValue(game) as Module;
                    ModuleType moduleType = fPlacedModuleType.GetValue(game) as ModuleType;
                    if (module == null || moduleType == null) return false;
                    Vector3 position = module.getPosition();
                    preview = new CoopPlacementPreviewData
                    {
                        Active = true,
                        Kind = CoopPlacementKind.Module,
                        ParentId = 0,
                        TypeName = moduleType.GetType().Name,
                        SizeIndex = module.getSizeIndex(),
                        PositionX = position.x,
                        PositionY = position.y,
                        PositionZ = position.z,
                        RotationY = module.getTransform().localRotation.eulerAngles.y,
                        Valid = module.isValidPosition()
                    };
                    return true;
                }

                if (mode == "PlacingComponent")
                {
                    Module parent = fActiveModule.GetValue(game) as Module;
                    ConstructionComponent component = fPlacedComponent.GetValue(game) as ConstructionComponent;
                    ComponentType componentType = fPlacedComponentType.GetValue(game) as ComponentType;
                    if (parent == null || component == null || componentType == null) return false;
                    Vector3 position = component.getPosition();
                    preview = new CoopPlacementPreviewData
                    {
                        Active = true,
                        Kind = CoopPlacementKind.Component,
                        ParentId = parent.getId(),
                        ParentReference = CoopObjectBridge.Capture(parent, true),
                        TypeName = componentType.GetType().Name,
                        SizeIndex = 0,
                        PositionX = position.x,
                        PositionY = position.y,
                        PositionZ = position.z,
                        RotationY = component.getTransform().localRotation.eulerAngles.y,
                        Valid = component.isValidPosition()
                    };
                    return true;
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] yerleştirme önizlemesi okunamadı: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Full mode'da vanilla tryPlace* metodu guest tarafinda gercek hologrami
        /// gunceller. Yalnizca ag paketinin local kopyasini yenilemek icin cagrilir.
        /// </summary>
        public static void CaptureVanillaPreview()
        {
            if (!SyncManager.FullMode || SyncManager.Role != "guest") return;
            CoopPlacementPreviewData preview;
            if (TryGetLocalPreview(out preview))
            {
                preview.Source = CoopRole.Guest;
                PlacementSync.Instance?.PublishGuestPreview(preview);
            }
            else
            {
                ClearNetworkPreview();
            }
        }

        /// <summary>
        /// Vanilla preview Module/Component.create ile gecici bir ID alir.
        /// Preview iptal edilene kadar bu ID'yi rezerve etmeden geri yuklemek,
        /// kabul edilen yerlesimin host ve guest'te ayni ID ile yaratilmasini saglar.
        /// </summary>
        public static void RestorePreviewId(int previousNextId)
        {
            if (fNextId == null || previousNextId < 0) return;
            try
            {
                IdGenerator generator = Singleton<IdGenerator>.getInstance();
                if (generator != null) fNextId.SetValue(generator, previousNextId);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] preview ID geri yuklenemedi: " + ex.Message);
            }
        }

        public static int ReadNextId()
        {
            try
            {
                IdGenerator generator = Singleton<IdGenerator>.getInstance();
                return generator != null && fNextId != null ? (int)fNextId.GetValue(generator) : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static void NeutralizeLocalPreviewIds(GameStateGame game)
        {
            if (game == null) return;
            try
            {
                string mode = ModeName();
                if (mode == "PlacingModule")
                {
                    Module module = fActiveModule.GetValue(game) as Module;
                    if (module != null && fModuleId != null) fModuleId.SetValue(module, -1);
                }
                else if (mode == "PlacingComponent")
                {
                    ConstructionComponent component = fPlacedComponent.GetValue(game) as ConstructionComponent;
                    if (component != null && fComponentId != null) fComponentId.SetValue(component, -1);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] preview ID etkisizlestirilemedi: " + ex.Message);
            }
        }

        public static bool TryUpdateGuestNetworkPreview(GameStateGame game, bool componentMode)
        {
            if (!sInit || !sInitOk || game == null || PlacementSync.Instance == null) return false;
            if (!CoopInputPolicy.IsInputAllowed(SyncManager.FullMode, CoopFocusState.IsFocused))
            {
                PlacementSync.Instance.PublishGuestPreview(null);
                return false;
            }
            try
            {
                if (!componentMode)
                {
                    ModuleType type = fPlacedModuleType.GetValue(game) as ModuleType;
                    if (type == null) return false;
                    Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                    RaycastHit hit;
                    if (!Physics.Raycast(ray, out hit, 150f, 256))
                    {
                        PlacementSync.Instance.PublishGuestPreview(null);
                        return false;
                    }
                    float floor = TerrainGenerator.getInstance().getFloorHeight();
                    PlacementSync.Instance.PublishGuestPreview(new CoopPlacementPreviewData
                    {
                        Active = true,
                        Source = CoopRole.Guest,
                        Kind = CoopPlacementKind.Module,
                        ParentId = 0,
                        TypeName = type.GetType().Name,
                        SizeIndex = (int)fCurrentModuleSize.GetValue(game),
                        PositionX = hit.point.x,
                        PositionY = floor,
                        PositionZ = hit.point.z,
                        RotationY = 0f,
                        Valid = true
                    });
                    return true;
                }

                Module parent = fActiveModule.GetValue(game) as Module;
                ComponentType componentType = fPlacedComponentType.GetValue(game) as ComponentType;
                if (parent == null || componentType == null) return false;
                Ray componentRay = Camera.main.ScreenPointToRay(Input.mousePosition);
                RaycastHit componentHit;
                if (!Physics.Raycast(componentRay, out componentHit, 100f, 4096))
                {
                    PlacementSync.Instance.PublishGuestPreview(null);
                    return false;
                }
                PlacementSync.Instance.PublishGuestPreview(new CoopPlacementPreviewData
                {
                    Active = true,
                    Source = CoopRole.Guest,
                    Kind = CoopPlacementKind.Component,
                    ParentId = parent.getId(),
                    ParentReference = CoopObjectBridge.Capture(parent, true),
                    TypeName = componentType.GetType().Name,
                    SizeIndex = 0,
                    PositionX = componentHit.point.x,
                    PositionY = parent.getFloorPosition().y,
                    PositionZ = componentHit.point.z,
                    RotationY = parent.getTransform().localRotation.eulerAngles.y,
                    Valid = true
                });
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] guest ağ önizlemesi üretilemedi: " + ex.Message);
                return false;
            }
        }

        public static bool TryValidateAuthoritativePlacement(CoopPlacementCommandData request, out CoopPlacementCommandData normalized, out string reason)
        {
            if (!CoopPlacementValidator.TryValidate(request, out normalized, out reason)) return false;
            if (!sInit || !sInitOk)
            {
                reason = "placement reflection is unavailable";
                return false;
            }

            try
            {
                if (normalized.Kind == CoopPlacementKind.Module)
                {
                    ModuleType type = FindModuleType(normalized.TypeName);
                    if (type == null)
                    {
                        reason = "unknown module type";
                        return false;
                    }
                    if (normalized.SizeIndex < type.getMinSize() || normalized.SizeIndex > type.getMaxSize())
                    {
                        reason = "module size is not supported";
                        return false;
                    }
                    TerrainGenerator terrain = TerrainGenerator.getInstance();
                    if (terrain == null)
                    {
                        reason = "terrain is unavailable";
                        return false;
                    }
                    float floor = terrain.getFloorHeight();
                    RaycastHit hit;
                    Vector3 origin = new Vector3(normalized.PositionX, floor + 200f, normalized.PositionZ);
                    if (!Physics.Raycast(new Ray(origin, Vector3.down), out hit, 400f, 256))
                    {
                        reason = "module position is outside terrain";
                        return false;
                    }
                    normalized.PositionY = floor;
                    return true;
                }

                CoopObjectResolution parentResolution;
                CoopObjectReference parentReference = normalized.ParentReference ??
                    new CoopObjectReference((int)normalized.ParentId, "Module", 0f, 0f, 0f, false);
                Module parent = CoopObjectBridge.ResolveModule(parentReference, out parentResolution);
                ComponentType componentType = FindComponentType(normalized.TypeName);
                if (parent == null || componentType == null)
                {
                    reason = "component parent or type is unavailable (" + parentResolution + ")";
                    return false;
                }
                normalized.ParentId = parent.getId();
                normalized.ParentReference = CoopObjectBridge.Capture(parent, true);
                normalized.PositionY = parent.getFloorPosition().y;
                return true;
            }
            catch (Exception ex)
            {
                reason = "host placement validation failed: " + ex.Message;
                return false;
            }
        }

        public static bool TryApplyAuthoritativePlacement(CoopPlacementCommandData command)
        {
            GameStateGame game = GetGame();
            if (game == null || command == null) return false;
            Module createdModule = null;
            ConstructionComponent createdComponent = null;
            int previousNextId = ReadNextId();
            bool applied = false;
            bool placementInvoked = false;
            try
            {
                if (previousNextId < 0)
                    throw new InvalidOperationException("authoritative next id is unavailable");
                Vector3 position = new Vector3(command.PositionX, command.PositionY, command.PositionZ);
                if (command.Kind == CoopPlacementKind.Module)
                {
                    ModuleType type = FindModuleType(command.TypeName);
                    if (type == null) return false;
                    createdModule = Module.create(position, command.SizeIndex, type);
                    EnsureAuthoritativeIdAdvance(
                        previousNextId,
                        createdModule == null ? -1 : (int)fModuleId.GetValue(createdModule));
                    createdModule.setRenderTop((bool)fRenderTops.GetValue(game));
                    createdModule.getTransform().localRotation = Quaternion.Euler(0f, command.RotationY, 0f);
                    createdModule.setPosition(position);
                    createdModule.setValidPosition(true);
                    createdModule.setPositionY(
                        Singleton<TerrainGenerator>.getInstance().getFloorHeight());
                    placementInvoked = true;
                    createdModule.onUserPlaced();
                    EnsureAuthoritativeIdAdvance(
                        previousNextId,
                        createdModule == null ? -1 : (int)fModuleId.GetValue(createdModule));
                    applied = true;
                    return true;
                }

                CoopObjectResolution parentResolution;
                CoopObjectReference parentReference = command.ParentReference ??
                    new CoopObjectReference((int)command.ParentId, "Module", 0f, 0f, 0f, false);
                Module parent = CoopObjectBridge.ResolveModule(parentReference, out parentResolution);
                ComponentType componentType = FindComponentType(command.TypeName);
                if (parent == null || componentType == null)
                {
                    CoopPlugin.Log.LogWarning("[Coop] component parent cozumlenemedi: " + parentResolution);
                    return false;
                }
                createdComponent = ConstructionComponent.create(parent, componentType);
                EnsureAuthoritativeIdAdvance(
                    previousNextId,
                    createdComponent == null ? -1 : (int)fComponentId.GetValue(createdComponent));
                createdComponent.setPosition(position);
                createdComponent.setRotation(command.RotationY);
                createdComponent.setValidPosition(true);
                createdComponent.setPositionY(parent.getFloorPosition().y);
                placementInvoked = true;
                parent.addComponent(createdComponent);
                createdComponent.onUserPlaced();
                EnsureAuthoritativeIdAdvance(
                    previousNextId,
                    createdComponent == null ? -1 : (int)fComponentId.GetValue(createdComponent));
                applied = true;
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] authoritative placement uygulanamadi: " + ex);
                return false;
            }
            finally
            {
                if (!applied)
                {
                    DisposeTemporary(createdModule);
                    DisposeTemporary(createdComponent);
                    if (!placementInvoked) RestorePreviewId(previousNextId);
                }
            }
        }

        private static void EnsureAuthoritativeIdAdvance(int previousNextId, int entityId)
        {
            int nextId = ReadNextId();
            if (!CoopAuthoritativeIdPolicy.IsExpectedAdvance(
                    previousNextId, entityId, nextId))
            {
                throw new InvalidOperationException(string.Format(
                    "authoritative id invariant failed: previous={0} entity={1} next={2}",
                    previousNextId, entityId, nextId));
            }
        }

        // Unity Object.Destroy is deferred until the end of the frame.  A
        // validation object must leave physics immediately or the next rapid
        // placement can raycast against a stale collider/registry entry.
        private static void DisposeTemporary(Selectable temporary)
        {
            if (temporary == null) return;
            try
            {
                GameObject gameObject = temporary.getGameObject();
                if (gameObject != null) gameObject.SetActive(false);
            }
            catch (Exception) { }
            try { temporary.destroy(); }
            catch (Exception ex) { CoopPlugin.Log.LogWarning("[Coop] geçici yerleştirme nesnesi temizlenemedi: " + ex.Message); }
        }

        public static void CancelSynchronizedPreview()
        {
            GameStateGame game = GetGame();
            if (game == null) return;
            try { ClearLocalPlacement(game); }
            catch (Exception ex) { CoopPlugin.Log.LogWarning("[Coop] yerleştirme iptali başarısız: " + ex.Message); }
        }

        private static void ClearLocalPlacement(GameStateGame game)
        {
            string mode = fMode.GetValue(game)?.ToString() ?? string.Empty;
            if (mode == "PlacingModule") mCancelModulePlacement.Invoke(game, null);
            else if (mode == "PlacingComponent") mCancelComponentPlacement.Invoke(game, null);
        }

        private static ModuleType FindModuleType(string runtimeName)
        {
            if (String.IsNullOrEmpty(runtimeName)) return null;
            List<ModuleType> types = TypeList<ModuleType, ModuleTypeList>.get();
            for (int i = 0; i < types.Count; i++)
            {
                ModuleType type = types[i];
                if (type != null && StringComparer.Ordinal.Equals(type.GetType().Name, runtimeName)) return type;
            }
            return TypeList<ModuleType, ModuleTypeList>.find(runtimeName);
        }

        private static ComponentType FindComponentType(string runtimeName)
        {
            if (String.IsNullOrEmpty(runtimeName)) return null;
            List<ComponentType> types = TypeList<ComponentType, ComponentTypeList>.get();
            for (int i = 0; i < types.Count; i++)
            {
                ComponentType type = types[i];
                if (type != null && StringComparer.Ordinal.Equals(type.GetType().Name, runtimeName)) return type;
            }
            return TypeList<ComponentType, ComponentTypeList>.find(runtimeName);
        }

        private static void SetMode(GameStateGame game, string name)
        {
            fMode.SetValue(game, Enum.Parse(fMode.FieldType, name));
        }

        /// <summary>Her kare: oyun yerleştirme modundan çıktıysa durumumuzu sıfırla.</summary>
        public static void UpdateState()
        {
            if (!Active)
            {
                return;
            }
            GameStateGame g = GetGame();
            if (g == null)
            {
                return;
            }
            string mode = ModeName();
            bool inPlacement = sComponentMode ? mode == "PlacingComponent" : mode == "PlacingModule";
            if (!inPlacement)
            {
                Active = false;
                ClearNetworkPreview();
                CoopPlugin.Log.LogInfo("[Coop] inşaat modu bitti (oyun çıkardı)");
            }
        }

        /// <summary>B: inşaat modunu aç/kapat (modül).</summary>
        public static void ToggleBuild()
        {
            if (!sInit || !sInitOk)
            {
                return;
            }
            if (Active)
            {
                Cancel();
                return;
            }
            GameStateGame g = GetGame();
            if (g == null)
            {
                return;
            }
            List<ModuleType> mods = TypeList<ModuleType, ModuleTypeList>.get();
            if (mods.Count == 0)
            {
                return;
            }
            sComponentMode = false;
            sTypeIndex = 0;
            // halka açık giriş noktası: onButtonBuildModule -> startPlacingModule
            g.onButtonBuildModule(mods[0]);
            Active = true;
            CoopPlugin.Log.LogInfo("[Coop] MİSAFİR inşaat modu: " + CurrentTypeName + " — sol tık yerleştir, sağ tık iptal, ,/. tür değiştir");
        }

        /// <summary>C: seçili modüle bileşen yerleştirmeyi başlat.</summary>
        public static void ToggleComponent()
        {
            GameStateGame g = GetGame();
            if (g == null)
            {
                return;
            }
            if (Active && sComponentMode)
            {
                Cancel();
                return;
            }
            if (Selection.getSelected() == null)
            {
                CoopPlugin.Log.LogInfo("[Coop] bileşen için önce bir modül SEÇ (sol tık)");
                return;
            }
            List<ComponentType> comps = TypeList<ComponentType, ComponentTypeList>.get();
            if (comps.Count == 0)
            {
                return;
            }
            if (Active)
            {
                Cancel();
            }
            sComponentMode = true;
            sTypeIndex = 0;
            g.onButtonPlaceComponent(comps[0]);
            Active = true;
            CoopPlugin.Log.LogInfo("[Coop] MİSAFİR bileşen modu: " + CurrentTypeName + " — sol tık yerleştir, sağ tık iptal");
        }

        /// <summary>,/. : tür değiştir (modül veya bileşen).</summary>
        public static void Cycle(int delta)
        {
            if (!Active)
            {
                return;
            }
            GameStateGame g = GetGame();
            if (g == null)
            {
                return;
            }
            try
            {
                if (sComponentMode)
                {
                    List<ComponentType> comps = TypeList<ComponentType, ComponentTypeList>.get();
                    if (comps.Count == 0) return;
                    // tür değiştirmek için önce mevcut yerleşimi iptal et
                    mCancelComponentPlacement.Invoke(g, null);
                    sTypeIndex = (sTypeIndex + delta + comps.Count) % comps.Count;
                    g.onButtonPlaceComponent(comps[sTypeIndex]);
                }
                else
                {
                    List<ModuleType> mods = TypeList<ModuleType, ModuleTypeList>.get();
                    if (mods.Count == 0) return;
                    mCancelModulePlacement.Invoke(g, null);
                    sTypeIndex = (sTypeIndex + delta + mods.Count) % mods.Count;
                    g.onButtonBuildModule(mods[sTypeIndex]);
                }
                CoopPlugin.Log.LogInfo("[Coop] inşaat türü: " + CurrentTypeName);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] tür değiştirme hatası: " + ex.Message);
            }
        }

        /// <summary>Misafir sol tık: yerleştir.</summary>
        public static void GuestLeftClick()
        {
            if (!Active)
            {
                return;
            }
            GameStateGame g = GetGame();
            if (g == null)
            {
                return;
            }
            try
            {
                if (sComponentMode)
                {
                    mPlaceComponent.Invoke(g, null);
                }
                else
                {
                    mPlaceModule.Invoke(g, null);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] yerleştirme hatası: " + ex.Message);
            }
        }

        /// <summary>Misafir sağ tık: iptal et.</summary>
        public static void GuestRightClick()
        {
            ClearNetworkPreview();
            if (!Active)
            {
                return;
            }
            GameStateGame g = GetGame();
            if (g == null)
            {
                return;
            }
            try
            {
                if (sComponentMode)
                {
                    mCancelComponentPlacement.Invoke(g, null);
                }
                else
                {
                    mCancelModulePlacement.Invoke(g, null);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] iptal hatası: " + ex.Message);
            }
            Active = false;
        }

        public static void Cancel()
        {
            GuestRightClick();
        }

        /// <summary>
        /// tryPlaceModule'in misafir ışınıyla çalışan versiyonu.
        /// Harmony prefix'i orijinal metodu atlar ve burayı çağırır.
        /// </summary>
        public static bool TryPlaceModuleWithGuestRay(GameStateGame g)
        {
            try
            {
                Ray ray = GuestCameraController.Instance.GetGuestRay();
                const int layerMask = 256;
                if (!Physics.Raycast(ray, out RaycastHit hitInfo, 150f, layerMask))
                {
                    return false;
                }

                Module activeModule = (Module)fActiveModule.GetValue(g);
                int currentSize = (int)fCurrentModuleSize.GetValue(g);
                ModuleType placedType = (ModuleType)fPlacedModuleType.GetValue(g);
                bool renderTops = (bool)fRenderTops.GetValue(g);

                float size = Module.ValidSizes[currentSize];
                if (activeModule == null)
                {
                    activeModule = Module.create(hitInfo.point, currentSize, placedType);
                    activeModule.setRenderTop(renderTops);
                    activeModule.setValidPosition(false);
                    fCost.SetValue(g, activeModule.calculateCost());
                    fActiveModule.SetValue(g, activeModule);
                }
                if (currentSize != activeModule.getSizeIndex())
                {
                    activeModule.changeSize(currentSize);
                    fCost.SetValue(g, activeModule.calculateCost());
                    activeModule.setValidPosition(false);
                }
                bool validPosition = activeModule.canPlaceModule(hitInfo.point, hitInfo.normal, size);

                Vector3 point = hitInfo.point;
                point.y = TerrainGenerator.getInstance().getFloorHeight();
                if ((!activeModule.isValidPosition() | validPosition) || (point - activeModule.getPosition()).magnitude > 5f)
                {
                    activeModule.setValidPosition(validPosition);
                    activeModule.setPosition(point);
                }
                activeModule.setPositionY(point.y + 0.1f);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] modül önizleme hatası: " + ex.Message);
            }
            return false;
        }

        /// <summary>tryPlaceComponent'in misafir ışınıyla çalışan versiyonu.</summary>
        public static bool TryPlaceComponentWithGuestRay(GameStateGame g)
        {
            try
            {
                Ray ray = GuestCameraController.Instance.GetGuestRay();
                const int layerMask = 4096;
                if (!Physics.Raycast(ray, out RaycastHit hitInfo, 100f, layerMask))
                {
                    return false;
                }

                Module module = (Module)fActiveModule.GetValue(g);
                ConstructionComponent placed = (ConstructionComponent)fPlacedComponent.GetValue(g);
                if (placed == null)
                {
                    placed = ConstructionComponent.create(module, (ComponentType)fPlacedComponentType.GetValue(g));
                    placed.setRotation(module.getTransform().localRotation.y);
                    fCost.SetValue(g, placed.calculateCost());
                    fPlacedComponent.SetValue(g, placed);
                }
                placed.setPosition(hitInfo.point);
                placed.setValidPosition(module.canPlaceComponent(placed));

                if (placed.isValidPosition())
                {
                    fValidComponentPosition.SetValue(g, placed.getPosition());
                    fValidComponentRotation.SetValue(g, placed.getRotation());
                }
                else
                {
                    Vector3? validPos = (Vector3?)fValidComponentPosition.GetValue(g);
                    if (validPos.HasValue)
                    {
                        if (Vector3.Distance(placed.getPosition(), validPos.Value) < 1f)
                        {
                            placed.setPosition(validPos.Value);
                            placed.setRotation((float)fValidComponentRotation.GetValue(g));
                            placed.setValidPosition(true);
                        }
                        else
                        {
                            fValidComponentPosition.SetValue(g, null);
                            fValidComponentRotation.SetValue(g, null);
                        }
                    }
                }
                placed.setPositionY(placed.getParentConstruction().getFloorPosition().y + 0.1f);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] bileşen önizleme hatası: " + ex.Message);
            }
            return false;
        }
    }
}
