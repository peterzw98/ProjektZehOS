# Ladybird `js` für ZehOS (Demo-Binary)

Nur für den Gasttest der Ladybird-Story (`story/ladybird`), nicht für master oder Pakete.

| Angabe | Wert |
| --- | --- |
| Programm | `js` (LibJS-REPL von Ladybird), ohne JIT |
| Ladybird-Commit | `6fba5b78a1043fe93b3451204e04dcc0e3bd983a` |
| Ziel | `x86_64-linux-musl`, statisch gelinkt (keine dynamischen Abhängigkeiten) |
| Größe | 33 092 712 Byte |
| SHA-256 | `bd719b4fd8a383e4d0d386a88ce716157c91bc205fcac7700f39bbaad3d32804` |
| Bau | Alpine 3.24 (musl), Rust 1.98, gcc 15.2, `experiments/Ladybird.Probe/musl/build.sh --target js` (Branch `task/ladybird-n1d1-js`) |
| Geprüft | unter Linux: Rechenprobe `1+1` und Array-Sortierung (10 000) bestanden |

Bauabweichungen gegenüber dem Skript: echtes `ninja` (Paket `ninja-build`) vor samurai im Suchpfad, `gcompat` für das glibc-`gn` von vcpkg (Skia), libedit gegen statisches `libncursesw.a` gelinkt.

Lizenz: BSD-2-Clause, siehe `LICENSE.ladybird`.
