#!/bin/bash
# Ladybird-Demo in einem Befehl: Cosmos-Werkzeuge + Kernel-Testbau (ZehOSLinuxRun) aus diesem Arbeitsbaum, Datenplatte aus dem
# Buendel, Gastlauf von "Hub --headless=screenshot --disable-sandbox" auf static.html, Screenshot (PNG) aus der Platten-Kopie holen.
#
# Nutzung: scripts/ladybird-demo-run.sh [-b <buendel.tar.gz>] [-o <ausgabeordner>] [-u <url>] [-s <sekunden-verzoegerung>]
#                                       [--smp N] [--timeout S] [--skip-cosmos] [--skip-build] [--iso <iso>] [--disk <qcow2>]
# Ausgabe: <ausgabeordner>/shot.png (Standard ~/ladybird-demo-out/<zeitstempel>), run.log (alles, mit Zeitstempeln), times.txt,
#          dazu die Dateien des Gastskripts (serial.log, program-out.txt). Exit 0 nur mit PNG und Hub-Status "exit 0".
# Voraussetzungen: dotnet (DOTNET_ROOT, Standard ~/.dotnet), KVM, qemu-img, mtools, Netz nur fuer den Cosmos-Quellbau.
# Ein Gast gleichzeitig: bei laufendem qemu bricht das Skript ab (QEMU_OK=1 erzwingt den Start).
# --skip-cosmos: vorhandene Werkzeuge unter .tools/cosmos-tools/<VERSION> nutzen; --skip-build: --iso verwenden; --disk: fertige Platte.
set -u
R=$(cd "$(dirname "$0")/.." && pwd)
BUNDLE=$(ls -t "$HOME"/ladybird-bundle/lb-headless-*.tar.gz 2>/dev/null | head -1)
OUT=$HOME/ladybird-demo-out/$(date +%Y%m%d-%H%M%S)
URL=file:///share/zehos-probe/static.html; DELAY=5; SMP=1; TIMEOUT=600; SKIPC=0; SKIPB=0; ISO=; DISK=
while [ $# -gt 0 ]; do case $1 in
  -b) BUNDLE=$2; shift 2;; -o) OUT=$2; shift 2;; -u) URL=$2; shift 2;; -s) DELAY=$2; shift 2;;
  --smp) SMP=$2; shift 2;; --timeout) TIMEOUT=$2; shift 2;; --skip-cosmos) SKIPC=1; shift;; --skip-build) SKIPB=1; shift;;
  --iso) ISO=$2; shift 2;; --disk) DISK=$2; shift 2;; -h|--help) sed -n 2,11p "$0"; exit 0;;
  *) echo "unbekannte Option $1" >&2; exit 2;; esac; done
[ -n "$ISO" ] && SKIPB=1
mkdir -p "$OUT" || exit 2; LOG=$OUT/run.log; : > "$LOG"; : > "$OUT/times.txt"
T0=$(date +%s)
step() { echo "[$(date +%H:%M:%S) +$(( $(date +%s)-T0 ))s] $*" | tee -a "$LOG"; }
timed() { local n=$1; shift; local s=$(date +%s); step "BEGIN $n"; "$@" >> "$LOG" 2>&1; local rc=$?
  echo "$n $(( $(date +%s)-s )) s rc=$rc" | tee -a "$OUT/times.txt"; step "END $n rc=$rc"; return $rc; }
die() { step "FEHLER: $*"; echo "Log: $LOG"; exit 1; }
[ -z "$DISK" ] && { [ -f "$BUNDLE" ] || die "Buendel fehlt (-b)"; }
if pgrep qemu >/dev/null 2>&1; then [ "${QEMU_OK:-0}" = 1 ] || die "ein qemu laeuft (ein Gast gleichzeitig); QEMU_OK=1 erzwingt"; fi
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
  # Das Buendel-fonts.conf hat <dir prefix="relative">share/...</dir>: relativ zur Konfigurationsdatei, im Gast also /etc/fonts -> falsch.
  # Wir legen eine Kopie mit absoluten Pfaden (Prozesswurzel = lb) nach /etc/fonts/fonts.conf (Standardsuchpfad von fontconfig; die
  # Umgebung des Testbaus ist fest: HOME=/, kein FONTCONFIG_FILE/TMPDIR/XDG_*) und ein beschreibbares /tmp mit Cache-Ordner.
  tar -xzOf "$BUNDLE" ./fonts.conf 2>/dev/null | sed -e 's#<dir prefix="relative">share/fonts/dejavu</dir>#<dir>/share/fonts/dejavu</dir>#' \
      -e 's#<dir prefix="relative">share/Lagom/fonts</dir>#<dir>/share/Lagom/fonts</dir><cachedir>/tmp/fontconfig</cachedir>#' > "$OUT/fonts.conf"
  grep -q '<dir>/share/fonts/dejavu</dir>' "$OUT/fonts.conf" || die "fonts.conf: relative <dir> nicht gefunden (Buendel geaendert?)"
  : > "$OUT/keep"
  timed platte python3 scripts/linux-root-disk.py "$BUNDLE" "$DISK" --dest lb --skip-dangling \
    --add /etc/fonts/fonts.conf="$OUT/fonts.conf" --add /tmp/keep="$OUT/keep" || die "linux-root-disk.py"
fi
RUN="bin/Hub --headless=screenshot --disable-sandbox --force-cpu-painting --window-width 798 --window-height 519 --screenshot-delay $DELAY --screenshot-path /shot.png $URL"
step "Gastlauf: $RUN"
timed gastlauf python3 scripts/test-linux-root-disk-guest.py "$ISO" "$DISK" "$OUT/guest" --dest lb --run "$RUN" --timeout "$TIMEOUT" --smp "$SMP" --fetch lb/shot.png --out "$OUT/shot.png"
RC=$?
[ -s "$OUT/shot.png" ] && file "$OUT/shot.png" | tee -a "$LOG" | sed 's/.*: //' || step "kein Screenshot"
step "gesamt $(( $(date +%s)-T0 )) s, Gastlauf rc=$RC"
echo "gesamt $(( $(date +%s)-T0 )) s" >> "$OUT/times.txt"
if [ "$RC" = 0 ] && [ -s "$OUT/shot.png" ]; then echo "OK: $OUT/shot.png"; exit 0; fi
echo "NICHT OK (Log $LOG, Gast $OUT/guest/serial.log, program-out.txt)"; exit 1
