# ahci-flush-timeout

- **Commit:** auf `story/ladybird-cosmos` (Branch `task/lb-ahci-flush`), Hash nach dem Merge nachtragen
- **Dateien:** `src/Cosmos.Kernel.Drivers/Pci/Storage/Ahci/AhciState.cs` (`FlushTimeoutMilliseconds`), `src/Cosmos.Kernel.Drivers/Pci/Storage/Ahci/AhciPort.cs` (Zeitlimit in `IssueCommand`, Kommentar an `Claim`)
- **Grund:** `IssueCommand` gab jedem Befehl 5 s (`CommandTimeoutMilliseconds`), auch FLUSH CACHE EXT. Ein Flush schreibt den ganzen flüchtigen Cache zurück; unter QEMU mit qcow2 und `cache=writeback` ist das ein `fdatasync` auf dem Host. Unter Host-Last dauerte er mehr als 5 s: ZehOS-Gastläufe auf MSI brachen beim ersten `FileService.WriteText` nach dem Start (Paketkatalog, ein Schreibvorgang mit `Flush(true)`) mit `SATA: command completion timeout` ab, auch mit nur einem Gast (Analyse agent-peter-engineer, 27. November 2026; bestandene Läufe hielten die Sperre dort schon 4,6 bis 5,1 s). Die Folge war ein Geräte-Reset mitten im Schreiben und ein Fehler beim Login.
- **Änderung:** FLUSH CACHE EXT bekommt 30 s (`FlushTimeoutMilliseconds`), wie das obere Flush-Zeitlimit von Linux libata (`ata_eh_flush_timeouts` 15/15/30 s). Lesen und Schreiben behalten 5 s, die Wartezeit auf BSY/DRQ vor dem Befehl (1 s) und die Budgets der Wiederherstellung bleiben gleich.
- **Grenzen:** Ein hängender Flush hält den Port und damit alle Wartenden in `Claim` jetzt bis zu 32 bis 33 s statt 7 bis 8 s; in ZehOS liegt das unter `StorageLock`. Lese- und Schreibbefehle unter Host-Last sind nicht verlängert; trifft es sie, braucht es einen eigenen Befund. ATAPI hat keinen Flush und ist unverändert.
- **Entfernter ZehOS-Umweg:** keiner (Testläufe nutzten als Umgehung eine Datenplatte mit `cache=unsafe`)
- **Prüfung:** siehe PR (nativer Bau, Gastlauf)
