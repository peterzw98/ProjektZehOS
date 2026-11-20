# Korrigierter Cosmos-ext2-Formatter

Gemeinsame Quelle für Kernel (Plug) und Hostprobe `experiments/StorageProbe` (`--formatter fixed`). Cosmos bleibt
3.0.97; der Treiber zum Mounten, Lesen und Schreiben kommt aus dem Paket. Ersetzt werden zwei Stellen: das Formatieren
(`Ext2Formatter.Format`) und die Inode-Freigabe beim Löschen (`Ext2Superblock.FreeInode`, siehe unten).

Im Produkt wird ext2 nur in einem Fall beim Booten eingehängt: Bei einer installierten Identität mit Zustand `RootUnclean`
wählt `CosmosLayoutProfile.Select` die Lesekomponente und hängt `/` damit lesend ein (keine Schreibzugriffe, Anmeldung mit
vorhandenen Konten). Sonst wird ext2 weder beim Booten noch über die Shell eingehängt. Die Lesekomponente
`CosmosReadOnlyExt2FileSystem` prüft `Ext2FeatureGate.Evaluate` vor dem nativen Lesemount; für diesen expliziten Weg
verwendet sie einen eigenen Dateisystemnamen sowie `/mnt/ext2-ro`, niemals `/data`. Als allgemeine Produktfunktion
(Shell, Datenlaufwerk) ist sie nicht aktiviert.

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

