# Korrigierter Cosmos-ext2-Formatter

Gemeinsame Quelle für Kernel (Plug) und Hostprobe `experiments/StorageProbe` (`--formatter fixed`). Cosmos bleibt
3.0.97; der Treiber zum Mounten, Lesen und Schreiben kommt aus dem Paket. Ersetzt werden zwei Stellen: das Formatieren
(`Ext2Formatter.Format`) und die Inode-Freigabe beim Löschen (`Ext2Superblock.FreeInode`, siehe unten).

Im Produkt wird ext2 noch nirgends beim Booten oder über die Shell eingehängt. Die neue explizite Lesekomponente
`CosmosReadOnlyExt2FileSystem` prüft `Ext2FeatureGate.Evaluate` vor dem nativen Mount und verwendet einen eigenen
Dateisystemnamen sowie `/mnt/ext2-ro`, niemals `/data`. Sie ist noch nicht als Produktfunktion aktiviert.

## Abgesicherter Lesemount

`Ext2FeatureGate` lässt nur das geprüfte 4-KiB-Profil zu. Unbekannte Incompat-Bits führen zur Ablehnung,
unbekannte Compat-/RoCompat-Bits höchstens zum Lesemount. `s_state` ist eine Bitmaske: nur 1 (VALID ohne ERROR)
behält die Featureentscheidung; 0, 2 und 3 erlauben höchstens Lesen, unbekannte Zustandsbits führen zur Ablehnung.
Diese Entscheidung repariert keine Metadaten und bestätigt keine Konsistenz beschädigter Volumes.
Vor dem nativen Mount werden außerdem positive Inode-/Blockzahlen (mindestens 16 Blöcke), `first_data_block=0`,
Gruppengrößen von 1 bis 32768 sowie eine Inodegröße als Zweierpotenz zwischen 128 und 4096 geprüft. Damit werden
offensichtliche Geometriefehler vor den nativen Bitmap- und Inodetabellenzugriffen abgelehnt; Verweise innerhalb
der Gruppen und beschädigte Verzeichnisse benötigen weiterhin eine unabhängige Offlineprüfung.
Volumen und Gerät sind auf 128 GiB begrenzt; höchstens 16384 Gruppen entsprechen dem geprüften Formatterprofil
mit 8-MiB-Gruppen. Die Inodezahl muss exakt Gruppenzahl × Inodes je Gruppe entsprechen (überlaufsicher mit `ulong`
berechnet). Diese Grenzen verhindern unkontrollierte Gruppendeskriptor-Allokationen aus manipulierten Headern.

Das Mountflag allein verhindert in Cosmos 3.0.97 nicht alle Schreibzugriffe. Erneute ILSpy-Prüfung der installierten
`Cosmos.Kernel.System.dll` aus Paket 3.0.97 bestätigte (Zeilen im dekompilierten Quelltext):

- `Ext2FileOperations.cs:170–182`: `Fsync` schreibt jeden nativen Inode und flusht immer; `Release` ruft es ohne
  Dirty- oder Read-only-Prüfung auf. `VfsFileHandle.cs:88–92` ruft `Release` auch nach reinem Lesen auf.
- `Ext2Superblock.cs:537–547`: auch `Drop` selbst ruft `Flush` auf; dieses flusht das Gerät und aktualisiert
  anschließend den Superblock. Nur den Aufruf in `Ext2SuperblockOperations` zu umgehen genügt deshalb nicht.

Die Lesesicht verpackt alle über Lookup und ReadDir gelieferten Inodes, verweigert Mutationen und delegiert
Read/Seek mit dem nativen Inode. Release/Fsync/Sync/Drop schreiben nicht und benötigen keine native Freigabe:
die Caches sind verwaltet. Darunter verweigert `CosmosReadOnlyBlockDevice` jeden WriteBlock- und Flush-Aufruf
mit einer sichtbaren Ausnahme. Eine nichtleere Source wird abgelehnt, weil sie im Cosmos-Treiber das injizierte
Gerät umgehen würde. Die Komponente unterstützt zunächst nur 512-Byte-Sektoren.

