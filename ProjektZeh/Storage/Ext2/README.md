# Korrigierter Cosmos-ext2-Formatter

Gemeinsame Quelle für Kernel (Plug) und Hostprobe `experiments/StorageProbe` (`--formatter fixed`). Cosmos bleibt
3.0.97; der Treiber zum Mounten, Lesen und Schreiben kommt unverändert aus dem Paket. Ersetzt wird nur das
Formatieren.

## Herkunft und Lizenz

Quelle: [CosmosOS/Cosmos @ efcf20cc62884b042a0adadffe9fc4540491fd89](https://github.com/CosmosOS/Cosmos/tree/efcf20cc62884b042a0adadffe9fc4540491fd89/src/Cosmos.Kernel.System/FileSystem/Ext2),
der Commit, den das NuGet-Paket `Cosmos.Kernel.System` 3.0.97 nennt. BSD 3-Clause, Text in
[LICENSE-Cosmos.txt](LICENSE-Cosmos.txt) (SHA-256 `29f118db…0be04072`).

| Datei hier | Original | SHA-256 des Originals | Änderung |
| --- | --- | --- | --- |
| `CosmosExt2Formatter.cs` | `Ext2Formatter.cs` | `695560294181c78effe6f20a4099ca25ce6db604201f689dd58d314a5d5642d7` | siehe unten |
| `CosmosExt2SuperblockLayout.cs` | `Ext2SuperblockLayout.cs` | `47a9c04f997b23c85a7ca0984cd7e76a1d91779573355d21cd47de62e29b0939` | nur Namespace und Klassenname |
| `CosmosExt2InodeLayout.cs` | `Ext2InodeLayout.cs` | `6778cf991ccf5f25dfc29c461a8423b7019f056603b9e6790f9197b84c7f9d8c` | nur Namespace und Klassenname |

Die beiden Layoutklassen sind in Cosmos `internal` und deshalb kopiert. `Destroy` des Originals ist nicht
übernommen, weil nur `Format` gebraucht wird.

## Korrekturen im Formatter

Jede Änderung ist im Code mit `Änderung:` markiert.

1. **Gruppengeometrie:** Gruppe g beginnt bei `first_data_block + g * blocks_per_group`; Bit b der Blockbitmap
   steht für Block `groupStart + b`. Das Original zählte ab Block 0 und markierte bei 1K-Blöcken einen Block zu
   viel als belegt (e2fsck: `Block bitmap differences: -134` bei 4 MiB).
2. **`sparse_super` mit Sicherungen:** Merkmal `sparse_super` (ro_compat 0x1) gesetzt; Gruppen 0, 1 und Potenzen
   von 3, 5 und 7 erhalten Superblock (mit `s_block_group_nr`) und Deskriptortabelle, die Bitmaps folgen danach.
   Das Original schrieb keine Sicherungen, setzte das Merkmal aber auch nicht (e2fsck: `Corrupt group descriptor`).
3. **Bitmap-Padding:** Bits hinter dem Gruppenende bzw. hinter `inodes_per_group` bis zum Ende des Bitmapblocks
   sind gesetzt (e2fsck: `Padding at end of … bitmap is not set`).
4. **Kleine Volumes:** Bei einer einzigen Gruppe wird `blocks_per_group` auf ein Vielfaches von 8 abgerundet
   (bis zu 7 Restblöcke bleiben frei), `inodes_per_group` auf ganze Inodetabellenblöcke aufgerundet. Eine letzte
   Gruppe, die ihre eigenen Metadaten nicht fasst, wird abgeschnitten statt das Formatieren abzulehnen.
5. **Reihenfolge:** Superblock und Deskriptortabellen werden zuletzt geschrieben; freie Zähler werden direkt aus
   dem Layout berechnet statt aus zurückgelesenen Bitmaps. Das ist keine Absturzsicherheit: eine ältere
   Signatur auf dem Gerät wird vorher nicht gelöscht.
6. **Grenzen vor jedem Schreibzugriff:** Gerätesektorgröße 0 und Geräte über 128 GiB (Zielumfang) werden mit
   `false` abgelehnt; die Größenprüfung kann nicht überlaufen.
7. **Standardblockgröße:** `options == null` formatiert mit 4096 statt 1024 Byte (siehe Grenze unten).

## Bekannte Grenze: 1K-Blöcke nicht mit dem Cosmos-Treiber verwenden

`Ext2Superblock.TryAllocateBlock` und `FreeBlock` in Cosmos 3.0.97 rechnen `blockNumber = g * blocks_per_group + i`
ohne `first_data_block`. Bei 1K-Blöcken ist das korrekte Layout um einen Block verschoben. Schreibt der
Cosmos-Treiber auf ein korrekt formatiertes 1K-Volume, kann er belegte Metadaten- oder Datenblöcke
überschreiben. Das Original war mit diesem Treiberfehler selbstkonsistent, aber nicht ext2-konform. Kernel und
Plug rufen deshalb `FormatForKernel` auf: nur 4096 Byte (`null` => 4096), jede andere Blockgröße endet mit
`false` vor dem ersten Schreibzugriff. 2048 ist formatterseitig korrigiert, aber nicht für den Kernel qualifiziert.
`Format` selbst bleibt für die Matrix mit 1K/2K/4K nutzbar. Achtung: `new Ext2FormatOptions()` hat in Cosmos weiterhin den
Standard 1024; Aufrufer setzen `BlockSize` ausdrücklich. Die 1K-Korrektur bleibt für die Linux-Matrix und einen
möglichen Upstream-Fix erhalten.

## Regel für Aufrufer

`ProjektZeh/Storage/CosmosExt2FormatterPlug.cs` ersetzt `Ext2Formatter.Format` aus dem Paket durch
`CosmosExt2Formatter.FormatForKernel`; auch `VfsManager.TryFormat`/`Ext2FileSystemType.TryFormat` mit ext2 laufen damit
nur über das 4-KiB-Profil. Neuer Kernelcode ruft trotzdem bevorzugt `FormatForKernel(device, options)` direkt auf.
`CosmosExt2Formatter.Format` (1K/2K erlaubt) ist nur für die Hostprobe.

Der Plugkörper wird vom Cosmos-Patcher nach `Cosmos.Kernel.System` kopiert und darf deshalb keinen Typ aus
`Cosmos.Kernel.System` nennen; `FormatForKernel` nimmt darum `object?`. Mit `Ext2FormatOptions` in der Signatur brach
ILC mit „library initializer assemblies Cosmos.Kernel.System reference each other in a cycle“ ab (a2c9e39 auf
Branch `task/storage-ext2-kernel-plug`, Gegenprobe ohne Plug grün).

`ProjektZeh.Tests/Ext2FormatterUsageTests.cs` sucht in `ProjektZeh/**/*.cs` außerhalb dieses Ordners nach
`(Cosmos)Ext2Formatter.Format(` und nach `TryFormat` mit `"ext2"` oder `Ext2FormatOptions`; über Variablen
weitergereichte Dateisystemnamen erkennt die Suche nicht.

## Löschen: Plug auf FreeInode

Cosmos 3.0.97 `Rmdir` schreibt das freigegebene Inode nie zurück, `Unlink` setzt kein `dtime`; e2fsck meldete das nach
der Gastprobe (Details und Zeilen: [Issue-Entwurf](../../../docs/COSMOS_EXT2_UPSTREAM_ISSUE.md)).
`ProjektZeh/Storage/CosmosExt2FreeInodePlug.cs` ersetzt `Ext2Superblock.FreeInode`. Die Plattenlogik steht rein in
`Ext2InodeRelease.Release` (Host-Tests `ProjektZeh.Tests/Ext2InodeReleaseTests.cs`): zuerst das Inode mit
`links_count` 0 und `dtime` schreiben, dann das Bitmapbit löschen; ist das Bit schon frei, wird nichts geschrieben und
die Zähler bleiben unverändert. Blöcke gibt sie nicht frei (Rmdir/Truncate tun das selbst). Zähler, Deskriptor und
Cache schreibt der Plug wie das Original fort; interne Member erreicht er nur über `UnsafeAccessor` mit Typnamen, der
Plugkörper nennt keinen Typ aus `Cosmos.Kernel.System`.
