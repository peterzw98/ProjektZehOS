# Etappe 2: Patchreihe ext2

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-18-multicore.md) lesen, besonders „Cosmos-Patches“.
> Empfohlene Arbeitsweise: superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel:** Die ext2-Fehler von Cosmos 3.0.97 sind in der Cosmos-Quelle behoben. Die fünf ext2-Plugs, ihre
Attrappentypen und die kopierte Formatter-Quelle sind aus ZehOS entfernt; die Grenze von 8189 Verzeichniseinträgen
und die dokumentierte Inode-Cache-Grenze sind aufgehoben oder begründet geblieben.

**Architektur:** Je Fehler ein Commit im Branch `cosmos` mit einem Test im Cosmos-Baum. Die ZehOS-Logik aus den Plugs
wandert dabei in die Cosmos-Methoden; die ZehOS-Hosttests dazu werden zu Cosmos-Tests. In ZehOS entfällt der Plug im
selben PR, der den Cosmos-Commit einbindet.

**Voraussetzung:** Etappe 1, einschließlich E1-F (StorageProbe gegen die eigenen Pakete).

**Entwurf:** [Abschnitt 8.1](../specs/2026-11-18-multicore-design.md); Hintergrund in
`docs/STORAGE_3_0_97_FINDINGS.md`, `docs/COSMOS_SUPPORT.md` (Abschnitte zu ext2) und `ProjektZeh/Storage/Ext2/README.md`.

**Abstimmung:** Die Reihe gehört fachlich zur Storage-Arbeitsgruppe. Vor Beginn mit deren Lead klären: Reihenfolge
gegenüber den laufenden Etappen 4.3, 4.5, 7a und 7b, und wer die Gastläufe fährt. Diese Etappen hängen an denselben
Dateien: 7a-S4 sieht `CosmosExt2Formatter.FormatForKernel` im Installer vor; 4.3 und 4.5 nutzen `Ext2FeatureGate`,
`CosmosReadOnlyExt2FileSystem` und `MigrationPlan`.

## Drei Dinge, die für jedes Paket gelten

1. **Ein stehengebliebener Plug überschreibt den Cosmos-Fix still.** Bleibt die Signatur der Zielmethode gleich, bindet der Plug weiter, und `scripts/check-plug-binding.py` meldet nichts. Der ZehOS-PR setzt deshalb `deploy/cosmos/COMMIT` und löscht den Plug **im selben Commit**.
2. **Geprüft wird auf drei Ebenen**, jeweils gegen den gepatchten Stand:
   - Cosmos-Test: NUnit im Cosmos-Baum, `tests/Cosmos.Kernel.Tests.System/FileSystem/Ext2/` (dort liegt `Ext2SmokeTest.cs`; `InternalsVisibleTo` für dieses Testprojekt ist gesetzt). Aufruf im Quellbaum: `dotnet test tests/Cosmos.Kernel.Tests.System/Cosmos.Kernel.Tests.System.csproj -c Release --filter "FullyQualifiedName~Ext2"`.
   - Hostprobe mit dem echten Treiber: `experiments/StorageProbe` (nach E1-F gegen die eigenen Pakete), Linux mit e2fsprogs.
   - Gastlauf mit `e2fsck -fn`, immer auf einer Kopie des Images.
3. **Lizenz:** Die Logik der Plugs ist ZehOS-Code unter GPL-3.0; der Cosmos-Baum steht unter BSD 3-Clause. Der Branch `cosmos` bleibt Teil des ZehOS-Repositorys und wird nicht weitergegeben; der Plan nimmt deshalb an, dass übernommene ZehOS-Logik unter GPL-3.0 bleibt (offene Frage in der Übersicht, nicht blockierend). Jeder Patch trägt an der geänderten Stelle den Kommentar `// ZehOS patch <Kurzname>, see PATCHES.md` (so steht es auch in `CLAUDE.md` des Branches `cosmos`). `CosmosExt2Formatter.cs` ist aus Cosmos abgeleitet und steht schon unter BSD 3-Clause.

## Gastwerkzeug E4.0

Die Probe `-p:ZehOSStorageRootProbe=true` und `scripts/test-storage-ext2-root.py` liegen nur auf dem Branch
`origin/task/storage-e4-0-root-probe`, nicht auf `master`. Sie sind die einzige Prüfung für Blockgrenzen, große
Dateien und volle Verzeichnisse.

**Paket E2-P0 (Voraussetzung für P1 bis P4, P7, P8):** Mit dem Storage-Lead klären, ob die Probe nach `master` kommt
(bevorzugt) oder ob die Gastläufe von einem Branch aus fahren, der `origin/task/storage-e4-0-root-probe` mit dem
jeweiligen ZehOS-PR zusammenführt. Ergebnis als eine Zeile im Kanal und in der PR-Beschreibung jedes Pakets.

## Pakete und Bahnen

Die Patches teilen sich wenige Cosmos-Dateien. Damit mehrere Agenten arbeiten können, gibt es drei Bahnen; innerhalb
einer Bahn läuft es nacheinander, die Bahnen laufen nebeneinander.

