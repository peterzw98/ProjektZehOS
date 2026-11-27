#!/bin/sh
# V25: dynamisch gelinkte Programme gegen Alpine-musl (ld-musl, libstdc++, libgcc_s). Braucht eine Alpine-Wurzel mit
# build-base in $ALPINE_ROOT (etwa minirootfs plus "apk add build-base"); gebaut wird darin per Benutzer-Namensraum
# (unshare -r, chroot), ohne root. Ohne ALPINE_ROOT wird nichts gebaut. Die Bibliotheken kommen nach $OUT_DIR/lib und
# $OUT_DIR/usr/lib, wie sie in der Prozesswurzel liegen muessen; die Erwartungen stammen aus einem Lauf in derselben Wurzel.
set -eu
[ -n "${ALPINE_ROOT:-}" ] || { echo "v25: ALPINE_ROOT fehlt, dynamische Programme nicht gebaut" >&2; exit 0; }
work=$ALPINE_ROOT/tmp/v25
rm -rf "$work"; mkdir -p "$work"
cp "$SRC_DIR"/dyn/*.c "$SRC_DIR"/dyn/*.cpp "$work"/
unshare -r chroot "$ALPINE_ROOT" /usr/bin/env -i PATH=/usr/bin:/bin /bin/sh -c '
    set -e; cd /tmp/v25
    gcc -O1 -Wall -Wextra -Werror -std=gnu11 -o dyn_hello dyn_hello.c
    g++ -O1 -Wall -Wextra -Werror -std=c++17 -pthread -o dyn_cxx dyn_cxx.cpp
    gcc -O1 -Wall -Wextra -Werror -std=gnu11 -Wl,--dynamic-linker=/lib/ld-fehlt.so.1 -o ohne_interp dyn_hello.c'
# ohne_interp (V25b): PT_INTERP nennt eine fehlende Datei; execve muss ENOENT liefern.
cp "$work/dyn_hello" "$work/dyn_cxx" "$work/ohne_interp" "$OUT_DIR"/
mkdir -p "$OUT_DIR/lib" "$OUT_DIR/usr/lib"
cp "$ALPINE_ROOT/lib/ld-musl-x86_64.so.1" "$OUT_DIR/lib/"
cp -L "$ALPINE_ROOT/usr/lib/libstdc++.so.6" "$ALPINE_ROOT/usr/lib/libgcc_s.so.1" "$OUT_DIR/usr/lib/"
echo "gebaut: out/v25/dyn_hello out/v25/dyn_cxx out/v25/ohne_interp (+ lib, usr/lib)"
