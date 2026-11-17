# Etappe 4.0: RW-Qualifikation ext2 als `/` – Prüfentwurf

Stand: 17. November 2026, Basis `origin/master`. Paket 4.0 aus [Etappe 4](2026-11-17-storage-etappe4-design.md#4-arbeitspakete): **ohne Go kein `Root`-Profil** (4.3 hängt daran). Dieses Dokument legt Gastprobe, Hostskript, Abläufe und Go/No-Go fest. **Nur Entwurf**, kein Code, nichts gelaufen. Grundlage ist die Etappe-1-Probe `CosmosExt2GuestProbe` (72 MiB, `sata1`, zwei Läufe, `scripts/test-storage-ext2-guest.py`); sie bleibt unverändert. Reviews: peter-review und huang zu `d41dd08` eingearbeitet, Leitungsentscheide 11:56–11:59.

## 1. Was qualifiziert wird – und was nicht

Der Cosmos-3.0.97-ext2-Treiber mit den ZehOS-Plugs (`FormatForKernel`, `FreeInode`) als schreibbares `/` unter Dauerlast, einschließlich der ZehOS-Eigenleistungen aus Etappe 4 §2: `s_state` = 0 nach dem RW-Mount **mit `Device.Flush()` vor dem ersten Datenschreibzugriff**, beim Herunterfahren Sync, Sync, `s_state` = 1 per Lesen-Ändern-Schreiben, eigenes `Device.Flush()`.

**Kein Beleg für Stromausfall.** Der harte Abbruch ist ein `kill` des QEMU-Prozesses: der Host-Seitencache bleibt erhalten, vom Gast geschriebene Daten gehen nicht verloren, und die Wirksamkeit der Flush-Barrieren wird damit **nicht** geprüft. Ein Stromausfall mit flüchtigem Plattencache wäre nur über ein Schreibprotokoll mit Wiedergabe bis zu einer Flush-Grenze (z. B. `blklogwrites`) prüfbar – nicht Teil von 4.0. Ebenso nicht qualifiziert: Installer, Migration, Profilwahl (4.3–4.5), VMware (eigener Lauf später).

## 2. Gastprobe `CosmosExt2RootProbe`

- Eigener Buildschalter `-p:ZehOSStorageRootProbe=true` (`ZEHOS_STORAGE_ROOT_PROBE`), Einhängepunkt im `Kernel.cs` wie die vorhandenen Proben (`#if … if (!Probe.Run()) { Stop(); return; }`). Kein normaler Build enthält ihn.
- **Root-Platte:** `sata1`, Größe **exakt** 32 GiB oder 128 GiB (512-Byte-Sektoren), sonst Abbruch ohne Schreibzugriff. Lauf 1 nur auf einer Platte, deren erstes MiB vollständig null ist; Folgeläufe nur, wenn der Superblock den Volumenamen `zehos-rootprobe` trägt.
- **Zusatzplatte:** `sata2`, **exakt** 1 GiB, gleiche Schutzregel mit Volumename `zehos-inodeprobe`, nur für die Inode-Erschöpfung (Schritt i2).
- **Format:** `CosmosExt2Formatter.FormatForKernel` (4-KiB-Blöcke, `sparse_super`), danach `Ext2FeatureGate` = `Ok`.
- **Mounts wie das Produkt:** ext2 schreibbar auf `/`; Verzeichnis `/boot/efi` anlegen und die FAT-Platte (`sata0`) dort einhängen (Etappe 4 §2), nicht unter `/data`. Damit sind ein verschachtelter Mount und der längste Treffer (Etappe 3) mitgeprüft.
- **Zwei Zugriffswege:** `System.IO` (wie Cosmos-Aufrufer) und der ZehOS-`FileService` mit `CosmosPathGuard.For("/")`; jeder Lastschritt nennt, welchen Weg er nutzt.
- **Deterministischer Inhalt:** jede Integritätsdatei hat den Inhalt `Generate(saat, pfad, länge)` (SHA-256-Kette über Saat und Pfad). Die Erwartung wird in jedem Lauf **neu berechnet**, nie aus einer Datei im geprüften Dateisystem gelesen (SHA-256 ist im Kernel vorhanden, `CosmosSecurityProbe`). Füll-Dateien für ENOSPC bestehen aus **Nullen** (siehe §4, qcow2).
- **Ausgabe:** seriell `STORAGE_ROOT <schritt> PASS|FAIL <detail>`, am Ende `STORAGE_ROOT DONE`; nach `DONE` beendet sich der Gast selbst. In Lauf 3 zusätzlich `STORAGE_ROOT WRITING` (Zeitbezug für den Abbruch) und `STORAGE_ROOT FLUSHED-STATE` (nach `s_state` = 0 und `Device.Flush()`, vor dem ersten Datenschreibzugriff).
- **Ausnahmen:** `IOException` bzw. ein Fehler-`ResultCode` sind erwartete Fehlerwege; jeder andere Ausnahmetyp (`NullReferenceException`, `IndexOutOfRangeException`, `InvalidOperationException` …) ist FAIL.
- **Superblockzähler:** nach jeder Phase `s_free_blocks_count`, `s_free_inodes_count`, `s_r_blocks_count`, `s_state` seriell ausgeben; der Host vergleicht sie mit `dumpe2fs -h` am Image (§4).

## 3. Abläufe

Je Größe (32 GiB, 128 GiB) ein frisches Image im neuen Arbeitsordner (`new_work_dir`, CreateNew), Beschleunigung KVM. Nach **jedem** Lauf hostseitig `e2fsck -fn` und `dumpe2fs -h` auf dem Image.

**Lauf 1 – frisch, Last, sauberes Herunterfahren**

| Schritt | Inhalt (Weg) | PASS-Bedingung |
|---|---|---|
| a | Format, Gate, Mount `/`, `/boot/efi` daneben | Gate `Ok`, beide Mounts sichtbar, `/boot/efi` über `/` erreichbar, `FileService` lehnt `/boot/efi/...` als fremden Mount ab |
| b | `s_state` lesen (= 1), auf 0 setzen, `Device.Flush()` | danach erst Daten; Wert nach jeder Phase c–l2 und i2 erneut gelesen und = 0 (Sync/Flush des Treibers fasst ihn nicht an) |
| c | Baum `/etc/zehos`, `/home/test/.config/zehos`, Tiefe 8; Schreiben, Lesen, Umbenennen, Löschen in `/home/test` (`FileService`) | alle Ergebnisse `IsOK()` |
| d | **10 000 Dateien** in 10 Verzeichnissen × 1 000, Größen 0, 1, 4095, 4096, 4097, 64 KiB, 1 MiB, einige 16 MiB (`System.IO`) | Rücklesen = neu berechnete Erwartung |
| e | **Großes Verzeichnis:** 20 000 Einträge in einem Verzeichnis (über die 12 direkten Blöcke hinaus, Indirektblock), darin 2 000 umbenennen, 2 000 löschen (Zusammenführen von `rec_len`), 2 000 neu anlegen | Auflistung vollständig, keine Doppelnamen, Inhalte stimmen |
| f | **Umbenennen:** 1 000 Dateien im Verzeichnis, 1 000 über Verzeichnisgrenzen, ein ganzes Verzeichnis mit 1 000 Einträgen, 200 Umbenennungen **auf vorhandene Namen** | alter Name fehlt, Ziel hat den Inhalt der Quelle |
| g | **Ersetzen wie das Produkt** (`FileService.WriteData` mit und ohne `keepBackup`): 1 000 Ersetzungen wachsend, schrumpfend und auf Länge 0 | Inhalt neu, keine `.zehos-*.tmp`/`.bak`-Reste, keine Inode-Lecks (`e2fsck`) |
| h | **Löschen:** 2 000 Dateien, 3 Verzeichnisse rekursiv; danach 2 000 neue Dateien | Zähler steigen beim Löschen, sinken beim Neuanlegen passend |
| i | **Namen:** 255 Byte, UTF-8 (`ä`, `ß`, Leerzeichen), `A` und `a` nebeneinander (Ordinal, beide getrennt lesbar) | Auflistung zeigt genau diese Namen; `FileService` findet sie mit Ordinal-Vergleich |
| j | **Nebenläufigkeit:** 3 gleichzeitige Aufgaben (Anlegen, Umbenennen/Löschen, Lesen), zwei davon im selben Verzeichnis, 60 s; kein `s_state`-Zugriff in dieser Zeit | keine Ausnahme außer erwarteten `IOException`, Inhalte stimmen |
| k | **Große Datei:** 2 GiB − 1 Byte schreiben und lesen, dann ein Byte anhängen | 2 GiB − 1 lesbar; Anhängen abgelehnt, `large_file` bleibt aus, Gate `Ok` |
| l1 | **Blöcke erschöpfen:** 64-MiB-Nullen-Dateien bis zum ersten Fehler, dann 4-KiB-Dateien bis zum zweiten, dann `Mkdir` (braucht einen Block); `s_r_blocks_count` protokollieren | Fehler je über `System.IO` **und** `FileService` (`ResultCode`, Shell-Weg bedienbar); nach Löschen einer Datei wieder schreibbar; jede 50. Füll-Datei zurücklesen und auf „alles null“ prüfen (doppelt vergebene Blockketten fielen sonst nur über Integritätsdateien auf) |
| l2 | **Ersetzen bei vollem Datenträger:** nach dem Löschen in l1 erst wieder bis zum Fehler auffüllen, dann `WriteData` auf eine vorhandene Integritätsdatei mit größerem Inhalt | Fehler, **Original unverändert** (Hash) |
| i2 | **Inodes erschöpfen** auf der 1-GiB-Zusatzplatte (≈ 65 000 Inodes): 0-Byte-Dateien bis zum Fehler | Fehler über beide Wege, `s_free_inodes_count` = 0, danach löschen und neu anlegen gelingt; `e2fsck -fn` der Zusatzplatte = 0 |
| m | Herunterfahren wie Produkt: Sync, Sync, `s_state` = 1, `Device.Flush()`; **kein** Unmount von `/` | `DONE`; der Host prüft `s_state` = 1 am Image (`dumpe2fs -h`: „clean“), nicht der Gast |

**Lauf 2 – Neustart, Prüfung, weiterschreiben**

Gate `Ok` (also `s_state` = 1 am Image), Mount `/`, `s_state` = 0 + Flush; alle Integritätsdateien gegen die neu berechnete Erwartung; Schritte g und h in kleinerem Umfang (je 500); Herunterfahren wie m.

**Lauf 3 – harter Abbruch, mehrfach**

Von einer **Kopie** des Images nach Lauf 2 aus (je Zeitpunkt eine eigene Kopie): Mount `/`, `s_state` = 0, Flush, dann fortlaufend Schreiben, Umbenennen, Ersetzen **nur im eigenen Baum `/qual3`**. Der Host beendet QEMU per `kill` zu festen Zeitpunkten nach `WRITING`: **0,3 s, 2 s, 7 s, 20 s** (der Anfang ist das gefährliche Fenster). Danach `e2fsck -fn` protokollieren (Exit ≠ 0 erlaubt, Kategorie der Meldungen festhalten).

**Lauf 4 – Folge des Abbruchs (je Kopie)**

Gate muss `ReadOnly` wegen `s_state` = 0 melden (bei jedem Zeitpunkt). Die Probe hängt nicht schreibbar ein und liest über `CosmosReadOnlyExt2FileSystem` alle Integritätsdateien aus Lauf 2 außerhalb `/qual3` (protokolliert, kein Pass-Kriterium in Lauf 4 selbst). Image-SHA vor/nach Lauf 4 gleich. Danach hostseitig `e2fsck -fy` **auf einer weiteren Kopie**, anschließend `e2fsck -fn` = 0 auf dieser Kopie.

**Lauf 5 – nach Reparatur (auf der reparierten Kopie)**

Gate `Ok`, Mount `/`; **alle Integritätsdateien aus Lauf 2 außerhalb `/qual3` unverändert**; Verluste nur in `/qual3`, protokolliert; Schreiben, sauberes Herunterfahren, `e2fsck -fn` = 0.

## 4. Hostskript `scripts/test-storage-ext2-root.py`

Muster `scripts/test-storage-ext2-guest.py` (`new_work_dir`, `qemu_tool`, `wait_serial`, Kurzschluss nach FAIL). Parameter: `--iso`, `--work` (neu), `--size 32|128`, `--accel kvm|whpx|tcg`, `--timeout` je Lauf, `--e2fsck`, `--dumpe2fs`.

- **Imageformat:** qcow2 (`qemu-img create -f qcow2`), angehängt mit `-drive …,discard=unmap,detect-zeroes=unmap`. Füll-Dateien bestehen aus Nullen und belegen dadurch keinen Hostplatz; nur die Integritätsdateien (≈ 3 GiB) und Metadaten werden real geschrieben. Beide Größen werden **voll** gefüllt (Leitung 11:58). Für `e2fsck`/`dumpe2fs` wird das Image per `qemu-img convert -O raw` in eine sparse RAW-Kopie gewandelt (oder `qemu-nbd`, falls vorhanden); belegter Hostplatz und Laufzeit kommen in den Bericht. E4.0 läuft auf einem **Linux-Host** (ext4/xfs): `qemu-img convert -O raw` erzeugt auf NTFS ohne Sparse-Flag eine voll belegte Datei; für Windows-Läufe vorher das Sparse-Flag setzen. Speicherbedarf und Laufzeit von `e2fsck` auf dem 128-GiB-Image auf dem Testrechner messen und mitberichten.
- **Kopien** für Lauf 3/4/5 per `qemu-img create -b <basis> -F qcow2` (Overlay) bzw. vollständige Kopie vor `e2fsck -fy`.
- Ausgabe je Lauf `PASS/FAIL/DONE/qemu-exit`, `e2fsck`-Exit, Superblockzähler Gast vs. `dumpe2fs -h`, Image-SHA vor/nach Lauf 4.
- Exit 0 nur bei Go (§5). Fehlende Werkzeuge (`qemu-img`, `e2fsck`, `dumpe2fs`) ⇒ „offen“, nie „bestanden“.

**Zeitbudget (Schätzung, kein Messwert):** AHCI ≈ 100 MB/s unter KVM; Format 128 GiB ≈ 15 s; Last c–k ≈ 4 GiB ≈ 1–2 min plus 60 s Nebenläufigkeit; Volllauf 32 GiB ≈ 6 min, 128 GiB ≈ 25 min (Nullen werden trotzdem übertragen); Lauf 3 viermal je < 1 min. `--timeout` Standard 3600 s.

## 5. Go/No-Go

**Go** (je Größe, beide Größen nötig), nur wenn alles zutrifft:
1. Läufe 1, 2 und 5: kein FAIL, `DONE`, QEMU-Exit 0.
2. `e2fsck -fn` Exit 0 nach Lauf 1, 2, 5 und nach i2 auf der Zusatzplatte.
3. Alle Integritätsdateien in Lauf 2 und 5 gleich der neu berechneten Erwartung (Lauf 5: außerhalb `/qual3`).
4. Blöcke und Inodes erschöpft (l1, i2): Fehler am Aufrufer gefangen (`IOException` bzw. `ResultCode`), kein ungefangener Fehler, kein anderer Ausnahmetyp, kein Kernelabsturz, kein Hänger; nach Löschen wieder schreibbar; l2 lässt das Original unverändert.
5. `s_state`: nach jeder Phase 0 (b), am Image nach m „clean“ (Host), bei **jedem** Abbruchzeitpunkt in Lauf 4 Gate `ReadOnly`; `e2fsck -fy` stellt auf der Kopie einen sauberen Zustand her.
6. Große Datei (k): 2 GiB − 1 korrekt, Anhängen abgelehnt, `large_file` nicht gesetzt.
7. Lauf 4 lässt das Image unverändert (SHA gleich).
8. Superblockzähler aus dem Gast stimmen nach m mit `dumpe2fs -h` am Image überein.

**No-Go** bei jedem: `e2fsck`-Fehler nach sauberem Lauf, Abweichung einer Integritätsdatei, Hänger oder Kernelabsturz, beschädigtes Dateisystem nach Erschöpfung, stilles Überschreiten von 2 GiB − 1, Gate `Ok` nach hartem Abbruch, Verlust einer Lauf-2-Datei außerhalb `/qual3`.

**Bedingtes Go** (Leitung entscheidet): Abweichung in 6, wenn das Produkt die Grenze selbst durchsetzt (`FileService` lehnt Schreiben über 2 GiB − 1 ab, eigener Test) – dann als Produktgrenze dokumentiert.

Ergebnis in `docs/STORAGE_3_0_97_FINDINGS.md` mit ISO-Hash, Commit, Größe, Beschleunigung, Laufzeiten, belegtem Hostplatz, `e2fsck`-/`dumpe2fs`-Ausgaben; ausdrücklich „kein Stromausfall-Beleg“ (§1). QEMU und später VMware getrennt.

## 6. Offene Punkte

1. *Entschieden 11:58/11:59:* beide Größen voll füllen (qcow2, Nullen), Inode-Erschöpfung auf der 1-GiB-Zusatzplatte, `-fy` nur auf Kopien.
2. Ob Cosmos `/` als Einhängepunkt ohne Vorbedingung annimmt (Quelle: kein Verzeichnischeck, `VfsManager.cs:146`) – Schritt a ist der erste Gastbeleg.
3. Ob Cosmos die 2-GiB-Grenze selbst durchsetzt, ist ungeprüft (Gate erlaubt `large_file` bewusst nicht, `Ext2FeatureGate.cs:19`) – Schritt k klärt es.
4. Ob QEMU-AHCI Discard/Nullen so weiterreicht, dass qcow2 wirklich klein bleibt, ist ungeprüft; das Skript misst den belegten qcow2-Platz während l1 und bricht ab, sobald er 16 GiB übersteigt oder der freie Hostplatz unter 10 GiB fällt (dann ist die Nullenerkennung nicht wirksam; Befund, kein Go).
5. Zuständigkeit (Leitung 11:56): Probe und Skript peter-senior nach `task/storage-7a-installer-exclusion`; Läufe aot-probe/tripleclerk, ein Gast zur Zeit, Ansage.