| Bahn | Pakete in Reihenfolge | Cosmos-Dateien | Rolle |
| --- | --- | --- | --- |
| A Verzeichnis | E2-P1 `RemoveEntry` → E2-P7 Verzeichnisgrenze | `Ext2Directory.cs` | Engineer senior |
| B Inode und Blöcke | E2-P3 `Truncate` → E2-P4 `FreeInode` → E2-P2 `Rmdir` → E2-P8 Inode-Cache → E2-P6 Feature-Prüfung | `Ext2Superblock.cs`, `Ext2InodeOperations.cs`, `Ext2FileSystemType.cs`, `Ext2FileOperations.cs` | Engineer senior |
| C Formatter | E2-P5 Formatter | `Ext2Formatter.cs`, `Ext2FormatOptions.cs` | Engineer |

**Reihenfolge in ZehOS:** Die Plugs für `RemoveEntry`, `Rmdir`, `Truncate` und `FreeInode` hängen voneinander ab (der
Rmdir-Plug ruft den Adapter aus der Datei des RemoveEntry-Plugs und setzt das Verhalten der Plugs für `Truncate` und
`FreeInode` voraus). Sie fallen deshalb in **einem** ZehOS-PR (Paket E2-Z1), sobald die vier Cosmos-Commits P1 bis P4
vorliegen. P5, P6, P7 und P8 haben je einen eigenen ZehOS-PR.

Cosmos-Pfade unten sind relativ zu `src/Cosmos.Kernel.System/FileSystem/Ext2/` im Branch `cosmos`.

## Review-Schwerpunkte

Fehler, die beim Lesen der Cosmos-Quelle aufgefallen und noch durch keinen Lauf belegt sind. Jeder bekommt im
genannten Paket einen Test.

1. `RemoveEntry` schreibt heute alle Verzeichnisblöcke zurück, auch wenn ein Blockzeiger 0 ist; `WriteBlocks` hat keinen Schutz für Block 0 (E2-P1: nur den geänderten Block schreiben; Test mit Verzeichnisloch).
2. `Rmdir` lehnt `.` nicht ab und würde den Punkteintrag im Elternverzeichnis entfernen (E2-P2).
3. Ein Verzeichnis über 16 MiB oder 4096 Blöcke liefert eine leere Eintragsliste; `Rmdir` hielte es für leer (E2-P7).
4. Die blocklokale Löschung lässt je Block ein freies Record stehen; `MaxParseIters` zählt freie Records mit, das Lesefenster kann vor 8189 lebenden Einträgen enden (E2-P7).
5. `FreeInode` für ein schon freies Inode zählt die Freizähler doppelt hoch (E2-P4).

---

### Paket E2-P1: `RemoveEntry` blocklokal

**Cosmos-Branch:** `cosmos-patch/ext2-remove-entry`. **Datei:** `Ext2Directory.cs:302-369`,
`public static bool RemoveEntry(Ext2Superblock sb, Ext2Inode dir, string name)` in `internal static class Ext2DirectoryHelper`.
Aufrufer: `Ext2InodeOperations.cs:417`, `:472`, `:544`, `:546`.

**Fehler:** Der Vorgänger wird über Blockgrenzen weitergetragen (`:363-365`), beim Verschmelzen entsteht ein Record
über die Blockgrenze und möglicher 16-Bit-Überlauf (`:344-345`); danach werden alle Blöcke geschrieben (`:353-357`).

**Änderung:** Die Logik aus ZehOS `ProjektZeh/Storage/Ext2/Ext2DirectoryRemoval.cs:12-63` übernehmen:
Blöcke bis zum ersten Loch lesen; jedes Record prüfen (Kopf im Block, `rec_len >= 8`, Vielfaches von 4, keine
Blockgrenze überschritten), bei Verstoß `false` ohne Schreiben; Namen als UTF-8-Bytes vergleichen; nur mit dem
Vorgänger im selben Block verschmelzen, das erste Record eines Blocks nur mit `inode = 0` freigeben; genau den einen
geänderten Block schreiben. Die Signatur bleibt.

- [ ] **Schritt 1: Cosmos-Tests** (`Ext2RemoveEntryTests.cs` neben `Ext2SmokeTest.cs`): die elf Fälle aus ZehOS `ProjektZeh.Tests/Ext2DirectoryRemovalTests.cs` übernehmen (`FirstEntryOfLaterBlock_OnlyFreedNotMerged`, `EntryInsideBlock_MergesWithPredecessorInSameBlock`, `LastEntryOfBlock_MergesUpToBlockEnd`, `ManyConsecutiveDeletes_NoRecordCrossesBlockOrOverflows`, `FreedFirstSlotThenNextEntry_MergesIntoFreedSlot`, `OnlyEntryOfBlock_FreedInPlace`, `Missing_ReturnsNotFoundAndChangesNothing`, `RecordCrossingBlock_RefusedWithoutWrite`, `ZeroRecLen_RefusedWithoutWrite`, `NamesCompareExactBytes`, `Layout_SpansSeveralBlocks`), jetzt gegen `Ext2DirectoryHelper.RemoveEntry` auf einem RAM-Gerät wie im Smoke-Test. Neu: `DirectoryWithHole_DoesNotWriteBlockZero` (Blockzeiger 1 des Verzeichnisses ist 0; nach `RemoveEntry` ist Block 0 des Geräts unverändert).
- [ ] **Schritt 2:** Tests schlagen fehl. Patch schreiben, Tests bestehen, `Ext2SmokeTest` weiter grün.
- [ ] **Schritt 3:** Datei `patches/ext2-remove-entry.md`, ein Commit, PR gegen `cosmos`.
- [ ] **Schritt 4: Hostprobe:** in ZehOS mit `scripts/build-cosmos.sh --commit <Commit>` bauen, dann `python3 experiments/StorageProbe/test-boundary.py <neuer Ordner>` mit `-p:ZehOSCosmosVersion=<Version aus der Skriptausgabe>`. Erwartet nach dem Löschen des ersten Eintrags im zweiten Block: Verzeichnisstruktur gültig (ohne Patch meldet `e2fsck` „directory corrupted“). `e2fsck -fn` endet hier noch **nicht** mit 0: Es bleibt „Deleted inode … has zero dtime“, auch im Kontrollfall ohne Blockgrenze. Das behebt erst E2-P4 (`FreeInode`); Ende 0 ist die Abnahme von E2-Z1 mit P1 bis P4 (Befund der Hostprobe am 18. November 2026).

