# ext2-formatter

- **Commit:** Squash von #346
- **Dateien:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Formatter.cs` (`Format`), `Ext2FormatOptions.cs` (Standard `BlockSize` 4096); Tests `tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/Ext2FormatterTests.cs`, `Ext2SmokeTest.cs` (1-KiB-Fälle `[Ignore]`), Testvolumes auf 2048 in `Ext2DirectoryLimitTests.cs` und `Ext2RemoveEntryTests.cs`
- **Grund:** `Format` schrieb kein `sparse_super` und keine Backup-Superblöcke, rechnete Gruppenanfänge ohne `first_data_block` und ließ die Füllbits der Bitmaps frei (e2fsck-Fehler); eine letzte Gruppe ohne Platz für ihre Metadaten lehnte das ganze Format ab. **`Format(null)` und der Standard von `Ext2FormatOptions.BlockSize` sind jetzt 4096 (vorher 1024); `Format` mit 1024 liefert `false`, bis der Treiber `FirstDataBlock` beachtet (optionales Paket E2-P9 in Bahn B), weil ein korrektes 1-KiB-Volume sonst falsch beschrieben würde (`Ext2Superblock` ohne `FirstDataBlock`). 2048 ist nicht betroffen.** Die 1-KiB-Varianten der Smoketests bleiben als `[Ignore]` sichtbar
- **Entfernter ZehOS-Umweg:** `CosmosExt2FormatterPlug`; die kopierte Formatter-Quelle `CosmosExt2Formatter.Format` (fällt im ZehOS-PR, `FormatForKernel` bleibt als dünne Hülle mit 4096-Byte- und 128-GiB-Grenze)
- **Prüfung:** NUnit `Ext2FormatterTests` (8 Fälle, 5 davon vor dem Patch rot), Ext2 gesamt 10/10 grün; Hostprobe und Gast folgen im ZehOS-PR
- **Auf SMP-Stand (`feat/smp-phase5b@4495213`):** ohne Konflikt übernommen (3-Wege-Anwendung), keine Upstream-Änderung an den Patchdateien
