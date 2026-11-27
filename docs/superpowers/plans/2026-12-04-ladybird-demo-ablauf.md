# Ladybird-Demo am 4. Dezember 2026

## Vor der Vorführung

Auf dem MSI den mit der Probe verwendeten ZehOSLinuxRun-ISO-Pfad als `ISO`
setzen. Einen freien KVM-Slot absprechen. Das Bündel mit `ImageDecoder` und die
fertigen PNGs bereitlegen.

```bash
bundle="$HOME/ladybird-bundle/ladybird-static-with-images.tar.gz"
demos="$HOME/ladybird-bundle/zehos-freitag-demos.zip"
out=$(mktemp -d "${TMPDIR:-/tmp}/ladybird-demo.XXXXXX")
static_ref="$HOME/zehos-v3-out/v38/diag2/shot.png"
test -s "$ISO" && test -s "$bundle" && test -s "$static_ref"
printf '%s  %s\n' 42c8c8db73245555c557c17d5a849209a9e36d6539546d0138c72b53683b0f5d "$demos" | sha256sum -c -
unzip -q "$demos" -d "$out/png"
file "$static_ref" "$out/png/zehos-projekt.png" "$out/png/canvas.png"
```

Erwartet werden drei lesbare PNGs mit 798 × 4087, 798 × 720 und 798 × 639
Pixeln. Vor der Vorführung einmal öffnen und bei weißer Fläche oder fehlender
Datei den Rückfall vorbereiten; `python3 scripts/png-nonwhite.py <png>` zählt
die nicht-weißen Pixel (Exit 1 bei weißem Bild).

**Ein Gast gleichzeitig.** Das Skript bricht ab, solange irgendein
`qemu-system-*` läuft, auch ein Gast aus einem anderen Slot, und nennt dessen
PID. Vor dem Livestart deshalb `pgrep -a qemu-system` prüfen und den fremden Gast
beenden lassen. Nur wenn der parallele Gast abgesprochen ist, mit `QEMU_OK=1`
vor dem Befehl starten; Zeitangaben gelten dann nicht als Messung.

**Wiederholung.** Jeder Lauf baut mit `-b` eine neue Platte; der Gast schreibt
ohnehin nur in eine Kopie (`<ausgabe>/guest/disk.qcow2`). Eine schon benutzte
Kopie als `--disk` lehnt das Skript ab: Socketdateien und Caches des Vorlaufs
führen sonst zu `EADDRINUSE` und füllen die Prüfgrenze von 4096 Einträgen unter der
Prozesswurzel (`FileService.MaximumProcessRootEntries`). Ein zweiter
Versuch mit demselben `-o` landet in einem neuen Unterordner
`<ausgabe>/<zeitstempel>`; der erste Lauf bleibt für die Diagnose erhalten.

## Live im ZehOS-Gast

1. **HTML-Seite rendern.** Aus dem Repository-Stamm mit vorbereitetem ISO starten.

   ```bash
   bash scripts/ladybird-demo-run.sh --iso "$ISO" -b "$bundle" \
     -o "$out/static" --accel kvm --smp 1 --memory 2G --page static -s 30 \
     --timeout 180
   file "$out/static/shot.png"
   ```

   Erwartet werden `LINUXRUN END ... exit 0` im Gastprotokoll,
   `OK: .../shot.png` im Runner (nur mit nicht weißem Bild, die Zeile davor
   nennt den Anteil nicht-weißer Pixel) und in der Vorschau 50 Absätze plus Tabelle
   bei 798 × 4087 Pixeln. `--timeout 180` begrenzt die Zeit vom Programmstart
   bis zum Ende; Boot und Anmeldung kommen davor. Bei einem Kernel-Halt endet
   das Skript sofort mit `KERNEL-HALT` (Exit 3). Hängt der Lauf, beendet
   Strg+C das Skript und den Gast (Exit 130); danach auf den Rückfall wechseln. Der beobachtete KVM-smp1-Lauf dauerte 117 s
   mit Kernelbau (41 s) und Plattenbau (3 s), der Gastlauf selbst 67 s;
   mit fertigem ISO (`--iso`) also etwa 70 s (V42, `89d389776`, 2 GiB, `~/ladybird-demo-out/m28-*`).
   Der vollständige Screenshot mit 30 s Wartezeit ist zusätzlich durch
   V38 belegt (`cbcdb6bb`, KVM smp1, `$static_ref`).

2. **Projektseite und Canvas zeigen.** Die beiden Linux-PNGs aus dem ZIP zeigen
   Navigation, Grid und PNG-Illustration sowie Canvas bei Frame 90. Falls die
   Gastprobe mit demselben ISO und Bündel vorab erfolgreich war, dieselben
   Seiten live starten:

   ```bash
   bash scripts/ladybird-demo-run.sh --iso "$ISO" -b "$bundle" \
     -o "$out/projekt" --accel kvm --smp 1 --memory 2G -s 30 --page zehos-projekt \
     --timeout 180
   bash scripts/ladybird-demo-run.sh --iso "$ISO" -b "$bundle" \
     -o "$out/canvas" --accel kvm --smp 1 --memory 2G -s 30 \
     -u 'file:///share/zehos-probe/canvas.html?frame=90' --timeout 180
   ```

   Am 27. November unter Linux gerendert und visuell geprüft. Canvas Frame 90
   war in zwei Läufen bytegleich. Gastdauer für beide neuen Seiten steht bis
   zur Gastprobe aus.

## Rückfall

Bei blockiertem MSI (anderer Gast läuft), `NICHT OK` (auch weißes PNG),
`KERNEL-HALT`, fehlendem Screenshot oder nach drei Minuten ohne vorzeigbares
Bild (Strg+C) auf die bereitliegenden PNGs wechseln.
Das static-PNG stammt aus dem ZehOS-Gast, die beiden Demo-PNGs aus Ladybird
unter Linux. Für die Diagnose `run.log`, `times.txt` und das Gastprotokoll im
Ausgabeordner behalten.

[Übersicht der Demoseiten und Laufzeitabhängigkeiten](../../../experiments/Ladybird.Probe/pages/README.md)
