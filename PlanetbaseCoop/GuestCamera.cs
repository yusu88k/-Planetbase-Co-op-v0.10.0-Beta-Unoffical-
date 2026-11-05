using System.Reflection;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Milestone 2 — İkinci bağımsız kamera ("misafir görüntüsü").
    ///
    /// Unity lifecycle'ına güvenmek yerine oyunun kendi GameBehaviour.Update/OnGUI
    /// metodlarına Harmony postfix'i ile bağlanır (Plugin.cs). Her kare kesin çalışır.
    ///
    /// Modlar:
    ///   TAKİP (varsayılan) — misafir kamera ana kamerayı birebir kopyalar.
    ///                         Güvenli test: aynı sahne, aynı görüş; araziye girmez.
    ///   MANUEL (F8)        — bağımsız kontrol: ok tuşları pan, PageUp/PageDown zoom,
    ///                         Insert/Delete döndür. (Gerçek co-op'ta bu input'lar
    ///                         ağ üzerinden gelen misafir komutları olacak.)
    ///   F6                 — "ANA KAMERAYA GİT": misafir kamerayı anında ana
    ///                         kameranın konumuna/rotasyonuna ışınlar.
    ///   F9                 — önizleme kutusunu göster/gizle.
    /// </summary>
    public class GuestCameraController
    {
        public static GuestCameraController Instance;

        private const int RTWidth = 960;
        private const int RTHeight = 540;

        private RenderTexture mRT;
        private Camera mGameCam;
        private Camera mSkyCam;
        private bool mInit;
        private bool mInitWarned;
        private bool mInitFailed;
        private float mNextInitRetry;
        private bool mManual;
        private bool mNetInit;
        private bool mVisible = true;
        private byte mPrevGuestButtons;
        private byte mPrevGuestKeys;
        private byte mPrevGuestKeys2;
        private float mNextLog;
        private Vector3 mPos;
        private Quaternion mRot;
        private float mHeight = 21f;
        private float mYaw;

        public RenderTexture GuestRenderTexture => mRT;
        public bool Initialized => mInit;

        public static void Create()
        {
            Instance = new GuestCameraController();
            CoopPlugin.Log.LogInfo("[Coop] GuestCameraController hazır — ikinci kamera ana kamera bulununca kurulacak");
        }

        /// <summary>Her kare: GameBehaviour.Update postfix'inden çağrılır. ASLA istisna fırlatmaz.</summary>
        public void Tick()
        {
            // FULL SENKRON modunda eski video/ikinci kamera sistemi kapalı —
            // herkes kendi oyununda oynuyor, partner önizlemesi Faz 4'te gelecek.
            if (SyncManager.FullMode)
            {
                return;
            }
            try
            {
                TickInner();
            }
            catch (System.Exception ex)
            {
                // oyunu asla çökertme — logla ve devam et
                CoopPlugin.Log.LogError("[Coop] Tick hatası (mod çalışmaya devam ediyor): " + ex);
            }
        }

        private void TickInner()
        {
            if (!mInit)
            {
                TryInit();
                if (!mInit && Time.time >= mNextLog)
                {
                    mNextLog = Time.time + 5f;
                    CoopPlugin.Log.LogInfo(string.Format(
                        "[Coop] bekleniyor: Camera.main={0}, kamera sayısı={1}",
                        Camera.main != null ? Camera.main.name : "yok",
                        Object.FindObjectsOfType<Camera>().Length));
                }
                return;
            }

            // kurulum başarısız olduysa bir daha deneme
            if (mInitFailed)
            {
                return;
            }

            // kameralar kaybolduysa (sahne değişimi vb.) kendini iyileştir: yeniden kur
            if (mGameCam == null || mSkyCam == null)
            {
                if (Time.time >= mNextInitRetry)
                {
                    mNextInitRetry = Time.time + 2f;
                    if (!mInitWarned)
                    {
                        mInitWarned = true;
                        CoopPlugin.Log.LogWarning(string.Format(
                            "[Coop] misafir kameraları kayboldu — yeniden kuruluyor... (t={0:F1}, sahnedeki kamera sayısı={1})",
                            Time.time, Object.FindObjectsOfType<Camera>().Length));
                    }
                    mInit = false;
                }
                return;
            }
            mInitWarned = false;

            SyncSettings();

            // NOT: F1-F10 oyunun kendi kısayolları (paneller, hızlı kayıt). Çakışma olmasın
            // diye mod tuşları F11/F12 ve harfler. F12=manuel, P=önizleme, G=ana kameraya git, F11=host.
            if (Input.GetKeyDown(KeyCode.F12))
            {
                mManual = !mManual;
                Camera mc = Camera.main ?? FindMainCamera();
                if (mc != null && mManual)
                {
                    // manuel moda geçerken ana kameranın yüksekliğinden başla (araziye girmesin)
                    mPos = mc.transform.position;
                    mRot = mc.transform.rotation;
                    mYaw = mc.transform.eulerAngles.y;
                    mHeight = Mathf.Clamp(mc.transform.position.y, 12f, 40f);
                }
                CoopPlugin.Log.LogInfo("[Coop] Misafir kamera modu: " + (mManual ? "MANUEL (bağımsız)" : "TAKİP (ana kamera)"));
            }
            if (Input.GetKeyDown(KeyCode.P))
            {
                mVisible = !mVisible;
                CoopPlugin.Log.LogInfo("[Coop] Önizleme kutusu: " + (mVisible ? "görünür" : "gizli"));
            }
            if (Input.GetKeyDown(KeyCode.G))
            {
                SnapToMain();
            }
            if (Input.GetKeyDown(KeyCode.F11))
            {
                if (NetHost.Instance != null && NetHost.Instance.IsHosting)
                {
                    NetHost.Instance.StopHost();
                }
                else if (NetHost.Instance != null)
                {
                    NetHost.Instance.StartHost();
                }
            }

            Camera mainCam = Camera.main ?? FindMainCamera();
            if (mainCam == null)
            {
                return;
            }

            bool netActive = NetHost.Instance != null && NetHost.Instance.HasGuest;
            string mode;
            if (netActive)
            {
                // AĞ: misafir kamerayı misafirin input'u sürer
                if (!mNetInit)
                {
                    // misafir bağlanınca ana kameranın konumundan başla
                    mPos = mainCam.transform.position;
                    mRot = mainCam.transform.rotation;
                    mYaw = mainCam.transform.eulerAngles.y;
                    mHeight = Mathf.Clamp(mainCam.transform.position.y, 12f, 40f);
                    mNetInit = true;
                    CoopPlugin.Log.LogInfo("[Coop] AĞ modu: misafir kamerayı misafir kontrol ediyor");
                }
                ApplyNetworkInput();
                HandleGuestActions();
                mode = "ağ";
            }
            else
            {
                mNetInit = false;
                if (!mManual)
                {
                    // TAKİP: ana kamerayı birebir kopyala — her zaman geçerli bir konum
                    mPos = mainCam.transform.position;
                    mRot = mainCam.transform.rotation;
                    mode = "takip";
                }
                else
                {
                    ManualControl();
                    mode = "manuel";
                }
            }

            mGameCam.transform.position = mPos;
            mGameCam.transform.rotation = mRot;
            mSkyCam.transform.position = mPos;
            mSkyCam.transform.rotation = mRot;

            // video karesini ağ katmanına ver (misafir bağlıysa)
            if (NetHost.Instance != null)
            {
                NetHost.Instance.CaptureFrame();
            }

            // durum kanıtı: her 3 sn'de bir log
            if (Time.time >= mNextLog)
            {
                mNextLog = Time.time + 3f;
                CoopPlugin.Log.LogInfo(string.Format(
                    "[Coop] misafir kamera pos={0} rot={1} mod={2}",
                    mPos.ToString("F1"), mRot.eulerAngles.ToString("F0"), mode));
            }
        }

        /// <summary>Misafir kameranın RT'sindeki misafir imlecinden dünya ışını üretir.</summary>
        public Ray GetGuestRay()
        {
            if (mGameCam == null || NetHost.Instance == null)
            {
                return default(Ray);
            }
            NetHost net = NetHost.Instance;
            Vector3 screenPoint = new Vector3(
                Mathf.Clamp(net.MouseU, 0f, 1f) * RTWidth,
                Mathf.Clamp(net.MouseV, 0f, 1f) * RTHeight, 0f);
            return mGameCam.ScreenPointToRay(screenPoint);
        }

        /// <summary>AĞ modu: misafirin gönderdiği pan/zoom/rotate değerleriyle kamerayı sürer.</summary>
        private void ApplyNetworkInput()
        {
            NetHost net = NetHost.Instance;
            float dt = Time.deltaTime;
            float pan = 40f * dt;
            Vector3 fwd = Quaternion.Euler(0f, mYaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, mYaw, 0f) * Vector3.right;

            mPos += fwd * net.PanZ * pan;
            mPos += right * net.PanX * pan;
            mHeight = Mathf.Clamp(mHeight + net.Zoom * 12f * dt + net.Wheel * 6f, 12f, 40f);
            mYaw += net.Rotate * 40f * dt;
            mPos.y = mHeight;
            mRot = Quaternion.Euler(25f, mYaw, 0f);
        }

        /// <summary>AĞ modu: misafirin tıklama ve kısayol aksiyonlarını uygular.</summary>
        private void HandleGuestActions()
        {
            NetHost net = NetHost.Instance;

            // inşaat modu durumunu izle (oyun çıkardıysa sıfırla)
            GuestPlacement.UpdateState();

            byte curButtons = net.Buttons;
            byte prevButtons = mPrevGuestButtons;
            mPrevGuestButtons = curButtons;

            // sol tık (yükselen kenar) → yerleştirme veya seçim
            if ((curButtons & NetHost.BtnLeft) != 0 && (prevButtons & NetHost.BtnLeft) == 0)
            {
                if (GuestPlacement.Active)
                {
                    GuestPlacement.GuestLeftClick();
                }
                else
                {
                    GuestSelect();
                }
            }
            // sağ tık (yükselen kenar) → yerleştirmeyi iptal
            if ((curButtons & NetHost.BtnRight) != 0 && (prevButtons & NetHost.BtnRight) == 0)
            {
                if (GuestPlacement.Active)
                {
                    GuestPlacement.GuestRightClick();
                }
            }

            // tuş kısayolları (yükselen kenar)
            byte keys = net.Keys;
            byte rising = (byte)(keys & ~mPrevGuestKeys);
            mPrevGuestKeys = keys;
            byte keys2 = net.Keys2;
            byte rising2 = (byte)(keys2 & ~mPrevGuestKeys2);
            mPrevGuestKeys2 = keys2;

            Planetbase.TimeManager tm = Planetbase.Singleton<Planetbase.TimeManager>.getInstance();
            if (tm == null)
            {
                return;
            }
            if ((rising & NetHost.KeyPause) != 0)
            {
                if (tm.isPaused()) tm.unpause(); else tm.pause();
                CoopPlugin.Log.LogInfo("[Coop] misafir: " + (tm.isPaused() ? "duraklattı" : "devam etti"));
            }
            if ((rising & NetHost.KeySpeedUp) != 0) { tm.increaseSpeed(); CoopPlugin.Log.LogInfo("[Coop] misafir: hız arttı"); }
            if ((rising & NetHost.KeySpeedDown) != 0) { tm.decreaseSpeed(); CoopPlugin.Log.LogInfo("[Coop] misafir: hız azaldı"); }
            if ((rising & NetHost.KeyNormalSpeed) != 0) { tm.setNormalSpeed(); CoopPlugin.Log.LogInfo("[Coop] misafir: normal hız"); }

            // --- inşaat ve aksiyonlar ---
            if ((rising & NetHost.KeyBuild) != 0) GuestPlacement.ToggleBuild();
            if ((rising & NetHost.KeyPrev) != 0) GuestPlacement.Cycle(-1);
            if ((rising & NetHost.KeyNext) != 0) GuestPlacement.Cycle(1);
            if ((rising & NetHost.KeyRecycle) != 0) RecycleSelected();
            if ((rising2 & NetHost.Key2Roofs) != 0) ToggleRoofs();
            if ((rising2 & NetHost.Key2Lock) != 0) ToggleLockSelected();
            if ((rising2 & NetHost.Key2Component) != 0) GuestPlacement.ToggleComponent();
            if ((rising2 & NetHost.Key2Disable) != 0) ToggleDisableSelected();
        }

        /// <summary>X: seçili objeyi geri dönüştür/sil.</summary>
        private static void RecycleSelected()
        {
            Planetbase.Selectable sel = Planetbase.Selection.getSelected();
            if (sel == null)
            {
                return;
            }
            bool enabled = false;
            if (Planetbase.Selection.isDeleteable(out enabled) && enabled)
            {
                Planetbase.Selection.deleteSelected();
                CoopPlugin.Log.LogInfo("[Coop] misafir: geri dönüşüm — " + sel.GetType().Name);
            }
        }

        /// <summary>T: çatıları göster/gizle.</summary>
        private static void ToggleRoofs()
        {
            GameStateGame g = GameManager.getInstance().getGameState() as GameStateGame;
            if (g != null)
            {
                g.onButtonRenderTops(null);
                CoopPlugin.Log.LogInfo("[Coop] misafir: çatılar değiştirildi");
            }
        }

        /// <summary>L: seçili yapıyı kilitle/aç.</summary>
        private static void ToggleLockSelected()
        {
            Planetbase.Selectable sel = Planetbase.Selection.getSelected();
            Planetbase.Construction c = sel as Planetbase.Construction;
            if (c != null)
            {
                c.setLocked(!c.isLocked());
                CoopPlugin.Log.LogInfo("[Coop] misafir: kilit " + (c.isLocked() ? "açıldı" : "kapatıldı"));
            }
        }

        /// <summary>K: seçili yapıyı devre dışı bırak/aç.</summary>
        private static void ToggleDisableSelected()
        {
            Planetbase.Selectable sel = Planetbase.Selection.getSelected();
            Planetbase.Construction c = sel as Planetbase.Construction;
            if (c != null)
            {
                c.setEnabled(!c.isEnabled());
                CoopPlugin.Log.LogInfo("[Coop] misafir: devre " + (c.isEnabled() ? "açıldı" : "kapatıldı"));
            }
        }

        /// <summary>
        /// Misafirin tıkladığı noktayı misafir kamerasından ışınlayıp dünya objesi seçer.
        /// Oyunun kendi updateSelection() mantığının misafir kamerasına uyarlanmış hali.
        /// </summary>
        private void GuestSelect()
        {
            NetHost net = NetHost.Instance;
            if (mGameCam == null)
            {
                return;
            }

            // misafir kamerasının ışını
            Ray ray = GetGuestRay();

            Selectable best = null;
            float bestDist = float.PositiveInfinity;

            Planetbase.Character character = Planetbase.Character.selectionCast(ray, out float dist);
            if (character != null && dist < bestDist) { best = character; bestDist = dist; }

            Planetbase.ConstructionComponent comp = Planetbase.ConstructionComponent.selectionCastAll(ray, out dist);
            if (comp != null && dist < bestDist) { best = comp; bestDist = dist; }

            if (Physics.Raycast(ray, out RaycastHit hit, float.PositiveInfinity, 4267008) && hit.collider != null)
            {
                GameObject root = hit.collider.gameObject.transform.root.gameObject;
                Planetbase.Ship ship = Planetbase.Ship.find(root);
                if (ship != null && hit.distance < bestDist) { best = ship; bestDist = hit.distance; }

                Planetbase.Construction cons = Planetbase.Construction.find(root);
                if (cons != null && hit.distance < bestDist) { best = cons; bestDist = hit.distance; }

                Transform parent = hit.collider.gameObject.transform.parent;
                if (parent != null)
                {
                    Planetbase.Resource res = Planetbase.Resource.find(parent.gameObject);
                    if (res != null && hit.distance < bestDist) { best = res; bestDist = hit.distance; }
                }
            }

            if (best != null)
            {
                if (best.isSelected())
                {
                    Planetbase.Selection.clear();
                    CoopPlugin.Log.LogInfo("[Coop] misafir seçimi temizledi");
                }
                else
                {
                    Planetbase.Selection.select(best);
                    CoopPlugin.Log.LogInfo("[Coop] misafir seçti: " + best.GetType().Name);
                }
            }
            else
            {
                Planetbase.Selection.clear();
                CoopPlugin.Log.LogInfo("[Coop] misafir boş yere tıkladı — seçim temizlendi");
            }
        }

        /// <summary>F6: misafir kamerayı anında ana kameranın konumuna ışınla.</summary>
        private void SnapToMain()
        {
            Camera mainCam = Camera.main ?? FindMainCamera();
            if (mainCam == null)
            {
                return;
            }
            mPos = mainCam.transform.position;
            mRot = mainCam.transform.rotation;
            mYaw = mainCam.transform.eulerAngles.y;
            mHeight = Mathf.Clamp(mainCam.transform.position.y, 12f, 40f);
            CoopPlugin.Log.LogInfo("[Coop] G — misafir kamera ana kameraya ışınlandı: " + mPos.ToString("F1"));
        }

        /// <summary>Ana kamera hazır olduğunda ikinci kamera çiftini kurar.</summary>
        /// <remarks>
        /// NOT: Object.Instantiate(mainCam) YAPMIYORUZ — klonlanan kamera hiyerarşisi
        /// oyun tarafından sürekli yok ediliyor (sürekli KURULDU döngüsü + Tick çökmesi).
        /// Bunun yerine TAZE GameObject + AddComponent&lt;Camera&gt; (Milestone 2'de kanıtlanmış
        /// stabil yaklaşım) kullanıyoruz ve görsel efekt bileşenlerini (MadrugaScattering,
        /// Antialiasing vb.) reflection ile ana kameradan kopyalıyoruz.
        /// </remarks>
        private void TryInit()
        {
            try
            {
                Camera mainCam = FindMainCamera();
                if (mainCam == null)
                {
                    return;
                }

                mRT = new RenderTexture(RTWidth, RTHeight, 24) { name = "Coop Guest RT" };
                mRT.filterMode = FilterMode.Bilinear;
                mRT.Create();

                // TAZE GameObject + AddComponent<Camera> — oyun prefab'larından bağımsız
                GameObject guestGo = new GameObject("Coop Guest Game Cam");
                guestGo.tag = "Untagged"; // Camera.main'ı bozma
                Object.DontDestroyOnLoad(guestGo); // sahne geçişlerinde yok olmasın
                mGameCam = guestGo.AddComponent<Camera>();
                mGameCam.CopyFrom(mainCam); // tüm kamera ayarlarını kopyala
                mGameCam.targetTexture = mRT;

                // görsel efekt bileşenlerini ana kameradan kopyala (MadrugaScattering,
                // Antialiasing vb.) — misafir görüntüsü ana ekranla aynı görünsün
                CopyEffectComponents(mainCam.gameObject, guestGo);

                // gökyüzü kamerası: taze GameObject + ayarlar her karede senkronlanır
                GameObject skySrc = GameObject.Find("Skydome Camera");
                GameObject guestSky = new GameObject("Coop Guest Sky Cam");
                guestSky.tag = "Untagged";
                Object.DontDestroyOnLoad(guestSky);
                mSkyCam = guestSky.AddComponent<Camera>();
                if (skySrc != null)
                {
                    Camera src = skySrc.GetComponent<Camera>();
                    if (src != null)
                    {
                        mSkyCam.CopyFrom(src);
                        CopyEffectComponents(skySrc, guestSky);
                    }
                }
                mSkyCam.targetTexture = mRT;

                // misafir, ana kameranın bulunduğu yerden başlar (takip modu)
                mPos = mainCam.transform.position;
                mRot = mainCam.transform.rotation;
                mInit = true;

                // ağ katmanını otomatik başlat
                if (NetHost.Instance != null)
                {
                    NetHost.Instance.StartHost();
                }

                CoopPlugin.Log.LogInfo(string.Format(
                    "[Coop] İkinci kamera KURULDU — RT {0}x{1}, ana kamera: {2} (taze GameObject + efekt kopyası)",
                    RTWidth, RTHeight, mainCam.name));
                CoopPlugin.Log.LogInfo("[Coop] Modlar: TAKİP (varsayılan) | F12=manuel bağımsız | G=ana kameraya git | P=önizlemeyi gizle | F11=host aç/kapat");
                CoopPlugin.Log.LogInfo("[Coop] Manuel tuşlar: I/J/K/L pan, U/O döndür, PageUp/PageDown zoom. (F1-F10 oyunun kendi tuşları, çakışma yok)");
            }
            catch (System.Exception ex)
            {
                // kurulum başarısız — bir daha deneme (log ile)
                mInitFailed = true;
                CoopPlugin.Log.LogError("[Coop] misafir kamera kurulumu başarısız: " + ex);
            }
        }

        /// <summary>
        /// Kaynak GameObject'teki görsel efekt bileşenlerini (Camera/AudioListener/Transform
        /// hariç) hedefe kopyalar. Alanlar reflection ile taşınır — shader ve malzeme
        /// referansları dahil. Böylece misafir kamerada MadrugaScattering (atmosfer),
        /// Antialiasing gibi efektler de çalışır.
        /// </summary>
        private static void CopyEffectComponents(GameObject src, GameObject dst)
        {
            foreach (Component c in src.GetComponents<Component>())
            {
                if (c is Transform || c is Camera || c is AudioListener || c == null)
                {
                    continue;
                }
                try
                {
                    Component copy = dst.AddComponent(c.GetType());
                    foreach (System.Reflection.FieldInfo f in c.GetType().GetFields(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance))
                    {
                        if (!f.IsLiteral && !f.IsInitOnly)
                        {
                            f.SetValue(copy, f.GetValue(c));
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    CoopPlugin.Log.LogWarning("[Coop] efekt bileşeni kopyalanamadı (" + c.GetType().Name + "): " + ex.Message);
                }
            }
        }

        private static Camera FindMainCamera()
        {
            // 1) Unity'nin MainCamera etiketi
            Camera c = Camera.main;
            if (c != null)
            {
                return c;
            }

            // 2) oyunun CameraManager'ının özel alanı (deterministik)
            try
            {
                Planetbase.CameraManager cm = Planetbase.CameraManager.getInstance();
                if (cm != null)
                {
                    var f = typeof(Planetbase.CameraManager).GetField(
                        "mMainCameraComponent",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null)
                    {
                        Camera fromManager = f.GetValue(cm) as Camera;
                        if (fromManager != null)
                        {
                            return fromManager;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] CameraManager reflection başarısız: " + ex.Message);
            }

            // 3) fallback: oyundaki ana kamera "Main Camera (...)" adıyla oluşturulur
            foreach (Camera cam in Object.FindObjectsOfType<Camera>())
            {
                if (cam.name.StartsWith("Main Camera"))
                {
                    return cam;
                }
            }
            return null;
        }

        /// <summary>Ana kameranın parametrelerini ve efekt durumlarını her karede senkronlar.</summary>
        private void SyncSettings()
        {
            Camera mainCam = Camera.main ?? FindMainCamera();
            if (mainCam == null)
            {
                return;
            }

            mGameCam.clearFlags = mainCam.clearFlags;
            mGameCam.cullingMask = mainCam.cullingMask;
            mGameCam.fieldOfView = mainCam.fieldOfView;
            mGameCam.nearClipPlane = mainCam.nearClipPlane;
            mGameCam.farClipPlane = mainCam.farClipPlane;
            mGameCam.renderingPath = mainCam.renderingPath;
            mGameCam.allowHDR = mainCam.allowHDR;
            mGameCam.useOcclusionCulling = mainCam.useOcclusionCulling;
            mGameCam.layerCullSpherical = mainCam.layerCullSpherical;
            if (mainCam.layerCullDistances != null && mainCam.layerCullDistances.Length == 32)
            {
                mGameCam.layerCullDistances = mainCam.layerCullDistances;
            }

            // görsel efektlerin açık/kapalı durumunu ana kamerayla senkronla
            // (ör. MadrugaScattering başlık ekranında kapalı, oyunda açık)
            SyncComponentStates(mainCam.gameObject, mGameCam.gameObject);

            // gökyüzü kamerası: oyundaki "Skydome Camera" ayarlarının kopyası
            var skyGo = GameObject.Find("Skydome Camera");
            if (skyGo != null && mSkyCam != null)
            {
                Camera src = skyGo.GetComponent<Camera>();
                mSkyCam.cullingMask = src.cullingMask;
                mSkyCam.depth = src.depth;
                mSkyCam.clearFlags = src.clearFlags;
                mSkyCam.nearClipPlane = src.nearClipPlane;
                mSkyCam.farClipPlane = src.farClipPlane;
                SyncComponentStates(skyGo, mSkyCam.gameObject);
            }
        }

        /// <summary>Kaynak objedeki Behaviour'ların enabled durumunu hedefe kopyalar.</summary>
        private static void SyncComponentStates(GameObject src, GameObject dst)
        {
            foreach (Behaviour b in src.GetComponents<Behaviour>())
            {
                if (b is Camera || b is AudioListener)
                {
                    continue;
                }
                Behaviour d = dst.GetComponent(b.GetType()) as Behaviour;
                if (d != null)
                {
                    d.enabled = b.enabled;
                }
            }
        }

        /// <summary>
        /// Manuel mod (host tek başına test ederken): I/J/K/L pan, U/O döndür, PageUp/PageDown zoom.
        /// NOT: ok tuşları/Delete oyunda başka işlevlere bağlı olduğu için kullanılmaz.
        /// </summary>
        private void ManualControl()
        {
            float pan = 40f * Time.deltaTime;
            Vector3 fwd = Quaternion.Euler(0f, mYaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, mYaw, 0f) * Vector3.right;

            if (Input.GetKey(KeyCode.I)) mPos += fwd * pan;
            if (Input.GetKey(KeyCode.K)) mPos -= fwd * pan;
            if (Input.GetKey(KeyCode.L)) mPos += right * pan;
            if (Input.GetKey(KeyCode.J)) mPos -= right * pan;
            if (Input.GetKey(KeyCode.PageUp)) mHeight = Mathf.Clamp(mHeight + 15f * Time.deltaTime, 12f, 40f);
            if (Input.GetKey(KeyCode.PageDown)) mHeight = Mathf.Clamp(mHeight - 15f * Time.deltaTime, 12f, 40f);
            if (Input.GetKey(KeyCode.U)) mYaw += 40f * Time.deltaTime;
            if (Input.GetKey(KeyCode.O)) mYaw -= 40f * Time.deltaTime;

            mPos.y = mHeight;
            mRot = Quaternion.Euler(25f, mYaw, 0f);
        }

        /// <summary>Her kare: GameBehaviour.OnGUI postfix'inden çağrılır. ASLA istisna fırlatmaz.</summary>
        public void DrawGUI()
        {
            // FULL SENKRON modunda eski MİSAFİR GÖRÜNTÜSÜ kutusu kapalı (Faz 4'te partner önizlemesi)
            if (SyncManager.FullMode)
            {
                return;
            }
            try
            {
                DrawGuiInner();
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogError("[Coop] GUI çizim hatası: " + ex.Message);
            }
        }

        private void DrawGuiInner()
        {
            if (!mVisible || mRT == null || !mInit)
            {
                return;
            }

            // oyunun kendi GUI matrix'inden etkilenmemek için sıfırla
            GUI.matrix = Matrix4x4.identity;

            // sağ alt köşe — HUD panellerinin üstüne binmesin
            float w = 320f;
            float h = w * (RTHeight / (float)RTWidth);
            float x = Screen.width - w - 12f;
            float y = Screen.height - h - 12f;

            GUI.Box(new Rect(x, y, w, h), GUIContent.none);
            GUI.DrawTexture(new Rect(x, y, w, h), mRT, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(x + 4f, y - 40f, w - 8f, 40f),
                "<b>MİSAFİR GÖRÜNTÜSÜ</b>\nF12=manuel  G=ana kameraya git  P=gizle  F11=host");

            // host durumu
            if (NetHost.Instance != null)
            {
                string hostStatus = NetHost.Instance.IsHosting
                    ? "HOST: " + NetHost.Instance.LocalIp + ":" + NetHost.Port + " — " +
                      (NetHost.Instance.HasGuest ? "<color=#7CFC00>MİSAFİR BAĞLI</color>" : "misafir bekleniyor")
                    : "HOST KAPALI (F7 ile aç)";
                string streamStatus = NetHost.Instance.HasGuest
                    ? "akış: " + NetHost.Instance.CaptureFps.ToString("F0") + " FPS"
                    : "";
                string placeStatus = GuestPlacement.Active
                    ? "INŞAAT: " + GuestPlacement.CurrentTypeName + " (sol tık=yerleştir, sağ tık=iptal, ,/.=tür)"
                    : "";
                GUI.Label(new Rect(x, y + h + 2f, w + 200f, 20f), hostStatus + (streamStatus.Length > 0 ? "  |  " + streamStatus : ""));
                if (placeStatus.Length > 0)
                {
                    GUI.Label(new Rect(x, y + h + 20f, w + 200f, 20f), placeStatus);
                }
            }
        }
    }
}
