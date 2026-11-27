# Ladybird-Demo am 4. Dezember 2026

## Vor der Vorführung

Aus dem Repository-Stamm arbeiten und den absoluten Pfad zum vorbereiteten
ZehOSLinuxRun-ISO als `ISO` setzen. Ein frischer Klon enthält weder das ISO
noch die Demo-Archive und Referenzbilder. Diese vorab privat bereitstellen.
`LADYBIRD_DEMO_ASSETS` kann auf den lokalen Ordner für Bündel und Demo-ZIP
zeigen. Mit `LADYBIRD_STATIC_REF` lässt sich das Gast-Referenzbild angeben.

| Lokales Archiv auf dem MSI | Bytes | SHA-256 |
| --- | ---: | --- |
| `ladybird-static-with-images.tar.gz` (Standard für die Demo) | 147158521 | `cd2fb1d7b5d90807791a41b9bf3f64818821ce1512d6169c703c28d6fa69cdd6` |
| `ladybird-static-absolute.tar.zst` (schlanke Variante für `static.html`) | 118500639 | `a3f867b5ee3d05428eb4194c93e8a1750fe6a6b593ae7638dfcf41f079aa1283` |
| `ladybird-loopback-demo.tar.gz` (Inhalt der HTTP-Erweiterung) | 2779980 | `94199579d9a7c06f00af47e11c148e222d42fa370894a195d67b125b0e32748a` |
| `bundle-http.tar.gz` (kombiniertes, getestetes Bündel für Schritt 3) | 147287387 | `7a0c1b2239fb09c8cce6b261bb30204e311d6ac0a8649d7bbb72d8174c7c5674` |

```bash
assets=${LADYBIRD_DEMO_ASSETS:-"$HOME/ladybird-bundle"}
bundle="$assets/ladybird-static-with-images.tar.gz"
demos="$assets/zehos-freitag-demos.zip"
static_ref=${LADYBIRD_STATIC_REF:-"$HOME/zehos-v3-out/v38/diag2/shot.png"}
unset out
prepare_demo() {
  local missing=0 input tool
  for input in "${ISO:-}" "$bundle" "$demos" "$static_ref"; do
    if [ ! -f "$input" ] || [ ! -s "$input" ]; then
      printf 'Fehlende Datei: %s\n' "${input:-ISO (Variable setzen)}" >&2
      missing=1
    fi
  done
  for tool in python3 qemu-system-x86_64 qemu-img mcopy mdir file unzip sha256sum; do
    if ! command -v "$tool" >/dev/null; then
      printf 'Fehlendes Werkzeug: %s\n' "$tool" >&2
      missing=1
    fi
  done
  [ "$missing" -eq 0 ] || return 1
  printf '%s  %s\n' cd2fb1d7b5d90807791a41b9bf3f64818821ce1512d6169c703c28d6fa69cdd6 "$bundle" | sha256sum -c - || return 1
  printf '%s  %s\n' 42c8c8db73245555c557c17d5a849209a9e36d6539546d0138c72b53683b0f5d "$demos" | sha256sum -c - || return 1
  out=$(mktemp -d "${TMPDIR:-/tmp}/ladybird-demo.XXXXXX") || return 1
  unzip -q "$demos" -d "$out/png" &&
    cp -- "$static_ref" "$out/png/static-gast.png" &&
    file "$out/png/static-gast.png" "$out/png/zehos-projekt.png" "$out/png/canvas.png"
}
prepare_demo
```

Erst nach erfolgreicher Vorbereitung den Gast starten. Erwartet werden drei
lesbare PNGs mit 798 × 4087, 798 × 720 und 798 × 639
Pixeln. Vor der Vorführung alle drei öffnen und in dieser Reihenfolge im
Bildbetrachter oder in den Folien bereitlegen. `python3 scripts/png-nonwhite.py <png>`
zählt die nicht-weißen Pixel (Exit 1 bei weißem Bild).
Das ZIP enthält außerdem das Bild `zehos-desktop.png` der Projektseite
(720 × 480 Pixel).

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

**Für die Vorführung 2 GiB verwenden** (`--memory 2G`, zugleich Standard).
Bei nur 512 MiB kann der Kernel seit V43 `WebContent` mit `SIGKILL` beenden.
Wiederholte Neustarts können bis zum Zeitlimit laufen und ein weißes oder
fehlendes PNG hinterlassen.

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
   bis zum Ende. Boot und Anmeldung kommen davor. Der beobachtete KVM-smp1-Lauf dauerte 117 s
   mit Kernelbau (41 s) und Plattenbau (3 s), der Gastlauf selbst 67 s;
   mit fertigem ISO (`--iso`) also etwa 70 s (V42, `89d389776`, 2 GiB, `~/ladybird-demo-out/m28-*`).
   Der vollständige Screenshot mit 30 s Wartezeit ist zusätzlich durch
   V38 belegt (`cbcdb6bb`, KVM smp1, `~/zehos-v3-out/v38/diag2/shot.png`).

   **Generalprobe unter WSL ohne KVM.** Nach derselben Vorbereitung
   `static.html` mit TCG prüfen. Für diese Probe gilt das längere Zeitlimit.

   ```bash
   bash scripts/ladybird-demo-run.sh --iso "$ISO" -b "$bundle" \
     -o "$out/static-tcg" --accel tcg --smp 1 --memory 2G --page static -s 30 \
     --timeout 600
   file "$out/static-tcg/shot.png"
   ```

   Die WSL-Generalprobe mit QEMU 10.0.13 dauerte zweimal 104 s gesamt
   (96 s im Gast). Das PNG war bytegleich zur MSI-KVM-Referenz.

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

   Bei der MSI-Generalprobe auf `ca0e3238` (KVM, 1 CPU, 2 GiB) dauerten
   beide ZehOS-Läufe je 81 s im Gast und 88 s gesamt. Das Projekt-PNG im
   Gast misst 798 × 653 Pixel. Die Linux-Referenz mit 798 × 720 Pixeln
   zeigt denselben Inhalt mit zusätzlichem leerem Rand unten.
   Canvas Frame 90 war bytegleich zum Linux-Referenzbild (798 × 639).

