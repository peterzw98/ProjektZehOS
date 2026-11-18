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
