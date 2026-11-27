#!/bin/bash
# Ladybird-Demo in einem Befehl: Cosmos-Werkzeuge + Kernel-Testbau (ZehOSLinuxRun) aus diesem Arbeitsbaum, Datenplatte aus dem
# Buendel, Gastlauf von "Hub --headless=screenshot --disable-sandbox" auf static.html, Screenshot (PNG) aus der Platten-Kopie holen.
#
# Nutzung: scripts/ladybird-demo-run.sh [-b <buendel.tar.gz|.tar.zst>] [-o <ausgabeordner>] [-u <url>] [-s <sekunden-verzoegerung>]
#                                       [--smp N] [--timeout S] [--skip-cosmos] [--skip-build] [--iso <iso>] [--disk <qcow2>]
#                                       [--page <name>] [--accel kvm|tcg] [--wait-scale F] [--memory 2G]
# Ausgabe: <ausgabeordner>/shot.png (Standard ~/ladybird-demo-out/<zeitstempel>), run.log (alles, mit Zeitstempeln), times.txt,
#          dazu die Dateien des Gastskripts (serial.log, program-out.txt). Exit 0 nur mit PNG, Hub-Status "exit 0" und nicht weissem
#          Bild (scripts/png-nonwhite.py: mindestens 0,1 % Pixel mit einem Kanal < 250); weisses Bild: Exit 1.
#          Kernel-Halt im Gast (KERNEL PANIC, "System halting", [FAILFAST]): sofortiger Abbruch statt --timeout, Exit 3.
# -o auf einen vorhandenen, nicht leeren Ordner: Lauf geht in einen neuen Unterordner <ausgabeordner>/<zeitstempel>; nichts wird geleert.
# Voraussetzungen: dotnet (DOTNET_ROOT, Standard ~/.dotnet), KVM, qemu-img, mtools, zstd fuer .tar.zst, Netz nur fuer den Cosmos-Quellbau.
# Standardbuendel ohne -b: ~/ladybird-bundle/ladybird-static-with-images.tar.gz.
# Ein Gast gleichzeitig: laeuft schon ein qemu-system-* (auch in einem anderen Slot), bricht das Skript mit dessen PID ab.
#   QEMU_OK=1 startet trotzdem (nur, wenn der andere Gast abgesprochen ist; Messungen sind dann nicht gueltig).
# --timeout: Sekunden vom Programmstart bis LINUXRUN END (Standard 600; Vorfuehrung: 180). Dazu kommen Boot/Anmeldung und Herunterfahren.
# Strg+C beendet den Lauf: das Gastskript beendet qemu im finally, das Skript endet mit Exit 130.
# --skip-cosmos: vorhandene Werkzeuge unter .tools/cosmos-tools/<VERSION> nutzen; --skip-build: --iso verwenden.
# --disk: fertige Platte; der Gast schreibt nur in eine Kopie (<ausgabeordner>/guest/disk.qcow2), die Vorlage bleibt unveraendert. Eine
#   schon benutzte Kopie (.../guest/disk.qcow2) wird abgelehnt: /tmp, Caches und Socketdateien vom Vorlauf (EADDRINUSE, 4096-Eintraege-Grenze).
# --page: Demoseite aus experiments/Ladybird.Probe/pages (beim Plattenbau samt PNG-Bildern und fonts/ nach /share/zehos-probe gelegt).
# --memory: Gastspeicher (QEMU -m, Standard 2G fuer die Demo). Mit 512M kann der Kernel WebContent per SIGKILL beenden.
# --accel tcg: ohne KVM (feste Wartezeiten x10, --wait-scale aendert den Faktor); --timeout dann entsprechend hoeher setzen.
set -u
R=$(cd "$(dirname "$0")/.." && pwd)
BUNDLE=$HOME/ladybird-bundle/ladybird-static-with-images.tar.gz
OUT=$HOME/ladybird-demo-out/$(date +%Y%m%d-%H%M%S)
URL=file:///share/zehos-probe/static.html; DELAY=30; SMP=1; TIMEOUT=600; SKIPC=0; SKIPB=0; ISO=; DISK=; ACCEL=kvm; WSCALE=; MEM=2G
while [ $# -gt 0 ]; do case $1 in
  -b) BUNDLE=$2; shift 2;; -o) OUT=$2; shift 2;; -u) URL=$2; shift 2;; -s) DELAY=$2; shift 2;;
  --smp) SMP=$2; shift 2;; --timeout) TIMEOUT=$2; shift 2;; --skip-cosmos) SKIPC=1; shift;; --skip-build) SKIPB=1; shift;;
  --iso) ISO=$2; shift 2;; --disk) DISK=$2; shift 2;; -h|--help) sed -n '2,/^set -u$/p' "$0" | sed '$d'; exit 0;;
  --page)
    case ${2-} in
      ""|[!a-z0-9]*|*[!a-z0-9-]*)
        echo "--page nur [a-z0-9][a-z0-9-]*" >&2
        exit 2;;
    esac
    URL=file:///share/zehos-probe/$2.html; shift 2;;
  --accel) ACCEL=$2; shift 2;; --wait-scale) WSCALE=$2; shift 2;; --memory) MEM=$2; shift 2;;
  *) echo "unbekannte Option $1" >&2; exit 2;; esac; done
