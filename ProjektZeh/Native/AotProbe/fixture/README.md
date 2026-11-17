# AOT-Fixtures (AOT-Plan Etappen B und D)

Die `.aot`-Dateien entstehen nur unter Linux mit `wamrc` 2.4.5 (LLVM 18.1.8) aus der Hostprobe 5b
(`wamrc` SHA-256 `6b400e9cf9cf46fe96b38b1800bd0149f9da903ac6e95ad011a16e5dca1fa774`) und wasi-sdk 25.0. Für
Windows gibt es kein `wamrc`. Die festen Übersetzerschalter stehen in `compile-aot.sh` (gleich `build.sh`).

| Skript | Erzeugt | Inhalt |
| --- | --- | --- |
| `build.sh` | `../aot_fixture_data.c` | Probe B: `ret42`, `call_host`, `fsleep`, `oob` (Speicherverletzung), `recurse` (Wasm-Stack), `recurse_native` (nativer Stack). Etappe D nutzt dieselben Bytes über `zehos_wasm_run_aot`; `_start` fehlt dort, das ist der Fall „fehlender Einstieg“. Ungültige Datei und falsche Formatversion erzeugt die Probe selbst durch Bytemanipulation. |
| `build-runtime.sh` | `../aot_runtime_fixture_data.c` | Etappe D: `host` (WASI und `zehos_v1` über die gemeinsame Hostfunktionstabelle, `proc_exit`, Trap einer Hostfunktion, `memory.grow` über 256 MiB, offene Datei nach Trap/`proc_exit`) und `limit` (gleiche Quelle mit 257 MiB Anfangsspeicher, muss mit `LoadFailed` abgelehnt werden). |
| `compile-aot.sh` | ein `.aot` | beliebiges `.wasm`, etwa Doom |

## Linux-Schritte

```sh
cd ProjektZeh/Native/AotProbe/fixture
./build-runtime.sh <wasi-sdk-25.0> <wamrc>          # schreibt ../aot_runtime_fixture_data.c
# Gegenprobe der Werkzeugkette: build.sh muss aot_fixture_data.c bytegleich reproduzieren.
./build.sh <wasi-sdk-25.0> <wamrc> && git diff --exit-code ../aot_fixture_data.c
# Doom: veröffentlichtes Paket 1.1.0 aus dem Branch packages, unverändert.
git show origin/packages:doom/1.1.0/files/doom.wasm > /tmp/doom.wasm
git show origin/packages:doom/1.1.0/files/freedoom1.wad > /tmp/freedoom1.wad
./compile-aot.sh <wamrc> /tmp/doom.wasm /tmp/doom.aot
```

`aot_runtime_fixture_data.c` wird committet. `doom.aot` gehört nicht ins Repository; bis zum Paketformat aus
Etappe E wird es von Hand auf das Probelaufwerk gelegt.

## Gastprobe

```sh
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:ZehOSAotProbe=true
python scripts/test-wasm-loader.py --iso ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso \
  --probe aot --accel tcg --timeout 600 [--doom-aot /tmp/doom.aot --wad /tmp/freedoom1.wad]
```

Erwartet `AOT_PROBE PASS`. Ohne gebaute Runtime-Fixtures bzw. ohne `--doom-aot` meldet die Probe die Fälle als
`SKIP`. Der Doom-Fall verlangt Status `Ok`, Exitcode 0, mindestens ein Bild und das richtige Prüfbild.
