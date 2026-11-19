# ext2-directory-limit

- **Commit:** Squash von #345
- **Dateien:** `Ext2Directory.cs` (`TryParseDirectory`, `AddEntry`), `Ext2InodeOperations.cs` (`Lookup`, `ReadDir`, `Rmdir`); Tests `tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/Ext2DirectoryLimitTests.cs`
- **Grund:** `MaxParseIters = 8192` zählte freie Records mit, ein Verzeichnis über 16 MiB oder 4096 Blöcken wurde als leer geliefert (`Rmdir` hielt es für leer), `AddEntry` kannte die Grenze nicht (unsichtbare Einträge). Folge: ohne Iterationsgrenze legt ein Parse bis zu ~1 Mio. `Ext2DirEntry` an (16 MiB, 12–16-Byte-Records); die Produktgrenze setzt ZehOS (65.536 Einträge)
- **Entfernter ZehOS-Umweg:** Grenze 8189 Einträge in ZehOS (`FileService.MaximumExt2DirectoryEntries`, ändert E2-P7 in ZehOS)
- **Prüfung:** NUnit `Ext2DirectoryLimitTests` (5 Fälle, 23/23 Ext2-Tests grün; `Lookup_OfEntryBeyondOldParseLimit_Finds` auf P1 rot); Gastlauf 20.000 Einträge folgt mit dem ZehOS-PR
- **Auf SMP-Stand (`feat/smp-phase5b@4495213`):** ohne Konflikt übernommen (3-Wege-Anwendung), keine Upstream-Änderung an den Patchdateien