[ -n "$ISO" ] && SKIPB=1
case $DISK in */guest/disk.qcow2) echo "--disk $DISK ist die Kopie eines frueheren Laufs; die Vorlage (lb.qcow2) angeben" >&2; exit 2;; esac
# Wiederholung mit gleichem -o: den ersten Lauf (run.log, lb.qcow2, guest/) nie leeren oder ueberschreiben, sondern daneben ablegen.
if [ -d "$OUT" ] && [ -n "$(ls -A "$OUT" 2>/dev/null)" ]; then
  SUB=$OUT/$(date +%Y%m%d-%H%M%S); [ -e "$SUB" ] && SUB=$SUB-$$
  echo "Ausgabeordner $OUT nicht leer, neuer Unterordner $SUB"; OUT=$SUB
fi
mkdir -p "$OUT" || exit 2; LOG=$OUT/run.log; : > "$LOG"; : > "$OUT/times.txt"
T0=$(date +%s)
step() { echo "[$(date +%H:%M:%S) +$(( $(date +%s)-T0 ))s] $*" | tee -a "$LOG"; }
timed() { local n=$1; shift; local s=$(date +%s); step "BEGIN $n"; "$@" >> "$LOG" 2>&1; local rc=$?
  echo "$n $(( $(date +%s)-s )) s rc=$rc" | tee -a "$OUT/times.txt"; step "END $n rc=$rc"; return $rc; }
die() { step "FEHLER: $*"; echo "Log: $LOG"; exit 1; }
[ -z "$DISK" ] && { [ -f "$BUNDLE" ] || die "Buendel fehlt (-b)"; }
# Strg+C: Python und qemu (gleiche Prozessgruppe) bekommen SIGINT, das Gastskript beendet qemu im finally; hier nur den Abbruch vermerken.
trap 'step "ABGEBROCHEN (Strg+C)"; exit 130' INT TERM
# Nur echte Gaeste zaehlen (qemu-img, Shells mit "qemu" im Befehl nicht); pgrep sieht alle Benutzer, also auch Gaeste anderer Slots.
QPIDS=$(pgrep -d ' ' '^qemu-system-' 2>/dev/null)
if [ -n "$QPIDS" ]; then
  [ "${QEMU_OK:-0}" = 1 ] || die "qemu laeuft schon (PID $QPIDS; ein Gast gleichzeitig). Gast beenden oder, wenn abgesprochen, QEMU_OK=1 setzen"
  step "QEMU_OK=1: starte neben qemu PID $QPIDS"
