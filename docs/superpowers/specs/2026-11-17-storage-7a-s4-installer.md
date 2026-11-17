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
| Disk-, ESP- und Root-GUID **hier einmal erzeugt** (`CosmosSaltSource.Fill`) und im Job gehalten; der Writer bekommt sie übergeben und ruft keine Zufallsquelle mehr auf | „Keine Hardware-Zufallsquelle (RDRAND/RDSEED/virtio-rng)“ |
| Mountpunkte frei: `SystemPaths.InstallMount` und `/mnt/install-root` nicht belegt | „Installations-Mountpunkt belegt“ |
| Summe der ESP-Dateien (Kernel ×2, EFI, Version, Limine-Konfiguration, Marker) ≤ 512 MiB − 16 MiB Reserve | „Installationsdateien passen nicht auf die EFI-Partition“ |
| Installationsdateien vorhanden | wie heute |

## 3. Stufen (`InstallJob` mit Layout `Gpt`)

`InstallStage` erhält neue Werte; der Legacy-Ablauf bleibt Wert für Wert unverändert (`InstallJob.cs:54-92`).

| Stufe | Inhalt | % |
|---|---|---|
| `Partitioning` | `GptLayoutWriter.Write` (Schreibreihenfolge 7a §3, Flush), `RescanPartitions`, `GptLayout.Verify` gültig, genau 2 Partitionen mit ESP-/Linux-Typ, Zuordnung über Startsektor | 0 |
| `FormattingEsp` | FAT32 Label `ZEHOSBOOT`, Einhängen am Installationsmountpunkt (`SystemPaths.InstallMount` des laufenden Profils) | 5 |
| `FormattingRoot` | `CosmosExt2Formatter.FormatForKernel` auf Partition 2 (Formatter ist fester Teil des Builds, kein Laufzeit-Vorabtest); danach `log_block_size` = 2 und `sparse_super` prüfen wie die Etappe-1-Probe, sonst Abbruch | 10 |
| `VerifyingRoot` | Superblock lesen, `InstalledIdentity.CheckRoot` = `Ok` | 25 |
| `PreparingRoot` | Root schreibbar unter `/mnt/install-root` einhängen, `s_state` = 0 + Flush; Verzeichnisse `/boot`, `/boot/efi`, `/mnt`, `/proc`, `/sys`, `/dev`, `/etc`, `/etc/zehos` (**vollständige Liste der vom Installer auf Root angelegten Einträge**, zusammen mit dem Root-Marker; E4.4 §4 „vom Installer angelegt“ verweist genau hierauf); Root-Marker `/etc/zehos/.zehos-layout` `state=fresh`, Disk-GUID (Temp + Rename) über eine **eigene `FileService`-Instanz ohne Rechteverwaltung** mit `CosmosPathGuard.For("/mnt/install-root")` (wie die Kontendatei in `AccountService`; der Nutzer-`FileService` sperrt `.zehos-`-Namen); Sync, Sync, `s_state` = 1, Flush; aushängen, danach ein eigenes `Device.Flush()` (Aushängen schreibt über `UpdateSuperblock` zuletzt, nach dem Treiber-Flush; das Rücklesen belegt wegen des Gerätecaches nur Sichtbarkeit); **danach Superblock neu lesen**: `s_state` = 1 und `CheckRoot` = `Ok`, sonst Abbruch (der Treiber könnte beim Aushängen einen gepufferten Superblock zurückschreiben) | 27 |
| `CopyingFiles` | wie heute auf die ESP (`/EFI/BOOT/BOOTX64.EFI`, `/boot/ProjektZeh.elf`, `.bak`, `/boot/zehos-version.txt`) | 30–90 |
| `WritingBootConfig` | `/boot/limine/limine.conf` wie heute (`boot():` zeigt auf die ESP), danach **Flush/Sync der ESP** (Dauerhaftigkeitsschranke: der Marker darf nie vor den Bootdateien auf der Platte stehen, Cosmos-FAT puffert) | 92 |
| `WritingMarker` | ESP-Marker `boot/.zehos-install.id` (`InstallMarker.Serialize`) per Temp + Rename, erneut Flush, zurücklesen und `Parse` = gleich | 96 |
| `Completed` | ESP aushängen, Flush; Fehler hier ist ein **Installerfehler** (Exit ≠ 0, §4) | 100 |

