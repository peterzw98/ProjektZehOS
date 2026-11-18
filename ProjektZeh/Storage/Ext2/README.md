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
Sektorgröße 0 und Überlauf. Der Standard von `Ext2FormatOptions.BlockSize` bleibt dort 1024. Die Tests liegen im
Cosmos-Baum (`Ext2FormatterTests`). Die 128-GiB-Grenze und die Beschränkung auf 4096 Byte sind ZehOS-Politik und stehen
in `FormatForKernel`.

## Bekannte Grenze: 1K-Blöcke nicht mit dem Cosmos-Treiber verwenden

`Ext2Superblock.TryAllocateBlock` und `FreeBlock` in Cosmos 3.0.97 rechnen `blockNumber = g * blocks_per_group + i`
ohne `first_data_block`. Bei 1K-Blöcken ist das korrekte Layout um einen Block verschoben. Schreibt der
Cosmos-Treiber auf ein korrekt formatiertes 1K-Volume, kann er belegte Metadaten- oder Datenblöcke
überschreiben. Das Original war mit diesem Treiberfehler selbstkonsistent, aber nicht ext2-konform. Kernel und
Kernel rufen deshalb `FormatForKernel` auf: nur 4096 Byte (`null` => 4096), jede andere Blockgröße endet mit
`false` vor dem ersten Schreibzugriff. 2048 ist formatterseitig korrigiert, aber nicht für den Kernel qualifiziert.
Der Cosmos-Formatter ist für die Matrix mit 1K/2K/4K nutzbar (`Ext2FileSystemType.TryFormat`). Achtung: `new
Ext2FormatOptions()` hat in Cosmos weiterhin den Standard 1024; ohne Plug formatiert `VfsManager.TryFormat` ohne Optionen
deshalb mit 1024 Byte. Die 1-KiB-Varianten der Cosmos-Smoketests sind `[Ignore]`, bis der Treiber `FirstDataBlock`
beachtet.

## Regel für Aufrufer

Kernelcode formatiert ext2 nur über `CosmosExt2Formatter.FormatForKernel(device, options)`. Es prüft das ZehOS-Profil
(4096 Byte, höchstens 128 GiB, Sektorgröße > 0), sperrt jede Plattenoperation mit `StorageLock`
(`CosmosLockedBlockDevice`) und ruft dann `Ext2FileSystemType.TryFormat` mit dem Cosmos-Formatter auf. Der frühere Plug
`CosmosExt2FormatterPlug` ist entfallen; seit dem Wegfall formatiert `VfsManager.TryFormat` mit ext2 und ohne
Optionen mit 1024 Byte, deshalb sucht `ProjektZeh.Tests/Ext2FormatterUsageTests.cs` in `ProjektZeh/**/*.cs` außerhalb
dieses Ordners nach `(Cosmos)Ext2Formatter.Format(` und nach `TryFormat` mit `"ext2"` oder `Ext2FormatOptions`; über Variablen
weitergereichte Dateisystemnamen erkennt die Suche nicht.
