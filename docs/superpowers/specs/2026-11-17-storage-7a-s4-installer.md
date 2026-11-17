# Etappe 7a, Schritt S4: GPT-Installer – Umsetzungsentwurf

Stand: 17. November 2026, Basis `origin/master` @ `0a5547e`. Konkretisiert [7a §4 und §7](2026-11-17-storage-7a-installer-layout.md) für den Installer. Baut auf S2 (`GptLayoutWriter`, `GptLayout.Verify`, `Crc32`) und S3 (`InstallMarker`, `InstalledIdentity`) aus `task/storage-7a-identity` sowie auf `task/storage-7a-installer-exclusion` (Ausschluss von Marker-/Root-/GPT-`ZEHOSDATA`-Platten) auf. **Nur Entwurf**, kein Code, nichts im Gast.

## 0. Entscheidungen (§0.2/§0.3 von der Leitung am 12:03 bestätigt)

1. **Ausdrückliche Wahl, kein Fallback.** `setup install <Laufwerk> [--erase]` bleibt unverändert der Legacy-Installer (eine FAT-Partition, MBR). Der neue Weg heißt `setup install <Laufwerk> --layout gpt --erase`. Schlägt eine GPT-Vorbedingung fehl, endet der Befehl mit Fehler; es wird nie still auf Legacy gewechselt (7a Entscheidung 7).
2. **`--erase` ist beim GPT-Weg immer Pflicht.** Eine 32–128-GiB-Platte lässt sich nicht schnell als leer belegen (`DiskEmptyCheck` prüft über 4 GiB nur Stichproben), und der Writer löscht Anfang und Ende ohnehin. Das hält die Regel einfach und datensicher.
3. **Freischaltung:** Der GPT-Weg existiert zunächst nur in Builds mit `-p:ZehOSGptInstaller=true`. Normale Builds bieten ihn erst nach Go von Paket 4.0 und grünen Gasttests G1–G7 an (7a §8 Punkt 3: Root-Format hinter einem Schalter bis zu den Zieltests).
4. **Marker zuletzt.** Reihenfolge: Root-Marker `state=fresh` auf ext2, dann Bootdateien, dann als **letzter** Schreibzugriff der ESP-Marker per Temp + Rename. Jeder Abbruch davor lässt eine Platte mit Root-Partition und ohne ESP-Marker zurück ⇒ `Unclear` (7a §5), nie `Legacy`, nie Einrichtung.

## 1. Bedienung

- **Shell:** `setup list` zeigt je Platte zusätzlich `gpt: ja` oder `gpt: nein (<Grund>)` (Größe außerhalb 32–128 GiB, Sektor ≠ 512, Installationsmedium, ausgeschlossene Datenplatte). `setup install <L> --layout gpt` ohne `--erase` ⇒ Fehler mit Hinweis. Unbekannte `--layout`-Werte ⇒ Verwendungsfehler.
- **SetupWizard:** Auswahl „Standard (eine EFI-Partition, FAT32)“ / „GPT mit ext2-Root (32–128 GiB)“ vor der Bestätigung; die GPT-Zeile ist bei ungeeigneten Platten ausgegraut mit Grund. Der Teilungstext (`SetupWizard.cs:525`, heute „1 EFI-Systempartition (FAT32)“) nennt für GPT „EFI-Systempartition 512 MiB (FAT32) + Root (ext2, Rest)“. Die Bestätigung ist beim GPT-Weg immer die Löschbestätigung.
- Ohne Freischaltung (§0.3) erscheinen weder Option noch Spalte.

## 2. Vorbedingungen in `InstallService.Begin` (alle vor dem ersten Schreibzugriff)