3. **Projektseite über HTTP zeigen.** Auf dem MSI mit freiem KVM-Slot eine
   eigene Platte aus `bundle-http.tar.gz` aufbauen. Das kombinierte Bündel
   der HTTP-Abnahme in den Asset-Ordner legen oder dessen absoluten Pfad
   als `LADYBIRD_HTTP_BUNDLE` setzen.

   ```bash
   (
     set -euo pipefail
     if pgrep -a '^qemu-system-'; then
       echo "KVM-Slot belegt" >&2
       exit 1
     fi
     http_bundle=${LADYBIRD_HTTP_BUNDLE:-"$assets/bundle-http.tar.gz"}
     printf '%s  %s\n' 7a0c1b2239fb09c8cce6b261bb30204e311d6ac0a8649d7bbb72d8174c7c5674 "$http_bundle" | sha256sum -c -
     http=$(mktemp -d "$out/http.XXXXXX")
     cat > "$http/go.sh" <<'SH'
   bin/busybox httpd -f -p 127.0.0.1:8080 -h /share/zehos-probe &
   server=$!
   bin/busybox sleep 1
   bin/Hub --headless=screenshot --disable-sandbox --force-cpu-painting \
     --window-width 798 --window-height 720 --screenshot-delay 30 \
     --screenshot-path /shot-http.png http://127.0.0.1:8080/zehos-projekt.html
   bin/busybox kill "$server"
   SH
     : > "$http/keep"
     python3 scripts/linux-root-disk.py "$http_bundle" "$http/lb.qcow2" \
       --dest lb --skip-dangling --add /tmp/keep="$http/keep" --add /go.sh="$http/go.sh"
     python3 scripts/test-linux-root-disk-guest.py "$ISO" "$http/lb.qcow2" "$http/guest" \
       --dest lb --run 'bin/busybox sh go.sh' --accel kvm --smp 1 --memory 2G \
       --disk-cache unsafe --timeout 400 --fetch lb/shot-http.png --out "$http/shot.png"
     python3 scripts/png-nonwhite.py "$http/shot.png"
     file "$http/shot.png"
   )
   ```

   BusyBox-`httpd` und Ladybird laufen im selben LinuxRun-Prozessbaum.
   Der Wrapper öffnet `http://127.0.0.1:8080/zehos-projekt.html`, schreibt
   `/shot-http.png` in die Prozesswurzel und beendet danach den HTTP-Server.
   Erwartet werden `LINUXRUN END exit 0` und ein lesbares PNG mit 798 × 720
   Pixeln. Bei der [HTTP-Abnahme](2026-11-26-ladybird-vorstellung-folien.md)
   gelangen alle drei KVM-Läufe (68–91 s). Für die Vorführung gilt die
   Drei-Minuten-Grenze im Rückfallplan.

## Rückfall während der Vorführung

| Auslöser | Handgriff |
| --- | --- |
| KVM-Slot belegt, Start bricht ab | Direkt die vorbereiteten Bilder öffnen. |
| `KERNEL-HALT` (Exit 3) | Der Runner beendet den Gast. Direkt auf die Bilder wechseln. |
| `NICHT OK`, weißes oder fehlendes PNG | Das geprüfte Referenzbild der betroffenen Seite zeigen. |
| Nach drei Minuten ab Runner-Start gibt es noch kein vorzeigbares Bild | Im Terminal dieses Laufs Strg+C drücken (Exit 130) und auf die Bilder wechseln. Diese Vorführgrenze enthält auch Boot und Anmeldung. |

Mit `$out/png/static-gast.png` beginnen. Danach `zehos-projekt.png` und
`canvas.png` zeigen. Beim Wechsel sagen:

> Der Live-Lauf liefert gerade kein vorzeigbares Bild. Hier ist der zuvor
> aufgezeichnete Screenshot aus dem ZehOS-Gast. Projektseite und Canvas
> zeige ich anschließend anhand unserer Linux-Referenzbilder.

Für die Diagnose `run.log`, `times.txt` und das Gastprotokoll im
Ausgabeordner behalten.

[Übersicht der Demoseiten und Laufzeitabhängigkeiten](../../../experiments/Ladybird.Probe/pages/README.md)
