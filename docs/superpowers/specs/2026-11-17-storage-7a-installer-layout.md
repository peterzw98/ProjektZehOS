# Etappe 7a: Installer-Layout ESP + ext2, Boot und Recovery (Inventar, Entwurf, Testspezifikation)

Stand: 17. November 2026, Basis `origin/feature/storage-data-disk` @ `22dd279` (ZehOS 0.5.12). **Nur lesen und entwerfen:** keine Formatier-, Boot- oder Installeränderung, nichts davon im Gast ausgeführt. Grundlage: [Plan, Etappe 7](../plans/2026-11-17-storage-linux.md#etappe-7-installer-updates-und-energieaktionen), [Aufrufer-Inventar](2026-11-17-storage-callers.md), [Etappe-2-Entscheidungen](2026-11-17-storage-etappe2-design.md), Cosmos-Quelle `efcf20c` (= 3.0.97). Aussagen zu Cosmos stammen aus dem Quellbaum, nicht aus dem Gast.

## 1. Inventar: heutiger Installer-, Boot-, Update- und Recovery-Pfad

| Bereich | Fundstelle | Annahme heute | Betroffen vom neuen Layout |
|---|---|---|---|
| Zielschutz | `Installer/InstallService.cs:31-49` (`Begin`), `:18` `MinimumDiskSize` 128 MiB | Medium/zu kleine Platte/Bestand ohne `--erase` verweigert | Mindestgröße, Meldungstext |
| Kandidaten | `Installer/CosmosInstallService.cs:33-50` (`GetCandidates`), `:127` `IsInstallMedium` (CD001), `:102-125` `IsBlank` (nur ≤256 MiB gelesen) | jede Platte außer Medium und `ZEHOSDATA`-Platte | Leerprüfung ab 256 MiB ist immer „nicht leer"; `DiskEmptyCheck` (Etappe 2) wäre die passende Wiederverwendung |
| OpenTarget | `CosmosInstallService.cs:52-80` | 512-Byte-Sektoren, 128 MiB bis 2 TiB (`BlockCount - 2048 <= uint.MaxValue`), nichts eingebunden, `/data` frei | 2-TiB-Grenze folgt aus MBR; mit GPT entfällt sie |
| Partitionieren | `CosmosInstallService.cs:144-160` (`Partition`) | alte GPT-Reste nullen (Sektor 0..2047, letzte 33), `Mbr.Create`, **eine** MBR-Partition `0xEF` ab LBA 2048 bis Plattenende | ersetzt durch GPT mit zwei Partitionen |
| Formatieren | `:162-176` (`Format`) | `Partitions.Count == 1`, `TryFormat` FAT32 Label `ZEHOS`, Mount **auf `/data`** | zweites Format (ext2); ESP bekommt anderes Label/Mountziel |
| Dateien | `InstallJob.cs:21` Pfade `/EFI/BOOT/BOOTX64.EFI`, `/boot/ProjektZeh.elf`, `.bak`, `/boot/zehos-version.txt`; `:26-30` Limine-Config (`boot():/boot/ProjektZeh.elf`, Einträge 1920×1080, 1024×768, `.bak`); `:78-80` schreibt `/boot/limine/limine.conf` | alles auf der einen FAT-Partition | `boot():` zeigt auf die ESP, die Pfade bleiben, solange der Kernel auf der ESP liegt (Entwurf) |
| Stufen | `InstallJob.cs:54-92`, `IInstallService.cs:21-27` (`Partitioning/Formatting/CopyingFiles/WritingBootConfig/Completed`), `Percent` `:39-47` | feste Prozentsprünge | zusätzliche Stufe „Root formatieren/prüfen" verschiebt die Prozentwerte; Tests `InstallJobTests`, `InstallServiceTests`, `SetupWizardTests` |
| UI/Shell | `Kernel.cs:41,69` (Konstruktion), `:137` `SetupShellCommands`, `:193`/`:272-277` Installer-Text und `InstallerSession`; `Installer/SetupWizard.cs:522,775` Größenanzeige, Teilungstext `:525` „1 EFI-Systempartition (FAT32)" | Text nennt genau eine Partition | Text und `InstallDisk.PartitionCount`/`ContentText` |
| Update | `Update/FileUpdateTarget.cs:8-14` (`/data` + `boot/ProjektZeh.elf`, `boot/zehos-version.txt`), `UpdateController.cs:10,16,67-69` (Offline-Kernel muss unter `/data` liegen), `UpdateService.cs:43-70` (`.bak`-Rotation) | die ESP **ist** `/data` | bleibt gültig, solange `/data` die ESP bleibt (bis Etappe 4); 7b richtet das Ziel getrennt aus |
| Datenkonten | `Kernel.cs:45,47` `AccountService("/data")`, `PackageStore("/data/.zehos-packages")` | Konten und Pakete liegen auf der ESP/`/data` | Root-Prüfung muss die Kontoeinrichtung sperren (Abschnitt 5) |
| Boot-Mount | `Kernel.cs:196` `MountData`, `Storage/DataDiskSelector.cs` `Decide`, `Storage/CosmosDataDisk.cs:15-30` | Bestand = `0x0C` auf `sata0`; sonst Rückfall `sata0` mit genau einer Partition | **ESP + ext2 = zwei Partitionen auf `sata0`** → `Decide` liefert `Blocked`/`UnexpectedPartitionCount`; **Typ kommt nur aus der MBR-Tabelle** (`Mbr.Parse`), GPT-Platten haben Typ 0 |
| Recovery | `ProjektZeh.csproj:14,19` (`ZEHOS_RECOVERY`, `ZEHOS_SECURITY_RECOVERY`), `Kernel.cs:48-52,228-234`, `SystemServices/BootService.cs:46-58` (`IsRecoveryMode` bei Anzeigefehler/erzwungen), `Shell/AccountShellCommands.cs:114-129` (`security recover`) | Recovery ist ein eigener Build/ISO; auf der installierten Platte nur der Rückfallkernel `.bak` | kein Recovery-Pfad, der ohne ext2 und ohne Konten einen Reparaturbefehl bietet |
| Tests | `ProjektZeh.Tests/InstallJobTests.cs`, `InstallServiceTests.cs`, `SetupWizardTests.cs`, `UpdateServiceTests.cs`, `UpdateIoTests.cs`; Gastprüfung nur manuell in `docs/INSTALLER_UPDATES_TESTS.md` (kein `scripts/test-installer-*.py`) | Mocks von `IInstallTarget`/`IInstallPlatform` | neue Hosttests und ein Gastskript nötig |

## 2. Wiederverwendbare Cosmos-3.0.97-APIs und Lücken (Quelle `efcf20c`)

| Baustein | Befund | Nutzung |
|---|---|---|
| `StorageManager.Devices/GetPartitions/RescanPartitions`, `Partition` (`Host/StartSector/BlockCount/BlockSize`) | `Partition` kennt keinen Typ; `Gpt.Parse` akzeptiert Einträge ab `LBA 34`, prüft **keine CRCs** (`Gpt.cs:20-21`, `:236-246`) | Partitionsliste und Neuerkennung nach dem Schreiben der Tabelle |
| `Gpt.Create/AddPartition` (`Gpt.cs:255-330`) | schreibt Schutz-MBR und Primärheader, **CRC32 des Headers und der Eintragstabelle = 0** (`:73`, `:296`, `:307`), **keinen Backup-Header** | **für UEFI-Boot untauglich**: Firmware verlangt gültige CRCs (Annahme aus der UEFI-Spezifikation, nicht im Gast geprüft). Eigener GPT-Schreiber mit CRC32 + Backup nötig (reine Funktion, hosttestbar) |
| `Gpt`-Konstanten | nur `BasicDataPartitionType`; ESP-GUID `C12A7328-F81F-11D2-BA4B-00A0C93EC93B` und Linux-Dateisystem `0FC63DAF-8483-4772-8E79-3D69D8477DE4` fehlen; keine Partitionsnamen/Attribute | eigene Konstanten |
| `Mbr.Create/AddPartition`, `PartitionManager.Create(…, mbrSystemId, gptType)` | Fassade: GPT wenn `Gpt.IsGpt`, sonst MBR; MBR-Felder 32 Bit (2 TiB) | aktuelle Installerbasis; `PartitionManager.Create` verwendet bei GPT den CRC-losen Weg |
| `VfsManager.TryFormat/TryMount/TryUnmount/RegisterFileSystem`, `FatFileSystemType`, `FatFormatOptions` (Label, Typ) | FAT32-Formatter schreibt Bootsektor, FSInfo, Backup, FATs, Root (`FatFormatter.cs:166-195`); Label wird übernommen | ESP-Format wie heute |
| `Ext2FileSystemType`, `Ext2FormatOptions`, Plug `CosmosExt2FormatterPlug` → `CosmosExt2Formatter.FormatForKernel` | Cosmos-Formatter ist laut `docs/STORAGE_3_0_97_FINDINGS.md` inkonsistent (18/18 Images, `e2fsck`-Fehler); korrigierter Formatter im Repo (`Storage/Ext2/README.md`), im Produkt **nirgends eingehängt**, Obergrenze 128 GiB | Root-Format **erst nach** dokumentiertem Go der e2fsck-Matrix; `Ext2FeatureGate.Evaluate` vor jedem Mount |
| `Ext2Superblock.TryCreate`/Gate | Superblock lesbar ohne Mount; Gate liefert `Ok/ReadOnly/Reject` | Root-Gesundheitsprobe (Abschnitt 5) |
| `DiskEmptyCheck` (Etappe 2) | Vollscan ≤4 GiB, darüber Stichprobe mit `--yes-large`-Bestätigung | ersetzt die 256-MiB-`IsBlank`-Kürzung im Installer (Entscheidung offen) |

## 3. Entwurf: Ziel-Layout

- **Partitionstabelle:** GPT mit Schutz-MBR, 1-MiB-Ausrichtung (LBA 2048 wie heute), Primär- und Backup-Header, korrekte CRC32. Schreiber als reine, injizierbare Klasse (`GptLayoutWriter`: Plattengröße, Sektorgröße 512, GUID-Quelle → Byte-Image von Sektor 0..33 und letzten 33 Sektoren). Disk-/Partitions-GUIDs aus der vorhandenen Zufallsquelle (`CosmosSaltSource`/Hardware-Random), im Test fest.
- **Partition 1 – ESP:** Typ `C12A7328-…`, FAT32, Label `ZEHOSBOOT`, Standard 512 MiB (Kernel + `.bak` + Limine; konkrete Größe nach Payloadmessung festlegen), Inhalt wie heute: `/EFI/BOOT/BOOTX64.EFI`, `/boot/ProjektZeh.elf(.bak)`, `/boot/zehos-version.txt`, `/boot/limine/limine.conf`. Bleibt bis Etappe 4 auch `/data` (Konten, Pakete, Updatekonfiguration).
- **Partition 2 – Root:** Typ `0FC63DAF-…`, GPT-Name `ZEHOS-ROOT`, ext2 mit 4096-Byte-Blöcken, ext2-Volumename `zehos-root`, Größe = Rest der Platte, **höchstens 128 GiB** (Formatterobergrenze); der Rest bleibt unpartitioniert (für spätere Datenpartition, nicht Teil von 7a). Wird in 7a formatiert und geprüft, aber **nicht** als `/` eingehängt (Etappe 4).
- **Mindestgröße:** ESP 512 MiB + Root ≥ 256 MiB + Ränder; praktische Untergrenze ≈ 1 GiB (heutige 128 MiB entfallen). Obergrenze 2 TiB entfällt (GPT).
- **Boot:** Limine bleibt auf der ESP; `boot():` verweist auf die Partition, von der `BOOTX64.EFI` startet. Kein ext2-Zugriff zum Booten, Rückfalleintrag `.bak` bleibt. Ob Limine/OVMF das neue GPT-Layout startet, ist die zentrale Gastprüfung.
- **Auswahl beim Boot (`Decide`):** muss das Layout erkennen (Typ aus GPT lesen statt nur MBR). Ein installiertes System (ESP + Root auf `sata0`) mountet die ESP als `/data`, ohne dass „zwei Partitionen" zu `Blocked` führt. Vorschlag: reine Klassifikation `InstalledLayout.Classify(partitions)` → `None | Complete | RootMissing | RootDamaged`, Eingabe Typ-GUIDs/Namen aus einer GPT-fähigen `CosmosDataDisk.Describe`.
- **Schutz:** Installer bietet Platten mit `ZEHOSDATA`-Partition weiter nicht an (GPT-Erkennung nachziehen!); Medium (CD001) und eingebundene Ziele bleiben ausgeschlossen; Löschen nur mit `--erase`.

## 4. Entwurf: Installer-Ablauf

Stufen: Partitionieren (GPT schreiben, Backup, Flush, `RescanPartitions`, **Zählen: genau 2 mit erwarteten Typen**) → ESP formatieren und einhängen → Root formatieren (`TryFormat` ext2) → **Root prüfen** (Gate + Superblocklesen; Host-`e2fsck -fn` nur im Test) → Dateien kopieren → Limine-Config → Aushängen/Flush. Schlägt eine Stufe fehl: Abbruch mit Bereinigung wie `InstallJob.Step` (bestehendes Muster), Meldung nennt, dass Tabelle/Teile zurückbleiben (Hinweis wie P3 (d) bei `disk init`). Kein stilles Weiterverwenden einer Root-Partition, die das Gate nicht mit `Ok` passiert.

## 5. Entwurf: fehlendes oder beschädigtes Root sperrt die Kontoeinrichtung

Zustände (pure Funktion `RootHealth.Evaluate(layout, rootSuperblockBytes?)`):
1. **Kein installiertes Layout** (keine Root-Partition erwartet): Verhalten wie heute.
2. **Installiertes Layout, Root vollständig und Gate `Ok`:** normal.
3. **Installiertes Layout, Root fehlt** (Partitionstyp fehlt/Eintrag gelöscht): `Blocked` (`RootMissing`).
4. **Root vorhanden, aber Magic falsch, Gate `Reject`, Superblock nicht lesbar oder `e2fsck`-ähnliche Plausibilitätsprüfung (Größe > Partition) scheitert:** `Blocked` (`RootDamaged`); `ReadOnly` bei unbekanntem RoCompat bleibt lesbar, aber ohne Einrichtung.

In den Zuständen 3 und 4 gilt dieselbe Mechanik wie Etappe 2: `DataDiskSelector.SetupBlockedReason` → `AccountService.SetupBlockedReason`, serielle Fehlerzeile `STORAGE Fehler: … Keine Ersteinrichtung.`, **kein Konto, keine Ersteinrichtung**, auch nicht beim Boot-Fenster. Die ESP darf für die Shell/Recovery eingehängt bleiben; Konten werden dann **nicht** neu angelegt, auch wenn dort keine Kontendatei liegt. Offen: ob eine vorhandene Kontendatei auf der ESP bei Root-Schaden weiter zur Anmeldung dient (Vorschlag: ja, nur Einrichtung gesperrt).

## 6. Entwurf: Recovery ohne ext2

- **FAT-Recovery:** Kernel, Limine, Rückfalleintrag und Konten liegen auf der ESP und starten ohne ext2. Root-Schaden verhindert den Boot nicht, sperrt nur die Einrichtung.
- **ISO-Recovery:** der vorhandene Installer-/Recovery-ISO-Build (kein ext2 nötig, `ZEHOS_SECURITY_RECOVERY`). Ergänzung vorgeschlagen: Shellbefehl `setup repair-root <Laufwerk> --erase-root` (formatiert nur die Root-Partition neu, ESP und Konten bleiben, braucht Bestätigung und Admin/Recovery); in 7a nur entwerfen und mit Hosttest des Zustandsautomaten absichern, nicht implementieren, bevor das Gate/Formatter-Go vorliegt.
- **Auswahl-Rückfall:** der Rückfall „`sata0` mit genau einer Partition" bleibt für alte MBR-Installationen (0xEF) gültig; neue GPT-Installationen werden über die Klassifikation erkannt.

## 7. Testspezifikation (Tests zuerst)

**Hosttests (reine Logik, `ProjektZeh.Tests`, einzelne Compile-Includes):**
1. `GptLayoutWriter`: für 1 GiB, 32 GiB, 128 GiB und `2 TiB + 512`: Header-CRC und Tabellen-CRC gegen feste Referenzwerte (aus `zlib.crc32` des Testgenerators), Primär bei LBA 1, Backup in den letzten 33 Sektoren, `FirstUsable=34`, `LastUsable=BlockCount-34`, Partitionen 1-MiB-ausgerichtet und innerhalb der nutzbaren Grenzen, Root ≤ 128 GiB, keine Überlappung, zu kleine Platte (< ≈1 GiB) und Sektor ≠ 512 verweigert **vor jedem Schreibzugriff** (Zähl-Fakegerät: 0 Zugriffe).
2. Typ-GUID-Bytereihenfolge (gemischtes Endian): `C12A7328-…` und `0FC63DAF-…` als Rohbytes.
3. `InstalledLayout.Classify`/`DataDiskSelector.Decide`-Erweiterung: GPT-Typen statt MBR; ESP+Root ⇒ eingehängt wird die ESP, **nicht** `Blocked`; ESP ohne Root ⇒ `RootMissing`; Root mit zerstörtem Magic ⇒ `RootDamaged`; weiterhin: Bestand `0x0C`, `ZEHOSDATA`, zwei Labels.
4. `RootHealth`/`SetupBlockedReason`: Zustände 1-4, `NoDataDisk` unverändert, Meldung enthält Ursache; Integration mit `AccountService` (Fenster offen, gemountet, Datei fehlt) ⇒ `NeedsSetup=false`.
5. `InstallJob`: neue Stufenreihenfolge, Abbruch in jeder Stufe ruft `Abort` und meldet zurückbleibende Teile; Prozentwerte monoton; Ziel-Fake zählt Aufrufe (Partitionieren genau einmal, Root-Format nach ESP).
6. `InstallService.Begin`: neue Mindestgröße, 2-TiB-Grenze entfernt, `ZEHOSDATA`-Platte und Medium geschützt, Leerprüfung über `DiskEmptyCheck` (Stichprobe nur mit Bestätigung).
7. Update-Ziel: `FileUpdateTarget`/`UpdateController` unverändert grün mit ESP als `/data` (Regression, Pfade `boot/…`).
8. Bestandsimages: ein 64-MiB-MBR-Image mit Prüfsumme bleibt von `Decide`/Installer-Auswahl unverändert.

**Host-Gegenprüfung (Python, Werkzeuge nur wenn vorhanden, sonst „offen"):** `scripts/test-installer-layout.py` erzeugt ein neues sparse Image (existierender Pfad = Abbruch), lässt den Host-Schreiber-Ausgabe (Hosttest-Binary oder byteidentischer Python-Spiegel) anwenden und prüft mit `sfdisk -l`/`sgdisk -v` (Tabelle gültig), `fsck.fat -n` auf der ESP und `e2fsck -fn` auf der Root-Partition. Exit 0 nur wenn alles grün.

**Gasttests (QEMU+OVMF, danach VMware; nur auf Ansage, ein Gast gleichzeitig):**
- G1 frische Installation auf 1 GiB, 32 GiB, 128 GiB: Boot per OVMF von der installierten GPT-Platte (zentrale Annahme), `disk list` zeigt zwei Partitionen, ESP als `/data`, Anmeldung/Ersteinrichtung wie heute.
- G2 `.bak`-Rückfalleintrag und `update` (7b-Regression nur Lesen der Pfade).
- G3 Root fehlt (Partition 2 offline löschen): Boot gelingt, serielle Fehlerzeile, **keine** Ersteinrichtung, Recovery-Shell erreichbar.
- G4 Root beschädigt (ext2-Magic/Incompat-Bit offline ändern): wie G3.
- G5 Recovery-ISO bei G3/G4: bootet ohne ext2, `disk list`, ESP-Zugriff, Konten unverändert.
- G6 Installer verweigert Medium, `ZEHOSDATA`-Platte und eingebundenes Ziel; Bestandsplatte nur mit `--erase`.
- G7 Auswahl bei zwei Platten (installierte Platte + `ZEHOSDATA`): Bestand gewinnt, Hinweis im Protokoll.

## 8. Offene Entscheidungen und Risiken

1. **Eigener GPT-Schreiber vs. MBR bleiben:** Cosmos-`Gpt` ist für UEFI-Boot unbrauchbar (CRC 0, kein Backup). Empfehlung: eigener Schreiber (klein, rein, hosttestbar). Alternative für 7a: MBR mit zwei Primärpartitionen (0xEF + 0x83), behält die 2-TiB-Grenze und mehrere UEFI-Firmwares verlangen GPT; Entscheidung des Leads.
2. **Formatter-Go:** Root-Format erst nach belegter `e2fsck`-Matrix des korrigierten Formatters (Etappe 1); bis dahin Installer-Root-Stufe hinter Schalter/ohne Aktivierung.
3. **ESP-Größe** nach gemessener Payload (Kernel × 2 + EFI); **Root-Cap 128 GiB** und Rest der Platte.
4. **GPT-Typen im Kernel lesen:** `CosmosDataDisk.Describe` liest heute nur MBR; GPT ⇒ Typ 0. Muss zusammen mit dem Installer-Ausschluss (`HasDataPartition`) für GPT erweitert werden, sonst wäre eine GPT-`ZEHOSDATA`-Platte als Installationsziel sichtbar.
5. **Gast belegt nur MBR-Boot unter OVMF** (`docs/INSTALLER_UPDATES_TESTS.md`); GPT-Boot ist neu.
6. **Konten bei Root-Schaden** (Abschnitt 5, offene Frage) und **`setup repair-root`** brauchen eine Leitungsentscheidung, bevor Code entsteht.
7. VMware (eigene AHCI-Platte), Prüfgrößen und Zeitbudgets wie in Etappe 2 (`DiskEmptyCheck`: ≈100 MB/s Vollscan unter KVM gemessen).
