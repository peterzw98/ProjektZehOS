#!/usr/bin/env bash
# Baut den statischen LibJS-Nachweis in einer Alpine-Umgebung.
set -euo pipefail

readonly commit=6fba5b78a1043fe93b3451204e04dcc0e3bd983a
readonly rust_channel=1.98.0
jobs=3
workdir="$HOME/ladybird-probe/v4"
check_only=0
resume=0

usage() {
    echo 'Aufruf: build.sh --target js [--jobs N] [--workdir DIR] [--check] [--resume]'
}

die() {
    echo "[musl/build.sh] $*" >&2
    exit 1
}

while (($#)); do
    case "$1" in
        --target)
            [[ $# -ge 2 && "$2" == js ]] || die 'V4 unterstützt --target js.'
            shift 2 ;;
        --jobs)
            [[ $# -ge 2 && "$2" =~ ^[1-9][0-9]*$ ]] || die '--jobs braucht eine positive Ganzzahl.'
            jobs="$2"; shift 2 ;;
        --workdir)
            [[ $# -ge 2 && -n "$2" ]] || die '--workdir braucht einen Pfad.'
            workdir="$2"; shift 2 ;;
        --check) check_only=1; shift ;;
        --resume) resume=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "Unbekannte Option: $1" ;;
    esac
done

[[ "$(uname -m)" == x86_64 && -f /etc/alpine-release ]] || die 'Alpine x86_64 ist erforderlich.'
(( EUID != 0 )) || die 'Der Bau braucht einen unprivilegierten Benutzer.'
readonly ninja_dir=/usr/lib/ninja-build/bin
readonly ncurses_archive=/usr/lib/libncursesw.a
[[ -x "$ninja_dir/ninja" ]] || die 'Alpine-Paket ninja-build fehlt.'
/sbin/apk info -e gcompat >/dev/null || die 'Alpine-Paket gcompat fehlt (für Skias gn).'
[[ -r "$ncurses_archive" ]] || die 'Alpine-Paket ncurses-static fehlt.'
# Samurai akzeptiert das von vcpkg verwendete `ninja install -v` nicht.
export PATH="$ninja_dir:$PATH"
for tool in git realpath gcc g++ cmake ninja python3 rustup readelf strace timeout; do
    command -v "$tool" >/dev/null || die "Werkzeug fehlt: $tool"
done
[[ "$(gcc -dumpmachine)" == x86_64-*-linux-musl ]] || die 'GCC verwendet kein x86_64-musl-Ziel.'
rustup run "$rust_channel" rustc --version
parent=$(realpath -m "$HOME/ladybird-probe")
workdir=$(realpath -m -- "$workdir")
[[ "$workdir" == "$parent/"* ]] || die "Arbeitsordner muss unter $parent liegen."
if (( check_only )); then
    printf 'Voraussetzungen erfüllt. Jobs: %s. Arbeitsordner: %s\n' "$jobs" "$workdir"
    exit 0
fi

src="$workdir/src"
build="$workdir/build"
if (( resume )); then
    [[ -f "$workdir/.ladybird-musl-workdir" && -d "$src/.git" && "$(cat "$workdir/.ladybird-musl-workdir")" == "$commit" ]] || die 'Kein fortsetzbarer V4-Arbeitsordner.'
else
    [[ ! -e "$workdir" ]] || die 'Arbeitsordner existiert bereits. Für einen begonnenen V4-Bau --resume verwenden.'
    mkdir -p "$src" "$build" "$workdir/logs"
    printf '%s\n' "$commit" > "$workdir/.ladybird-musl-workdir"
    git -C "$src" init -q
    git -C "$src" remote add origin https://github.com/LadybirdBrowser/ladybird.git
fi

if ! git -C "$src" cat-file -e "$commit^{commit}" 2>/dev/null; then
    git -C "$src" fetch --depth 1 origin "$commit" 2>&1 | tee "$workdir/logs/fetch.log"
fi
head=$(git -C "$src" rev-parse --verify HEAD 2>/dev/null || true)
if [[ -z "$head" ]]; then
    git -C "$src" checkout --detach "$commit"
fi
[[ "$(git -C "$src" rev-parse HEAD)" == "$commit" ]] || die 'Ladybird-Quellstand stimmt nicht.'
mkdir -p "$src/Build"
if [[ ! -L "$src/Build/distribution" ]]; then
    ln -s "$build" "$src/Build/distribution"
fi
[[ "$(realpath "$src/Build/distribution")" == "$build" ]] || die 'Distribution verweist auf einen anderen Bauordner.'

cd "$src"
export RUSTUP_TOOLCHAIN="$rust_channel" CCACHE_DISABLE=1
export LADYBIRD_SOURCE_DIR="$src" LADYBIRD_MAIN_SOURCE_DIR="$src"
export VCPKG_ROOT="$src/Build/vcpkg-distribution" VCPKG_MAX_CONCURRENCY="$jobs"
# vcpkg soll die Alpine-Werkzeuge für reguläre Bauschritte verwenden.
export VCPKG_FORCE_SYSTEM_BINARIES=1
triplet=$(PYTHONPATH=Meta python3 -c 'from Utils.host_platform import Platform; from Utils.vcpkg_deps import determine_triplet; print(determine_triplet(Platform(), "Distribution"))')
printf 'Ladybird %s, Triplet %s, Jobs %s\n' "$commit" "$triplet" "$jobs"

python3 Meta/ladybird.py deps --preset Distribution --cc gcc --cxx g++ --jobs "$jobs" \
    2>&1 | tee "$workdir/logs/deps.log"
cmake --preset Distribution -S "$src" -B "$build" \
    -DCMAKE_C_COMPILER=gcc -DCMAKE_CXX_COMPILER=g++ \
    -DVCPKG_MANIFEST_INSTALL=OFF "-DVCPKG_TARGET_TRIPLET=$triplet" "-DVCPKG_HOST_TRIPLET=$triplet" \
    -DENABLE_GUI_TARGETS=OFF -DBUILD_TESTING=OFF -DBUILD_SHARED_LIBS=OFF -DENABLE_JS_JIT=OFF \
    -DLADYBIRD_ENABLE_CCACHE=OFF -DLADYBIRD_ENABLE_LTO=OFF -DLADYBIRD_GENERATE_SYMBOL_FILES=OFF \
    -DCMAKE_EXE_LINKER_FLAGS=-static "-Dpkgcfg_lib_LIBEDIT_ncurses:FILEPATH=$ncurses_archive" \
    2>&1 | tee "$workdir/logs/configure.log"
grep -qx 'ENABLE_JS_JIT:BOOL=OFF' "$build/CMakeCache.txt" || die 'JavaScript-JIT bleibt aktiv.'
/usr/bin/time -v ninja -C "$build" -j "$jobs" js 2>&1 | tee "$workdir/logs/build.log"
binary="$build/bin/js"
[[ -x "$binary" ]] || die 'Ausführbare Datei build/bin/js fehlt.'
export LC_ALL=C
header=$(readelf -h "$binary")
segments=$(readelf -lW "$binary")
dynamic=$(readelf -dW "$binary")
grep -q 'Machine:.*X86-64' <<<"$header" || die 'js ist keine x86-64-ELF-Datei.'
if grep -Eq '^[[:space:]]*INTERP[[:space:]]' <<<"$segments" || grep -q '(NEEDED)' <<<"$dynamic"; then
    die 'js benötigt einen dynamischen Loader oder Shared Libraries.'
fi

# Der REPL-Zustand liegt außerhalb des normalen Anwendungsverzeichnisses.
mkdir -p "$workdir/smoke-home" "$workdir/strace"
export HOME="$workdir/smoke-home"
arithmetic=$("$binary" -i -l -c '1+1')
[[ "$arithmetic" == 2 ]] || die "1+1 ergab '$arithmetic' statt 2."
sorted=$("$binary" -i -l -c '
    const size = 10000;
    const values = Array.from({ length: size }, (_, i) => size - i);
    values.sort((left, right) => left - right);
    values.every((value, index) => value === index + 1) ? values.length : -1;
')
[[ "$sorted" == 10000 ]] || die "Array-Sortierprobe ergab '$sorted' statt 10000."

trace_dir=$(mktemp -d "$workdir/strace/run.XXXXXX")
strace -ff -yy -o "$trace_dir/js" "$binary" -c '1+1' > "$workdir/logs/strace-stdout.log"
compgen -G "$trace_dir/js.*" >/dev/null || die 'strace hat kein Protokoll angelegt.'
printf 'Statische x86-64-ELF: %s (%s Bytes)\n' "$binary" "$(stat -c %s "$binary")"
printf 'Rechenprobe und Array-Sortierung bestanden. Systemaufrufe: %s\n' "$trace_dir"