Den Schreibschutz liefert der Cosmos-Treiber selbst (Pin `3.0.97-zehos.6`, Cosmos #432): `MountFlags.ReadOnly` setzt
`IsReadOnly`; im Lesemodus schreiben `WriteBlocks`, `WriteInode`, `UpdateSuperblock`, `Fsync`, `Release`, `Drop` und
`Flush` nichts, mutierende Inode-Operationen liefern `false`, Öffnen zum Schreiben scheitert. Unbekannte Incompat-Bits
lehnt Cosmos beim Mount ab, unbekannte Compat-/RoCompat-Bits und `s_rev_level` über 1 ergeben einen Lesemount; das
`Ext2FeatureGate` bleibt das strengere Projektprofil davor. Unter Cosmos 3.0.97 (ohne Patch) schrieben Release und Drop
auch bei einem Lesemount; dafür gab es die Lesesicht `CosmosReadOnlyVfs` und `CosmosReadOnlyBlockDevice` um das Gerät.
Beide bleiben für FAT (der FAT-Treiber hat keinen Lesemodus), werden für ext2 aber nicht mehr verwendet. Darunter liegt
nur noch `CosmosLockedBlockDevice` (Sperre je Operation). Eine nichtleere Source wird abgelehnt, weil sie im
Cosmos-Treiber das injizierte Gerät umgehen würde. Die Komponente unterstützt zunächst nur 512-Byte-Sektoren.

Ein langlebiges Komponentenobjekt registriert seinen Namen einmal und kann denselben Datenträger erneut mounten.
Für die spätere Produktintegration fehlen noch Gerätebesitz, Rechteprüfung und die allgemeine Mountverwaltung;
derzeit darf die Komponente ausschließlich mit exklusiv gehaltenen Testdatenträgern verwendet werden.

Die Hostprobe `dotnet run --project experiments/StorageProbe/StorageProbe.csproj -c Release -- readonly-selftest`
prüft mit den echten Cosmos-Assemblies ein frisches RAM-Volume einschließlich Datei-/Verzeichnislesen,
Schließen, Sync/Drop, verbotenen Mutationen und unverändertem Volumenhash; das Gerät zählt dabei jeden WriteBlock- und
Flush-Aufruf, erwartet wird 0 (der Schutz kommt aus Cosmos, nicht aus einer Gerätehülle). Sie ersetzt keinen Gasttest des VFS-Mounts.

## Herkunft und Lizenz

Quelle: [CosmosOS/Cosmos @ efcf20cc62884b042a0adadffe9fc4540491fd89](https://github.com/CosmosOS/Cosmos/tree/efcf20cc62884b042a0adadffe9fc4540491fd89/src/Cosmos.Kernel.System/FileSystem/Ext2),
der Commit, den das NuGet-Paket `Cosmos.Kernel.System` 3.0.97 nennt. BSD 3-Clause, Text in
[LICENSE-Cosmos.txt](LICENSE-Cosmos.txt) (SHA-256 `29f118db…0be04072`).

| Datei hier | Original | SHA-256 des Originals | Änderung |
| --- | --- | --- | --- |
| `CosmosExt2SuperblockLayout.cs` | `Ext2SuperblockLayout.cs` | `47a9c04f997b23c85a7ca0984cd7e76a1d91779573355d21cd47de62e29b0939` | nur Namespace und Klassenname |
| `CosmosExt2InodeLayout.cs` | `Ext2InodeLayout.cs` | `6778cf991ccf5f25dfc29c461a8423b7019f056603b9e6790f9197b84c7f9d8c` | nur Namespace und Klassenname |

Die beiden Layoutklassen sind in Cosmos `internal` und deshalb kopiert. Der Formatter selbst ist keine Kopie mehr:
seine Korrekturen stehen als Patch `ext2-formatter` im Branch `cosmos` (`PATCHES.md`); `CosmosExt2Formatter.cs` enthält nur
das ZehOS-Profil `FormatForKernel`.

## Korrekturen im Formatter (jetzt in Cosmos)

Der Patch `ext2-formatter` im Branch `cosmos` behebt, was früher hier korrigiert war: Gruppengeometrie mit
`first_data_block`, `sparse_super` mit Sicherungs-Superblöcken (Gruppen 0, 1 und Potenzen von 3, 5, 7, mit
`s_block_group_nr`), gesetzte Füllbits der Bitmaps, `blocks_per_group` als Vielfaches von 8 bei einer Gruppe, Abschneiden
einer zu kleinen letzten Gruppe, Superblock und Deskriptoren zuletzt (keine Absturzsicherheit) und Ablehnen von
Sektorgröße 0 und Überlauf. Der Standard von `Ext2FormatOptions.BlockSize` und von `Format(null)` ist dort 4096, und 1024 wird abgelehnt (siehe unten). Die Tests liegen im
Cosmos-Baum (`Ext2FormatterTests`). Die 128-GiB-Grenze und die Beschränkung auf 4096 Byte sind ZehOS-Politik und stehen
in `FormatForKernel`.

## Bekannte Grenze: 1K-Blöcke nicht mit dem Cosmos-Treiber verwenden

`Ext2Superblock.TryAllocateBlock` und `FreeBlock` in Cosmos 3.0.97 rechnen `blockNumber = g * blocks_per_group + i`
ohne `first_data_block`. Bei 1K-Blöcken ist das korrekte Layout um einen Block verschoben. Schreibt der
Cosmos-Treiber auf ein korrekt formatiertes 1K-Volume, kann er belegte Metadaten- oder Datenblöcke
überschreiben. Das Original war mit diesem Treiberfehler selbstkonsistent, aber nicht ext2-konform. Der Cosmos-Formatter
(Patch `ext2-formatter`) lehnt 1024 deshalb ab, bis der Treiber `FirstDataBlock` beachtet (optionales Paket E2-P9), und
`Format(null)` sowie der Standard von `Ext2FormatOptions.BlockSize` sind 4096. 2048 ist formatterseitig korrigiert, aber
nicht für den Kernel qualifiziert. Die 1-KiB-Varianten der Cosmos-Smoketests sind `[Ignore]`, bis der Treiber
`FirstDataBlock` beachtet.

## Regel für Aufrufer

Kernelcode formatiert ext2 nur über `CosmosExt2Formatter.FormatForKernel(device, options)`. Es erzwingt das ZehOS-Profil
(nur 4096 Byte, höchstens 128 GiB, Sektorgröße > 0), sperrt jede Plattenoperation mit `StorageLock`
(`CosmosLockedBlockDevice`) und ruft dann `Ext2FileSystemType.TryFormat` mit dem Cosmos-Formatter auf. Der frühere Plug
`CosmosExt2FormatterPlug` ist entfallen. Der allgemeine VFS-Weg (`VfsManager.TryFormat` mit ext2) würde technisch mit 4096
Byte formatieren, aber ohne Größenbegrenzung und ohne Sperre je Plattenoperation und auch 2048 Byte zulassen; das ist
Politik, nicht Technik. `ProjektZeh.Tests/Ext2FormatterUsageTests.cs` sucht deshalb in `ProjektZeh/**/*.cs` außerhalb
dieses Ordners nach `(Cosmos)Ext2Formatter.Format(` und nach `TryFormat` mit `"ext2"` oder `Ext2FormatOptions`; über Variablen
weitergereichte Dateisystemnamen erkennt die Suche nicht.

## Löschen: Korrektur in der Cosmos-Quelle

Bis E2-Z1 ersetzten vier Plugs (`RemoveEntry`, `Rmdir`, `Truncate`, `FreeInode`) fehlerhafte Methoden des
Cosmos-ext2-Treibers 3.0.97. Seit E2-Z1 sind die Fehler im Branch `cosmos` behoben (`PATCHES.md`: `ext2-remove-entry`,
`ext2-truncate`, `ext2-free-inode`, `ext2-rmdir`); die Plugs, ihre Attrappentypen und die Helfer `Ext2DirectoryRemoval`,
`Ext2IndirectBlocks` und `Ext2InodeRelease` samt Hosttests sind entfernt, die Tests leben als NUnit-Tests im Cosmos-Baum
(`tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/`). Hintergrund und frühere Gastbelege:
[Issue-Entwurf](../../../docs/COSMOS_EXT2_UPSTREAM_ISSUE.md) und `docs/COSMOS_SUPPORT.md`.
