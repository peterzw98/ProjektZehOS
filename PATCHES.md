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
| ext2-formatter | Squash von #<PR-Nr> | `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Formatter.cs` (`Format`) | `Format` schrieb kein `sparse_super` und keine Backup-Superblöcke, rechnete Gruppenanfänge ohne `first_data_block` und ließ die Füllbits der Bitmaps frei (e2fsck-Fehler); eine letzte Gruppe ohne Platz für ihre Metadaten lehnte das ganze Format ab. **`Format(null)` formatiert jetzt mit 4096 (vorher 1024). Format mit ausdrücklich 1024 erzeugt ein korrektes Layout, das der Treiber beim Schreiben falsch adressiert (`Ext2Superblock` ohne `FirstDataBlock`); nur 4096 ist qualifiziert.** Nicht hier behoben (Bahn B): die schreibenden Smoketests laufen mit 4096, die 1-KiB-Varianten bleiben als `[Ignore]` sichtbar | `CosmosExt2FormatterPlug`; die kopierte Formatter-Quelle `CosmosExt2Formatter.Format` (fällt im ZehOS-PR, `FormatForKernel` bleibt als dünne Hülle mit 4096-Byte- und 128-GiB-Grenze) | NUnit `Ext2FormatterTests` (6 Fälle, die ersten 5 vor dem Patch rot), Ext2 gesamt 10/10 grün; Hostprobe und Gast folgen im ZehOS-PR |
