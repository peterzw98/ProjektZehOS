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
- `capability=storage`: optionale einzelne Zeile nach `entry` beziehungsweise nach `capability=net` (feste Reihenfolge: erst `net`, dann `storage`). Sie gibt der App einen eigenen, schreibbaren App-Speicher `/save` (Etappe 13.4, Vertrag in [PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md)). Das Paketwerkzeug setzt sie mit `--storage`. Kernel ohne App-Speicher halten die Zeile für eine ungültige Dateizeile und installieren das Paket nicht; ein `min-kernel` ist dafür nicht nötig.
- `min-kernel`: optionale einzelne Zeile nach `entry` beziehungsweise nach den Fähigkeiten, mit der niedrigsten ZehOS-Version (drei Dezimalzahlen), deren Hostfunktionen das Paket braucht. Als Wert dient die Version, die den neuesten verwendeten Import eingeführt hat (etwa 0.2.22 für `pointer_capture`). Das Paketwerkzeug setzt die Zeile mit `--min-kernel X.Y.Z`. Ab ZehOS 0.3.0 gilt: Ist der laufende Kernel älter als der Wert, lehnt `pkg install`/`pkg upgrade` die Version mit „… braucht ZehOS X.Y.Z oder neuer“ ab, bevor eine Datei geladen wird; die installierte Version bleibt unverändert. Auch der Start einer installierten Version wird mit dieser Meldung verweigert.

  Kernel vor 0.3.0 kennen das Feld nicht und verwerfen **jedes** Manifest mit dieser Zeile als ungültig („Ungueltige Dateizeile“), unabhängig vom Wert. Der Katalog bleibt für sie lesbar. Folgen auf solchen Kerneln:

  - Sie installieren und aktualisieren das Paket nicht; `pkg list` zeigt dauerhaft das Update an.
  - Führt der Katalog nur noch diese Version, lässt sich das Paket dort auch nicht mehr neu installieren.
  - Nach einer Rückstufung auf einen solchen Kernel (etwa über den Starteintrag der vorherigen Version) gilt eine schon installierte Version als beschädigt. Sie verschwindet aus Startmenü und `pkg list` und lässt sich nur mit `pkg remove <id>` entfernen.

Die Signatur deckt genau die gespeicherten Bytes ab; ZehOS serialisiert Manifeste nie neu.

## AOT-Datei (optional, AOT-Plan Etappe E)

Ein Paket kann neben `entry` eine vom Herausgeber mit `wamrc` übersetzte Fassung derselben App enthalten. Es gibt dafür kein eigenes Manifestfeld: Die Datei steht als gewöhnliche `file`-Zeile im Manifest (ordinal sortiert) und ist damit von Hash und Signatur gedeckt.

