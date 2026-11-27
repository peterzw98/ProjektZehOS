#!/bin/sh
# Satz v18 (Review V18 P3): badrsp.c ergibt zwei Programme mit eigener Erwartung, rspnc (RSP nichtkanonisch, signal 7)
# und rspkern (-DKERNEL_RSP, RSP in der Kernelhälfte, signal 11). Wird von ../../build.sh mit CC, OUT_DIR und SRC_DIR
# aufgerufen, nachdem src/v18/*.c schon gebaut ist; das dort entstandene out/v18/badrsp hat keine Erwartung und
# fiele in run-linux.py als Abweichung auf, deshalb wird es hier entfernt.
set -eu
: "${CC:?}" "${OUT_DIR:?}" "${SRC_DIR:?}"
# Dieselben Schalter wie ../../build.sh, damit die Programme gleich gebaut sind wie der Rest des Satzes.
flags="-static -fno-pie -no-pie -O1 -Wall -Wextra -Werror -std=gnu11"
$CC $flags -o "$OUT_DIR/rspnc" "$SRC_DIR/badrsp.c"
echo "gebaut: out/v18/rspnc"
$CC $flags -DKERNEL_RSP -o "$OUT_DIR/rspkern" "$SRC_DIR/badrsp.c"
echo "gebaut: out/v18/rspkern"
rm -f "$OUT_DIR/badrsp"
