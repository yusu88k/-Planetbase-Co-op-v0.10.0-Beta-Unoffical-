using System;
using System.Reflection;
using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Hostun gerçek Planetbase seçim ekranını misafire salt-okunur olarak yansıtır.
    /// Ağ thread'i yalnız DTO günceller; Unity nesnelerine yalnız ana GUI thread'i dokunur.
    /// </summary>
    internal static class CoopVanillaSelectionBridge
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly MethodInfo OnPlanetSelectedMethod =
            typeof(GameStateLocationSelection).GetMethod("onPlanetSelected", InstanceFlags);
        private static readonly MethodInfo OnLocationCancelledMethod =
            typeof(GameStateLocationSelection).GetMethod("onLocationSelectCancelled", InstanceFlags);
        private static readonly MethodInfo UpdateLocationMethod =
            typeof(GameStateLocationSelection).GetMethod("updateLocationSelect", InstanceFlags);
        private static readonly FieldInfo SelectedPlanetField =
            typeof(GameStateLocationSelection).GetField("mSelectedPlanet", InstanceFlags);
        private static readonly FieldInfo LatitudeField =
            typeof(GameStateLocationSelection).GetField("mLatitude", InstanceFlags);
        private static readonly FieldInfo LongitudeField =
            typeof(GameStateLocationSelection).GetField("mLongitude", InstanceFlags);
        private static readonly FieldInfo LastMousePositionField =
            typeof(GameStateLocationSelection).GetField("mLastMousePos", InstanceFlags);

        [ThreadStatic] private static bool sApplyingRemoteView;
        [ThreadStatic] private static bool sAuthorizingWorldTransition;
        private static long sLastAppliedRevision;

        public static bool ApplyingRemoteView
        {
            get { return sApplyingRemoteView; }
        }

        public static bool AuthorizingWorldTransition
        {
            get { return sAuthorizingWorldTransition; }
        }

        public static void BeginAuthorizedWorldTransition()
        {
            sAuthorizingWorldTransition = true;
        }

        public static void EndAuthorizedWorldTransition()
        {
            sAuthorizingWorldTransition = false;
        }

        public static bool IsGuestReadOnlySelection()
        {
            if (SyncManager.Role != "guest" || SyncManager.Instance == null) return false;
            CoopLobbyInfo info = SyncManager.Instance.GetLobbyInfo();
            if (info.Phase != CoopLobbyPhase.Selecting &&
                info.Phase != CoopLobbyPhase.WorldCommitted) return false;
            try
            {
                return GameManager.getInstance().getGameState() is GameStateLocationSelection;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Reset()
        {
            sApplyingRemoteView = false;
            sAuthorizingWorldTransition = false;
            sLastAppliedRevision = 0;
        }

        public static void ApplyRemoteView(CoopLobbySelectionView view)
        {
            if (!IsGuestReadOnlySelection() ||
                !CoopLobbyProtocol.IsValidSelectionView(view) ||
                view.Revision <= sLastAppliedRevision) return;

            GameStateLocationSelection state =
                GameManager.getInstance().getGameState() as GameStateLocationSelection;
            if (state == null || SelectedPlanetField == null || LatitudeField == null ||
                LongitudeField == null) return;

            sApplyingRemoteView = true;
            try
            {
                int selectedPlanet = (int)SelectedPlanetField.GetValue(state);
                if (view.Stage == CoopSelectionStage.Planet)
                {
                    if (selectedPlanet >= 0 && OnLocationCancelledMethod != null)
                        OnLocationCancelledMethod.Invoke(state, null);
                }
                else
                {
                    if (selectedPlanet >= 0 && selectedPlanet != view.PlanetIndex &&
                        OnLocationCancelledMethod != null)
                    {
                        OnLocationCancelledMethod.Invoke(state, null);
                        selectedPlanet = -1;
                    }
                    if (selectedPlanet != view.PlanetIndex && OnPlanetSelectedMethod != null)
                        OnPlanetSelectedMethod.Invoke(state, new object[] { view.PlanetIndex });

                    LatitudeField.SetValue(state, view.Latitude);
                    LongitudeField.SetValue(state, view.Longitude);
                    if (LastMousePositionField != null)
                        LastMousePositionField.SetValue(state, Input.mousePosition);
                    Colony colony = Singleton<Colony>.getInstance();
                    colony.setName(view.BaseName);
                    colony.setLatitude(Mathf.FloorToInt(view.Latitude));
                    colony.setLongitude(Mathf.FloorToInt(view.Longitude));

                    // 0.5 saniyelik vanilla geçiş adımını tek seferde tamamlar;
                    // ardından guest update patch'leri tüm yerel fare girdisini keser.
                    if (UpdateLocationMethod != null)
                        UpdateLocationMethod.Invoke(state, new object[] { 0.5f });
                    LatitudeField.SetValue(state, view.Latitude);
                    LongitudeField.SetValue(state, view.Longitude);
                }

                sLastAppliedRevision = view.Revision;
                SyncManager.Instance.AcknowledgeLobbySelectionLoaded(view.Revision);
            }
            catch (TargetInvocationException ex)
            {
                Exception cause = ex.InnerException ?? ex;
                CoopPlugin.Log.LogError("[Coop] misafir seçim görünümü uygulanamadı: " + cause);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] misafir seçim görünümü uygulanamadı: " + ex);
            }
            finally
            {
                sApplyingRemoteView = false;
            }
        }
    }

    [HarmonyPatch(typeof(GameStateLocationSelection), "updatePlanetSelect")]
    internal static class CoopGuestPlanetSelectionInputPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CoopVanillaSelectionBridge.IsGuestReadOnlySelection() ||
                CoopVanillaSelectionBridge.ApplyingRemoteView;
        }
    }

    [HarmonyPatch(typeof(GameStateLocationSelection), "updateLocationSelect")]
    internal static class CoopGuestLocationSelectionInputPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CoopVanillaSelectionBridge.IsGuestReadOnlySelection() ||
                CoopVanillaSelectionBridge.ApplyingRemoteView;
        }
    }

    [HarmonyPatch(typeof(GameStateLocationSelection), "onPlanetSelected")]
    internal static class CoopGuestPlanetSelectionMutationPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CoopVanillaSelectionBridge.IsGuestReadOnlySelection() ||
                CoopVanillaSelectionBridge.ApplyingRemoteView;
        }
    }

    [HarmonyPatch(typeof(GameStateLocationSelection), "onLocationSelectCancelled")]
    internal static class CoopGuestLocationCancelPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CoopVanillaSelectionBridge.IsGuestReadOnlySelection() ||
                CoopVanillaSelectionBridge.ApplyingRemoteView;
        }
    }

    [HarmonyPatch(typeof(GameStateLocationSelection), "onGui")]
    internal static class CoopGuestSelectionGuiLockPatch
    {
        [HarmonyPrefix]
        public static void Prefix(out bool __state)
        {
            __state = GUI.enabled;
            if (CoopVanillaSelectionBridge.IsGuestReadOnlySelection()) GUI.enabled = false;
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            GUI.enabled = __state;
        }
    }
}
