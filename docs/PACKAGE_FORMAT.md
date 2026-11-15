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
file=<sha256 klein> <bytes> <pfad>
...
end
```

- `id`: `[a-z0-9][a-z0-9-]{0,47}`.
- `version`: drei Dezimalzahlen ohne führende Nullen, wie bei Kernelupdates.
- `name`: Anzeigename im Startmenü, 1–32 druckbare ASCII-Zeichen, ohne Leerzeichen am Rand.
- `file`: höchstens 128 Zeilen, ordinal nach Pfad sortiert, zusammen höchstens 128 MiB. Pfadkomponenten bestehen nur aus `A–Z a–z 0–9 . _ -`, Trenner ist `/`. Abgelehnt werden leere Komponenten, `.`, `..`, abschließende Punkte und Pfade, die sich nur in Groß-/Kleinschreibung unterscheiden (FAT32).
- `entry`: eine der gelisteten Dateien, Endung `.wasm`.

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
pkg status | pkg cancel        Ergebnis bzw. Abbruch des laufenden Auftrags dieser Sitzung
```

Vorher muss IPv4 konfiguriert sein (`net dhcp`). Ergebnisse von Hintergrundaufträgen stehen zusätzlich seriell als `PKG …`.

## Paket bauen

```bash
python scripts/build-package.py keygen <Schlüsselordner>
python scripts/build-package.py package --key <Schlüsselordner>/private.pem --repo <repo> --id doom --version 1.0.0 --name Doom --entry doom.wasm <doom.wasm>=doom.wasm <freedoom1.wad>=freedoom1.wad
python scripts/build-package.py catalog --key <Schlüsselordner>/private.pem --repo <repo> --generation 1
```

Den privaten Schlüssel nicht ins Repository legen. Veröffentlichte Versionen sind unveränderlich; eine Änderung braucht eine neue Version und eine höhere Katalog-Generation.

## Server und Prüfstand

Das öffentliche Repository liegt unter `https://packages.pjoter.de/`. Ausgeliefert wird es von `deploy/package-repo` (Docker-Compose mit nginx und einem `updater`, der den Branch `packages` alle 5 Minuten nachzieht). Neue Pakete werden mit dem privaten Herausgeberschlüssel gebaut und im Branch `packages` committet; der Server übernimmt sie von selbst.

Geprüft am 15. November 2026 in QEMU TCG mit `scripts/test-wasm-doom-desktop.py`. VMware und echte Hardware sind nicht geprüft.

| Prüfung | Ergebnis |
| --- | --- |
| Normal gebauter Kernel (Herausgeberschlüssel), `pkg install doom` von packages.pjoter.de | Katalog 32 s, Installation 120 s, Doom spielbar |
| F4/Esc, Schließen über X, zehn Starts mit Beenden über das Doom-Menü | alle Starts laufen; frei 416 MiB vorher, 414 MiB danach |
| Lokales Testrepository: Entfernen während Doom läuft | abgelehnt: „Paket doom laeuft gerade; zuerst die App schliessen.“ |
| Entfernen, `pkg list`, Neuinstallation | entfernt, als verfügbar gelistet, wieder installiert |
| Neustart mit Anmeldung | Doom bleibt installiert und startet |
| Katalog-Generation 2, `pkg upgrade` | `doom 1.1.0 aktualisiert`, startet danach |
| Server liefert veränderte WAD | „Pruefsumme falsch: freedoom1.wad“, nichts installiert |

## Bewusste Grenzen von Version 1

- Nur grafische `zehos_v1`-Apps im Desktopfenster; Konsolen-Apps und ein Shellbefehl `run` fehlen.
- Eine Paket-App zur Zeit (gemeinsamer Wasm-Worker).
- Keine Benutzerdaten-/Speicherstandsverzeichnisse; Apps lesen nur ihr Paket.
- `install-local` liest den Ordner direkt (nur Administratoren), weil von außen auf das Laufwerk kopierte Dateien keine Rechteeinträge haben.
- Kein Speicherplatztest vorab; ein voller Datenträger bricht die Installation mit Dateifehler ab und entfernt das unvollständige Versionsverzeichnis.
- `pkg` gibt es nur in Builds mit Wasm-Loader (`-p:ZehOSWasm=true`).
- Eine App, die `should_exit` nie abfragt, lässt sich nicht beenden; ihr Paket bleibt bis zum Neustart gesperrt.
- Downloads haben nur ein Zeitlimit je Datenpaket, kein Gesamtlimit. Ein sehr langsamer Server hält die Netzwerksperre, bis `pkg cancel` den Auftrag abbricht.
- Das Startmenü erkennt ein bereits offenes Fenster am Anzeigenamen; Paketnamen sollten deshalb eindeutig sein.
- Veränderte Paketdateien sperren den Start; `pkg install <id>` derselben Version repariert sie.
