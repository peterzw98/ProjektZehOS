# Ladybird L2-B: LibJS ohne flapc-Interpreter

Lesestudie zu Block #452 der [Etappe L2](../plans/2026-11-19-ladybird-l2-lesestudie.md). Stand Ladybird: `6fba5b78a104` (18.11.2026). Stand ZehOS: `origin/master` Kopf `bbf64f46`. Nur gelesen (`git log`, `git show`, `git grep`, Dateien), nichts gebaut oder ausgeführt. Pfade ohne Präfix liegen unter `Libraries/LibJS/`. Zeilenzahlen gelten für `6fba5b78a104`.

## Frage

Gibt es einen JS-Ausführungsweg ohne flapc-Maschinencode, der nach wasm32 übersetzt? Gesucht sind: (1) ein zweiter, portabler Weg im Stand, (2) was ein Wasm-Weg ändern müsste, (3) was der native Weg von ZehOS braucht.

## Befunde

| Nr | Befund | Beleg | Status |
| --- | --- | --- | --- |
| 1 | Der einzige Bytecode-Interpreter ist `interpreter.flap` (4725 Zeilen, 193 `handler`). `flapc` macht daraus GNU-Assembler, `build.rs` assembliert ihn mit `cc` zu `libjs_runtime_interpreter`. Der Rust-Teil ruft ihn als `js_interpreter(...)`. | `Interpreter/interpreter.flap` (`handler` je Opcode ab `:1278`); `Rust/build.rs:315-344`; `Rust/src/interpreter/run.rs:34-37`; `Rust/src/lib.rs:9-10` | BELEGT |
| 2 | `flapc` kennt nur `Architecture::{X86_64, Aarch64}`; `build.rs` bricht für jede andere Architektur mit `panic!` ab. Das Objektformat ist ELF, außer auf Apple (Mach-O) und Windows (COFF); ein unbekanntes OS fällt auf ELF. | `Flap/src/lib.rs:69-72`; `Rust/build.rs:83-93` | BELEGT |
| 3 | `build.rs` verlangt 64-Bit-little-endian vom Ziel (`assert!`) und von der Baumaschine (`const`-Assert), weil das Layout auf der Baumaschine berechnet wird. Schon `cargo build --target wasm32-*` bricht damit vor dem Interpreter ab. | `Rust/build.rs:71-81` | BELEGT |
| 4 | Kein zweiter Ausführungsweg im Stand: Es gibt keinen Rust-, C++- oder Switch-Interpreter. Die Rust-Runtime kennt nur `js_interpreter`; ohne JIT bleibt allein die „plain“-Variante. | `Rust/src/interpreter/run.rs:34-37`; `Rust/src/jit/disabled.rs:7-11`; `Rust/build.rs:167-200` | BELEGT |
| 5 | Der C++-Interpreter ist aus der Historie weg: AsmInt (Assembler-Interpreter) kam am 06.03.2026 (`200103b0af`); der generische C++-Dispatch wurde am 14.06.2026 entfernt (`5ca52d2a77`: „AsmInt is the only bytecode execution engine“); Flap ersetzte AsmInt am 25.07.2026 (`84b087555e`). Letzter reiner C++-Interpreter: `Bytecode/Interpreter.cpp`, 3400 Zeilen, vor `200103b0af`. | `git log`/`git show` der genannten Commits | BELEGT |
| 6 | Auch die C++-Runtime, auf der ein C++-Interpreter liefe, ist weg: `4014a8a6da` (06.10.2026) „Remove the C++ runtime“, 567 Dateien, 116 664 gelöschte Zeilen. LibWeb baut seither standardmäßig auf der Rust-Runtime (`2c109b15ab`). Die Rust-Runtime ist `Rust/src` mit 224 981 Zeilen. | `git show --shortstat 4014a8a6da`; `git log` | BELEGT |
| 7 | Der Interpreter ist Threaded Code: `dispatch_next` springt über eine 256-Einträge-Tabelle von Codeadressen (`js_interpreter_dispatch_table`, `..._debug_dispatch_table`), die Tabelle liegt in einem Register (x86-64: `r12`). Die VM kopiert die Tabellen und überschreibt Einträge zur Laufzeit (Unterbrechung ohne Prüfung an Rücksprüngen, Debugger, Tiers). Wasm kennt keine Codeadressen und keinen indirekten Sprung. | `Rust/src/interpreter/dispatch_tables.rs:21-25,43-46,48-60`; `Flap/src/target/x86_64.rs:760` | BELEGT (Aufbau); HYPOTHESE (Folge für Wasm) |
| 8 | `flapc` hat zwar ein Profil `WebAssembly`, das ist aber ein Interpreter für Wasm-Module (Ladybirds LibWasm), kein Wasm-Ziel: Es erzeugt weiter x86-64- oder AArch64-Assembler. | `Flap/src/profile.rs:21-29`; `Flap/src/main.rs:13,114`; `Flap/src/target/x86_64.rs:714-726` | BELEGT |
| 9 | Aufbau und Größe von `flapc` (`Flap/src`, ohne Tests): 52 617 Zeilen. Die Architekturteile: x86-64 `target/x86_64.rs` 1342 + `target/x86_64/finalize.rs` 2620; AArch64 `target/aarch64.rs` 1758 + `finalize.rs` 2859 + `wasm.rs` 882; gemeinsam u. a. `target/allocator.rs` 2848 (Registerzuteilung für Maschinenregister), `target/description.rs` 1227. | `wc -l` der genannten Dateien | BELEGT |
| 10 | Der Interpreter ruft für alles Schwere Rust-Slow-Paths (`call_slow_path`, 150 Vorkommen in `interpreter.flap`); diese liegen in `Rust/src/interpreter/slow_paths/*.rs` (8281 Zeilen) und sind portabler Rust-Code. Die Handler selbst enthalten die schnellen Pfade (Ganzzahlen, Inline-Caches, Frames, Sprünge). | `Interpreter/interpreter.flap:299,499,530`; `wc -l Rust/src/interpreter/slow_paths/*.rs` | BELEGT (Zahlen); HYPOTHESE (dass ein Interpreter mit fast nur Slow-Paths korrekt, nur langsamer läuft) |
| 11 | Die Schnittstelle erzeugter Code und Runtime: eine Funktion `js_interpreter(bytecode, entry_point, values, vm)` mit Ein- und Rücksprung, zwei Dispatch-Tabellen, das Layout (`layout.conf`, 536 Zeilen Fixture) und die Slow-Path-Funktionen aus `runtime_functions.rs`. Frames werden inline betreten (`run.rs:373,466,585`). | `Rust/src/interpreter/run.rs:34-37,373,466,585`; `Flap/tests/interpreter-layout.conf`; `Rust/build.rs:178-225` | BELEGT |
| 12 | Pflege: 95 Commits an `interpreter.flap` seit dem 20.08.2026 (Monate: Aug 9, Sep 16, Okt 47, Nov 26 bis 18.11., ab 06.03.: 135 an `Libraries/LibJS/Interpreter`), +2214/−1073 Zeilen im Quartal (rund 70 % der Dateigröße). 126 Commits an `Flap/`, 147 an `Rust/src/interpreter`. Ein Fork der Opcode-Semantik veraltet in Wochen. | `git log --since=2026-08-20 --numstat -- Libraries/LibJS/Interpreter/interpreter.flap` | BELEGT |
| 13 | Der Frontend- und Runtime-Code hat nur kleine Architekturzweige mit Rückfall (`parser/unit.rs:291`, `bytecode/native_disassembler.rs:79`); Architekturabhängig sind der Interpreter, der JIT und `build.rs`. | `Rust/src/parser/unit.rs:57,291`; `Rust/src/bytecode/native_disassembler.rs:20,46,79` | BELEGT |
| 14 | Der Interpreter braucht keinen ausführbaren Speicher zur Laufzeit: Sein Code ist assemblierter, statischer Text in der Bibliothek, die Tabellen sind unveränderliche Daten. Nur der JIT legt `PROT_EXEC`-Mappings an (`MAP_JIT`, Schutzschlüssel). Der JIT ist per Cargo-Feature `jit` (`ENABLE_JS_JIT`) abschaltbar, der Stub steht in `jit/disabled.rs`. | `Rust/src/interpreter/dispatch_tables.rs:21`; `Rust/src/jit/executable_memory.rs:13,140-144,180-183`; `Rust/Cargo.toml:9-13`; `CMakeLists.txt:15-16`; `Rust/src/lib.rs:119-122` | BELEGT |
| 15 | Der Interpreter ist über Größen an die GC-Region gebunden (`HEAP_REGION_SIZE` 32 GiB, Käfig 4 TiB im Layout; `heap_region_offset`/NaN-Boxing in den Handlern). Jeder 32-Bit-Weg hängt deshalb zusätzlich an L2-A. | `Rust/build.rs:101-102,124-127`; `Interpreter/interpreter.flap:40-47` | BELEGT |

