# Storage-Aufrufer und Plattenannahmen (Etappe 2, Inventar)

Stand: 17. November 2026, Basis `feature/storage-linux` @ `8ad5551`. Nur Lesen, kein Code geändert; nichts davon wurde im Gast ausgeführt. Frage je Fund: was bricht bei einer zweiten oder größeren Platte (oder anderem Dateisystem als FAT32 auf Partition 0)?

## 1. Kernel: Boot, Mountpunkt, Energie

| Fundstelle | Annahme | Bruch bei 2. / größerer Platte |
|---|---|---|
| `ProjektZeh/Kernel.cs:189` `storage.Mount("sata0")` | Datenplatte heißt fest `sata0` | Eine zweite Platte wird nie eingebunden. Wird eine andere Platte `sata0` (z. B. Systemplatte/Reihenfolge der AHCI-Ports), hängt der Gast die falsche unter `/data` ein. |
| `Kernel.cs:198` Hinweistext `disk init sata0` | wie oben | Nur Text, aber Bedienungsanleitung für genau eine Platte. |
| `Kernel.cs:45,47,77,124` `new("/data", …)`, `PackageStore("/data/.zehos-packages")`, `FileService(… ?? "/data")` | Konten, Pakete und Heimatverzeichnis liegen unter dem einen Mountpunkt `/data` | Mehrere Mounts: alles Persistente hängt an genau einem Dateisystem. Ist `/data` nicht gemountet, ist die Sitzung ohne Persistenz (siehe `SettingsApp.cs:63`). Wechsel auf ext2 ändert Rechte-/Namensverhalten für Konten und Paketspeicher (nicht geprüft). |
| `Kernel.cs:290` `TryGetMount("/data")` vor `storage.Unmount()` | Beim Herunterfahren wird nur `/data` ausgehängt | Weitere Mounts würden beim Poweroff/Reboot nicht synchronisiert/ausgehängt (kein `Sync`/`Drop`). |

## 2. `CosmosStorageService` (FAT-Datenplatte)

| Fundstelle | Annahme | Bruch |
|---|---|---|
| `Storage/CosmosStorageService.cs:15,17` `FileSystemName = "zehos-fat"`, `TestDiskSize = 64 MiB` | genau ein Dateisystemtyp, eine feste Testgröße | Weiteres Dateisystem (ext2) braucht eigenen Namen/Registrierung; `RegisterFileSystem` (Z. 202-215) registriert nur `FatFileSystemType`, ein zweiter Aufruf mit anderem Namen ist nicht vorgesehen, `registered` ist ein einzelnes Bool. |
| `:59-67` `InitializeTestDisk` | nur 512-Byte-Sektoren, `BlockCount == 64 MiB` exakt | Größere Platte (z. B. die 72-MiB-ext2-Testplatte) wird mit `UnsupportedDiskSize` abgelehnt; gewollt als Schutz, aber es gibt keinen Weg, eine größere Platte zu initialisieren. |
| `:69-72` | nur leere, partitionslose Platte | Platte mit GPT/ext2 wird nie initialisiert (gewollt). |
| `:86-93` `Mbr.Create`, `PartitionManager.Create(… 0x0C …)`, `PartitionStart = 2048` (Z. 18) | MBR mit einer FAT32-LBA-Partition | ext2 bräuchte Typ `0x83`; GPT wird hier nicht benutzt (Cosmos-`Gpt` siehe Vertragsdokument). |
| `:104`, `:142-148` `partitions[0]`, `Count != 1` | genau eine Partition je Platte, die erste ist die Datenpartition | Platte mit zwei Partitionen (z. B. Daten + ext2) wird mit `UnexpectedPartitionCount` abgelehnt; mit `Count == 1` wird blind `zehos-fat` darauf gemountet, auch wenn dort ext2 liegt (Mount scheitert oder, schlimmer, `disk init` formatiert nicht, aber `FormatRefused`/`MountRefused`-Pfad ist die einzige Abwehr). |
| `:131` `TryGetMount("/data")` | ein Mountpunkt | zweite Platte → `AlreadyMounted`. |
| `:33-34` `GetDevices` (`device.Name`, `GetPartitions().Count`) | nur Name/Größe/Partitionsanzahl | kein Dateisystemtyp, keine Mountinformation je Platte. |

