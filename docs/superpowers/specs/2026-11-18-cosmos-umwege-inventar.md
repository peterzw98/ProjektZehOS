# Inventar: ZehOS-Umwege um Cosmos 3.0.97

Stand 18. November 2026, ZehOS master `5c5538d`, Cosmos-Quelle zum Paketcommit `efcf20c`. Anlass und Einordnung:
[Multicore-Entwurf](2026-11-18-multicore-design.md), Abschnitt 8. Reine Bestandsaufnahme, kein Arbeitsauftrag.

ZehOS-Pfade sind relativ zu `ProjektZeh/`, Cosmos-Pfade relativ zu `src/` im Cosmos-Repository.

Spalte „Ursache“:

- **Q✓**: in der Cosmos-Quelle `efcf20c` nachgesehen und bestätigt.
- **Z**: nur aus ZehOS-Kommentaren oder -Doku, in der Quelle nicht geprüft.
- **Q≠**: die Quelle sieht heute anders aus als der ZehOS-Kommentar.

Korrekturgröße in Cosmos: K klein, M mittel, G groß. „Entfällt?“: ob der Umweg nach der Korrektur ganz wegfallen kann.
Die Recherche stammt von einem Unteragenten; gegen die Quelle selbst nachgeprüft sind die Zeilen zu `InitializeStack`,
`CCBuildTask`, `BuildCommand` und dem Paketbau. Keiner der Befunde ist durch einen Gastlauf mit geändertem Cosmos belegt.

## Plugs, Attrappen, Zugriffe auf interne Member

| ZehOS-Datei:Zeile | Umweg | Cosmos-Ursache | Korrektur | Entfällt? |
| --- | --- | --- | --- | --- |
| `Input/CosmosKeyboardPlug.cs:6-29` | Plug auf `KeyboardManager.HandleScanCode` leitet alle Make- und Break-Codes in einen eigenen Puffer | Q✓ `Cosmos.Kernel.System/Input/KeyboardManager.cs:239-294`: nur `!released` wird eingereiht, Break-Codes gehen verloren | M: öffentliches Rohereignis | ja |
| `Input/CosmosInputService.cs:78-81` | `UnsafeAccessor` auf `KeyboardLayout.ConvertScanCode` | Q✓ `Input/KeyboardLayout.cs:68` ist `internal` | K | ja |
| `Input/GermanKeyboard.cs:7-9` | korrigiert ISO-Tasten 43/86 und CapsLock-Spalten | Q✓ `Input/Layouts/DEStandardLayout.cs:68`: 0x2B falsch belegt, 0x56 fehlt; CapsLock Z | K | ja |
| `Input/CosmosMousePlug.cs:9-56` | Plug auf `MouseManager.HandleRelative` puffert Rohdeltas; sechs `UnsafeAccessor` auf private Felder | Q✓ `Input/MouseManager.cs:194-226`: begrenzt auf den Bildschirm, bevor Deltas lesbar sind | M | ja |
| `Input/CosmosUsbTabletDriver.cs:11-22` | eigener QEMU-Tablet-Treiber | Q✓ `MouseManager.cs:240-245`: `HandleAbsolute` übernimmt nur Tasten | M | teilweise |
| `Runtime/CosmosVmwareClockPlug.cs:1-128`, `ThirdParty/CosmosTiming` | Plugs auf `LocalApic.CalibrateTimer` und `X64CpuOps`; Frequenzen unter VMware aus CPUID 0x40000010 | Q✓ `Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs:280-320` nur PIT, `X64CpuOps.cs:40-63` TSC nur über LAPIC; Fehlwerte Z | K–M | ja, samt Kopie |
| `Runtime/CosmosFaultIsolation.cs:11-30, 177-189` | Plug auf `Panic.HaltCpu` und `PrintCpuContext`; `UnsafeAccessor` auf `ScheduleFromInterrupt` und `InternalCpu.Halt` | Q✓ `Cosmos.Kernel/Panic.cs:19-36, 167-174`: jede CPU-Ausnahme endet im Halt, kein Hook | M: öffentlicher Ausnahme-Hook | erledigt mit E3-A/E3-B (Patch `ausnahme-hook`, 3.0.97-zehos.2) |
| `Runtime/CosmosIrqContextStub.cs:1-10` | leere öffentliche Attrappe `IRQContext`, Versätze 120/152 fest | Q✓ `Core/CPU/IRQContext.cs:16`: `internal struct`; Patcher vergleicht Typen nur über den Namen | K | erledigt mit E3-B (Hook bekommt `CpuExceptionInfo`, `IRQContext` bleibt intern) |
| `Storage/CosmosExt2PlugTypes.cs` | Attrappen `Ext2Superblock`/`Ext2Inode` | Q✓ interne Typen | mit den ext2-Korrekturen | ja |
| `Storage/CosmosExt2RemoveEntryPlug.cs:9-67` | Records nur innerhalb eines Blocks verschmelzen | Q✓ `FileSystem/Ext2/Ext2Directory.cs:302, 340-345` | K | ja |
| `Storage/CosmosExt2RmdirPlug.cs:10-103` | gibt alle Blöcke frei | Q✓ `Ext2/Ext2InodeOperations.cs:472-482`: nur `Block[0]` | K | ja |
| `Storage/CosmosExt2TruncatePlug.cs:8-130` | gibt auch geleerte Zeigerblöcke frei | Q✓ `Ext2/Ext2Superblock.cs:1043-1049`; dreifach indirekt liefert 0 (`:1014`) | M | ja |
| `Storage/CosmosExt2FreeInodePlug.cs:8-91` | schreibt Inode samt dtime vor dem Bitmapbit | Q✓ `Ext2/Ext2Superblock.cs:617-632` | K | ja |
~~| `Storage/CosmosExt2FormatterPlug.cs:8-17`, `Storage/Ext2/CosmosExt2Formatter.cs` | eigener Formatter | Q✓ `Ext2/Ext2Formatter.cs:185`: keine Backup-Superblöcke; Geometrie Z | M | ja, samt Kopie |~~ erledigt am 18. November 2026: Patch `ext2-formatter` (Plug und Kopie entfernt, `FormatForKernel` bleibt als ZehOS-Profil).
| `Storage/Ext2/Ext2FeatureGate.cs` | prüft Feature-Bits vor dem Mount | Q✓ `Ext2/Ext2Superblock.cs:154-156` liest sie nur | M | größtenteils |
| `Graphics/DisplayService.cs:120-122` | `UnsafeAccessor` auf `Canvas._display` für Teilrechtecke | Q✓ `Graphics/Canvas.cs:65` privat | K | ja |
| `Runtime/CosmosSystemInfo.cs:30-37` | `UnsafeAccessor` auf `PageAllocator` | Q✓ intern, aber `Diagnostics/MemoryDiagnostics.cs:19` ist öffentlich | keine | ja, schon heute |
| `Update/CosmosHttpUpdateSource.cs:94-109` | setzt `TcpConnection.s_nextPort` zufällig | Q✓ `Network/Protocols/Tcp/TcpConnection.cs:39`: fester Start nach jedem Boot | K | ja |
| `Security/CosmosSaltSource.cs:18-23` | `DllImport` auf RDSEED/RDRAND | Q✓ `Core/Security/HardwareRandom.cs:48` intern | K | wahrscheinlich |
| `Security/CosmosVirtioEntropyDriver.cs:9-70` | eigener virtio-rng-Treiber | Q✓ in `Cosmos.Kernel.Drivers/Virtio/` fehlt rng | M | verschiebbar |
| `Scheduling/CosmosStackProbe.cs`, `ThreadStarter.cs:8-58` | Probe-Allokation vor jedem Threadstart | Q✓ `Core/Scheduler/SchedulerThread.cs:163-188`: `MemoryOp.Alloc` ungeprüft | K | ja |

