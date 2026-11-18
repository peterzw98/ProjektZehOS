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
| Doom 1.1.0 | 3x 20 s, Demo-Schleife | 555 Bilder/CPU-s | 489 Bilder/CPU-s | offen (LLVM 18 fehlt) |
| ClassiCube (SoftGPU, Startwelt) | 3x 30 s | 11,5 Bilder/CPU-s | 8,9 Bilder/CPU-s | offen (LLVM 18 fehlt) |
| NetSurf 1.2.0 | – | läuft | nicht lauffähig: Exception-Handling als exnref (Duktape/setjmp), WAMR 2.4.5 kann nur das Legacy-Format | – |

Doom liefert in beiden Laufzeiten gleich viele Bilder (1359 in 20 s, durch die Spielzeit begrenzt); verglichen wird
deshalb die CPU-Zeit. ClassiCube ist CPU-gebunden (≈ 30 s CPU je 30 s); die Läufe streuen stark (wasm3 8,7–11,8),
weil jeder Start eine andere Zufallswelt erzeugt.

**Stand:** Der WAMR-Interpreter ist hier langsamer als wasm3 (Doom −12 %, ClassiCube −23 %); ein Wechsel lohnt
nur mit AOT. `wamrc` (WAMR 2.4.5) braucht LLVM 18; mit dem installierten LLVM 23 baut es nicht (API-Brüche in
`aot_llvm.c`/`aot_llvm_extra*.cpp`). Der AOT-Teil wartet auf die Freigabe des LLVM-18-Downloads.
