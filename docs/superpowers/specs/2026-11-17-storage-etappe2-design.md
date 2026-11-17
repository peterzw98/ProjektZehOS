# Etappe 2: größere Datenträger – Entscheidungen des Leads

Stand: 17. November 2026, Grundlage [Aufrufer-Inventar](2026-11-17-storage-callers.md) und [Plan](../plans/2026-11-17-storage-linux.md#etappe-2-größere-datenträger-sicher-verwendbar-machen). Gilt nur für FAT-Datenplatten; ext2 bleibt in dieser Etappe auf die isolierte Gastprobe beschränkt.

## Entscheidungen

1. **Standard bleibt unverändert.** Ohne neue Platte verhält sich der Kernel wie heute: `sata0`, 64 MiB, FAT32 auf MBR-Partition 0, Mountpunkt `/data`. Bestandsimages werden nicht verändert; Hosttest mit Prüfsumme.
2. **`disk init <gerät>` akzeptiert leere Platten von 64 MiB bis 128 GiB** (512-Byte-Sektoren); Größen außerhalb: `UnsupportedDiskSize`. Leerprüfung (Datenverlust hat Vorrang vor Zeit, Cosmos-AHCI überträgt nur 8 Sektoren je Befehl, AhciPort.cs:34 in efcf20c):
   - **bis 4 GiB:** vollständiger Scan wie bisher.
   - **über 4 GiB:** Teilscan, nur zusammen mit `disk init <gerät> --yes-large` und deutlichem Hinweis in der Ausgabe: erste und letzte 64 MiB vollständig, Signaturstellen (MBR/GPT Sektor 0/1 und Ende, FAT-Bootsektor, ext2 `0x438`, NTFS, LUKS) und eine Stichprobe von 4 KiB je 64 MiB. Ein einziges Byte ungleich null bricht ab; ohne `--yes-large` wird nichts geschrieben.
   Zeitbudget vor Gasttests mit 64 MiB und 4 GiB messen.
3. **Identität statt Gerätename.** `disk init` markiert die Datenpartition mit dem FAT-Volumelabel `ZEHOSDATA`. Beim Boot sucht der Kernel über alle Geräte genau eine Partition mit Typ `0x0C` und diesem Label und hängt sie unter `/data` ein. Mehr als ein Treffer: nichts einhängen, Fehler melden (kein Raten). Kein Treffer: Rückfall auf das heutige `sata0`-Verhalten, damit unmarkierte Bestandsplatten weiter funktionieren. Lässt Cosmos 3.0.97 kein Label beim Formatieren zu, ersatzweise Markerdatei `/.zehos-data` im Wurzelverzeichnis, geprüft nach einem Nur-lesen-Mount.
4. **Ein Mountpunkt `/data`** bis Etappe 4. Mehrere persistente Mounts, `/`-Root und Mountpunkt-Argumente kommen erst mit dem Root-Baum.
5. **Installer schützt die Datenplatte.** Platten mit `ZEHOSDATA`-Partition erscheinen nicht in `setup list`; Installation auf sie wird verweigert, auch mit `--erase`.
6. **VMware:** eigene AHCI-Testplatte, keine Annahme über VMware-Standard-SCSI. Zeitbudget für 128 GiB wird gemessen, die Platte wird nicht vollständig gelesen.
7. **Außerhalb dieser Etappe** (notiert, nicht jetzt): Sync/Unmount aller Mounts beim Herunterfahren (Etappe 7), Update-Ziel bei getrennter Boot-/Datenplatte (Etappe 7), Orte-Leiste im Dateimanager (Etappe 4).

## Arbeitspakete

| Paket | Inhalt | Prüfung |
|---|---|---|
| 2.1 | Reine Auswahlfunktion `DataDiskSelector` (Geräteliste mit Sektorgröße, Blockzahl, Partitionen mit Typ/Label → gewählte Partition oder Fehler) plus Größenprüfung für `disk init`; Tests zuerst: kein/ein/zwei Treffer, Rückfall `sata0`, Grenzen 64 MiB−1/64 MiB/128 GiB/128 GiB+1 | Hosttests |
| 2.2 | Cosmos-Anbindung: Label beim Formatieren (oder Markerdatei), Boot-Suche über 2.1, Installer-Ausschluss | nativer Build, Gast |
| 2.3 | Gastabnahme QEMU: frische sparse Images 32/64/128 GiB, `disk init`, Mount, Kapazitätsanzeige, Neustart; Gegenprobe Bestandsimage 64 MiB mit Prüfsumme unverändert | Tester, nur mit Ansage |
| 2.4 | dasselbe unter VMware | Tester mit VMware, später |
