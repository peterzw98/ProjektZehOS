#!/usr/bin/env bash
# Baut aus aot_runtime_fixture.c die beiden .aot-Fixtures fuer Etappe D und schreibt sie als C-Bytefelder nach
# ../aot_runtime_fixture_data.c: "host" (2 Seiten Anfangsspeicher, ohne Obergrenze) und "limit" (gleiche Quelle mit
# 257 MiB Anfangsspeicher, ueber der Kernelgrenze von 256 MiB). Nur unter Linux: wamrc gibt es nicht fuer Windows.
# Aufruf: build-runtime.sh <wasi-sdk-Ordner> <wamrc>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sdk="${1:?wasi-sdk-Ordner angeben}"; wamrc="${2:?wamrc angeben}"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
build() { # build <name> <Anfangsspeicher in Byte>
  "$sdk/bin/clang" --target=wasm32 -O2 -nostdlib -Wl,--no-entry -Wl,--allow-undefined \
    -Wl,--initial-memory="$2" -Wl,-z,stack-size=16384 -o "$work/$1.wasm" "$here/aot_runtime_fixture.c"
  "$here/compile-aot.sh" "$wamrc" "$work/$1.wasm" "$work/$1.aot"
}
build host 131072
build limit 269484032
# Werkzeugstand in den Kopf der erzeugten Datei, damit die Bytefelder nachvollziehbar bleiben.
tools="$("$wamrc" --version | head -n1) (SHA-256 $(sha256sum "$wamrc" | cut -c1-64)); wasi-sdk $(head -n1 "$sdk/VERSION"), $(sed -n 2p "$sdk/VERSION"), clang SHA-256 $(sha256sum "$sdk/bin/clang" | cut -c1-64)"
python3 - "$work/host.aot" "$work/limit.aot" "$here/../aot_runtime_fixture_data.c" "$tools" <<'PY'
import sys
def array(name, data):
    lines = [", ".join(f"0x{b:02x}" for b in data[i:i + 16]) for i in range(0, len(data), 16)]
    return (f"const uint32_t zehos_aot_{name}_fixture_size = {len(data)};\n"
            f"const uint8_t zehos_aot_{name}_fixture[] = {{\n" + ",\n".join("    " + line for line in lines) + "\n};\n")
host = open(sys.argv[1], "rb").read()
limit = open(sys.argv[2], "rb").read()
with open(sys.argv[3], "w", newline="\n") as out:
    out.write("// ZehOS: von fixture/build-runtime.sh erzeugt, nicht von Hand aendern.\n")
    out.write(f"// Werkzeuge: {sys.argv[4]}\n#include <stdint.h>\n\n")
    out.write(array("host", host) + "\n" + array("limit", limit))
PY
