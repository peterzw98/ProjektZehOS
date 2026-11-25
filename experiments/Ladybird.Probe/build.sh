#!/usr/bin/env bash
# Ladybird L1-A: Ladybird am festen Commit auf Linux ohne JIT bauen und den Aufwand messen.
#
# Nur Messung auf einem Linux-Host; nichts davon gehört in den Kernel- oder Paketbau von ZehOS.
# Das Skript installiert nichts und braucht kein root. Fehlendes meldet die Voraussetzungsprüfung
# mit Paketnamen; installieren muss der Mensch (siehe README.md).
#
# Aufruf: build.sh [--jobs N] [--workdir DIR] [--resume] [--check]
#   DRY_RUN=1 build.sh   prüft die Voraussetzungen und zeigt die geplanten Schritte, ohne zu klonen oder zu bauen.
#
# Exit-Codes: 0 Bau fertig und $WORKDIR/build/bin/Ladybird vorhanden, 1 Aufruf- oder Umgebungsfehler,
# 2 Voraussetzungen fehlen, 3 Abbruchregel gegriffen (Zeit, Platz, OOM), 4 Bau fehlgeschlagen,
# 5 Quelle steht nicht auf dem festen Commit.

set -euo pipefail

# Fester Stand laut Plan (docs/superpowers/plans/2026-11-17-ladybird.md, „Feste Werte“).
readonly LADYBIRD_REPO="${LADYBIRD_REPO:-https://github.com/LadybirdBrowser/ladybird.git}"
readonly LADYBIRD_COMMIT="6fba5b78a1043fe93b3451204e04dcc0e3bd983a"
# Aus rust-toolchain.toml dieses Commits; nach dem Klonen wird es gegengeprüft.
readonly RUST_CHANNEL="1.98.0"
# Untergrenzen aus Documentation/BuildInstructionsLadybird.md und Meta/Utils/find_compiler.py dieses Commits.
readonly MIN_CMAKE="3.30"
readonly MIN_QT="6.10"
readonly MIN_GCC=14
readonly MIN_CLANG=19
readonly MIN_PYTHON="3.7"
# Abbruchregeln laut Plan L1-A. Die Umgebungsvariablen sind nur für Tests des Skripts gedacht;
# abweichende Werte stehen in build-stats.json und machen die Messung ungültig für den Plan.
readonly MAX_SECONDS="${LADYBIRD_PROBE_MAX_SECONDS:-$((8 * 3600))}"
readonly MAX_DISK_BYTES="${LADYBIRD_PROBE_MAX_DISK_BYTES:-$((200 * 1024 * 1024 * 1024))}"
readonly MIN_RAM_KIB="${LADYBIRD_PROBE_MIN_RAM_KIB:-$((32 * 1024 * 1024))}"
readonly MONITOR_INTERVAL="${LADYBIRD_PROBE_MONITOR_INTERVAL:-15}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR

DRY_RUN="${DRY_RUN:-0}"
JOBS=""
WORKDIR="${HOME}/ladybird-probe"
RESUME=0
CHECK_ONLY=0

log() { printf '[build.sh %s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }
err() { printf '[build.sh] FEHLER: %s\n' "$*" >&2; }
die() { err "$2"; exit "$1"; }

usage() {
	cat <<'EOF'
Aufruf: build.sh [--jobs N] [--workdir DIR] [--resume] [--check]

  --jobs N       parallele Jobs für Abhängigkeiten und Bau (Standard: Zahl der logischen Kerne)
  --workdir DIR  Arbeitsordner für Quelle, Bau, Logs und build-stats.json (Standard: ~/ladybird-probe)
  --resume       vorhandenen Arbeitsordner weiterbauen (nach Unterbrechung); die Zeiten gelten dann
                 nicht als kalter Bau und build-stats.json vermerkt "resumed": true
  --check        nur die Voraussetzungen prüfen
  DRY_RUN=1      Voraussetzungen prüfen und die geplanten Schritte ausgeben, nichts klonen oder bauen
EOF
}

while [[ $# -gt 0 ]]; do
	case "$1" in
		--jobs)
			[[ $# -ge 2 ]] || die 1 "--jobs braucht einen Wert"
			JOBS="$2"; shift 2 ;;
		--jobs=*) JOBS="${1#--jobs=}"; shift ;;
		--workdir)
			[[ $# -ge 2 ]] || die 1 "--workdir braucht einen Wert"
			WORKDIR="$2"; shift 2 ;;
		--workdir=*) WORKDIR="${1#--workdir=}"; shift ;;
		--resume) RESUME=1; shift ;;
		--check) CHECK_ONLY=1; shift ;;
		-h|--help) usage; exit 0 ;;
		*) usage >&2; die 1 "unbekannte Option: $1" ;;
	esac
done

if [[ -z "$JOBS" ]]; then
	JOBS="$(nproc)"
fi
[[ "$JOBS" =~ ^[1-9][0-9]*$ ]] || die 1 "--jobs muss eine positive Zahl sein, nicht '$JOBS'"
[[ "$DRY_RUN" == 0 || "$DRY_RUN" == 1 ]] || die 1 "DRY_RUN muss 0 oder 1 sein"