---

### Paket E2-P3: `Truncate` gibt Zeigerblöcke frei

**Cosmos-Branch:** `cosmos-patch/ext2-truncate`. **Datei:** `Ext2Superblock.cs:1049-1076`,
`internal void Truncate(Ext2Inode inode, ulong newSize)`. Aufrufer: `Ext2InodeOperations.cs:425`, `:627`.

**Fehler:** Nur Datenblöcke werden freigegeben (`:1060-1069`); der Kommentar `:1043-1045` nennt das Leck.
`GetBlockPointer` liefert für den dreifach indirekten Bereich 0 (`:1014-1016`).

**Änderung:** Die Logik aus ZehOS `ProjektZeh/Storage/Ext2/Ext2IndirectBlocks.cs:13-113` (`Collect`, `PlanTruncate`)
und `CosmosExt2TruncatePlug.cs:27-83` übernehmen. Reihenfolge, die ein Abbruch nicht zu einem Zeiger auf einen freien
Block machen kann: Plan über den Zeigerbaum **vor** dem Freigeben berechnen (Zeiger ab `BlocksCount` überspringen,
doppelte einmal zählen); Datenblöcke freigeben; Einträge in teilweise genutzten Zeigerblöcken nullen; die Wurzeln
`i_block[12..14]` nullen; dann erst die Zeigerblöcke freigeben; `inode.Blocks` auch für Zeigerblöcke verringern.
**Schneller Symlink** (Befund Ada, 18. November 2026): `Unlink` ruft `Truncate(target, 0)` auch für Symlinks, deren Ziel
in `i_block` steht (`Blocks == 0`, `Ext2FileOperations.WriteInlineSymlink`). Heute liest `GetBlockPointer(0)` die ersten
vier Textbytes als Blocknummer und gibt einen fremden Block frei; `Blocks` läuft unter null. Deshalb zuerst: Ist der
Inode ein Symlink und `Blocks == 0`, nur die Größe setzen und den Inode schreiben, nichts freigeben. Test
`Unlink_FastSymlink_FreesNoBlock` (Blockbitmap und `free_blocks_count` vorher und nachher gleich). Derselbe Fehler steckt
im ZehOS-Plug `CosmosExt2TruncatePlug`; er fällt mit E2-Z1. Auf `master` ist er nicht erreichbar, weil `PathGuard`
jeden Symlink im Pfad verweigert und ZehOS selbst keine anlegt. **Wer `PathGuard` für Symlinks lockert, braucht vorher
E2-Z1.**
Dreifach indirekte **Daten**blöcke bleiben außerhalb dieses Patches (Cosmos legt sie nicht an); der Kommentar im Code
nennt das.

- [ ] **Schritt 1: Cosmos-Tests** (`Ext2TruncateTests.cs`): die zwölf Fälle aus ZehOS `ProjektZeh.Tests/Ext2IndirectBlocksTests.cs` für die Planlogik übernehmen. Dazu zwei Tests am echten Treiber: `TruncateToZero_SingleIndirectFile_FreesPointerBlock` und `TruncateToZero_DoubleIndirectFile_FreesAllPointerBlocks` (Datei schreiben wie in `LargeFileIndirectRoundtrip`, freie Blöcke vor dem Anlegen und nach `Truncate(inode, 0)` vergleichen: gleich).
- [ ] **Schritt 2:** Tests schlagen fehl, Patch, Tests bestehen.
- [ ] **Schritt 3:** Datei `patches/ext2-truncate.md`, ein Commit, PR gegen `cosmos`.

---

### Paket E2-P4: `FreeInode` schreibt Inode und dtime

**Cosmos-Branch:** `cosmos-patch/ext2-free-inode` (auf P3). **Datei:** `Ext2Superblock.cs:617-632`,
`internal void FreeInode(uint inodeNumber)`. Aufrufer: `Ext2InodeOperations.cs:179`, `:216`, `:261`, `:349`, `:383`, `:426`, `:484`.

**Fehler:** Bitmapbit löschen und Zähler erhöhen, ohne das Inode zu schreiben und ohne Prüfung, ob das Bit schon frei war.