## 3. Installer und Update

| Fundstelle | Annahme | Bruch |
|---|---|---|
| `Installer/CosmosInstallService.cs:37-40,51-58` Schleife über `StorageManager.Devices`, `IsInstallMedium` (Z. 121) | Installationsmedium wird per Erkennung ausgeschlossen, jede andere Platte ist Ziel | Eine zusätzliche Daten-/ext2-Platte erscheint als Installationsziel (`setup list`); nur der Mount-Check (Z. 57-58) und die Löschbestätigung (`--erase`) schützen. |
| `:55` `BlockSize == 512`, `BlockCount ≥ 128 MiB` (`InstallService.cs:18,39`), `BlockCount - 2048 ≤ uint.MaxValue` | 512-Byte-Sektoren, ≥128 MiB, ≤2 TiB | 64/72-MiB-Testplatten sind nie Ziel; >2 TiB abgelehnt (MBR-Grenze). |
| `:60` `TryGetMount("/data")` | Installation braucht freien `/data` | Ist die Datenplatte gemountet, ist Installation auf eine zweite Platte gesperrt. |
| `:140-150` `device.WriteBlock(0,2048,…)` und letzte 33 Sektoren, `Mbr.Create`, `Mbr.AddPartition(0xEF, 2048, …)` | MBR/EFI-Partition, alter GPT-Kopf wird gelöscht | Löscht auf der Zielplatte auch eine vorhandene GPT/ext2-Partitionstabelle (nur nach `--erase`). |
| `:160-164` `partitions[0]`, `Count != 1`, Mount nach `/data` | wie oben, EFI-Partition als FAT32 unter `/data` | Verwendet denselben `/data`-Mountpunkt wie die Datenplatte, schließt beide gegenseitig aus. |
| `:178,197` `"/data" + path`, `TryUnmount("/data")` | Schreibziel fest | – |
| `Update/FileUpdateTarget.cs:7-14`, `UpdateController.cs:10` Root `/data`, `boot/ProjektZeh.elf`, `boot/zehos-version.txt` | Das Update schreibt in die unter `/data` eingebundene EFI-Partition | Ist `/data` die Datenplatte und nicht die EFI-Partition (Normalfall im QEMU-Test), liegt `boot/` auf dem Datenlaufwerk, nicht auf der Boot-Partition; bei zwei Platten ist unklar, welche die Boot-Platte ist. Nicht im Gast geprüft. |

## 4. Weitere Verbraucher von `/data`

| Fundstelle | Zweck |
|---|---|
| `Runtime/CosmosSystemInfo.cs:44` `VfsManager.TryStatFs(mount)` | Laufende Pfadsuche nach oben bis zu einem Mount: funktioniert für mehrere Mounts, liefert je Pfad den richtigen Mount. |
| `:60-66` `ReadStorage` | zeigt nur `/data` + Geräteliste; weitere Mounts erscheinen nicht. |
| `Shell/StorageShellCommands.cs:25,74,83` `disk list\|init <Name>\|mount <Name>\|unmount` | `mount`/`unmount` ohne Mountpunkt-/Typargument; `unmount` hängt nur `/data` aus. Kein `disk mount sata1 /mnt/…`. |
| `Apps/FileManager/FileManagerApp.cs:68` `placePaths = [CurrentDirectory, "/data"]` | Orte-Leiste kennt nur `/data`; weitere Mounts nicht erreichbar außer per Pfadeingabe. |
| `Apps/Settings/SettingsApp.cs:63` `/data/hintergrund.bmp` | Standardpfad fürs Hintergrundbild. |
| `Runtime/CosmosSecurityProbe.cs:56`, `CosmosWasmProbe.cs:20,21,107,122` | Probes schreiben/lesen fest unter `/data`. |

## 5. VM-Testskripte und ihre Platten