fi
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1
export PATH=$DOTNET_ROOT:$PATH
cd "$R" || exit 2
step "Kopf $(git rev-parse --short HEAD), Buendel ${BUNDLE:-<Platte>}, Ausgabe $OUT"
V=$(cat deploy/cosmos/VERSION); TOOLS=$R/.tools/cosmos-tools/$V
if [ "$SKIPB" = 0 ]; then
  if [ "$SKIPC" = 0 ] || [ ! -d "$TOOLS" ]; then timed cosmos-bau scripts/build-cosmos.sh || die "build-cosmos.sh"; fi
  [ -d "$TOOLS" ] || die "Werkzeuge fehlen: $TOOLS"
  export PATH=$TOOLS:$PATH
  rm -rf ProjektZeh/bin ProjektZeh/obj ProjektZeh/output-x64
  timed kernelbau dotnet build ProjektZeh/ProjektZeh.csproj -c Release -warnaserror -p:UseSharedCompilation=false -p:ZehOSLinuxRun=true || die "Kernelbau (siehe run.log)"
  ISO=$R/ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso
  [ -f "$ISO" ] || die "ISO fehlt: $ISO"
  cp "$ISO" "$OUT/kernel.iso"; ISO=$OUT/kernel.iso
  grep -E '^ +[0-9]+ (Warn|Fehler|Error)' "$LOG" | sed 's/^ */  /' | tee -a "$OUT/times.txt"
