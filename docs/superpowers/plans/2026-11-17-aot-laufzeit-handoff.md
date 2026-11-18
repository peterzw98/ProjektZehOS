# Übergabe: AOT-Laufzeit für Paket-Apps (ClassiCube-Plan, Etappe 5)

Stand 17. November 2026, erstellt von agent-peter-classicube-port für den Coordinator. Grundlage ist der [ClassiCube-Plan](2026-11-16-classicube.md), Abschnitt „Etappe 5“. Peter hat die AOT-Idee am 17. November 2026 ausdrücklich begrüßt und um Vorbereitung gebeten. Die **Umsetzung** braucht laut Plan trotzdem zuerst die Sicherheitsbewertung und Peters Freigabe (Abschnitt 2). Diese Übergabe ist ein Vorschlag zur Zuteilung, keine Freigabe.

## 1 Ausgangslage und Messwerte

ClassiCube läuft seit Etappe 2 (Branch `feature/classicube-port`, `experiments/ClassiCube.Wasi/`) als WASIp1-Paket unter wasm3. Gemessen unter QEMU 10.2.2 auf dem Windows-Entwicklungsrechner, Fenster 798×519, Renderer SoftGPU. Die Werte liegen in `experiments/ClassiCube.Wasi/README.md`:

| Beschleuniger | Bilder/s | Raster je Bild | Mesh | Rest | Welt erzeugt |
| --- | --- | --- | --- | --- | --- |
| WHPX | 5–6 | 110–180 ms | 15–45 ms | 2–12 ms | 0,3–0,7 s |
| TCG | 1,1–1,2 | 607–655 ms | 0 ms | 192–232 ms | 15,7 s |

Die Rasterisierung belegt 70–90 % der Bildzeit. Peter hat deshalb Etappe 3 (Rasterizer als Hostfunktion) als Nächstes entschieden. Daran arbeitet agent-peter-classicube-port nach dem Merge von Etappe 2.

AOT ist ein **allgemeiner** Hebel. Er beschleunigt den ganzen Gastcode: Rasterizer, Mesh-Bau, Spiellogik, Doom, NetSurf und künftige Apps. Etappe 3 beschleunigt dagegen nur einen Teil von ClassiCube. Beide Wege schließen sich nicht aus. Der Vergleich am Ende von Etappe 5 soll zeigen, ob sich Etappe 3 mit AOT noch lohnt oder Etappe 3 AOT unnötig macht.

## 2 Erste Aufgabe: Sicherheitsbewertung (vor jeder Umsetzung)

Heute ist Wasm in ZehOS die einzige Schutzgrenze zwischen App und Kernel (Ring 0, kein Prozessschutz). wasm3 prüft jeden Speicherzugriff zur Laufzeit. AOT erzeugt nativen x86-64-Code, der im Ring 0 läuft:

- Die Bereichsprüfungen stecken im erzeugten Code. Ein Fehler im Compiler (`wamrc`) oder ein manipuliertes `.aot` ist ein Kernel-Exploit.
- Der Kernel kann ein `.aot` nicht sinnvoll validieren. Das Vertrauen liegt allein bei Paketsignatur, Herausgeber und Compilerversion.
- Bezug: Roadmap 12.4 (Speicher- und Prozessisolation) und 6c (Rechte).

Ergebnis ist ein kurzes Dokument unter `docs/superpowers/specs/`. Es nennt das Bedrohungsmodell, Optionen und eine Empfehlung. Optionen sind etwa:

- AOT nur für signierte Pakete des eigenen Herausgebers;
- `.aot` nur auf dem Paketserver aus dem `.wasm` erzeugen, nie vom App-Autor;
- abschaltbar per Kernel-Einstellung;
- wasm3 als Standard, AOT nur auf Wunsch.

**Peter entscheidet, bevor Code entsteht.** Bis dahin sind nur Recherche und Probe erlaubt, siehe unten.

## 3 Kandidat und Vorarbeiten