| Fundstelle | Annahme | Bruch |
|---|---|---|
| `scripts/start-qemu.ps1:33-36,68` `.tools\qemu\data-fat32.img`, 64 MiB, `--disk …,ahci` | genau eine 64-MiB-FAT32-Platte; `-DataImagePath` (Branch `task/start-qemu-data-image`, ungeprüft) ersetzt sie nur | Zweite Platte nicht vorgesehen; alles was `sata0` ist, ist diese Platte. |
| `scripts/test-network.py:225,228` `disk init sata0` / `disk mount sata0` per Monitor | eine Testplatte = `sata0` | Eine zusätzliche Platte vor dieser verschiebt die Namen; `disk init` würde die falsche Platte treffen (gestoppt nur durch den 64-MiB-Größencheck). |
| `test-network.py:293-306` `os.ftruncate(…, 64 MiB)`, `.owned`-Marker, Größencheck 64 MiB | Platte exakt 64 MiB | Größere Platte wird abgelehnt (Schutz, gewollt). |
| `test-network.py:338-340` `-drive …testdisk … ich9-ahci … ide-hd bus=testahci.0` | eigener AHCI-Controller `testahci`, Port 0 | Die Platte ist die einzige am Controller; eine zweite Platte müsste ein eigenes `-drive`/`-device` bekommen, sonst wird sie nicht gesehen. |
| `scripts/test-wasm-doom-desktop.py:236,251,813` MACHINE: `ahci,id=ahci` + `ide-hd,drive=appdisk,bus=ahci.0`, `data.qcow2` aus `vvfat` | Datenplatte als qcow2 am AHCI-Port 0 | Alle `test-classicube/netsurf-desktop.py` (`:39-41`/`:47-49`, `install-local /data/pkgs/…`) hängen daran. `test-storage-ext2-guest.py` setzt die ext2-Platte auf Port 1 und erwartet, dass der Kernel die App-Platte als `sata0` bootet; wer `sata0` wird, hängt von der AHCI-Reihenfolge ab (nicht geprüft). |
| `scripts/test-wasm-loader.py:37-38` `fat:{disk}`, `snapshot=on`, `ahci.0` | read-only-vvfat-Platte als `appdisk` | wie oben, einzige Platte. |
| `scripts/create-full-fat32-test-copy.py`, `scripts/tests/test_full_fat32_copy.py` | arbeitet auf Image-Dateien, nicht auf Gastgeräten | – |

## 6. Tests mit Plattenannahmen (Host)

`ProjektZeh.Tests/InstallServiceTests.cs:19-35` (Mock-Platten `sata0/1/2`), `StorageShellCommandsTests.cs:14-61` (`disk init/mount sata0`, `unmount sata0` abgelehnt) und viele `FileService("/data")` in `DesktopTests.cs`/`FileManagerTests.cs` setzen `sata0` und `/data` als Konvention voraus; sie bleiben bei zusätzlichen Mounts grün, spiegeln aber nur den Ein-Platten-Fall.

## 7. Zusammenfassung der Brüche (für die Planung)

1. **Namens-/Mount-Konvention:** `sata0` ⇒ `/data` ist an Kernel (Boot), Shell, Hinweistexte und Testskripte verdrahtet; es gibt keinen Mountpunkt- oder Typparameter.
2. **`partitions[0]` mit `Count == 1`** in Storage-Service und Installer: Mehrpartitionsplatten werden abgelehnt, die einzige Partition wird immer als FAT32 behandelt.
3. **Ein Mountpunkt `/data`** wird von Datenplatte und Installer gemeinsam beansprucht.
4. **Fester Dateisystemname `zehos-fat`** und einmalige Registrierung: ext2 braucht eine zweite Registrierung (Cosmos erlaubt keine Doppelnamen, siehe Vertragsdokument).
5. **Herunterfahren** synchronisiert/hängt nur `/data` aus.
6. **Update-Ziel** `/data/boot/…` ist nur dann die EFI-Partition, wenn der Installer sie dort eingebunden hat; bei Datenplatte + Bootplatte nicht eindeutig.
7. **Testskripte:** 64-MiB-Größenprüfungen sind Schutzmechanismen und müssen für andere Größen bewusst erweitert werden; die AHCI-Portreihenfolge entscheidet über `sataN`.
