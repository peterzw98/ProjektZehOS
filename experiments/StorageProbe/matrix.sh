#!/usr/bin/env bash
# Formatiert je Groesse und Blockgroesse ein frisches sparse Image mit dem Cosmos-Ext2Formatter, prueft es mit
# e2fsck -fn (nur lesend) und dumpe2fs -h und loescht nur das eigene Image wieder. Ausgabe: eine Zeile je Fall.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
work="${1:?Arbeitsordner angeben, z. B. .tools/storage-probe}"
mkdir -p "$work"
dotnet build -c Release "$here" > /dev/null
for size in 4194304 8388608 67108864 34359738368 68719476736 137438953472; do
  for block in 1024 2048 4096; do
    image="$work/cosmos-ext2-$size-$block.img"
    result=$(dotnet run -c Release --no-build --project "$here" -- format "$image" --size "$size" --block-size "$block" || true)
    status=0
    e2fsck -fn "$image" > "$image.e2fsck.txt" 2>&1 || status=$?
    groups=$(dumpe2fs -h "$image" 2>/dev/null | awk -F: '/Block count/{c=$2} /Blocks per group/{p=$2} END{printf "%d", (c+p-1)/p}')
    first=$(grep -m1 -E 'conflicts|differences|Padding|Corrupt' "$image.e2fsck.txt" || true)
    echo "size=$size block=$block groups=$groups format=$result e2fsck=$status first=\"$first\""
    rm -f "$image"
  done
done
