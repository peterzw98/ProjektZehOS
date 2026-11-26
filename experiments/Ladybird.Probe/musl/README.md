# Ladybird V4: statische LibJS-Probe mit musl

Die Probe baut das Kommandozeilenprogramm `js` aus Ladybird `6fba5b78a1043fe93b3451204e04dcc0e3bd983a`. Sie verwendet das Distribution-Preset mit statischen Bibliotheken und baut ohne GUI und JavaScript-JIT. Die Probe gehört zu [Paket N1-D1](../../../docs/superpowers/plans/2026-11-24-ladybird-n1-nachweise.md).

## Voraussetzungen

Alpine Linux x86_64 als Container oder Chroot, ein unprivilegierter Benutzer, ein musl-C++-Compiler, CMake, Ninja, Python 3, Rust 1.98.0, Git, Binutils und strace. Die Abhängigkeiten des festgeschriebenen Ladybird-Commits baut Upstream-vcpkg. Alle Quellen, Protokolle und Ausgaben liegen unter `~/ladybird-probe/`.

## Aufruf

```bash
experiments/Ladybird.Probe/musl/build.sh --target js --jobs 3 --check
timeout --kill-after=30s 8h experiments/Ladybird.Probe/musl/build.sh --target js --jobs 3
```

`--workdir` wählt einen anderen Unterordner unter `~/ladybird-probe/`. `--resume` setzt einen dort begonnenen Bau desselben Commits fort. Beim Erfolg prüft das Skript die ELF-Header, den Rechenausdruck `1+1` und die numerische Sortierung von 10.000 Einträgen. Es schreibt die Systemaufrufe von `js -c '1+1'` in den Unterordner `strace/`.

## Prüfstand

Der vollständige Bau und die Ausführung in Alpine stehen aus.