## Was ein Umbau ändern müsste

Alle Wege setzen den Umbau von `build.rs:71-81` (Layout für 32-Bit-Ziel) voraus, ohne den es nicht einmal baut. Er ist in den Zahlen enthalten.

**Variante 1: `flapc` um ein wasm32-Ziel erweitern**

| Datei | Zeilen (Bestand) | Geänderte/neue Zeilen (Schätzung) |
| --- | --- | --- |
| `Flap/src/target/wasm32/*` (neu, nach Vorbild x86-64: 1342 + 2620) | 0 | ca. 4000 |
| `Flap/src/target/description.rs`, `Flap/src/lib.rs`, `Flap/src/main.rs` (Architektur, Objektformat) | 1227 + 1295 + 159 | ca. 300 |
| `Interpreter/interpreter.flap` (Zeigerbreite, Dispatch ohne Codeadressen) | 4725 | ca. 500 |
| `Rust/build.rs` (Ziel, Layout, Objekt) | 345 | ca. 80 |
| `Rust/src/interpreter/run.rs`, `dispatch_tables.rs`, `vm.rs` (Ein-/Rücksprung, Tabellen) | 733 + 122 + 2784 | ca. 400 |
| Summe | | ca. 5300 |

**Variante 2: portabler Interpreter in Rust für dieselben Opcodes**

