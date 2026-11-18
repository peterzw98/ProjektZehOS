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
| ext2-truncate | Squash von #<PR> | `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Superblock.cs` (`Truncate`), `Ext2IndirectBlocks.cs` (neu) | `Truncate` gab nur Datenblöcke frei; einfach und doppelt indirekte Zeigerblöcke blieben belegt (E4.0: „Block bitmap differences“ nach Löschen großer Dateien und Verzeichnisse über 12 Blöcke). Außerdem las es bei schnellen Symlinks (Ziel inline in `i_block`) und Geräte-Inodes Text als Blockzeiger und gab fremde Blöcke frei; jetzt wie Linux `ext2_truncate_blocks` keine Freigabe für sie. Wurzelzeiger werden vor der Freigabe der Zeigerblöcke geschrieben. Dreifach indirekte Datenblöcke bleiben außen vor, der Treiber legt sie nicht an | `CosmosExt2TruncatePlug`, `Ext2IndirectBlocks` (fällt in E2-Z1) | NUnit `Ext2IndirectBlocksTests` (12 Fälle, aus ZehOS übernommen) und `Ext2TruncateTests` (5 Fälle am Treiber; vor dem Patch 3 rot, der Symlink-Fall ohne Guard rot); alle 295 Tests von `Cosmos.Kernel.Tests.System` grün |
| ext2-free-inode | Squash von #<PR> | `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Superblock.cs` (`FreeInode`) | `FreeInode` löschte nur das Bitmapbit und erhöhte die Zähler; das Inode blieb auf der Platte mit `links_count` > 0 und `dtime` 0 (e2fsck: „Deleted inode … has zero dtime“). Ein schon freies Inode erhöhte die Freizähler ein zweites Mal. Jetzt: schon frei → nichts schreiben; sonst zuerst `links_count = 0` und `dtime = max(jetzt, 1)` direkt in den Tabellenblock (nicht über `WriteInode`, das den Cache wieder füllt), danach Bitmap und Zähler | `CosmosExt2FreeInodePlug`, `Ext2InodeRelease` (fällt in E2-Z1) | NUnit `Ext2FreeInodeTests` (5 Fälle am Treiber, Plattenzustand über einen neuen Mount gelesen; vor dem Patch alle 5 rot) |
