# Paketformat und Paketverwaltung

Stand: 15. November 2026. Version 1 des Formats für installierbare Wasm-Apps (Laufzeitprofil `zehos-wasm32-v1`). Implementierung in `ProjektZeh/Packages/`, Shellbefehle in `ProjektZeh/Shell/PackageShellCommands.cs`, Hostwerkzeug `scripts/build-package.py`.

## Repository

Ein Repository ist ein HTTPS-Verzeichnis (oder für `pkg install-local` ein lokaler Paketordner) ohne Archive:

```text
catalog                       signierter Katalog
catalog.sig                   64-Byte-Ed25519-Signatur über catalog
<id>/<version>/manifest       signiertes Manifest
<id>/<version>/manifest.sig   64-Byte-Ed25519-Signatur über manifest
<id>/<version>/files/<pfad>   Paketdateien, genau wie im Manifest gelistet
```

Jede Datei wird mit einem eigenen `GET` ohne Umleitungen, ohne Transfer-/Inhaltscodierung und mit `Content-Length` geladen. Die Zertifikatsprüfung ist dieselbe wie bei `fetch`.

## Manifest

UTF-8, nur LF, Felder in genau dieser Reihenfolge, höchstens 16 KiB:

```text
zehos-package 1
id=doom
version=1.0.0
runtime=zehos-wasm32-v1
name=Doom
entry=doom.wasm
min-kernel=0.3.0
file=<sha256 klein> <bytes> <pfad>
...
end
```

- `id`: `[a-z0-9][a-z0-9-]{0,47}`.
- `version`: drei Dezimalzahlen ohne führende Nullen, wie bei Kernelupdates.
- `name`: Anzeigename im Startmenü, 1–32 druckbare ASCII-Zeichen, ohne Leerzeichen am Rand.
- `file`: höchstens 128 Zeilen, ordinal nach Pfad sortiert, zusammen höchstens 128 MiB. Pfadkomponenten bestehen nur aus `A–Z a–z 0–9 . _ -`, Trenner ist `/`. Abgelehnt werden leere Komponenten, `.`, `..`, abschließende Punkte und Pfade, die sich nur in Groß-/Kleinschreibung unterscheiden (FAT32).
- `entry`: eine der gelisteten Dateien, Endung `.wasm`.
- `capability=net`: optionale einzelne Zeile direkt nach `entry`; sie erlaubt Netzwerkzugriff über `zehos_v1`. Ohne diese Zeile ist der Zugriff gesperrt. Unbekannte oder wiederholte Fähigkeiten werden abgelehnt. Die Berechtigung umfasst auch localhost und private LAN-Ziele, schließt TLS über `ZEHOS_NET_TLS` ein und ist von der Paketsignatur gedeckt. Das Paketwerkzeug setzt sie mit `--network`.
- `min-kernel`: optionale einzelne Zeile nach `entry` beziehungsweise nach `capability=net`, mit der niedrigsten ZehOS-Version (drei Dezimalzahlen), deren Hostfunktionen das Paket braucht. Als Wert dient die Version, die den neuesten verwendeten Import eingeführt hat (etwa 0.2.22 für `pointer_capture`). Das Paketwerkzeug setzt die Zeile mit `--min-kernel X.Y.Z`. Ab ZehOS 0.3.0 gilt: Ist der laufende Kernel älter als der Wert, lehnt `pkg install`/`pkg upgrade` die Version mit „… braucht ZehOS X.Y.Z oder neuer“ ab, bevor eine Datei geladen wird; die installierte Version bleibt unverändert. Auch der Start einer installierten Version wird mit dieser Meldung verweigert.

  Kernel vor 0.3.0 kennen das Feld nicht und verwerfen **jedes** Manifest mit dieser Zeile als ungültig („Ungueltige Dateizeile“), unabhängig vom Wert. Der Katalog bleibt für sie lesbar. Folgen auf solchen Kerneln:

  - Sie installieren und aktualisieren das Paket nicht; `pkg list` zeigt dauerhaft das Update an.
  - Führt der Katalog nur noch diese Version, lässt sich das Paket dort auch nicht mehr neu installieren.
  - Nach einer Rückstufung auf einen solchen Kernel (etwa über den Starteintrag der vorherigen Version) gilt eine schon installierte Version als beschädigt. Sie verschwindet aus Startmenü und `pkg list` und lässt sich nur mit `pkg remove <id>` entfernen.