| Datei | Zeilen (Bestand) | Geänderte/neue Zeilen (Schätzung) |
| --- | --- | --- |
| `Rust/src/interpreter/portable.rs` (neu; 193 Handler, Übertragung von `interpreter.flap` 4725) | 0 | ca. 5500 (nur Slow-Paths für alles außer Mov, Sprünge und Ganzzahlen: ca. 2500) |
| `Rust/src/interpreter/run.rs`, `dispatch_tables.rs` (Einstieg wählen) | 733 + 122 | ca. 300 |
| `Rust/build.rs` (flapc-Schritt hinter Feature, Layout) | 345 | ca. 80 |
| Summe | | ca. 5900 (klein: ca. 2900) |

**Variante 3: alten C++-Interpreter wiederbeleben**

| Datei | Zeilen | Geänderte/neue Zeilen |
| --- | --- | --- |
| `Bytecode/Interpreter.cpp` (vor `200103b0af`) und die gelöschte C++-Runtime (`4014a8a6da`) | 3400 + 116 664 | ca. 116 664 (Runtime) |

## Aufwand

Regel 5: 1 Personwoche je angefangene 1500 Zeilen, mindestens 1.

| Variante | Zeilen | Personwochen | Begründung und Wartungslast |
| --- | --- | --- | --- |
| 1: flapc-Ziel wasm32 | ca. 5300 | 4 bis 6 | Regel ergibt 4. Zuschlag bis 6, weil ein Wasm-Backend anders aufgebaut ist als ein weiteres Maschinencode-Backend: Es gibt keine Codeadressen (Threaded Code), also entweder eine Schleife mit `br_table` oder Tail-Calls (Wasm-Tail-Call-Erweiterung, Trägerschaft in der ZehOS-Laufzeit offen, L2-F); die Registerzuteilung (2848 Zeilen) entfällt, Strukturierung des Kontrollflusses kommt hinzu. HYPOTHESE. Wartung: Handler bleiben gemeinsam gepflegt (kein Fork), aber jede Änderung an `flapc` (126 Commits im Quartal) muss das neue Backend mittragen. |
| 2: portabler Rust-Interpreter | ca. 5900 (klein ca. 2900) | 2 bis 5 | Regel ergibt 2 (klein) bis 4 (voll), plus 1 für Fehlersuche in Frames und Inline-Caches. Wartung: Fork der Opcode-Semantik; `interpreter.flap` bekam im Quartal 95 Commits, rund 32 je Monat, 2214 neue und 1073 gelöschte Zeilen. Jede Änderung ist von Hand nachzuziehen oder die Variante fällt hinter den Stand zurück. Entlastung (HYPOTHESE): Die Slow-Paths (8281 Zeilen) bleiben unverändert und gemeinsam. |
| 3: C++-Interpreter wiederbeleben | ca. 116 664 | ca. 78 | Rein rechnerisch. Die C++-Runtime ist seit dem 06.10.2026 gelöscht, LibWeb baut auf der Rust-Runtime; ein Rückbau wäre ein Fork von LibJS. |

