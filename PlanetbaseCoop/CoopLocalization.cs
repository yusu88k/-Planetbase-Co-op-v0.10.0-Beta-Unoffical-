using System;
using System.Collections.Generic;

namespace PlanetbaseCoop
{
    public enum CoopLanguage
    {
        Turkish = 0,
        English = 1
    }

    /// <summary>
    /// Arayuz metinleri kaynakta Turkce yazilir ve ekrana cizilirken secilen
    /// dile cevrilir. Boylece dil oyun icinden aninda degisir; ag paketleri ve
    /// loglar dilden bagimsiz kalir. Bilinmeyen metin (oyuncu/kayit adi) aynen doner.
    /// </summary>
    public static class CoopText
    {
        private static volatile int sLanguage = (int)CoopLanguage.English;

        private static readonly Dictionary<string, string> Exact =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ARKADAŞ DAVET ET", "INVITE FRIEND" },
            { "AYRIL", "LEAVE" },
            { "AĞ BEKLEMESİ BİTTİ — OYUN TOPARLANDI", "NETWORK WAIT OVER — GAME RECOVERED" },
            { "AĞ GECİKMESİ NORMALE DÖNDÜ", "NETWORK LATENCY BACK TO NORMAL" },
            { "AĞ OTURUMU BAŞLATILAMADI", "COULD NOT START NETWORK SESSION" },
            { "BAGLANTI SINYALI ALINAMIYOR", "NO SIGNAL FROM THE OTHER PLAYER" },
            { "BAĞLAN", "CONNECT" },
            { "BAĞLANDIN • HAZIRLIK ONAYI BEKLENİYOR", "CONNECTED • PRESS READY" },
            { "BAĞLANDIN • HAZIRSIN • HOST BEKLENİYOR", "CONNECTED • READY • WAITING FOR HOST" },
            { "BAĞLANTI", "CONNECTION" },
            { "BAŞLAT", "START" },
            { "BINA HOST TARAFINDAN REDDEDILDI", "BUILDING REJECTED BY HOST" },
            { "BINA YERLESTIRILEMEDI", "BUILDING COULD NOT BE PLACED" },
            { "BİR OTURUM ZATEN AKTİF", "A SESSION IS ALREADY ACTIVE" },
            { "CO-OP OTURUMU KAPANDI — ANA MENÜ", "CO-OP SESSION CLOSED — MAIN MENU" },
            { "DAVET EDİLEBİLECEK STEAM ARKADAŞI BULUNAMADI", "NO STEAM FRIENDS AVAILABLE TO INVITE" },
            { "DAVETİ YALNIZ HOST AÇABİLİR", "ONLY THE HOST CAN OPEN INVITES" },
            { "DAVETİ YALNIZ HOST GÖNDEREBİLİR", "ONLY THE HOST CAN SEND INVITES" },
            { "DÜNYA SEÇİM EKRANI AÇILAMADI", "COULD NOT OPEN WORLD SELECTION" },
            { "DÜNYA: SON KAYITTAN DEVAM", "WORLD: CONTINUE LAST SAVE" },
            { "DÜNYA: YENİ OYUN", "WORLD: NEW GAME" },
            { "GECERSIZ PORT", "INVALID PORT" },
            { "GECERSIZ ROL", "INVALID ROLE" },
            { "GECERSIZ ZIYARETCI ETKINLIGI", "INVALID VISITOR EVENT" },
            { "GERI DONUSUM HEDEFI BULUNAMADI", "RECYCLE TARGET NOT FOUND" },
            { "GERI DONUSUM HOST TARAFINDAN REDDEDILDI", "RECYCLING REJECTED BY HOST" },
            { "GERI DONUSUM ISTEGI ZAMAN ASIMINA UGRADI", "RECYCLE REQUEST TIMED OUT" },
            { "GERİ", "BACK" },
            { "GEZEGEN SEÇİMİ BAŞLADI", "PLANET SELECTION STARTED" },
            { "GEÇERSİZ DÜNYA SEÇİMİ", "INVALID WORLD SELECTION" },
            { "GEÇERSİZ KAYIT SEÇİMİ", "INVALID SAVE SELECTION" },
            { "GEÇERSİZ PORT", "INVALID PORT" },
            { "GEÇERSİZ SEÇİM GÖRÜNÜMÜ", "INVALID SELECTION VIEW" },
            { "GEÇERSİZ STEAM ARKADAŞI", "INVALID STEAM FRIEND" },
            { "HAZIR İPTAL EDİLDİ", "READY CANCELLED" },
            { "HAZIRIM", "READY" },
            { "HAZIRLIĞI İPTAL ET", "CANCEL READY" },
            { "HOST ADRESI GEREKLI", "HOST ADDRESS REQUIRED" },
            { "HOST ADRESİ:", "HOST ADDRESS:" },
            { "HOST DÜNYASI BEKLENİYOR", "WAITING FOR THE HOST WORLD" },
            { "HOST GEZEGEN SEÇİMİNİ BAŞLATTI", "HOST STARTED PLANET SELECTION" },
            { "HOST IP ADRESİ", "HOST IP ADDRESS" },
            { "HOST LOBİSİ", "HOST LOBBY" },
            { "HOST LOBİSİ AÇILDI — MİSAFİR BEKLENİYOR", "HOST LOBBY OPEN — WAITING FOR GUEST" },
            { "HOST ONAYI BEKLENIYOR", "WAITING FOR THE HOST" },
            { "HOST ORTAK DÜNYAYI BAŞLATTI", "HOST STARTED THE SHARED WORLD" },
            { "HOST'A BAĞLANILIYOR", "CONNECTING TO HOST" },
            { "HOST'A BAĞLANILIYOR...", "CONNECTING TO HOST..." },
            { "INIS IZINLERI ORTAK TICKE PLANLANDI", "LANDING PERMISSIONS SCHEDULED" },
            { "INIS IZNI HOSTA GONDERILDI", "LANDING PERMISSION SENT TO HOST" },
            { "IP İLE KATIL", "JOIN BY IP" },
            { "KAYIT BULUNAMADI", "SAVE NOT FOUND" },
            { "KAYIT SEÇ", "CHOOSE SAVE" },
            { "KAYITLI ORTAK DÜNYA BAŞLATILIYOR", "STARTING SHARED WORLD FROM SAVE" },
            { "KAYITTAN CO-OP AÇ", "CO-OP FROM SAVE" },
            { "KULLANILABİLİR KAYIT BULUNAMADI", "NO USABLE SAVES FOUND" },
            { "LAN / IP HOST OL", "HOST ON LAN / IP" },
            { "LOBİ DURUMU GEÇERSİZ", "INVALID LOBBY STATE" },
            { "LOBİ KAPALI", "LOBBY CLOSED" },
            { "MISAFIR DURUMU HOSTA BILDIRILDI", "GUEST STATE REPORTED TO HOST" },
            { "MISAFIR DURUMU YENILENIYOR", "REFRESHING GUEST STATE" },
            { "MİSAFİR BAĞLANDI", "GUEST CONNECTED" },
            { "MİSAFİR BAĞLANTISI KOPTU", "GUEST DISCONNECTED" },
            { "MİSAFİR BEKLENİYOR", "WAITING FOR GUEST" },
            { "MİSAFİR BEKLENİYOR...", "WAITING FOR GUEST..." },
            { "MİSAFİR HAZIR", "GUEST READY" },
            { "MİSAFİR HAZIR DEĞİL", "GUEST NOT READY" },
            { "MİSAFİR HAZIRLIĞI İPTAL ETTİ", "GUEST CANCELLED READY" },
            { "MİSAFİR KATILDI • HAZIR", "GUEST JOINED • READY" },
            { "MİSAFİR KATILDI • HAZIR DEĞİL", "GUEST JOINED • NOT READY" },
            { "MİSAFİR SEÇİM EKRANI GÜNCELLENİYOR", "UPDATING GUEST SELECTION SCREEN" },
            { "MİSAFİR SEÇİM EKRANI HAZIR DEĞİL", "GUEST SELECTION SCREEN NOT READY" },
            { "MİSAFİR STEAM ÜZERİNDEN BAĞLANDI", "GUEST CONNECTED VIA STEAM" },
            { "NORMAL BİR .SAV KAYDI BULUNAMADI", "NO REGULAR .SAV SAVE FOUND" },
            { "ORTAK DÜNYA BAŞLATILIYOR", "STARTING SHARED WORLD" },
            { "ORTAK INIS HAZIR", "SHARED LANDING READY" },
            { "OTURUM DURUMU GECERSIZ", "INVALID SESSION STATE" },
            { "OTURUM ZATEN AKTIF", "SESSION ALREADY ACTIVE" },
            { "OYUN BILGISI: KOLONI SONLANDI", "GAME INFO: THE COLONY HAS ENDED" },
            { "OYUNA KATIL", "JOIN GAME" },
            { "OYUNU HOST SONLANDIRDI", "THE HOST ENDED THE GAME" },
            { "PING  ÖLÇÜLÜYOR", "PING  MEASURING" },
            { "PORT AÇMA GEREKMEZ", "NO PORT FORWARDING NEEDED" },
            { "Planetbase Co-op lobisinde", "In a Planetbase Co-op lobby" },
            { "SECIM GONDERILEMEDI", "COULD NOT SEND SELECTION" },
            { "SENKRON MUTASYONU BASARISIZ - YENILENIYOR", "SYNC ACTION FAILED - RESYNCING" },
            { "SENKRON YENILENIYOR", "RESYNCING" },
            { "SEÇİLEN KULLANICI STEAM ARKADAŞIN DEĞİL", "SELECTED USER IS NOT YOUR STEAM FRIEND" },
            { "SEÇİM BAŞLATILAMADI", "COULD NOT START SELECTION" },
            { "SEÇİM EKRANI AÇILAMADI", "COULD NOT OPEN SELECTION SCREEN" },
            { "SEÇİM EKRANI GÖNDERİLEMEDİ", "COULD NOT SEND SELECTION SCREEN" },
            { "SEÇİM GÖNDERİLEMEDİ", "COULD NOT SEND SELECTION" },
            { "SEÇİM YETKİSİ YOK", "NOT ALLOWED TO SELECT" },
            { "SEÇİM ZATEN BAŞLADI", "SELECTION ALREADY STARTED" },
            { "SNAPSHOT GECERSIZ", "INVALID SNAPSHOT" },
            { "SNAPSHOT YUKLEME DOGRULANAMADI", "SNAPSHOT LOAD COULD NOT BE VERIFIED" },
            { "SON SEÇİM YAYINLANAMADI", "COULD NOT PUBLISH FINAL SELECTION" },
            { "SONRAKİ", "NEXT" },
            { "START GONDERILEMEDI", "COULD NOT SEND START" },
            { "STEAM ARKADAŞ DAVETİ AÇILDI", "STEAM FRIEND INVITE OPENED" },
            { "STEAM ARKADAŞ LİSTESİ ALINAMADI", "COULD NOT LOAD STEAM FRIEND LIST" },
            { "STEAM ARKADAŞLARI", "STEAM FRIENDS" },
            { "STEAM DAVET LOBİSİNE KATILINIYOR...", "JOINING STEAM INVITE LOBBY..." },
            { "STEAM DAVETİ GÖNDERİLEMEDİ", "COULD NOT SEND STEAM INVITE" },
            { "STEAM HAZIRLANMADI", "STEAM NOT INITIALIZED" },
            { "STEAM HOST KİMLİĞİ BULUNAMADI", "STEAM HOST ID NOT FOUND" },
            { "STEAM HOSTA BAĞLANILIYOR...", "CONNECTING TO STEAM HOST..." },
            { "STEAM KAPALI", "STEAM OFF" },
            { "STEAM KULLANILAMIYOR: SteamAPI.Init basarisiz", "STEAM UNAVAILABLE: SteamAPI.Init failed" },
            { "STEAM LOBİ BİLGİSİ YAZILAMADI", "COULD NOT WRITE STEAM LOBBY DATA" },
            { "STEAM LOBİ BİLGİSİ ZAMAN AŞIMINA UĞRADI", "STEAM LOBBY DATA TIMED OUT" },
            { "STEAM LOBİSİ AÇILAMADI", "COULD NOT OPEN STEAM LOBBY" },
            { "STEAM LOBİSİ HAZIR — ARKADAŞINI DAVET ET", "STEAM LOBBY READY — INVITE YOUR FRIEND" },
            { "STEAM LOBİSİ HENÜZ HAZIR DEĞİL", "STEAM LOBBY NOT READY YET" },
            { "STEAM LOBİSİ OLUŞTURULUYOR...", "CREATING STEAM LOBBY..." },
            { "STEAM MİSAFİRİ AYRILDI — LOBİ YENİ DAVETE AÇIK", "STEAM GUEST LEFT — LOBBY OPEN FOR A NEW INVITE" },
            { "STEAM OVERLAY BU SÜREÇTE ETKİN DEĞİL", "STEAM OVERLAY IS NOT ACTIVE IN THIS PROCESS" },
            { "STEAM SERVİSİ HAZIR DEĞİL", "STEAM SERVICE NOT READY" },
            { "STEAM'DE HOST OL", "HOST ON STEAM" },
            { "TICARET BITINCE SENKRON KONTROL EDILECEK", "SYNC WILL BE CHECKED AFTER TRADING" },
            { "TICARET ISTEGI GONDERILEMEDI", "COULD NOT SEND TRADE REQUEST" },
            { "TICARET PENCERESINI KAPATIN", "PLEASE CLOSE THE TRADE WINDOW" },
            { "UYUMSUZ VEYA GEÇERSİZ PLANETBASE CO-OP LOBİSİ", "INCOMPATIBLE OR INVALID PLANETBASE CO-OP LOBBY" },
            { "VANİLLA SEÇİMİ DOĞRULANAMADI", "COULD NOT VERIFY GAME SELECTION" },
            { "YALNIZ HOST BAŞLATIR", "ONLY THE HOST CAN START" },
            { "YALNIZ HOST SECIM YAPABILIR", "ONLY THE HOST CAN SELECT" },
            { "YENİ OYUN", "NEW GAME" },
            { "YENİLE", "REFRESH" },
            { "YOL HEDEFI ARTIK GECERLI DEGIL", "CONNECTION TARGET IS NO LONGER VALID" },
            { "YOL HOST TARAFINDAN REDDEDILDI", "CONNECTION REJECTED BY HOST" },
            { "YOL ISTEGI ZAMAN ASIMINA UGRADI", "CONNECTION REQUEST TIMED OUT" },
            { "YOL OLUSTURULAMADI", "COULD NOT CREATE CONNECTION" },
            { "ZIYARETCI ETKINLIGI ARTIK GECERLI DEGIL", "VISITOR EVENT IS NO LONGER VALID" },
            { "ZIYARETCI ETKINLIGI GONDERILEMEDI", "COULD NOT SEND VISITOR EVENT" },
            { "ZIYARETCI ISTEGI GONDERILEMEDI", "COULD NOT SEND VISITOR REQUEST" },
            { "ÖNCE MEVCUT CO-OP OTURUMUNDAN AYRIL", "LEAVE THE CURRENT CO-OP SESSION FIRST" },
            { "ÖNCEKİ", "PREVIOUS" }
        };

