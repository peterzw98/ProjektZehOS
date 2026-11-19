# ext2-addentry-guard

- **Commit:** Squash von #362
- **Dateien:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Directory.cs` (`AddEntry`); Tests in `tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/Ext2RmdirTests.cs`
- **Grund:** `AddEntry` las das Verzeichnis bis zum ersten Loch, schrieb danach aber alle Blöcke zurück; bei einem Loch innerhalb `i_size` ging das als `WriteBlocks(0, …)` auf Block 0 (bei 4-KiB-Blöcken liegt dort der primäre Superblock). Außerdem prüfen `Create`, `Mkdir`, `Symlink` und `Rename` die Existenz über `TryFind`, das seit `ext2-rmdir` bei jedem defekten Verzeichnis „nicht vorhanden“ liefert; `AddEntry` legte dann ein Duplikat an. Jetzt prüft `AddEntry` das Verzeichnis vorab mit `TryParseDirectory` und schreibt nie einen Block 0 (Befund Ada Lovelace zu #355, nach dessen Squash)
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** NUnit `Ext2RmdirTests.Create_InDirectoryWithHole_IsRefusedAndBlockZeroUntouched` (Block 0 vor und nach dem Aufruf gleich) und `Create_ExistingNameInCorruptDirectory_IsRefused`; ohne den Fix beide rot
- **Auf SMP-Stand (`feat/smp-phase5b@4495213`):** ohne Konflikt übernommen (3-Wege-Anwendung), keine Upstream-Änderung an den Patchdateien
