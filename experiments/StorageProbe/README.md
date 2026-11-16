# StorageProbe: Cosmos-3.0.97-ext2 auf dem Host prüfen

Formatiert ein neues, dünn belegtes Rohimage (eine Partition ohne Partitionstabelle) mit dem `Ext2Formatter` aus
`Cosmos.Kernel.System` 3.0.97 (NuGet, `internal`, per Reflection) und prüft es mit e2fsprogs. Kein Kernelbuild.

```bash
dotnet run -c Release --project experiments/StorageProbe -- format IMAGE --size BYTES --block-size 1024|2048|4096 [--label L]
experiments/StorageProbe/matrix.sh .tools/storage-probe
```

`format` legt `IMAGE` mit `FileMode.CreateNew` an (eine vorhandene Datei wird nie überschrieben) und gibt eine
JSON-Zeile aus. Das Dateigerät hat 512-Byte-Sektoren und prüft jeden Blockzugriff. `matrix.sh` löscht nur die
eigenen Images und behält die e2fsck-Ausgabe.

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