Die Signatur deckt genau die gespeicherten Bytes ab; ZehOS serialisiert Manifeste nie neu.

## Katalog

UTF-8, LF, höchstens 256 KiB und 128 Einträge, je Paket genau eine Version:

```text
zehos-catalog 1
generation=<n ≥ 1>
package=<id> <version> <sha256 des manifest>
end
```

ZehOS nimmt einen Katalog nur an, wenn seine Generation nicht kleiner ist als die des gespeicherten. `pkg install` lädt das Manifest und prüft, dass sein SHA-256 dem Katalogeintrag entspricht und die Signatur gültig ist. Erst dann wird jede Datei mit Größen- und SHA-256-Prüfung geladen.

## Vertrauensschlüssel

Pakete verwenden einen eigenen Ed25519-Schlüssel, nicht den Kernelupdate-Schlüssel. Der öffentliche Schlüssel kommt nur über `-p:ZehOSPackageKeyPath=<Datei mit Base64 der 32 Bytes>` in einen Build. Ohne ihn sind alle ändernden `pkg`-Befehle gesperrt und es erscheinen keine Paket-Apps. Ein produktiver Herausgeberschlüssel ist noch nicht festgelegt.

Für Tests mit lokalem HTTPS-Repository ergänzt `-p:ZehOSPackageTestCaPath=<PEM>` die TLS-Wurzeln, aber nur für Paketdownloads.

## Paketbestand im Gast

```text
/data/.zehos-packages/source            Repository-URL
/data/.zehos-packages/catalog.signed    Signatur + zuletzt angenommener Katalog
/data/.zehos-packages/apps/<id>/active  aktive Version
/data/.zehos-packages/apps/<id>/<version>/{manifest,manifest.sig,files/...}
```

- **Zugriff:** Der normale Dateidienst und die Dateirechte geben `.zehos-`-Pfade nicht frei. Nur der Paketdienst schreibt dort. Apps lesen ausschließlich ihr eigenes `files/`-Verzeichnis, schreibgeschützt über die WASI-Brücke.
- **Installation:** Dateien werden in das Versionsverzeichnis kopiert und dabei gehasht. Danach werden Manifest und Signatur abgelegt. Zuletzt wird der Marker `active` über eine Ersatzdatei (`active.new`) veröffentlicht. Ein Versionsverzeichnis ohne Marker ist nie startbar. Die alte Version wird erst nach dem neuen Marker gelöscht.
- **Aufräumen beim Einbinden:** Ein halb ersetzter Marker wird abgeschlossen. Pakete ohne Marker und nicht aktive Versionen werden gelöscht. Hinter einem unlesbaren Marker oder einem fehlenden Versionsordner wird nichts gelöscht; das Paket bleibt gesperrt.
- **Start:** Marker und Manifestsignatur werden geprüft, beim ersten Start nach jedem Neustart werden zusätzlich alle Dateien erneut gehasht. Eine veränderte Datei sperrt den Start. Bei Doom dauert das unter QEMU bis zu etwa 30 s; im Fenster steht „Pruefe Paket …“. Danach merkt sich der Kernel die geprüfte Version bis zum Neustart, weil im laufenden System nur der Paketdienst diese Dateien ändern kann.
- **Sperren:** Eine laufende App hält ihre Version. `pkg remove` und `pkg upgrade` lehnen laufende Pakete ab. Zwei Änderungen am selben Paket schließen sich aus.
- **Kein Stromausfallschutz:** FAT32 garantiert keine atomaren Schritte. Unklare Zustände werden beim nächsten Einbinden aufgeräumt oder bleiben gesperrt, bis das Paket neu installiert oder entfernt wird.

## Befehle

