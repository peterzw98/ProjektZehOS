# Patches gegenüber Upstream

## Grundstand

| Repository | Commit | Entspricht | Eingespielt am |
| --- | --- | --- | --- |
| `https://github.com/CosmosOS/Cosmos` | `efcf20cc62884b042a0adadffe9fc4540491fd89` | NuGet-Pakete 3.0.97 | 18. November 2026 |

## Übernommene Upstream-Stände

Noch keine.

## Eigene Patches

| Kurzname | Commit | Dateien | Grund | Entfernter ZehOS-Umweg | Prüfung |
| --- | --- | --- | --- | --- | --- |
| ext2-remove-entry | Squash von #344 | `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Directory.cs` (`RemoveEntry`) | `RemoveEntry` trug den Vorgänger über Blockgrenzen, verschmolz dabei über die Blockgrenze (16-Bit-Überlauf von `rec_len`) und schrieb alle Blöcke zurück, auch bei Blockzeiger 0 (Block 0 des Geräts) | `CosmosExt2RemoveEntryPlug`, `Ext2DirectoryRemoval` (fällt in E2-Z1) | NUnit `Ext2RemoveEntryTests` (14 Fälle, vor dem Patch 7 rot, laut Review 6 weitere sind Regressionsfälle); Hostprobe `test-boundary.py` siehe PR |
| ext2-directory-limit | Squash von #345 | `Ext2Directory.cs` (`TryParseDirectory`, `AddEntry`), `Ext2InodeOperations.cs` (`Lookup`, `ReadDir`, `Rmdir`) | `MaxParseIters = 8192` zählte freie Records mit, ein Verzeichnis über 16 MiB oder 4096 Blöcken wurde als leer geliefert (`Rmdir` hielt es für leer), `AddEntry` kannte die Grenze nicht (unsichtbare Einträge). Folge: ohne Iterationsgrenze legt ein Parse bis zu ~1 Mio. `Ext2DirEntry` an (16 MiB, 12–16-Byte-Records); die Produktgrenze setzt ZehOS (65.536 Einträge) | Grenze 8189 Einträge in ZehOS (`FileService.MaximumExt2DirectoryEntries`, ändert E2-P7 in ZehOS) | NUnit `Ext2DirectoryLimitTests` (5 Fälle, 23/23 Ext2-Tests grün; `Lookup_OfEntryBeyondOldParseLimit_Finds` auf P1 rot); Gastlauf 20.000 Einträge folgt mit dem ZehOS-PR |
| ext2-formatter | Squash von #346 | `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Formatter.cs` (`Format`) | `Format` schrieb kein `sparse_super` und keine Backup-Superblöcke, rechnete Gruppenanfänge ohne `first_data_block` und ließ die Füllbits der Bitmaps frei (e2fsck-Fehler); eine letzte Gruppe ohne Platz für ihre Metadaten lehnte das ganze Format ab. **`Format(null)` und der Standard von `Ext2FormatOptions.BlockSize` sind jetzt 4096 (vorher 1024); `Format` mit 1024 liefert `false`, bis der Treiber `FirstDataBlock` beachtet (optionales Paket E2-P9 in Bahn B), weil ein korrektes 1-KiB-Volume sonst falsch beschrieben würde (`Ext2Superblock` ohne `FirstDataBlock`). 2048 ist nicht betroffen.** Die 1-KiB-Varianten der Smoketests bleiben als `[Ignore]` sichtbar | `CosmosExt2FormatterPlug`; die kopierte Formatter-Quelle `CosmosExt2Formatter.Format` (fällt im ZehOS-PR, `FormatForKernel` bleibt als dünne Hülle mit 4096-Byte- und 128-GiB-Grenze) | NUnit `Ext2FormatterTests` (8 Fälle, 5 davon vor dem Patch rot), Ext2 gesamt 10/10 grün; Hostprobe und Gast folgen im ZehOS-PR |
| lint1-nmi | Squash von #351 | `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs` | NMI erreicht den Kernel: `LocalApic.Initialize` richtet LINT1 unmaskiert mit Zustellart NMI ein statt es zu maskieren; LINT0 bleibt maskiert. LINT1 ist fest verdrahtet (PC-Konvention wie Linux), MADT-Einträge Typ 4 (Local APIC NMI) wertet Cosmos nicht aus. Folge: Ein externer NMI hält das System jetzt an, statt still verloren zu gehen. Mit SMP (E5) nur auf der BSP setzen, APs maskiert lassen (wie Linux `setup_local_APIC`) | Entmaskierung von LINT1 in `CosmosIsolationProbe` (entfällt mit ZehOS E3-C) | ZehOS E3-C: `test-isolation-probe.py --nmi` (KVM); Desktop TCG und KVM ohne unerwarteten NMI |
