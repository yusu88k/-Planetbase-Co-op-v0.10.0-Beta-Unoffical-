using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Milestone 3 — Ağ katmanı (LAN).
    ///
    /// HOST tarafı (oyun içinde çalışır):
    ///   - TCP dinleyici (port 5555), misafir bağlanınca:
    ///       * GuestRenderTexture'tan JPEG kareleri alıp misafire gönderir
    ///       * Misafirden gelen kamera input komutlarını (pan/zoom/rotate) okur
    ///   - Video:  [4 bayt uzunluk][JPEG verisi]  (host -> misafir)
    ///   - Input:  [1 bayt tip=1][panX][panZ][zoom][rotate]  (misafir -> host, 17 bayt)
    ///
    /// Ağ işlemleri arka plan thread'lerinde; RenderTexture erişimi ana thread'de
    /// (CaptureFrame, GuestCameraController.Tick'ten çağrılır).
    /// </summary>
    public class NetHost
    {
        public static NetHost Instance;

        public const int Port = 5555;
        private const float CaptureFpsTarget = 20f;
        private const int MaxQueuedFrames = 6;
        private const int JpegQuality = 85;
        private const int PingPacketSize = 2;

        private TcpListener mListener;
        private Thread mAcceptThread;
        private volatile TcpClient mClient;
        private volatile bool mRunning;
        private readonly ConcurrentQueue<byte[]> mFrames = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> mPingEchoes = new ConcurrentQueue<byte[]>();
        private Texture2D mCaptureTex;
        private float mNextCapture;
        private bool mLoggedInput;

        // misafir input durumu (recv thread yazar, ana thread okur)
        public volatile float PanX;
        public volatile float PanZ;
        public volatile float Zoom;
        public volatile float Rotate;
        public volatile float MouseU;      // 0..1 (soldan sağa)
        public volatile float MouseV;      // 0..1 (üstten alta)
        public volatile byte Buttons;      // bit0=sol, bit1=sağ, bit2=orta
        public volatile sbyte Wheel;       // tekerlek delta
        public volatile byte Keys;         // kamera/oyun tuşları
        public volatile byte Keys2;        // inşaat/aksiyon tuşları
        public volatile bool HasGuest;
        private long mLastInputTicks;

        // tuş bitleri (Keys)
        public const byte KeyPause = 0x01;      // Boşluk: duraklat/devam
        public const byte KeySpeedUp = 0x02;    // 1: hız+
        public const byte KeySpeedDown = 0x04;  // 2: hız-
        public const byte KeyNormalSpeed = 0x08; // 3: normal hız
        public const byte KeyBuild = 0x10;      // B: inşaat modu
        public const byte KeyPrev = 0x20;       // ,: önceki tür
        public const byte KeyNext = 0x40;       // .: sonraki tür
        public const byte KeyRecycle = 0x80;    // X: geri dönüşüm/sil

        // tuş bitleri (Keys2)
        public const byte Key2Roofs = 0x01;     // T: çatılar
        public const byte Key2Lock = 0x02;      // L: kilitle
        public const byte Key2Component = 0x04; // C: bileşen yerleştir
        public const byte Key2Disable = 0x08;   // K: devre dışı/aç

        public const byte BtnLeft = 0x01;
        public const byte BtnRight = 0x02;
        public const byte BtnMiddle = 0x04;

        // istatistik (ana thread'de okunur)
        public volatile float CaptureFps;
        private int mFramesLastSecond;
        private float mFpsTimer;

        public bool IsHosting => mRunning;
        public string LocalIp { get; private set; } = "127.0.0.1";

        public static void Create()
        {
            Instance = new NetHost();
        }

        public void StartHost()
        {
            // FULL SENKRON modunda eski video akışı devre dışı — senkron ayrı kanaldan (SyncManager)
            if (SyncManager.FullMode)
            {
                return;
            }
            if (mRunning)
            {
                return;
            }
            try
            {
                LocalIp = FindLocalIp();
                mListener = new TcpListener(IPAddress.Any, Port);
                mListener.Start();
                mRunning = true;
                mAcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CoopAccept" };
                mAcceptThread.Start();
                CoopPlugin.Log.LogInfo("[Coop] HOST başladı: " + LocalIp + ":" + Port);
                CoopPlugin.Log.LogInfo("[Coop] Tüm adresler: " + string.Join(", ", ListAllIps()) + " — misafir LAN'daki adrese bağlansın");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] HOST başlatılamadı: " + ex.Message);
            }
        }

        public void StopHost()
        {
            mRunning = false;
            try { mListener?.Stop(); } catch { }
            try { mClient?.Close(); } catch { }
            mClient = null;
            HasGuest = false;
            CoopPlugin.Log.LogInfo("[Coop] HOST kapatıldı");
        }

        private void AcceptLoop()
        {
            while (mRunning)
            {
                try
                {
                    TcpClient client = mListener.AcceptTcpClient();
                    if (mClient != null)
                    {
                        try { mClient.Close(); } catch { }
                    }
                    mClient = client;
                    client.NoDelay = true;
                    HasGuest = true;
                    mLoggedInput = false;
                    CoopPlugin.Log.LogInfo("[Coop] MİSAFİR BAĞLANDI: " + client.Client.RemoteEndPoint);

                    var send = new Thread(SendLoop) { IsBackground = true, Name = "CoopSend" };
                    send.Start();
                    var recv = new Thread(RecvLoop) { IsBackground = true, Name = "CoopRecv" };
                    recv.Start();
                }
                catch (Exception ex)
                {
                    if (mRunning)
                    {
                        CoopPlugin.Log.LogWarning("[Coop] bağlantı kabul hatası: " + ex.Message);
                    }
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// Tüm ağ yazımları tek thread'den (bu metod) yapılır — karışma olmaz.
        /// Format: [1 bayt tip][veri]
        ///   tip 0 = video: [0][4 bayt uzunluk][JPEG]
        ///   tip 1 = ping yankısı: [1][1 bayt seq]
        /// </summary>
        private void SendLoop()
        {
            TcpClient c = mClient;
            try
            {
                NetworkStream stream = c.GetStream();
                byte[] lenBuf = new byte[4];
                while (mRunning && c.Connected && ReferenceEquals(mClient, c))
                {
                    // ping yankıları önce (küçük paketler)
                    if (mPingEchoes.TryDequeue(out byte[] echo))
                    {
                        stream.WriteByte(1);
                        stream.Write(echo, 0, echo.Length);
                        continue;
                    }
                    if (mFrames.TryDequeue(out byte[] jpg))
                    {
                        stream.WriteByte(0);
                        lenBuf[0] = (byte)jpg.Length;
                        lenBuf[1] = (byte)(jpg.Length >> 8);
                        lenBuf[2] = (byte)(jpg.Length >> 16);
                        lenBuf[3] = (byte)(jpg.Length >> 24);
                        stream.Write(lenBuf, 0, 4);
                        stream.Write(jpg, 0, jpg.Length);
                    }
                    else
                    {
                        Thread.Sleep(4);
                    }
                }
            }
            catch (Exception)
            {
                // bağlantı kapandı — normal
            }
            finally
            {
                OnGuestLeft(c);
            }
        }

        private void RecvLoop()
        {
            TcpClient c = mClient;
            try
            {
                NetworkStream stream = c.GetStream();
                byte[] buf = new byte[256];
                byte[] acc = new byte[29];
                int accLen = 0;
                while (mRunning && c.Connected && ReferenceEquals(mClient, c))
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        break;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        acc[accLen++] = buf[i];
                        // paket boyutu ilk bayttan belli: 1=17, 2=28, 3=29, 4=ping(2)
                        int expected;
                        if (acc[0] == 2) expected = 28;
                        else if (acc[0] == 3) expected = 29;
                        else if (acc[0] == 4) expected = PingPacketSize;
                        else expected = 17;
                        if (accLen >= expected)
                        {
                            if (acc[0] == 4)
                            {
                                // ping: yankıyı gönderim kuyruğuna koy (tek thread'den yazılsın)
                                if (mPingEchoes.Count < 4)
                                {
                                    mPingEchoes.Enqueue(new[] { acc[1] });
                                }
                            }
                            else
                            {
                                ParseInput(acc, expected);
                            }
                            accLen = 0;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // bağlantı kapandı — normal
            }
            finally
            {
                OnGuestLeft(c);
            }
        }

        private void ParseInput(byte[] pkt, int length)
        {
            if (pkt[0] == 3 && length >= 29)
            {
                // v3: v2 + ikinci tuş baytı (inşaat/aksiyon)
                PanX = BitConverter.ToSingle(pkt, 1);
                PanZ = BitConverter.ToSingle(pkt, 5);
                Zoom = BitConverter.ToSingle(pkt, 9);
                Rotate = BitConverter.ToSingle(pkt, 13);
                MouseU = Mathf.Clamp01(BitConverter.ToSingle(pkt, 17));
                MouseV = Mathf.Clamp01(BitConverter.ToSingle(pkt, 21));
                Buttons = pkt[25];
                Wheel = (sbyte)pkt[26];
                Keys = pkt[27];
                Keys2 = pkt[28];
                mLastInputTicks = DateTime.UtcNow.Ticks;
                if (!mLoggedInput)
                {
                    mLoggedInput = true;
                    CoopPlugin.Log.LogInfo("[Coop] misafir input akışı başladı (v3) — mouse=" + MouseU.ToString("F2") + "," + MouseV.ToString("F2"));
                }
            }
            else if (pkt[0] == 2 && length >= 28)
            {
                // v2: kamera + mouse + buton + tekerlek + tuşlar
                PanX = BitConverter.ToSingle(pkt, 1);
                PanZ = BitConverter.ToSingle(pkt, 5);
                Zoom = BitConverter.ToSingle(pkt, 9);
                Rotate = BitConverter.ToSingle(pkt, 13);
                MouseU = Mathf.Clamp01(BitConverter.ToSingle(pkt, 17));
                MouseV = Mathf.Clamp01(BitConverter.ToSingle(pkt, 21));
                Buttons = pkt[25];
                Wheel = (sbyte)pkt[26];
                Keys = pkt[27];
                mLastInputTicks = DateTime.UtcNow.Ticks;
                if (!mLoggedInput)
                {
                    mLoggedInput = true;
                    CoopPlugin.Log.LogInfo("[Coop] misafir input akışı başladı (v2) — panX=" + PanX.ToString("F1") +
                                           " panZ=" + PanZ.ToString("F1") + " zoom=" + Zoom.ToString("F1") +
                                           " rotate=" + Rotate.ToString("F1") + " mouse=" + MouseU.ToString("F2") + "," + MouseV.ToString("F2"));
                }
            }
            else if (pkt[0] == 1 && length >= 17)
            {
                // v1: sadece kamera (geri uyumluluk)
                PanX = BitConverter.ToSingle(pkt, 1);
                PanZ = BitConverter.ToSingle(pkt, 5);
                Zoom = BitConverter.ToSingle(pkt, 9);
                Rotate = BitConverter.ToSingle(pkt, 13);
                mLastInputTicks = DateTime.UtcNow.Ticks;
            }
        }

        public bool HasRecentInput()
        {
            return HasGuest && (DateTime.UtcNow.Ticks - mLastInputTicks) < TimeSpan.TicksPerSecond * 2;
        }

        private void OnGuestLeft(TcpClient c)
        {
            // yarış durumu koruması: yeni bir misafir bağlandıysa bu eski bağlantının
            // çıkışıdır — yeni bağlantıyı kapatma
            if (!ReferenceEquals(mClient, c) || !HasGuest)
            {
                return;
            }
            HasGuest = false;
            try { mClient?.Close(); } catch { }
            mClient = null;
            PanX = PanZ = Zoom = Rotate = 0f;
            MouseU = 0.5f;
            MouseV = 0.5f;
            Buttons = 0;
            Wheel = 0;
            Keys = 0;
            CoopPlugin.Log.LogInfo("[Coop] MİSAFİR AYRILDI");
        }

        /// <summary>Ana thread: GuestRenderTexture'tan JPEG karesi alıp kuyruğa koyar.</summary>
        public void CaptureFrame()
        {
            if (!mRunning || !HasGuest)
            {
                return;
            }
            if (Time.time < mNextCapture)
            {
                return;
            }
            mNextCapture = Time.time + 1f / CaptureFpsTarget;

            RenderTexture rt = GuestCameraController.Instance != null
                ? GuestCameraController.Instance.GuestRenderTexture
                : null;
            if (rt == null)
            {
                return;
            }

            if (mCaptureTex == null)
            {
                mCaptureTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            }

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                mCaptureTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            }
            finally
            {
                RenderTexture.active = prev;
            }

            byte[] jpg = mCaptureTex.EncodeToJPG(JpegQuality);
            if (mFrames.Count < MaxQueuedFrames)
            {
            mFrames.Enqueue(jpg);

            // kare hızı sayacı (gösterim için)
            mFramesLastSecond++;
            mFpsTimer += 1f / CaptureFpsTarget;
            if (mFpsTimer >= 1f)
            {
                CaptureFps = mFramesLastSecond;
                mFramesLastSecond = 0;
                mFpsTimer = 0f;
            }
        }
    }

        private static System.Collections.Generic.List<string> ListAllIps()
        {
            var ips = new System.Collections.Generic.List<string>();
            try
            {
                foreach (IPAddress addr in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    {
                        ips.Add(addr.ToString());
                    }
                }
            }
            catch (Exception)
            {
                // yok say
            }
            if (ips.Count == 0)
            {
                ips.Add("127.0.0.1");
            }
            return ips;
        }

        private static string FindLocalIp()
        {
            // özel LAN aralıklarını (192.168.x / 10.x / 172.16-31.x) tercih et;
            // yoksa ilk IPv4 adresi
            foreach (string ip in ListAllIps())
            {
                if (IsPrivateLan(ip))
                {
                    return ip;
                }
            }
            return ListAllIps()[0];
        }

        private static bool IsPrivateLan(string ip)
        {
            if (ip.StartsWith("192.168.") || ip.StartsWith("10."))
            {
                return true;
            }
            if (ip.StartsWith("172."))
            {
                try
                {
                    int second = int.Parse(ip.Substring(4, ip.IndexOf('.', 4) - 4));
                    return second >= 16 && second <= 31;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return false;
        }
    }
}