## Scheduler, Speicher, Threads

| ZehOS-Datei:Zeile | Umweg | Cosmos-Ursache | Korrektur | Entfällt? |
| --- | --- | --- | --- | --- |
| `Scheduling/RoundRobinCore.cs:10-13, 42` | Idle-Thread wird bei jedem Tick verdrängt | Q✓ `Core/Scheduler/ConditionVariable.cs:61-64, 170`: Idle-Thread wartet per Halt; der Boot-Thread ist der Idle-Thread | G | ja |
| `Scheduling/BackgroundTaskService.cs:26-30`, `DedicatedWorker.cs:10`, `Runtime/Wasm/WasmRuntime.cs:134-140` | langlebige Worker, beim Boot angelegt | Q✓ Threadstacks werden nie freigegeben | M | Zwang entfällt |
| `Shared/BufferAllocation.cs:17-20`, `WasmRuntime.cs:312-315` | vorher `GC.Collect` | Q✓ `Core/Memory/GarbageCollector/GarbageCollector.cs:527-539`: sammelt nur bei Fehlschlag; `Heap.cs:119-139` ohne Sammlung | M–G | teilweise |
| `Native/Wasm/libc/libc.c:77-78` | `malloc` lehnt über 4 GiB selbst ab | Q✓ `Core/Bridge/Export/HeapNative.cs:14-17`: `(uint)size` | K | ja |
| `#pragma warning disable COSMOS0001` (Scheduler, `Kernel.cs:519-531`, Eingabe, Proben) | experimentelle API | Q✓ `Core/Scheduler/Experimentals.cs:19`: gewollt | K | kosmetisch |
| `Storage/StorageLock.cs:10-28` und rund 60 `*StorageLocked`-Methoden | globale Sperre um jeden Plattenzugriff | Q✓ keine Sperre in `FileSystem/Ext2`, `Fat`, `VfsManager` außer `s_mountLock` | G | Sperre ja |
| `Runtime/CosmosIsolationProbe.cs:40-44` (nur Probe) | entmaskiert LINT1 | Q✓ `Core.X64/Cpu/LocalApic.cs:172-174` | K | ja |

