# Bestandsaufnahme: virtuelle Systemdateien

Stand: 17. November 2026. Grundlage für [Etappe 5 des Storage-Plans](../plans/2026-11-17-storage-linux.md#etappe-5-cosmos-systemdaten-als-virtuelle-dateien), keine Implementierung oder Aktivierung. Die unten genannten Projektdateien wurden gegen `d5e56765d9aa696afdfece8e6004882988372ea6` verglichen: kein Unterschied zum untersuchten Stand. API-Verfügbarkeit wurde zusätzlich direkt im installierten Paket `Cosmos.Kernel.System` **3.0.97**, Assembly `runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.System.dll`, mit ILSpy geprüft. Das sind Codebefunde, keine erfolgreichen Host-, Build- oder Gasttests.

## Verfügbarkeitsmatrix

Vorhandene Dienste bleiben die Datenquelle. Aktualisierung bedeutet hier den vorgeschlagenen Zeitpunkt eines neuen Snapshots; die Dateien existieren noch nicht. Die verbindlichen Knotenzugriffsrechte werden in Etappen 3/5 geklärt, ohne eine zweite Rechteverwaltung einzuführen. Daraus folgt keine Erweiterung der Wasm-Rechte.

| Vorgesehener Pfad | Belegte Quelle / Eigentümer | Aktualisierung und Grenzen | Fehlende Werte / auszuschließende Inhalte |
| --- | --- | --- | --- |
| `/proc/version` | [Kernel.cs](../../../ProjektZeh/Kernel.cs), Zeilen 32–37 und 121–122: ZehOS-/Cosmos-Assemblyversion | Unveränderliche Versionsmetadaten; Snapshot beim Öffnen | Keine Host-, Buildpfade, Benutzer- oder Rechnernamen |
| `/proc/uptime` | [Kernel.cs](../../../ProjektZeh/Kernel.cs), Zeilen 29 und 352–360: vorhandene Stoppuhr; `TimerManager.IsInitialized` | Ein Wert beim Öffnen; fehlende Timerinitialisierung kenntlich machen | Kein belegter Idle-Zähler; kein erfundenes zweites Linux-uptime-Feld |
| `/proc/meminfo` | [CosmosSystemInfo.cs](../../../ProjektZeh/Runtime/CosmosSystemInfo.cs), Zeilen 19–37 und 51–54; öffentliche `MemoryDiagnostics`-Zähler in 3.0.97 | Zusammengehörige Zähler einmal lesen; `free <= total` prüfen; Größen und Einheiten ausdrücklich benennen | Keine belegten Swap-/Cache-/Bufferzähler; keine Adressen. Bestehender privater `UnsafeAccessor` ist für die öffentlichen 3.0.97-Zähler nicht nötig |
| `/proc/mounts` | `VfsManager.Mounts` und `VfsMount.Name/Source/MountPoint/Flags/IsReadOnly` in 3.0.97 | `Mounts` einmal lesen; Cosmos ersetzt das zugrunde liegende Array bei Mountänderungen. Keine bestehende Ausgabeobergrenze | Quellzeichenketten nicht ungeprüft übernehmen; Feldtrennung und Steuerzeichen maskieren, keine beliebigen Pfade aus Diagnosen hinzufügen |
| `/proc/log` | [SystemLog.cs](../../../ProjektZeh/SystemServices/SystemLog.cs), Zeilen 65–94; [SystemLogEntry.cs](../../../ProjektZeh/SystemServices/SystemLogEntry.cs), Zeilen 10–13; [Kernel.cs](../../../ProjektZeh/Kernel.cs), Zeile 65: Ring mit 128 Einträgen | Vorhandenen chronologischen Snapshot verwenden. Anzahl begrenzt, Nachrichtenlänge bisher unbegrenzt | Ausschließlich Allowlist-Projektion aus Zeitstempel, Level und fest zugeordneter sicherer Kategorie. Keine Freitextnachrichten, MACs, Befehle, Pfade, Zeiger oder Exceptiondetails |
| `/sys/kernel/components` | `KernelFeatures`; `DriverDiagnostics.DriverCount/TryGetDriver` in 3.0.97 | Feste Featureliste und begrenzter Treibersnapshot beim Öffnen | Buildflags von initialisierten Diensten und tatsächlich funktionierender Hardware unterscheiden; keine erfundene Modul-/Komponentenregistrierung |
| `/sys/devices` | `DriverDiagnostics.NodeCount/DeviceCount/TryGetNode/TryGetDevice` in 3.0.97 | Werte-Snapshots je Aufruf; Gesamtaufzählung nicht als atomar belegt. Anzahl und Textfelder zusätzlich begrenzen | Kein `LastFault`, keine Offer-`Reason`, keine Rohadressen, beliebige Fehlertexte oder Ressourceninhalte |
| `/sys/block` | [CosmosStorageService.cs](../../../ProjektZeh/Storage/CosmosStorageService.cs), Zeilen 27–40; [StorageDeviceInfo.cs](../../../ProjektZeh/Storage/StorageDeviceInfo.cs), Zeile 6: Name, Größe, Partitionsanzahl | Bestehende Metadatenprojektion wiederverwenden; keine Sektorzugriffe. Cosmos 3.0.97 registriert höchstens 8 Geräte; die Projektion setzt keine zusätzliche Grenze. Größenmultiplikation derzeit ungeprüft | Keine belegten Linux-major/minor-, Queue-, Seriennummern- oder Geräte-Dateiverträge; nichts erfinden |

Das Rohprotokoll ist **nicht bereits bereinigt**: [Kernel.cs](../../../ProjektZeh/Kernel.cs), Zeile 220 protokolliert MAC-Adressen, Zeile 141 Storage-Befehlstext; [BootService.cs](../../../ProjektZeh/SystemServices/BootService.cs), Zeile 36 protokolliert `exception.ToString()`. Nachträgliche Regex-Ersetzung wäre kein belastbarer Nachweis für sichere beliebige Nachrichten.

## Exakte Cosmos-3.0.97-Typen

Für diese Assemblybefunde liegen hier keine Cosmos-Quelldateien mit verifizierten Zeilennummern vor. Typ und Mitglied sind deshalb die Evidenzstelle; neuere Online-APIs werden nicht vorausgesetzt.

| Vollständiger Typ | Belegte öffentliche API |
| --- | --- |
| `Cosmos.Kernel.System.KernelFeatures` | `Interrupts`, `Uart`, `Pci`, `Timer`, `Keyboard`, `Mouse`, `Network`, `Scheduler`, `Graphics`, `Storage`, `Fat`, `Usb`, `Audio`, `FinalizerThread`: kompilierte Features |
| `Cosmos.Kernel.System.Diagnostics.MemoryDiagnostics` | `PageSizeBytes`, `TotalPages`, `FreePages`, `RamSizeBytes`, `GcTimePercent`, `TotalCollections`, `TotalObjectsFreed`. `Collect()` verändert Zustand und gehört nicht in einen Dateilesezugriff |
| `Cosmos.Kernel.System.Diagnostics.DriverDiagnostics` | `IsStarted`, `HasWorker`, `DriverCount`, `NodeCount`, `DeviceCount`, `GetTotalHeldResourceCount()`, `TryGetDriver`, `TryGetNode`, `TryGetOffer`, `TryGetDevice` |
| `Cosmos.Kernel.System.Diagnostics.DriverInfo` | Unveränderlicher Werte-Snapshot: `Name`, `Priority` |
| `Cosmos.Kernel.System.Diagnostics.DeviceNodeInfo` | Unveränderlicher Werte-Snapshot: `Path`, `BusName`, `Description`, `DriverName`, `State`, `ParentPath`, Ressourcen-/Interrupt-/Offer-/Kind-/Fehlerzähler, `LastFault` |
| `Cosmos.Kernel.System.Diagnostics.DeviceOfferInfo` | Unveränderlicher Werte-Snapshot: Treibername, Priorität, Spezifität, Ergebnis, Grund und Zahl freigegebener Ressourcen |
| `Cosmos.Kernel.System.Diagnostics.PublishedDeviceInfo` | Unveränderlicher Werte-Snapshot: `Kind`, `Name`, `NodePath`, `DriverName`, `IsConsumed`, `IsWithdrawn` |

Zugehörige Enums: `Cosmos.Kernel.System.Diagnostics.DeviceNodeState`, `DeviceOfferOutcome` und `PublishedDeviceKind` im selben Namespace.

## Tatsächlich verfügbare Dienstzustände

| Vollständiger Eigentümer | Öffentliche Zustände und Aussagegrenze |
| --- | --- |
| `Cosmos.Kernel.System.Timers.TimerManager` | `IsEnabled`; `IsInitialized` bedeutet `TickSource != null` |
| `Cosmos.Kernel.System.Storage.StorageManager` | `IsEnabled`; `IsInitialized` bedeutet vorhandene Gerätecollection; `Devices`/`Partitions` liefern vorher leere Arrays |
| `Cosmos.Kernel.System.Network.NetworkManager` | `IsEnabled`, `IsInitialized`, `DeviceCount`, `Ready`, `LinkUp`; Initialisierung betrifft die Collection, Ready/LinkUp das primäre Gerät |
| `Cosmos.Kernel.System.Graphics.DisplayManager` | `IsEnabled`, `Count`, `Primary`; kein `IsInitialized` gefunden |
| `Cosmos.Kernel.System.Audio.AudioManager` | `IsEnabled`, `Count`, `Primary`; kein `IsInitialized` gefunden |
| `Cosmos.Kernel.System.Input.KeyboardManager` / `MouseManager` | Jeweils `IsEnabled`; keine Initialisierungs-/Bereitschaftseigenschaft gefunden |

`DriverDiagnostics.IsStarted/HasWorker` beschreiben die Treiberengine, keinen Funktionsnachweis einzelner Geräte.

## Lesevertrag und wiederverwendbare Bausteine

- [FileService.cs](../../../ProjektZeh/Storage/FileService.cs), Zeilen 90–140 liest Text bereits bis EOF, unabhängig von `Stream.Length`. Standardgrenze: **65.536 Zeichen** (Zeile 15). Die Schleife liest höchstens Grenze plus ein Zeichen und meldet Überschreitung als Fehler, statt still abzuschneiden. Tests für kurze Reads/EOF, exakte Grenze und Überschreitung stehen in [FileServiceTests.cs](../../../ProjektZeh.Tests/FileServiceTests.cs), Zeilen 230–245; hier nicht ausgeführt.
- `ReadBytes` bleibt dagegen längenbasiert ([FileService.cs](../../../ProjektZeh/Storage/FileService.cs), Zeilen 143–157): Bei als null gemeldeter Dateilänge entsteht ein leeres Ergebnis. Dieser Vertrag deckt solche Pseudodateien nicht ab.
- Vorgeschlagene **Produktionsgrenzen**: pro Öffnung stabiler Snapshot mit höchstens **64 KiB UTF-8**, **128 Datensätzen**, **256 Byte je Textfeld**. Das sind Entwurfsentscheidungen, keine vorhandenen Garantien. Producer müssen vor unbegrenzter Allokation begrenzen; die Lesergrenze genügt dafür nicht. Maskierung ungültiger Unicode-/Steuerzeichen muss in die Grenzen eingehen; nicht darstellbare oder zu große Snapshots liefern einen begrenzten Fehler, keine stille Kürzung.
- Cosmos stellt `IVfsFileSystemType`, `IVfsInode`, `IInodeOperations`, `IFileOperations` und `IVfsOpenFile` bereit. `VfsFileHandle.Read` erhöht die Position selbst; eine Backend-Read-Methode darf sie nicht zusätzlich erhöhen. Ein separat untersuchter ext2-Adapter gehört noch nicht zum genannten Quellstand und ist deshalb keine vorausgesetzte Grundlage.
- Im untersuchten Produktionscode und der 3.0.97-Systemassembly wurde kein fertiges virtuelles `/proc`-/`/sys`-Backend gefunden. Neue Ausgabefelder, Dateinamen und Fehlerdarstellung brauchen noch den Vertrag aus Etappe 5.
- Optionales `/usr/src` darf ausschließlich absichtlich ausgelieferte Quellartefakte sichtbar machen. Vorhandene Repository-Quellen belegen keine Auslieferung im ISO. Keine Hostverzeichnisse, Werkzeuge, Buildzwischenstände oder Zugangsdaten als Backing-Tree verwenden.

Offen bleiben Rechtepolitik, Implementierung, Producer-/VFS-Tests, nativer Build sowie QEMU-/VMware-Prüfung. Diese Bestandsaufnahme belegt ausschließlich Quellen und APIs.
