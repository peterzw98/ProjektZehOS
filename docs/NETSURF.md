# NetSurf als Offline-Paket

Stand: 15. November 2026. NetSurf 3.11 läuft als separates `wasm32-wasip1`-Paket im bestehenden wasm3-Loader.
HTML, CSS, Links, Zurück, Scrollen und Formulareingabe sind im ZehOS-Gast unter QEMU geprüft.
GPL-Code bleibt im Paket. Der Kernel ergänzt allgemeine Text-/Zeigereingabe und lesende Dateistatusabfragen.
NetSurf ist noch nicht im öffentlichen Paketkatalog veröffentlicht.

## Bauen und installieren

Die Linux-Bauanleitung steht in [experiments/NetSurf.Wasi/README.md](../experiments/NetSurf.Wasi/README.md).
`python3 experiments/NetSurf.Wasi/build.py` lädt feste Quellen/Toolchain-Versionen mit SHA-256-Prüfung,
wendet die Portpatches auf einen zum Patchsatz gehörenden Quellbaum an und liefert
`experiments/NetSurf.Wasi/bin/package/`. Der CI-Job „NetSurf offline package“ baut denselben Stand und
liefert diese Dateien als **unsigniertes** Artefakt `NetSurf-offline-files`.

Diese Dateien werden mit [build-package.py](../scripts/build-package.py) zu einem signierten Paket
`netsurf` mit Einstieg `netsurf.wasm` zusammengefasst. Alle Ressourcen, Testseiten und Lizenztexte müssen
in das Manifest. Für lokale Tests ausschließlich einen eigenen Testschlüssel verwenden und dessen öffentlichen
Teil beim Kernelbuild mit `-p:ZehOSPackageKeyPath=<public.b64>` einbetten. Normale Builds behalten den
bestehenden Herausgeberschlüssel. Schlüssel nie committen. Veröffentlichung bleibt ein gesonderter Auftrag.

Nach dem Kopieren des signierten Paketordners auf eine isolierte Testplatte als Administrator:

```text
pkg install-local /data/pkgs/netsurf/1.0.0
```

Danach im Programmmenü „NetSurf“ starten. Die Startseite `file:///pages/index.html` ist im Paket enthalten.
Der Zurückpfeil oben links führt zurück; Mausrad, Pfeile und Bild auf/ab scrollen. Die Adressleiste nimmt
lokale `file:///pages/...`-Adressen an. Das X des ZehOS-Fensters beendet den Lauf. F4/Esc erhalten den Wechsel
zwischen Desktop und Textshell; Fokusverlust setzt Eingaben zurück und bricht einen begonnenen Klick ab.

## Wiederholbare Gastprüfung

```powershell
python scripts/test-netsurf-desktop.py --iso <Test-ISO> --package <unsignierte-Paketdateien> --key <Testschlüssel-private.pem>
```

Das ISO muss zum öffentlichen Gegenstück des Testschlüssels passen. Der Test erstellt selbst ein signiertes
lokales Paket und eine frische beschreibbare QEMU-Platte; bestehende Datenimages werden nicht verwendet.
Er prüft Installation, Start, Markerfarben für HTML/CSS und Linknavigation, Zurück, Fokusverlust ohne Klick,
Mausrad/Bild ab, Weiterbedienung nach einem kaputten Link und drei Starts mit Schließen.
Das Paket erhält dafür `capability=net`. Nach `net dhcp` lädt NetSurf Seiten von einem lokalen Testserver
auf dem Entwicklungsrechner (aus dem Gast `10.0.2.2`): eine Seite, eine zweistufige relative Weiterleitung,
eine `chunked`-Antwort und eine 404-Seite, danach wieder eine lokale Seite.
Bildschirmfotos dokumentieren zusätzlich „Grüße @“ im Formular, die sichtbare Fehlerseite „Not found“ und
die HTTPS-Fehlerseite; diese drei Inhalte werden visuell geprüft. Drei Speicherwerte nach dem Aufräumen dürfen um höchstens
8 MiB sinken. Nachweise bleiben unter `.tools/netsurf-guest/` und werden nicht eingecheckt.

## Bisheriger Nachweis

