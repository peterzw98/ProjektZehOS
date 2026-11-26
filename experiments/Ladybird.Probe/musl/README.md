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

Der Preflight lief in Alpine 3.24.4 auf WSL 2 mit GCC 15.2.0, CMake 4.2.3 und Rust 1.98.0 (`x86_64-unknown-linux-musl`). Drei parallele Jobs sind für den Baulauf vorgesehen. Der vollständige Bau und die Ausführung in Alpine stehen aus.

Für eine Wiederholung wurden im Alpine-Minirootfs diese Pakete installiert. [Alpine-Linux-Downloads](https://www.alpinelinux.org/downloads/#minirootfs)

```bash
apk add --no-cache bash git curl ca-certificates build-base cmake ninja nasm \
  autoconf automake libtool autoconf-archive zip unzip time python3 pkgconf \
  binutils file util-linux coreutils linux-headers ncurses-dev ncurses-static libdrm-dev strace
```

Der Build startet als normaler Benutzer im Alpine-Rootfs mit eingehängtem `/proc` und `/dev/fd`, Rust 1.98.0 im `PATH` und dem oben angegebenen Aufruf. Die Protokolle `fetch.log`, `deps.log`, `configure.log` und `build.log` liegen nach dem jeweiligen Schritt in `~/ladybird-probe/v4/logs/`.

Der erste Abhängigkeitslauf stoppte bei vcpkgs heruntergeladenem CMake 4.4.4. Sein ELF-Interpreter war `/lib64/ld-linux-x86-64.so.2`. Mit `VCPKG_FORCE_SYSTEM_BINARIES=1` verwendet vcpkg das CMake und Ninja aus Alpine. Der erneute Lauf mit `--resume` hat den Abhängigkeitsbau gestartet. [vcpkg-Umgebungsvariablen](https://learn.microsoft.com/en-us/vcpkg/users/config-environment#vcpkg_force_system_binaries)

Der fortgesetzte V4-Lauf stoppte beim Installieren von `pkgconf` im vcpkg-Triplet `x64-linux`. `/usr/bin/ninja install -v` endet mit `ninja: unknown target '-v'`. Details liegen im Bauordner unter `build/vcpkg-deps/pkgconf/buildtrees/pkgconf/package-x64-linux-rel-err.log`. Als Nächstes die Reihenfolge der Ninja-Argumente am festgeschriebenen vcpkg-Stand prüfen.
