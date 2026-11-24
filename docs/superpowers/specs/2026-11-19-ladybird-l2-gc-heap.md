# Ladybird L2-A: GC-Heap ohne 32-GiB-Region

Stand: 20. November 2026. Paket L2-A des [L2-Plans](../plans/2026-11-19-ladybird-l2-lesestudie.md), Board-Block #451.
Gelesen (nichts gebaut, nichts ausgeführt): Ladybird `6fba5b78a1043fe93b3451204e04dcc0e3bd983a`; ZehOS `origin/master` `bbf64f46`.
Alle Pfade ohne Präfix liegen unter `Libraries/`. Bearbeiter war ein Sonnet-Modell, obwohl der Plan Opus vorsieht: Aufwandszahlen und alles mit HYPOTHESE sind vor L3 gegenzulesen.

## Frage

Lässt sich LibGC mit einer Heap-Region von höchstens 1 GiB in einem 32-Bit-Adressraum (wasm32) betreiben? Bewertet werden drei Varianten:
(1) Region ≤ 1 GiB, Verschiebung (Shift) 3 unverändert; (2) unkomprimierte 32-Bit-Zeiger unter wasm32; (3) Code unverändert unter memory64.
Gesondert: der PrimitiveStorage-Käfig ohne JIT.

## Befunde

