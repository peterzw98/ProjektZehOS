#!/bin/sh
# Baut jede src/<satz>/*.c statisch für x86_64-linux-musl nach out/<satz>/ und
# ruft danach src/<satz>/build.sh, falls vorhanden (Aufruf: build.sh [satz ...]).
# Übersetzer: $CC (Standard musl-gcc; sonst x86_64-linux-musl-gcc). Binärdateien
# werden nicht eingecheckt (out/ ist ignoriert).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
cc=${CC:-}
if [ -z "$cc" ]; then
    for c in musl-gcc x86_64-linux-musl-gcc; do
        if command -v "$c" >/dev/null 2>&1; then cc=$c; break; fi
    done
fi
[ -n "$cc" ] || { echo "build.sh: kein musl-Übersetzer (musl-tools installieren oder CC setzen)" >&2; exit 1; }
if [ "$#" -gt 0 ]; then sets=$*; else sets=$(cd "$here/src" && ls -d */ | tr -d /); fi
for s in $sets; do
    src=$here/src/$s
    out=$here/out/$s
    [ -d "$src" ] || { echo "build.sh: Satz $s fehlt" >&2; exit 1; }
    rm -rf "$out"; mkdir -p "$out"
    for f in "$src"/*.c; do
        [ -e "$f" ] || continue
        n=$(basename "$f" .c)
        # -fno-pie/-no-pie: statisch und lagefest; -O1 -fno-builtin hält die Programme einfach.
        $cc -static -fno-pie -no-pie -O1 -Wall -Wextra -Werror -std=gnu11 -o "$out/$n" "$f"
        echo "gebaut: out/$s/$n"
    done
    if [ -f "$src/build.sh" ]; then
        OUT_DIR=$out SRC_DIR=$src sh "$src/build.sh"
    fi
done
