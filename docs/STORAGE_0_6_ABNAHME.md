# Storage-Abnahme für ZehOS 0.6

Stand: 18. November 2026. Grundlage für Peters Abnahme (QEMU/WHPX auf seinem Rechner; die zutreffenden Fälle unten sind die Kriterien) und das Release v0.6.0. Der Bericht sammelt die Gastergebnisse der
Storage-Etappen 7a-S4 (GPT-Installer), 4 (Root-Profil und Migration), 7b (Updates und Energieaktionen) und 8 (Matrix).
Er nennt je Fall Ergebnis, Prüfstand und Quelle. **PASS** heißt: im genannten Gast so geprüft. **OFFEN** heißt: nicht oder
nicht vollständig geprüft. **FAIL** heißt: geprüft und nicht bestanden.

Alle Gastläufe liefen unter QEMU/KVM mit OVMF (edk2 202608) auf Linux-Rechnern. **VMware ist für 0.6 ausdrücklich nicht
geprüft und liegt außerhalb der Abnahme** (Entscheidung Peter, 18. November 2026). Echte Hardware ist nicht geprüft.

## Installer (7a-S4, Spec [§5](superpowers/specs/2026-11-17-storage-7a-s4-installer.md))

| Fall | Inhalt | Ergebnis | Stand und Quelle |
| --- | --- | --- | --- |
| G1 | 32 und 128 GiB installieren, offline prüfen, `Healthy` von der Platte starten | PASS | master 6d5ddc3 (#268) und #273 875e94c, RED |
| G1 Konto | Ersteinrichtung auf der installierten Platte legt das Konto an | PASS | 56c8c88, RED |
| G2 | Rückfalleintrag `.bak` startet | PASS | 56c8c88, RED |
| G3/G4 | Root offline gelöscht/beschädigt: Boot normal, Einrichtung und `security recover` gesperrt | PASS | 56c8c88, RED |
| G5 | Security-Recovery-ISO bei G3/G4 | PASS | 56c8c88, RED |
| G6 | Installer verweigert Medium und `ZEHOSDATA`-Platte | PASS | 982f302, RED |
| G6 | Installer verweigert 31 und 129 GiB und fehlendes `--erase`, keine Schreibzugriffe | PASS | 875e94c-Stand, red-matrix |
| G6 | eingebundenes Ziel, belegter Installationsmountpunkt | OFFEN | im Installer-Gast nicht herstellbar (kein Konto, `disk mount` verweigert) |
| G7 | installierte Platte und `ZEHOSDATA`: installierte Platte gewinnt | PASS | 56c8c88, RED |
| G8 | Abbruch in `CopyingFiles` auf frischer Platte ⇒ `Unclear`, Neuinstallation mit `--erase` | PASS | master 6d5ddc3 und #273, RED |
| G8 Probe | Halt bei 96 % nach dem Bootdatei-Flush: Bootdateien vollständig, kein ESP-Marker, `fsck.fat` sauber | PASS | 56c8c88 mit G8-Probebau, RED |
| G9 | Neuinstallation über `Healthy`, Abbruch in `CopyingFiles` ⇒ nicht `Healthy` | PASS | 56c8c88, RED |
| RDRAND | TCG ohne RDRAND/RDSEED: Installer verweigert | PASS | 56c8c88, RED |

## Root-Profil und Migration (Etappe 4, Spec [Etappe 4 §6](superpowers/specs/2026-11-17-storage-etappe4-design.md))

Harness `scripts/test-storage-migrate-guest.py` (Branch `task/storage-migrate-guest`), Quelle jeweils eine 64-MiB-Platte mit
Testkonto und Testdateien (lange Namen), Ziel per GPT-Installer.

| Fall | Inhalt | Ergebnis | Stand und Quelle |
| --- | --- | --- | --- |
| G4 | 64 MiB → 32 GiB: Quelle unverändert, `e2fsck -fn` sauber, Marker `ready`, Dateien gleich, Ziel startet im Profil `Root` mit altem Kennwort, Altplatte startet weiter | PASS | 0.5.106-Stand, blue |
| G5 | harter Abbruch in der Kopierphase ⇒ Marker `migrating`, Profil `Blocked`, offline `e2fsck -fy`, Wiederholung migriert (305 Dateien), danach `e2fsck -fn` sauber, Dateien gleich, Quelle unverändert | PASS | Kernelcode = master 6ec4e93 (#273, #284), blue |
| G5 | volles Ziel (kleine Root per Testlayout) | OFFEN | kein Testlayout mit kleiner Root vorhanden |
| G6 | 64 GiB und 128 GiB: Migration, Start im Profil `Root`, `e2fsck -fn` sauber | PASS | 875e94c, blue |
| G4 | ACL-Vergleich, NetSurf-Start nach der Migration | OFFEN | nicht im Harness |

**G5-Befunde (Produkt), beide behoben:**

1. Nach `e2fsck -fy` verweigerte die Wiederholung wegen `/lost+found` (`e2fsck` legt es an, der Formatter nicht). Behoben mit
   #273.
2. Bricht der Gast zwischen Journalzeile und Aktion ab, scheitert die Wiederholung mit `cause='Missing'`
   (`Pfadkomponente fehlt`): `DeleteTolerant` duldet nur `ResultCode.NotFound`, `PathGuard` meldet fehlende Komponenten als
   `Failure`. Behoben mit #284; G5 danach im Gast bestanden.

Direkt nach einem harten Abbruch verlangt die Abnahme kein sauberes `e2fsck` (ext2 ohne Journal); in den Läufen traten
Belegungsdifferenzen einzelner Inodes auf, einmal war das Ergebnis sauber (Entscheidung lead2).

## Updates und Energieaktionen (7b, Spec [Gastprüfung](superpowers/specs/2026-11-17-storage-7b-updates-power.md))

| Fall | Inhalt | Ergebnis | Stand und Quelle |
| --- | --- | --- | --- |
| P1 | `Legacy` mit Datenplatte: `shutdown` hängt `/data` aus, `fsck.fat -n` 0 | PASS | 0.5.125 (#270), senior |
| P2 | `Root`: `shutdown` ⇒ „clean“, `e2fsck -fn` 0, ESP `fsck.fat -n` 0 | PASS | 0.5.125 (#270), senior |
| P3a/P3b | `Root`: `reboot` setzt `s_state` 1; Neustart im Profil `Root`, danach sauber | PASS | 0.5.125 (#270), senior |
| P4a–f | Fehlerprobe je Stufe (ESP-Sync, ESP-Unmount, Root-Sync, Root-Flush, Root-Flush mit Reopen, Root-Sync mit Remount): Gast läuft weiter, Shell bedienbar, zweites `shutdown` sauber, `e2fsck -fn` 0, `fsck.fat` nur tolerierte Meldungen, `s_state` 1; bei P4e greift die Schreibsperre (`RootWriteLocked`) | PASS | Probebau bb28345 (`PowerFaultProbe`), Skript `task/storage-7b-p4-guest` 6d05342, senior |
| P5 | offline `update install` im Profil `Root` (je Lauf frischer Update-Testschlüssel): neuer Kernel startet nach `reboot`, `.bak` = alter Kernel (SHA-256), Rückfalleintrag startet den alten Kernel, Root danach sauber | PASS | Kernel A/B mit `ZehOSUpdateTestKeyPath`, Skript #310, senior |
| P6 | laufende Paket-App: `shutdown` verweigert (`PowerBusy`), nach dem Schließen sauber | PASS | Paket mit Testschlüssel, Skript #310, senior |
| Release | `build-release.sh` (#309, Root-Profil + Migration, Installer mit GPT): Release-ISO startet eine 64-MiB-Legacy-Platte mit `/data`; Installer-ISO zeigt die GPT-Zeile, installiert 32 GiB, Start im Profil `Root` | PASS | 9cd442a, senior (Kommentar in #309) |

## Gesamtmatrix (Etappe 8)

| Fall | Inhalt | Ergebnis | Stand und Quelle |
| --- | --- | --- | --- |
| Matrix 32 GiB | Installation, Boot im Profil `Root`, Ersteinrichtung, zwei Terminals mit eigenem Arbeitsverzeichnis, Dateimanager, Neustart (`s_state` 1), offline `e2fsck` und `fsck.fat` der ESP | PASS | f22e311 (0.5.129), red-matrix |
| Matrix 32 GiB | virtuelle Dateien per `cat` und Bild: `/proc/version`, `/proc/meminfo`, `/proc/mounts`, `/sys/kernel/components`, `/dev/null` | PASS | f22e311, red-matrix |
| Matrix 32 GiB | Inode-Grenzfall (~17.500 Dateien je Mount): saubere Meldung, kein Absturz, `e2fsck` sauber | OFFEN (akzeptiert) | Füllung mit 18.000 Dateien belegt (`e2fsck` sauber, Gast ohne Absturz). Die Cache-Grenze wird über die Shell nicht erreicht: `du`/`find` sind auf 1024 Einträge begrenzt. Peter: dokumentierte offene Grenze. red-matrix |
| Matrix 64/128 GiB | wie 32 GiB (ohne Inode-Grenze), virtuelle Dateien per Bild | PASS | f22e311 (0.5.130), senior; erster 128-GiB-Lauf FEHLER unter Hostlast, Wiederholung allein PASS (siehe Grenzen) |
| Legacy | Regression mit 64-MiB-Datenplatte (P1) | PASS | 875e94c-Stand, red-matrix |
| `pkg install`, `update install` | in der Matrix | OFFEN | Runde 2 (Testschlüsselbau, lokaler Server) |
| voller Datenträger | Schreiben bis `ENOSPC` auf 32+ GiB | OFFEN | Dauer über Cosmos-ext2 (Stunden); Ersatz: Inode-Grenzfall (Entscheidung Peter) |

## Bekannte Grenzen

- Unter starker Hostlast (paralleler Build auf denselben CPUs) brach eine 128-GiB-Installation in `CopyingFiles` mit `SATA: command completion timeout` ab (ein Schreibblock brauchte über 5 s); ohne Parallellast bestanden. Ein eigener Timeout-Test fehlt.
- `cp` aus `/proc`, `/sys` und `/dev` wird mit `cause='ForeignMount'` verweigert (virtuelle Dateien haben keine verlässliche Größe); lesen geht per `cat`. Als Grenze bestätigt (lead2, 18.11.).
- Höchstens etwa 17.500 verschiedene Dateien je Mount (Cosmos-Inode-Cache, #228/#233) und 8189 Einträge je Verzeichnis.
- FAT: kurze kleingeschriebene 8.3-Namen fremder Systeme erscheinen groß (Cosmos wertet das NT-Kleinschreib-Flag nicht aus,
  #263); die Migration übernimmt diese Schreibweise.
- Keine Stromausfallsicherheit; nach einem harten Abbruch ist offline `e2fsck -fy` nötig (`RootUnclean`).
- VMware und echte Hardware nicht geprüft.

Diese Grenzen beschreibt lead2 ausführlich in der Grenzen-Doku; dieser Bericht verweist nur darauf.