- **Kandidat:** WAMR (Apache-2.0, `bytecodealliance/wasm-micro-runtime`) mit `wamrc`, das auf LLVM aufsetzt. Erfahrungen aus der Firefox-Probe, PR [#54](https://github.com/peterzw98/ProjektZehOS/pull/54) (geschlossen, Bericht und Skripte dort): Laden und Validieren gelangen unter WAMR auf dem Entwicklungsrechner. Der NetSurf-Prüfhost hat einen WAMR-Zweig (`experiments/NetSurf.Wasi/host/zehos_host_linux.c`, `README.md` dort).
- **Primärquellen prüfen:** Lizenz, aktuelle Version, Plattformschicht (`core/shared/platform`), Bedarf an `mmap`/ausführbarem Speicher, Schutzseiten oder expliziten Bereichsprüfungen (`--bounds-checks=1`), `setjmp`/`longjmp`, Threads und SIMD. Dazu die Verträglichkeit mit Cosmos über `GCCProject` und NativeAOT (Vorbild: `ProjektZeh/ThirdParty/wasm3`, `Native/Wasm/libc`). Eine Desktopbibliothek ist nicht allein deshalb kernelfähig, weil sie C ist (`AGENTS.md`).
- **Alternativen** kurz bewerten und die Wahl begründen: wasm3 behalten und nur Hotspots als Hostfunktion (das ist Etappe 3), WAMR im Fast-Interpreter- oder Fast-JIT-Modus, wasmtime/Cranelift (Rust, vermutlich nicht kernelfähig).
- **Probe ohne Kernel (erlaubt vor der Freigabe):** ClassiCube, Doom und NetSurf mit `wamrc` übersetzen und auf dem Host gegen wasm3 messen. Grundlage sind die Messpunkte aus `experiments/ClassiCube.Wasi/platform/Measure_ZehOS.c` und der NetSurf-Prüfhost (Linux nötig; auf dem Windows-Entwicklungsrechner gibt es kein WSL). Ergebnis ist der erwartbare Gewinn je App.

## 4 Umsetzung (erst nach Freigabe)

Nach Plan, Etappe 5:

1. Plattformschicht von WAMR für Cosmos: Speicher für ausführbaren Code, Bereichsprüfungen ohne Schutzseiten, `setjmp`/`longjmp`, keine Threads.
2. Die Hostfunktionen aus `ProjektZeh/Native/Wasm/host/zehos_wasm.c` (783 Zeilen) auf die neue Laufzeit bringen: WASI-Teil (`link_wasi`) und `zehos_v1` (Tabelle ab Zeile 711). Ziel ist **eine** Tabelle mit zwei Bindungen statt zweier Kopien. Die C#-Seite (`ProjektZeh/Runtime/Wasm/WasmRuntime.cs`, Rückrufe `zehos_wasm_host_*`) bleibt unverändert. Alle Gastzeiger und Längen werden weiter überlaufsicher geprüft, und Ausnahmen überqueren `UnmanagedCallersOnly` nicht.
3. wasm3 bleibt als Rückfall. Paketformat (`docs/PACKAGE_FORMAT.md`): optionales `.aot` je Laufzeit- und Compilerversion neben dem `.wasm`. Ältere Kernel lehnen `.aot`-Pakete sauber ab oder nutzen das `.wasm`. Dazu `min-kernel` und eine höhere Katalog-Generation.
4. Grenzen wie bisher: 256 MiB Speicher, 1 MiB Wasm-Stack, Traps beenden nur die App. Ressourcen werden beim Beenden freigegeben, auch nach einem Trap.
5. Doku: `docs/PAKETE_ERSTELLEN.md` und `docs/PACKAGE_FORMAT.md` (Vertrag mit App-Entwicklern) sowie `docs/COSMOS_SUPPORT.md` (neue Cosmos-Erkenntnisse).

## 5 Abnahme (aus dem Plan)

- Messbarer Gewinn für ClassiCube, Doom und NetSurf, verglichen mit wasm3 und mit Datum dokumentiert.
- Alle bestehenden Pakete laufen weiter: `scripts/test-wasm-doom-desktop.py --scenario all`, `scripts/test-netsurf-desktop.py`, `scripts/test-classicube-desktop.py`.
- Die Sicherheitsgrenze ist dokumentiert und von Peter freigegeben.
- Hosttests mit `-warnaserror`, nativer Normal- und Recovery-Build, QEMU **und** VMware. Was nicht ausführbar ist, wird ausdrücklich als offen genannt.

## 6 Abhängigkeiten und Abstimmung

- **Cosmos 3.0.97** (`feature/cosmos-3.0.97`, agent-laser-pink-cosmos, PR als 0.5.4): Etappe 5 hat einen Kernelanteil und beginnt erst nach diesem Merge. Recherche, Sicherheitsbewertung und Hostprobe gehen vorher.
- **Etappe 3** (agent-peter-classicube-port) berührt ebenfalls `zehos_wasm.c`: Die Rasterizer-Hostfunktion kommt in die `zehos_v1`-Tabelle. Bei Änderungen an der Bindung früh im Kanal abstimmen. Die Rasterizer-ABI wird so entworfen, dass sie unter beiden Laufzeiten gleich ist.
- **Etappe 1** (Messen und Anzeigeweg) liefert die Messung, an der der AOT-Gewinn gemessen wird. Sie wartet ebenfalls auf Cosmos 3.0.97.
- **Laufende Arbeiten** in `ProjektZeh/Native/Wasm/libc` (agent-laser-orange-libcmem, Rückbau `zehos_mem*`) vor Änderungen dort abgleichen.
- Keine Abhängigkeits- oder Werkzeugupdates ohne Rückfrage. WAMR und LLVM/`wamrc` sind neue Abhängigkeiten; ihre Aufnahme ist Teil der Freigabe aus Abschnitt 2.

## 7 Vorschlag zur Zuteilung

| Teil | Umfang | Voraussetzung |
| --- | --- | --- |
| 5a Sicherheitsbewertung und Kandidatenprüfung (Abschnitte 2–3, Dokument) | 1–2 Tage | keine |
| 5b Hostprobe `wamrc` vs. wasm3 für ClassiCube, Doom und NetSurf | 1–2 Tage, Linux-Umgebung nötig | keine |
| 5c Umsetzung im Kernel | 1,5–3 Monate | 5a von Peter freigegeben, Cosmos 3.0.97 gemergt |

5a und 5b können zwei Agenten parallel bearbeiten. Ergebnisse gehen an Peter (Entscheidung) und an agent-peter-classicube-port (Abgleich mit Etappe 3).
