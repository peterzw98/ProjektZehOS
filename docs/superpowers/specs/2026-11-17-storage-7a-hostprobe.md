# Storage 7a, Schritt 2: Hostprobe-Spezifikation (ohne Code)

Stand: 17. November 2026. Ergänzt [Installer-Layout](2026-11-17-storage-7a-installer-layout.md) §7 „Host-Gegenprüfung“. **Nur Spezifikation**; Code erst, wenn senior Schritt 1 (`GptLayoutWriter`, `GptLayout.Verify`, `Crc32` mit Hosttests) gemeldet hat. Keine Installeraktivierung.

## 1. Zweck und Beweisregel

Beweist auf dem Host mit **fremden Werkzeugen**, dass der **produktive** Writer eine gültige Platte erzeugt (Tabelle + Backup, FAT32-ESP, ext2-Root). Kein Python-Spiegel: Python ruft auf und wertet aus, kennt keine Tabellenbytes. Die Probe belegt nicht: OVMF-/Limine-Boot, Cosmos-Mount, `RescanPartitions` (das bleibt Gasttest G1).

## 2. Bauteile

- **`experiments/StorageProbe`** (vorhanden; bindet bereits `ProjektZeh/Storage/Ext2/*.cs` per Compile-Include ein) bekommt den Unterbefehl `layout IMAGE --size BYTES` und `--guid-seed HEX` (fester Seed für reproduzierbare Läufe; ohne Seed `RandomNumberGenerator`). Eingebunden werden die **produktiven** Quellen `GptLayoutWriter`, `GptLayout` (Verify), `Crc32`; Quellen (Schritt 1, `task/storage-7a-gpt` a8eb2c5): `ProjektZeh/Storage/Partitioning/Crc32.cs`, `GptLayout.cs`, `GptLayoutWriter.cs`, Namespace `ProjektZeh.Storage.Partitioning`, alle `internal`, per Compile-Include (wie die Ext2-Quellen) und `using ProjektZeh.Shared` für `ResultObject<T>` (`ResultCode`/`ResultObjectFactory` zusätzlich einbinden). Aufruf: `GptLayoutWriter.Write(blockSize, blockCount, Action<byte[]> random, BlockWriter write, Action flush)` und `GptLayout.Verify(blockSize, blockCount, BlockReader read)`; `random` = fester Seed (`--guid-seed`) bzw. `RandomNumberGenerator.Fill`; `BlockWriter`/`BlockReader` über das Dateigerät. GPT-Namen der Einträge `ZEHOS-BOOT`/`ZEHOS-ROOT` (Dateisystemlabel der ESP bleibt `ZEHOSBOOT`).
- **Dateigerät** der Probe (vorhanden: 512 B, lehnt Zugriffe hinter dem Ende ab) wird als Ziel des Writers und des ext2-Formatters verwendet; für ESP und Root ein **Offset-/Längenfenster** über dem Gerät (die Probe formatiert die Partitionen, nicht das Rohimage). Kein Cosmos-Aufruf außer dem schon eingebundenen korrigierten `CosmosExt2Formatter`.
- **FAT32-ESP:** Cosmos-FAT-Formatter ist nicht hosttauglich; die Probe formatiert die ESP **nicht** selbst. Zwei Varianten, Entscheid für Schritt 2: (A) `mkfs.fat -F 32 -n ZEHOSBOOT` auf das ESP-Fenster (Loop-frei per `--offset` bzw. `mkfs.fat` auf Dateiausschnitt, sonst `mtools`/`mformat -i image@@offset`), prüft nur die **Lage** (LBA/Größe), nicht den Cosmos-FAT-Formatter; (B) ESP in der Probe leer lassen und nur die Tabelle prüfen. Vorschlag: **(A)**, ausdrücklich als „Lage/Größe der ESP, nicht Cosmos-FAT“ im Bericht.
- **`scripts/test-installer-layout.py`:** ruft die Probe auf, wertet aus. Muster wie `scripts/test-storage-filesystems.py`/`experiments/StorageProbe/matrix.sh` (Lauf in neuem `mktemp -d`-Ordner, löscht nur selbst erzeugte Images).

## 3. Ablauf je Größe

Größen (sparse, immer **neues** Image, existierender Pfad ⇒ Abbruch Exit 3 wie `format`): 32 GiB, 64 GiB, 128 GiB (Zielumfang) und zur Grenze 32 GiB − 512 B, 128 GiB + 512 B (verweigert: kein Image, Exit ≠ 0, Datei nicht angelegt).

1. Probe `layout` schreibt Schutz-MBR, GPT (Primär + Backup), formatiert Root-Fenster (ext2, 4 KiB, Label `zehos-root`), (A) ESP-Fenster.
2. Auswertung (jede Prüfung einzeln, Exit 0 nur wenn **alle** grün; fehlendes Werkzeug ⇒ „offen“, nie „bestanden“):