| Prüfung | Fehlertext (Kern) |
|---|---|
| Platte gefunden, kein Installationsmedium, nicht eingebunden, nicht ausgeschlossen (exclusion-Branch) | wie heute bzw. „Datenplatte/installiertes System, nicht als Ziel erlaubt“ |
| Sektorgröße 512 | „GPT-Layout nur mit 512-Byte-Sektoren“ |
| 32 GiB ≤ Größe, `BlockCount ≤ 268435456` (128 GiB) | „GPT-Layout nur für 32 bis 128 GiB“ |
| `--erase` gesetzt | „GPT-Installation löscht die Platte; --erase erforderlich“ |
| Hardware-Zufall verfügbar (Probe-Aufruf von `CosmosSaltSource.Fill` auf einen Wegwerfpuffer) | „Keine Hardware-Zufallsquelle (RDRAND/RDSEED/virtio-rng)“ |
| Installationsdateien vorhanden | wie heute |
| ext2-Formatter verfügbar und Plug aktiv | „ext2-Formatter nicht verfügbar“ |

## 3. Stufen (`InstallJob` mit Layout `Gpt`)

`InstallStage` erhält neue Werte; der Legacy-Ablauf bleibt Wert für Wert unverändert (`InstallJob.cs:54-92`).

| Stufe | Inhalt | % |
|---|---|---|
| `Partitioning` | `GptLayoutWriter.Write` (Schreibreihenfolge 7a §3, Flush), `RescanPartitions`, `GptLayout.Verify` gültig, genau 2 Partitionen mit ESP-/Linux-Typ, Zuordnung über Startsektor | 0 |
| `FormattingEsp` | FAT32 Label `ZEHOSBOOT`, Einhängen am Installationsmountpunkt (`SystemPaths.InstallMount` des laufenden Profils) | 5 |
| `FormattingRoot` | `CosmosExt2Formatter.FormatForKernel` auf Partition 2 | 10 |
| `VerifyingRoot` | Superblock lesen, `InstalledIdentity.CheckRoot` = `Ok` | 25 |
| `PreparingRoot` | Root schreibbar unter `/mnt/install-root` einhängen, `s_state` = 0 + Flush; Verzeichnisse `/boot/efi`, `/mnt`, `/proc`, `/sys`, `/dev`, `/etc/zehos`; Root-Marker `/etc/zehos/.zehos-layout` `state=fresh`, Disk-GUID (Temp + Rename); Sync, Sync, `s_state` = 1, Flush; aushängen | 27 |
| `CopyingFiles` | wie heute auf die ESP (`/EFI/BOOT/BOOTX64.EFI`, `/boot/ProjektZeh.elf`, `.bak`, `/boot/zehos-version.txt`) | 30–90 |
| `WritingBootConfig` | `/boot/limine/limine.conf` wie heute (`boot():` zeigt auf die ESP) | 92 |
| `WritingMarker` | ESP-Marker `boot/.zehos-install.id` (`InstallMarker.Serialize`) per Temp + Rename, zurücklesen und `Parse` = gleich | 96 |
| `Completed` | ESP aushängen, Flush | 100 |

Prozentwerte monoton steigend; `InstallJobTests`, `InstallServiceTests`, `SetupWizardTests` für Legacy bleiben unverändert grün.

## 4. Abbruch

`InstallJob.Step` ruft wie heute `target.Abort()` (bestehendes Muster). `Abort` hängt aus, was eingehängt ist (Root zuerst, dann ESP), und löscht **nichts**. Die Meldung nennt den Stand, z. B. „GPT geschrieben, Root formatiert, Bootdateien unvollständig. Die Platte gilt als unklar; neu installieren mit `setup install <L> --layout gpt --erase`.“ Folgen je Stufe:

| Abbruch in | Platte danach | Identität beim nächsten Boot |
|---|---|---|
| `Partitioning` vor Schritt 4 des Writers | alter Primärheader genullt, evtl. Backup neu | `Unclear` (7a §3) |
| `Partitioning` danach bis `WritingBootConfig` | GPT gültig, Root-Partition vorhanden, kein ESP-Marker | `Unclear` |
| `WritingMarker` | Temp-Datei, kein gültiger Marker | `Unclear` |
| `Completed` (Aushängen scheitert) | Marker gültig | `Healthy`; Meldung „Aushängen fehlgeschlagen, Puffer evtl. nicht geschrieben“ |

