#!/usr/bin/env bash
# Uebersetzt ein .wasm mit wamrc 2.4.5 in ein .aot fuer den ZehOS-Kernel (AOT-Plan Etappe D).
# Aufruf: compile-aot.sh <wamrc> <eingabe.wasm> <ausgabe.aot>
# Die Schalter sind fest und gleich denen von build.sh (Probe B) und dem Sicherheitsdokument 5a: feste Zielmaschine
# ohne Erweiterungen des Baurechners (TCG-tauglich), Speicher- und Stackpruefung im erzeugten Code, grosses Codemodell
# (der Cosmos-Heap liegt weiter als 2 GiB vom Kernel, COSMOS_SUPPORT.md), kein SIMD.
set -euo pipefail
wamrc="${1:?wamrc angeben}"; input="${2:?.wasm angeben}"; output="${3:?.aot angeben}"
version="$("$wamrc" --version | head -n1)"
case "$version" in
  *2.4.5*) ;;
  *) echo "wamrc 2.4.5 erwartet, gefunden: $version" >&2; exit 1 ;;
esac
"$wamrc" --target=x86_64 --cpu=x86-64 --bounds-checks=1 --stack-bounds-checks=1 --size-level=0 --disable-simd \
  -o "$output" "$input"
echo "$(basename "$output") $(stat -c %s "$output") Byte, SHA-256 $(sha256sum "$output" | cut -c1-64), $version"