- 445 Hosttests mit Warnungen als Fehler bestanden; native Release- und Recovery-Builds ohne Warnungen.
- Acht native Tests verwenden die echte C-Brücke mit wasm3: Ereignislayout, unaligned Pointer,
  Speicherende/Überlauf, lesende Dateiabfragen und Paketpfadgrenzen. Zwei Tests sichern wiederholte Builds
  mit neuen bzw. zuvor fehlgeschlagenen Patches ab. Der Browserbuild führt einen nativen Eingabetest aus.
- Linux-CI-Lauf [43743357576](https://github.com/peterzw98/ProjektZehOS/actions/runs/43743357576):
  Hosttests, Skripttests, Kernelbuild und frischer Browserbuild bestanden.
- QEMU TCG, vollständiger Lauf mit korrigiertem Frontend: bestanden. Freier Speicher nach drei Läufen
  444912, 445232, 445488 KiB. Formulartext und Fehlerseite anhand der Bilder geprüft.
- Eine zusätzliche kleine Paketprobe hat Tastendruck/-freigabe, Text, alle drei Maustasten, Rad in beide
  Richtungen, Fokusreset, Koordinaten, Loslassen außerhalb sowie Neustart/Schließen im Gast geprüft.

Endabnahme am 15. November 2026 auf genau dem PR-Stand `51c9ec8`, mit `master` einschließlich #55 und #58 und Version 0.2.2:

- **Hosttests:** 473 mit Warnungen als Fehler bestanden. Darunter sind sechs neue Tests für `WasmPackagePaths`: Größe und Typ, `ENOENT`, `EACCES` beim Ausbruch aus dem Paket sowie `EIO` statt Ausnahme.
- **Kernelbuilds:** `cosmos build` (Release) erfolgreich; Recovery-Build und Test-ISO mit Testschlüssel ohne Warnungen. Beide ISOs booten in QEMU als 0.2.2, Release bis zum Desktop und Recovery bis zur Textshell.
- **Paket und Linux-Prüfhost:** frischer Paketbuild; nativer Eingabetest, `host/run_tests.py` 7/7 und Buildtests bestanden.
- **CI:** Linux-Lauf [43753744664](https://github.com/peterzw98/ProjektZehOS/actions/runs/43753744664) grün.
- **QEMU 10.2.2, TCG, unter Linux:** `NETSURF_GUEST PASS`, ohne zusätzliche Werkzeug-Umgehungen (#58).
  - Freier Speicher nach drei Läufen: 442600, 443240 und 443176 KiB.
  - Formulartext „Grüße @“ (deutsche Belegung mit AltGr) und Fehlerseite anhand der Bilder geprüft.
- **Korrekturen gegenüber dem vorherigen Gastnachweis:**
  - Entf in der Adressleiste fehlte im fbtk.
  - Eine zweite Taste während des Rollbalken-Ziehens löste den Grab.
  - Ein Fokusreset bei mehreren gedrückten Tasten konnte einen Klick auslösen.
  - Der Build ist reproduzierbar und enthält keine Benutzer-, Rechner- oder Pfaddaten mehr.

HTTP-Gastprüfung am 15. November 2026 (Branch `feature/netsurf-http-guest`, 0.2.11, gestapelt auf dem Host-PR #69):
`NETSURF_GUEST PASS` unter QEMU mit KVM (`-cpu qemu64,+rdrand,+rdseed`); freier Speicher nach drei Läufen
438952, 439528 und 441472 KiB. HTTPS-Fehlerseite anhand des Bildes geprüft. Linux-Prüfhost: `host/run_tests.py` 13/13.

## Netzwerkfehler

Das Netz-ABI kennt für Verbindungsfehler `ECONNREFUSED` (14), `ECONNRESET` (15) und `EHOSTUNREACH` (23); der Fetcher
zeigt dafür eigene Fehlerseiten. Cosmos 3.0.94 unterscheidet diese Fälle nicht: Ein gescheiterter Verbindungsaufbau
kommt im Kernel nur als allgemeine Ausnahme an und wird als `EIO` (29) gemeldet, ein Abbruch durch RST erscheint wie ein
normales Verbindungsende. Die eigenen Werte entstehen deshalb nur auf dem Linux-Prüfhost und in den Hosttests mit echten
Sockets. Der Prüfhost übernimmt die Kernelgrenzen: höchstens 4 Verbindungen, 64 KiB je `net_send`/`net_recv`,
Hostnamen bis 253 ASCII-Zeichen und `EACCES` ohne Netzwerkberechtigung. Die Prüfung einzelner Namensteile übernimmt er
nicht; solche Namen meldet dort die Auflösung (`ENOENT`) statt `EINVAL`. Diese erteilt dort die Umgebungsvariable
`ZEHOS_NET=1` anstelle von `capability=net`. TLS (`ZEHOS_NET_TLS`) bildet der Prüfhost mit OpenSSL nach; die
Vertrauensanker kommen aus `ZEHOS_TLS_CA=<PEM-Datei>`, ohne sie meldet `net_open` wie der Kernel `ENOSYS`. Er
braucht dafür `-lssl -lcrypto`. Die Fehlercodes für TLS stehen in [PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md).

## Grenzen

- HTTP nur ohne TLS (siehe [HTTP](../experiments/NetSurf.Wasi/README.md#http)); `https://` zeigt eine Fehlerseite. Cookies nur bis zum Beenden, keine HTTP-Anmeldung, höchstens 4 gleichzeitige Verbindungen. Kein JavaScript. GIF/BMP sind aktiviert; PNG/JPEG/SVG sind ausgeschaltet.
- Festes Browserbild 632×392, kein Resize-Import. Das ZehOS-Fenster zentriert bzw. skaliert es.
- Formulare unterstützen Unicode; die Adressleiste nur ASCII. Strg-Kürzel orientieren sich an der
  physischen US-Tastenposition. Ziffernblock und Navigation haben teilweise dieselben normalisierten Codes.
- Dateien nur lesend im eigenen Paket; keine persistenten Profile, Downloads oder Verzeichnislisten.
- Eine Paket-App zur Zeit; NetSurf muss den gemeinsamen Worker nach dem Schließen freigeben.
- Der finale Recovery-Kernel hat in QEMU ohne Datenplatte bis zur Textshell gebootet.
- Doom-Regression auf Kernel 0.2.0: Tastatur, Fokusreset, Schliessen, Neustart und Beenden ueber das App-Menue bestanden.
- VMware Player: Kernel 0.2.0 ohne Datenplatte seriell bis zur bereiten Shell und zum Desktop gebootet.
  Ein Bildschirmfoto ueber vmrun scheiterte an fehlender Gastanmeldung; Browserbedienung nicht geprueft.
- VMware-Browserbedienung und echte Hardware sind noch nicht geprüft. Ein QEMU-Ergebnis belegt sie nicht.
- Die urspruengliche Annahme fehlender QMP-Eingaben unter KVM ([#56](https://github.com/peterzw98/ProjektZehOS/issues/56)) war falsch: Ohne RDRAND/RDSEED scheiterte die Kontoanlage. Mit `-cpu qemu64,+rdrand,+rdseed` und unterstuetzendem Host bestand die NetSurf-Gastabnahme unter KVM; ohne diese CPU-Merkmale bleibt die Anmeldung nicht moeglich. Die oben dokumentierte Endabnahme lief mit TCG.
- `build.py` baut gperf 3.1 und flex 2.6.4 selbst: gperf 3.1 und 3.3 erzeugen unterschiedlichen Code, Ubuntus gepatchtes flex
  verschiebt Zeilen in der Debuginfo. bison, m4 und perl kommen weiter vom Host;
  byte-gleiche Pakete sind daher nur mit denselben Versionen dieser Werkzeuge belegt (CI: Ubuntu 24.04).
- Paketpfade prüfen symbolische Links nur in der letzten Komponente; auf FAT ist das unkritisch.
- Unter Windows scheitern fünf bestehende Skript-Testfälle an direktem Shellskriptstart bzw. fehlendem
  Symlinkrecht. Dieselben unveränderten Tests bestehen in Linux-CI.

Lizenztexte und `licenses/SOURCES.txt` werden mit dem Paket ausgeliefert; letzteres nennt Archive,
Prüfsummen, Rezept-Commit und die Quellen der statisch gelinkten WASI-Laufzeit.
