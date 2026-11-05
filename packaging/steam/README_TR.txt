PLANETBASE CO-OP (RESMÎ OLMAYAN) - STEAM SÜRÜMÜ
===============================================

TELİF SORUNLARI İÇİN: lütfen önce ayabakanziya@gmail.com adresine ulaşın.

Planetbase'i arkadaşınla oyna: iki oyuncu aynı koloniyi birlikte yönetir.
Ücretsiz, resmî olmayan bir hayran modudur. Madruga Works ile bağlantısı yoktur.
Plugin v@@VERSION@@ / ağ protokolü v@@PROTOCOL@@.
BU BİR BETADIR. Lütfen kayıtlarını yedekle.

GEREKENLER
----------
- Steam'den alınmış Planetbase, sürüm 1.4.2 (güncel Steam sürümü).
- İki oyuncuda da Steam'de Planetbase ve bu paketin aynısı olmalı.
- GOG sürümü bu paketle desteklenmez.

KURULUM
-------
1) Planetbase'i kapat.
2) Steam'de Planetbase'e sağ tıkla: Yönet > Yerel dosyalara göz at.
   Planetbase.exe dosyasının olduğu klasör açılır.
3) Bu paketin İÇİNDEKİ HER ŞEYİ Planetbase.exe'nin yanına kopyala.
   Windows sorarsa klasörleri birleştir ve dosyaları değiştir.
   Paketin dış klasörünü oyun klasörünün içine koyma.
4) Planetbase'i Steam'den normal şekilde aç.
   Ana menüde New Game'in altında CO-OP düğmesi varsa kurulum tamamdır.

OYNAMA
------
1) İki oyuncu da Planetbase ana menüsünde olsun.
2) Host: CO-OP > STEAM'DE HOST OL > ARKADAŞ DAVET ET, sonra arkadaşını seç.
3) Arkadaşın Steam davetini kabul edince lobiye otomatik girer.
4) Misafir HAZIRIM der. Host YENİ OYUN ya da KAYIT SEÇ ile dünyayı seçer ve
   BAŞLAT'a basar. Misafirde o kayıt dosyasının olması gerekmez.
Steam seçeneğinde port açmaya gerek yoktur.

Yedek yöntem: LAN / IP HOST OL ve IP İLE KATIL. Aynı yerel ağda ya da özel bir
VPN üzerinden çalışır. Bu modda kimlik doğrulama yoktur, portu internete açma.

DİL
---
Co-op menüleri Windows diline göre Türkçe ya da İngilizce açılır.
CO-OP ekranının sağ üstündeki düğmeyle değiştirebilirsin. Ayar dosyası:
BepInEx\config\com.planetbasecoop.core.cfg (Language = tr / en / auto).

KAYITLAR
--------
Mod mevcut kayıtlarını değiştirmez. Kendi dosyalarını ekler (coop_recv.sav ve
coop klasörü). Host olarak oynarken otomatik kayıtlar oyunun normal otomatik
kayıt yuvalarına yazılır, yani eski bir otomatik kaydın üzerine gelebilir.

KALDIRMA
--------
winhttp.dll dosyasını silmek modu devre dışı bırakır. Tamamen kaldırmak için bu
paketin eklediği her şeyi sil. Oyunun kendi dosyaları değişmez.

SORUN GİDERME
-------------
- CO-OP düğmesi yok: Dosyalar yanlış yerde. winhttp.dll, Planetbase.exe'nin
  yanında olmalı.
- "Steam kullanılamıyor": Steam açık olmalı ve oyun Steam'den başlatılmalı.
- Arkadaşın katılamıyor: İkinizde de aynı mod sürümü olmalı.
- Ekranda "Senkron yenileniyor": İki oyun ayrışmış, mod host'un dünyasını iki
  tarafa yeniden yüklüyor. Birkaç saniye bekle.
- Ana menüye döndü: Bağlantı koptu ya da dünya doğrulanamadı. Oyunu yeniden
  başlatmadan yeni lobi açabilirsin.
- Antivirüs winhttp.dll için uyarı veriyor: Bu dosya BepInEx yükleyicisidir.
  Emin değilsen aşağıdaki SHA-256 değerini karşılaştır.

HATA BİLDİRİMİ
--------------
Oyunu yeniden açmadan İKİ oyuncunun da BepInEx\LogOutput.log dosyasını gönder.
Kısa açıklama ekle: ne oldu, yaklaşık ne zaman, oyun hızı kaçtı.
Senkron hatası olduysa Belgeler\Planetbase\coop içindeki oos_*.sav dosyalarını
da gönder (misafirinkiler coop-guest-menu\coop altında). Loglarda Steam kimliğin
bulunur.

BİLİNEN SINIRLAR
----------------
- Yalnızca iki oyuncu.
- Ping sağ altta görünür. Yaklaşık 180 ms üstünde, iki simülasyon aynı kalsın
  diye oyun kısa süreli yavaşlayabilir.
- Ara sıra kısa senkron yenilemeleri olabilir.
- Steam daveti bu sürümle iki ayrı PC'de test edilmedi.

PlanetbaseCoop.dll SHA-256:
@@DLLHASH@@

Üçüncü taraf lisansları: THIRD_PARTY_NOTICES klasörü.
