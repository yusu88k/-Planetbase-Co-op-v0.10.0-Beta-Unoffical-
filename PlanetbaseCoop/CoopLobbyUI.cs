using System;
using HarmonyLib;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Ana menü co-op arayüzü — oyunun kendi GuiRenderer stilleriyle çizilir
    /// (vanilya font/tasarım; GUILayout panel kullanılmaz).
    /// Akış: CO-OP -> bekleme/hazır lobisi -> hostun gerçek vanilla gezegen ve
    /// konum seçimi. Misafir aynı vanilla ekranı salt-okunur izler.
    /// </summary>
    public static class CoopLobbyUI
    {
        private static bool sJoinOpen;
        private static string sIpText = "127.0.0.1";
        private static string sPortText = SyncManager.DefaultPort.ToString();
        private static string sError = "";
        private static bool sMenuRequested;
        private static string sLastUiError = "";
        private static bool sEntryOpenLogged;
        private static bool sHookLogged;
        private static int sLastDrawFrame = -1;
        private static bool sVanillaNewGameRequested;
        private static int sVanillaTransitionDepth;
        private static CoopLobbySelectionView sLastVanillaView;
        private static bool sHasLastVanillaView;
        private static float sLastVanillaPublishTime = -100f;
        private static bool sPendingVanillaWorldStart;
        private static bool sSteamFriendPickerOpen;
        private static int sSteamFriendPage;
        private static bool sSavePickerOpen;
        private static int sSavePage;
        private static int sInputBlockedThroughFrame = -1;
        private static CoopSteamFriendInfo[] sSteamFriends =
            new CoopSteamFriendInfo[0];
        private static CoopSaveEntry[] sSaveEntries = new CoopSaveEntry[0];
        private const int SavePageSize = 5;
        private const string GuestSelectionNotReadyError =
            "MİSAFİR SEÇİM EKRANI HAZIR DEĞİL";

        private static GuiRenderer sNativeRenderer;

        private static GuiRenderer R()
        {
            if (sNativeRenderer == null) sNativeRenderer = new GuiRenderer();
            return sNativeRenderer;
        }

        public static void OnGUI(string host)
        {
            try
            {
                bool repaint = Event.current != null &&
                    Event.current.type == EventType.Repaint;
                if (repaint)
                {
                    int frame = Time.frameCount;
                    if (frame == sLastDrawFrame) return;
                    sLastDrawFrame = frame;
                    if (!sHookLogged)
                    {
                        sHookLogged = true;
                        CoopPlugin.Log.LogInfo(
                            "[Coop] lobi arayüzü çiziliyor (kanca=" + host + ")");
                    }
                }
                bool wasEnabled = GUI.enabled;
                GUI.enabled = true;
                try
                {
                    DrawLobbyUi(SyncManager.Instance);
                }
                finally
                {
                    GUI.enabled = wasEnabled;
                }
            }
            catch (Exception ex)
            {
                if (!string.Equals(sLastUiError, ex.Message, StringComparison.Ordinal))
                {
                    sLastUiError = ex.Message;
                    CoopPlugin.Log.LogError("[Coop] lobi arayuzu hatasi: " + ex);
                }
            }
        }

        private static void DrawLobbyUi(SyncManager instance)
        {
            if (instance != null && SyncManager.FullMode &&
                !string.IsNullOrEmpty(SyncManager.Role))
            {
                CoopLobbyInfo info = instance.GetLobbyInfo();
                if (!info.SessionActive)
                {
                    if (IsTitleState() && sMenuRequested) DrawEntryScreen();
                    return;
                }
                bool vanillaSelection = CoopLobbyPolicy.ShouldOpenVanillaNewGameSelection(
                    info.Role,
                    info.SessionActive,
                    info.LobbyActive,
                    info.Started,
                    info.Phase) &&
                    (info.Role == "guest" || sVanillaNewGameRequested ||
                        IsLocationSelectionState());
                if (vanillaSelection)
                {
                    if (info.Role == "host")
                    {
                        if (PublishHostVanillaSelection())
                            TryCompletePendingVanillaWorldStart();
                    }
                    else
                    {
                        EnsureGuestVanillaNewGameSelection();
                        CoopVanillaSelectionBridge.ApplyRemoteView(info.SelectionView);
                    }
                    return;
                }
                if (info.LobbyActive &&
                    CoopLobbyPolicy.ShouldRenderWaitingLobby(info.Phase))
                {
                    DrawWaitingLobby(info);
                }
                return;
            }
            if (!IsTitleState()) return;
            if (sMenuRequested) DrawEntryScreen();
        }

        public static void OpenMenu()
        {
            BlockInputThroughCurrentFrame();
            sMenuRequested = true;
        }

        public static void CloseMenu()
        {
            sMenuRequested = false;
            sJoinOpen = false;
            sError = "";
            sEntryOpenLogged = false;
        }

        public static bool IsMenuOpen()
        {
            return sMenuRequested;
        }

        internal static bool ShouldSuppressVanillaTitleMenu()
        {
            CoopLobbyInfo info = SyncManager.Instance == null
                ? default(CoopLobbyInfo)
                : SyncManager.Instance.GetLobbyInfo();
            return CoopLobbyPolicy.ShouldSuppressTitleMenu(
                sMenuRequested,
                info.SessionActive,
                info.LobbyActive,
                info.Phase);
        }

        internal static bool IsVanillaLocationTransitionAuthorized
        {
            get { return sVanillaTransitionDepth > 0; }
        }

        internal static bool IsHostVanillaSelectionState()
        {
            return sVanillaNewGameRequested &&
                SyncManager.Role == "host" &&
                IsLocationSelectionState();
        }

        private static bool IsTitleState()
        {
            try
            {
                GameManager gm = GameManager.getInstance();
                return gm != null && gm.getGameState() is GameStateTitle;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsLocationSelectionState()
        {
            try
            {
                GameManager gm = GameManager.getInstance();
                return gm != null && gm.getGameState() is GameStateLocationSelection;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ==================== VANİLYA ÇİZİM YARDIMCILARI ====================

        /// <summary>Vanilya renderTitle kopyası: Huge/Title fontuyla sayfa başlığı.</summary>
        private static void RenderPageTitle(string text)
        {
            float width = Screen.width * 0.4f;
            float height = Screen.height * 0.1f;
            GUIStyle style = new GUIStyle(R().getLabelStyle(FontSize.Huge, FontStyle.Bold,
                TextAnchor.UpperCenter, FontType.Title));
            GUI.Label(new Rect(Screen.width * 0.5f - width * 0.5f,
                Screen.height * 0.5f - Screen.height * 0.4f, width, height), CoopText.T(text), style);
        }

        private static GUIStyle TitleLabel(FontSize size, TextAnchor anchor)
        {
            return new GUIStyle(R().getLabelStyle(size, FontStyle.Bold, anchor, FontType.Title));
        }

        private static GUIStyle BodyLabel(FontSize size, TextAnchor anchor)
        {
            return new GUIStyle(R().getLabelStyle(size, FontStyle.Bold, anchor));
        }

        private static bool RenderBigButton(Rect rect, string text)
        {
            return RenderBigButton(rect, text, FontSize.VeryLarge);
        }

        private static bool RenderBigButton(Rect rect, string text, FontSize size)
        {
            bool wasEnabled = GUI.enabled;
            if (!CoopLobbyPolicy.CanAcceptUiInput(
                    Time.frameCount, sInputBlockedThroughFrame))
                GUI.enabled = false;
            try
            {
                return R().renderTitleButton(rect, CoopText.T(text), size);
            }
            finally
            {
                GUI.enabled = wasEnabled;
            }
        }

        private static void BlockInputThroughCurrentFrame()
        {
            sInputBlockedThroughFrame = Math.Max(
                sInputBlockedThroughFrame, Time.frameCount);
        }

        private static void DrawErrorCentered(float y)
        {
            if (string.IsNullOrEmpty(sError)) return;
            GUIStyle style = BodyLabel(FontSize.Normal, TextAnchor.MiddleCenter);
            style.normal.textColor = new Color(1f, 0.45f, 0.3f);
            style.wordWrap = true;
            float width = Screen.width * 0.5f;
            GUI.Label(new Rect(Screen.width * 0.5f - width * 0.5f, y, width, 60f), CoopText.T(sError), style);
        }

        // ==================== GİRİŞ EKRANI (vanilya stili) ====================

        private static void DrawEntryScreen()
        {
            if (!sEntryOpenLogged)
            {
                sEntryOpenLogged = true;
                CoopPlugin.Log.LogInfo("[Coop] co-op giris ekrani ciziliyor");
            }

            Vector2 size = GuiRenderer.getMenuButtonSize(FontSize.VeryLarge);
            float cx = Screen.width * 0.5f;
            float step = size.y * 1.35f;
            float y = Screen.height * 0.36f;

            if (!sJoinOpen)
            {
                if (RenderBigButton(new Rect(cx - size.x * 0.5f, y, size.x, size.y),
                    "STEAM'DE HOST OL", FontSize.Large))
                {
                    BlockInputThroughCurrentFrame();
                    sError = CoopSteamService.CreateFriendsLobby();
                }
                y += step;
                if (RenderBigButton(new Rect(cx - size.x * 0.5f, y, size.x, size.y),
                    "LAN / IP HOST OL", FontSize.Large))
                {
                    StartSession("host");
                }
                y += step;
                if (RenderBigButton(new Rect(cx - size.x * 0.5f, y, size.x, size.y),
                    "IP İLE KATIL", FontSize.Large))
                {
                    BlockInputThroughCurrentFrame();
                    sJoinOpen = true;
                    sError = "";
                }

                GUIStyle steamStatus = BodyLabel(FontSize.Normal,
                    TextAnchor.MiddleCenter);
                steamStatus.wordWrap = true;
                GUI.Label(new Rect(cx - size.x, Screen.height * 0.72f,
                    size.x * 2f, 54f), CoopText.T(CoopSteamService.Status), steamStatus);
            }
            else
            {
                GUIStyle header = TitleLabel(FontSize.Large, TextAnchor.MiddleLeft);
                GUIStyle field = R().getTextFieldStyle(FontSize.Large, FontStyle.Bold);
                float fieldWidth = Screen.height * 0.42f;
                float left = cx - fieldWidth * 0.5f;
                y = Screen.height * 0.31f;

                GUI.Label(new Rect(left, y, fieldWidth, size.y * 0.45f), CoopText.T("HOST ADRESİ:"), header);
                y += size.y * 0.58f;
                sIpText = GUI.TextField(
                    new Rect(left, y, fieldWidth, size.y * 0.68f), sIpText, 253, field);
                y += size.y * 1.08f;
                GUI.Label(new Rect(left, y, fieldWidth, size.y * 0.45f), "PORT:", header);
                y += size.y * 0.58f;
                sPortText = GUI.TextField(
                    new Rect(left, y, fieldWidth, size.y * 0.68f), sPortText, 5, field);
                y += size.y * 1.18f;

                if (RenderBigButton(new Rect(cx - size.x * 0.5f, y, size.x, size.y), "BAĞLAN"))
                {
                    StartSession("guest");
                }
            }

            DrawErrorCentered(Screen.height * 0.82f);

            // Dil secimi sag ustte; BepInEx config'e kaydedilir.
            Vector2 langSize = GuiRenderer.getMenuButtonSize(FontSize.Normal);
            Rect langRect = new Rect(Screen.width - langSize.x * 1.7f - 24f, 24f,
                langSize.x * 1.7f, langSize.y);
            if (RenderBigButton(langRect, CoopText.ToggleLabel(CoopText.Language),
                    FontSize.Normal))
            {
                BlockInputThroughCurrentFrame();
                CoopPlugin.ToggleLanguage();
            }

            // Vanilya geri butonuyla aynı stil/konum.
            Rect backRect = new Rect(cx - size.x * 0.5f,
                Screen.height - Screen.height * 0.08f - size.y, size.x, size.y);
            if (RenderBigButton(backRect, StringList.get("back"), FontSize.VeryLarge))
            {
                BlockInputThroughCurrentFrame();
                if (sJoinOpen)
                {
                    sJoinOpen = false;
                    sError = "";
                }
                else
                {
                    CloseMenu();
                }
            }
        }

        // ==================== LOBİ EKRANI (vanilya stili) ====================
        // Host seçim yapar; misafir aynı ekranı izler (yalnız HAZIRIM/AYRIL aktif).

        private static void DrawWaitingLobby(CoopLobbyInfo info)
        {
            bool host = info.Role == "host";
            bool steam = SyncManager.UsingSteamTransport;
            if (host && sSavePickerOpen)
            {
                DrawSavePicker();
                return;
            }
            if (host && steam && sSteamFriendPickerOpen)
            {
                DrawSteamFriendPicker();
                return;
            }
            RenderPageTitle(host ? "HOST LOBİSİ" : "OYUNA KATIL");

            Vector2 size = GuiRenderer.getMenuButtonSize(FontSize.VeryLarge);
            float cx = Screen.width * 0.5f;
            float columnWidth = size.x * 1.55f;
            float left = cx - columnWidth * 0.5f;
            float y = Screen.height * 0.29f;
            float row = size.y * 0.82f;

            GUIStyle heading = TitleLabel(FontSize.Large, TextAnchor.MiddleCenter);
            GUIStyle body = BodyLabel(FontSize.Normal, TextAnchor.MiddleCenter);
            body.wordWrap = true;

            GUI.Label(new Rect(left, y, columnWidth, row),
                CoopText.T(steam ? "BAĞLANTI" : "HOST IP ADRESİ"), heading);
            y += row * 0.72f;
            GUI.Label(new Rect(left, y, columnWidth, row),
                string.IsNullOrEmpty(info.HostAddress) ? "127.0.0.1" : CoopText.T(info.HostAddress),
                body);
            y += row;
            GUI.Label(new Rect(left, y, columnWidth, row), steam
                ? CoopText.T("PORT AÇMA GEREKMEZ")
                : "PORT  " + info.Port, heading);
            y += row * 1.2f;

            string status;
            if (host)
            {
                status = !info.Connected
                    ? "MİSAFİR BEKLENİYOR..."
                    : (info.ReadyVisible
                        ? "MİSAFİR KATILDI • HAZIR"
                        : "MİSAFİR KATILDI • HAZIR DEĞİL");
            }
            else
            {
                status = !info.Connected
                    ? "HOST'A BAĞLANILIYOR..."
                    : (info.ReadyVisible
                        ? "BAĞLANDIN • HAZIRSIN • HOST BEKLENİYOR"
                        : "BAĞLANDIN • HAZIRLIK ONAYI BEKLENİYOR");
            }
            GUI.Label(new Rect(left, y, columnWidth, row * 1.4f), CoopText.T(status), heading);

            string worldLabel = info.Selection.Mode == CoopLobbyMode.Load
                ? "DÜNYA: KAYIT — " + (info.Selection.SaveName ?? string.Empty)
                : (info.Selection.Mode == CoopLobbyMode.Continue
                    ? "DÜNYA: SON KAYITTAN DEVAM"
                    : "DÜNYA: YENİ OYUN");
            GUI.Label(new Rect(left, Screen.height * 0.57f, columnWidth, row),
                CoopText.T(worldLabel), heading);

            if (host)
            {
                float choiceY = Screen.height * 0.64f;
                float choiceGap = size.x * 0.10f;
                float choiceWidth = size.x * 0.92f;
                if (RenderBigButton(new Rect(cx - choiceGap * 0.5f - choiceWidth,
                        choiceY, choiceWidth, size.y), "YENİ OYUN", FontSize.Large))
                {
                    sError = SyncManager.Instance.SetLobbySelection(
                        new CoopLobbySelection
                        {
                            Mode = CoopLobbyMode.NewGame,
                            PlanetIndex = 0,
                            Seed = 0,
                            SaveName = string.Empty
                        });
                }
                if (RenderBigButton(new Rect(cx + choiceGap * 0.5f,
                        choiceY, choiceWidth, size.y), "KAYIT SEÇ", FontSize.Large))
                {
                    OpenSavePicker();
                }
            }

            DrawErrorCentered(Screen.height * 0.76f);

            float bottomY = Screen.height - Screen.height * 0.08f - size.y;
            float buttonGap = size.x * 0.16f;
            float leftButtonX = cx - buttonGap * 0.5f - size.x;
            float rightButtonX = cx + buttonGap * 0.5f;

            if (RenderBigButton(new Rect(leftButtonX, bottomY, size.x, size.y), "AYRIL"))
            {
                BlockInputThroughCurrentFrame();
                SyncManager.EndMenuSession();
                ResetUiState();
            }

            if (host)
            {
                if (steam)
                {
                    Rect inviteRect = new Rect(cx - size.x * 0.5f,
                        bottomY - size.y * 1.18f, size.x, size.y);
                    if (RenderBigButton(inviteRect, "ARKADAŞ DAVET ET",
                        FontSize.Large))
                    {
                        BlockInputThroughCurrentFrame();
                        OpenSteamFriendPicker();
                    }
                }
                bool canStart = CoopLobbyPolicy.CanEnterSelection(
                    info.Role, info.Phase, info.Connected, info.ReadyVisible);
                GUI.enabled = canStart;
                if (RenderBigButton(new Rect(rightButtonX, bottomY, size.x, size.y),
                    "BAŞLAT"))
                {
                    BlockInputThroughCurrentFrame();
                    if (info.Selection.Mode == CoopLobbyMode.NewGame)
                    {
                        sError = SyncManager.Instance.RequestLobbySelectionStart();
                        if (string.IsNullOrEmpty(sError)) OpenHostVanillaNewGameSelection();
                    }
                    else
                    {
                        sError = SyncManager.Instance.RequestLobbySavedGameStart();
                    }
                }
                GUI.enabled = true;
            }
            else
            {
                GUI.enabled = info.Connected;
                string readyLabel = info.ReadyVisible ? "HAZIRLIĞI İPTAL ET" : "HAZIRIM";
                if (RenderBigButton(new Rect(rightButtonX, bottomY, size.x, size.y),
                    readyLabel))
                {
                    SyncManager.Instance.SetGuestLobbyReady(!info.ReadyVisible);
                }
                GUI.enabled = true;
            }
        }

        // ==================== OTURUM/SEÇİM MANTIĞI ====================

        private static void StartSession(string role)
        {
            BlockInputThroughCurrentFrame();
            int port;
            if (!int.TryParse(sPortText.Trim(), out port) || port < 1 || port > 65535)
            {
                sError = "GEÇERSİZ PORT";
                return;
            }
            string error = SyncManager.BeginSessionFromMenu(
                role, role == "guest" ? sIpText : "", port);
            if (!string.IsNullOrEmpty(error))
            {
                sError = error;
                return;
            }
            ResetUiState();
            CoopStatusOverlay.Notify(role == "host"
                ? "HOST LOBİSİ AÇILDI — MİSAFİR BEKLENİYOR"
                : "HOST'A BAĞLANILIYOR");
        }

        private static void OpenHostVanillaNewGameSelection()
        {
            sVanillaNewGameRequested = true;
            sHasLastVanillaView = false;
            sLastVanillaPublishTime = -100f;
            sVanillaTransitionDepth++;
            try
            {
                GameManager.getInstance().setGameStateLocationSelection();
                CoopPlugin.Log.LogInfo(
                    "[Coop] host vanilla NEW GAME gezegen secimine geciyor");
            }
            catch (Exception ex)
            {
                sVanillaNewGameRequested = false;
                SyncManager.EndMenuSession();
                sMenuRequested = true;
                sError = "DÜNYA SEÇİM EKRANI AÇILAMADI";
                CoopPlugin.Log.LogError(
                    "[Coop] vanilla gezegen secimi acilamadi: " + ex);
            }
            finally
            {
                sVanillaTransitionDepth--;
            }
        }

        private static void OpenSteamFriendPicker()
        {
            sSteamFriendPickerOpen = true;
            sSteamFriendPage = 0;
            RefreshSteamFriends();
        }

        private static void RefreshSteamFriends()
        {
            string error;
            sSteamFriends = CoopSteamService.GetFriends(out error);
            if (sSteamFriends == null) sSteamFriends = new CoopSteamFriendInfo[0];
            sSteamFriendPage = CoopSteamFriendPolicy.ClampPage(
                sSteamFriendPage, sSteamFriends.Length);
            sError = error;
        }

        private static void DrawSteamFriendPicker()
        {
            RenderPageTitle("STEAM ARKADAŞLARI");
            Vector2 size = GuiRenderer.getMenuButtonSize(FontSize.VeryLarge);
            float cx = Screen.width * 0.5f;
            float listWidth = size.x * 1.85f;
            float listY = Screen.height * 0.24f;
            float row = size.y * 1.02f;
            GUIStyle body = BodyLabel(FontSize.Normal, TextAnchor.MiddleCenter);
            body.wordWrap = true;

            if (sSteamFriends.Length == 0)
            {
                GUI.Label(new Rect(cx - listWidth * 0.5f, listY,
                    listWidth, row * 2f),
                    CoopText.T(string.IsNullOrEmpty(sError)
                        ? "DAVET EDİLEBİLECEK STEAM ARKADAŞI BULUNAMADI"
                        : sError),
                    body);
            }
            else
            {
                sSteamFriendPage = CoopSteamFriendPolicy.ClampPage(
                    sSteamFriendPage, sSteamFriends.Length);
                int first = sSteamFriendPage * CoopSteamFriendPolicy.PageSize;
                int last = Math.Min(sSteamFriends.Length,
                    first + CoopSteamFriendPolicy.PageSize);
                for (int i = first; i < last; i++)
                {
                    CoopSteamFriendInfo friend = sSteamFriends[i];
                    string label = friend.DisplayName +
                        (friend.Online ? "  •  ÇEVRİMİÇİ" : "  •  ÇEVRİMDIŞI");
                    if (RenderBigButton(new Rect(cx - listWidth * 0.5f,
                            listY + (i - first) * row, listWidth, size.y),
                        label, FontSize.Normal))
                    {
                        sError = CoopSteamService.InviteFriend(friend.SteamId);
                    }
                }

                int pageCount = CoopSteamFriendPolicy.PageCount(sSteamFriends.Length);
                if (pageCount > 1)
                {
                    float pagerY = listY + CoopSteamFriendPolicy.PageSize * row;
                    GUI.enabled = sSteamFriendPage > 0;
                    if (RenderBigButton(new Rect(cx - size.x - 12f, pagerY,
                            size.x, size.y), "ÖNCEKİ", FontSize.Normal))
                        sSteamFriendPage--;
                    GUI.enabled = sSteamFriendPage + 1 < pageCount;
                    if (RenderBigButton(new Rect(cx + 12f, pagerY,
                            size.x, size.y), "SONRAKİ", FontSize.Normal))
                        sSteamFriendPage++;
                    GUI.enabled = true;
                }
            }

            GUI.Label(new Rect(cx - listWidth * 0.5f, Screen.height * 0.70f,
                listWidth, size.y), CoopText.T(CoopSteamService.Status), body);
            DrawErrorCentered(Screen.height * 0.76f);

            float bottomY = Screen.height - Screen.height * 0.08f - size.y;
            float gap = size.x * 0.16f;
            if (RenderBigButton(new Rect(cx - gap * 0.5f - size.x, bottomY,
                    size.x, size.y), "YENİLE", FontSize.Large))
                RefreshSteamFriends();
            if (RenderBigButton(new Rect(cx + gap * 0.5f, bottomY,
                    size.x, size.y), "GERİ", FontSize.Large))
            {
                BlockInputThroughCurrentFrame();
                sSteamFriendPickerOpen = false;
                sError = "";
            }
        }

        private static void OpenSavePicker()
        {
            BlockInputThroughCurrentFrame();
            sSavePickerOpen = true;
            sSavePage = 0;
            RefreshSaveEntries();
        }

        private static void RefreshSaveEntries()
        {
            sSaveEntries = CoopSaveCatalog.List(SaveData.FolderName);
            int pageCount = Math.Max(1,
                (sSaveEntries.Length + SavePageSize - 1) / SavePageSize);
            sSavePage = Math.Max(0, Math.Min(sSavePage, pageCount - 1));
            sError = sSaveEntries.Length == 0 ? "KULLANILABİLİR KAYIT BULUNAMADI" : "";
        }

        private static void DrawSavePicker()
        {
            RenderPageTitle("KAYITTAN CO-OP AÇ");
            Vector2 size = GuiRenderer.getMenuButtonSize(FontSize.VeryLarge);
            float cx = Screen.width * 0.5f;
            float listWidth = size.x * 1.9f;
            float listY = Screen.height * 0.23f;
            float row = size.y * 1.03f;
            GUIStyle body = BodyLabel(FontSize.Normal, TextAnchor.MiddleCenter);
            body.wordWrap = true;

            int first = sSavePage * SavePageSize;
            int last = Math.Min(sSaveEntries.Length, first + SavePageSize);
            for (int i = first; i < last; i++)
            {
                CoopSaveEntry entry = sSaveEntries[i];
                string label = entry.SaveName + "  •  " +
                    entry.LastWriteTime.ToString("dd.MM  HH:mm");
                if (RenderBigButton(new Rect(cx - listWidth * 0.5f,
                        listY + (i - first) * row, listWidth, size.y),
                    label, FontSize.Normal))
                {
                    sError = SyncManager.Instance.SetLobbySelection(
                        new CoopLobbySelection
                        {
                            Mode = CoopLobbyMode.Load,
                            PlanetIndex = 0,
                            Seed = 0,
                            SaveName = entry.SaveName
                        });
                    if (string.IsNullOrEmpty(sError)) sSavePickerOpen = false;
                }
            }

            if (sSaveEntries.Length == 0)
            {
                GUI.Label(new Rect(cx - listWidth * 0.5f, listY,
                    listWidth, row * 2f),
                    CoopText.T("NORMAL BİR .SAV KAYDI BULUNAMADI"), body);
            }

            int pageCount = Math.Max(1,
                (sSaveEntries.Length + SavePageSize - 1) / SavePageSize);
            float pagerY = Screen.height * 0.72f;
            GUI.enabled = sSavePage > 0;
            if (RenderBigButton(new Rect(cx - size.x - 12f, pagerY,
                    size.x, size.y), "ÖNCEKİ", FontSize.Normal)) sSavePage--;
            GUI.enabled = sSavePage + 1 < pageCount;
            if (RenderBigButton(new Rect(cx + 12f, pagerY,
                    size.x, size.y), "SONRAKİ", FontSize.Normal)) sSavePage++;
            GUI.enabled = true;

            DrawErrorCentered(Screen.height * 0.80f);
            float bottomY = Screen.height - Screen.height * 0.08f - size.y;
            float gap = size.x * 0.16f;
            if (RenderBigButton(new Rect(cx - gap * 0.5f - size.x, bottomY,
                    size.x, size.y), "YENİLE", FontSize.Large))
                RefreshSaveEntries();
            if (RenderBigButton(new Rect(cx + gap * 0.5f, bottomY,
                    size.x, size.y), "GERİ", FontSize.Large))
            {
                BlockInputThroughCurrentFrame();
                sSavePickerOpen = false;
                sError = "";
            }
        }

        private static void EnsureGuestVanillaNewGameSelection()
        {
            if (SyncManager.Role != "guest" || IsLocationSelectionState() ||
                sVanillaNewGameRequested) return;
            sVanillaNewGameRequested = true;
            sVanillaTransitionDepth++;
            try
            {
                GameManager.getInstance().setGameStateLocationSelection();
                CoopPlugin.Log.LogInfo(
                    "[Coop] misafir salt-okunur vanilla seçim ekranına geçiyor");
            }
            catch (Exception ex)
            {
                sError = "SEÇİM EKRANI AÇILAMADI";
                CoopPlugin.Log.LogError(
                    "[Coop] misafir seçim ekranı açılamadı: " + ex);
            }
            finally
            {
                sVanillaTransitionDepth--;
            }
        }

        private static bool PublishHostVanillaSelection(bool force = false)
        {
            if (!IsHostVanillaSelectionState() || SyncManager.Instance == null) return false;
            try
            {
                GameStateLocationSelection state =
                    GameManager.getInstance().getGameState() as GameStateLocationSelection;
                if (state == null) return false;
                Traverse traverse = Traverse.Create(state);
                int planetIndex = traverse.Field<int>("mSelectedPlanet").Value;
                if (planetIndex < -1 || planetIndex > 3) return false;

                float latitude = planetIndex < 0
                    ? 0f
                    : traverse.Field<float>("mLatitude").Value;
                float longitude = planetIndex < 0
                    ? 0f
                    : traverse.Field<float>("mLongitude").Value;
                int seed = planetIndex < 0
                    ? 0
                    : Mathf.FloorToInt(longitude) * 1000 + Mathf.FloorToInt(latitude);
                string baseName = planetIndex < 0
                    ? string.Empty
                    : Singleton<Colony>.getInstance().getName();
                var view = new CoopLobbySelectionView
                {
                    Revision = 1,
                    Stage = planetIndex < 0
                        ? CoopSelectionStage.Planet
                        : CoopSelectionStage.Location,
                    PlanetIndex = planetIndex,
                    Latitude = latitude,
                    Longitude = longitude,
                    Seed = seed,
                    BaseName = baseName ?? string.Empty
                };

                // Zorunlu yayın yalnız throttle'ı atlar; değişmemiş görünüm için
                // yeni revision üretmek mevcut guest ACK'ini gereksiz yere bozardı.
                if (sHasLastVanillaView && ViewsMatch(sLastVanillaView, view)) return true;
                bool urgent = !sHasLastVanillaView ||
                    sLastVanillaView.Stage != view.Stage ||
                    sLastVanillaView.PlanetIndex != view.PlanetIndex ||
                    !StringComparer.Ordinal.Equals(sLastVanillaView.BaseName, view.BaseName);
                if (!force && !urgent && Time.unscaledTime - sLastVanillaPublishTime < 0.05f)
                    return true;

                string error = SyncManager.Instance.PublishLobbySelectionView(view);
                if (!string.IsNullOrEmpty(error) &&
                    SyncManager.Instance.GetLobbyInfo().Connected)
                {
                    sError = error;
                    return false;
                }
                else if (string.IsNullOrEmpty(error))
                {
                    sLastVanillaView = view;
                    sHasLastVanillaView = true;
                    sLastVanillaPublishTime = Time.unscaledTime;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError(
                    "[Coop] vanilla gezegen secimi yayinlanamadi: " + ex);
                return false;
            }
        }

        private static bool ViewsMatch(
            CoopLobbySelectionView left,
            CoopLobbySelectionView right)
        {
            return left.Stage == right.Stage &&
                left.PlanetIndex == right.PlanetIndex &&
                Mathf.Abs(left.Latitude - right.Latitude) < 0.01f &&
                Mathf.Abs(left.Longitude - right.Longitude) < 0.01f &&
                left.Seed == right.Seed &&
                StringComparer.Ordinal.Equals(left.BaseName, right.BaseName);
        }

        internal static bool TryHandleVanillaNewGameStart(int seed, int planetIndex)
        {
            if (!IsHostVanillaSelectionState() || SyncManager.Instance == null) return false;
            CoopLobbyInfo info = SyncManager.Instance.GetLobbyInfo();
                if (!CoopLobbyPolicy.ShouldOpenVanillaNewGameSelection(
                     info.Role,
                     info.SessionActive,
                     info.LobbyActive,
                     info.Started,
                     info.Phase))
            {
                // Lobi START'ı verildikten sonra SyncManager pump'ının yaptığı
                // gerçek setGameStateGameNew çağrısı serbest bırakılmalıdır.
                return false;
            }

            if (!PublishHostVanillaSelection(force: true))
            {
                sPendingVanillaWorldStart = false;
                sError = "SON SEÇİM YAYINLANAMADI";
                CoopStatusOverlay.Notify(sError);
                return true;
            }
            CoopLobbyInfo committedInfo = SyncManager.Instance.GetLobbyInfo();
            CoopLobbySelectionView committedView = committedInfo.SelectionView;
            if (committedView.Stage != CoopSelectionStage.Location ||
                committedView.PlanetIndex != planetIndex ||
                committedView.Seed != seed)
            {
                sPendingVanillaWorldStart = false;
                sError = "VANİLLA SEÇİMİ DOĞRULANAMADI";
                CoopStatusOverlay.Notify(sError);
                return true;
            }
            string error = SyncManager.Instance.RequestLobbyStart();
            if (StringComparer.Ordinal.Equals(error, GuestSelectionNotReadyError))
            {
                sPendingVanillaWorldStart = true;
                CoopStatusOverlay.Notify("MİSAFİR SEÇİM EKRANI GÜNCELLENİYOR");
            }
            else if (!string.IsNullOrEmpty(error))
            {
                sPendingVanillaWorldStart = false;
                sError = error;
                CoopStatusOverlay.Notify(error);
            }
            else
            {
                sPendingVanillaWorldStart = false;
            }
            return true;
        }

        private static void TryCompletePendingVanillaWorldStart()
        {
            if (!sPendingVanillaWorldStart || SyncManager.Instance == null) return;
            string error = SyncManager.Instance.RequestLobbyStart();
            if (string.IsNullOrEmpty(error))
            {
                sPendingVanillaWorldStart = false;
                return;
            }
            if (StringComparer.Ordinal.Equals(error, GuestSelectionNotReadyError)) return;

            sPendingVanillaWorldStart = false;
            sError = error;
            CoopStatusOverlay.Notify(error);
        }

        internal static void OnHostVanillaSelectionExited()
        {
            ResetUiState();
        }

        private static void ResetUiState()
        {
            sMenuRequested = false;
            sJoinOpen = false;
            sError = "";
            sEntryOpenLogged = false;
            sVanillaNewGameRequested = false;
            sVanillaTransitionDepth = 0;
            sLastVanillaView = default(CoopLobbySelectionView);
            sHasLastVanillaView = false;
            sLastVanillaPublishTime = -100f;
            sPendingVanillaWorldStart = false;
            sSteamFriendPickerOpen = false;
            sSteamFriendPage = 0;
            sSteamFriends = new CoopSteamFriendInfo[0];
            sSavePickerOpen = false;
            sSavePage = 0;
            sSaveEntries = new CoopSaveEntry[0];
            CoopVanillaSelectionBridge.Reset();
        }
    }

    // ==================== BAŞLIK EKRANI CO-OP BUTONU ====================
    // Vanilya GameStateTitle.onGui sonrası, NEW GAME'in hemen altına oyunun
    // kendi GuiRenderer.renderTitleButton metoduyla AYNI font/tasarımda bir
    // CO-OP butonu çizilir. NEW GAME sonrası vanilya butonları çizim anında
    // tek satır aşağı itilir; 2. sıra CO-OP için açılır.

    [HarmonyPatch(typeof(GameStateTitle), "onGui")]
    public static class CoopTitleCoopButtonPatch
    {
        // renderTitleButton prefix'i yalnızca başlık menüsü çizimi sırasında devrede.
        internal static bool InTitleMenuGui;

        private static GuiRenderer sTitleRenderer;
        private static bool sClickLogged;

        [HarmonyPrefix]
        public static bool Prefix()
        {
            InTitleMenuGui = true;
            return !CoopLobbyUI.ShouldSuppressVanillaTitleMenu();
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            InTitleMenuGui = false;
        }

        [HarmonyPostfix]
        public static void Postfix(GameStateTitle __instance)
        {
            try
            {
                if (CoopLobbyUI.ShouldSuppressVanillaTitleMenu()) return;
                // Vanilya da Space basılıyken menü çizimini atlar; biz de atlayalım.
                if (Input.GetKey(KeyCode.Space)) return;
                if (sTitleRenderer == null) sTitleRenderer = new GuiRenderer();

                bool wasEnabled = GUI.enabled;
                GUI.enabled = true;
                try
                {
                    float rightOffset = Traverse.Create(__instance)
                        .Field<float>("mRightOffset").Value;

                    Vector2 menuLocation = Singleton<TitleScene>.getInstance().getMenuLocation();
                    Vector2 size = GuiRenderer.getMenuButtonSize(FontSize.Huge);
                    float step = size.y * 1.3f;
                    float x = menuLocation.x - size.x + rightOffset;
                    float y = menuLocation.y + step; // NEW GAME'in bir altı (2. sıra)

                    Rect rect = new Rect(x, y, size.x, size.y);
                    if (sTitleRenderer.renderTitleButton(rect, "CO-OP", FontSize.Huge))
                    {
                        if (!sClickLogged)
                        {
                            sClickLogged = true;
                            CoopPlugin.Log.LogInfo("[Coop] başlık ekranı CO-OP butonu tıklandı");
                        }
                        CoopLobbyUI.OpenMenu();
                    }
                }
                finally
                {
                    GUI.enabled = wasEnabled;
                }
            }
            catch (Exception)
            {
                // Başlık butonu asla oyun döngüsünü bozmasın.
            }
        }
    }

    // NEW GAME sonrası başlık butonlarını (CONTINUE..QUIT) tek satır aşağı iter;
    // NEW GAME yerinde kalır, aradaki 2. sıra CO-OP'a kalır.
    [HarmonyPatch(typeof(GuiRenderer), "renderTitleButton")]
    public static class CoopTitleVanillaListShiftPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref Rect rect, string text, FontSize fontSize)
        {
            try
            {
                if (!CoopTitleCoopButtonPatch.InTitleMenuGui) return;
                if (fontSize != FontSize.Huge) return;
                if (text == "CO-OP") return; // kendi butonumuza dokunma
                if (text == StringList.get("new_game")) return; // NEW GAME yerinde kalır
                Vector2 size = GuiRenderer.getMenuButtonSize(FontSize.Huge);
                rect.y += size.y * 1.3f;
            }
            catch (Exception)
            {
                // Kaydırma asla oyunu bozmasın.
            }
        }
    }

    // ==================== VANİLYA MENÜ KİLİDİ ====================
    // Lobi aktif ve host START vermeden hiçbir taraf vanilya menüden tek
    // başına dünya kuramaz (yeni oyun/continue/load/konum/challenge).

    [HarmonyPatch(typeof(GameManager), "setGameStateLocationSelection")]
    public static class CoopBlockLocationSelectionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return CoopLobbyUI.IsVanillaLocationTransitionAuthorized ||
                !SyncManager.ShouldBlockVanillaMenuTransitions();
        }
    }

    [HarmonyPatch(typeof(GameManager), "setGameStateChallengeSelection")]
    public static class CoopBlockChallengeSelectionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !SyncManager.ShouldBlockVanillaMenuTransitions();
        }
    }

    [HarmonyPatch(typeof(GameManager), "setGameStateLoadGame")]
    public static class CoopBlockLoadGamePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !SyncManager.ShouldBlockVanillaMenuTransitions();
        }
    }

    [HarmonyPatch(typeof(GameManager), "setGameStateGameNew")]
    public static class CoopBlockGameNewPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int seed, int planetIndex)
        {
            if (CoopVanillaSelectionBridge.IsGuestReadOnlySelection() &&
                !CoopVanillaSelectionBridge.AuthorizingWorldTransition)
                return false;
            if (CoopLobbyUI.TryHandleVanillaNewGameStart(seed, planetIndex))
                return false;
            return !SyncManager.ShouldBlockVanillaMenuTransitions();
        }
    }

    [HarmonyPatch(typeof(GameManager), "setGameStateTitle")]
    public static class CoopHostVanillaSelectionBackPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (CoopVanillaSelectionBridge.IsGuestReadOnlySelection() &&
                !CoopGuestSnapshotTransition.AuthorizingTitle) return false;
            if (CoopLobbyUI.IsHostVanillaSelectionState())
            {
                SyncManager.EndMenuSession();
                CoopLobbyUI.OnHostVanillaSelectionExited();
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GameManager), "setGameStateGameContinue")]
    public static class CoopBlockGameContinuePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !SyncManager.ShouldBlockVanillaMenuTransitions();
        }
    }

    [HarmonyPatch(typeof(GameManager), "setGameStateGameLoad")]
    public static class CoopBlockGameLoadPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !SyncManager.ShouldBlockVanillaMenuTransitions();
        }
    }
}
