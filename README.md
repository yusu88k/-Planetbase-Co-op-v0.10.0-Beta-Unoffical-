# Planetbase Co-op (Unofficial)

**English** | [Türkçe](README.tr.md)

A free, unofficial two-player co-op mod for [Planetbase](https://store.steampowered.com/app/403190/Planetbase/),
built as a BepInEx plugin. Two players manage the same colony. Steam friends can invite each other
with no port forwarding, and a LAN/IP mode is available as a fallback.

**This is a beta. Back up your saves.** Not affiliated with or endorsed by Madruga Works.
No Planetbase game files or decompiled game code are included in this repository or its releases.

## Status

- Target: Planetbase 1.4.2 on Steam. The GOG build is not supported by the release package.
- Two players only. Both must use the same release.
- See the release notes for what has and has not been tested.

## Install

Download the zip from [Releases](../../releases) and follow `README_EN.txt` inside it (`README_TR.txt` is the Turkish version).
In short: copy everything in the zip next to `Planetbase.exe`, then start the game from Steam.
A CO-OP button appears on the main menu. Delete `winhttp.dll` to disable the mod.

## How it works

Both players run a full copy of the simulation in deterministic lockstep:

- The host owns the world. The guest loads the host's snapshot and never builds its own world.
- Player actions are requests that the host schedules for one shared simulation tick.
- Periodic checksums detect desyncs. Two failed checks reload the host's world on both sides;
  anything that cannot be verified closes the session safely to the main menu.
- Steam mode uses Steam lobbies and Steam Networking relay under the game's own App ID.

## Build from source

You need your own copy of Planetbase with BepInEx 5.4.23 installed, and the .NET SDK.

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

`PlanetbaseCoop/CoopTestHooks.cs` only does anything when built with `-p:DefineConstants=COOP_TESTHOOKS`;
release builds compile those calls out.

## Reporting bugs

Open an issue with `BepInEx\LogOutput.log` from both players. After a desync, also attach the
`oos_*.sav` files from `Documents\Planetbase\coop` (the guest's are under `coop-guest-menu\coop`).
Logs contain your SteamID; remove it if you prefer.

## License

The mod's own code is under the MIT License (see `LICENSE`). Third-party notices are in
`packaging/steam/THIRD_PARTY_NOTICES`. Planetbase is the property of Madruga Works.
If the rights holder asks for this project to be taken down, it will be.
