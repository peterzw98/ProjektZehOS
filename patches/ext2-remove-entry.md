# ext2-remove-entry

- **Commit:** Squash von #344
- **Dateien:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Directory.cs` (`RemoveEntry`); Tests `tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/Ext2RemoveEntryTests.cs`
- **Grund:** `RemoveEntry` trug den Vorgänger über Blockgrenzen, verschmolz dabei über die Blockgrenze (16-Bit-Überlauf von `rec_len`) und schrieb alle Blöcke zurück, auch bei Blockzeiger 0 (Block 0 des Geräts)
- **Entfernter ZehOS-Umweg:** `CosmosExt2RemoveEntryPlug`, `Ext2DirectoryRemoval` (fällt in E2-Z1)
- **Prüfung:** NUnit `Ext2RemoveEntryTests` (14 Fälle, vor dem Patch 7 rot, laut Review 6 weitere sind Regressionsfälle); Hostprobe `test-boundary.py` siehe PR
- **Auf SMP-Stand (`feat/smp-phase5b@4495213`):** ohne Konflikt übernommen (3-Wege-Anwendung), keine Upstream-Änderung an den Patchdateien
