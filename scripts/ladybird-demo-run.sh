#!/bin/bash
# Ladybird-Demo in einem Befehl: Cosmos-Werkzeuge + Kernel-Testbau (ZehOSLinuxRun) aus diesem Arbeitsbaum, Datenplatte aus dem
# Buendel, Gastlauf von "Hub --headless=screenshot --disable-sandbox" auf static.html, Screenshot (PNG) aus der Platten-Kopie holen.
#
# Nutzung: scripts/ladybird-demo-run.sh [-b <buendel.tar.gz>] [-o <ausgabeordner>] [-u <url>] [-s <sekunden-verzoegerung>]
#                                       [--smp N] [--timeout S] [--skip-cosmos] [--skip-build] [--iso <iso>] [--disk <qcow2>]
#                                       [--page <name>] [--accel kvm|tcg] [--wait-scale F] [--memory 1G]
# Ausgabe: <ausgabeordner>/shot.png (Standard ~/ladybird-demo-out/<zeitstempel>), run.log (alles, mit Zeitstempeln), times.txt,
#          dazu die Dateien des Gastskripts (serial.log, program-out.txt). Exit 0 nur mit PNG und Hub-Status "exit 0".
#          Kernel-Halt im Gast (KERNEL PANIC, "System halting", [FAILFAST]): sofortiger Abbruch statt --timeout, Exit 3.
# Voraussetzungen: dotnet (DOTNET_ROOT, Standard ~/.dotnet), KVM, qemu-img, mtools, Netz nur fuer den Cosmos-Quellbau.
# Ein Gast gleichzeitig: bei laufendem qemu bricht das Skript ab (QEMU_OK=1 erzwingt den Start).
# --skip-cosmos: vorhandene Werkzeuge unter .tools/cosmos-tools/<VERSION> nutzen; --skip-build: --iso verwenden; --disk: fertige Platte.
# --page: Demoseite aus experiments/Ladybird.Probe/pages (beim Plattenbau samt PNG-Bildern und fonts/ nach /share/zehos-probe gelegt).
# --memory: Gastspeicher (QEMU -m, Standard 2G; mit 512M haelt der Kernel bei Ladybird aus Speichermangel).
# --accel tcg: ohne KVM (feste Wartezeiten x10, --wait-scale aendert den Faktor); --timeout dann entsprechend hoeher setzen.
set -u
R=$(cd "$(dirname "$0")/.." && pwd)
BUNDLE=$(ls -t "$HOME"/ladybird-bundle/lb-headless-*.tar.gz 2>/dev/null | head -1)
OUT=$HOME/ladybird-demo-out/$(date +%Y%m%d-%H%M%S)
URL=file:///share/zehos-probe/static.html; DELAY=30; SMP=1; TIMEOUT=600; SKIPC=0; SKIPB=0; ISO=; DISK=; ACCEL=kvm; WSCALE=; MEM=2G
while [ $# -gt 0 ]; do case $1 in
  -b) BUNDLE=$2; shift 2;; -o) OUT=$2; shift 2;; -u) URL=$2; shift 2;; -s) DELAY=$2; shift 2;;
  --smp) SMP=$2; shift 2;; --timeout) TIMEOUT=$2; shift 2;; --skip-cosmos) SKIPC=1; shift;; --skip-build) SKIPB=1; shift;;
  --iso) ISO=$2; shift 2;; --disk) DISK=$2; shift 2;; -h|--help) sed -n 2,16p "$0"; exit 0;;
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
  # conf.d (sans-serif-Alias, 45-generic/49-sansserif ...): fonts.conf hat <include>conf.d</include>, relativ -> /etc/fonts/conf.d. Ohne sie
  # fehlt "DejaVu Sans" fuer sans-serif, FontPlugin::default_font liefert null (Befund senior 10:54). Nur regulaere Dateien (--add kennt keine Links).
  rm -rf "$OUT/confd"; mkdir -p "$OUT/confd"
  tar -xzf "$BUNDLE" -C "$OUT/confd" --wildcards './conf.d/*.conf' --strip-components=2 || die "conf.d aus Buendel"
  CONFD_ADD=()
  for f in "$OUT"/confd/*.conf; do [ -f "$f" ] && [ ! -L "$f" ] && CONFD_ADD+=(--add "/etc/fonts/conf.d/$(basename "$f")=$f"); done
  [ ${#CONFD_ADD[@]} -gt 0 ] || die "conf.d leer"
  # Demoseiten mit Webfonts und PNG-Bildern: Das Buendel enthaelt nur static.html.
  PAGES_ADD=(); P=$R/experiments/Ladybird.Probe/pages
  for f in "$P"/*.html "$P"/*.png "$P"/fonts/*; do [ -f "$f" ] && PAGES_ADD+=(--add "/share/zehos-probe/${f#"$P"/}=$f"); done
  timed platte python3 scripts/linux-root-disk.py "$BUNDLE" "$DISK" --dest lb --skip-dangling \
    --add /etc/fonts/fonts.conf="$OUT/fonts.conf" --add /tmp/keep="$OUT/keep" "${CONFD_ADD[@]}" "${PAGES_ADD[@]}" || die "linux-root-disk.py"
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
[ -s "$OUT/shot.png" ] && file "$OUT/shot.png" | tee -a "$LOG" | sed 's/.*: //' || step "kein Screenshot"
step "gesamt $(( $(date +%s)-T0 )) s, Gastlauf rc=$RC"
echo "gesamt $(( $(date +%s)-T0 )) s" >> "$OUT/times.txt"
if [ "$RC" = 0 ] && [ -s "$OUT/shot.png" ]; then echo "OK: $OUT/shot.png"; exit 0; fi
echo "NICHT OK (Log $LOG, Gast $OUT/guest/serial.log, program-out.txt)"; exit 1