| Nr | Befund | Beleg | Stand |
| --- | --- | --- | --- |
| 1 | Region 32 GiB, Offset 32 Bit mal 8; Untergrenze 4 GiB und Obergrenze 2 MiB nie committet | `LibGC/HeapRegion.h:20,28,32` | BELEGT |
| 2 | Reserviert werden 2 × Regionsgröße, um die Basis auf die Regionsgröße auszurichten; Fehlschlag bricht per `MUST` ab; `static_assert(sizeof(FlatPtr) >= 8)` | `LibGC/BlockAllocator.cpp:74-103` (`:76`, `:81`) | BELEGT |
| 3 | Reservieren, Committen und Freigeben sind POSIX-`mmap`/`madvise` (`PROT_NONE`, `MAP_NORESERVE`); Chunks zu 2 MiB, Blöcke zu 16 KiB | `LibCore/System.cpp:163-229`; `LibGC/BlockAllocator.cpp:51-52,187-195,235-278`; `LibGC/HeapBlock.h:274` | BELEGT |
| 4 | Komprimierter Zeiger = Adresse >> 3 als u32; `static_assert(1 << (32+3) == HEAP_REGION_SIZE)`; Dekodieren ohne Maske, weil „jede 32-Bit-Zahl in der Region landet“ | `LibGC/CompressedPointer.h:106-107,114-133` | BELEGT |
| 5 | Dieselbe Gleichheit prüft die Rust-Seite zur Laufzeit; `compress_address` nimmt die Regionsbasis aus der Ausrichtung | `LibJS/Rust/src/layout_forward.rs:116`; `LibJS/Rust/src/layout/member.rs:22-36` | BELEGT |
| 6 | Rust-Konstante spiegelt die C++-Größe von Hand: `HEAP_REGION_SIZE = 32 * GIB`, `primitive_storage_cage_offset_mask = 4 * TIB - 1`; dazu Abgleich mit dem Layout aus C++ | `LibJS/Rust/build.rs:101-102,124-126`; `LibJS/Rust/src/gc/heap.rs:821-822`; C++-Seite `LibGC/CAPI.cpp:109-110` | BELEGT |
| 7 | NaN-Box: 48-Bit-Nutzlast = Offset (`& HEAP_REGION_OFFSET_MASK`) plus Basis; `static_assert(sizeof(void*) == sizeof(u64))` bricht unter wasm32 | `LibGC/NanBoxedValue.h:159,197,211-214` | BELEGT |
| 8 | Rust und Interpreter dekodieren über dieselbe Maske (nicht über feste Zahlen) | `LibJS/Rust/src/runtime/value.rs:55-56,189`; `LibJS/Rust/src/bytecode/feedback.rs:290-291`; `LibJS/Interpreter/interpreter.flap:43,54,337-338` | BELEGT |
| 9 | C++-Objekte dekodieren Felder direkt mit `GC::decompress*` | `LibJS/Runtime/Object.h:187-188`; `LibJS/Runtime/Shape.h:34-37` | BELEGT |
| 10 | Der Stack-Scan erkennt Zellen an beiden 32-Bit-Hälften jedes Worts; die untere Schranke `MIN_COMPRESSED_CELL_POINTER` hängt an der 4-GiB-Untergrenze (4 GiB >> 3 = 2^29, größer als eine 1-GiB-Region: 1 GiB >> 3 = 2^27) | `LibGC/Heap.cpp:550-581`; `LibGC/CompressedPointer.h:110` | BELEGT |
| 11 | Der Scan liest `setjmp`-Register, `__builtin_frame_address(0)` und `StackInfo::top()` des nativen Stacks | `LibGC/Heap.cpp:687-690,906-909,1346-1353` | BELEGT |
| 12 | Unter wasm sind Register und Wasm-Lokale für Code unsichtbar; nur Speicher (Schattenstack) ist scannbar. Zeiger, die nur in Lokalen leben, würden übersehen. Das gilt für alle drei Varianten gleich | folgt aus 11; Wasm-Modell, kein Ladybird-Beleg | HYPOTHESE |
| 13 | Der Käfig reserviert 4 TiB + 32 GiB Guard; `ensure_cage` liefert bei `FlatPtr` < 64 Bit ENOMEM, damit scheitert jede ArrayBuffer-Zuteilung (`TRY(...try_reserve)`) | `LibGC/PrimitiveStorage.h:34,40`; `LibGC/PrimitiveStorage.cpp:73-74`; `LibJS/Runtime/ArrayBuffer.h:80-90` | BELEGT |
| 14 | Der Guard von 32 GiB (= 2^32 × 8) ist durch ein JIT-Argument begründet. Der Interpreter maskiert dagegen die ganze Summe `(cached_offset + index*scale) & MASK` | `LibGC/PrimitiveStorage.h:37-41`; `LibJS/Interpreter/interpreter.flap:328-335` | BELEGT |
| 15 | Der Käfig muss eine Zweierpotenz sein (Maske); große Speicher reservieren ihre volle Kapazität im Käfig | `LibGC/PrimitiveStorage.h:34-36`; `LibGC/PrimitiveStorage.cpp:173-229` | BELEGT |
| 16 | JS-`WebAssembly.Memory` (LibWasm) reserviert bis 2 × 4 GiB + Seite im selben Käfig | `LibWasm/Engine/src/runtime/memory.rs:17,25,121-136` | BELEGT |
| 17 | Speichergrenze je Wasm-App in ZehOS: 256 MiB; Seitenobergrenze 65536 (= 4 GiB, das wasm32-Maximum) | ZehOS `ProjektZeh/Runtime/Wasm/WasmRuntime.cs:29`; `ProjektZeh/Native/Wasm/host/zehos_host_wamr.c:17` | BELEGT |
| 18 | Ob ZehOS-WAMR Linearspeicher beim Anlegen vollständig belegt (nicht sparse) ist, wurde hier nicht gelesen (gehört zu L2-F) | – | HYPOTHESE |

### Zählung der direkten Verwendungsstellen

`git grep -c -E 'GC::decompress|GC::compress|compress_cell|decompress_nonnull|COMPRESSED_POINTER|CompressedPointer|NanBoxedValue|heap_region_base|HEAP_REGION|heap_region_offset|js_heap_region_base|heap_region_start|is_in_heap_region'`, Stand `6fba5b78a104`, Treffer in Zeilen:

| Bibliothek | Sprache | Dateien | Zeilen | davon JIT/Flap/Tests |
| --- | --- | --- | --- | --- |
| LibGC | C++ | 18 | 108 | (Tests/LibGC: 4 Dateien, 15 Zeilen, nicht oben mitgezählt) |
| LibJS | C++ | 4 | 13 | 0 |
| LibJS | Rust und Flap | 39 | 136 | 23 Dateien, 70 Zeilen in `LibJS/JIT` und `LibJS/Flap` (entfallen ohne JIT, `Flap` ist L2-B) |
| LibWasm | Rust | 1 | 1 | – |
| LibWeb | C++ und Rust | 0 | 0 | – |

