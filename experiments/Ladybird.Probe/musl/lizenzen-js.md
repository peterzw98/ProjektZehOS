# Lizenzbestand der statischen `js`-Probe

Diese Liste gilt für das [in N1-D1 geprüfte ELF](bericht-n1-d1.md). Ein separater Linklauf mit dem Ninja-Linkbefehl und einer GNU-Linker-Map erzeugte dasselbe SHA-256. Die Liste erfasst eingebundene Objektdateien laut Map und Header-Abhängigkeiten laut `ninja -t deps`. Für die Versions- und Lizenzzuordnung wurden `vcpkg_installed/x64-linux/share/<port>/{copyright,vcpkg.spdx.json}`, die Alpine-Paketdaten und die `Cargo.toml`-Dateien der verlinkten Crates geprüft. Cargo-Objekte lassen sich anhand ihres Hashes mit den `.d`-Dateien unter `build/cargo/build/x86_64-unknown-linux-musl/release/deps/` abgleichen.

## C und C++

| Bestandteil | Stand | Lizenz |
| --- | --- | --- |
| Ladybird und Lagom (`AK`, `LibCore`, `LibGC`, `LibJS`, `LibMain`, `LibThreading`, `LibUnicode`) | `6fba5b78a1043` | BSD-2-Clause |
| musl (`libc.a`, Startobjekte) | `1.2.6-r3` | MIT gemäß Alpine, zusätzliche Hinweise in `COPYRIGHT` |
| GCC-Laufzeit (`libstdc++`, `libgcc`, `libgcc_eh`) | `15.2.0-r5` | GPL-3.0-or-later mit GCC Runtime Library Exception 3.1 für die entsprechend gekennzeichneten Dateien |
| ncursesw | `6.6_p20260516-r0` | X11 gemäß Alpine |
| ICU (`icui18n`, `icuuc`, `icudata` sowie die separate `icudt78l.dat`) | `78.3#3` | Unicode-3.0 und Drittanbieterhinweise laut `share/icu/copyright` |
| cpptrace | `1.0.4#2` | MIT |
| FastFloat (Header) | `8.3.3` | Apache-2.0 OR BSL-1.0 OR MIT |
| libdwarf | `2.3.3` | LGPL-2.1-only für die entsprechend gekennzeichneten Dateien, zusätzlich BSD-2-Clause für einzelne Dateien |
| libedit | `2024-08-08#1` | BSD-3-Clause |
| LLVM libc (Header) | `e54169423e7e` | Apache-2.0 WITH LLVM-exception |
| mimalloc-secure | `2.2.7#1` | MIT |
| simdutf | `9.3.3` | Apache-2.0 OR MIT |
| zlib | `1.4` | Zlib |
| zmij | `1.2#1` | MIT |
| zstd | `1.5.7` | BSD-3-Clause oder GPLv2 |

Für die GCC-Laufzeit ist die Kennzeichnung der konkreten Quelldateien maßgeblich. Die Laufzeitausnahme gilt auch für statische Links unter ihren Bedingungen. [GNU FAQ zur GCC Runtime Library Exception](https://www.gnu.org/licenses/gcc-exception-3.1-faq.html)

## Rust

Die Linker-Map enthält Objekte aus `liblibjs_rust.a`, `liblibtextcodec_rust.a` und `liblibunicode_rust.a`. Die folgenden Versionen stammen aus dem Ladybird- bzw. dem Rust-1.98.0-Stand (`88d9e12ae178`). Eine Version ist zusammengefasst, wenn sie aus beiden Lockfiles stammt. [Rust-Lizenzbestand](https://github.com/rust-lang/rust/blob/88d9e12ae178fab0fb5cc050a94da85685d449ea/COPYRIGHT) · [Rust-Lockfile](https://github.com/rust-lang/rust/blob/88d9e12ae178fab0fb5cc050a94da85685d449ea/library/Cargo.lock)

| Eingebundener Code | Lizenz laut Quell- oder Crate-Metadaten |
| --- | --- |
| Ladybird-eigene Crates `ak`, `ladybird_allocator`, `libcompress_rust`, `libjs_rust`, `libregex_rust`, `libtextcodec_rust`, `libunicode_rust` sowie das Interpreter-Objekt | BSD-2-Clause |
| Rust `std`, `core`, `alloc`, `std_detect`, `panic_abort` | Apache-2.0 OR MIT, mit den Hinweisen im Rust-`COPYRIGHT` |
| `compiler_builtins` aus Rust 1.98.0 | MIT OR Apache-2.0 WITH LLVM-exception |
| `addr2line` 0.25.1, `core_detect` 1.0.0, `flate2` 1.1.11, `getrandom` 0.4.2, `gimli` 0.32.3, `hashbrown` 0.16.1 und 0.17.1, `num-bigint` 0.5.1, `num-integer` 0.1.46, `object` 0.37.3, `rustc-demangle` 0.1.27 und 0.1.28, `simdutf8` 0.1.5 | MIT OR Apache-2.0 |
| `brotli` 9.1.3 | BSD-3-Clause AND MIT |
| `brotli-decompressor` 6.3.2 | BSD-3-Clause OR MIT |
| `calendrical_calculations` 0.2.4 | Apache-2.0 |
| `encoding_rs` 0.8.44 | (Apache-2.0 OR MIT) AND BSD-3-Clause |
| `foldhash` 0.2.0, `zlib-rs` 0.6.8 | Zlib |
| `iced-x86` 1.22.0, `libm` 0.2.16 | MIT gemäß Crate-Metadaten |
| `icu_calendar`, `icu_locale_core`, `icu_locale_fallback`, `icu_provider` (je 2.4.0), `ixdtf` 0.6.7, `yoke` 0.8.4, `zerotrie` 0.2.5 | Unicode-3.0 |
| `memchr` 2.7.6 und 2.8.0 | Unlicense OR MIT |
| `adler2` 2.0.1 | 0BSD OR MIT OR Apache-2.0 |
| `miniz_oxide` 0.8.9 | MIT OR Zlib OR Apache-2.0 |

## Weitergabe des ELF

Die Map weist u. a. `dwarf_alloc.c.o` aus `libdwarf.a` im ELF nach. § 6 der LGPL 2.1 verlangt einen deutlichen Hinweis auf die Nutzung und Lizenz von `libdwarf` bei jeder Kopie sowie den Lizenztext. Zeigt das Programm bei der Ausführung Copyright-Hinweise, gehört der Hinweis der Bibliothek ebenfalls dazu. Die Weitergabebedingungen müssen Änderungen zum Eigengebrauch und Rückentwicklung zur Fehlersuche solcher Änderungen erlauben.

Bei Weitergabe nach § 6(a) gehören der vollständige entsprechende Bibliotheksquellcode einschließlich verwendeter Änderungen und der Objektcode und/oder vollständige Quellcode des verlinkenden Programms zum Lieferumfang. Die Empfänger müssen mit den mitgelieferten Bauinformationen, Daten und Hilfsprogrammen ein ELF mit geänderter Bibliothek linken können. § 6 nennt eine Ausnahme für bestimmte Systemkomponenten und weitere Wege zur Bereitstellung. Auf dem Binärbranch sind Ladybird und ICU dokumentiert. Für die Weitergabe werden zusätzlich die Lizenztexte und das Relink-Material benötigt. [GNU LGPL 2.1, § 6](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.en.html)
