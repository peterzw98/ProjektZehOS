#!/usr/bin/env bash
# Baut probe.S zu einem flachen, positionsunabhaengigen Abbild (probe.bin) und prueft es.
# Aufruf: build.sh [Ausgabeordner]   (Standard: ./out neben diesem Skript, nicht eingecheckt)
# Braucht GNU as und ld (binutils); der Kernelbau ruft dieses Skript noch nicht auf (N1-A6).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-$here/out}"
mkdir -p "$out"

as --64 -o "$out/probe.o" "$here/probe.S" 2> "$out/as.log" || { cat "$out/as.log" >&2; exit 1; }
if [ -s "$out/as.log" ]; then
  cat "$out/as.log" >&2
  echo "build.sh: Warnungen von as (etwa ein zu langer Name in der Kopftabelle)" >&2
  exit 1
fi

# Eine Relokation hiesse absolute Adresse: das Abbild muss an jede Seitengrenze passen.
if readelf -r "$out/probe.o" | grep -q 'R_X86_64'; then
  echo "build.sh: probe.o enthaelt Relokationen, das Abbild waere nicht positionsunabhaengig" >&2
  exit 1
fi

ld -m elf_x86_64 -e 0 -Ttext=0 --oformat=binary -o "$out/probe.bin" "$out/probe.o"
ld -m elf_x86_64 -e 0 -Ttext=0 -o "$out/probe.elf" "$out/probe.o"
# Nur den Code (ab dem ersten Slot bis zu den Daten); Kopftabelle und Konstanten waeren sonst als Befehle falsch gelesen.
data="$(nm "$out/probe.elf" | awk '$3 == "probe_data" { print "0x" $1 }')"
objdump -d -M intel --start-address=0x200 --stop-address="$data" "$out/probe.elf" > "$out/probe.dis"
echo "probe.bin: $(stat -c %s "$out/probe.bin") Byte, Disassembly: $out/probe.dis"