Prozentwerte monoton steigend; `InstallJobTests`, `InstallServiceTests`, `SetupWizardTests` für Legacy bleiben unverändert grün.

## 4. Abbruch

`InstallJob.Step` ruft wie heute `target.Abort()` (bestehendes Muster). `Abort` hängt aus, was eingehängt ist (Root zuerst, dann ESP), und löscht **nichts**; scheitern beide Aushängevorgänge, nennt die Meldung beide Fehler, der Befehl endet mit Fehler. Die Meldung nennt den Stand, z. B. „GPT geschrieben, Root formatiert, Bootdateien unvollständig. Die Platte gilt als unklar; neu installieren mit `setup install <L> --layout gpt --erase`.“ Folgen je Stufe:

| Abbruch in | Platte danach | Identität beim nächsten Boot |
|---|---|---|
| `Partitioning` vor Schritt 4 des Writers | alter Primärheader genullt, evtl. Backupheader neu, keine ESP | `Unclear` nur, wenn `InstalledIdentity` „Primär ungültig, Backup gültig“ und „GPT-Rest ohne Marker“ so einstuft – **im Identity-Test ausdrücklich festlegen**; sonst ist die Platte leer/unbekannt und braucht ohnehin `--erase` |
| `Partitioning` danach bis `WritingBootConfig` | GPT gültig, Root-Partition vorhanden, kein ESP-Marker | `Unclear` |
| `WritingMarker` | Temp-Datei, kein gültiger Marker | `Unclear` |
| `Completed` (Aushängen scheitert) | Marker und Bootdateien geflusht (Schranken §3) | `Healthy`, aber der Installer meldet **Fehler** (Exit ≠ 0) „Aushängen fehlgeschlagen“ |
| Neuinstallation über ein `Healthy`-System, Abbruch vor `FormattingEsp` | alter ESP-Marker kann im ESP-Bereich stehen bleiben | `Unclear`, weil der alte Marker die alte Disk-GUID trägt und die neue GPT neue GUIDs hat; Test und G9 belegen das |

## 5. Tests

**Hosttests (zuerst):**
1. `Begin` GPT: 32 GiB − 512 B, 32 GiB, 128 GiB, 128 GiB + 512 B; Sektor 4096; ohne `--erase`; Zufallsquelle wirft; Medium; ausgeschlossene Platte; Installationsdateien fehlen; Mountpunkt belegt; Dateien zu groß für die ESP ⇒ jeweils Fehler und **0 Schreibzugriffe** (Zähl-Fake). Der Writer erhält die GUIDs aus `Begin` (Fake-Zufallsquelle wird nach `Begin` nie mehr aufgerufen).
2. Legacy unverändert: bestehende Tests ohne Änderung grün; `setup install <L>` ohne `--layout` erzeugt den Legacy-Job.
3. `--layout gpt` ohne Freischaltung ⇒ Verwendungsfehler; unbekanntes Layout ⇒ Verwendungsfehler. Die Freischaltung ist im getesteten Code ein injizierter `bool` (Konstruktorparameter von `SetupShellCommands`/`SetupWizard`/`InstallService`); nur `Kernel.cs` bildet `-p:ZehOSGptInstaller` darauf ab, kein `#if` im getesteten Code – Tests 3 und 8 laufen mit beiden Zuständen.
4. Stufenfolge mit Zähl-Fake: Writer genau einmal, ESP-Format vor Root-Format, Root-Marker vor Bootdateien, ESP-Marker als **letzter** Schreibzugriff mit **Flush davor und danach**; nach dem Aushängen des Root ein zweites Superblock-Lesen.
5. Abbruch in jeder Stufe: `Abort` aufgerufen, ESP-Marker nie geschrieben (außer Stufe `Completed`), Meldung nennt den Stand; Prozent monoton.
6. `VerifyingRoot`: `CheckRoot` ≠ `Ok` ⇒ Abbruch vor `PreparingRoot`; zweites Lesen nach dem Aushängen mit `s_state` ≠ 1 ⇒ Abbruch vor `CopyingFiles`.
6a. Neuinstallation über `Healthy` mit Abbruch nach `Partitioning`: `InstalledIdentity` ergibt nicht `Healthy`.
7. `WritingMarker`: Rücklesen ungleich ⇒ Abbruch.
8. `setup list`: Spalte `gpt` mit Gründen; SetupWizard zeigt die GPT-Zeile nur mit Freischaltung, ausgegraut mit Grund.