LibWeb berührt weder Kompression noch NaN-Box noch Regionsbasis direkt; es verwendet `GC::Member`/`GC::Ref` (16 Dateien mit `GC::Member<`/`NonnullMember<`, 39 Zeilen) und bleibt bei jeder Variante quelltextgleich. Ebenso LibJS-C++ über `GC::Member` (1 Datei, 4 Zeilen). Dasselbe Verfahren für `PrimitiveStorage|primitive_storage|PRIMITIVE_STORAGE`: LibJS C++ 4 Dateien/67 Zeilen, LibJS Rust 21/206 (darunter `gc/primitive_storage.rs`, `embedding/array_buffer.rs`, `runtime/array_buffer.rs`), LibWeb Rust 4/22, LibWasm Rust 2/20, LibWasm C++ 1/4. Diese Stellen gehen über die Schnittstelle, nicht über die Käfiggröße. **Schluss (BELEGT):** Die Größenannahmen stehen in wenigen Konstanten und Asserts (siehe Befunde 1, 4, 5, 6, 7), die Verwendungsstellen sind überwiegend parametrisiert (`HEAP_REGION_OFFSET_MASK`).

## Bewertung der Varianten

**Variante 1: Region ≤ 1 GiB, Shift 3.** Die Offsets passen weiterhin in u32 (1 GiB >> 3 = 2^27). Zu ändern: Regionsgröße und beide Guards (die 4-GiB-Untergrenze ist größer als die Region, `HeapRegion.h:28`); die Gleichheiten `CompressedPointer.h:107` und `layout_forward.rs:116` werden zu `<=`; `decompress_nonnull` braucht eine Maske `& (HEAP_REGION_SIZE-1) >> 3`, sonst verlässt eine beliebige 32-Bit-Zahl die Region (Befund 4; die Maske kostet eine Anweisung je Dekodieren). `MIN_COMPRESSED_CELL_POINTER` wird kleiner (Befund 10) und der Scan hat mehr Fehltreffer (nur Leistung). Die Ausrichtung auf die Regionsgröße (`BlockAllocator.cpp:83`) lässt sich unter wasm32 nicht über 2 × Reservierung erzwingen (Befund 3); Alternative: eine feste ausgerichtete Arena im Linearspeicher oder `compress` mit Basisabzug (ein Ladevorgang mehr, `CompressedPointer.h:114-120`, `member.rs:30-36`). Bruchstellen: `NanBoxedValue.h:159`, `BlockAllocator.cpp:76`, `PrimitiveStorage.cpp:73` (Befund 13), Schutzseiten und `madvise` fehlen unter WASI (HYPOTHESE: wasi-libc bietet `mmap` nur eingeschränkt).

**Variante 2: unkomprimierte 32-Bit-Zeiger unter wasm32.** Ein `T*` ist dort schon 32 Bit; Komprimieren spart nichts. Mit Shift 0 und Basis 0 ist `compress` die Identität, die NaN-Box trägt die Adresse. Dann entfallen Ausrichtung und Basisregister, aber: die Gleichheit `Region == 2^(32+Shift)` verlangt eine 4-GiB-Region, die es nicht gibt (also Assert ersatzlos streichen oder auf die tatsächliche Region prüfen); der 64-Bit-Scan `data >> 32` (`Heap.cpp:568`) ist unter 32-Bit-Wörtern undefiniert und muss weg; Feld- und Layoutkonstanten, die Rust und Flap aus C++ übernehmen, ändern ihre Bedeutung (`build.rs:101-126`, `interpreter.flap:337`). Die Dateimenge ist dieselbe wie bei Variante 1, der Quelltext gabelt sich aber stärker (32-/64-Bit-Zweig in `Member.h`-Umfeld und Rust-`member.rs`), also mehr Wartung bei Upstream-Änderungen (HYPOTHESE: Upstream baut die Region ohnehin gerade um, Stand LibJS-Umzug nach Rust). Vorteil: die Region darf beliebig im Linearspeicher liegen.

