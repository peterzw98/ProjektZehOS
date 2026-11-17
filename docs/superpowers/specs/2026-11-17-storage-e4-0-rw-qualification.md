# Etappe 4.0: RW-Qualifikation ext2 als `/` – Prüfentwurf

Stand: 17. November 2026, Basis `origin/master`. Paket 4.0 aus [Etappe 4](2026-11-17-storage-etappe4-design.md#4-arbeitspakete): **ohne Go kein `Root`-Profil** (4.3 hängt daran). Dieses Dokument legt Gastprobe, Hostskript, Abläufe und Go/No-Go fest. **Nur Entwurf**, kein Code, nichts gelaufen. Grundlage ist die Etappe-1-Probe `CosmosExt2GuestProbe` (72 MiB, `sata1`, zwei Läufe, `scripts/test-storage-ext2-guest.py`); sie bleibt unverändert.

## 1. Was qualifiziert wird

Der Cosmos-3.0.97-ext2-Treiber mit den ZehOS-Plugs (`FormatForKernel`, `FreeInode`) als schreibbares `/` unter Dauerlast, einschließlich der in Etappe 4 §2 festgelegten ZehOS-Eigenleistungen: `s_state` = 0 nach dem RW-Mount, beim Herunterfahren Sync, Sync, `s_state` = 1 per Lesen-Ändern-Schreiben, eigenes `Device.Flush()`. Nicht qualifiziert: Installer, Migration, Profilwahl (4.3–4.5), VMware (eigener Lauf später).

## 2. Gastprobe `CosmosExt2RootProbe`

- Eigener Buildschalter `-p:ZehOSStorageRootProbe=true` (`ZEHOS_STORAGE_ROOT_PROBE`), Einhängepunkt im `Kernel.cs` wie die vorhandenen Proben (`#if … if (!Probe.Run()) { Stop(); return; }`). Kein normaler Build enthält ihn.
- **Platte:** `sata1`, Größe **exakt** 32 GiB oder 128 GiB (512-Byte-Sektoren), sonst Abbruch ohne Schreibzugriff. Lauf 1 nur auf einer Platte, deren erstes MiB vollständig null ist; Folgeläufe nur, wenn der Superblock den Volumenamen `zehos-rootprobe` trägt. Alles andere ⇒ FAIL ohne Schreibzugriff (Schutz wie Etappe 1).
- **Format:** `CosmosExt2Formatter.FormatForKernel` (4-KiB-Blöcke, `sparse_super`, Volumename `zehos-rootprobe`), danach `Ext2FeatureGate` = `Ok`.
- **Mount auf `/`**, schreibbar. Zusätzlich hängt die Probe die vorhandene App-/Datenplatte (`sata0`, FAT) wie heute unter `/data` ein: damit sind verschachtelte Mounts unter `/` (längster Treffer, Etappe 3) mitgeprüft.
- **Zwei Zugriffswege:** der Großteil der Last über `System.IO` (wie Cosmos-Aufrufer), ein fester Teil (Abschnitt 3, Schritt c) über den ZehOS-`FileService` mit `CosmosPathGuard.For("/")`, damit der Produktpfad mit Wurzel `/` und Pfadwächter belegt ist.
- **Ausgabe:** seriell `STORAGE_ROOT <schritt> PASS|FAIL <detail>`, am Ende `STORAGE_ROOT DONE`; nach `DONE` beendet sich der Gast selbst (wie Etappe 1). Für Lauf 3 zusätzlich `STORAGE_ROOT WRITING` (Signal für den harten Abbruch).
- **Prüfliste im Dateisystem:** `/qual/manifest` (Pfad, Größe, SHA-256 je Datei), damit Folgeläufe ohne Hostwissen vergleichen; zusätzlich die Summe aller Hashes seriell.

## 3. Abläufe

Je Größe (32 GiB, 128 GiB) ein frisches sparse RAW-Image im neuen Arbeitsordner (`new_work_dir`, CreateNew), Beschleunigung KVM. Nach **jedem** Lauf hostseitig `e2fsck -fn` auf dem Image.

**Lauf 1 – frisch, Last, sauberes Herunterfahren**

| Schritt | Inhalt | PASS-Bedingung |
|---|---|---|
| a | Format, Gate, Mount `/`, `/data` daneben | Gate `Ok`, beide Mounts sichtbar, `/data` über `/` erreichbar |
| b | `s_state` lesen (muss 1 sein), auf 0 setzen (Lesen-Ändern-Schreiben), Superblock zurücklesen | 0 steht auf der Platte; der Treiber hat ihn beim Mount nicht verändert |
| c | Baum: `/etc/zehos`, `/home/test/.config/zehos`, Tiefe 8; über `FileService` Schreiben, Lesen, Umbenennen, Löschen in `/home/test` | alle Ergebnisse `IsOK()`; Pfadwächter lässt `/data/...` als fremden Mount nicht durch |
| d | **10 000 Dateien** in 10 Verzeichnissen × 1 000, Größen deterministisch aus fester Saat: 0, 1, 4095, 4096, 4097, 64 KiB, 1 MiB, einige 16 MiB | Rücklesen jeder Datei = Hash im Manifest |
| e | **Umbenennen:** 1 000 Dateien im Verzeichnis, 1 000 über Verzeichnisgrenzen, ein ganzes Verzeichnis mit 1 000 Einträgen | alter Name fehlt, neuer Name mit gleichem Hash |
| f | **Löschen:** 2 000 Dateien, 3 Verzeichnisse rekursiv; danach 2 000 neue Dateien (Inode-/Blockwiederverwendung) | freie Blöcke/Inodes im Superblock steigen beim Löschen und sinken beim Neuanlegen passend |
| g | **Große Datei:** 2 GiB − 1 Byte schreiben und lesen; dann versuchen, ein weiteres Byte anzuhängen | 2 GiB − 1 lesbar; das Anhängen wird **abgelehnt** und `large_file` bleibt aus (Gate weiter `Ok`). Setzt der Treiber stattdessen `large_file` oder schreibt still weiter, ist das ein Befund (Abschnitt 5) |
| h | **ENOSPC** (siehe Abschnitt 4): Füllen mit 64-MiB-Dateien bis zum ersten Fehler, dann mit 4-KiB-Dateien bis zum zweiten | Fehler kommt als Ausnahme/Fehlercode, Shell/Probe läuft weiter; danach eine Datei löschen, erneut schreiben gelingt |
| i | Manifest schreiben; Herunterfahren wie Produkt: Sync, Sync, `s_state` = 1 (Lesen-Ändern-Schreiben), `Device.Flush()`; **kein** Unmount von `/` | `s_state` = 1 auf der Platte; danach `DONE` |

**Lauf 2 – Neustart, Prüfung, weiterschreiben**

Gate `Ok` (also `s_state` = 1 überlebt), Mount `/` schreibbar, `s_state` = 0; alle Dateien aus dem Manifest mit Hash vergleichen; 1 000 Dateien ändern, 1 000 löschen, 1 000 neu; Manifest neu; Herunterfahren wie i. Danach `e2fsck -fn`.

**Lauf 3 – harter Abbruch**

Mount `/`, `s_state` = 0, fortlaufend Dateien schreiben und umbenennen; nach `STORAGE_ROOT WRITING` beendet der Host QEMU nach einer festen Zeit (z. B. 20 s) hart (`kill`). Danach `e2fsck -fn`: Exit ≠ 0 ist hier **erlaubt**, das Ergebnis wird protokolliert (Kategorie der Meldungen).

**Lauf 4 – Folge des Abbruchs**

Gate muss `ReadOnly` melden (wegen `s_state` = 0, nicht wegen RoCompat) ⇒ Probe hängt nicht schreibbar ein, prüft Lesbarkeit über `CosmosReadOnlyExt2FileSystem` (Manifest aus Lauf 2, soweit vorhanden). Danach hostseitig `e2fsck -fy`, anschließend `e2fsck -fn` = 0, **Lauf 5** wie Lauf 2 (Gate `Ok`, Hashvergleich der von `e2fsck` erhaltenen Dateien, Schreiben, sauberes Herunterfahren, `e2fsck -fn` = 0).

## 4. Hostskript `scripts/test-storage-ext2-root.py`

Muster `scripts/test-storage-ext2-guest.py` (`new_work_dir`, `qemu_tool`, `wait_serial`, Kurzschluss nach FAIL). Parameter: `--iso`, `--work` (neu), `--size 32|128`, `--accel kvm|whpx|tcg`, `--timeout` je Lauf, `--e2fsck`, `--fill full|partial`. Ausgabe je Lauf `PASS/FAIL/DONE/qemu-exit` und `e2fsck`-Exit; Image-SHA vor/nach Lauf 4 (nur lesend ⇒ gleich). Exit 0 nur bei Go-Kriterien (Abschnitt 5). Fehlende Werkzeuge ⇒ „offen“, nie „bestanden“.

**Plattenplatz:** ENOSPC schreibt das Image voll, das sparse Image wird dann real 32 bzw. 128 GiB groß. Das Skript prüft vorher den freien Hostplatz (Imagegröße + 10 %) und bricht sonst ab. `--fill partial` füllt nur die ersten 8 GiB und prüft ENOSPC stattdessen auf einer zusätzlichen 1-GiB-Testplatte im selben Lauf (Abschnitt 6, offene Entscheidung).

**Zeitbudget (Schätzung, kein Messwert):** AHCI überträgt 8 Sektoren je Befehl, unter KVM ≈ 100 MB/s; Format 128 GiB (Inodetabellen ≈ 1 GiB) ≈ 15 s; Last d–g ≈ 3 GiB ≈ 1 min; Volllauf 32 GiB ≈ 6 min, 128 GiB ≈ 25 min. `--timeout` Standard 3600 s.

## 5. Go/No-Go

**Go** (je Größe, beide Größen nötig), nur wenn alles zutrifft:
1. Läufe 1, 2 und 5: kein FAIL, `DONE`, QEMU-Exit 0.
2. `e2fsck -fn` Exit 0 nach Lauf 1, 2 und 5.
3. Alle Manifest-Hashes in Lauf 2 und 5 gleich.
4. ENOSPC (h): Fehler wird gemeldet, keine Ausnahme bis zur Probe-Grenze durchgereicht, kein Hängen; nach Löschen wieder schreibbar; `e2fsck -fn` danach 0.
5. `s_state`: Treiber verändert ihn nicht (b), ZehOS-Wert überlebt Sync/Flush (i), Lauf 4 erkennt den Abbruch über das Gate (`ReadOnly`), `e2fsck -fy` stellt einen sauberen Zustand her.
6. Große Datei (g): 2 GiB − 1 korrekt; Anhängen abgelehnt, `large_file` nicht gesetzt.
7. Lauf 4 lässt das Image unverändert (SHA gleich).

**No-Go** bei jedem: `e2fsck`-Fehler nach sauberem Lauf, Hashabweichung, Hänger oder Kernelabsturz, beschädigtes Dateisystem nach ENOSPC, stilles Überschreiten von 2 GiB − 1, Gate `Ok` nach hartem Abbruch.

**Bedingtes Go** (Leitung entscheidet): Abweichung in 6, wenn das Produkt die Grenze selbst durchsetzt (`FileService` lehnt Schreiben über 2 GiB − 1 ab, eigener Test) – dann wird das als Produktgrenze dokumentiert.

Ergebnis in `docs/STORAGE_3_0_97_FINDINGS.md` mit ISO-Hash, Commit, Größe, Beschleunigung, Laufzeiten und `e2fsck`-Ausgaben; QEMU und später VMware getrennt.

## 6. Offene Punkte

1. **Leitung:** ENOSPC auf 128 GiB voll schreiben (braucht ≈ 140 GiB freien Hostplatz beim Tester) oder `--fill partial` mit 1-GiB-Zusatzplatte? Vorschlag: 32 GiB voll, 128 GiB partial; Begründung: die Füllgrenze ist größenunabhängig, die 128-GiB-Abdeckung zielt auf Gruppen-/Bitmapgeometrie, die schon d–g treffen.
2. Ob Cosmos `/` als Einhängepunkt ohne Vorbedingung annimmt (Quelle: kein Verzeichnischeck, `VfsManager.cs:146`) – Schritt a ist der erste Gastbeleg.
3. Ob Cosmos die 2-GiB-Grenze selbst durchsetzt, ist ungeprüft (Gate erlaubt `large_file` bewusst nicht, `Ext2FeatureGate.cs:19`) – Schritt g klärt es.
4. Zuständigkeit: Probe und Skript peter-senior (oder Zuteilung Coordinator), Läufe aot-probe/tripleclerk, ein Gast zur Zeit, Ansage.