```text
pkg list                       installierte und im Katalog verfügbare Pakete
pkg source [<https-URL>/]      Quelle anzeigen oder setzen (Admin)
pkg update                     signierten Katalog laden (Admin, Hintergrundauftrag)
pkg install <id>               aus dem Katalog installieren (Admin, Hintergrundauftrag)
pkg install-local <Ordner>     Paketordner <id>/<version> lokal installieren (Admin)
pkg upgrade [<id>]             auf Katalogversionen aktualisieren, je Paket einzeln (Admin)
pkg remove <id>                Paket entfernen (Admin)
pkg cancel                     Abbruch des laufenden Auftrags dieser Sitzung anfordern
```

IPv4 wird beim Boot automatisch per DHCP konfiguriert; ohne DHCP-Server ist `net static` nötig. `install` und `upgrade` laden zuerst den signierten Katalog und brechen bei einem Abruffehler ab. Während des Auftrags erscheint in der aufrufenden Shell eine ersetzbare Statuszeile mit Phase, Paket/Datei, Balken, Prozent und Bytes. Das Ergebnis folgt nach Abschluss automatisch und zusätzlich seriell als `PKG …`. Standardquelle ist `https://packages.pjoter.de/`; `pkg source` setzt bei Bedarf eine eigene HTTPS-Quelle. Gespeicherte eigene Quellen bleiben erhalten. Eine vorherige manuelle Katalogaktualisierung ist nicht erforderlich.

## Paket bauen

```bash
python scripts/build-package.py keygen <Schlüsselordner>
python scripts/build-package.py package --key <Schlüsselordner>/private.pem --repo <repo> --id doom --version 1.1.0 --name Doom --entry doom.wasm --min-kernel 0.2.22 <doom.wasm>=doom.wasm <freedoom1.wad>=freedoom1.wad
python scripts/build-package.py catalog --key <Schlüsselordner>/private.pem --repo <repo> --generation 1
```

Den privaten Schlüssel nicht ins Repository legen. Veröffentlichte Versionen sind unveränderlich; eine Änderung braucht eine neue Version und eine höhere Katalog-Generation. Der Katalog führt je Paket nur die höchste Version; ältere Versionsordner bleiben im Repository liegen, werden aber nicht mehr angeboten.

## Server und Prüfstand

Das öffentliche Repository liegt unter `https://packages.pjoter.de/`. Ausgeliefert wird es von `deploy/package-repo` (Docker-Compose mit nginx und einem `updater`, der den Branch `packages` alle 5 Minuten nachzieht). Neue Pakete werden mit dem privaten Herausgeberschlüssel gebaut und per Pull Request in den Branch `packages` gebracht; nach dem Merge übernimmt der Server sie von selbst.

Geprüft am 15. November 2026 in QEMU mit `scripts/test-wasm-doom-desktop.py`. VMware und echte Hardware sind nicht geprüft.

Das Skript baut aus dem Doom-Testrepository (`--repo`) ein eigenes Repository mit der kleinen App `scripts/guest-testapp` (1.0.0, im Upgrade-Repository 1.1.0) und signiert es mit dem Testschlüssel (`--key`). Die Paketlogik (`remove`, `reboot`, `upgrade`, `corrupt`, `minkernel`) läuft mit dieser App in Sekunden statt mit dem 28-MB-Doom. Doom wird einmal über HTTPS installiert und im Szenario `full` gespielt. Unter TCG richtet eine Vorbereitungs-VM Konto, Netz und Katalog ein, installiert Doom und speichert den Zustand (`savevm`). Alle Szenarien starten dann parallel aus Kopien dieses Snapshots. Er liegt je ISO und Repository unter `.tools/guest-snapshots` und wird wiederverwendet.

```powershell
# Schneller Satz für jede Änderung (remove, upgrade, corrupt, minkernel)
py -3 scripts/test-wasm-doom-desktop.py --iso <Test-ISO> --repo <doom-repo> --key <private.pem> --tls <tls>
# Vollständig vor dem Pull Request, mit Speicherprüfung über zehn Doom-Starts
py -3 scripts/test-wasm-doom-desktop.py --iso <Test-ISO> --repo <doom-repo> --key <private.pem> --tls <tls> --scenario all --starts 10
```

Für einen automatisierten Langlauf ergänzt `--scenario full --starts 10 --long-seconds 300` nach den Neustarts eine mindestens fünfminütige Sitzung mit Bewegungs-, Dreh-, Schuss- und Benutzungsereignissen. Die Benutzungstaste startet nach einem Tod erneut. Die Bildschirmfotos und Taskmanagerwerte müssen ausgewertet werden; der bloße Skripterfolg belegt weder menschliches Spielen noch die Bildrate von Doom.

