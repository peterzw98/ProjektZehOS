#!/usr/bin/env bash
# Baut aus aot_fixture.c das .aot und schreibt es als C-Bytefeld nach ../aot_fixture_data.c.
# Aufruf: build.sh <wasi-sdk-Ordner> <wamrc>; die festen wamrc-Schalter entsprechen dem Sicherheitsdokument 5a.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sdk="${1:?wasi-sdk-Ordner angeben}"; wamrc="${2:?wamrc angeben}"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
"$sdk/bin/clang" --target=wasm32 -mbulk-memory -O2 -nostdlib -Wl,--no-entry -Wl,--allow-undefined \
  -Wl,--initial-memory=65536 -Wl,--max-memory=65536 -Wl,-z,stack-size=16384 \
  -o "$work/fixture.wasm" "$here/aot_fixture.c"
"$wamrc" --target=x86_64 --cpu=x86-64 --bounds-checks=1 --stack-bounds-checks=1 --size-level=0 --disable-simd \
  -o "$work/fixture.aot" "$work/fixture.wasm" > "$work/wamrc.log"
# Werkzeugstand in den Kopf der erzeugten Datei, damit das Bytefeld nachvollziehbar bleibt.
tools="$("$wamrc" --version | head -n1) (SHA-256 $(sha256sum "$wamrc" | cut -c1-64)); wasi-sdk $(head -n1 "$sdk/VERSION"), $(sed -n 2p "$sdk/VERSION"), clang SHA-256 $(sha256sum "$sdk/bin/clang" | cut -c1-64)"
python3 - "$work/fixture.aot" "$here/../aot_fixture_data.c" "$tools" <<'PY'
import sys
data = open(sys.argv[1], "rb").read()
lines = [", ".join(f"0x{b:02x}" for b in data[i:i + 16]) for i in range(0, len(data), 16)]
with open(sys.argv[2], "w") as out:
    out.write("// ZehOS: von fixture/build.sh erzeugt, nicht von Hand aendern.\n")
    out.write(f"// Werkzeuge: {sys.argv[3]}\n#include <stdint.h>\n\n")
    out.write(f"const uint32_t zehos_aot_fixture_size = {len(data)};\nconst uint8_t zehos_aot_fixture[] = {{\n")
    out.write(",\n".join("    " + line for line in lines) + "\n};\n")
PY
echo "fixture.aot $(stat -c %s "$work/fixture.aot") Byte"