# Ladybird lehnt root ab (Meta/ladybird.py), weil der Bauordner sonst root gehört; dieses Skript ebenso.
if [[ "$(id -u)" -eq 0 && "$DRY_RUN" == 0 ]]; then
	die 1 "nicht als root ausführen; der Bau läuft als normaler Benutzer"
fi

WORKDIR="$(realpath -m -- "$WORKDIR")"
readonly WORKDIR
readonly SRC_DIR="$WORKDIR/src"
readonly BUILD_DIR="$WORKDIR/build"
readonly LOG_DIR="$WORKDIR/logs"
readonly STATE_DIR="$WORKDIR/.state"
readonly STATS_FILE="$WORKDIR/build-stats.json"

# ---------------------------------------------------------------------------------------------
# Voraussetzungen
# ---------------------------------------------------------------------------------------------

MISSING=()   # Einträge "was fehlt -> Manjaro/Arch-Paket oder Abhilfe"
WARNINGS=()
CC_SEL=""
CXX_SEL=""

missing() { MISSING+=("$1"); }
warn() { WARNINGS+=("$1"); }

# version_ge A B: wahr, wenn Version A >= B (Punkte-getrennt, nur Zahlen verglichen).
version_ge() {
	[[ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n1)" == "$2" ]]
}

need_cmd() {
	local cmd="$1" pkg="$2"
	command -v "$cmd" >/dev/null 2>&1 || missing "Programm '$cmd' -> Paket $pkg"
}

compiler_major() {
	"$1" -dumpversion 2>/dev/null | sed -E 's/^([0-9]+).*/\1/'
}

pick_compiler() {
	# Gleiche Untergrenzen wie Meta/Utils/find_compiler.py; CC/CXX aus der Umgebung haben Vorrang.
	local cc="${CC:-}" cxx="${CXX:-}" major
	if [[ -n "$cc" || -n "$cxx" ]]; then
		if [[ -z "$cc" || -z "$cxx" ]]; then
			missing "CC und CXX nur zusammen setzen (gesetzt: CC='$cc' CXX='$cxx')"
			return
		fi
		if ! command -v "$cc" >/dev/null 2>&1 || ! command -v "$cxx" >/dev/null 2>&1; then
			missing "Compiler aus CC='$cc' / CXX='$cxx' nicht gefunden"
			return
		fi
		CC_SEL="$cc"; CXX_SEL="$cxx"
		return
	fi
	if command -v gcc >/dev/null 2>&1 && command -v g++ >/dev/null 2>&1; then
		major="$(compiler_major g++)"
		if [[ -n "$major" && "$major" -ge "$MIN_GCC" ]]; then
			CC_SEL=gcc; CXX_SEL=g++; return
		fi
	fi
	if command -v clang >/dev/null 2>&1 && command -v clang++ >/dev/null 2>&1; then
		major="$(compiler_major clang++)"
		if [[ -n "$major" && "$major" -ge "$MIN_CLANG" ]]; then
			CC_SEL=clang; CXX_SEL=clang++; return
		fi
	fi
	missing "C++23-Compiler (gcc >= $MIN_GCC oder clang >= $MIN_CLANG) -> Paket base-devel (gcc) oder clang"
}

qt_version() {
	local q
	for q in qmake6 qmake-qt6 qtpaths6; do
		if command -v "$q" >/dev/null 2>&1; then
			if [[ "$q" == qtpaths6 ]]; then
				"$q" --qt-query QT_VERSION 2>/dev/null && return 0
			else
				"$q" -query QT_VERSION 2>/dev/null && return 0
			fi
		fi
	done
	pkg-config --modversion Qt6Core 2>/dev/null && return 0
	return 1
}

qt_has_module() {
	# Arch legt die CMake-Pakete unter $(qmake6 -query QT_INSTALL_LIBS)/cmake ab.
	local mod="$1" libs
	pkg-config --exists "$mod" 2>/dev/null && return 0
	if command -v qmake6 >/dev/null 2>&1; then
		libs="$(qmake6 -query QT_INSTALL_LIBS 2>/dev/null || true)"
		[[ -n "$libs" && -d "$libs/cmake/$mod" ]] && return 0
	fi
	local d
	for d in /usr/lib/cmake /usr/lib64/cmake /usr/lib/x86_64-linux-gnu/cmake; do
		[[ -d "$d/$mod" ]] && return 0
	done
	return 1
}

check_prereqs() {
	local v

	[[ "$(uname -s)" == Linux ]] || missing "Linux-Host (gefunden: $(uname -s))"
	[[ "$(uname -m)" == x86_64 ]] || warn "Architektur $(uname -m): geprüft ist nur x86_64"

	need_cmd git git
	need_cmd cmake cmake
	need_cmd ninja ninja
	need_cmd nasm nasm
	need_cmd python3 python
	need_cmd pkg-config "pkgconf (base-devel)"
	need_cmd make "make (base-devel)"
	need_cmd autoconf "autoconf (base-devel)"
	need_cmd automake "automake (base-devel)"
	need_cmd libtoolize "libtool (base-devel)"
	need_cmd curl curl
	need_cmd tar tar
	need_cmd zip zip
	need_cmd unzip unzip
	need_cmd setsid "util-linux"
	need_cmd du coreutils
	need_cmd df coreutils
	need_cmd realpath coreutils

	# GNU time liefert "Maximum resident set size"; das Bash-Schlüsselwort time kann das nicht.
	if [[ ! -x /usr/bin/time ]] || ! /usr/bin/time --version 2>&1 | grep -q GNU; then
		missing "GNU time unter /usr/bin/time -> Paket time"
	fi

	if command -v cmake >/dev/null 2>&1; then
		v="$(cmake --version | sed -nE '1s/.*version ([0-9]+\.[0-9]+(\.[0-9]+)?).*/\1/p')"
		if [[ -z "$v" ]] || ! version_ge "$v" "$MIN_CMAKE"; then
			missing "CMake >= $MIN_CMAKE (gefunden: ${v:-unbekannt}) -> Paket cmake"
		fi
	fi

	if command -v python3 >/dev/null 2>&1; then
		v="$(python3 -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
		version_ge "$v" "$MIN_PYTHON" || missing "Python >= $MIN_PYTHON (gefunden: $v) -> Paket python"
		python3 -c 'import venv, ensurepip' 2>/dev/null || missing "Python-Module venv/ensurepip -> Paket python"
	fi

	if command -v aclocal >/dev/null 2>&1; then
		local acdir
		acdir="$(aclocal --print-ac-dir 2>/dev/null || true)"
		[[ -n "$acdir" && -f "$acdir/ax_check_compile_flag.m4" ]] \
			|| missing "autoconf-archive (ax_check_compile_flag.m4 nicht in ${acdir:-?}) -> Paket autoconf-archive"
	else
		missing "Programm 'aclocal' -> Paket automake (base-devel)"
	fi

	pick_compiler

	if v="$(qt_version)"; then
		version_ge "$v" "$MIN_QT" || missing "Qt >= $MIN_QT (gefunden: $v) -> Paket qt6-base"
	else
		missing "Qt 6 (qmake6/qtpaths6/pkg-config Qt6Core) -> Paket qt6-base"
	fi
	qt_has_module Qt6Positioning || missing "Qt6 Positioning -> Paket qt6-positioning"
	qt_has_module Qt6Widgets || missing "Qt6 Widgets -> Paket qt6-base"

	if command -v pkg-config >/dev/null 2>&1; then
		pkg-config --exists gl || missing "OpenGL-Entwicklungsdateien (pkg-config gl) -> Paket libgl (mesa)"
		pkg-config --exists libpulse || missing "PulseAudio-Entwicklungsdateien (pkg-config libpulse) -> Paket libpulse"
		pkg-config --exists wayland-client || missing "Wayland (pkg-config wayland-client) -> Paket wayland"
		pkg-config --exists xcb-icccm || missing "xcb-icccm (pkg-config xcb-icccm) -> Paket xcb-util-wm"
	fi

	# Ladybird pinnt Rust über rust-toolchain.toml; nur rustup wertet die Datei aus.
	if ! command -v rustup >/dev/null 2>&1; then
		missing "rustup -> Paket rustup (ersetzt das Paket rust), danach als Benutzer: rustup toolchain install $RUST_CHANNEL --profile minimal -c rustfmt,clippy"
	elif ! rustup toolchain list 2>/dev/null | grep -q "^${RUST_CHANNEL}-"; then
		missing "Rust-Toolchain $RUST_CHANNEL -> als Benutzer: rustup toolchain install $RUST_CHANNEL --profile minimal -c rustfmt,clippy"
	fi
	command -v cargo >/dev/null 2>&1 || missing "Programm 'cargo' (kommt mit rustup; ~/.cargo/bin im PATH?)"

	command -v glslangValidator >/dev/null 2>&1 \
		|| warn "glslangValidator fehlt (Paket glslang): Ladybird baut dann ohne den Vulkan-Anzeigeweg der Qt-Oberfläche; für die Messung ohne Belang"
	if command -v fc-list >/dev/null 2>&1; then
		fc-list 2>/dev/null | grep -qi 'Liberation' || warn "Schrift Liberation fehlt (Paket ttf-liberation); Ladybird nutzt sie als Standardschrift"
	fi

	local mem_kib
	mem_kib="$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)"
	if [[ "$mem_kib" -lt "$MIN_RAM_KIB" ]]; then
		missing "mindestens $((MIN_RAM_KIB / 1024 / 1024)) GiB RAM (gefunden: $((mem_kib / 1024 / 1024)) GiB); Plan L1-A verlangt einen größeren Rechner"
	fi

	local probe_dir="$WORKDIR" avail
	while [[ ! -d "$probe_dir" ]]; do probe_dir="$(dirname -- "$probe_dir")"; done
	avail="$(df -B1 --output=avail -- "$probe_dir" | tail -n1 | tr -d ' ')"
	if [[ "$avail" -lt "$MAX_DISK_BYTES" && "$RESUME" == 1 ]]; then
		warn "nur $((avail / 1024 / 1024 / 1024)) GiB frei unter $probe_dir (--resume, Platzregel zählt ab Start dieses Laufs)"
	elif [[ "$avail" -lt "$MAX_DISK_BYTES" ]]; then
		missing "mindestens $((MAX_DISK_BYTES / 1024 / 1024 / 1024)) GiB frei unter $probe_dir (frei: $((avail / 1024 / 1024 / 1024)) GiB)"
	fi
}

report_prereqs() {
	local w m
	for w in "${WARNINGS[@]+"${WARNINGS[@]}"}"; do
		printf '[build.sh] Hinweis: %s\n' "$w" >&2
	done
	if [[ ${#MISSING[@]} -gt 0 ]]; then
		err "Voraussetzungen fehlen (nichts wurde installiert; Paketliste in README.md):"
		for m in "${MISSING[@]}"; do
			printf '  - %s\n' "$m" >&2
		done
		return 1
	fi
	log "Voraussetzungen erfüllt (Compiler: $CC_SEL/$CXX_SEL, Jobs: $JOBS, Arbeitsordner: $WORKDIR)"
	return 0
}

# ---------------------------------------------------------------------------------------------
# Ablauf
# ---------------------------------------------------------------------------------------------

LINK_POOL=2
TRIPLET=""
START_EPOCH=""
MONITOR_PID=""
STATUS="failed"
REASON=""
ATTEMPTS=0
CURRENT_PHASE=""

cmake_args() {
	printf '%s\n' \
		--preset Release \
		-S "$SRC_DIR" \
		-B "$BUILD_DIR" \
		"-DCMAKE_C_COMPILER=$CC_SEL" \
		"-DCMAKE_CXX_COMPILER=$CXX_SEL" \
		-DVCPKG_MANIFEST_INSTALL=OFF \
		"-DVCPKG_TARGET_TRIPLET=$TRIPLET" \
		"-DVCPKG_HOST_TRIPLET=$TRIPLET" \
		-DENABLE_JS_JIT=OFF \
		"-DLAGOM_LINK_POOL_SIZE=$LINK_POOL" \
		-DLADYBIRD_ENABLE_CCACHE=OFF
}

print_plan() {
	TRIPLET="${TRIPLET:-x64-linux-dynamic}"
	cat <<EOF
Geplante Schritte (DRY_RUN=1, nichts wird ausgeführt):
  1. Quelle: git init $SRC_DIR; git fetch --depth 1 $LADYBIRD_REPO $LADYBIRD_COMMIT; checkout; Prüfung git rev-parse HEAD
  2. Bauordner: $BUILD_DIR, Verweis $SRC_DIR/Build/release -> $BUILD_DIR (dort erwartet Meta/ladybird.py den Release-Bau)
  3. Abhängigkeiten (vcpkg): python3 Meta/ladybird.py deps --preset Release --cc $CC_SEL --cxx $CXX_SEL --jobs $JOBS
  4. Konfigurieren: cmake $(cmake_args | tr '\n' ' ')
  5. Bauen: ninja -C $BUILD_DIR -j $JOBS ladybird   (unter /usr/bin/time -v)
     bei OOM einmal: LAGOM_LINK_POOL_SIZE=1, neu konfigurieren, weiterbauen; zweites OOM -> Abbruch
  6. Prüfen: $BUILD_DIR/bin/Ladybird vorhanden; Größen aller Dateien in bin/, lib/, libexec/
  7. Ergebnis: $STATS_FILE, Logs in $LOG_DIR
Abbruchregeln: Gesamtzeit > $((MAX_SECONDS / 3600)) h, Platzzuwachs > $((MAX_DISK_BYTES / 1024 / 1024 / 1024)) GiB, OOM auch mit LAGOM_LINK_POOL_SIZE=1
EOF
}

# Hintergrundwächter: Zeit, Platzzuwachs auf dem Dateisystem des Arbeitsordners, belegter Speicher.
monitor() {
	local avail0="$1" now avail growth used peak_used=0 peak_growth=0 reason pgid
	while :; do
		now="$(date +%s)"
		used="$(awk '/^MemTotal:/ {t=$2} /^MemAvailable:/ {a=$2} END {print t-a}' /proc/meminfo)"
		if [[ "$used" -gt "$peak_used" ]]; then
			peak_used="$used"; echo "$peak_used" >"$STATE_DIR/peak_system_used_kib"
		fi
		avail="$(df -B1 --output=avail -- "$WORKDIR" | tail -n1 | tr -d ' ')"
		growth=$((avail0 - avail))
		if [[ "$growth" -gt "$peak_growth" ]]; then
			peak_growth="$growth"; echo "$peak_growth" >"$STATE_DIR/peak_disk_growth_bytes"
		fi
		reason=""
		if [[ $((now - START_EPOCH)) -gt "$MAX_SECONDS" ]]; then
			reason="time"
		elif [[ "$growth" -gt "$MAX_DISK_BYTES" ]]; then
			reason="disk"
		fi
		if [[ -n "$reason" ]]; then
			echo "$reason" >"$STATE_DIR/abort"
			if [[ -s "$STATE_DIR/step_pgid" ]]; then
				pgid="$(cat "$STATE_DIR/step_pgid")"
				kill -TERM -- "-$pgid" 2>/dev/null || true
				sleep 30
				kill -KILL -- "-$pgid" 2>/dev/null || true
			fi
			return 0
		fi
		sleep "$MONITOR_INTERVAL"
	done
}

cgroup_oom_kills() {
	# Zähler oom_kill der eigenen cgroup (v2); 0, wenn nicht lesbar.
	local cg file
	cg="$(awk -F: '$1 == "0" {print $3}' /proc/self/cgroup 2>/dev/null || true)"
	file="/sys/fs/cgroup${cg}/memory.events"
	if [[ -n "$cg" && -r "$file" ]]; then
		awk '$1 == "oom_kill" {print $2}' "$file"
	else
		echo 0
	fi
}

log_shows_oom() {
	grep -Eqi 'Killed signal terminated program|fatal error: Killed|virtual memory exhausted|out of memory|Cannot allocate memory|std::bad_alloc|SIGKILL|signal: 9|exit status 137|exit code 137' "$1"
}

# run_step NAME BEFEHL...: in eigener Prozessgruppe unter /usr/bin/time -v, Ausgabe nach logs/NAME.log.
run_step() {
	local name="$1"; shift
	local rc=0 t0 t1 pid
	CURRENT_PHASE="$name"
	[[ -e "$STATE_DIR/abort" ]] && return 99
	log "Schritt $name startet (Log: $LOG_DIR/$name.log)"
	t0="$(date +%s)"
	setsid /usr/bin/time -v -o "$STATE_DIR/time-$name.txt" "$@" >>"$LOG_DIR/$name.log" 2>&1 &
	pid=$!
	echo "$pid" >"$STATE_DIR/step_pgid"
	wait "$pid" || rc=$?
	: >"$STATE_DIR/step_pgid"
	t1="$(date +%s)"
	echo $((t1 - t0)) >>"$STATE_DIR/seconds-$name"
	log "Schritt $name endet nach $((t1 - t0)) s mit Code $rc"
	[[ -e "$STATE_DIR/abort" ]] && return 99
	return "$rc"
}

write_stats() {
	local end_epoch
	end_epoch="$(date +%s)"
	if [[ -d "$SRC_DIR" || -d "$BUILD_DIR" ]]; then
		log "Platz wird gezählt (du -sb, kann einige Minuten dauern)"
	fi
	env STATUS="$STATUS" REASON="$REASON" ATTEMPTS="$ATTEMPTS" LINK_POOL="$LINK_POOL" JOBS="$JOBS" \
	START_EPOCH="$START_EPOCH" END_EPOCH="$end_epoch" RESUME="$RESUME" PHASE="$CURRENT_PHASE" \
	SRC_DIR="$SRC_DIR" BUILD_DIR="$BUILD_DIR" STATE_DIR="$STATE_DIR" STATS_FILE="$STATS_FILE" \
	EXPECTED_COMMIT="$LADYBIRD_COMMIT" CC_SEL="$CC_SEL" CXX_SEL="$CXX_SEL" TRIPLET="$TRIPLET" \
	MAX_SECONDS="$MAX_SECONDS" MAX_DISK_BYTES="$MAX_DISK_BYTES" MIN_RAM_KIB="$MIN_RAM_KIB" SCRIPT_DIR="$SCRIPT_DIR" \
	CMAKE_ARGS="$(cmake_args)" \
		python3 -I - <<'PY'
import json, os, re, subprocess, time

env = os.environ

def sh(*args, cwd=None, timeout=120):
    # Fehlende Programme ergeben leere Felder, keinen Abbruch: die Zahlen sollen auch bei Teilständen ankommen.
    try:
        return subprocess.run(args, capture_output=True, text=True, timeout=timeout, cwd=cwd).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""

def first_line(text):
    return text.splitlines()[0] if text else ""

def read_int(path):
    try:
        with open(path) as f:
            return int(f.read().strip() or 0)
    except (OSError, ValueError):
        return None

def du_bytes(path):
    if not os.path.lexists(path):
        return 0
    out = sh("du", "-sb", "--", path, timeout=None)  # große Bäume brauchen Minuten
    return int(out.split()[0]) if out else None

state = env["STATE_DIR"]
src, build = env["SRC_DIR"], env["BUILD_DIR"]

# Spitzen-RSS je Schritt aus /usr/bin/time -v (größter Einzelprozess, nicht Summe).
peak_rss = {}
phases = {}
for name in ("fetch", "deps", "configure", "configure-retry", "build", "build-retry"):
    tfile = os.path.join(state, f"time-{name}.txt")
    if os.path.exists(tfile):
        with open(tfile, errors="replace") as f:
            m = re.search(r"Maximum resident set size \(kbytes\): (\d+)", f.read())
        if m:
            peak_rss[name] = int(m.group(1))
    sfile = os.path.join(state, f"seconds-{name}")
    if os.path.exists(sfile):
        with open(sfile) as f:
            phases[name] = sum(int(x) for x in f.read().split())

binaries = {}
for sub in ("bin", "lib", "libexec"):
    root = os.path.join(build, sub)
    if not os.path.isdir(root):
        continue
    for dirpath, dirnames, filenames in os.walk(root):
        for fn in filenames:
            p = os.path.join(dirpath, fn)
            if os.path.islink(p) or not os.path.isfile(p):
                continue  # .so-Verweise nicht doppelt zählen
            binaries[os.path.relpath(p, build)] = os.stat(p).st_size

head = sh("git", "-C", src, "rev-parse", "HEAD") if os.path.isdir(os.path.join(src, ".git")) else ""
disk_parts = {
    "src_without_build": None,
    "vcpkg_tool": du_bytes(os.path.join(src, "Build", "vcpkg-release")),
    "vcpkg_binary_cache": du_bytes(os.path.join(src, "Build", "caches")),
    "build": du_bytes(build),
}
src_total = du_bytes(src)
if src_total is not None:
    # src/Build/release ist nur ein Verweis; du folgt ihm nicht, Bau wird separat gezählt.
    disk_parts["src_without_build"] = src_total
disk_bytes = None
if src_total is not None and disk_parts["build"] is not None:
    disk_bytes = src_total + disk_parts["build"]

def meminfo_kib():
    with open("/proc/meminfo") as f:
        for line in f:
            if line.startswith("MemTotal:"):
                return int(line.split()[1])
    return None

def cpu_model():
    with open("/proc/cpuinfo", errors="replace") as f:
        for line in f:
            if line.startswith("model name"):
                return line.split(":", 1)[1].strip()
    return ""

def distro():
    try:
        with open("/etc/os-release") as f:
            for line in f:
                if line.startswith("PRETTY_NAME="):
                    return line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return ""

qt = sh("qmake6", "-query", "QT_VERSION") or sh("pkg-config", "--modversion", "Qt6Core")

stats = {
    "status": env["STATUS"],
    "reason": env["REASON"] or None,
    "phase": env["PHASE"] or None,
    "commit": head or None,
    "commit_expected": env["EXPECTED_COMMIT"],
    "commit_ok": head == env["EXPECTED_COMMIT"],
    "seconds": int(env["END_EPOCH"]) - int(env["START_EPOCH"]),
    "phase_seconds": phases,
    "peak_rss_kib": max(peak_rss.values()) if peak_rss else None,
    "peak_rss_kib_by_phase": peak_rss,
    "peak_system_used_kib": read_int(os.path.join(state, "peak_system_used_kib")),
    "peak_disk_growth_bytes": read_int(os.path.join(state, "peak_disk_growth_bytes")),
    "disk_bytes": disk_bytes,
    "disk_bytes_detail": disk_parts,
    "binaries": dict(sorted(binaries.items())),
    "binaries_total_bytes": sum(binaries.values()),
    "ladybird_present": os.path.isfile(os.path.join(build, "bin", "Ladybird")),
    "jobs": int(env["JOBS"]),
    "link_pool_size": int(env["LINK_POOL"]),
    "build_attempts": int(env["ATTEMPTS"]),
    "resumed": env["RESUME"] == "1",
    "cmake_args": env["CMAKE_ARGS"].splitlines(),
    "vcpkg_triplet": env["TRIPLET"] or None,
    "limits": {"max_seconds": int(env["MAX_SECONDS"]), "max_disk_bytes": int(env["MAX_DISK_BYTES"]),
               "min_ram_kib": int(env["MIN_RAM_KIB"])},
    "started_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(int(env["START_EPOCH"]))),
    "finished_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(int(env["END_EPOCH"]))),
    "host": {
        "cpu": cpu_model(),
        "logical_cpus": os.cpu_count(),
        "mem_total_kib": meminfo_kib(),
        "distro": distro(),
        "kernel": sh("uname", "-r"),
        "cc": first_line(sh(env["CC_SEL"], "--version")) if env["CC_SEL"] else "",
        "cxx": first_line(sh(env["CXX_SEL"], "--version")) if env["CXX_SEL"] else "",
        "cmake": first_line(sh("cmake", "--version")),
        "ninja": sh("ninja", "--version"),
        "qt": qt,
        # Im Quellordner, damit rustup die Version aus rust-toolchain.toml meldet.
        "rustc": sh("rustc", "--version", cwd=src if os.path.isdir(src) else None),
        "python": sh("python3", "--version"),
    },
    "probe_script_head": sh("git", "-C", env["SCRIPT_DIR"], "rev-parse", "HEAD") or None,
}

tmp = env["STATS_FILE"] + ".tmp"
with open(tmp, "w") as f:
    json.dump(stats, f, indent=2, ensure_ascii=False)
    f.write("\n")
os.replace(tmp, env["STATS_FILE"])
PY
}

cleanup() {
	if [[ -n "$MONITOR_PID" ]]; then
		kill "$MONITOR_PID" 2>/dev/null || true
		wait "$MONITOR_PID" 2>/dev/null || true
	fi
}

# finish CODE: Wächter beenden, build-stats.json schreiben, mit CODE enden.
finish() {
	local code="$1"
	cleanup
	MONITOR_PID=""
	if write_stats; then
		log "Ergebnis: $STATS_FILE (Status $STATUS${REASON:+, Grund $REASON})"
	else
		err "build-stats.json konnte nicht geschrieben werden"
		[[ "$code" -eq 0 ]] && code=4
	fi
	if [[ "$code" -eq 3 ]]; then
		err "Abbruchregel gegriffen ($REASON). Nicht erneut starten ohne Rückfrage; Stand und Zahlen in $STATS_FILE melden."
	fi
	exit "$code"
}

abort_reason_from_state() {
	if [[ -e "$STATE_DIR/abort" ]]; then
		cat "$STATE_DIR/abort"
	fi
}

fetch_source() {
	if [[ -d "$SRC_DIR/.git" ]]; then
		local head
		head="$(git -C "$SRC_DIR" rev-parse HEAD 2>/dev/null || true)"
		if [[ "$head" == "$LADYBIRD_COMMIT" ]]; then
			log "Quelle steht schon auf $LADYBIRD_COMMIT"
			return 0
		fi
		STATUS="failed"; REASON="commit_mismatch"
		err "$SRC_DIR steht auf '${head:-?}', erwartet $LADYBIRD_COMMIT; Arbeitsordner wechseln"
		finish 5
	fi
	mkdir -p "$SRC_DIR"
	# Flach auf genau den festen Commit holen; GitHub erlaubt das Holen über den vollen Hash.
	# shellcheck disable=SC2016 # die Argumente wertet die innere bash aus
	run_step fetch bash -c '
		set -euo pipefail
		git -C "$1" init -q
		git -C "$1" remote add origin "$2"
		git -C "$1" fetch --depth 1 origin "$3"
		git -C "$1" -c advice.detachedHead=false checkout -q --detach FETCH_HEAD
	' fetch "$SRC_DIR" "$LADYBIRD_REPO" "$LADYBIRD_COMMIT" || {
		local rc=$?
		[[ $rc -eq 99 ]] && { STATUS="aborted"; REASON="$(abort_reason_from_state)"; finish 3; }
		STATUS="failed"; REASON="fetch"; finish 4
	}
	local head
	head="$(git -C "$SRC_DIR" rev-parse HEAD)"
	if [[ "$head" != "$LADYBIRD_COMMIT" ]]; then
		STATUS="failed"; REASON="commit_mismatch"
		err "HEAD ist $head, erwartet $LADYBIRD_COMMIT"
		finish 5
	fi
	log "Quelle auf $head"
}

check_source_pins() {
	local channel
	channel="$(sed -nE 's/^channel *= *"([^"]+)".*/\1/p' "$SRC_DIR/rust-toolchain.toml" 2>/dev/null || true)"
	if [[ "$channel" != "$RUST_CHANNEL" ]]; then
		STATUS="failed"; REASON="rust_channel"
		err "rust-toolchain.toml nennt '${channel:-?}', das Skript erwartet $RUST_CHANNEL"
		finish 1
	fi
	TRIPLET="$(cd "$SRC_DIR" && python3 -c '
import sys
sys.path.insert(0, "Meta")
from Utils.host_platform import Platform
from Utils.vcpkg_deps import determine_triplet
print(determine_triplet(Platform(), "Release"))
')" || { STATUS="failed"; REASON="triplet"; err "vcpkg-Triplet nicht bestimmbar"; finish 1; }
	log "vcpkg-Triplet: $TRIPLET"
}

# Umgebung wie Meta/ladybird.py configure_build_env für das Preset Release.
export_build_env() {
	export LADYBIRD_SOURCE_DIR="$SRC_DIR"
	export LADYBIRD_MAIN_SOURCE_DIR="$SRC_DIR"
	export VCPKG_ROOT="$SRC_DIR/Build/vcpkg-release"
	export PATH="$PATH:$VCPKG_ROOT"
	export VCPKG_MAX_CONCURRENCY="$JOBS"
	# Kalter Bau: kein ccache, auch nicht in vcpkg-Ports.
	export CCACHE_DISABLE=1
}

configure() {
	local name="$1"
	local -a args
	mapfile -t args < <(cmake_args)
	# Aus dem Quellordner, damit cmake die CMakePresets.json sicher findet.
	# shellcheck disable=SC2016 # die Argumente wertet die innere bash aus
	run_step "$name" bash -c 'cd "$1" && shift && exec cmake "$@"' configure "$SRC_DIR" "${args[@]}"
}

# Strg+C erreicht die Bauschritte nicht (eigene Sitzung über setsid), deshalb hier weiterreichen.
# shellcheck disable=SC2317 # über trap aufgerufen
on_signal() {
	local pgid
	if [[ -s "$STATE_DIR/step_pgid" ]]; then
		pgid="$(cat "$STATE_DIR/step_pgid")"
		kill -TERM -- "-$pgid" 2>/dev/null || true
	fi
	STATUS="aborted"; REASON="signal"
	finish 3
}

main() {
	check_prereqs
	if ! report_prereqs; then
		[[ "$DRY_RUN" == 1 ]] && print_plan
		exit 2
	fi
	if [[ "$CHECK_ONLY" == 1 ]]; then
		exit 0
	fi
	if [[ "$DRY_RUN" == 1 ]]; then
		print_plan
		exit 0
	fi

	if [[ -e "$WORKDIR" && "$RESUME" == 0 ]] && [[ -n "$(ls -A -- "$WORKDIR" 2>/dev/null)" ]]; then
		die 1 "$WORKDIR ist nicht leer; für eine Messung einen frischen Ordner nehmen oder --resume angeben"
	fi
	mkdir -p "$WORKDIR" "$LOG_DIR" "$STATE_DIR" "$BUILD_DIR"
	# Zeiten und Spitzenwerte gelten je Lauf; auch --resume beginnt mit leerem Zustand.
	rm -f "$STATE_DIR/abort" "$STATE_DIR/step_pgid" "$STATE_DIR"/seconds-* "$STATE_DIR"/time-*.txt \
		"$STATE_DIR/peak_system_used_kib" "$STATE_DIR/peak_disk_growth_bytes"

	START_EPOCH="$(date +%s)"
	local avail0
	avail0="$(df -B1 --output=avail -- "$WORKDIR" | tail -n1 | tr -d ' ')"
	monitor "$avail0" &
	MONITOR_PID=$!
	trap 'cleanup' EXIT
	trap on_signal INT TERM

	fetch_source
	check_source_pins
	export_build_env

	mkdir -p "$SRC_DIR/Build"
	if [[ ! -L "$SRC_DIR/Build/release" ]]; then
		[[ -e "$SRC_DIR/Build/release" ]] && die 1 "$SRC_DIR/Build/release existiert und ist kein Verweis"
		ln -s "$BUILD_DIR" "$SRC_DIR/Build/release"
	fi

	local rc=0
	run_step deps python3 "$SRC_DIR/Meta/ladybird.py" deps --preset Release \
		--cc "$CC_SEL" --cxx "$CXX_SEL" --jobs "$JOBS" || rc=$?
	if [[ $rc -ne 0 ]]; then
		if [[ $rc -eq 99 ]]; then STATUS="aborted"; REASON="$(abort_reason_from_state)"; finish 3; fi
		if log_shows_oom "$LOG_DIR/deps.log"; then STATUS="aborted"; REASON="oom"; finish 3; fi
		STATUS="failed"; REASON="deps"; finish 4
	fi

	local oom0 oom1 attempt
	for attempt in 1 2; do
		ATTEMPTS="$attempt"
		local cname="configure" bname="build"
		if [[ $attempt -eq 2 ]]; then cname="configure-retry"; bname="build-retry"; fi
		rc=0
		configure "$cname" || rc=$?
		if [[ $rc -ne 0 ]]; then
			if [[ $rc -eq 99 ]]; then STATUS="aborted"; REASON="$(abort_reason_from_state)"; finish 3; fi
			STATUS="failed"; REASON="configure"; finish 4
		fi
		if ! grep -q '^ENABLE_JS_JIT:BOOL=OFF$' "$BUILD_DIR/CMakeCache.txt"; then
			STATUS="failed"; REASON="jit_not_off"; err "ENABLE_JS_JIT steht im CMake-Cache nicht auf OFF"; finish 4
		fi
		oom0="$(cgroup_oom_kills)"
		rc=0
		run_step "$bname" ninja -C "$BUILD_DIR" -j "$JOBS" ladybird || rc=$?
		oom1="$(cgroup_oom_kills)"
		[[ $rc -eq 0 ]] && break
		if [[ $rc -eq 99 ]]; then STATUS="aborted"; REASON="$(abort_reason_from_state)"; finish 3; fi
		if [[ "$oom1" -gt "$oom0" ]] || log_shows_oom "$LOG_DIR/$bname.log"; then
			if [[ $attempt -eq 1 ]]; then
				log "OOM erkannt; zweiter und letzter Versuch mit LAGOM_LINK_POOL_SIZE=1"
				LINK_POOL=1
				continue
			fi
			STATUS="aborted"; REASON="oom"; finish 3
		fi
		STATUS="failed"; REASON="build"; finish 4
	done

	if [[ ! -x "$BUILD_DIR/bin/Ladybird" ]]; then
		STATUS="failed"; REASON="no_binary"; err "$BUILD_DIR/bin/Ladybird fehlt trotz erfolgreichem Bau"; finish 4
	fi
	STATUS="ok"; REASON=""
	finish 0
}

main