**Änderung** (Vorlage: ZehOS `Ext2InodeRelease.cs:11-39`): Ist das Bit schon frei, nichts schreiben und zurückkehren.
Sonst **zuerst** im Inodetabellenblock `links_count = 0` (`Ext2InodeLayout.LinksCountOffset`, 26) und
`dtime = max(jetzt, 1)` (`DtimeOffset`, 20) schreiben, **danach** die Bitmap, dann die Zähler wie bisher.
`_inodeCache.Remove(inodeNumber)` bleibt am Ende. Achtung: `WriteInode` legt das Objekt wieder in den Cache (`:560`);
entweder direkt über den Tabellenblock schreiben oder `Remove` nach `WriteInode` ausführen.

- [ ] **Schritt 1: Cosmos-Tests** (`Ext2FreeInodeTests.cs`): die fünf Fälle aus ZehOS `ProjektZeh.Tests/Ext2InodeReleaseTests.cs` am echten Treiber (`Release_LiveDirectory_WritesLinksZeroAndDtimeThenClearsBit`, `Release_LastInodeOfTableBlock_MarksOnlyThatSlot`, `Release_AlreadyFree_WritesNothingAndReportsFalse` als „Zähler unverändert“, `Release_WithoutClock_StoresNonZeroDtime`, `Release_InodeInSecondTableBlock_UsesThatBlock`). Dazu `FreeInode_RemovesFromCache`: nach `FreeInode` liefert ein erneutes Lesen des Inodes `LinksCount == 0` und `Dtime != 0`.
- [ ] **Schritt 2:** Tests schlagen fehl, Patch, Tests bestehen.
- [ ] **Schritt 3:** Datei `patches/ext2-free-inode.md`, ein Commit, PR gegen `cosmos`.

---

### Paket E2-P2: `Rmdir` gibt alles frei

**Cosmos-Branch:** `cosmos-patch/ext2-rmdir` (auf P4; braucht P1, P3 und P4). **Datei:** `Ext2InodeOperations.cs:442-499`,
`public bool Rmdir(IVfsInode dir, ReadOnlySpan<char> name)`.

**Fehler:** Nur `target.Block[0]` wird freigegeben (`:477-481`); `LinksCount = 0` wird gesetzt, das Inode aber nie
geschrieben (`:483-484`); `.` wird nicht abgelehnt.

**Änderung:** `.` und `..` vorab mit `false` ablehnen. `:477-484` ersetzen durch `_superblock.Truncate(target, 0);`
`target.LinksCount = 0;` `_superblock.WriteInode(target);` `_superblock.FreeInode(target.InodeNumber);`. Der Rest
(Eltern-Links, `UsedDirsCount`, `WriteUsedDirsCount`) bleibt.

- [ ] **Schritt 1: Cosmos-Tests** (`Ext2RmdirTests.cs`): `Rmdir_DirectoryWithManyBlocks_FreesAllBlocksAndPointerBlock` (Verzeichnis mit mehr als 12 Blöcken füllen, leeren, entfernen; freie Blöcke und freie Inodes wie vor dem Anlegen), `Rmdir_Dot_IsRefused`, `Rmdir_DotDot_IsRefused`, `Rmdir_NonEmpty_IsRefused`, `Rmdir_WritesDtime`.
- [ ] **Schritt 2:** Tests schlagen fehl, Patch, Tests bestehen.
- [ ] **Schritt 3:** Datei `patches/ext2-rmdir.md`, ein Commit, PR gegen `cosmos`.

---

### Paket E2-Z1: Vier Plugs aus ZehOS entfernen

**Branch:** `task/mc-e2-z1-ext2-plugs`. **Nach:** P1, P3, P4, P2 liegen als Commits vor.

