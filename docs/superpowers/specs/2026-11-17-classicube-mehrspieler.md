# ClassiCube: Mehrspieler über die Netz-ABI der Paket-Apps

Entwurf für Etappe 6 des [ClassiCube-Plans](../plans/2026-11-16-classicube.md), 17. November 2026. Quellstand ClassiCube `2b01bee867fe81331d0a898e7ad40a71882131de`; Dateiverweise beziehen sich darauf.

## Ziel

ClassiCube verbindet sich als Paket-App über das Classic-Protokoll mit einem Server. Der Desktop friert dabei nie ein, Einzelspieler ohne Netz bleibt möglich. Geprüft wird gegen einen kleinen lokalen Testserver in `scripts/`.

## Entscheidungen

### Keine Kernelerweiterung

Die vorhandene Netz-ABI (`net_open`, `net_status`, `net_send`, `net_recv`, `net_close`; höchstens vier Verbindungen, 64 KiB je Übertragung, DNS und Verbindungsaufbau asynchron im Kernel) deckt ClassiCubes Socket-Schnittstelle (`src/Platform.h`, Abschnitt *Sockets*) vollständig ab. ClassiCube braucht genau eine TCP-Verbindung ohne TLS. Der Wasm-Lauf hat einen eigenen Worker-Thread; `sleep_ms` und Netzaufrufe blockieren den Desktop nicht. Deshalb bleibt der Kernel unverändert.

### Abbildung der Socket-Funktionen

ClassiCube trennt Adressauflösung (`Socket_ParseAddress`), Anlegen (`Socket_Create`) und Verbinden (`Socket_Connect`). Der Kernel löst Namen erst in `net_open` auf und verbindet dort sofort. Die Plattformschicht `platform/Platform_ZehOS.c` bildet deshalb so ab:

| ClassiCube | ZehOS |
| --- | --- |
| `Socket_ParseAddress` | prüft nur die Form (IPv4-Literal oder ASCII-DNS-Name) und legt Name und Port in `cc_sockaddr` ab; keine Namensauflösung |
| `Socket_Create` | `net_open(host, port, 0)`; das Handle ist der `cc_socket` |
| `Socket_Connect` | meldet `EINPROGRESS`; der Aufbau läuft bereits |
| `Socket_Poll(WRITE)` | `net_status`: `EAGAIN` heißt „noch nicht bereit“, 0 „verbunden“, sonst Fehler |
| `Socket_Read`, `Socket_Write` | `net_recv`, `net_send`, höchstens 64 KiB je Aufruf; `EAGAIN` ist ClassiCubes „würde blockieren“ |
| `Socket_Close` | `net_close` |

`net_recv` meldet EOF als Erfolg mit 0 Bytes. ClassiCube (`src/Server.c`, `MPConnection_Tick`) würde EOF erst nach 30 s ohne Daten als Abbruch werten. Die Plattformschicht meldet EOF deshalb als `ECONNRESET` (`ReturnCode_SocketDropped`): Ein beendeter Server führt sofort zum Bildschirm „Disconnected“. Eine Kick-Nachricht des Servers kommt vorher als Daten an und wird zuerst verarbeitet.

Fehlercodes der Netz-ABI sind WASI-Fehlernummern; sie stimmen mit den `errno`-Werten von wasi-libc überein, `strerror` beschreibt sie also direkt.

### HTTP bleibt aus

`CC_BUILD_NETWORKING` schaltet in ClassiCube Classic-Protokoll und HTTP-Worker (Skins, Texturpakete vom Server, classicube.net-Anmeldung, Updateprüfung) gemeinsam ein. Für ZehOS wird nur das Classic-Protokoll gebraucht. Ein Patch lässt `src/Http_Worker.c` für ZehOS bei den vorhandenen Stubs: Alle HTTP-Abrufe scheitern weiter sofort, das Paket nimmt keine Verbindung zu Dritten auf und BearSSL wird nicht gebraucht. Folge: Spieler erscheinen mit Standardskin, Server-Texturpakete werden nicht geladen.

### Eingabe von Server und Name: ClassiCubes Launcher

