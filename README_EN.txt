PLANETBASE CO-OP (UNOFFICIAL) - STEAM VERSION
=============================================

Play Planetbase with a friend: two players manage the same colony.
Free, unofficial fan mod. Not affiliated with or endorsed by Madruga Works.
Plugin v0.10.0 / network protocol v21.
THIS IS A BETA. Please back up your saves.

REQUIREMENTS
------------
- Planetbase on Steam, version 1.4.2 (the current Steam build).
- Both players need Planetbase on Steam and this same package.
- The GOG version is not supported by this package.

INSTALL
-------
1) Close Planetbase.
2) In Steam: right-click Planetbase > Manage > Browse local files.
   This opens the folder that contains Planetbase.exe.
3) Copy EVERYTHING inside this package next to Planetbase.exe.
   If Windows asks, merge folders and replace files.
   Do not put the package's outer folder inside the game folder.
4) Start Planetbase from Steam as usual.
   A CO-OP button under New Game on the main menu means the install worked.

PLAYING
-------
1) Both players are on the Planetbase main menu.
2) Host: CO-OP > HOST ON STEAM > INVITE FRIEND, then pick your friend.
3) The friend accepts the Steam invite and joins the lobby automatically.
4) Guest presses READY. Host chooses NEW GAME or CHOOSE SAVE, then START.
   The guest does not need the host's save file.
No port forwarding is needed for the Steam option.

Fallback: HOST ON LAN / IP and JOIN BY IP, for the same local network or a
private VPN. This mode has no authentication, so do not forward its port to
the internet.

LANGUAGE
--------
The co-op menus follow your Windows language (Turkish or English).
Use the button in the top-right corner of the CO-OP screen to switch,
or set Language = tr / en / auto in BepInEx\config\com.planetbasecoop.core.cfg.

SAVES
-----
The mod does not change your existing save files. It adds its own files
(coop_recv.sav and a coop folder). Autosaves made while hosting use the game's
normal autosave slots, so they can replace an older autosave.

UNINSTALL
---------
Delete winhttp.dll to disable the mod. To remove it completely, also delete
everything this package added. The game's own files are not changed.

TROUBLESHOOTING
---------------
- No CO-OP button: the files are in the wrong place. winhttp.dll must be next
  to Planetbase.exe.
- "Steam unavailable": keep Steam running and start the game from Steam.
- Friend cannot join: both players must use the same mod version.
- "Resyncing" on screen: the two games drifted apart and the mod is reloading
  the host's world for both players. Wait a few seconds.
- Back at the main menu: the connection was lost or the world could not be
  verified. You can open a new lobby without restarting the game.
- Antivirus warning about winhttp.dll: this file is the BepInEx loader. Compare
  the hash below if you are unsure.

BUG REPORTS
-----------
Please send BepInEx\LogOutput.log from BOTH players, before restarting the game,
with a short description (what happened, roughly when, game speed).
After a desync, also send the oos_*.sav files from Documents\Planetbase\coop
(the guest's are in coop-guest-menu\coop). Logs contain your SteamID.

KNOWN LIMITS
------------
- Two players only.
- Ping is shown bottom-right. Above about 180 ms the game may briefly slow down
  so both simulations stay identical.
- Occasional short resyncs can happen.
- Steam invites have not been tested on two PCs with this version.

PlanetbaseCoop.dll SHA-256:
09726911A279D93FD4569595EF45B0D3B5DD0936AB674795AF0C46505E6A884A

Third-party licenses: see THIRD_PARTY_NOTICES.
