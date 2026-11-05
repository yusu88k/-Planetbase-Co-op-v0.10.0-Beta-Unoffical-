using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Faz 4 — Partner Önizlemesi: her oyuncu diğerinin kamerasını küçük PIP kutusunda canlı izler.
    ///
    /// Ağ: kamera transform paketi (25 bayt, 10 Hz):
    ///   [4][posX:4][posY:4][posZ:4][rotX:4][rotY:4][rotZ:4]
    ///
    /// Her instance kendi kamerasını gönderir, partnerin kamerasını alır.
    /// Partner kamerası bir RenderTexture'a çizilir → IMGUI köşede kutu.
    /// </summary>
    public class PartnerPreview
    {
        public static PartnerPreview Instance;

        // Partner kamera durumu (ag thread yazar, ana thread okur)
        private Vector3 mPartnerPos;
        private Quaternion mPartnerRot;
        private float mLastUpdate;
        private bool mHasData;
        private readonly object mCameraPacketLock = new object();
        private float[] mPendingCamera;

        // Partner kamerası + RT
        private Camera mPartnerCam;
        private RenderTexture mPartnerRT;
        private bool mInit;

        // Kutu boyutları
        private const int BoxW = 320;
        private const int BoxH = 180;
        private const int Margin = 10;

        // Gönderim zamanlaması
        private int mSendCounter;
        private const int SendEveryNTicks = 5; // ~10 Hz (50Hz tick / 5)

        // Paket tipi (SyncManager ile paylaşılan)
        public const byte PktCamera = 4;

        public static void Create()
        {
            if (!SyncManager.FullMode) return;
            Instance = new PartnerPreview();
            CoopPlugin.Log.LogInfo("[Coop] PartnerPreview hazır — partner kamerası PIP kutusu");
        }

        /// <summary>Her render karesinde çağrılır.</summary>
        public static void Disable()
        {
            if (Instance != null)
            {
                Instance.Destroy();
                Instance = null;
            }
        }

        public void OnFrame()
        {
            if (!SyncManager.FullMode) return;
            try
            {
                ApplyQueuedCameraPacket();
                // Partner kamerasını başlat (ilk seferde)
                if (!mInit)
                {
                    TryInitCamera();
                }

                // Partner kamerasını partnerin transform'una konumlandır
                if (mInit && mHasData && mPartnerCam != null)
                {
                    mPartnerCam.transform.position = mPartnerPos;
                    mPartnerCam.transform.rotation = mPartnerRot;
                }

                // Gönderim zamanlaması — belirli aralıklarla kamera transform'u gönder
                mSendCounter++;
                if (mSendCounter >= SendEveryNTicks)
                {
                    mSendCounter = 0;
                    SendCameraTransform();
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] PartnerPreview hatası: " + ex.Message);
            }
        }

        /// <summary>IMGUI'da PIP kutusunu çiz.</summary>
        public void OnGUI()
        {
            if (!SyncManager.FullMode || !mInit || mPartnerRT == null || !mHasData) return;
            try
            {
                int x = Screen.width - BoxW - Margin;
                int y = Margin;

                // arka plan
                GUI.DrawTexture(new Rect(x - 2, y - 2, BoxW + 4, BoxH + 4), Texture2D.blackTexture);
                // partner görüntüsü
                GUI.DrawTexture(new Rect(x, y, BoxW, BoxH), mPartnerRT, ScaleMode.ScaleToFit, false);

                // etiket
                string label = SyncManager.Role == "host" ? "MİSAFİR KAMERASI" : "HOST KAMERASI";
                GUIStyle style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.yellow }
                };
                GUI.Label(new Rect(x + 4, y + 4, BoxW, 20), label, style);
            }
            catch (Exception)
            {
                // GUI hataları oyunu çökertmesin
            }
        }

        /// <summary>Partner kamerasını başlat.</summary>
        private void TryInitCamera()
        {
            try
            {
                mPartnerRT = new RenderTexture(BoxW, BoxH, 16)
                {
                    name = "PartnerPreview RT",
                    filterMode = FilterMode.Bilinear
                };
                mPartnerRT.Create();

                GameObject go = new GameObject("Coop Partner Camera");
                go.tag = "Untagged";
                UnityEngine.Object.DontDestroyOnLoad(go);
                mPartnerCam = go.AddComponent<Camera>();
                mPartnerCam.targetTexture = mPartnerRT;
                mPartnerCam.clearFlags = CameraClearFlags.SolidColor;
                mPartnerCam.backgroundColor = new Color(0.1f, 0.1f, 0.15f);
                mPartnerCam.cullingMask = -1; // tüm katmanlar
                mPartnerCam.fieldOfView = 45f;
                mPartnerCam.nearClipPlane = 0.3f;
                mPartnerCam.farClipPlane = 500f;
                mPartnerCam.depth = -1; // en arkada

                // Ana kameradan rendering ayarlarını kopyala
                Camera main = Camera.main;
                if (main != null)
                {
                    mPartnerCam.renderingPath = main.renderingPath;
                    mPartnerCam.allowHDR = main.allowHDR;
                }

                mInit = true;
                CoopPlugin.Log.LogInfo("[Coop] Partner kamerası kuruldu — RT " + BoxW + "x" + BoxH);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] Partner kamerası kurulamadı: " + ex.Message);
            }
        }

        /// <summary>Kendi kamera transform'umuzu partner'e gönder.</summary>
        private void SendCameraTransform()
        {
            if (SyncManager.Instance == null) return;
            try
            {
                Camera main = Camera.main;
                if (main == null) return;

                Vector3 pos = main.transform.position;
                Vector3 rot = main.transform.eulerAngles;

                // [4][posX:4][posY:4][posZ:4][rotX:4][rotY:4][rotZ:4] = 25 bayt
                byte[] pkt = new byte[1 + 12 + 12];
                pkt[0] = PktCamera;
                Buffer.BlockCopy(BitConverter.GetBytes(pos.x), 0, pkt, 1, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(pos.y), 0, pkt, 5, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(pos.z), 0, pkt, 9, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(rot.x), 0, pkt, 13, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(rot.y), 0, pkt, 17, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(rot.z), 0, pkt, 21, 4);

                SyncManager.Instance.SendPacket(pkt);
            }
            catch (Exception)
            {
                // sessizce geç
            }
        }

        /// <summary>Network thread'den çağrılabilir; yalnızca ilkel değerleri kuyruğa alır.</summary>
        public void QueueCameraPacket(byte[] packet)
        {
            if (packet == null || packet.Length < 25) return;
            try
            {
                float[] values = new float[6];
                for (int i = 0; i < values.Length; i++) values[i] = BitConverter.ToSingle(packet, 1 + i * 4);
                lock (mCameraPacketLock) mPendingCamera = values;
            }
            catch (Exception)
            {
                // sessizce geç
            }
        }

        public void OnCameraReceived(byte[] packet)
        {
            QueueCameraPacket(packet);
        }

        private void ApplyQueuedCameraPacket()
        {
            float[] values = null;
            lock (mCameraPacketLock)
            {
                if (mPendingCamera != null)
                {
                    values = mPendingCamera;
                    mPendingCamera = null;
                }
            }
            if (values == null) return;
            mPartnerPos = new Vector3(values[0], values[1], values[2]);
            mPartnerRot = Quaternion.Euler(values[3], values[4], values[5]);
            mHasData = true;
            mLastUpdate = Time.time;
        }

        /// <summary>Partner kamerasını temizle.</summary>
        public void Destroy()
        {
            if (mPartnerCam != null)
            {
                UnityEngine.Object.Destroy(mPartnerCam.gameObject);
                mPartnerCam = null;
            }
            if (mPartnerRT != null)
            {
                mPartnerRT.Release();
                mPartnerRT = null;
            }
            mInit = false;
            mHasData = false;
        }
    }
}
