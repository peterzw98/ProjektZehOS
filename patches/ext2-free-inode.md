# ext2-free-inode

- **Commit:** Squash von #354
- **Dateien:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Superblock.cs` (`FreeInode`); Tests `tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/Ext2FreeInodeTests.cs`
- **Grund:** `FreeInode` löschte nur das Bitmapbit und erhöhte die Zähler; das Inode blieb auf der Platte mit `links_count` > 0 und `dtime` 0 (e2fsck: „Deleted inode … has zero dtime“). Ein schon freies Inode erhöhte die Freizähler ein zweites Mal. Jetzt: schon frei → nichts schreiben; sonst zuerst `links_count = 0` und `dtime = max(jetzt, 1)` direkt in den Tabellenblock (nicht über `WriteInode`, das den Cache wieder füllt), danach Bitmap und Zähler. Ein noch gehaltenes Inode-Objekt bekommt `links_count 0` und `dtime` mit, damit ein späteres `WriteInode` (offene, gelöschte Datei) `dtime` nicht wieder auf 0 setzt
- **Entfernter ZehOS-Umweg:** `CosmosExt2FreeInodePlug`, `Ext2InodeRelease` (fällt in E2-Z1)
- **Prüfung:** NUnit `Ext2FreeInodeTests` (6 Fälle am Treiber, Plattenzustand und Gruppendeskriptor über einen neuen Mount gelesen; vor dem Patch alle 6 rot)
- **Auf SMP-Stand (`feat/smp-phase5b@4495213`):** ohne Konflikt übernommen (3-Wege-Anwendung), keine Upstream-Änderung an den Patchdateien
