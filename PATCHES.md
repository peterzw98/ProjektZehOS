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
| ausnahme-hook | Squash von #349 | `src/Cosmos.Kernel.Core/CPU/CpuExceptionHook.cs` (neu), `src/Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs` (`TryAbandonCurrentFromInterrupt`), `src/Cosmos.Kernel/Panic.cs` (`CpuException`), `src/Cosmos.Kernel.Core/PublicAPI.Unshipped.txt`, `Scheduler/Experimentals.cs`, `docs/articles/dev/public-api.md` | Öffentlicher, experimenteller Hook (`COSMOS0001`) für CPU-Ausnahmen: der Kernel kann den fehlerhaften Thread aufgeben statt anzuhalten; Schachtelungszähler, ohne Hook unverändert Halt. Der Hook bekommt `CpuExceptionInfo` (Vektor, IP, SP, Fehleradresse) statt `IRQContext`, weil dessen Layout je Architektur verschieden ist und eine öffentliche Fassung die gemeinsame `PublicAPI`-Datei des Mehrarchitekturpakets bricht | `CosmosPanicHaltPlug`, `CosmosIrqContextStub`, Feldversätze, `UnsafeAccessor` `ScheduleFromInterrupt`/`Halt`, `DllImport` `_native_get_context_switch_sp` (entfallen mit E3-B) | ZehOS E3-B: `test-isolation-probe.py` positiv (KVM 10, TCG 3), negativ, NMI; Desktop, Doom quick |