Ein langlebiges Komponentenobjekt registriert seinen Namen einmal und kann denselben Datenträger erneut mounten.
Für die spätere Produktintegration fehlen noch Gerätebesitz, Rechteprüfung und die allgemeine Mountverwaltung;
derzeit darf die Komponente ausschließlich mit exklusiv gehaltenen Testdatenträgern verwendet werden.

Die Hostprobe `dotnet run --project experiments/StorageProbe/StorageProbe.csproj -c Release -- readonly-selftest`
prüft mit den echten Cosmos-Assemblies ein frisches RAM-Volume einschließlich Datei-/Verzeichnislesen,
Schließen, Sync/Drop, verbotenen Mutationen und unverändertem Volumenhash. Sie ersetzt keinen Gasttest des VFS-Mounts.

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

## Löschen: Korrektur in der Cosmos-Quelle

Bis E2-Z1 ersetzten vier Plugs (`RemoveEntry`, `Rmdir`, `Truncate`, `FreeInode`) fehlerhafte Methoden des
Cosmos-ext2-Treibers 3.0.97. Seit E2-Z1 sind die Fehler im Branch `cosmos` behoben (`PATCHES.md`: `ext2-remove-entry`,
`ext2-truncate`, `ext2-free-inode`, `ext2-rmdir`); die Plugs, ihre Attrappentypen und die Helfer `Ext2DirectoryRemoval`,
`Ext2IndirectBlocks` und `Ext2InodeRelease` samt Hosttests sind entfernt, die Tests leben als NUnit-Tests im Cosmos-Baum
(`tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/`). Hintergrund und frühere Gastbelege:
[Issue-Entwurf](../../../docs/COSMOS_EXT2_UPSTREAM_ISSUE.md) und `docs/COSMOS_SUPPORT.md`.

## Gastprobe

`-p:ZehOSStorageProbe=true` baut `ProjektZeh/Storage/CosmosExt2GuestProbe.cs` ein; die Hostseite ist
`scripts/test-storage-ext2-guest.py`. Die Probe nutzt nur `sata1` (AHCI-Port 1 hinter der App-Platte an Port 0) und
nur, wenn es genau 72 MiB groß ist; die App-Platte hat 64 MiB. Erstes MiB komplett null: Lauf 1 formatiert bewusst
über `VfsManager.TryFormat` (4096 Byte und `sparse_super` belegen den Plug), prüft `Ext2FeatureGate`, mountet die
ganze Platte ohne Partitionstabelle und führt Create/Write, Append, Seek-Write, Read, Rename, Mkdir, Rmdir, Delete,
Sync und Unmount aus; `probe.bin` (72 KiB, reicht in den Einfach-Indirektblock) bleibt. Trägt die Platte ext2:
Lauf 2 verwendet nur die Lesekomponente `CosmosReadOnlyExt2FileSystem` unter `/mnt/ext2-ro` (der native
Cosmos-Lesemount schreibt bei Sync/Drop): SHA-256 über die ganze Platte vorher, Featurevertrag, Mount, Lesen von
`probe.bin`/`reuse.bin`/`neu`, Datei-Handle mit Lesen, Sync und Schließen bei verweigertem Write/SetAttr, verweigerte
Create/Delete/Mkdir/Rename ohne Wirkung, Unmount und derselbe SHA-256 danach; das Hostskript vergleicht zusätzlich den
Image-Hash vor und nach Lauf 2. Für die verweigerten Mutationen prüft die Probe vorher die Ausgangslage (Ziel
vorhanden, neue Namen fehlen) und nachher unveränderten Namen und Hash. `/mnt` muss nicht existieren:
`VfsManager.TryMount` normalisiert nur den Einhängepunkt (Cosmos `VfsManager.cs:692-706`, efcf20c) und prüft keinen
Elternpfad; die Probe protokolliert `ro-info /mnt existiert=…` ohne Bewertung. Jeder andere Inhalt bricht ohne
Schreibzugriff ab.
Seriell `STORAGE_PROBE <schritt> PASS|FAIL …`, Ausnahmen werden dem laufenden Schritt zugeordnet, am Ende immer
`STORAGE_PROBE DONE`; danach fährt der Gast über den normalen Energiepfad herunter.
