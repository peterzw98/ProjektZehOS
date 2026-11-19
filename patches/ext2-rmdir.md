# ext2-rmdir

- **Commit:** Squash von #355
- **Dateien:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2InodeOperations.cs` (`Rmdir`), `Ext2Directory.cs` (`TryParseDirectory`); Tests `tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/Ext2RmdirTests.cs`
- **Grund:** `Rmdir` gab nur `Block[0]` frei (weitere Verzeichnisblöcke und Zeigerblöcke blieben belegt), setzte `LinksCount = 0` ohne das Inode zu schreiben und lehnte `.` nicht ab (hätte den Punkteintrag entfernt). Jetzt: `.` und `..` vorab abgelehnt; `Truncate(target, 0)`, `WriteInode`, dann `FreeInode` (braucht `ext2-truncate`, `ext2-free-inode`). `TryParseDirectory` lieferte bei Blockloch innerhalb `i_size`, defektem Record oder Record über eine Blockgrenze eine Teilliste mit `true`, `Rmdir` hielt ein nicht leeres Verzeichnis dann für leer (Befund B zu #345); jetzt `false` (Lead-Entscheid: Fix in diesem Patch)
- **Entfernter ZehOS-Umweg:** `CosmosExt2RmdirPlug`, `CosmosExt2PlugTypes` (fällt in E2-Z1)
- **Prüfung:** NUnit `Ext2RmdirTests` (8 Fälle, Eltern-Links und `UsedDirsCount` nach Remount; vor dem Patch 6 rot, `..` und „nicht leer“ sind Regressionsfälle)
- **Auf SMP-Stand (`feat/smp-phase5b@4495213`):** ohne Konflikt übernommen (3-Wege-Anwendung), keine Upstream-Änderung an den Patchdateien