Nativer Weg: Der Interpreter selbst braucht keinen ausführbaren Speicher (Befund 14). ZehOS müsste einen statisch gelinkten x86-64-Code-Abschnitt laden (ELF oder ein eigener Bauweg; `flapc` schreibt ELF-Assembler für unbekannte OS, `build.rs:89-93`) und `PROT_EXEC` nur für den JIT bieten, den man abschaltet (`ENABLE_JS_JIT=OFF`). W^X-Umschaltung, `MAP_JIT` und Schutzschlüssel entfallen damit. Die Wasm-Seite (L2-F) und der Prozessteil (L2-C) sind davon getrennt.

## Urteil

`schreibbar`

Begründung: Einen fertigen portablen Ausführungsweg nach wasm32 gibt es im Stand nicht (Befunde 4 bis 6, 8), also nicht `vorhanden`. Er ist in jeder der beiden gangbaren Varianten (1, 2) mit etwa 2 bis 6 Personwochen schreibbar. Variante 3 ist nicht vertretbar. Die Wartung (Befund 12) ist der eigentliche Preis: Variante 1 lässt den Interpreter gemeinsam gepflegt, Variante 2 forkt die Opcode-Semantik. Das Urteil gilt nur für den Interpreter; es hängt für wasm32 an L2-A (32-Bit-Heap) und, für das Tail-Call-Muster, an L2-F.

## Nicht geprüft

- Ob WAMR 2.4.5 im ZehOS-Zuschnitt Tail-Calls (`return_call`) trägt (L2-F).
- Ob die Rust-Runtime (`Rust/src`, 224 981 Zeilen) außer dem Interpreter und `build.rs` weitere 64-Bit-Annahmen enthält (L2-A).
- Der JIT (`Rust/src/jit`, `JIT/Rust`) ist ausgeklammert, weil er abschaltbar ist.
- Laufzeitverhalten und Geschwindigkeit eines portablen Interpreters, `flapc`-Optimierer und die Genauigkeit der Zeilenschätzungen (Schätzungen, nicht gemessen).
- Der Bau selbst: nichts wurde gebaut oder ausgeführt.