`--accel whpx` läuft ohne Snapshot, weil QEMU unter WHPX kein `savevm` erlaubt; jede VM richtet sich dann selbst ein. `--accel kvm` läuft ebenfalls ohne Snapshot, weil `savevm` unter KVM nicht geprüft ist; die VM startet mit `-cpu qemu64,+rdrand,+rdseed` (siehe `COSMOS_SUPPORT.md`). Dasselbe gilt für `--no-snapshot` und für `--source` (nur Szenario `full`). Voraussetzungen sind wasi-sdk 25 (`--wasi-sdk`, sonst `WASI_SDK_PATH` oder `.tools/wasi-sdk-25.0-x86_64-windows`), OpenSSL für `build-package.py` und `qemu-img` neben QEMU. Ändern sich ISO, Repository, QEMU-Version oder Geräte, legt das Skript einen neuen Snapshot an; ändert sich der Vorbereitungsablauf im Skript, ist `SNAPSHOT_FORMAT` zu erhöhen.

Laufzeiten am 15. November 2026 (Windows, QEMU 10.2.2, alle Szenarien bestanden):

| Lauf | Dauer |
| --- | --- |
| TCG, `--scenario all`, mit Erstellung des Snapshots (Vorbereitung 121 s) | 226 s |
| TCG, `--scenario all`, Snapshot wiederverwendet | 104 s |
| TCG, `--scenario all --starts 10`, Snapshot wiederverwendet | 164 s |
| WHPX, `--scenario all`, ohne Snapshot | 198 s |

| Prüfung (Stand dieses Skripts) | Ergebnis |
| --- | --- |
| Vorbereitung: `pkg install doom` (28 MB über HTTPS, lokales Testrepository) | installiert in 68 s (TCG) |
| `full`: F4/Esc, Schließen über X, Starts mit Beenden über das Doom-Menü | alle Starts laufen. Freier Speicher nach jedem Lauf (`WASM Speicher`, nach dem Aufräumen) mit `--starts 10`: TCG 415,3 → 414,2 MiB, WHPX 415,5 → 414,6 MiB. Das Skript lässt den Test scheitern, wenn er um mehr als 8 MiB sinkt. Ein früherer Wert von 392 → 353 MiB stammte von einem Taskmanager-Foto direkt nach `APP doom beendet`, also bevor der Worker gesammelt hatte. Eine gesonderte Messreihe (je sechs Läufe, Doom 2 s bzw. 30 s nach dem ersten Bild beendet, danach 60 s Ruhe) blieb in beiden Fällen bei 393 MiB ± 0,3: kurze und lange Läufe geben ihren Speicher gleich vollständig frei |
| `remove`: Entfernen, während testapp läuft | abgelehnt: „Paket testapp laeuft gerade; zuerst die App schliessen.“ |
| `remove`: Entfernen, Startmenü, `pkg install-local /data/pkgs/testapp/1.0.0` | entfernt, „Keine Programme gefunden“, lokal wieder installiert und gestartet |
| `reboot`: Neustart mit Anmeldung | testapp bleibt installiert und startet |
| `upgrade`: Katalog-Generation G+1, `pkg upgrade` | `testapp 1.1.0 aktualisiert`, startet in der Farbe von 1.1.0 |
| `corrupt`: Server liefert veränderte `testapp.wasm` | „Pruefsumme falsch: testapp.wasm“, nichts installiert |
| `minkernel`: testapp 1.1.0 mit `min-kernel=99.0.0`, `pkg upgrade` (16. November 2026, TCG, Kernel 0.3.0) | „testapp 1.1.0 braucht ZehOS 99.0.0 oder neuer; laufender Kernel ist 0.3.0.“, 1.0.0 bleibt installiert und startet. `pkg list` zeigt weiter „Update 1.1.0“, weil der Katalog keine Mindestversion enthält |