- **Namensregel:** `<entry ohne .wasm>.<aot-abi>.aot` im selben Ordner wie `entry`, etwa `doom.wamr245-aot5-x64-bc-nrz.aot` zu `doom.wasm`.
- **`<aot-abi>`** ist eine Kernelkonstante (`WasmEngineSelection.AotAbi`), derzeit `wamr245-aot5-x64-bc-nrz`: WAMR 2.4.5, AOT-Formatversion 5, Ziel x86-64, Speicher- und Stackprüfung (`--bounds-checks=1 --stack-bounds-checks=1`, Schalter in `ProjektZeh/Native/AotProbe/fixture/compile-aot.sh`), `nrz`: übersetzt mit dem wamrc-Patch `fixture/wamrc-noredzone.patch` ohne Red Zone; `scripts/build-package.py` prüft das zusätzlich am Ergebnis (`scripts/check-aot-redzone.py`, kein Zugriff unter RSP). Kernel vor diesem Wechsel nutzten die Kennung `wamr245-aot5-x64-bc` (wamrc ohne Patch); solche Dateien lädt ein neuer Kernel nicht mehr. Ein Kernel lädt nur ein `.aot` mit genau seiner Kennung; jede andere Datei mit Endung `.aot` ist für ihn gewöhnliche Paketdatei. Ändert sich WAMR-Version, Format oder Prüfschalter, ändert sich die Kennung, und ältere `.aot` werden ignoriert.
- **Auswahl beim Start:** AOT nur, wenn der Kernel WAMR enthält (Standard; nicht in Recovery, Installer und mit `-p:ZehOSWasmAot=false`), der Schalter `wasmaot` an ist (Standard an) und die passende Datei im Manifest steht. Ist die Datei laut Manifest leer oder größer als 64 MiB, startet die App unter wasm3. Sonst liest der Kernel sie mit einem einzigen Dateizugriff vollständig in den Speicher und prüft genau diese Bytes gegen den SHA-256 des Manifests, bei jedem Start. Nur dieser Puffer geht an WAMR. Passen Größe oder Hash nicht, ist der Start wie bei jeder veränderten Paketdatei gesperrt („Paketdatei … wurde veraendert; Start gesperrt.“).
- **Rückfall:** Scheitert das `.aot`, bevor WAMR mit dem Instanziieren beginnt (Format, Laden, Speicherobergrenze des Moduls, Speicher für die Kopie), startet dieselbe App unter wasm3. Scheitert es danach (Instanziieren, fehlender oder unpassender Einstieg, Ausführungsumgebung) oder trappt es, gibt es keinen zweiten Versuch: Beim Instanziieren kann schon Gastcode gelaufen sein (start-Funktion, Konstruktoren). Das Fenster zeigt dann den AOT-Fehler. Das serielle Protokoll zeigt die Laufzeit jedes App-Starts als `WASM Start <id> Laufzeit=wasm3` beziehungsweise `WASM Start <id> Laufzeit=aot wamr245-aot5-x64-bc-nrz`; ein Rückfall ergänzt genau eine Zeile `WASM Start <id> Laufzeit=wasm3 AOT-Rueckfall: <Status> <Meldung>`.
- **Nach einem CPU-Fehler unter AOT:** Beendet eine isolierte CPU-Ausnahme einen AOT-Lauf, bleibt WAMRs Laufzeitzustand halb benutzt zurück. Jeder weitere AOT-Start endet deshalb bis zum Neustart vor dem Instanziieren und fällt auf wasm3 zurück (`wasm3 AOT-Rueckfall: LoadFailed AOT nach CPU-Fehler bis zum Neustart gesperrt`, `zehos_host_aot_guard.h`). Ein CPU-Fehler unter wasm3 sperrt AOT nicht. Gasttest: Szenario `aotfault` in `scripts/test-wasm-doom-desktop.py`.
- **Ältere Kernel** (vor Etappe E) und Kernel ohne WAMR behandeln das `.aot` als Daten: Das Manifest nimmt beliebige zusätzliche `file`-Zeilen an (`PackageManifest.Parse`), `pkg install` lädt und prüft die Datei wie jede andere, und der Start nutzt nur `entry`. `min-kernel` ist dafür nicht nötig. Die Datei zählt aber gegen die 128 MiB Paketinhalt und vergrößert den Download auch dort.
- **Grenzen:** Unter AOT gibt es kein Gasbudget; Paket-Apps laufen auch unter wasm3 ohne Budget, Läufe mit Budget bleiben bei wasm3. Eine App, die `should_exit` nie abfragt, beendet der Kernel 5 s nach dem Schließen am nächsten Prüfpunkt: unter wasm3 an jedem Host- und Wasm-Funktionsaufruf und jedem Schleifendurchlauf, unter AOT an Hostaufrufen und, in einer Schleife ohne Hostaufruf, beim nächsten Scheduler-Tick im übersetzten Code (Cosmos ab smp.13). Das `.wasm` selbst wird nach `VerifyOnce` (einmal je Paketversion und Kernellauf) ohne erneuten Hash gelesen; eine spätere Änderung daran fällt erst nach einem Neustart auf. NetSurf lädt unter WAMR 2.4.5 nicht (Ausnahmen im exnref-Format) und erhält kein `.aot`. Der Kernel kann nicht prüfen, ob das `.aot` wirklich mit Speicherprüfung übersetzt wurde; das sichern Herausgeberwerkzeug und Signatur ([Sicherheitsbewertung](superpowers/specs/2026-11-17-aot-sicherheit.md)).

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

Den privaten Schlüssel nicht ins Repository legen. Veröffentlichte Versionen sind unveränderlich; eine Änderung braucht eine neue Version und eine höhere Katalog-Generation. Der Katalog führt je Paket nur die höchste Version; ältere Versionsordner bleiben im Repository liegen, werden aber nicht mehr angeboten. Bricht `package` ab, entfernt das Werkzeug den angefangenen Versionsordner; `catalog` berücksichtigt nur Versionen mit `manifest.sig`.

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

Mehrere Gastläufe (auch mit verschiedenen `ZEHOS_QEMU_SMP`) arbeitet `scripts/run-guest-suite.py` im Hintergrund und parallel ab, statt sie nacheinander im Vordergrund abzuwarten. Die Suite ist eine JSON-Liste (Beispiel `scripts/guest-suites/e5j.json`, Platzhalter `{iso}` usw. kommen aus `--var`; die dortigen `note`-Felder nennen ungeprüfte Annahmen):

```powershell
py -3 scripts/run-guest-suite.py scripts/guest-suites/e5j.json --out .tools/suite-1 --var iso=<ISO> --var key=<private.pem> --detach  # gibt den Ordner aus und kehrt zurück
py -3 scripts/run-guest-suite.py --status .tools/suite-1   # summary.md; Exit 0 alles bestanden, 1 Fehlschlag, 2 läuft noch
```

E4.0-Qualifikation (`scripts/guest-suites/e4-0.json`): Standard sind **3 Wiederholungen** (nicht 5). Aufruf mit `--parallel 3` (die Wiederholungen laufen gleichzeitig); darin startet `test-storage-ext2-root.py` die Abbruchläufe mit `--parallel 2`, es laufen also bis zu 3 × 2 QEMU-Prozesse zugleich. Die QMP-Ports reserviert `free_port()` in `scripts/test-wasm-doom-desktop.py` prozessübergreifend per `flock`-Sperrdatei (`<tmp>/zehos-qmp-ports-<uid>/<port>.lock`), damit zwei Prozesse nie denselben Port ziehen.