## 5. Tests

**Hosttests (zuerst):**
1. `Begin` GPT: 32 GiB − 512 B, 32 GiB, 128 GiB, 128 GiB + 512 B; Sektor 4096; ohne `--erase`; Zufallsquelle wirft; Medium; ausgeschlossene Platte ⇒ jeweils Fehler und **0 Schreibzugriffe** (Zähl-Fake).
2. Legacy unverändert: bestehende Tests ohne Änderung grün; `setup install <L>` ohne `--layout` erzeugt den Legacy-Job.
3. `--layout gpt` ohne Freischaltung ⇒ Verwendungsfehler; unbekanntes Layout ⇒ Verwendungsfehler.
4. Stufenfolge mit Zähl-Fake: Writer genau einmal, ESP-Format vor Root-Format, Root-Marker vor Bootdateien, ESP-Marker als **letzter** Schreibzugriff.
5. Abbruch in jeder Stufe: `Abort` aufgerufen, ESP-Marker nie geschrieben (außer Stufe `Completed`), Meldung nennt den Stand; Prozent monoton.
6. `VerifyingRoot`: `CheckRoot` ≠ `Ok` ⇒ Abbruch vor `PreparingRoot`.
7. `WritingMarker`: Rücklesen ungleich ⇒ Abbruch.
8. `setup list`: Spalte `gpt` mit Gründen; SetupWizard zeigt die GPT-Zeile nur mit Freischaltung, ausgegraut mit Grund.

**Gasttests (QEMU+OVMF, danach VMware; Ansage, ein Gast; Testbuild mit `-p:ZehOSGptInstaller=true`), Zuordnung zu 7a §7:**
- G1 32 GiB und 128 GiB installieren, von der GPT-Platte über OVMF booten, `disk list` zeigt 2 Partitionen, Identität `Healthy`, Ersteinrichtung wie heute; `sgdisk -v`, `fsck.fat -n`, `e2fsck -fn` offline Exit 0, Root-Marker `state=fresh`.
- G2 Rückfalleintrag `.bak` startet.
- G3/G4 Root offline gelöscht bzw. beschädigt ⇒ Boot normal, Einrichtung und `security recover` gesperrt.
- G5 Recovery-ISO bei G3/G4.
- G6 Installer verweigert Medium, `ZEHOSDATA`-Platte, eingebundenes Ziel, 31 GiB, 129 GiB, fehlendes `--erase`.
- G7 installierte Platte + `ZEHOSDATA`-Platte: Bestand gewinnt.
- G8 Abbruch: Gast während `CopyingFiles` hart beenden ⇒ nächster Boot von der Platte nicht möglich bzw. Identität `Unclear`, Neuinstallation mit `--erase` gelingt.
- TCG ohne RDRAND ⇒ Installer verweigert mit Zufallsquellen-Fehler (Gegenprobe, kein Fehlschlag).

## 6. Offene Punkte

1. *Entschieden 12:03:* `--erase` beim GPT-Weg immer Pflicht; Freischaltung nur mit `-p:ZehOSGptInstaller=true` bis 4.0-Go und G1–G7 grün, normaler Build zeigt die Option nicht.
2. Mountpunkt für das Root während der Installation: `/mnt/install-root` im `Legacy`-Profil ist ein Mount ohne Verzeichnis (zulässig, `VfsManager.cs:146`); im `Root`-Profil existiert `/mnt` – beides im Gast prüfen.
3. Ob `RescanPartitions` nach dem eigenen GPT-Writer die Partitionen mit den richtigen Startsektoren liefert (Cosmos liest GPT ohne CRC-Prüfung, 7a §2), belegt erst G1; der Host-Probelauf S2 (`scripts/test-installer-layout.py`, `sgdisk -v`) ist Voraussetzung.
4. Sync/`s_state` im Installer nutzt denselben Code wie die Energieaktion aus Etappe 4 §2 – gemeinsam mit 4.3 umsetzen, nicht doppelt.
