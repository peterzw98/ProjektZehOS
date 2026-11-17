# AOT-Hostprobe (ClassiCube-Plan, Etappe 5b)

Misst Paket-Apps auf dem Linux-Host unter wasm3 (Kernelkonfiguration) und WAMR. Kein Kernelcode; Werkzeuge und
Apps liegen nur unter `.tools/aot/` (nicht eingecheckt).

- `host/zehos_host_wamr.c`: volle `zehos_v1`-Tabelle für WAMR (`iwasm --native-lib`), dieselbe Hostlogik wie der
  wasm3-Prüfhost (`experiments/NetSurf.Wasi/host/zehos_host_core.c`).
- `bench.py`: feste App-Zeit, zählt Bilder und CPU-Zeit; Kennzahl Bilder je CPU-Sekunde.

## Aufbau (16./17. November 2026)

- wasm3: Prüfhost `zehos-wasm3` wie in `experiments/NetSurf.Wasi/README.md` (`-Dd_m3HasPosixHost=0 -Dd_m3GuardedMemory=0`).
- WAMR 2.4.5 (Apache-2.0, Tarball-SHA-256 `1ab09d51…c199b`), `iwasm` Fast-Interpreter mit
  `-DWAMR_DISABLE_HW_BOUND_CHECK=1` (explizite Bereichsprüfungen wie wasm3 im Kernel), `-DWAMR_BUILD_LIBC_WASI=1`
  nur für den Host.
- Apps: Doom 1.1.0 und NetSurf 1.2.0 aus packages.pjoter.de (Dateien gegen Manifest geprüft); ClassiCube aus
  `feature/classicube-port` (`build.py` mit wasi-sdk 25 Linux, SHA-256 wie im Projekt, ClassiCube 2b01bee,
  SoftGPU, mit Messung). Host: x86-64, Linux, nur ein Prozess je Messung.

## Ergebnisse

| App | Lauf | wasm3 | WAMR Fast-Interpreter | WAMR AOT (wamrc) |
| --- | --- | --- | --- | --- |
| Doom 1.1.0 | 3x 20 s, Demo-Schleife | 555 Bilder/CPU-s | 489 Bilder/CPU-s | 1602 Bilder/CPU-s (2,9x) |
| ClassiCube (SoftGPU, Startwelt) | je 30 s | 7,9–11,8 Bilder/s (5 Läufe, Median 10,1) | 7,8–9,3 Bilder/s | 27,6–109 Bilder/s (5 Läufe, Median 73) |
| NetSurf 1.2.0 | – | läuft | lädt nicht: Exception-Handling als exnref (Duktape/setjmp), WAMR 2.4.5 kann nur das Legacy-Format („invalid section id“) | – |

Doom liefert in allen Laufzeiten gleich viele Bilder (1359 in 20 s, durch die Spielzeit begrenzt); verglichen wird
deshalb die CPU-Zeit. Das letzte Bild ist unter wasm3 und AOT bytegleich. ClassiCube ist CPU-gebunden (≈ 30 s CPU je
30 s); jeder Start erzeugt eine andere Zufallswelt, deshalb streuen die Werte stark. Die Messanzeige von
`Measure_ZehOS.c` bestätigt das Verhältnis: AOT 28–42 fps bei 17–27 ms Raster je Bild, wasm3 7,5–12,5 fps bei
72–121 ms Raster. Grob ist ClassiCube mit AOT 3- bis 7-mal so schnell, die Rasterzeit je Bild sinkt etwa auf ein
Viertel. Das Planziel „ClassiCube mindestens 3x Bildrate“ ist damit auf dem Host erreicht.

`wamrc` lief mit `--bounds-checks=1 --stack-bounds-checks=1`, `iwasm` nur mit AOT und
`WAMR_DISABLE_HW_BOUND_CHECK=1` (explizite Speicher- und Stackprüfungen wie in der Sicherheitsbewertung 5a verlangt).

## Werkzeuge für AOT

- LLVM 18.1.8 aus den Quellen (`llvm-project-18.1.8.src.tar.xz`, SHA-256 `0b58557a…db2f2a`, Signatur von Tom
  Stellard geprüft), nur X86, ohne Clang/Tests, mit `-DCMAKE_CXX_FLAGS="-include cstdint"`: GCC 16 bindet
  `<cstdint>` nicht mehr implizit ein. Freigabe des Downloads durch laser am 17. November 2026.
- Das vorgebaute LLVM-18.1.8-Paket (Ubuntu 18.04) passte nicht: `wamrc` stürzte in `X86TargetMachine` ab, die
  beiliegenden Werkzeuge brauchen `libtinfo.so.5`, und dessen Clang 18 kann die libstdc++-Header von GCC 16 nicht lesen.
- Mit dem installierten LLVM 23 baut WAMR 2.4.5 nicht (API-Brüche in `aot_llvm.c`, `aot_llvm_extra*.cpp`).
- `wamr-compiler/CMakeLists.txt` lokal: OpenMP-Plugins, `LTO` und `Remarks` aus der Linkliste genommen
  (nur für das vorgebaute Paket nötig gewesen; mit dem selbst gebauten LLVM unerheblich).

## Grenzen

Nur Linux-Host, nicht im Kernel: Ein Kernel-AOT braucht die Plattformschicht für Cosmos (5c) und ist erst nach
Peters Freigabe dran. NetSurf läuft unter WAMR nicht, solange WAMR kein exnref kann. Keine Messung unter QEMU oder
VMware; SIMD und Threads waren aus.
