# ext2-truncate

- **Commit:** Squash von #353
- **Dateien:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Superblock.cs` (`Truncate`), `Ext2IndirectBlocks.cs` (neu)
- **Grund:** `Truncate` gab nur Datenblöcke frei; einfach und doppelt indirekte Zeigerblöcke blieben belegt (E4.0: „Block bitmap differences“ nach Löschen großer Dateien und Verzeichnisse über 12 Blöcke). Außerdem las es bei schnellen Symlinks (Ziel inline in `i_block`, auch mit xattr-Block) und Geräte-Inodes Text als Blockzeiger und gab fremde Blöcke frei; jetzt wie Linux `ext2_truncate_blocks` keine Freigabe für sie. Zeiger werden vor der Freigabe genullt, Wurzelzeiger vor der Freigabe der Zeigerblöcke geschrieben. `Ext2Inode.FileAcl` wird dafür gelesen (nicht zurückgeschrieben). Dreifach indirekte Datenblöcke bleiben außen vor, der Treiber legt sie nicht an
- **Entfernter ZehOS-Umweg:** `CosmosExt2TruncatePlug`, `Ext2IndirectBlocks` (fällt in E2-Z1)
- **Prüfung:** NUnit `Ext2IndirectBlocksTests` (12 Fälle, aus ZehOS übernommen) und `Ext2TruncateTests` (6 Fälle am Treiber, Wurzeln und `Blocks` nach Remount von der Platte gelesen; vor dem Patch 3 rot, die zwei Symlink-Fälle ohne Guard bzw. ohne EA-Abzug rot); alle 296 Tests von `Cosmos.Kernel.Tests.System` grün