Seit der Umstellung auf testapp prüft das Skript nicht mehr: das Upgrade eines großen Pakets (28 MB neben der alten Version), einen Prüfsummenfehler in einer großen gestreamten Datei und die Neuinstallation von Doom über das Netz nach dem Entfernen. Diese Pfade nutzen denselben Code wie bei testapp. Frühere Doom-Ergebnisse (ebenfalls 15. November 2026, TCG): Installation von packages.pjoter.de mit normal gebautem Kernel in 120 s; zehn Starts mit je 30 s Laufzeit, frei 416 → 414 MiB; `doom 1.1.0 aktualisiert`; „Pruefsumme falsch: freedoom1.wad“.

## Hostdienste für grafische Paket-Apps

Zusätzlich zu `poll_key()` bietet `zehos_v1.poll_event(ptr) -> i32` eine unabhängige, begrenzte
Ereigniswarteschlange für Tasten, Unicode-Text, Zeiger, alle drei Maustasten und Rad.
Der Import schreibt genau vier Little-Endian-`i32` (`type, code, x, y`, 16 Byte) und liefert
1 bei einem Ereignis, 0 ohne Ereignis, -1 bei Hostfehler. Ein ungültiger Gastbereich trappt vor
Entnahme. Fokusverlust/Überlauf melden Reset; Skalierung und Zentrierung werden zurückgerechnet.
Seit 0.2.22 fängt `zehos_v1.pointer_capture(on) -> i32` den Zeiger ein; dann kommen Bewegungen als
relativer Typ 9. PS/2-Rohbewegungen bleiben am Gastbildschirmrand erhalten; absolute Tablets bleiben auf ihre Positionsgrenzen beschränkt. Esc, Fokusverlust und Schließen geben ihn frei. Das erste Esc bei Zeigerfang erreicht die App; ein weiteres Esc führt zur Textshell. F4 wechselt jederzeit zur Textshell.
Seit 0.5.1 meldet Typ 10 die Größe des Fensterinhalts.
Die vollständige Typentabelle und die Grenze normalisierter Tastencodes stehen in
[PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md#was-eine-app-vom-kernel-bekommt).
Zusätzlich fragt `path_filestat_get` Dateityp und Größe unter derselben schreibgeschützten Paketgrenze ab;
`path_readlink` liefert für bestehende Nicht-Links `EINVAL`. Verzeichnislisten bleiben gesperrt.
Diese Ergänzungen ändern weder das Paketformat noch die Laufzeitkennung `zehos-wasm32-v1`.

Die native Grenze lässt sich mit `python scripts/tests/test_wasm_input_native.py` prüfen
(vorhandener GCC erforderlich). Der Test baut die echte C-Brücke mit wasm3 auf dem Host;
er ersetzt keinen Kernel-/Gasttest.

## Bewusste Grenzen von Version 1

- Nur grafische `zehos_v1`-Apps im Desktopfenster; Konsolen-Apps und ein Shellbefehl `run` fehlen.
- Eine Paket-App zur Zeit (gemeinsamer Wasm-Worker).
- Keine Benutzerdaten-/Speicherstandsverzeichnisse; Apps lesen nur ihr Paket.
- `install-local` liest den Ordner direkt (nur Administratoren), weil von außen auf das Laufwerk kopierte Dateien keine Rechteeinträge haben.
- Kein Speicherplatztest vorab; ein voller Datenträger bricht die Installation mit Dateifehler ab und entfernt das unvollständige Versionsverzeichnis.
- Wasm-Loader und `pkg` sind im normalen Kernelbuild standardmäßig enthalten. Ein ausdrücklich mit `-p:ZehOSWasm=false` gebauter Kernel und das Installer-ISO bieten keinen `pkg`-Befehl.
- Eine App, die `should_exit` nie abfragt, lässt sich nicht beenden; ihr Paket bleibt bis zum Neustart gesperrt.
- Downloads haben nur ein Zeitlimit je Datenpaket, kein Gesamtlimit. Ein sehr langsamer Server hält die Netzwerksperre, bis `pkg cancel` den Auftrag abbricht.
- Das Startmenü erkennt ein bereits offenes Fenster am Anzeigenamen; Paketnamen sollten deshalb eindeutig sein.
- Veränderte Paketdateien sperren den Start; `pkg install <id>` derselben Version repariert sie.