**Dateien:**
- Ändern: `deploy/cosmos/COMMIT` (Commit mit allen vier Patches), `deploy/cosmos/VERSION`, `global.json`
- Löschen: `ProjektZeh/Storage/CosmosExt2RemoveEntryPlug.cs`, `CosmosExt2RmdirPlug.cs`, `CosmosExt2TruncatePlug.cs`, `CosmosExt2FreeInodePlug.cs`, `CosmosExt2PlugTypes.cs`
- Löschen: `ProjektZeh/Storage/Ext2/Ext2DirectoryRemoval.cs`, `Ext2IndirectBlocks.cs`, `Ext2InodeRelease.cs` und ihre Einträge in `ProjektZeh.Tests/ProjektZeh.Tests.csproj` (bei `6348c7a` die Zeilen 85 bis 87)
- Löschen: `ProjektZeh.Tests/Ext2DirectoryRemovalTests.cs`, `Ext2IndirectBlocksTests.cs`, `Ext2InodeReleaseTests.cs`, `Ext2RemovalImageTests.cs` (ihr Inhalt lebt als Cosmos-Tests weiter)
- Doku: `docs/COSMOS_SUPPORT.md` (Abschnitt „E4.0: ext2-Löschung und Speichergrenzen“), `docs/STORAGE_3_0_97_FINDINGS.md` (Abschnitt zu den Plugs), `ProjektZeh/Storage/Ext2/README.md`, `ROADMAP.md` (Zeilen zu #225 und zum Truncate-Plug), Inventar (vier Zeilen als erledigt)

- [ ] **Schritt 1:** Dateien löschen, Versionen setzen, `scripts/build-cosmos.sh`.
- [ ] **Schritt 2:** `grep -rn "CosmosExt2DirectoryRemoval\|Ext2IndirectBlocks\|Ext2InodeRelease\|CosmosExt2PlugTypes" ProjektZeh ProjektZeh.Tests experiments scripts` ohne Treffer.
- [ ] **Schritt 3:** Hosttests, nativer Build, `python3 scripts/check-plug-binding.py artifacts/kernel-build.log ProjektZeh`. Erwartet: von den ext2-Plugs ist nur noch der Formatter-Plug gelistet und gebunden.
- [ ] **Schritt 4: Gastläufe (Clerk, KVM, Kopie des Images):**

| Lauf | Befehl | Erwartet |
| --- | --- | --- |
| kleine Probe | ISO mit `-p:ZehOSStorageProbe=true`; `python3 scripts/test-storage-ext2-guest.py --iso <ISO> --work <neuer Ordner> --accel kvm` | Ende 0, `e2fsck -fn` Ende 0 |
| E4.0 Löschen an der Blockgrenze | ISO mit `-p:ZehOSStorageRootProbe=true`; `python3 scripts/test-storage-ext2-root.py --size 2 --diagnose-run 10` (Stand laut E2-P0) | `STORAGE_ROOT … PASS`, `e2fsck -fn` Ende 0 |
| E4.0 Verzeichnis leeren und entfernen | `… --diagnose-run 9` | wie oben; Zähler im Gast gleich `dumpe2fs` |
| E4.0 große Dateien | `… --size 2`, Läufe 1 bis 5 | nach jedem Lauf `e2fsck -fn` Ende 0, keine „Block bitmap differences“ |
| Desktop mit Datenlaufwerk | `python3 scripts/test-storage-data-disk.py` (Aufruf wie in `docs/DESKTOP_STORAGE_TESTS.md`) | Erfolg |

- [ ] **Schritt 5:** Doku anpassen, Commit (`refactor(storage): ext2-Plugs entfernt, Fehler in Cosmos behoben`), Review durch die Storage-Arbeitsgruppe, PR. Merge erst, wenn die vier `cosmos`-PRs gemergt sind und `COMMIT` auf den gemergten Stand zeigt.

> **Lead-Entscheidung 18.11.2026 (Lead Multicore):** E4.0 Lauf 9 (8190 Einträge) scheitert in Z1, weil die Probe noch die alte Grenze prüft; das ist kein Fehler von Z1.
> Die Probe auf die neue Grenze umzustellen gehört zum ZehOS-Teil von E2-P7 (Schritt 3). Z1 führt Lauf 9 unter „Grenzen“ und nennt dort,
> dass `rmdir` großer Verzeichnisse im Gast nur über die Läufe 1 bis 5 abgedeckt ist.

---

### Paket E2-P5: Formatter

**Cosmos-Branch:** `cosmos-patch/ext2-formatter`. **Dateien:** `Ext2Formatter.cs:63-443`
(`public static bool Format(IBlockDevice device, Ext2FormatOptions? options)`, Aufrufer `Ext2FileSystemType.cs:67-68`).

**Fehler:** kein `sparse_super`, keine Backup-Superblöcke (`:156`, `:186-189`); Gruppenanfang ohne `first_data_block`
(`:193`); Füllbits der Bitmaps fehlen.

**Änderung:** Den Rumpf von `Format` durch den von ZehOS `ProjektZeh/Storage/Ext2/CosmosExt2Formatter.cs` (`Format`,
ab `:96`, mit den Helfern `DescriptorBlocks`, `MetadataBlocks`, `GroupBlocks`, `HasSuperblockBackup`, `SetBits`)
ersetzen; die Datei ist aus genau dieser Cosmos-Datei abgeleitet, die Änderungen sind dort mit „Änderung:“ markiert.
Namespaces und die Layout-Typen auf die Cosmos-Originale (`Ext2SuperblockLayout`, `Ext2InodeLayout`) zurückführen.

**Was in ZehOS bleibt** (Projektpolitik, gehört nicht nach Cosmos): nur 4096 Byte Blockgröße, Grenze 128 GiB.
`FormatForKernel` wird zu einer dünnen ZehOS-Methode, die diese Grenzen prüft und dann den Cosmos-Formatter mit
`new Ext2FormatOptions { BlockSize = 4096 }` aufruft. Der Standard von `Ext2FormatOptions.BlockSize` (1024) bleibt in
Cosmos unverändert; der Treiberfehler für 1-KiB-Volumes (`Ext2Superblock.cs:657`, `:675`, `:693` ohne
`FirstDataBlock`) wird hier nicht behoben, weil ZehOS solche Volumes nie anlegt und `Ext2FeatureGate` sie ablehnt.

- [ ] **Schritt 1: Cosmos-Tests** (`Ext2FormatterTests.cs`): `Format_4K_SetsSparseSuperAndWritesBackups` (Superblock-Kopien in den Gruppen 1, 3, 5, 7, 9 mit passendem `s_block_group_nr`), `Format_BitmapsHavePaddingBitsSet`, `Format_LastGroupTooSmall_IsCutOff`, `Format_SingleGroup_RoundsBlocksPerGroupToMultipleOfEight`, `Format_ZeroSectorSize_IsRefused`. `Ext2SmokeTest.FormatAndMount` (formatiert mit 1024) bleibt grün.
- [ ] **Schritt 2:** Tests schlagen fehl, Patch, Tests bestehen. Datei `patches/ext2-formatter.md`, ein Commit, PR gegen `cosmos`.
- [ ] **Schritt 3: ZehOS** (Branch `task/mc-e2-p5-formatter`): `CosmosExt2FormatterPlug.cs` löschen; `CosmosExt2Formatter.cs` auf `FormatForKernel` als dünne Hülle kürzen (Datei bleibt, damit `Ext2FormatterUsageTests` und die Aufrufer unverändert gelten); `CosmosExt2SuperblockLayout.cs` und `CosmosExt2InodeLayout.cs` bleiben, solange `CosmosExt2GuestProbe.cs` und `experiments/StorageProbe` ihre Konstanten nutzen; die Gastprobe (`CosmosExt2GuestProbe.cs:69-77`) formatiert über `FormatForKernel` statt mit `options = null`.
- [ ] **Schritt 4: Hostprobe (Linux):** `dotnet run -c Release --project experiments/StorageProbe -- selftest`; `experiments/StorageProbe/matrix.sh .tools/storage-probe fixed` (Ende 0 nur bei sauberer `e2fsck`-Matrix); `python3 scripts/test-installer-layout.py .tools/installer-layout`.
- [ ] **Schritt 5: Gast:** kleine Probe wie in E2-Z1; Schritt `format` meldet „4096 Byte, sparse_super“. Danach das formatierte Image unter Linux einhängen und Dateien lesen.
- [ ] **Schritt 6:** Doku (`ProjektZeh/Storage/Ext2/README.md`, `docs/COSMOS_SUPPORT.md` „ext2-Formatierer“), Commit, Review, PR.

> **Lead-Entscheidung 18.11.2026 (Lead Multicore):** Der ZehOS-Teil (#363) pinnt `deploy/cosmos/COMMIT` auf `d25b8200`
> (Kopf von `cosmos` mit #361 und #362, Nachfahre von `775666d7`) und setzt `VERSION`/`global.json` auf `3.0.97-zehos.5`.
> `zehos.3` und `zehos.4` sind verbrannt: Sie liegen in NuGet-Caches schon aus anderen Commits (`zehos.3` aus `4490718` lokal,
> `zehos.4` aus `75206e9` auf Peters Rechner; diese Angabe stammt aus einer Kanalmeldung vom 18.11.2026, Peters
> Cache wurde nicht selbst eingesehen), und `scripts/build-cosmos.sh` bricht an der Stempelprüfung ab, wenn eine Version
> zu einem anderen Commit gehört. Regel: Eine neue `-zehos.N` vor der Vergabe gegen alle bekannten Caches prüfen; Pin ist der
> aktuelle Kopf von `cosmos`.

---

### Paket E2-P7: Verzeichnisgrenze

**Cosmos-Branch:** `cosmos-patch/ext2-directory-limit` (auf P1). **Datei:** `Ext2Directory.cs:13-19`, `:28-108`
(`ParseDirectory`), Konstanten `MaxParseIters = 8192`, `MaxDirectorySize = 16 MiB`, `MaxDirectoryBlocks = 4096`.

**Fehler:** `MaxParseIters` zählt jedes Record, auch freie; `AddEntry` und `RemoveEntry` kennen die Grenze nicht, so
entstehen unsichtbare Einträge. Über `MaxDirectorySize` oder `MaxDirectoryBlocks` liefert `ParseDirectory` eine leere
Liste, als wäre das Verzeichnis leer.

**Änderung:** `MaxParseIters` entfernen; die Schleife endet durch die Record-Prüfungen und die Blocklänge. Bei
Überschreitung von `MaxDirectorySize` oder `MaxDirectoryBlocks` nicht „leer“ liefern, sondern den Aufrufer scheitern
lassen: `ParseDirectory` bekommt die Form `bool TryParseDirectory(Ext2Superblock sb, Ext2Inode dir, out List<Ext2DirEntry> entries)`;
die vier Aufrufer (`Ext2Directory.cs:122`, `Ext2InodeOperations.cs:67`, `:106`, `:462`) behandeln `false` als Fehler
(kein Fund, keine leere Liste, `Rmdir` lehnt ab). `AddEntry` lehnt ab, wenn das Verzeichnis dadurch über
`MaxDirectoryBlocks` wüchse.

- [ ] **Schritt 1: Cosmos-Tests** (`Ext2DirectoryLimitTests.cs`): `Directory_With10000Entries_ListsAll`, `Directory_AfterDeletingEveryOtherEntry_StillListsAllLiving` (Schwerpunkt 4), `AddEntry_BeyondMaxDirectoryBlocks_IsRefused`, `Rmdir_OfDirectoryThatCannotBeParsed_IsRefused` (Schwerpunkt 3).
- [ ] **Schritt 2:** Tests schlagen fehl, Patch, Tests bestehen. Datei `patches/<Kurzname>.md`, ein Commit, PR gegen `cosmos`.
- [ ] **Schritt 3: ZehOS** (Branch `task/mc-e2-p7-verzeichnisgrenze`): `FileService.MaximumExt2DirectoryEntries` (`FileService.cs:18-20`) auf die neue Grenze setzen. Sie ergibt sich aus Cosmos: 4096 Blöcke zu 4096 Byte bei mindestens 12 Byte je Record sind rechnerisch über eine Million Einträge; als Produktgrenze schlägt der Plan **65.536** vor (Verzeichnisse darüber sind nicht geprüft). Den Wert bestätigt der Storage-Lead vor diesem Schritt. Der Mechanismus (`EnsureDirectoryEntryAvailableStorageLocked`, `CosmosDirectoryEntryLimit.cs`) bleibt. `ProjektZeh.Tests/MigrationDirectoryLimitTests.cs:21` und `DirectoryEntryLimitTests.cs` auf die Konstante statt auf die Zahl `8189` beziehen.
- [ ] **Schritt 4: Gast:** E4.0 Lauf 9 mit 8190 Einträgen (`--limit-only`) und ein neuer Lauf mit 20.000 Einträgen in einem Verzeichnis: anlegen, auflisten (Anzahl stimmt), jeden zweiten löschen, auflisten, Rest löschen, `rmdir`; `e2fsck -fn` Ende 0. Der Lauf mit 20.000 Einträgen berührt die Inode-Cache-Grenze; scheitert er daran, ist E2-P8 Voraussetzung.
- [ ] **Schritt 5:** Doku (`docs/handbuch/ENTWICKLUNG.md`, Abschnitt „Dateisystem in QEMU“, früher `README.md:172`; `docs/COSMOS_SUPPORT.md` „ext2-Verzeichnisgrenze“, `docs/superpowers/specs/2026-11-17-ext2-directory-limit.md` als historisch kennzeichnen), Commit, Review, PR.

---

### Paket E2-P8: Inode-Cache begrenzen

**Cosmos-Branch:** `cosmos-patch/ext2-inode-cache` (Bahn B, nach P2). **Datei:** `Ext2Superblock.cs:20`
(`Dictionary<uint, Ext2Inode> _inodeCache`), gefüllt in `:512` und `:560`, geleert nur in `:631` und `:759`.

**Lage:** Kein ZehOS-Umweg, nur eine dokumentierte Grenze (rund 17.500 Inodes je Mount, dann scheitert die
Vergrößerung des Wörterbuchs an der Speicherzerstückelung). Peter hat einen Plug dafür abgelehnt; ein Patch an der
Quelle war damals nicht möglich. Cosmos verlässt sich darauf, dass es je Inode genau ein Objekt gibt.

**Änderung, erste Stufe (klein, belegt durch Diagnoselauf 12):** Das Wörterbuch beim Mount vorab dimensionieren,
`new Dictionary<uint, Ext2Inode>(capacity)` mit `capacity = min(InodesCount, 65_536)`. Damit entfällt das spätere
Umkopieren. Eine zweite Stufe (Einträge verdrängen, deren Inode niemand mehr hält) ist ein eigener Entwurf und nicht
Teil dieses Plans, weil sie die Objektidentität berührt.

- [ ] **Schritt 1: Cosmos-Test:** `InodeCache_ReadDirOf30000Entries_DoesNotGrowDictionary` (über ein internes Zählfeld oder `EnsureCapacity`-Rückgabe prüfbar; sonst als Speicherlauf im Gast).
- [ ] **Schritt 2:** Patch, Datei `patches/<Kurzname>.md`, Commit, PR gegen `cosmos`.
- [ ] **Schritt 3: Gast:** E4.0 `--diagnose-run 12` und der Lauf mit 20.000 Einträgen aus E2-P7. Erwartet: kein Allokationsfehler, `free` vor und nach dem Aushängen gleich.
- [ ] **Schritt 4: ZehOS:** nur Doku (`README.md:174`, `docs/COSMOS_SUPPORT.md:150-172`, `docs/STORAGE_3_0_97_FINDINGS.md` „Inode-Cache“), `COMMIT`/`VERSION`, PR.

> **Lead-Entscheidung 18.11.2026 (Lead Multicore):** Die erste Stufe ist angenommen. Kosten: `Dictionary(65_536)` rundet auf
> 75.431 Buckets, das sind rund 2,0 MiB je Mount (Review Jensen Huang); Laser hat die Kosten am 18.11.2026 akzeptiert (#375).
> Bedingung für Schritt 4: Die Grenze verschiebt sich nur. `README.md` und `docs/COSMOS_SUPPORT.md` nennen statt rund 17.500
> künftig „etwa 75.000 gecachte Inodes je Mount, darüber wie bisher“.

---

### Paket E2-P6: Feature-Prüfung und Lesemodus

**Cosmos-Branch:** `cosmos-patch/ext2-readonly` (Bahn B, zuletzt). Größter Patch der Reihe; vor dem Code steht eine
Entwurfsnotiz im PR, gegengelesen vom Storage-Lead.

**Dateien:** `Ext2Superblock.cs:183-303` (`TryCreate`, liest die Feature-Bits in `:219-221` nur), `Ext2FileSystemType.cs:38-54`
(reicht `flags` nicht weiter), `Ext2FileOperations.cs:243-260` (`Release` ruft immer `Fsync`), `Ext2Superblock.cs:757-771`
(`Drop` und `Flush` schreiben den Superblock).

**Änderung:**
- `TryCreate`: unbekannte incompat-Bits (alles außer FILETYPE `0x2`) → `false`. Unbekannte ro_compat-Bits (alles außer `sparse_super` `0x1`) oder unbekannte compat-Bits → Mount nur lesend.
- Neues Feld im Superblock, das `IVfsSuperblock.IsReadOnly` überschreibt; gesetzt auch durch `MountFlags.ReadOnly`, die `Ext2FileSystemType.TryMount` weiterreicht.
- Im Lesemodus schreiben `WriteBlocks`, `WriteInode`, `UpdateSuperblock`, `Fsync`, `Release`, `Drop` und `Flush` nichts.

**Was in ZehOS bleibt:** `Ext2FeatureGate` als Projektprofil (nur rev 1, 4-KiB-Blöcke, `first_data_block = 0`,
höchstens 128 GiB und 16.384 Gruppen, Einordnung von `s_state` für `InstalledIdentity` und `RootVolumeState`).
`CosmosReadOnlyVfs` bleibt, weil es auch FAT schützt (`Migration/MigrationSourceSwap.cs`). Entfallen können
`CosmosReadOnlyExt2FileSystem.cs` und `CosmosReadOnlyBlockDevice.cs`, wenn der Gastlauf zeigt, dass ein Lesemount von
Cosmos das Image nicht mehr verändert.

- [ ] **Schritt 1:** Entwurfsnotiz, Review durch den Storage-Lead.
- [ ] **Schritt 2: Cosmos-Tests** (`Ext2ReadOnlyTests.cs`): `Mount_UnknownIncompat_IsRefused`, `Mount_UnknownRoCompat_IsReadOnly`, `ReadOnlyMount_OpenReadClose_DoesNotChangeDevice` (Hash des RAM-Geräts vorher und nachher gleich), `ReadOnlyMount_CreateWriteUnlinkMkdirRename_AreRefused`, `ReadWriteMount_StillWorks`.
- [ ] **Schritt 3:** Patch, Datei `patches/<Kurzname>.md`, Commit, PR gegen `cosmos`.
- [ ] **Schritt 4: ZehOS** (Branch `task/mc-e2-p6-lesemodus`): `CosmosReadOnlyExt2FileSystem` auf den Cosmos-Lesemount umstellen oder entfernen; Aufrufer `CosmosLayoutProfile.cs`, `CosmosMigrationService.cs:126`, `CosmosExt2GuestProbe.cs:239-332` anpassen. Hosttests `Ext2FeatureGateTests`, `Ext2SupportedGeometryTests`, `RootVolumeStateTests` bleiben grün.
- [ ] **Schritt 5: Hostprobe:** `dotnet run --project experiments/StorageProbe/StorageProbe.csproj -c Release -- readonly-selftest`. **Gast:** kleine Probe, Lauf 2 (`ReadOnlyRun`): Image-SHA vorher und nachher gleich, Marken „Create, Delete, Mkdir, Rename ohne Wirkung“ und „Image unveraendert“; E4.0 Lauf 4.
- [ ] **Schritt 6:** Doku, Commit, Review durch die Storage-Arbeitsgruppe, PR.

> **Lead-Entscheidung 18.11.2026 (Lead Multicore):** Antworten auf die Fragen der Entwurfsnotiz:
> 1. Unbekannte compat-Bits, auch `ext_attr`, führen zum Lesemount. Das ist strenger als die Spezifikation, aber begründet:
>    Cosmos pflegt `resize_inode`, `dir_index` und `has_journal` nicht, `Unlink` gibt den `i_file_acl`-Block nicht frei und der
>    xattr-Refcount wird nicht gepflegt. Folge: Jedes mit `mke2fs -t ext2` erzeugte Image wird nur lesend eingehängt.
> 2. Neue Signatur von `TryCreate`: zulässig; der PR listet alle Aufrufer im Cosmos-Baum und in den Tests.
> 3. `Release` ruft `Fsync` nur im Lesemodus nicht mehr; im Schreibmodus bleibt alles gleich.
> 4. `large_file`: Ist das Bit nicht gesetzt, wird die Dateigröße auf 2 GiB − 1 begrenzt; ist es gesetzt, bleibt das Verhalten.
>    Der Superblock wird dabei nicht geändert. Der Test schreibt über 2 GiB − 1 auf ein Image ohne das Bit und ist ohne Fix rot.
> 5. `s_state` (`EXT2_VALID_FS`) beim Schreibmount löschen und beim Aushängen setzen: nicht Teil von P6, eigener Block.
> 6. `CosmosReadOnlyExt2FileSystem` entfällt nur, wenn `MigrationPlan` und `CosmosMigrationService` nicht mehr davon abhängen;
>    sonst bleibt der Adapter und wird auf den Cosmos-Lesemount umgestellt.
> 7. Ergänzung: `s_rev_level` über 1 führt zum Lesemount, wie `ext2_setup_super` in Linux.
>
> P6 wird nach E2-P8 (#375) gemergt. Die Lesung des Leads gilt als Gegenlesung nach Schritt 1 (Koordinator, 18.11.2026);
> den PR reviewt zusätzlich ein Engineer.

---

## Abschluss der Etappe

- [ ] `grep -rn "\[Plug" ProjektZeh/Storage` liefert keine Treffer.
- [ ] `ROADMAP.md`: datierter Absatz „ext2-Fehler in der Cosmos-Quelle behoben“ mit den `PATCHES.md`-Kurznamen und den Gastbelegen; die Zeilen zu den Plugs (#225, #230, Truncate-Plug) als historisch kennzeichnen.
- [ ] Inventar: alle ext2-Zeilen als erledigt oder mit dem verbliebenen Rest.