**Variante 3: unverändert unter memory64.** LibGC-Quelltext bleibt bis auf die Plattformschicht gleich (`LibCore/System.cpp:163-229`, `BlockAllocator.cpp`). Gebraucht werden aber 2 × 32 GiB + 4 TiB + 32 GiB (+ weitere 2 × 4 GiB je JS-Wasm-Speicher, Befund 16) als Adressraum, der nur reserviert, nicht belegt wird. Ein Wasm-Linearspeicher kennt kein `PROT_NONE`: `memory.grow` belegt. Das geht nur, wenn die Laufzeit sparse, lazy committete Speicher kann (ZehOS-Kernel: virtueller Speicher 12.4; Grenze heute 256 MiB, Befund 17) und wasi-sdk 25.0 `wasm64` samt libc++ liefert (nicht geprüft, L2-F). Ohne das scheitert schon `reserve_address_space`. Der Aufwand liegt außerhalb von LibGC.

**PrimitiveStorage-Käfig ohne JIT.** Der 32-GiB-Guard ist ohne JIT verzichtbar (BELEGT, Befund 14): nur `PrimitiveStorage.h:37-41` begründet ihn mit JIT-Zugriffen `maskierter Offset + u32-Index × 8`; der Interpreter maskiert die ganze Summe (`interpreter.flap:334`). Übrig bleibt der Überlauf eines Zugriffs von höchstens 8 Byte am oberen Käfigrand (Kommentar `PrimitiveStorage.cpp:81-83`, eine Seite genügt; im Linearspeicher ersatzweise die letzten 8 Byte ungenutzt lassen). Der Käfig muss eine Zweierpotenz bleiben (Maske, Befund 15) und die Summe aller lebenden ArrayBuffer samt reservierter Kapazität (`ArrayBuffer.h:90`) plus Slab-Verschnitt fassen. Unter dem 256-MiB-Limit (Befund 17) heißt das praktisch 16–64 MiB (HYPOTHESE, Messwert aus L1-C offen); größere JS-Wasm-Speicher (Befund 16) und resizable Buffer mit großer `maxByteLength` schlagen dann mit ENOMEM fehl. Ohne `decommit` (kein `madvise`) wird freier Käfig nicht an das System zurückgegeben. Die Zahl der Verwendungsstellen ist oben genannt; sie ändern sich nicht, nur die Konstanten und `ensure_cage`.

## Was ein Umbau ändern müsste

Zeilenzahlen sind die Gesamtzeilen der Datei im Stand `6fba5b78a104` (Regel 5); die tatsächlich betroffenen Abschnitte sind wesentlich kleiner und stehen unter „Stelle“.

| Datei | Zeilen | Stelle | V1 | V2 | V3 |
| --- | --- | --- | --- | --- | --- |
| `LibGC/HeapRegion.h` | 88 | Größe und Guards `:17-32` | ja | ja | nein |
| `LibGC/CompressedPointer.h` | 54 | Assert `:107`, Dekodieren `:127-133` | ja | ja | nein |
| `LibGC/NanBoxedValue.h` | 111 | `:159`, Maske `:197` | ja | ja | nein |
| `LibGC/BlockAllocator.cpp` | 641 | Region `:70-103`, Chunks `:187-195`, `madvise` `:235-278` | ja | ja | ja |
| `LibGC/PrimitiveStorage.h` | 170 | `:34-41` | ja | ja | nein |
| `LibGC/PrimitiveStorage.cpp` | 599 | `:73-84`, Commit/Decommit | ja | ja | nein |
| `LibGC/Heap.cpp` | 2139 | `:550-581`, Stack-Scan `:687-690,906-909,1336-1353` | ja | ja | nein |
| `LibCore/System.cpp` | 1013 | `:163-229` | ja | ja | ja |
| `LibJS/Rust/build.rs` | 345 | `:101-136` | ja | ja | nein |
| `LibJS/Rust/src/layout_forward.rs` | 116 | `:116` | ja | ja | nein |
| `LibJS/Rust/src/layout/member.rs` | 382 | `:22-36` | ja | ja | nein |
| **Summe Zeilen** | | | **5658** | **5658** | **1654** |

