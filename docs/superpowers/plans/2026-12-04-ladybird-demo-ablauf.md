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
Datei den Rückfall vorbereiten.

## Live im ZehOS-Gast

1. **HTML-Seite rendern.** Aus dem Repository-Stamm mit vorbereitetem ISO starten.

   ```bash
   bash scripts/ladybird-demo-run.sh --iso "$ISO" -b "$bundle" \
     -o "$out/static" --accel kvm --smp 1 --memory 2G --page static -s 30
   file "$out/static/shot.png"
   ```

   Erwartet werden `LINUXRUN END ... exit 0` im Gastprotokoll,
   `OK: .../shot.png` im Runner und in der Vorschau 50 Absätze plus Tabelle
   bei 798 × 4087 Pixeln. Der beobachtete KVM-smp1-Lauf dauerte 117 s
   inklusive Boot (V42, `89d389776`, 2 GiB, `~/ladybird-demo-out/m28-*`).
   Der vollständige Screenshot mit 30 s Wartezeit ist zusätzlich durch
   V38 belegt (`cbcdb6bb`, KVM smp1, `$static_ref`).

2. **Projektseite und Canvas zeigen.** Die beiden Linux-PNGs aus dem ZIP zeigen
   Navigation, Grid und PNG-Illustration sowie Canvas bei Frame 90. Falls die
   Gastprobe mit demselben ISO und Bündel vorab erfolgreich war, dieselben
   Seiten live starten:

   ```bash
   bash scripts/ladybird-demo-run.sh --iso "$ISO" -b "$bundle" \
     -o "$out/projekt" --accel kvm --smp 1 --memory 2G -s 30 --page zehos-projekt
   bash scripts/ladybird-demo-run.sh --iso "$ISO" -b "$bundle" \
     -o "$out/canvas" --accel kvm --smp 1 --memory 2G -s 30 \
     -u 'file:///share/zehos-probe/canvas.html?frame=90'
   ```

   Am 27. November unter Linux gerendert und visuell geprüft. Canvas Frame 90
   war in zwei Läufen bytegleich. Gastdauer für beide neuen Seiten steht bis
   zur Gastprobe aus.

## Rückfall

Bei blockiertem MSI, weißem PNG trotz Exit 0, fehlendem Screenshot oder nach
drei Minuten ohne vorzeigbares Bild auf die bereitliegenden PNGs wechseln.
Das static-PNG stammt aus dem ZehOS-Gast, die beiden Demo-PNGs aus Ladybird
unter Linux. Für die Diagnose `run.log`, `times.txt` und das Gastprotokoll im
Ausgabeordner behalten.

[Übersicht der Demoseiten und Laufzeitabhängigkeiten](../../../experiments/Ladybird.Probe/pages/README.md)
