# Ladybird N2: Prozesse und statische Programme (Umriss)

> Umriss. Pakete werden nach dem [Tor N1-Z](2026-11-24-ladybird-n1-nachweise.md#n1-z-tor) geschnitten. Grundlage: [Entwurf](../specs/2026-11-24-native-pakete-design.md), Abschnitte 4 bis 6; [Übersicht](2026-11-24-ladybird-nativ.md).

**Ziel:** Im Terminal startet ein statisches `x86_64-linux-musl`-Programm aus einem Testordner und endet sauber.

| Bahn | Inhalt | Dateien |
| --- | --- | --- |
| Cosmos | Patches aus N1-A und N1-B endgültig, mit `patches/*.md`, ohne Probecode | Branch `cosmos` |
| Lader | ELF64 statisch und statisch-PIE, Startstack mit `auxv`; Parser mit Hosttests und Fuzz (feste Saat) | `ProjektZeh/Processes/Elf*.cs` |
| Prozess | Prozess, Deskriptortabelle, Ende, Abbruch, Speichergrenze, Fehlermeldung | `ProjektZeh/Processes/` |
| Aufrufe | Satz S1, Kopierweg über HHDM, Hosttests je Aufruf | `ProjektZeh/Processes/Syscalls/`, `docs/LINUX_ABI.md` |
| Dateien, Terminal | Wurzel des Prozesses, `/dev/*`, Ein- und Ausgabe, Befehl `run <datei>` nur im Testbau | `ProjektZeh/Processes/Vfs/`, `ProjektZeh/Shell/` |
| Prüfung | Gastskript spielt N1-E-Programme ein und vergleicht mit `expected/` | `scripts/test-linux-abi.py` |

Lader, Aufrufe und Dateien laufen nebeneinander, sobald der Prozesstyp steht. **Abnahme:** Tor N2-Z. **Risiko:** Sperrreihenfolge zwischen Prozess, VFS und `StorageLock`; Abbruch, während ein Thread im Kernel wartet.