```sh
python3 scripts/run-guest-suite.py scripts/guest-suites/e4-0.json --out .tools/suite-e40 --var iso=<ISO mit -p:ZehOSStorageRootProbe=true> --parallel 3 --detach
```

Jeder Lauf schreibt `<name>-<i>/log.txt` unter `--out`; `summary.json` und `summary.md` werden nach jedem Lauf neu geschrieben. `--parallel N` begrenzt die gleichzeitigen Läufe (Standard `min(4, CPUs/2)`), ein Lauf über seinem `timeout` wird samt Kindprozessen beendet.

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
| `aot`: Pakete mit `.aot` (Fixture-Bytes aus `ProjektZeh/Native/AotProbe`), Kernel mit `-p:ZehOSWasmAot=true` (17. November 2026, WHPX und TCG) | AOT aus: `Laufzeit=wasm3`, erstes Bild. Nach `wasmaot on`: `Laufzeit=aot wamr245-aot5-x64-bc`, Exitcode 7 der Fixture. Fremde Kennung: `Laufzeit=wasm3`. `.aot` mit falscher Formatversion: `AOT-Rueckfall: InvalidModule`, danach erstes Bild unter wasm3. `.aot` ohne `_start`: „App abgebrochen: EntryMissing“, kein wasm3-Neustart. Die Startsperre bei einem nachträglich veränderten `.aot` ist nur hostgetestet: Die Shell darf nicht in den Paketbestand schreiben (`write` meldet „Zugriff verweigert“). (Lauf mit der damaligen Kennung `wamr245-aot5-x64-bc`; mit `-nrz` nicht erneut im Gast geprüft) |
| `aotold`: dasselbe Paket auf master ed92c7a (0.5.38, ohne AOT-Auswahl, WHPX) | installiert, startet unter wasm3 |
| `aotcorrupt`: Server liefert ein verändertes `.aot` (WHPX) | „Pruefsumme falsch: testapp.wamr245-aot5-x64-bc.aot“, nichts installiert (Lauf mit der damaligen Kennung `wamr245-aot5-x64-bc`; mit `-nrz` nicht erneut im Gast geprüft) |

Seit der Umstellung auf testapp prüft das Skript nicht mehr: das Upgrade eines großen Pakets (28 MB neben der alten Version), einen Prüfsummenfehler in einer großen gestreamten Datei und die Neuinstallation von Doom über das Netz nach dem Entfernen. Diese Pfade nutzen denselben Code wie bei testapp. Frühere Doom-Ergebnisse (ebenfalls 15. November 2026, TCG): Installation von packages.pjoter.de mit normal gebautem Kernel in 120 s; zehn Starts mit je 30 s Laufzeit, frei 416 → 414 MiB; `doom 1.1.0 aktualisiert`; „Pruefsumme falsch: freedoom1.wad“.

## Hostdienste für grafische Paket-Apps

