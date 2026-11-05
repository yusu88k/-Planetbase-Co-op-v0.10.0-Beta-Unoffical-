# Planetbase Co-op (Resmî Olmayan)

[English](README.md) | **Türkçe**

> **Telif sorunları için:** lütfen önce **ayabakanziya@gmail.com** adresine ulaşın.

[Planetbase](https://store.steampowered.com/app/403190/Planetbase/) için ücretsiz, resmî olmayan iki kişilik
co-op modu. BepInEx eklentisidir. İki oyuncu aynı koloniyi birlikte yönetir. Steam arkadaşları port açmadan
birbirini davet edebilir; yedek olarak LAN/IP modu da vardır.

**Bu bir betadır, kayıtlarını yedekle.** Madruga Works ile bağlantısı yoktur ve onların onayını taşımaz.
Bu depoda ve sürümlerde Planetbase oyun dosyası ya da çözümlenmiş oyun kodu bulunmaz.

## Durum

- Hedef: Steam'de Planetbase 1.4.2. Sürüm paketi GOG sürümünü desteklemez.
- Yalnızca iki oyuncu. İkisi de aynı sürümü kullanmalı.
- Neyin test edilip edilmediği sürüm notlarında yazar.

## Kurulum

[Releases](../../releases) sayfasından zip'i indir ve içindeki `README_TR.txt` dosyasını izle
(`README_EN.txt` İngilizce sürümdür). Kısaca: zip'in içindeki her şeyi `Planetbase.exe`'nin yanına kopyala
ve oyunu Steam'den aç. Ana menüde CO-OP düğmesi çıkar. Modu kapatmak için `winhttp.dll` dosyasını sil.

## Nasıl çalışır?

İki oyuncu da simülasyonun tam bir kopyasını deterministik lockstep ile çalıştırır:

- Dünyanın sahibi host'tur. Misafir host'un snapshot'ını yükler, kendi dünyasını kurmaz.
- Oyuncu işlemleri istektir; host bunları ortak bir simülasyon tick'ine planlar.
- Düzenli checksum kontrolleri ayrışmayı yakalar. Üst üste iki başarısız kontrolde host'un dünyası iki tarafa
  yeniden yüklenir. Doğrulanamayan her durum oturumu güvenli şekilde ana menüye kapatır.
- Steam modu, oyunun kendi App ID'si altında Steam lobilerini ve Steam Networking relay'ini kullanır.

## Kaynaktan derleme

BepInEx 5.4.23 kurulu kendi Planetbase kopyan ve .NET SDK gerekir.

```powershell
$game = 'D:\SteamLibrary\steamapps\common\Planetbase'
dotnet build PlanetbaseCoop\PlanetbaseCoop.csproj -c Release `
  "-p:PlanetbaseManagedPath=$game\Planetbase_Data\Managed" `
  "-p:BepInExCorePath=$game\BepInEx\core" `
  "-p:SteamworksManagedPath=$game\Planetbase_Data\Managed\com.rlabrecque.steamworks.net.dll" `
  -p:SteamworksReferenceName=com.rlabrecque.steamworks.net `
  -p:CopySteamworksManaged=false -p:BundleSteamRedistributable=false
dotnet test PlanetbaseCoop.Tests\PlanetbaseCoop.Tests.csproj -c Release
```

`PlanetbaseCoop/CoopTestHooks.cs` yalnızca `-p:DefineConstants=COOP_TESTHOOKS` ile derlenince çalışır;
yayın derlemelerinde bu çağrılar kaldırılır.

## Hata bildirimi

İki oyuncunun da `BepInEx\LogOutput.log` dosyasıyla issue aç. Senkron hatası olduysa
`Belgeler\Planetbase\coop` içindeki `oos_*.sav` dosyalarını da ekle (misafirinkiler `coop-guest-menu\coop`
altında). Loglarda SteamID bulunur; istersen sil.

## Lisans

Modun kendi kodu MIT lisanslıdır (`LICENSE`). Üçüncü taraf bildirimleri `packaging/steam/THIRD_PARTY_NOTICES`
klasöründedir. Planetbase, Madruga Works'ün mülkiyetindedir. Hak sahibi kaldırılmasını isterse proje kaldırılır.