        // Degisken iceren metinlerin sabit parcalari (oyuncu adi, ping, hata kodu).
        // Uzundan kisaya uygulanir; tam eslesme yoksa devreye girer.
        private static readonly KeyValuePair<string, string>[] Fragments = SortByLengthDescending(
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "  •  ÇEVRİMİÇİ", "  •  ONLINE" },
            { "  •  ÇEVRİMDIŞI", "  •  OFFLINE" },
            { "DAVET GÖNDERİLDİ: ", "INVITE SENT: " },
            { "DÜNYA: KAYIT — ", "WORLD: SAVE — " },
            { "DİKKAT: PİNG ÇOK YÜKSEK (", "WARNING: VERY HIGH PING (" },
            { " MS) — OYUN YAVAŞLAYABİLİR", " MS) — THE GAME MAY SLOW DOWN" },
            { "YÜKSEK PİNG: ", "HIGH PING: " },
            { "STEAM ARKADAŞ LİSTESİ HATASI: ", "STEAM FRIEND LIST ERROR: " },
            { "STEAM DAVETİ AÇILAMADI: ", "COULD NOT OPEN STEAM INVITE: " },
            { "STEAM DAVETİ HATASI: ", "STEAM INVITE ERROR: " },
            { "STEAM HAZIR — ", "STEAM READY — " },
            { "STEAM KULLANILAMIYOR: ", "STEAM UNAVAILABLE: " },
            { "STEAM LOBİSİ AÇILAMADI: ", "COULD NOT OPEN STEAM LOBBY: " },
            { "STEAM LOBİSİNE GİRİLEMEDİ: ", "COULD NOT ENTER STEAM LOBBY: " },
            { " HAZIR DEĞİL", " NOT READY" }
        });

        static CoopText()
        {
            // Plugin.TimeStateText: 3 aktor x (duraklat + 3 hiz).
            string[,] actors = { { "MISAFIR", "GUEST" }, { "HOST", "HOST" }, { "SISTEM", "SYSTEM" } };
            string[] speeds = { "1X", "2X", "4X" };
            for (int i = 0; i < actors.GetLength(0); i++)
            {
                Exact[actors[i, 0] + " OYUNU DURAKLATTI"] = actors[i, 1] + " PAUSED THE GAME";
                for (int s = 0; s < speeds.Length; s++)
                    Exact[actors[i, 0] + " OYUNU " + speeds[s] + " HIZA ALDI"] =
                        actors[i, 1] + " SET THE GAME TO " + speeds[s] + " SPEED";
            }
        }

        public static CoopLanguage Language
        {
            get { return (CoopLanguage)sLanguage; }
            set { sLanguage = (int)value; }
        }

        public static string T(string text)
        {
            return Translate(text, Language);
        }

        public static string Translate(string text, CoopLanguage language)
        {
            if (string.IsNullOrEmpty(text) || language == CoopLanguage.Turkish) return text;
            string translated;
            if (Exact.TryGetValue(text, out translated)) return translated;
            for (int i = 0; i < Fragments.Length; i++)
            {
                if (text.IndexOf(Fragments[i].Key, StringComparison.Ordinal) >= 0)
                    text = text.Replace(Fragments[i].Key, Fragments[i].Value);
            }
            return text;
        }

        /// <summary>
        /// "tr"/"en" acik secimdir; diger her deger (or. "auto") Windows diline gore
        /// Turkce sistemde Turkce, aksi halde Ingilizce secer.
        /// </summary>
        public static CoopLanguage Resolve(string configured, bool systemIsTurkish)
        {
            string value = (configured ?? string.Empty).Trim();
            if (string.Equals(value, "tr", StringComparison.OrdinalIgnoreCase))
                return CoopLanguage.Turkish;
            if (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase))
                return CoopLanguage.English;
            return systemIsTurkish ? CoopLanguage.Turkish : CoopLanguage.English;
        }

        public static string ConfigValue(CoopLanguage language)
        {
            return language == CoopLanguage.Turkish ? "tr" : "en";
        }

        public static string ToggleLabel(CoopLanguage language)
        {
            return language == CoopLanguage.Turkish ? "DİL: TÜRKÇE" : "LANGUAGE: ENGLISH";
        }

        internal static IEnumerable<KeyValuePair<string, string>> AllTranslations()
        {
            foreach (KeyValuePair<string, string> pair in Exact) yield return pair;
            foreach (KeyValuePair<string, string> pair in Fragments) yield return pair;
        }

        private static KeyValuePair<string, string>[] SortByLengthDescending(
            Dictionary<string, string> source)
        {
            var list = new List<KeyValuePair<string, string>>(source);
            list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            return list.ToArray();
        }
    }
}