Zusätzlich zu `poll_key()` bietet `zehos_v1.poll_event(ptr) -> i32` eine unabhängige, begrenzte
Ereigniswarteschlange für Tasten, Unicode-Text, Zeiger, alle drei Maustasten und Rad.
Der Import schreibt genau vier Little-Endian-`i32` (`type, code, x, y`, 16 Byte) und liefert
1 bei einem Ereignis, 0 ohne Ereignis, -1 bei Hostfehler. Ein ungültiger Gastbereich trappt vor
Entnahme. Fokusverlust/Überlauf melden Reset; Skalierung und Zentrierung werden zurückgerechnet.
Seit 0.2.22 fängt `zehos_v1.pointer_capture(on) -> i32` den Zeiger ein; dann kommen Bewegungen als
relativer Typ 9. Ab 0.5.2 bleiben PS/2-Rohbewegungen am Gastbildschirmrand erhalten; absolute Tablets bleiben auf ihre Positionsgrenzen beschränkt. Ab 0.5.128 zeichnet der Desktop bei Zeigerfang keinen Zeiger. Esc, Fokusverlust und Schließen geben ihn frei. Das erste Esc bei Zeigerfang erreicht die App; ein weiteres Esc führt zur Textshell. F4 wechselt jederzeit zur Textshell.
Seit 0.5.1 meldet Typ 10 die Größe des Fensterinhalts.
Seit 0.5.13 zeichnen `zehos_v1.raster_draw` und `raster_fill` Quads nativ in Puffer im Speicher der App; die Umgebung meldet das mit `ZEHOS_FEATURES=raster_v1` (Einzelheiten in [PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md#was-eine-app-vom-kernel-bekommt)). Ein Paket kann die Imports nutzen, ohne `min-kernel` zu erhöhen, wenn es sie nur bei gemeldeter Fähigkeit aufruft.
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
- Eine App, die `should_exit` nie abfragt, beendet der Kernel 5 s nach dem Schließen am nächsten Prüfpunkt (Host- oder Wasm-Funktionsaufruf, unter wasm3 auch jeder Schleifendurchlauf). Unter AOT beendet der Scheduler-Tick auch eine Schleife ohne Hostaufruf, solange der Lauf im übersetzten Code steht ([Grenzen](PAKETE_ERSTELLEN.md#grenzen)).
- Downloads haben nur ein Zeitlimit je Datenpaket, kein Gesamtlimit. Ein sehr langsamer Server hält die Netzwerksperre, bis `pkg cancel` den Auftrag abbricht.
- Das Startmenü erkennt ein bereits offenes Fenster am Anzeigenamen; Paketnamen sollten deshalb eindeutig sein.
- Veränderte Paketdateien sperren den Start; `pkg install <id>` derselben Version repariert sie.

## Format 2 (über `pkg native`, noch ohne Programmstart)

Vorarbeit für native Pakete (Laufzeit `linux-x64-v1`, [Plan](superpowers/plans/2026-11-24-ladybird-p-paketdienst-vorarbeit.md), [Entwurf, Abschnitt 7](superpowers/specs/2026-11-24-native-pakete-design.md#7-paketdienst-v2)). Seit N3-B1 erreicht die Shell den Dienst über `pkg native` (Abschnitt „Dienst und Index“); Programmstart (N3-E) und Server (N3-G) sind noch nicht angeschlossen. Alle übrigen `pkg`-Befehle lesen und schreiben weiter nur Version 1. Vertrauen und Signatur sind dieselben wie bei Version 1 (`PackageTrust`).

### Manifest

Implementierung `ProjektZeh/Packages/PackageManifest2.cs` (`PackageManifest2.Parse`, wirft nie). `Bytes` ist eine Kopie der Eingabe, aus der auch geparst wurde; die Signatur ist über genau diese Bytes zu prüfen. Fehlermeldungen geben Eingabetext nur gekürzt (höchstens 64 Zeichen) und auf druckbares ASCII gefiltert wieder. UTF-8 ohne BOM, nur LF (ein CR irgendwo ist ungültig), jede Zeile mit LF abgeschlossen, Felder genau in dieser Reihenfolge, höchstens 1 MiB:

```text
zehos-package 2
id=hello
version=1.2.3
release=4
runtime=linux-x64-v1
license=MIT OR Apache-2.0
name=Hallo Welt
entry=usr/bin/hello
capability=net
capability=storage
capability=files
memory-mib=256
min-kernel=0.5.0
depends=musl>=1.2.5-r1
provides=so:libhello.so.1
exec=<sha256 klein> <bytes> usr/bin/hello
file=<sha256 klein> <bytes> usr/lib/libhello.so.1
link=usr/lib/libhello.so libhello.so.1
end
```

- Pflicht sind `id`, `version`, `release`, `runtime`, `license` (lückenlos am Anfang) und mindestens eine `exec`- oder `file`-Zeile. Unbekannte Schlüssel, Leerzeilen, doppelte Einzelfelder und Felder an falscher Stelle sind ungültig.
- `id`, `version`: wie Version 1. `release`: Paketrevision 1–9999 ohne führende Null. Version und Revision zusammen schreiben sich kanonisch `1.2.3-r4` (`PackageVersion2`); verglichen wird erst die Version, dann die Revision.
- `runtime`: genau `linux-x64-v1`.
- `license`: SPDX-Ausdruck, 1–64 Zeichen aus `A–Z a–z 0–9 . + - ( )` und Leerzeichen. Die SPDX-Grammatik prüft der Kernel nicht.
- `name`: wie Version 1, nur zusammen mit `entry`. `entry`: Pfad einer `exec`-Zeile (nicht `file`, nicht `link`). Bibliotheken haben beides nicht; ein Programm darf `entry` ohne `name` haben.
- `capability=net|storage|files`: je höchstens einmal, in dieser Reihenfolge.
- `memory-mib`: 1–4096, nur zusammen mit `entry`. `min-kernel`: wie Version 1.
- `depends=<id>>=<version>-r<release>`: 0–64 Zeilen, ordinal nach `id`, keine doppelte `id`, nicht das eigene Paket (`PackageDependency`).
- `provides=so:<soname>`: 0–64 Zeilen, ordinal, `<soname>` 1–200 Zeichen aus `A–Z a–z 0–9 . _ + -`. `Provides` enthält den Wert samt `so:`.
- `exec`/`file`: Aufbau wie `file` in Version 1; `exec` markiert ausführbare Dateien. Beide zusammen ordinal nach Pfad, höchstens 4096, Summe höchstens 1 GiB (exakt 1 GiB ist erlaubt).
- `link=<pfad> <ziel>`: höchstens 4096, ordinal nach Pfad. Das Ziel ist kanonisch relativ zum Ordner des Links: null oder mehr führende `../`, danach mindestens eine normale Komponente (kein `.`, kein `..` mitten im Pfad, kein `/` am Anfang oder Ende). Das aufgelöste Ziel muss selbst ein gültiger Pfad nach der Regel unten sein (unter `usr/`, `lib/` oder `etc/`, höchstens 200 Zeichen); Ziele wie `/dev`, `/data`, `/save` oder `/tmp` sind damit ausgeschlossen. Weil `..` nur vorne steht und kein Link Ordner eines anderen Eintrags sein kann, entspricht die textuelle Auflösung der tatsächlichen.
- Linkketten: Der Parser folgt jedem Ziel durch die Links desselben Manifests (ohne Groß-/Kleinschreibung). Ungültig ist, wenn ein Schritt auf den Link selbst, einen seiner Ordner oder einen Pfad unter ihm zeigt, mehr als 40 Links aufeinander folgen (damit auch jeder Kreis über mehrere Links) oder der Pfad dabei länger als 200 Zeichen würde. Links auf Pfade anderer Pakete kann erst der Bestand (P3) prüfen.
- Pfade (auch von `entry` und `link`): wie Version 1, dazu `+` als Zeichen, höchstens 200 Zeichen, beginnend mit `usr/`, `lib/` oder `etc/`. Doppelte Pfade (auch nur in Groß-/Kleinschreibung verschieden, auch `link` gegen `exec`/`file`) sind ungültig, ebenso ein Pfad, der zugleich Ordner eines anderen ist.

### Katalog

Implementierung `ProjektZeh/Packages/PackageCatalog2.cs` (`PackageCatalog2.Parse`, wirft nie) und `PackageResolver.cs`. Die Signatur ist wie bei Version 1 über genau die übergebenen Bytes zu prüfen (`PackageTrust`); `Parse` prüft nur die Form. Meldungen geben Eingabetext wie beim Manifest nur gekürzt und auf druckbares ASCII gefiltert wieder. UTF-8 ohne BOM, nur LF, höchstens 4 MiB und 4096 Einträge:

```text
zehos-catalog 2
generation=<n ≥ 1, ohne führende Null, höchstens zehn Ziffern>
package=<id> <version>-r<release> <sha256 des Manifests, klein> <Summe der Inhaltsbytes, höchstens 1 GiB>
depends=<id>>=<version>-r<release>
provides=so:<soname>
end
```

- **Signatur vor `Parse`:** Ein Katalog mit 4 MiB braucht beim Parsen etwa 60 MiB Speicher (gemessen vom Reviewer). Aufrufer (P3/#641, P4) prüfen deshalb zuerst die Signatur über die Rohbytes und parsen erst dann; ein ungeprüfter Katalog wird nie geparst.
- Einträge strikt ordinal nach `id`, keine doppelte `id`. `depends` und `provides` gehören zur `package`-Zeile darüber, erst alle `depends`, dann alle `provides`; Regeln und Grenzen (höchstens je 64, ordinal, keine doppelte Zeile, nicht das eigene Paket) wie im Manifest. Je Paket steht genau eine Version im Katalog.
- Vor der Aktivierung müssen `depends`, `provides` und die Inhaltssumme von Katalog und Manifest übereinstimmen; das prüft die Installation (Plan P3), nicht `Parse`. Für Kernel, die nur Version 1 kennen, ändert sich nichts: Der Katalog v1 bleibt eine eigene Datei mit eigener Kopfzeile.

**Auflösung** (`PackageResolver`, rein rechnerisch, noch nicht angeschlossen): Die Ergebnisse sind Pläne, keine Installationen; Signatur-, Hash- und Dateiprüfung bleiben bei der Installation.

- `Install(catalog, installed, ids)`: Einträge für die angeforderten Pakete samt Abhängigkeiten, Abhängigkeiten zuerst, sonst ordinal nach `id` (Angeforderte ordinal, Abhängigkeiten je Paket in ihrer Katalogreihenfolge); leer, wenn nichts zu tun ist. Wird ein Paket erneuert oder neu gebracht, steht es vor jedem Paket der Zielmenge, das von ihm abhängt, auch wenn der alte Bestand dessen Mindestversion schon erfüllt hätte. Ein angefordertes Paket ist erledigt, wenn die installierte Version gleich oder neuer ist, eine Abhängigkeit, wenn die installierte Version die Mindestversion erfüllt. Es wird nie auf eine kleinere Version gewechselt. Fehlt ein Paket im Katalog (NotFound), ist die Katalogversion einer Abhängigkeit kleiner als die Mindestversion („zu alt“), ist ein Zyklus von den Angeforderten aus erreichbar (auch wenn das Ziel selbst nicht darin liegt) oder liefern zwei Pakete der Zielmenge dasselbe `provides`, scheitert alles (Failure); nichts wird teilweise geplant. Zyklen, die von den Angeforderten aus nicht erreichbar sind, stören nicht. Das `provides` der installierten Pakete kennt der Resolver nicht (`InstalledPackage2` trägt es nicht); das prüft die Aktivierung.
- `Upgrade(catalog, installed)`: erneuert jedes installierte Paket mit größerer Katalogversion samt neuen oder zu alten Abhängigkeiten; scheitert eine Mindestversion der neuen Fassung, scheitert der ganze Plan.
- `CheckRemove(installed, id)`: Failure mit den abhängigen installierten Paketen (ordinal, höchstens acht genannt), NotFound, wenn `id` nicht installiert ist.
- `Unused(installed)`: nicht angeforderte Pakete, die von keinem angeforderten Paket über `depends` erreichbar sind, ordinal; eine Kette oder ein Kreis aus lauter nicht angeforderten Paketen zählt ganz dazu. Entfernen müsste in umgekehrter Abhängigkeitsreihenfolge geschehen.
- Grenzen: Bestand und Anforderungen je höchstens 4096; die Suche ist iterativ, eine Kette über 4096 Pakete braucht keinen Stack je Stufe. Ein doppelter Bestandseintrag ist InvalidArgument.

### Bestand

Implementierung `ProjektZeh/Packages/PackageStore2.cs` (`PackageStore2`). Liegt neben dem Bestand von Version 1 unter derselben Wurzel; `apps/`, `catalog.signed` und `source` werden nie angefasst.

```text
/data/.zehos-packages/v2/active2                                   aktive Menge
/data/.zehos-packages/v2/<id>/<version>-r<release>/{manifest,manifest.sig,files/...}
```

`active2` (ASCII, nur LF, höchstens 1 MiB, höchstens 4096 Pakete, ordinal nach `id`, keine `id` doppelt):

```text
zehos-active 2
package=hello 1.0.0-r1 requested <sha256 des Manifests>
package=musl 1.2.5-r1 auto <sha256 des Manifests>
end
```

- **Stufen** (`Stage`, mit `id`, Version und Manifest-Hash aus dem Katalog): Manifest und Signatur aus `<id>/<version>-r<release>/` der Quelle laden, Hash gegen den Katalog, Signatur prüfen, **erst danach** parsen (der Parser sieht nur Text des Herausgebers), Kennung und Version gegen den Katalog, `min-kernel`. Dann jede Datei unter `files/` beim Schreiben hashen und die gespeicherte Länge prüfen (Cosmos kürzt bei vollem Laufwerk still), zuletzt Manifest und Signatur ablegen. Bei Fehler oder Abbruch wird der Versionsordner entfernt; die aktive Menge bleibt unberührt. Eine bereits aktive Version wird nie neu geschrieben, nur geprüft: Ist sie verändert, schlägt das Stufen fehl (Reparatur an Ort und Stelle ist nicht Teil von P3); nennt der Katalog für sie ein anderes Manifest, ebenfalls (eigene Meldung). Ist `active2` gerade nicht lesbar, gilt jeder vorhandene Versionsordner als womöglich aktiv und wird ebenso nur geprüft, nie gelöscht. Links stehen nur im Manifest, nicht auf der Platte.
- **Aktivieren** (`Activate`, ganze Zielmenge mit `requested`/`auto`): Jede Version muss in dieser Kernelsitzung gestuft oder schon aktiv sein. Geprüft werden Signatur vor dem Parsen und Manifest-Hash (gegen die Stufe beziehungsweise `active2`), keine Rückstufung gegenüber dem aktiven Stand, `min-kernel`, alle `depends` in der Menge erfüllt, kein `provides` doppelt, kein Pfad (Datei oder Link, ohne Groß-/Kleinschreibung) in zwei Paketen, kein Pfad Ordner eines Pfads eines anderen Pakets und Linkketten über Paketgrenzen ohne Kreis, mit höchstens 40 Links und 200 Zeichen. Alle Manifeste einer Menge zusammen höchstens 16 MiB, Dateien und Links zusammen höchstens 131 072. Die Pfad- und Linkprüfung arbeitet ohne Zeichenkette je Schritt und hat ein Budget von 16 Mi Tabellenzugriffen; eine Menge darüber (etwa Tausende Links auf den Anfang einer langen Kette in sehr tiefen Ordnern) wird abgelehnt statt minutenlang geprüft. Danach wechselt nur `active2`, ersetzt mit der Sicherungslogik von Version 1 (`.wip`, `.new`, `.old`). Alte Versionen bleiben bis zum nächsten Aufräumen liegen.
- **Lesen** (`ReadActive`): Fehlt `active2`, ist der Bestand leer. Jede Abweichung im Format, ein fehlender Versionsordner, eine falsche Signatur, ein abweichender Hash oder eine verletzte Regel über Paketgrenzen sperrt den ganzen Bestand (Fehler, nie leer). Lesen schreibt nichts; ein halb ersetztes `active2` wird so gelesen, wie das Aufräumen es abschließen würde. `min-kernel` prüft erst das Aktivieren (und später der Start). `VerifyFiles` hasht die Inhaltsdateien erneut.
- **Aufräumen** (`Reconcile`, beim Einbinden, bevor Paketprogramme laufen): halb ersetztes `active2` abschließen, dann unvollständige Stufen und nicht aktive Versionen löschen. Ist `active2` unlesbar, wird nichts gelöscht. Ordner ohne gültige Kennung oder Version bleiben unberührt.
- **Abbruchsicherheit:** Hosttests brechen nach jedem Dateischritt von Stufen, Aktivieren und Aufräumen ab (Kopie des Zustands im Moment des Abbruchs) und prüfen nach Neustart und Aufräumen: alte oder neue Menge, nie eine Mischung, nur aktive Versionen auf der Platte, Version 1 byteweise gleich, Wiederholung gelingt. Wie bei Version 1 garantiert FAT32 keine atomaren Schritte; das ist kein Stromausfallschutz.
- **Grenzen:** Fehlt `active2` samt `.old` und `.new`, ist der Bestand leer (so beauftragt), und das Aufräumen löscht alle Versionen: „nie aktiviert“ und „Datei verloren“ (FAT-Schaden, versehentlich gelöscht) sind nicht unterscheidbar; später etwa über einen Marker „v2 eingerichtet“. Ob `depends` und `provides` von Katalog und Manifest übereinstimmen, prüft nicht der Bestand (`CatalogEntry2Key` trägt nur Kennung, Version und Hash), sondern die Auflösung beim Aufrufer. `ReadActive` nimmt keine Schreibsperre und kann neben einem gleichzeitigen Aufräumen derselben Instanz fälschlich „beschädigt“ melden. Schreibende Operationen einer Instanz schließen sich aus. Es gibt noch keine Belegung laufender Versionen; deshalb entfernt nur das Aufräumen beim Einbinden alte Versionen. Die Pfadmenge im Manifestparser hasht ohne Salt (gezielte Kollisionen verlangsamen ihn); weil jede Signatur vorher geprüft wird, erreicht das nur der Herausgeber.

### Dienst und Index

`PackageService2` (N3-A) verbindet Katalog, Auflösung und Bestand; `PackageIndex2` (N3-B1) ist der Pfadindex der aktiven Menge. Shell (Administratoren für ändernde Befehle, die laufen im Hintergrund wie `pkg install`):

```text
pkg native list                                      aktive Menge (angefordert/automatisch, Update), Katalog v2
pkg native resolve <pfad>                            Pfad im Index: Paket, Version, Größe, ausführbar
pkg native update  [--from <Verzeichnis>]            catalog2 + catalog2.sig laden
pkg native install [--from <Verzeichnis>] <id>...    Zielmenge samt Abhängigkeiten, ein Wechsel von active2
pkg native upgrade [--from <Verzeichnis>]            alle neueren Katalogversionen in einem Wechsel
pkg native remove <id> | --unused                    Entfernen, Abhängigkeiten geschützt
```

Ohne `--from` gilt die HTTPS-Quelle von Version 1 (`pkg source`), `catalog2` liegt dort neben `catalog`. Mit `--from` ist die Quelle ein lokales Verzeichnis im Layout von `scripts/package2.py`. Vertraut wird in beiden Fällen nur die Signatur.

- **Index:** entsteht nur aus `ReadActive` (Signatur vor Parse, Manifest-Hash, Mengenregeln) und ist danach unveränderlich. Er bildet Datei- und Linkpfade auf die Datei unter `v2/<id>/<version>-r<release>/files/` ab; Links und Ordner sind nur Einträge des Index. Aufgelöst werden Datei- und Ordnerlinks, auch mitten im Pfad, höchstens 40 hintereinander, ohne Groß-/Kleinschreibung (ASCII, wie FAT32). Nur kanonische Pfade werden angenommen (optional führendes `/`, keine leeren, `.`- oder `..`-Komponenten); `..` wird abgewiesen statt textuell gekürzt, damit kein Pfad über einen Link aus dem Paketbaum führt. Den Inhalt prüft erst `PackageIndex2.Verify` (Größe und SHA-256 gegen das Manifest), bevor ein Start ihm vertraut.
- **Wechsel:** Nach jedem erfolgreichen `Activate` und beim Einbinden (nach `Reconcile`) baut der Dienst einen neuen Index; scheitert ein Auftrag vorher, bleibt der alte. Wer einen Stand hält, behält ihn: der Wechsel ersetzt nur einen Verweis. Ist `active2` beschädigt, gilt kein Index (Fehler, nie leer). Nach einem Neustart entsteht er aus dem gültigen `active2` neu.
- **Prozesswurzel (B2):** `PackageService2.Bind` bindet einen Start an den aktuellen Index (`PackageIndexLease`); der Lauf behält diesen Stand mit allen Kindprozessen, bis sein letzter Beteiligter endet, ein Upgrade gilt erst für den nächsten Start. Holen und Zählen liegen unter derselben Dienstsperre wie der Wechsel nach `Activate` (Sperre Start/Aktivierung; ein Blatt, darin weder Platte noch `StorageLock`). `BoundUsers` nennt die gebundenen Nutzer je Stand. `PackageRootStore` bildet daraus die Wurzel: Jeder erste Pfadteil der Indexwurzel (`usr`, `lib` …, ohne Groß-/Kleinschreibung) ist mit seinem Unterbaum schreibgeschützt (`-EROFS`, Modi 0755/0644), Links sind Symlinks mit dem Manifestziel, alles andere (`/tmp`, `/.cache`) liegt im Arbeitsspeicher. Beim ersten Öffnen einer Paketdatei je Wurzel prüft der Speicher Größe und SHA-256 auf demselben Strom, eine veränderte Datei ist `-EIO`. Testbau (`-p:ZehOSLinuxRun=true`): `run --pkg [NAME=WERT...] /usr/bin/x [argumente...]` startet ein statisches Programm daraus; `PT_INTERP` aus dem Paketbestand folgt mit N3-E.
- **Grenzen (B2):** Es gibt keine Nutzungszählung je Paketversion (N3-B3), das volle `Reconcile` beim Einbinden läuft weiter vor jedem Start. Eine Datei wird je Wurzel nur beim ersten Öffnen geprüft: Wer die Ablage am Dateidienst vorbei ändert (anderes System), bleibt danach unbemerkt. Gastbeleg nur KVM, ohne laufendes Programm während eines Upgrades (das deckt der Hosttest ab).

### Werkzeug

`scripts/package2.py` schreibt Version und Katalog des Formats 2 in einen lokalen Repository-Ordner; Schlüssel und Signatur kommen unverändert aus `scripts/build-package.py` (OpenSSL, Ed25519), dessen Befehle für Version 1 gleich bleiben. Nichts davon geht in den Branch `packages`, solange der Server Format 2 nicht ausliefert (N3-G). Gastprobe mit lokalem Testrepo: `scripts/test-package2-guest.py`.

```sh
python3 scripts/package2.py package --key <privater-Schlüssel.pem> --repo <ordner> --spec <paket>.toml <datei>=<pfad> ...
python3 scripts/package2.py catalog --key <privater-Schlüssel.pem> --repo <ordner> --generation <n>
```

Die Spec (`tomllib`) enthält die Manifestfelder ohne `exec`/`file`-Zeilen; Listen dürfen unsortiert sein, das Werkzeug schreibt die kanonische Reihenfolge. `exec` nennt nur die Pfade der ausführbaren Dateien, Hash und Größe rechnet das Werkzeug aus den Quellen `<datei>` aus:

```toml
id = "hello"
version = "1.2.3"
release = 4
runtime = "linux-x64-v1"
license = "MIT OR Apache-2.0"
name = "Hallo Welt"
entry = "usr/bin/hello"
capability = ["net"]
memory-mib = 256
min-kernel = "0.5.0"
depends = ["musl>=1.2.5-r1"]
provides = ["so:libhello.so.1"]
exec = ["usr/bin/hello"]

[link]
"usr/lib/libhello.so" = "libhello.so.1"
```

- **package** prüft alle Regeln des Manifests wie `PackageManifest2.Parse` (Pfade, Groß-/Kleinschreibung, Ordnerkonflikte, Linkziele und -ketten, Grenzen) und schreibt nur, was der Parser kanonisch annimmt. Quellen und Spec müssen reguläre Dateien sein (kein symbolischer Link, keine FIFO oder Gerätedatei; geöffnet wird nicht blockierend). Abgelehnt werden ein symbolischer Link auf dem Schreibweg im Repository, ein Schlüssel im Repository, die Schlüsseldatei selbst als Inhalt und jeder Inhalt, der irgendwo einen PEM-Kopf eines privaten Schlüssels enthält (`-----BEGIN … PRIVATE KEY-----`, auch `ENCRYPTED`, `RSA`, `EC`, `OPENSSH`, PGP `… BLOCK`; die ganze Datei wird durchsucht). Ein privater Schlüssel ohne PEM-Hülle (DER, roh) fällt dabei nicht auf. Kopiert, gehasht und durchsucht werden dieselben Bytes in einem Durchgang. Geschrieben wird in einen Ordner `.neu-…` neben der Version, signiert, die Signatur mit dem abgeleiteten öffentlichen Schlüssel gegen die geschriebenen Bytes geprüft und erst dann zu `<id>/<version>-r<release>` umbenannt; jeder Fehler, Strg+C, SIGTERM und SIGHUP entfernen den Ordner. Eine vorhandene Version wird nie überschrieben: Unter Linux benennt das Werkzeug mit `renameat2(RENAME_NOREPLACE)` um, sodass auch ein inzwischen angelegter leerer Zielordner nicht ersetzt wird; ohne diesen Aufruf (anderes System, Dateisystem ohne Unterstützung) bleibt zwischen Prüfung und `os.rename` ein kleines Fenster, in dem ein gleichzeitig angelegter leerer Ordner ersetzt würde. Bei hartem Abbruch (SIGKILL, Stromausfall) bleibt höchstens ein `.neu-…`-Ordner, den Katalog und Bestand nie als Version lesen; er ist von Hand zu löschen.
- **catalog** nimmt je Paket die höchste Version (Version, dann Revision), prüft deren Signatur vor dem Parsen, Kennung und Version gegen den Ordnernamen und jede Datei unter `files/` mit Größe und Hash (keine fehlende, zusätzliche oder verlinkte Datei). Ein symbolischer Link, dessen Name eine Paketkennung oder Version ist, bricht ab, statt still übergangen zu werden. Jede `depends`-Zeile muss im Katalog mit mindestens der verlangten Version stehen. Die Generation muss größer als die des vorhandenen `catalog2` sein. `catalog2` und `catalog2.sig` werden nach geprüfter Signatur nacheinander ersetzt; bricht der Lauf zwischen den beiden Umbenennungen ab, liegt bis zum nächsten erfolgreichen Lauf der neue Katalog neben der alten Signatur. ZehOS lehnt dieses Paar sicher ab (Signatur passt nicht), es installiert dann nichts.
- Meldungen geben Eingabetext nur gefiltert wieder (druckbares ASCII, höchstens 64 Zeichen); Schlüsselinhalt und OpenSSL-Befehlszeilen erscheinen nie.
- Testdaten: `ProjektZeh.Tests/TestData/Package2` ist ein so erzeugtes Repository mit drei Paketen und Testschlüssel (nur der öffentliche Teil ist eingecheckt). `scripts/tests/test_package2.py` erzeugt es mit einem neuen Schlüssel erneut und vergleicht alles außer den Signaturen byteweise; `PackageCatalog2Tests` liest es mit den C#-Parsern.
