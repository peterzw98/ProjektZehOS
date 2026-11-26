# Ladybird-Proben mit musl

Die Probe für das Kommandozeilenprogramm `js` aus Ladybird `6fba5b78a1043fe93b3451204e04dcc0e3bd983a` gehört zu N1-D1. Das gleiche Skript unterstützt den Browser und seine Dienste für N1-D2. Beide Varianten verwenden das Distribution-Preset ohne JavaScript-JIT. [N1-Plan](../../../docs/superpowers/plans/2026-11-24-ladybird-n1-nachweise.md)

## Voraussetzungen

Alpine Linux x86_64 als Container oder Chroot, ein unprivilegierter Benutzer, ein musl-C++-Compiler, CMake, `ninja-build`, `gcompat`, `ncurses-static`, Python 3, Rust 1.98.0, Git, Binutils und strace. Die Abhängigkeiten des festgeschriebenen Ladybird-Commits baut Upstream-vcpkg. Alle Quellen, Protokolle und Ausgaben liegen unter `~/ladybird-probe/`.

## Aufruf

```bash
experiments/Ladybird.Probe/musl/build.sh --target js --jobs 3 --check
timeout --kill-after=30s 8h experiments/Ladybird.Probe/musl/build.sh --target js --jobs 3
```

`--workdir` wählt einen anderen Unterordner unter `~/ladybird-probe/`. `--resume` setzt einen dort begonnenen Bau desselben Commits fort. Für das Ziel `js` prüft das Skript die ELF-Header, den Rechenausdruck `1+1` und die numerische Sortierung von 10.000 Einträgen. Jeder `js`-Lauf schreibt die Systemaufrufe von `js -c '1+1'` in einen eigenen Ordner unter `strace/` und gibt dessen Pfad aus.

### Vollbau und Headless-Probe (N1-D2)

Für den Vollbau werden zusätzlich Qt ab 6.10 und die GIO- und XCB-Entwicklungsmodule benötigt. DejaVu stellt die Schrift für den Headless-Test bereit.

```bash
apk add --no-cache qt6-qtbase-dev qt6-qtbase-private-dev qt6-qtpositioning-dev \
  glib-dev xcb-util-wm-dev font-dejavu
```

Der Vollbau verwendet das dynamische Qt aus Alpine. Die Ladybird-Bibliotheken bleiben statisch. `WebContent` baut den Dienst, `all` baut `ladybird` mit seinen Diensten und prüft `static.html` über `measure.py` mit `QT_QPA_PLATFORM=offscreen`. Das Prüfergebnis liegt in `logs/headless-static.json`.

```bash
experiments/Ladybird.Probe/musl/build.sh --target all --check
timeout --kill-after=30s 8h experiments/Ladybird.Probe/musl/build.sh --target all --jobs 3
# Alternativ erst WebContent bauen und anschließend den Browser im gleichen Baum ergänzen.
timeout --kill-after=30s 8h experiments/Ladybird.Probe/musl/build.sh --target WebContent --jobs 3
timeout --kill-after=30s 8h experiments/Ladybird.Probe/musl/build.sh --target all --jobs 3 --resume
```

`WebContent` und `all` verwenden standardmäßig `~/ladybird-probe/v5` und sind mit `--resume` im selben Baum nutzbar. Das statische `js` verwendet seinen eigenen Baum `~/ladybird-probe/v4`.

Der Vollbau wendet die Anpassungen unter [`patches/all/`](patches/all/) auf den festgeschriebenen Quellstand an. Die Unix-Nachrichtenheader der Broker werden vollständig nullinitialisiert und anschließend mit denselben Nutzdatenfeldern befüllt. So kompiliert das Layout mit den zusätzlichen Padding-Feldern von musl auch mit `-Werror=missing-field-initializers`. Der Seccomp-Code liest seine Ioctl-Typkonstanten aus dem Linux-UAPI-Header `linux/ioctl.h`.

