# Etappe 7a: Installer-Layout ESP + ext2, Boot und Recovery (Inventar, Entwurf, Testspezifikation)

Stand: 17. November 2026, Basis `origin/feature/storage-data-disk` @ `22dd279` (ZehOS 0.5.12). **Nur lesen und entwerfen:** keine Formatier-, Boot- oder Installeränderung, nichts davon im Gast ausgeführt. Grundlage: [Plan, Etappe 7](../plans/2026-11-17-storage-linux.md#etappe-7-installer-updates-und-energieaktionen), [Aufrufer-Inventar](2026-11-17-storage-callers.md), [Etappe-2-Entscheidungen](2026-11-17-storage-etappe2-design.md), Cosmos-Quelle `efcf20c` (= 3.0.97). Aussagen zu Cosmos stammen aus dem Quellbaum, nicht aus dem Gast.

## 0. Entscheidungen der Storage-Leitung (09:12/09:15, nachgezogen)

1. **Zielumfang:** 32 bis 128 GiB, 512-Byte-Sektoren, ESP 512 MiB (Payload ≈ 19,7 MiB × 2 reicht), Root = Rest. **Keine Freigabe über 128 GiB**, **kein `repair-root`** und keine neue Löschfunktion in 7a.
2. **GPT:** mit CRC32 und Backup, als schmaler, geprüfter eigener Writer statt `Gpt.Create/AddPartition` (CRC 0, kein Backup; Befund lokal gegen 3.0.97 bestätigt). `Gpt.Parse`, `Partition` und `RescanPartitions` von Cosmos werden wiederverwendet.
3. **Identität des installierten Systems** (Abschnitt 5): fehlendes Root muss auch bei **gelöschtem GPT-Eintrag** erkannt werden (ESP-Marker + Root-GUID); unklare Identität sperrt die Einrichtung. Ein Rootfehler führt in die Recovery; **vorhandene Konten werden niemals neu angelegt oder ersetzt.**
4. **GUIDs eindeutig:** Disk- und Partitions-GUIDs aus der Hardware-Zufallsquelle, **keine** aus der Plattengröße abgeleiteten (deterministischen) Cosmos-GUIDs.
5. **Reihenfolge/Aktivierung:** Writer-Code erst nach der SIMD-Aufgabe (ClassiCube S1) und nach dem Designreview; **keine Installeraktivierung vor Etappe 3 und vor der Qualifikation**.
6. **`security recover`:** wird bei `RootMissing`, `RootDamaged` und `Unclear` ebenfalls verweigert (09:18/09:29); eine Kontendatei wird nie umbenannt oder ersetzt. Die Recovery erlaubt Diagnose und Backup (lesend); ein neuer schreibender Reparaturvertrag kommt später.
7. **Installer:** der neue GPT-Installer gilt nur für 32 bis 128 GiB und ist eine **ausdrücklich gewählte Option**; der bestehende Legacy-Installer bleibt vorerst erhalten, es gibt **nie ein stilles Fallback** zwischen beiden.
8. **Formatter:** das No-Go des **Original**-Cosmos-ext2-Formatters ist ein historischer Befund; die Korrekturmatrix des korrigierten Formatters ist belegt. Die aktuellen Zieltests (32/64/128 GiB, 4-KiB-Blöcke, im Kernel-Plug) sind trotzdem noch offen.
9. **Beweisregel:** kein byteidentischer Python-Spiegel als Nachweis. Die Hostprobe führt den **produktiven** Writer (dieselbe C#-Quelle) aus.

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
| `Gpt.Create/AddPartition` (`Gpt.cs:255-330`) | schreibt Schutz-MBR und Primärheader, **CRC32 des Headers und der Eintragstabelle = 0** (`:73`, `:296`, `:307`), **keinen Backup-Header** | **für UEFI-Boot untauglich** (Firmware verlangt gültige CRCs; Befund von der Storage-Leitung lokal bestätigt). Entscheid: eigener schmaler GPT-Writer mit CRC32 + Backup, `Gpt.Create/AddPartition` wird **nicht** verwendet |
| `Gpt`-Konstanten | nur `BasicDataPartitionType`; ESP-GUID `C12A7328-F81F-11D2-BA4B-00A0C93EC93B` und Linux-Dateisystem `0FC63DAF-8483-4772-8E79-3D69D8477DE4` fehlen; keine Partitionsnamen/Attribute | eigene Konstanten |
| `Mbr.Create/AddPartition`, `PartitionManager.Create(…, mbrSystemId, gptType)` | Fassade: GPT wenn `Gpt.IsGpt`, sonst MBR; MBR-Felder 32 Bit (2 TiB) | aktuelle Installerbasis; `PartitionManager.Create` verwendet bei GPT den CRC-losen Weg |
| `VfsManager.TryFormat/TryMount/TryUnmount/RegisterFileSystem`, `FatFileSystemType`, `FatFormatOptions` (Label, Typ) | FAT32-Formatter schreibt Bootsektor, FSInfo, Backup, FATs, Root (`FatFormatter.cs:166-195`); Label wird übernommen | ESP-Format wie heute |
| `Ext2FileSystemType`, `Ext2FormatOptions`, Plug `CosmosExt2FormatterPlug` → `CosmosExt2Formatter.FormatForKernel` | *Historisch:* der **Original**-Cosmos-Formatter war laut `docs/STORAGE_3_0_97_FINDINGS.md` inkonsistent (18/18 Images, `e2fsck`-Fehler, No-Go). Der korrigierte Formatter im Repo (`Storage/Ext2/README.md`) hat eine belegte Korrekturmatrix; im Produkt **nirgends eingehängt**, Obergrenze 128 GiB, aktuelle Zieltests offen | Root-Format **erst nach** dokumentiertem Go der e2fsck-Matrix; `Ext2FeatureGate.Evaluate` vor jedem Mount |
| `Ext2Superblock.TryCreate`/Gate | Superblock lesbar ohne Mount; Gate liefert `Ok/ReadOnly/Reject` | Root-Gesundheitsprobe (Abschnitt 5) |
| `DiskEmptyCheck` (Etappe 2) | Vollscan ≤4 GiB, darüber Stichprobe mit `--yes-large`-Bestätigung | ersetzt die 256-MiB-`IsBlank`-Kürzung im Installer (Entscheidung offen) |

## 3. Entwurf: Ziel-Layout

- **Partitionstabelle:** GPT mit Schutz-MBR, 1-MiB-Ausrichtung (LBA 2048 wie heute), Primär- und Backup-Header, korrekte CRC32. Schreiber als schmale, injizierbare Klasse (`GptLayoutWriter`: Plattengröße, Sektorgröße 512, GUID-Quelle → Bytes für Sektor 0..33 und die letzten 33 Sektoren), nur für den Umfang 32 bis 128 GiB; alles andere wird **vor jedem Schreibzugriff** verweigert. **GUIDs:** Disk-GUID und Partitions-GUIDs kommen aus der Hardware-Zufallsquelle (Version-4-Bits gesetzt) und sind je Installation eindeutig; keine Ableitung aus der Plattengröße (das macht `Gpt.Create` mit `WriteDeterministicGuid` und würde gleich große Platten verwechselbar machen). Im Test wird die Quelle injiziert; zusätzlich ein Test, dass zwei Installationen verschiedene GUIDs erhalten.
- **Partition 1 – ESP:** Typ `C12A7328-…`, FAT32, Label `ZEHOSBOOT`, Standard 512 MiB (Kernel + `.bak` + Limine; konkrete Größe nach Payloadmessung festlegen), Inhalt wie heute: `/EFI/BOOT/BOOTX64.EFI`, `/boot/ProjektZeh.elf(.bak)`, `/boot/zehos-version.txt`, `/boot/limine/limine.conf`. Bleibt bis Etappe 4 auch `/data` (Konten, Pakete, Updatekonfiguration).
- **Partition 2 – Root:** Typ `0FC63DAF-…`, GPT-Name `ZEHOS-ROOT`, ext2 mit 4096-Byte-Blöcken, ext2-Volumename `zehos-root`, Größe = Rest der Platte (Zielumfang 32 bis 128 GiB, also Root ≤ ≈127,5 GiB, innerhalb der Formatterobergrenze). Wird in 7a formatiert und geprüft, aber **nicht** als `/` eingehängt (Etappe 4). Keine Freigabe über 128 GiB.
- **Größenbereich:** 32 bis 128 GiB (Entscheid). Platten außerhalb bekommen das neue Layout nicht (offen, ob der bisherige Ein-Partition-Installer für kleine Platten bestehen bleibt oder sie abgelehnt werden); ESP 512 MiB, Root = Rest.
- **Boot:** Limine bleibt auf der ESP; `boot():` verweist auf die Partition, von der `BOOTX64.EFI` startet. Kein ext2-Zugriff zum Booten, Rückfalleintrag `.bak` bleibt. Ob Limine/OVMF das neue GPT-Layout startet, ist die zentrale Gastprüfung.
- **Auswahl beim Boot (`Decide`):** muss das Layout erkennen (Typ aus GPT lesen statt nur MBR). Ein installiertes System (ESP + Root auf `sata0`) mountet die ESP als `/data`, ohne dass „zwei Partitionen" zu `Blocked` führt. Vorschlag: reine Klassifikation `InstalledLayout.Classify(partitions)` → `None | Complete | RootMissing | RootDamaged`, Eingabe Typ-GUIDs/Namen aus einer GPT-fähigen `CosmosDataDisk.Describe`.
- **Schutz:** Installer bietet Platten mit `ZEHOSDATA`-Partition weiter nicht an (GPT-Erkennung nachziehen!); Medium (CD001) und eingebundene Ziele bleiben ausgeschlossen; Löschen nur mit `--erase`.

## 4. Entwurf: Installer-Ablauf

Stufen: Partitionieren (GPT schreiben, Backup, Flush, `RescanPartitions`, **Zählen: genau 2 mit erwarteten Typen**) → ESP formatieren und einhängen → Root formatieren (`TryFormat` ext2) → **Root prüfen** (Gate + Superblocklesen; Host-`e2fsck -fn` nur im Test) → Dateien kopieren → Limine-Config → Aushängen/Flush. Schlägt eine Stufe fehl: Abbruch mit Bereinigung wie `InstallJob.Step` (bestehendes Muster), Meldung nennt, dass Tabelle/Teile zurückbleiben (Hinweis wie P3 (d) bei `disk init`). Kein stilles Weiterverwenden einer Root-Partition, die das Gate nicht mit `Ok` passiert.

## 5. Entwurf: Identität des installierten Systems, fehlendes oder beschädigtes Root

**Problem:** Wird der Root-GPT-Eintrag gelöscht, sieht die Platte wie eine reine ESP-Platte aus; ohne weitere Information wäre das „kein installiertes Layout" und die Einrichtung würde angeboten. Deshalb trägt die ESP die Identität:

- **ESP-Marker** `/boot/zehos-install.id` (Textdatei, Format-Version, Disk-GUID, Root-Partitions-GUID, Root-Startsektor/-Größe, vom Installer als letzter Schritt geschrieben). Der Dateiname gehört zum Schutz der `.zehos-`-/Systempfade (Etappe 3 festlegen).
- **Prüfung beim Boot** (reine Funktion `InstalledIdentity.Evaluate(marker?, gptEntries, diskGuid, rootSuperblock?)`):

| Lage | Ergebnis |
|---|---|
| kein Marker, ESP ohne Root-Partition (Altinstallation/Datenplatte) | `Legacy`: Verhalten wie heute |
| Marker lesbar, Disk-GUID passt, GPT-Eintrag mit Root-GUID vorhanden, Lage/Größe passen, Gate `Ok` | `Healthy` |
| Marker lesbar, aber **kein GPT-Eintrag mit der Root-GUID** (auch bei sonst gültiger Tabelle) | `RootMissing` |
| Eintrag vorhanden, Lage/Größe weichen ab, Magic falsch, Gate `Reject` oder Superblock unlesbar | `RootDamaged` (`ReadOnly` bei unbekanntem RoCompat: lesbar, keine Einrichtung) |
| Marker unlesbar/beschädigt, Disk-GUID passt nicht, Tabelle ungültig oder mehrdeutig | `Unclear` |

- **Folge:** `RootMissing`, `RootDamaged` und `Unclear` ergeben `Blocked` → `DataDiskSelector.SetupBlockedReason` → `AccountService.SetupBlockedReason` (Mechanik aus Etappe 2): keine Ersteinrichtung, kein neues Konto, serielle Fehlerzeile `STORAGE Fehler: … Keine Ersteinrichtung.` **Unklare Identität sperrt grundsätzlich.** Der Boot führt in die Recovery-Shell (Anzeige/Textshell), nicht in eine Neueinrichtung.
- **Vorhandene Konten werden niemals neu angelegt oder ersetzt:** Der Rootfehler-Pfad enthält keinen Aufruf von `RecoveryWork`/`SetupWork`. Entscheid: `security recover` (Sicherheits-Recovery-Build, verschiebt heute die Kontendatei und legt ein neues Konto an, `AccountService.RecoveryWork`) wird bei `RootMissing/RootDamaged/Unclear` **verweigert**. Umsetzungsvorschlag: `AccountService.RecoveryBlockedReason` (Gegenstück zu `SetupBlockedReason`), vom Kernel aus `InstalledIdentity` gesetzt; Test: Kontendatei vor/nach byteidentisch, keine `.zehos-security-recovery-*.db`-Datei entsteht. Erlaubt bleiben Diagnose und lesendes Backup.
- **Grenze:** Ein Angreifer mit Schreibzugriff auf die ESP kann den Marker löschen (dann `Legacy`); das ist kein Schutz gegen Offline-Manipulation, nur gegen Versehen und Teilfehler. Zu dokumentieren.

## 6. Entwurf: Recovery ohne ext2

- **FAT-Recovery:** Kernel, Limine, Rückfalleintrag und Konten liegen auf der ESP und starten ohne ext2. Root-Schaden verhindert den Boot nicht, sperrt nur die Einrichtung.
- **ISO-Recovery:** der vorhandene Installer-/Recovery-ISO-Build (kein ext2 nötig, `ZEHOS_SECURITY_RECOVERY`). **Keine** neue Reparatur-/Löschfunktion (`repair-root` entfällt in 7a); Root-Reparatur ist später und gesondert freizugeben. Die Recovery darf bei Rootfehlern lesen, `disk list` und Diagnose anbieten, aber keine Konten anlegen oder ersetzen.
- **Auswahl-Rückfall:** der Rückfall „`sata0` mit genau einer Partition" bleibt für alte MBR-Installationen (0xEF) gültig; neue GPT-Installationen werden über die Klassifikation erkannt.

## 7. Testspezifikation (Tests zuerst)

**Hosttests (reine Logik, `ProjektZeh.Tests`, einzelne Compile-Includes):**
1. `GptLayoutWriter`: für 32 GiB, 64 GiB, 128 GiB (Umfang) sowie 31 GiB, 128 GiB + 512 B und `2 TiB + 512` (verweigert): Header-CRC und Tabellen-CRC gegen feste Referenzwerte (aus `zlib.crc32` des Testgenerators), Primär bei LBA 1, Backup in den letzten 33 Sektoren, `FirstUsable=34`, `LastUsable=BlockCount-34`, Partitionen 1-MiB-ausgerichtet und innerhalb der nutzbaren Grenzen, Root ≤ 128 GiB, keine Überlappung, Platte außerhalb 32 bis 128 GiB und Sektor ≠ 512 verweigert **vor jedem Schreibzugriff** (Zähl-Fakegerät: 0 Zugriffe).
2. Typ-GUID-Bytereihenfolge (gemischtes Endian): `C12A7328-…` und `0FC63DAF-…` als Rohbytes.
3. `InstalledIdentity.Evaluate` (Tabelle Abschnitt 5): jede Zeile ein Test, insbesondere **gelöschter Root-Eintrag bei vorhandenem Marker ⇒ `RootMissing`**, falsche Disk-GUID/unlesbarer Marker ⇒ `Unclear`; `DataDiskSelector.Decide`-Erweiterung: GPT-Typen statt MBR; ESP+Root ⇒ eingehängt wird die ESP, **nicht** `Blocked`; weiterhin Bestand `0x0C`, `ZEHOSDATA`, zwei Labels.
3a. GUIDs: zwei Läufe mit der Hardware-Quelle ⇒ verschiedene Disk-/Partitions-GUIDs, Version-4-Bits; gleich große Platten ⇒ verschieden (Gegenprobe zu `WriteDeterministicGuid`).
4. `SetupBlockedReason` für `RootMissing/RootDamaged/Unclear`, `NoDataDisk` unverändert, Meldung enthält Ursache; Integration mit `AccountService` (Fenster offen, gemountet, Datei fehlt) ⇒ `NeedsSetup=false`; **vorhandene Kontendatei bleibt byteidentisch** (Prüfsumme vor/nach).
5. `InstallJob`: neue Stufenreihenfolge, Abbruch in jeder Stufe ruft `Abort` und meldet zurückbleibende Teile; Prozentwerte monoton; Ziel-Fake zählt Aufrufe (Partitionieren genau einmal, Root-Format nach ESP).
6. `InstallService.Begin`: neue Mindestgröße, 2-TiB-Grenze entfernt, `ZEHOSDATA`-Platte und Medium geschützt, Leerprüfung über `DiskEmptyCheck` (Stichprobe nur mit Bestätigung).
7. Update-Ziel: `FileUpdateTarget`/`UpdateController` unverändert grün mit ESP als `/data` (Regression, Pfade `boot/…`).
8. Bestandsimages: ein 64-MiB-MBR-Image mit Prüfsumme bleibt von `Decide`/Installer-Auswahl unverändert.

**Host-Gegenprüfung (Beweisregel: produktiver Writer, kein Python-Spiegel):** ein Hostprogramm unter `experiments/` (wie `StorageProbe`, bindet die **produktiven** Quellen `GptLayoutWriter`/Identität per Compile-Include ein) schreibt in ein NEUES sparse Image (existierender Pfad = Abbruch); `scripts/test-installer-layout.py` ruft es auf und prüft das Ergebnis mit `sfdisk -l`/`sgdisk -v` (Tabelle und Backup gültig), `fsck.fat -n` auf der ESP und `e2fsck -fn` auf der Root-Partition. Exit 0 nur wenn alles grün; fehlende Werkzeuge werden als offen gemeldet, nicht als bestanden. Python kennt nur Aufruf und Auswertung, nie die Tabellenbytes selbst.

**Gasttests (QEMU+OVMF, danach VMware; nur auf Ansage, ein Gast gleichzeitig):**
- G1 frische Installation auf 1 GiB, 32 GiB, 128 GiB: Boot per OVMF von der installierten GPT-Platte (zentrale Annahme), `disk list` zeigt zwei Partitionen, ESP als `/data`, Anmeldung/Ersteinrichtung wie heute.
- G2 `.bak`-Rückfalleintrag und `update` (7b-Regression nur Lesen der Pfade).
- G3 Root fehlt (Partition 2 offline löschen): Boot gelingt, serielle Fehlerzeile, **keine** Ersteinrichtung, Recovery-Shell erreichbar.
- G4 Root beschädigt (ext2-Magic/Incompat-Bit offline ändern): wie G3.
- G5 Recovery-ISO bei G3/G4: bootet ohne ext2, `disk list`, ESP-Zugriff, Konten unverändert.
- G6 Installer verweigert Medium, `ZEHOSDATA`-Platte und eingebundenes Ziel; Bestandsplatte nur mit `--erase`.
- G7 Auswahl bei zwei Platten (installierte Platte + `ZEHOSDATA`): Bestand gewinnt, Hinweis im Protokoll.

## 8. Offene Punkte nach den Entscheidungen

1. **Reihenfolge:** SIMD (S1) zuerst, dann Designreview dieses Dokuments, dann Writer-Code und Hostprobe; **keine Installeraktivierung vor Etappe 3 und Qualifikation.**
2. **ESP-Größe** 512 MiB ist entschieden; Details (Marker-Pfad/-Format, Root-Identität) nach der API-Prüfung.
3. **Aktuelle Zieltests des korrigierten ext2-Formatters** (32/64/128 GiB, 4-KiB-Blöcke, Kernel-Plug) stehen aus; der Root-Format-Schritt bleibt bis dahin hinter einem Schalter.
4. **GPT-Typen im Kernel lesen:** `CosmosDataDisk.Describe` liest heute nur MBR (GPT ⇒ Typ 0); gemeinsam mit dem Installer-Ausschluss (`HasDataPartition`) für GPT erweitern, sonst wäre eine GPT-`ZEHOSDATA`-Platte als Installationsziel sichtbar.
5. **GPT-Boot unter OVMF** ist ungeprüft (belegt ist nur MBR-Boot, `docs/INSTALLER_UPDATES_TESTS.md`).
6. **Installerwahl:** wie der Legacy-Installer und der neue GPT-Installer in `setup list`/`setup install`/`SetupWizard` ausdrücklich gewählt werden (eigene Option, kein Fallback), ist für den Entwurf der Bedienung noch zu spezifizieren; Platten außerhalb 32 bis 128 GiB sind für den neuen Installer nicht wählbar.
7. VMware (eigene AHCI-Platte), Zeitbudgets wie in Etappe 2 (`DiskEmptyCheck`: ≈100 MB/s Vollscan unter KVM).