Nicht in der Summe, weil nur bei Bedarf: `LibWasm/Engine/src/runtime/memory.rs` (255 Zeilen, Befund 16, nur wenn JS-WebAssembly gebraucht wird). Quelltextgleich bleiben `Member.h`, `Ptr.h`, `HeapBlock.*`, `CellAllocator.cpp`, `Value.rs`, `feedback.rs`, alle LibWeb-Dateien (rechnen über die Maske, Befund 8). Gleiches gilt für Rust-/wasm32-Portierungsarbeit außerhalb der Region (`usize` 32 Bit in `value.rs`, JIT-Abschaltung, Flap-Ziel): sie gehört zu L2-B und ist nicht mitgezählt.

## Aufwand

| Variante | Regel 5 | Spanne | Begründung |
| --- | --- | --- | --- |
| 1 | 5658 / 1500 = 4 | 4–6 Personwochen | Obergrenze: Stack-Scan-Absicherung und Tests (Befund 12) sowie 32-Bit-Plattformschicht (kein `mmap`) |
| 2 | 5658 / 1500 = 4 | 4–6 Personwochen | gleiche Dateien; dauerhaft zwei Zweige zu pflegen |
| 3 | 1654 / 1500 = 2 | 2 Personwochen im LibGC-Teil | dazu nicht in Personwochen schätzbar: memory64 und Sparse-Speicher in der ZehOS-Laufzeit (L2-F, 12.4) |

Abweichung von Regel 5: Die Regel zählt ganze Dateien; Heap.cpp (2139) und System.cpp (1013) werden nur an wenigen Stellen berührt. Die Spanne 4–6 statt 4 gilt wegen der unbekannten Kosten des Stack-Scans unter wasm.

## Urteil

`umbaubar`

Begründung: Eine Region ≤ 1 GiB mit unverändertem Shift (Variante 1) lässt sich in Konstanten, Asserts und der Plattformschicht abbilden (Befunde 1, 4-8); LibWeb bleibt unberührt. Der JIT-Guard des Käfigs entfällt (Befund 14). Das Urteil gilt für die **Region**. Es ist **nicht** `nur mit memory64`, weil Variante 3 mehr Laufzeit verlangt (Sparse-Speicher) als Variante 1 an Quelltext.

Vorbehalte, die L2-Z mitnehmen muss: (a) Der konservative Stack-Scan unter wasm ist ungeklärt (Befund 12, HYPOTHESE); ohne einen Weg, Zeiger aus Wasm-Lokalen sichtbar zu machen, wäre jede Variante falsch, nicht nur diese Studie. Das ist die Frage mit dem größten Risiko und gehört in ein eigenes Lesepaket oder in L2-D/L2-F. (b) Der Käfig bleibt unter dem 256-MiB-Limit klein (16–64 MiB, HYPOTHESE).

## Nicht geprüft

- Rust-Code außerhalb der gezählten Stellen auf 32-Bit-`usize`-Annahmen (`value.rs`, `vm.rs:717`).
- Ob wasi-libc/wasi-sdk 25.0 `mmap`-Ersatz, `setjmp` ohne Ausnahmebehandlung und `wasm64` liefert; Binaryen-`--spill-pointers` als Ausweg für Befund 12 (nur Idee).
- WAMR-Verhalten bei großem `memory.grow` (L2-F), tatsächlicher Speicherbedarf einer Seite (L1-C).
- Tests/LibGC (4 Dateien, 15 Zeilen mit Regionsbezug) und alle Plattformen außer Linux x86-64 im Upstream.
- Die Umstellung von LibJS auf Rust ist im Fluss; jede Zeilenzahl veraltet mit dem Upstream-Stand.