## Dateisystem und VFS

| ZehOS-Datei:Zeile | Umweg | Cosmos-Ursache | Korrektur | Entfällt? |
| --- | --- | --- | --- | --- |
| `Storage/FileService.cs:18-20`, `CosmosDirectoryEntryLimit.cs` | höchstens 8189 Einträge je ext2-Verzeichnis | Q✓ `Ext2/Ext2Directory.cs:19, 65`: `MaxParseIters = 8192` | K–M | ja |
| `Storage/CosmosReadOnlyVfs.cs:7-35`, `Migration/MigrationSourceSwap.cs:6-12` | Lese-Wrapper | Q✓ `Fat/FatFileOperations.cs:201-204`, `Ext2/Ext2FileOperations.cs:243-260`: `Release` ruft immer `Fsync` | K–M | weitgehend |
| `Storage/CosmosReadOnlyVfs.cs:107-110` | feste Registrierung mit austauschbarem Inneren | Q✓ `FileSystem/VfsManager.cs:61`: kein `Unregister` | K | ja |
| `Storage/CosmosMigrationService.cs:63-66` | durchsucht die Mounttabelle selbst | Q✓ `VfsManager.cs:334` privat | K | ja |
| `Security/PathGuard.cs:17-20, 52` | prüft jede Pfadkomponente, lehnt Symlinks ab | Q✓ `VfsManager.cs:508, 671` privat; `LStat` = `Stat` Z | M | teilweise |
| `Storage/Partitioning/GptLayout.cs:108-110`, `GptLayoutWriter.cs:9-12` | eigener GPT-Leser und -Schreiber | Q✓ `Storage/Gpt.cs:73, 296, 307`: CRC 0, `Parse` prüft nicht | M | ja |
| `Storage/FileService.cs:419` | Stream bei ENOSPC explizit schließen | Z (Stand 3.0.94) | offen | neu prüfen |
| `Storage/FileService.cs:661` | `touch` ohne Zeitstempel | Q✓ `Plugs/System/IO/InteropSysFilePlug.cs:231-234`: wirkungslos | M | ja |
| Installer: ESP mit 4-KiB-Clustern | FAT32 mit 16-KiB-Clustern scheitert | Z | offen | offen |
| ext2-Inode-Cache (nur dokumentierte Grenze) | keiner | Q✓ `Ext2/Ext2Superblock.cs:20, 759`: unbegrenztes Dictionary | M | ja |

## Netz, Zeit, Ausgabe

| ZehOS-Datei:Zeile | Umweg | Cosmos-Ursache | Korrektur | Entfällt? |
| --- | --- | --- | --- | --- |
| `SystemServices/LocalTime.cs:7-9` | feste Zeitzone Europe/Berlin | Z | M | ja |
| `Runtime/Wasm/CosmosWasmNetwork.cs:429-430` | Fehlerabbildung auf EIO | Z (3.0.94) | M | teilweise |
| `CosmosNetworkService.Exchange` | kein `close_notify` | Q≠ `Plugs/System/Net/Sockets/SocketPlug.cs:253`: `SelectWrite` jetzt auch bei `CLOSE_WAIT` | keine | eventuell schon heute |
| DHCP- und Connect-Wartephasen | kein Abbruch | Q✓ kein `CancellationToken` in `Network/DhcpClient.cs` | M | ja |
| `scripts/test-wasm-doom-desktop.py:281` | überspringt eingeschobene Fremdzeilen | Q≠ `Core/IO/Serial.cs:109-134`: atomar je Aufruf, nicht je Zeile; feste Debugausgaben bleiben | K | teilweise |

## MSBuild und Werkzeuge

| ZehOS-Datei:Zeile | Umweg | Cosmos-Ursache | Korrektur | Entfällt? |
| --- | --- | --- | --- | --- |
| `ProjektZeh.csproj:1` (`TreatAsLocalProperty`) | lokale `DefineConstants` erhalten | Q✓ `Cosmos.Tools/Commands/BuildCommand.cs:289` setzt sie global | K | ja |
| `ProjektZeh.csproj` (`InvalidateStaleCObjects`) | Stempel leert `cobj/` | Q✓ `Cosmos.Build.CC/Tasks/CCBuildTask.cs:73-90`: Objektname nur aus dem Inhalt der `.c`-Datei | K | ja |
| `ProjektZeh.csproj` (`MapNativeSourcePaths`) | `-ffile-prefix-map` für C | Q✓ fehlt in `Cosmos.Build.CC/build/Cosmos.Build.CC.targets` | K | ja |
| `scripts/check-release-paths.py:11-26` | feste Upstream-Pfade erlaubt | Q✓ vorgebaute DLLs mit `[CallerFilePath]` | K: eigener Build mit `PathMap` | ja |
| `scripts/check-plug-binding.py` | prüft im Log, dass jeder Plug gebunden hat | Q✓ `Cosmos.Patcher/Resolution/MethodResolver.cs:102`: fehlendes Ziel nur Warnung | K | ja |