ClassiCube gibt Server, Port, Name und mppass über die Startargumente (`<name> <mppass> <ip> <port>`) an das Spiel. Der Launcher erzeugt sie im Bildschirm *Direct connect* (`src/LScreens.c`, `DirectConnectScreen`) und startet das Spiel bei Plattformen mit einem Prozess im selben Programm neu (`Launcher_StartGame` → `Process_StartGame2` → zweiter `RunProgram`-Durchlauf in `main`). Der Loader liefert nur `argv = ["app"]`; das Paket ist nur lesbar, eine Konfigurationsdatei schiede also aus.

Das Paket startet deshalb künftig den Launcher statt direkt `--singleplayer`. Der Hauptbildschirm bietet *Direct connect* (Name, `IP:Port`, mppass) und *Singleplayer*. Angepasst wird:

- Der Launcher zeichnet alle Texte mit der Bitmapschrift aus `default.png`; die eingebaute Ersatzschrift fehlt in ZehOS aus Lizenzgründen (Patch 0002). `Font_Make` erzeugt unter ZehOS immer Bitmapschriften.
- Anmeldung bei classicube.net (Kennwort, *Sign in*, *Resume*) wird ausgeblendet, weil HTTP fehlt. Das Namensfeld bleibt; *Singleplayer* verwendet es wie bisher.
- Optionen werden nicht gespeichert (Paket nur lesbar); die Eingaben im Direct-Connect-Bildschirm gehen beim Beenden verloren (Etappe 4).

Verworfen: eigener Eingabebildschirm im Spiel (mehr eigener Code, weicht von ClassiCubes Bedienung ab) und ein neuer Kernelweg für Startargumente (Kernelerweiterung ohne Bedarf).

### Paket

Es gibt nur einen Build. Das Netz entscheidet das Manifest: Die neue Paketversion trägt `capability=net` (`build-package.py --network`). Ein Paket ohne Berechtigung bekommt aus `net_open` `EACCES`; ClassiCube zeigt dann „Failed to connect“, Einzelspieler läuft unverändert. Die Version im Branch `packages` ist nicht Teil dieser Etappe.

### Grenzen, die bleiben

- `MPConnection_SendData` wartet bei vollem Sendepuffer bis zu 10 s mit `Thread_Sleep(10)` (ClassiCube-Code). In dieser Zeit steht das Spielbild; der Desktop bleibt bedienbar. Bei 64 KiB Puffer und rund 200 Bytes/s Positionsdaten tritt das nur bei hängendem Server auf.
- Bleibt ein Server stumm, ohne die Verbindung zu schließen, erkennt ClassiCube das nicht selbst (wie unter Linux). Der Spieler kann über das Pausenmenü oder das Fenster beenden.
- Solange das Fenster inaktiv ist, blockiert `present` (Vertrag). Der Kernel puffert höchstens 64 KiB Empfangsdaten; ein Server, der in der Zeit viel sendet, kann die Verbindung als hängend schließen.
- Kein IPv6, kein TLS, keine CPE-Erweiterungen über das hinaus, was ClassiCube ohne HTTP kann.

## Testserver

`scripts/classic-test-server.py`, nur Standardbibliothek: Login (Protokoll 7, ohne CPE), Level (gzip, Ein-Byte-Blöcke, 64×32×64, flach), Spawn, Positionen und Blockänderungen an alle anderen Clients weiter, Chat-Echo, Ping. Jedes Ereignis als JSON-Zeile auf stdout und für Tests über einen Rückruf. `scripts/tests/test_classic_test_server.py` prüft Login, Level, Blockweitergabe zwischen zwei Clients, Positionen und Abbau bei Verbindungsende.

## Gastprüfung

`scripts/test-classicube-multiplayer.py` nach dem Muster von `scripts/test-classicube-desktop.py`: Testserver im Skript auf `127.0.0.1` (der Gast erreicht ihn als `10.0.2.2`), Paket mit `--network`, `net dhcp`, Start, *Direct connect*, Name und Adresse tippen, Verbinden. Geprüft wird: Login am Server, Bewegung (Positionspakete ändern sich), Blockänderung kommt am Server an, ein vom Server gesetzter Block erscheint im Bild, Schließen des Fensters bei bestehender Verbindung (Server sieht das Ende), Serverausfall (Server schließt alle Verbindungen, ClassiCube zeigt „Disconnected“, der Desktop reagiert weiter auf das Startmenü). `scripts/test-classicube-desktop.py` startet Einzelspieler künftig über den Launcher.