| Werkzeug | Prüfung |
|---|---|
| `sgdisk -v` | Tabelle und Backup gültig, keine Warnungen („No problems found“) |
| `sfdisk -d` / `sfdisk -l` | zwei Partitionen, Typen `C12A7328-…`/`0FC63DAF-…`, 1-MiB-ausgerichtet (Start durch 2048 teilbar), Root-Länge Vielfaches von 8 Sektoren, Root-Ende = `LastUsable` abgerundet auf 4 KiB |
| `sgdisk -i1/-i2` | Unique-GUIDs paarweise verschieden, ≠ Nil, v4-Bits (`[14]=='4'`, Variantenbits), Disk-GUID (`sgdisk -p`) verschieden |
| `fsck.fat -n` | ESP im Fenster fehlerfrei (Variante A) |
| `e2fsck -fn` | Root im Fenster Exit 0 (bei 128 GiB sparse; Dauer protokollieren) |
| eigene Byteprüfung (ohne Tabelle nachzubauen) | Schutz-MBR: genau ein Eintrag `0xEE`, Start 1, Größe `min(N-1,0xFFFFFFFF)`, andere drei Einträge 0, `55AA`; Prüfung der **Tabellenbytes macht `GptLayout.Verify` in C#**, Python nur Aufruf (`verify`-Unterbefehl der Probe) |
| Gegenprobe | je ein Lauf mit gezielt zerstörtem Byte (Primär-CRC, Backup-CRC, Tabellen-CRC, Eintrag außerhalb Usable) ⇒ `sgdisk -v` **und** `Verify` melden Fehler (beweist, dass Verify nicht „alles grün“ liefert) |
| Wiederholung | zwei Läufe mit gleichem `--guid-seed` ⇒ byteidentische Tabelle (Reproduzierbarkeit); ohne Seed ⇒ verschiedene GUIDs |

3. Marker: Die Probe schreibt zusätzlich `boot/.zehos-install.id` in das ESP-Fenster (Variante A, `mcopy`) mit dem **produktiven** Marker-Format (senior legt es mit Schritt 1/3 fest) und ruft `InstalledIdentity.Evaluate` auf der Platte: `Healthy`; danach Gegenproben **auf Kopien**: Root-Eintrag gelöscht ⇒ `RootMissing`, Root-Magic zerstört ⇒ `RootDamaged`, Marker gelöscht bei vorhandener Root ⇒ `Unclear`, Root-Größe ±1 Block ⇒ `RootDamaged`. Das deckt die Identitätstabelle am echten Plattenabbild ab (Host, ohne Gast).

## 4. Sicherheit und Betrieb

- **Nie ein vorhandenes Image, nie ein Blockgerät als Ziel** (Probe lehnt Pfade außerhalb des Arbeitsordners ab, existierender Pfad = Abbruch); kein `losetup`/`mount`, kein `sudo`. Alle Werkzeuge arbeiten auf Datei und Offset.
- **Speicher/Platz:** 128 GiB sparse belegen wenig, `e2fsck` liest Metadaten; trotzdem höchstens ein Lauf gleichzeitig (Clerk-Regel), Ordner nach dem Lauf entfernt, `e2fsck`-/`sgdisk`-Ausgaben bleiben im Laufordner.
- **Wer führt aus:** ein Clerk (nuc hat kein `dotnet`/`sgdisk`/`e2fsck`); Auftrag nennt Branch und Commit. Ergebnisse als Tabelle in `experiments/StorageProbe/results-<datum>.md`, Exit ≠ 0 bei jeder roten oder offenen Prüfung.
- **Nicht Teil der Probe:** Gast, OVMF, Limine, VMware, `disk list` nach Rescan (alle G1–G7).

## 5. Geklärte und offene Punkte

1. ~~Typnamen~~ geklärt (siehe §2).
2. ESP-Variante A (`mkfs.fat`/`mtools` auf Offset) vorgeschlagen; Werkzeugliste auf dem Clerk-Rechner (`sgdisk` aus `gdisk`, `dosfstools`, `mtools`, `e2fsprogs`) von einem Clerk zu bestätigen; fehlt eines, meldet das Skript „offen“.
3. `InstalledIdentity.Evaluate` und das Marker-Format gibt es in Schritt 1 **nicht**. Die Identitäts-Gegenproben (§3 Punkt 3) entfallen für Schritt 2 und kommen, sobald senior `InstalledIdentity` liefert; Schritt 2 deckt Writer, `Verify`, Schutz-MBR, ESP-Lage und ext2-Root ab.
4. Schreibreihenfolge im Code (Löschen 0..2047 + letzte 33 Sektoren, dann Backup, Primärkopf, Schutz-MBR zuletzt) weicht von der Spec nur im Detail ab (LBA 1 wird im ersten Löschschritt genullt); die Probe prüft das Ergebnis, nicht die Reihenfolge (die prüft der Hosttest mit Zähl-Fake).
