#!/bin/sh
# Werkzeuge für Satz "tools" (N1-E3): toybox (0BSD) und Rust-Programme, beide statisch
# für x86_64-linux-musl. Wird von ../../build.sh mit OUT_DIR und SRC_DIR aufgerufen,
# nachdem die C-Treiber (src/tools/*.c) schon nach $OUT_DIR gebaut sind.
#
# Eingaben (alles außerhalb des Repositorys, nichts wird eingecheckt):
#   TOYBOX_TARBALL  Pfad zu toybox-0.8.15.tar.gz; fehlt er, lädt das Skript ihn nach $OUT_DIR/../.cache
#   RUSTC           Pfad zu rustc (Standard: rustc im PATH); braucht das Ziel x86_64-unknown-linux-musl
#   CC              musl-Übersetzer; ../../build.sh reicht den von ihm gewählten weiter (Standard hier: musl-gcc)
# Ergebnis: $OUT_DIR/tbs_{cat,ls,wc} (eigenständig, S1), $OUT_DIR/tb/{toybox,cat,ls,wc,sh,echo} (Mehrfachprogramm
# mit Verknüpfungen für die Treiber tb_*), $OUT_DIR/rs_file_time, $OUT_DIR/rs_thread (Rust).
set -eu
TOYBOX_VERSION=0.8.15
# SHA-256 des Quellarchivs (beim Anlegen aus dem Download von landley.net errechnet, nicht aus einer zweiten Quelle bestätigt).
TOYBOX_SHA256=44e2bab43dfe2c171743297631ce558c6d9ed38b87a66fd751cef617574b375e
TOYBOX_URL=https://landley.net/toybox/downloads/toybox-$TOYBOX_VERSION.tar.gz
: "${OUT_DIR:?}" "${SRC_DIR:?}"
cc=${CC:-musl-gcc}
rustc=${RUSTC:-rustc}
work=$OUT_DIR/../.work-tools
cache=$OUT_DIR/../.cache
mkdir -p "$cache"
rm -rf "$work"; mkdir -p "$work"

tar_=${TOYBOX_TARBALL:-$cache/toybox-$TOYBOX_VERSION.tar.gz}
if [ ! -f "$tar_" ]; then
    echo "tools/build.sh: lade $TOYBOX_URL" >&2
    # Erst in eine Teildatei, dann umbenennen: ein abgebrochener Download soll keine unvollständige Datei im Cache hinterlassen.
    curl -fsSL -o "$tar_.part" "$TOYBOX_URL" && mv "$tar_.part" "$tar_" || { rm -f "$tar_.part"; echo "tools/build.sh: Download fehlgeschlagen" >&2; exit 1; }
fi
echo "$TOYBOX_SHA256  $tar_" | sha256sum -c - >/dev/null || { echo "tools/build.sh: SHA-256 von $tar_ stimmt nicht" >&2; exit 1; }
tar -C "$work" -xf "$tar_"
tb=$work/toybox-$TOYBOX_VERSION
(
    cd "$tb"
    # Nur das Nötigste: Mehrfachprogramm mit cat, ls, wc, echo, sh (toysh liegt in toys/pending und braucht Handbuch-freie Optionen).
    make allnoconfig >/dev/null
    for o in TOYBOX CAT LS WC SH ECHO; do
        sed -i "s/^# CONFIG_$o is not set\$/CONFIG_$o=y/" .config
    done
    make oldconfig </dev/null >/dev/null
    for o in TOYBOX CAT LS WC SH ECHO; do grep -q "^CONFIG_$o=y" .config || { echo "tools/build.sh: CONFIG_$o nicht aktiv" >&2; exit 1; }; done
    CC=$cc LDFLAGS=-static make -j"$(nproc)" >/dev/null
    cp toybox "$work/toybox.multi"
    # Eigenständige Programme je Befehl (scripts/single.sh, ohne Mehrfachprogramm und ohne execve): Satz S1.
    # single.sh liest die Optionen aus .config (nur TOYBOX_* werden übernommen) und baut mit $CC/$LDFLAGS.
    for a in cat ls wc; do
        CC=$cc LDFLAGS=-static sh -c 'exec bash scripts/single.sh "$0"' "$a" >/dev/null
        cp "$a" "$work/single-$a"
    done
)
mkdir -p "$OUT_DIR/tb"
cp "$work/toybox.multi" "$OUT_DIR/tb/toybox"
for a in cat ls wc; do cp "$work/single-$a" "$OUT_DIR/tbs_$a"; done
for a in cat ls wc sh echo; do ln -sf toybox "$OUT_DIR/tb/$a"; done
echo "gebaut: out/tools/tb/toybox (+ cat ls wc sh echo)"

for r in "$SRC_DIR"/rust/*.rs; do
    [ -e "$r" ] || continue
    n=rs_$(basename "$r" .rs)
    # Nicht-PIE und statisch wie die C-Programme; Binärdatei ohne Pfade aus dem Bau (--remap-path-prefix).
    "$rustc" --edition 2021 --target x86_64-unknown-linux-musl -C opt-level=1 -C panic=abort \
        -C relocation-model=static -C target-feature=+crt-static -C debuginfo=0 -C strip=symbols \
        --remap-path-prefix "$SRC_DIR=src" -o "$OUT_DIR/$n" "$r"
    echo "gebaut: out/tools/$n"
done
