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

Aufbau wie im Bauordner: `bin/js` sucht seine ICU-Daten unter `<bindir>/../share/Lagom/icu/icudt78l.dat` (oder `$LADYBIRD_ICU_DATA`). Im Gast also den ganzen Ordner `ladybird/` ablegen.

| Datei | Größe | SHA-256 |
| --- | --- | --- |
| `bin/js` | 33 092 712 | `bd719b4fd8a383e4d0d386a88ce716157c91bc205fcac7700f39bbaad3d32804` |
| `share/Lagom/icu/icudt78l.dat` (ICU 78) | 27 687 488 | `3910ea655e8ef353f740d34b4d4fcb1fd4c08f7d5da7ee7ca8099481128096cd` |

Lizenzen: Ladybird BSD-2-Clause (`LICENSE.ladybird`), ICU Unicode-Lizenz (`share/Lagom/icu/LICENSE.icu`).

## Lizenzhinweise und LGPL-Material

**Dieses Programm enthält die Bibliothek libdwarf 2.3.3, statisch gelinkt, lizenziert unter der GNU Lesser General Public License 2.1 (LGPL-2.1-only, einzelne Dateien BSD-2-Clause).** Lizenztext: `licenses/LICENSE.libdwarf`. Die Nutzung, Änderung zum Eigengebrauch und Rückentwicklung zur Fehlersuche solcher Änderungen ist erlaubt.

Vollständige Bestandsliste aller statisch gelinkten Teile (musl, libstdc++/libgcc, ICU, Rust-Crates u. a.) mit Version und Lizenz: `licenses/lizenzen-js.md` (aus PR #739, ermittelt per GNU-Linker-Map eines bitgleichen Links).

Material nach LGPL 2.1 § 6(a):

| Teil | Wo |
| --- | --- |
| Quellcode libdwarf 2.3.3, wie im Bau verwendet | `licenses/source/davea42-libdwarf-code-v2.3.3.tar.gz` (SHA-256 `5917068ba221d22c866331007b0bce8cb0d125513d3f4cdcc3efa0f20152feac`); angewendete Port-Dateien: vcpkg-Port `libdwarf` im von Ladybird gepinnten vcpkg-Stand |
| Quellcode des verlinkenden Programms | Ladybird, öffentlich, Commit `6fba5b78a1043fe93b3451204e04dcc0e3bd983a` (BSD-2-Clause) |
| Bauinformationen und Hilfsprogramme | `experiments/Ladybird.Probe/musl/build.sh` und README auf `task/ladybird-n1d1-js-story` (PR #739) in diesem Repository |

Neu linken mit geänderter libdwarf: Quellarchiv bzw. vcpkg-Port ändern und `build.sh --target js` erneut ausführen; das Skript baut die Abhängigkeiten und linkt `bin/js` statisch neu.
