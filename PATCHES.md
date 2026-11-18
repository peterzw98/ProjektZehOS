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
| ext2-remove-entry | dieser Commit (Hash nach dem Merge nicht in der Zeile selbst möglich) | `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Directory.cs` (`RemoveEntry`) | `RemoveEntry` trug den Vorgänger über Blockgrenzen, verschmolz dabei über die Blockgrenze (16-Bit-Überlauf von `rec_len`) und schrieb alle Blöcke zurück, auch bei Blockzeiger 0 (Block 0 des Geräts) | `CosmosExt2RemoveEntryPlug`, `Ext2DirectoryRemoval` (fällt in E2-Z1) | NUnit `Ext2RemoveEntryTests` (13 Fälle, vor dem Patch 7 rot); Hostprobe `test-boundary.py` siehe PR |