fi
[ -f "$ISO" ] || die "ISO fehlt (--iso)"
if [ -z "$DISK" ]; then
  DISK=$OUT/lb.qcow2
  tar -tf "$BUNDLE" > "$OUT/bundle-members.txt" || die "Buendel kann nicht gelesen werden: $BUNDLE"
  BUNDLE_PREFIX=
  DISK_ARGS=()
  if grep -Eq '^(\./)?ladybird-static/etc/fonts/fonts\.conf$' "$OUT/bundle-members.txt"; then
    BUNDLE_PREFIX=ladybird-static/
    DISK_ARGS+=(--strip 1)
  fi
  : > "$OUT/keep"
  if FONT_CONF=$(grep -m1 -Ex "(\./)?${BUNDLE_PREFIX}etc/fonts/fonts\.conf" "$OUT/bundle-members.txt"); then
    # Die fertige Konfiguration samt Aliassen liegt bereits an ihrem Ziel im Gast.
    grep -Eq "^(\./)?${BUNDLE_PREFIX}etc/fonts/conf\.d/[^/]+\.conf$" "$OUT/bundle-members.txt" || die "etc/fonts/conf.d leer"
    tar -xOf "$BUNDLE" "$FONT_CONF" > "$OUT/fonts-prepared.conf" || die "etc/fonts/fonts.conf aus Buendel"
    if ! grep -Eq '<cachedir([[:space:]]|>)' "$OUT/fonts-prepared.conf"; then
      step "WARNUNG: fonts.conf enthaelt keinen Cache-Ordner (<cachedir>)"
    fi
  else
    # Aeltere Buendel erwarten relative Pfade ab Buendelwurzel. Im Gast liegt die Kopie jedoch unter /etc/fonts.
    tar -xOf "$BUNDLE" ./fonts.conf > "$OUT/fonts-relative.conf" || die "fonts.conf aus Buendel"
    sed -e 's#<dir prefix="relative">share/fonts/dejavu</dir>#<dir>/share/fonts/dejavu</dir>#' \
        -e 's#<dir prefix="relative">share/Lagom/fonts</dir>#<dir>/share/Lagom/fonts</dir><cachedir>/tmp/fontconfig</cachedir>#' \
        "$OUT/fonts-relative.conf" > "$OUT/fonts.conf" || die "fonts.conf umschreiben"
    grep -q '<dir>/share/fonts/dejavu</dir>' "$OUT/fonts.conf" || die "fonts.conf: relative <dir> nicht gefunden (Buendel geaendert?)"
    mkdir "$OUT/confd" || die "Ausgabeordner conf.d"
    tar -xf "$BUNDLE" -C "$OUT/confd" --wildcards './conf.d/*.conf' --strip-components=2 || die "conf.d aus Buendel"
    CONFD_ADD=()
    for f in "$OUT"/confd/*.conf; do [ -f "$f" ] && [ ! -L "$f" ] && CONFD_ADD+=(--add "/etc/fonts/conf.d/$(basename "$f")=$f"); done
    [ ${#CONFD_ADD[@]} -gt 0 ] || die "conf.d leer"
    DISK_ARGS+=(--add /etc/fonts/fonts.conf="$OUT/fonts.conf" "${CONFD_ADD[@]}")
  fi
  # Demoseiten mit Webfonts und PNG-Bildern: Das Buendel enthaelt nur static.html.
  PAGES_ADD=(); P=$R/experiments/Ladybird.Probe/pages
  for f in "$P"/*.html "$P"/*.png "$P"/fonts/*; do [ -f "$f" ] && PAGES_ADD+=(--add "/share/zehos-probe/${f#"$P"/}=$f"); done
  timed platte python3 scripts/linux-root-disk.py "$BUNDLE" "$DISK" --dest lb --skip-dangling \
    --add /tmp/keep="$OUT/keep" "${DISK_ARGS[@]}" "${PAGES_ADD[@]}" || die "linux-root-disk.py"
fi
RUN="bin/Hub --headless=screenshot --disable-sandbox --force-cpu-painting --window-width 798 --window-height 519 --screenshot-delay $DELAY --screenshot-path /shot.png $URL"
step "Gastlauf: $RUN"
timed gastlauf python3 scripts/test-linux-root-disk-guest.py "$ISO" "$DISK" "$OUT/guest" --dest lb --run "$RUN" --timeout "$TIMEOUT" --smp "$SMP" --accel "$ACCEL" ${WSCALE:+--wait-scale "$WSCALE"} ${MEM:+--memory "$MEM"} --fetch lb/shot.png --out "$OUT/shot.png"
RC=$?
if [ "$RC" = 3 ]; then
  # Das Gastskript hat eine Haltmarke gesehen und den Gast beendet; die Platte wurde nicht gelesen.
  step "KERNEL-HALT: $(grep -m1 -E 'KERNEL PANIC|System halting|\[FAILFAST\]' "$OUT/guest/serial.log" 2>/dev/null)"
  echo "gesamt $(( $(date +%s)-T0 )) s (Kernel-Halt)" >> "$OUT/times.txt"
  echo "KERNEL-HALT (Gast $OUT/guest/serial.log, Log $LOG)"; exit 3
fi
PIX=1
if [ -s "$OUT/shot.png" ]; then
  file "$OUT/shot.png" | tee -a "$LOG" | sed 's/.*: //'
  # Ladybird liefert bei fehlendem Decoder oder zu kurzer Wartezeit ein weisses Bild mit Exit 0: das ist kein Erfolg.
  python3 -I "$R/scripts/png-nonwhite.py" "$OUT/shot.png" 2>&1 | tee -a "$LOG" "$OUT/times.txt"; PIX=${PIPESTATUS[0]}
  [ "$PIX" = 0 ] || step "BILD WEISS oder unlesbar (png-nonwhite rc=$PIX)"
else step "kein Screenshot"; fi
step "gesamt $(( $(date +%s)-T0 )) s, Gastlauf rc=$RC"
echo "gesamt $(( $(date +%s)-T0 )) s" >> "$OUT/times.txt"
if [ "$RC" = 0 ] && [ "$PIX" = 0 ]; then echo "OK: $OUT/shot.png"; exit 0; fi
echo "NICHT OK (Log $LOG, Gast $OUT/guest/serial.log, program-out.txt)"; exit 1
