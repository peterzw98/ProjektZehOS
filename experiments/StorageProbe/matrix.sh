#!/usr/bin/env bash
# Formatiert je Groesse und Blockgroesse ein frisches sparse Image mit dem Cosmos-Ext2Formatter, prueft es mit
# e2fsck -fn (nur lesend) und dumpe2fs -h. Ausgabe: eine Zeile je Fall.
# Zweites Argument: original oder fixed; beide formatieren seit dem Patch ext2-formatter mit dem Cosmos-Formatter.
# Jeder Lauf arbeitet in einem neu angelegten Ordner (mktemp -d) unter dem angegebenen Arbeitsordner; geloescht
# werden nur Images, die dieser Lauf dort selbst erzeugt hat. Scheitert ein Format, bricht das Skript ab und
# loescht nichts.
# Exit 0 gilt nur fuer eine vollstaendig fehlerfreie Matrix, sonst Exit 1.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
base="${1:?Arbeitsordner angeben, z. B. .tools/storage-probe}"
formatter="${2:-original}"
case "$formatter" in original|fixed) ;; *) echo "Formatter muss original oder fixed sein." >&2; exit 2;; esac
mkdir -p "$base"
run="$(mktemp -d "$base/run.XXXXXX")"
echo "Lauf: $run formatter=$formatter"
dotnet build -c Release "$here" > /dev/null
overall=0
for size in 4194304 8388608 67108864 34359738368 68719476736 137438953472; do
  # 1024 lehnt der Cosmos-Formatter ab, bis der Treiber FirstDataBlock beachtet (Patch ext2-formatter, E2-P9).
  for block in 2048 4096; do
    image="$run/cosmos-ext2-$formatter-$size-$block.img"
    if ! result=$(dotnet run -c Release --no-build --project "$here" -- format "$image" --size "$size" --block-size "$block" --formatter "$formatter"); then
      echo "size=$size block=$block Formatfehler: $result" >&2
      exit 1
    fi
    status=0
    e2fsck -fn "$image" > "$image.e2fsck.txt" 2>&1 || status=$?
    if (( status != 0 )); then overall=1; fi
    if ! header=$(dumpe2fs -h "$image" 2>/dev/null); then
      echo "size=$size block=$block Header konnte nicht gelesen werden." >&2
      exit 1
    fi
    if ! groups=$(printf '%s\n' "$header" | awk -F: '/Block count/{c=$2} /Blocks per group/{p=$2} END{if(c<=0 || p<=0) exit 1; printf "%d", (c+p-1)/p}'); then
      echo "size=$size block=$block Header enthaelt keine gueltige Gruppengroesse." >&2
      exit 1
    fi
    first=$(grep -m1 -E 'conflicts|differences|Padding|Corrupt' "$image.e2fsck.txt" || true)
    echo "size=$size block=$block groups=$groups format=$result e2fsck=$status first=\"$first\""
    # Grosse sparse Images belegen mehrere GB; nur das eben selbst angelegte Image im eigenen Laufordner loeschen.
    rm -- "$image"
  done
done
exit "$overall"