**Gasttests (QEMU+OVMF, danach VMware; Ansage, ein Gast; Testbuild mit `-p:ZehOSGptInstaller=true`), Zuordnung zu 7a §7:**
- G1 32 GiB und 128 GiB installieren, von der GPT-Platte über OVMF booten, `disk list` zeigt 2 Partitionen, Identität `Healthy`, Ersteinrichtung wie heute; `sgdisk -v`, `fsck.fat -n`, `e2fsck -fn` offline Exit 0, `dumpe2fs -h` „clean“, Root-Marker `state=fresh`.
- G2 Rückfalleintrag `.bak` startet.
- G3/G4 Root offline gelöscht bzw. beschädigt ⇒ Boot normal, Einrichtung und `security recover` gesperrt.
- G5 Recovery-ISO bei G3/G4.
- G6 Installer verweigert Medium, `ZEHOSDATA`-Platte, eingebundenes Ziel, 31 GiB, 129 GiB, fehlendes `--erase`.
- G7 installierte Platte + `ZEHOSDATA`-Platte: Bestand gewinnt.
- G8 Abbruch: Gast während `CopyingFiles` hart beenden ⇒ nächster Boot von der Platte nicht möglich bzw. Identität `Unclear`, Neuinstallation mit `--erase` gelingt. Abbrüche in `Partitioning` (zwischen den Writer-Schritten) und `PreparingRoot` sind im Gast nicht zeitgenau auslösbar; sie deckt der Zähl-Fake der Hosttests 4/5 ab.
- G9 Neuinstallation über ein `Healthy`-System, Abbruch in `CopyingFiles` ⇒ nicht `Healthy`, keine Einrichtung.
- TCG ohne RDRAND ⇒ Installer verweigert mit Zufallsquellen-Fehler (Gegenprobe, kein Fehlschlag).

## 6. Offene Punkte

1. *Entschieden 12:03:* `--erase` beim GPT-Weg immer Pflicht; Freischaltung nur mit `-p:ZehOSGptInstaller=true` bis 4.0-Go und G1–G7 grün, normaler Build zeigt die Option nicht.
2. Mountpunkt für das Root während der Installation: `/mnt/install-root` im `Legacy`-Profil ist ein Mount ohne Verzeichnis (zulässig, `VfsManager.cs:146`); im `Root`-Profil existiert `/mnt` – beides im Gast prüfen.
3. Ob `RescanPartitions` nach dem eigenen GPT-Writer die Partitionen mit den richtigen Startsektoren liefert (Cosmos liest GPT ohne CRC-Prüfung, 7a §2), belegt erst G1; der Host-Probelauf S2 (`scripts/test-installer-layout.py`, `sgdisk -v`) ist Voraussetzung.
4. **Abhängigkeit:** Sync/`s_state` im Installer nutzt denselben Code wie die Energieaktion aus Etappe 4 §2. S4 darf diesen Pfad nicht vor 4.3 duplizieren: entweder 4.3 zuerst oder S4 liefert den gemeinsamen Baustein, den 4.3 übernimmt (Zuteilung Coordinator/Leitung).
