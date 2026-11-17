# StorageProbe: Cosmos-3.0.97-ext2 auf dem Host prüfen

Formatiert ein neues, dünn belegtes Rohimage (eine Partition ohne Partitionstabelle) mit dem `Ext2Formatter` aus
`Cosmos.Kernel.System` 3.0.97 (NuGet, `internal`, per Reflection) und prüft es mit e2fsprogs. Kein Kernelbuild.

```bash
dotnet run -c Release --project experiments/StorageProbe -- selftest
dotnet run -c Release --project experiments/StorageProbe -- format IMAGE --size BYTES --block-size 1024|2048|4096 [--label L]
experiments/StorageProbe/matrix.sh .tools/storage-probe [original|fixed]
dotnet run -c Release --project experiments/StorageProbe -- layout IMAGE --size BYTES [--guid-seed HEX]
dotnet run -c Release --project experiments/StorageProbe -- verify IMAGE
python3 scripts/test-installer-layout.py .tools/installer-layout
```

`layout` (Etappe 7a Schritt 2) partitioniert ein neues sparse Image mit dem produktiven `GptLayoutWriter`
(32 bis 128 GiB, sonst keine Datei), formatiert die Root-Partition mit `CosmosExt2Formatter.FormatForKernel`
(4 KiB, `zehos-root`) und prüft mit `GptLayout.Verify`; `verify` prüft ein vorhandenes Image nur lesend.
`scripts/test-installer-layout.py` prüft das Ergebnis mit `sgdisk -v`, `sfdisk -J`, `mkfs.fat`/`fsck.fat` im
ESP-Fenster (Grenze: das belegt nur Lage und Größe der ESP, nicht ZehOS' eigenen FAT-Formatter; der ist nicht
hosttauglich und bleibt Gasttest G1), `e2fsck -fn` und `dumpe2fs -h` im Root-Fenster sowie Gegenproben mit zerstörtem Byte; fehlende
Werkzeuge gelten als offen.

`--formatter fixed` (bzw. `matrix.sh … fixed`) formatiert mit der korrigierten gemeinsamen Quelle
[ProjektZeh/Storage/Ext2](../../ProjektZeh/Storage/Ext2/README.md) statt mit dem Paketformatter; Standard bleibt
`original`. `selftest` prüft zusätzlich das Layout der korrigierten Quelle an einem 16-MiB-/1K-Image.

Die Cosmos-Pakete kommen über `PackageDownload` (genau `[3.0.97]`, ohne Cosmos-Build-Props); die Probe baut
deshalb auch ohne vorherigen Kernel-Restore (mit leerem NuGet-Ordner geprüft).

`format` prüft alle Argumente, bevor eine Datei entsteht: Optionen paarweise, Größe positives Vielfaches von 512,
Blockgröße 1024, 2048 oder 4096; unter Windows höchstens 64 MiB, solange die sparse Anlage dort nicht geprüft ist.
Das Image wird mit `FileMode.CreateNew` angelegt; eine vorhandene Datei bleibt unverändert (Exitcode 3). Ausgabe
ist eine JSON-Zeile, Exitcode 0 nur bei erfolgreichem Format. Das Dateigerät hat 512-Byte-Sektoren und lehnt
Zugriffe hinter dem Ende, zu kleine Puffer und Überläufe ab. `selftest` prüft diese Grenzen und die
Argumentprüfung ohne Cosmos-Aufruf.

`matrix.sh` legt je Lauf einen neuen Ordner (`mktemp -d`) im Arbeitsordner an, bricht bei einem Formatfehler ohne
Löschen ab und löscht nur die Images, die es in diesem Ordner selbst erzeugt hat (große sparse Images belegen
mehrere GB); die e2fsck-Ausgaben bleiben liegen.

Exit 0 des Matrixrunners bedeutet: alle Formate und alle Konsistenzprüfungen erfolgreich. Bei einem
`e2fsck`-Fehler werden die übrigen Fälle noch erfasst, der gesamte Lauf endet dann mit Exit 1.
Formatfehler sowie fehlende oder unbrauchbare `dumpe2fs`-Header brechen sofort mit Exit 1 ab.

## Ergebnis 17. November 2026 (Linux, e2fsprogs 1.47.4): NO-GO

Host: Linux 7.2.9, .NET SDK 10.0.112, e2fsprogs 1.47.4, x86-64, Cosmos.Kernel.System 3.0.97 aus NuGet. Genaue Matrix, `dumpe2fs -h` und e2fsck-Meldungen: [results-2026-11-17.md](results-2026-11-17.md).

Alle 18 Fälle (4 MiB, 8 MiB, 64 MiB, 32/64/128 GiB je 1K/2K/4K) formatieren ohne Fehler (7 ms bis 2 s), aber
`e2fsck -fn` endet immer mit Exit 4 (Fehler bleiben). Ursachen laut Quelle (`Ext2Formatter.cs`, Tag v3.0.97):

1. Keine Backup-Superblöcke und Gruppendeskriptoren, aber auch kein `sparse_super`. Ab zwei Gruppen erwartet ext2
   in jeder Gruppe ein Backup am Gruppenanfang; Cosmos legt dort Block- und Inode-Bitmap ab (Zeilen 183–206).
   e2fsck: „Group N's block bitmap at … conflicts with some other fs block“ in jeder Gruppe.
2. 1-KiB-Blöcke: `first_data_block` ist 1, die Gruppen beginnen aber bei `g * blocks_per_group` statt
   `1 + g * blocks_per_group`. Auch bei einer Gruppe meldet e2fsck eine Bitmap-Differenz (Block 134 bei 4 MiB).
3. Die Füllbits am Ende von Block- und Inode-Bitmap sind nicht gesetzt (alle Größen und Blockgrößen).

`dumpe2fs -h`: Revision 1, einziges Merkmal `filetype`, Inode 128 Byte, 8 MiB je Gruppe. Folgerung: Cosmos-ext2
nicht für Gastdatenträger freigeben; ein Upstream-Fix braucht `sparse_super` mit Backups in Gruppe 0, 1 und den
Potenzen von 3, 5 und 7, den korrekten Gruppenanfang bei 1 KiB und gesetzte Füllbits.
