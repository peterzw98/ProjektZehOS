# Plattenrate im Gast und parallele Gastläufe: Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Herausfinden, warum der Gast nur etwa 2 MB/s auf die Platte schreibt, das beheben und die langen Gastläufe (E4.0, E5-J) parallel im Hintergrund laufen lassen.

**Architecture:** Erst messen (Probe mit Zeitzählern im AHCI-Treiber und im Dateisystem), dann gezielt an der belegten Stelle ändern: AHCI-Befehlsgröße und Wartepfad im Cosmos-Branch `cosmos`, danach die Schreibmuster von ext2/FAT. Unabhängig davon die Testabläufe parallelisieren (`run-guest-suite.py`, Abbruchkopien in E4.0).

**Tech Stack:** Cosmos-Branch `cosmos` (C#, NativeAOT, Patches unter `patches/`), ZehOS-Probes per Buildschalter, Python-Gastskripte unter `scripts/`, QEMU mit KVM auf MSI.

**Spec:** Issue #542 (Peter, 20.11.2026) und die Messwerte darin. Die Messwerte sind die Schrittzeiten E4.0 von laser und `dd` auf dem Host vom Senior.

## Ausgangslage (gemessen bzw. gelesen am 20.11.2026)

| Wert | Quelle |
| --- | --- |
| E4.0 Lauf 1, Schritt l1 (2 GiB bis voll schreiben): ~900 s, also ~2,3 MB/s, gleich mit 1 und 4 CPUs | Kommentar laser an #542, KVM |
| E4.0 Lauf 1 gesamt ~2950 s; l2 mit Fix #531 ~700 s (Löschen der Fülldateien) | ebenda |
| Host `dd` auf MSI-NVMe: Schreiben 1,1 GB/s, Lesen 1,7 GB/s (direct I/O) | Senior, #542 |
| `AhciPort.MaxSectorsPerCommand = 8`: höchstens 4 KiB je Befehl, Bounce-Puffer eine Seite | `src/Cosmos.Kernel.Drivers/Pci/Storage/Ahci/AhciPort.cs:34`, `:92` |
| Jeder Befehl: Kopie in den Bounce-Puffer, ein Slot, Warten auf PxCI im Takt `Delay(10 µs)` | `AhciPort.cs:190-212`, `:350-385` |
| `Delay` auf x64 = 1 Lesezugriff auf Port 0x80 je µs; unter KVM ist jeder Zugriff ein VM-Exit | `X64PlatformInitializer.cs:99-106` |
| E4.0 Lauf 3/4: vier Abbruchverzögerungen strikt nacheinander, je eigene Boots | `scripts/test-storage-ext2-root.py:362`, laser an #542 |

Daraus folgt nur ein Verdacht, kein Beleg: Die Kosten je Befehl (VM-Exits für Port 0x80, MMIO-Abfragen, 4-KiB-Stücke) könnten dominieren, oder ext2 schreibt Metadaten je Datenblock mit. Paket M klärt das, bevor gebaut wird.

## Global Constraints

- Upstream (CosmosOS/Cosmos) wird nicht angefasst: keine Issues, keine PRs. Änderungen an Cosmos laufen als Patch im Branch `cosmos` mit Datei unter `patches/` und als PR gegen `cosmos`.
- Eine Cosmos-Versionsänderung in ZehOS (`deploy/cosmos/COMMIT`, `VERSION`, `global.json`) ist ein eigener PR und braucht die Freigabe von Peter.
- Schreibtests nur auf Kopien der Datenimages; `e2fsck -fn` beziehungsweise `fsck.fat -n` nach jedem Schreiblauf mit Ende 0.
- Datenintegrität geht vor Tempo: Jede Änderung im Schreibpfad wird mit Rücklesen gegen erwarteten Inhalt geprüft.
- Auf MSI höchstens 3 bauende Läufe gleichzeitig plus Reviewer; der Reviewer baut und testet nicht.
- Python-Skripte und Testabläufe landen auf `master` (Peter, 20.11.2026).

## Review Focus

1. **Größere AHCI-Befehle:** Ein Befehl über mehrere PRDT-Einträge bzw. einen größeren Bounce-Puffer darf an keiner Seitengrenze Daten vertauschen. Der Test schreibt ein Muster über 1 MiB mit ungerader Startsektor-Nummer und liest es zurück.
2. **Nicht ausgerichtete Länge:** Ein letzter Rest unter der neuen Befehlsgröße (z. B. 9 Sektoren bei Grenze 8 bzw. 129 bei Grenze 128) muss vollständig geschrieben werden. Gleicher Test mit Länge `Grenze + 1`.
3. **Fehler mitten in einem großen Befehl:** Ein Task-File-Fehler muss weiter als `IOException` ankommen und den Port frei geben (`Release`). Bestehender Fehlertest bleibt grün.
4. **Mehrere CPUs:** Zwei Threads schreiben gleichzeitig auf denselben Port (`Claim`/`Release`), Inhalt beider Dateien stimmt. Läuft im Gast mit `ZEHOS_QEMU_SMP=4`.
5. **Parallele Gastläufe:** Zwei Doom-Läufe mit gleicher ISO und gleichem SMP dürfen sich beim Snapshot nicht gegenseitig zerstören. Der Lauf in Paket P prüft das.

---

## Pakete und Parallelität

| Paket | Inhalt | Wer | Nach | parallel zu |
| --- | --- | --- | --- | --- |
| M | Messprobe: Rate je Dateisystem und Größe, Zähler im AHCI-Pfad | Senior (läuft) | – | P, A0 |
| A0 | QEMU-Seite prüfen: `cache=`, raw vs. qcow2, `aio=` | engineer | – | M, P |
| A | AHCI: größere Befehle, kürzerer Wartepfad | Senior | M | F |
| F | ext2/FAT: zusammenhängende Datenblöcke in einem Aufruf, Metadaten gebündelt | senior2 | M | A |
| P | Testabläufe parallel: `run-guest-suite.py` (#544), E4.0 Lauf 3/4 parallel | engineer2 / laser | – | M, A, F |
| Z | Pin, Abnahme E4.0 und E5-J vorher/nachher | engineer2 | A, F, P | – |

---

### Paket M: Messen (Probe)

**Files:**
- Create: `ProjektZeh/Storage/StorageRateProbe.cs` (nur mit `-p:ZehOSStorageRateProbe=true`)
- Modify: `ProjektZeh/ProjektZeh.csproj` (Schalter wie die übrigen Probes)
- Create: `scripts/test-storage-rate.py` (Aufbau wie `scripts/test-storage-ext2-guest.py`)
- Cosmos (Branch `cosmos-patch/ahci-stats`): `AhciState` zählt zusätzlich Sektoren und Zeit in `IssueCommand` (Stopwatch-Ticks bis PxCI frei) und in `Claim`

**Interfaces:**
- Produces, seriell, eine Zeile je Schritt: `STORAGE_RATE <fs> <op> <file_bytes> <files> ms=<n> mbps=<n.n>`; `fs` = `ext2|fat`, `op` = `write|read|delete`.
- Produces, nach jedem Schritt: `AHCI_STATS commands=<n> sectors=<n> issue_ms=<n> wait_ms=<n> claim_ms=<n>` (Differenz zum vorigen Schritt).
- Produces: `STORAGE_RATE DONE`.

- [ ] **Schritt 1:** Cosmos-Zähler in `AhciState` (`CountCommand(uint sectors, long issueTicks, long waitTicks)`); Lesen über eine vorhandene Diagnose-Schnittstelle oder eine neue `AhciState.Snapshot()`. Cosmos-Hosttests grün.
- [ ] **Schritt 2:** Probe: je Dateisystem 1 Datei 256 MiB schreiben, lesen, löschen; 1000 Dateien à 64 KiB in einem Verzeichnis schreiben, lesen, löschen. Inhalt deterministisch, Rücklesen prüft ihn.
- [ ] **Schritt 3:** Gast KVM, `ZEHOS_QEMU_SMP` leer und `4`, je ein Lauf, Datenimage als Kopie, raw und qcow2.
- [ ] **Schritt 4:** Ergebnis-Tabelle an #542: MB/s je Zeile, Anteil `wait_ms` und `issue_ms` an der Schrittzeit, Sektoren je Befehl im Mittel. **Entscheidungspunkt:** Liegt der Großteil der Zeit im AHCI-Pfad, geht Paket A vor; liegt er außerhalb, geht Paket F vor.

### Paket A0: QEMU-Seite

**Files:** `scripts/test-wasm-doom-desktop.py` (`start_qemu`, Laufwerksoptionen), `scripts/test-storage-ext2-root.py`

- [ ] **Schritt 1:** Aktuelle `-drive`-Optionen der Speichertests auflisten (Format, `cache`, `aio`, `discard`).
- [ ] **Schritt 2:** Probe aus M einmal mit `cache=unsafe` auf einer Kopie (nur Messung, nicht für Abnahmeläufe) und mit raw statt qcow2. Unterschied an #542.
- [ ] **Schritt 3:** Ergibt sich ein Faktor > 2 allein aus QEMU-Optionen, Vorschlag an Lead, welche Option für welche Testart gilt (Integritätsläufe behalten sichere Optionen).

### Paket A: AHCI-Befehle (Cosmos)

**Files:** `src/Cosmos.Kernel.Drivers/Pci/Storage/Ahci/AhciPort.cs` (`MaxSectorsPerCommand`, `_bounce`, `IssueCommand`, `ReadBlock`, `WriteBlock`), `AhciProtocol.cs` (PRDT), Tests im Cosmos-Testkern für Speicher, `patches/ahci-large-commands.md`

**Interfaces:**
- Consumes: Zähler aus M.
- Produces: unveränderte `IBlockDevice`-Schnittstelle.

- [ ] **Schritt 1:** Test im Cosmos-Speichertestkern: Muster schreiben und lesen für Längen 1, 8, 9, 128, 129, 2048 Sektoren an ungeraden Start-LBAs (Review Focus 1, 2). Gegen den alten Code grün; er dient als Schutz.
- [ ] **Schritt 2:** `MaxSectorsPerCommand` auf 128 (64 KiB) mit einem Bounce-Puffer dieser Größe (zusammenhängend über `AllocateDma`), PRDT-Länge passend. Bei `ByteCountAndInterrupt` die Grenze der PRDT-Bytezahl (4 MiB) beachten.
- [ ] **Schritt 3:** Wartepfad: PxCI zuerst ohne `Delay` einige Male lesen, erst danach mit `Delay`. Dazu messen, wie lange ein `Delay(10)` unter KVM wirklich dauert (Zähler aus M).
- [ ] **Schritt 4:** Gast: Probe M vorher/nachher, KVM 1 und 4 CPUs; Fehlertest (Review Focus 3) und zwei schreibende Threads (Review Focus 4). `e2fsck -fn` Ende 0.
- [ ] **Schritt 5:** Review, PR gegen `cosmos`; danach Pin-PR in ZehOS (Freigabe Peter).

### Paket F: Dateisystem-Schreibmuster (Cosmos)

**Files:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2FileOperations.cs`, `Ext2IndirectBlocks.cs`, `Ext2Superblock.cs` (`WriteBlocks` `:440-458`), FAT-Gegenstück unter `FileSystem/Fat/`, `patches/fs-coalesce-writes.md`

- [ ] **Schritt 1:** Mit den Zählern aus M für die 256-MiB-Datei ermitteln: Aufrufe von `WriteBlock` je MiB Nutzdaten und Anteil Metadaten (Bitmap, Gruppendeskriptor, Inode, Superblock).
- [ ] **Schritt 2:** Wenn Datenblöcke einzeln geschrieben werden: zusammenhängende Blöcke einer Datei in einem `WriteBlock` zusammenfassen. Wenn Metadaten je Block geschrieben werden: je Schreibaufruf einmal am Ende.
- [ ] **Schritt 3:** Integritätstests: bestehende ext2/FAT-Testkerne in Cosmos, ZehOS `scripts/test-storage-ext2-guest.py` und `test-storage-filesystems.py` auf Kopien, E4.0 Lauf 1 mit `e2fsck -fn` Ende 0.
- [ ] **Schritt 4:** Löschen (l2 ~700 s) gesondert messen; nur wenn es das gleiche Muster zeigt, hier mit beheben, sonst eigener Block.
- [ ] **Schritt 5:** Review, PR gegen `cosmos`.

### Paket P: Gastläufe parallel und im Hintergrund

**Files:** `scripts/run-guest-suite.py`, `scripts/guest-suites/e5j.json` (PR #544, Branch `feature/guest-suite`), `scripts/test-storage-ext2-root.py` (`main`, Schleife über `ABORT_DELAYS` `:362`)

- [ ] **Schritt 1:** #544 auf MSI mit der E5-J-Liste (engineer2): echte Pfade in `e5j.json`, `--detach`, `--status`. Gesamtdauer vorher/nachher an #544; Snapshot-Fall aus Review Focus 5.
- [ ] **Schritt 2:** E4.0 Lauf 3/4: die vier Abbruchverzögerungen in eigenen Arbeitsordnern parallel (`concurrent.futures`, Grenze `--parallel`), Ergebnisse in fester Reihenfolge ausgeben. Abstimmung mit laser vorher an #542 (deren Skript und Spec); Hosttest für die Reihenfolge der Ausgabe.
- [ ] **Schritt 3:** E4.0 als Suite-Datei `scripts/guest-suites/e4-0.json` (Läufe 1–2 nacheinander, 3/4 parallel, 5), damit sie per `--detach` läuft.
- [ ] **Schritt 4:** ISO-Wiederverwendung: in der Suite-Doku festhalten, dass je Kopfstand jede Probe-ISO einmal gebaut und per `--var` übergeben wird.
- [ ] **Schritt 5:** PRs gegen `master`.

### Paket Z: Pin und Abnahme

- [ ] **Schritt 1:** Pin-PR auf den `cosmos`-Kopf mit A (und F) in `task/mc-e5-smp`; Freigabe Peter.
- [ ] **Schritt 2:** Probe M, E4.0 Lauf 1 und die E5-J-Suite auf dem neuen Pin über `run-guest-suite.py`. Vergleich mit den Ausgangswerten oben.
- [ ] **Schritt 3:** Ergebnis in `docs/COSMOS_SUPPORT.md` (Abschnitt Speicherrate) und an #542.

**Abnahme (Peter, 20.11.2026):**
- Schreibrate im Gast mindestens zehnmal so hoch wie gemessen (gemessen ~2,3 MB/s, also mindestens ~23 MB/s; l1 höchstens 90 s). Ziel sind 100 MB/s.
- E4.0 Lauf 1 unter 15 min.
- E5-J-Suite in Wanduhrzeit unter 45 min.
- Kein Integritätsfehler, `e2fsck` Ende 0 in allen Läufen.

## Offene Fragen an Peter

1. Darf E4.0 Lauf 3/4 im Skript von laser parallelisiert werden, oder macht laser das selbst?