Für statische Linux-Bibliotheken kennt CMake die gegenseitige Abhängigkeit zwischen `LibGfx` und `libgfx_rust`. CMake wiederholt beide Archive auf der Linkzeile. So kann der GNU-Linker die Rückverweise aus Rust auf C++ auch beim `ImageDecoder` auflösen.

## Prüfstand für `js`

Die statische `js`-Probe wurde in Alpine 3.24.4 auf WSL 2 gebaut, mit GCC 15.2.0, CMake 4.2.3, Rust 1.98.0 (`x86_64-unknown-linux-musl`) und drei parallelen Jobs. Das [veröffentlichte ELF](https://github.com/peterzw98/ProjektZehOS/blob/9421ab54/ladybird/bin/js) ist 33.092.712 Bytes groß und hat weder `PT_INTERP` noch `DT_NEEDED`. `1+1` ergab `2`, der Sortiertest prüfte alle 10.000 Ausgabewerte und ergab `10000`. Die Ladybird- und vcpkg-Quellbäume sind sauber (je 0 Patchzeilen). Der [Bericht](bericht-n1-d1.md) enthält den ABI-Abgleich.

Der korrigierte Skriptstand wurde mit `--check` und `--resume` geprüft. Beim Wiederanlauf konfigurierte CMake neu, Ninja verwendete den fertigen Bau. Ein Frischbau mit diesem Skriptstand steht aus.

Für eine Wiederholung wurden im Alpine-Minirootfs diese Pakete installiert. [Alpine-Linux-Downloads](https://www.alpinelinux.org/downloads/#minirootfs)

```bash
apk add --no-cache bash git curl ca-certificates build-base cmake ninja-build gcompat nasm \
  autoconf automake libtool autoconf-archive zip unzip time python3 pkgconf \
  binutils file util-linux coreutils linux-headers ncurses-dev ncurses-static libdrm-dev strace
```

Rust wird als normaler Benutzer im Alpine-Rootfs eingerichtet. Der Prüfstand verwendete den `rustup-init`-Installer für `x86_64-unknown-linux-musl` und prüfte dessen SHA-256 anhand der Prüfsummendatei vom gleichen Downloadserver. [Rust-Installation](https://rust-lang.github.io/rustup/installation/other.html)

```bash
export CARGO_HOME="$HOME/ladybird-probe/toolchain/cargo"
export RUSTUP_HOME="$HOME/ladybird-probe/toolchain/rustup"
export PATH="$CARGO_HOME/bin:$PATH"
./rustup-init -y --no-modify-path --profile minimal \
  --default-host x86_64-unknown-linux-musl --default-toolchain 1.98.0
rustup run 1.98.0 rustc -vV
```

Der Build läuft im gleichen Benutzerkonto mit eingehängtem `/proc` und verfügbarem `/dev/fd`. Die Protokolle `fetch.log`, `deps.log`, `configure.log` und `build.log` liegen nach dem jeweiligen Schritt in `~/ladybird-probe/v4/logs/`.

Das Skript setzt `VCPKG_FORCE_SYSTEM_BINARIES=1`, damit vcpkg die Alpine-Werkzeuge verwendet. Das zuerst von vcpkg heruntergeladene CMake 4.4.4 brauchte den glibc-Loader. [vcpkg-Umgebungsvariablen](https://learn.microsoft.com/en-us/vcpkg/users/config-environment#vcpkg_force_system_binaries)

- Das Skript setzt `/usr/lib/ninja-build/bin` an den Anfang von `PATH`. Samurai lehnt `ninja install -v` beim Bauen von `pkgconf` ab.
- `gcompat` stellt den Loader für die glibc-Version von `gn` bereit, die Skia beim Bau aufruft.
- `pkgcfg_lib_LIBEDIT_ncurses` zeigt auf `/usr/lib/libncursesw.a`, damit `libedit` statisch gegen ncurses gelinkt wird.

## Lizenzen

Die [Lizenzbestandsliste](lizenzen-js.md) erfasst die Bestandteile des geprüften `js` einschließlich der statisch eingebundenen `libdwarf` unter LGPL 2.1. Sie beschreibt auch, welche Unterlagen für die Weitergabe des ELF benötigt werden.
