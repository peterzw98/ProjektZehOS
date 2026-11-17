# AOT-Laufzeit: Sicherheitsbewertung (ClassiCube-Plan, Etappe 5a)

Stand 17. November 2026, erstellt von agent-scientist auf Zuweisung des Koordinators (Etappe A des [Umsetzungsplans](../plans/2026-11-17-aot-kernel.md), Branch `docs/aot-5c-plan`). Grundlage: Übergabe `2026-11-17-aot-laufzeit-handoff.md` (Branch `docs/aot-handoff`), Hostprobe 5b (`experiments/AOT.Probe/README.md`, Branch `experiments/aot-hostprobe`), Quellen von WAMR 2.4.5 und das GitHub-Repository `bytecodealliance/wasm-micro-runtime` (abgerufen am 17. November 2026). Nichts gebaut oder im Gast gemessen.

**Peter entscheidet die vier Punkte in Abschnitt 5, bevor Kernelcode entsteht.**

## 1 Was sich ändert

Heute ist Wasm die einzige Schutzgrenze zwischen Paket-App und Kernel: Apps laufen im Ring 0 ohne Prozessschutz (ROADMAP 6c, 12.4). wasm3 prüft jeden Speicherzugriff zur Laufzeit. Die vertrauenswürdige Basis (TCB) für eine Paket-App besteht aus wasm3, den Hostfunktionen (`ProjektZeh/Native/Wasm/host/zehos_host_core.c`, `zehos_host_wasm3.c`, `WasmRuntime.cs`) und der Paketprüfung.

Mit AOT kommen hinzu:

| Neu in der TCB | Wo es läuft | Folge eines Fehlers |
| --- | --- | --- |
| `wamrc` 2.4.5 samt LLVM 18.1.8 (Übersetzer) | Rechner des Herausgebers | falsch erzeugte Bereichsprüfung = Schreiben in Kernelspeicher |
| AOT-Lader von WAMR (`core/iwasm/aot`, Relokationen) | Kernel | Fehler beim Parsen des `.aot` = Kernelfehler |
| der erzeugte x86-64-Code | Kernel, Ring 0 | ist selbst die Schutzgrenze |
| Bauschalter beim Herausgeber | Rechner des Herausgebers | vergessene Prüfschalter = Code ohne Prüfung (Abschnitt 3.2) |

Das Vertrauen in ein `.aot` hängt damit allein an Signatur, Herausgeber und Übersetzerversion. Der Kernel kann nicht prüfen, ob das `.aot` aus dem `.wasm` stammt oder überhaupt Prüfungen enthält.

## 2 Bedrohungsmodell

| Angreifer | Heute (wasm3) | Mit AOT nach Empfehlung |
| --- | --- | --- |
| App-Autor liefert ein bösartiges `.wasm` | braucht einen Fehler in wasm3 oder einer Hostfunktion | zusätzlich: ein Fehler in `wamrc`/LLVM, der falsche Prüfungen erzeugt. Sein `.wasm` geht nur durch das `wamrc` des Herausgebers. |
| App-Autor liefert ein eigenes `.aot` | – | ausgeschlossen: Nur der Herausgeber erzeugt `.aot` (Empfehlung 2) |
| Herausgeberschlüssel oder Baurechner kompromittiert | beliebige Wasm-Apps, eingeschränkt durch wasm3 und die Hostfunktionen | **beliebiger nativer Code im Ring 0**, auf jedem Kernel mit AOT an |
| Netz (Spiegel, `pkg source`, MITM) | abgewehrt durch Ed25519-Signatur und SHA-256 je Datei | unverändert: `.aot` ist eine signierte `file`-Zeile |
| Zugriff auf das Datenlaufwerk (offline) | abgewehrt durch das Nachhashen beim ersten Start nach jedem Neustart | unverändert, wenn der Lader genau die gehashten Bytes lädt (Abschnitt 4) |

Die wichtigste Folge steht in Zeile drei: **Mit AOT hat der Paketschlüssel dieselbe Macht wie der Kernelupdate-Schlüssel.** `docs/PACKAGE_FORMAT.md` hält beide Schlüssel bewusst getrennt und sagt, dass ein produktiver Herausgeberschlüssel „noch nicht festgelegt“ ist. AOT hebt diese Trennung für jeden Kernel mit AOT an auf. Ist AOT standardmäßig aus, gilt das nur für Systeme, auf denen jemand AOT eingeschaltet hat. Die Pakete mit `.aot` liegen aber trotzdem signiert auf dem Server, und ein Schlüssel in fremden Händen bleibt für alle Systeme mit AOT an eine Gefahr.

`capability=net` und die Dateirechte aus 6c gelten unter AOT nur, solange der erzeugte Code korrekt ist. Das ist heute bei wasm3 genauso, die TCB ist aber größer.

## 3 Befunde aus den Primärquellen

### 3.1 Lizenz und Hinweispflichten

- WAMR: Apache-2.0 mit LLVM-Ausnahme (`LICENSE`, ab Zeile 204 „LLVM Exceptions to the Apache 2.0 License“; GitHub meldet `Apache-2.0`). Damit verträglich mit dem Kernel; kein Copyleft.
- Pflichten bei Weitergabe (Apache-2.0 §4): Lizenztext beilegen, Änderungen kennzeichnen. WAMR 2.4.5 hat keine `NOTICE`-Datei, also entfällt §4(d). Für den Kernel: `ProjektZeh/ThirdParty/wamr/LICENSE` und `ORIGIN.txt` mit Version, Tarball-SHA-256 und Änderungen, wie im Plan, Etappe D, vorgesehen.
- `ATTRIBUTIONS.md` von WAMR nennt Fremdkomponenten (unter anderem wasmtime, uvwasi, FreeBSD libm, asmjit, zydis). Für einen reinen AOT-Lader werden diese Verzeichnisse nicht gebraucht. In Etappe D je übernommene Datei prüfen und Fremdcode in `ORIGIN.txt` nennen.
- LLVM 18.1.8 (Apache-2.0 mit LLVM-Ausnahme) läuft nur beim Herausgeber. Die LLVM-Ausnahme deckt eventuell eingebettete Laufzeitteile im `.aot` ab. In Paketen und im Kernel gibt es keine LLVM-Pflichten.
- Tarball `WAMR-2.4.5.tar.gz` von GitHub heute erneut geladen: SHA-256 `1ab09d51099f276ca4a1d6629f6b589aab2bd0caa01445e05031a4bed22c199b`, gleich dem Wert der Hostprobe.

### 3.2 Bereichsprüfungen stehen nicht im `.aot`

- `wamrc --help` (2.4.5, `wamr-compiler/main.c`): Auf 64-Bit-Zielen ist `--bounds-checks` **standardmäßig aus**. Der Code verlässt sich dann auf Schutzseiten der Hardware. Laut Hilfe ist ein so erzeugtes Modul „incompatible with a runtime without the hardware bounds checks“.
- Der Kernel baut ohne Schutzseiten (`WAMR_DISABLE_HW_BOUND_CHECK=1`). Das AOT-Format kennt kein Merkmal für Softwareprüfungen: `target_info.feature_flags` enthält nur SIMD, Bulk-Memory, Threads, Referenztypen, GC und ähnliche Merkmale (`core/iwasm/aot/aot_runtime.h`, `aot_emit_aot_file.c`). `aot_loader.c` prüft nichts dazu.
- **Folge:** Fehlt beim Herausgeber `--bounds-checks=1`, lädt der Kernel das `.aot` trotzdem und führt Speicherzugriffe ohne jede Prüfung aus. Die Kennung `<aot-abi>` im Dateinamen (`…-bc`) ist nur eine Behauptung des Herausgebers, keine Prüfung. Die Absicherung muss deshalb im Werkzeug liegen und durch Tests belegt werden (Abschnitt 5, Punkt 3).

### 3.3 Version und Sicherheitsmeldungen

- Neueste Version ist **WAMR 2.4.5** vom 29. Juni 2026 (GitHub Releases). Auf `main` sind seitdem 398 Commits dazugekommen. `AOT_CURRENT_VERSION` ist dort **6**, in 2.4.5 ist es 5 (`core/config.h`). Der nächste Release ändert also die `<aot-abi>`, wie im Plan unter „Risiken“ vorgesehen.
- Veröffentlichte Meldungen (GitHub Security Advisories, abgerufen am 17. November 2026):

| Meldung | Bereich | betrifft den AOT-Lader im Kernel? |
| --- | --- | --- |
| CVE-2026-54914, kritisch, `<=2.4.4`: Heap-Überlauf in WASI `poll_oneoff` | libc-wasi | nein, libc-wasi ist aus. Die Hostprobe nutzte libc-wasi nur auf dem Host. |
| CVE-2026-54913, mittel, `<=2.4.4`: `ref.null`-Init gibt Stackbytes preis | Interpreter-Lader `wasm_loader.c` | nicht im Kernel. `wamrc` nutzt diesen Lader aber für das `.wasm` des App-Autors (siehe unten). |
| CVE-2026-54912, hoch: Fast-Interpreter-Lader, behoben in 2.4.5 | Fast-Interpreter | nein |
| CVE-2025-64704, CVE-2025-64713, CVE-2025-54126, CVE-2025-43853 | SIMD-Interpreter, Fast-Interpreter, Socket-Adresspool, uvwasi | nein (Interpreter, SIMD, Sockets und uvwasi sind aus) |
| CVE-2025-58749, mittel, 2.4.1: `memory.fill` ab 2 GiB hängt | LLVM-Codeerzeugung, die `wamrc` mit dem LLVM-JIT teilt | Fehlerklasse ja, behoben ab 2.4.2, also nicht in 2.4.5 |

- **Noch ohne Release** (auf `main`, nicht in 2.4.5):
  - `afd77c6` (16. November 2026): Bereichsprüfungen von `wamrc` bei konstanten Adressen und Bulk-Memory konnten bei Überlauf der Summe in Hostbreite durchgehen (`compilation/aot_emit_memory.c`). Das ist genau die Fehlerklasse „wamrc erzeugt falsche Prüfung“. Laut Diff betrifft der Fix vor allem die Pfade für Shared Heap und memory64. Eigene Einschätzung, nicht getestet: Für x86-64 mit memory32 ist die Summe zweier 32-Bit-Werte in 64 Bit nicht überlaufbar, unser Ziel ist also wahrscheinlich nicht betroffen.
  - `b46a5ef` (17. November 2026): `module_malloc` übernahm den Rückgabewert der App-eigenen `malloc` ohne Bereichsprüfung als Zeiger (Interpreter und AOT). Relevant nur, wenn Kernel oder Hostfunktionen `wasm_runtime_module_malloc` aufrufen; libc-builtin ist aus. In Etappe D prüfen und vermeiden.
  - Dazu Fixes in libc-wasi (`b844d65`) und im Interpreter-Lader (`b0ad5c5`), beide nicht im Kernel.
- WAMR veröffentlicht Fixes nach einer Sperrfrist (`SECURITY.md`), Patch-Releases gibt es laut `doc/stability_release.md` für Sicherheitsfehler. Meldungen gehen an die Liste `wamr-dev` (groups.google.com/g/wamr-dev). Wer AOT betreut, sollte diese Liste und die Advisories beobachten.
- **`wamrc` liest fremde Eingaben.** Das `.wasm` stammt vom App-Autor. Fehler im Wasm-Lader von WAMR treffen damit den Baurechner des Herausgebers, auf dem auch der Paketschlüssel liegt. `wamrc` sollte deshalb getrennt vom Signieren laufen (Abschnitt 5, Punkt 2).

### 3.4 Alternativen

| Weg | Gewinn (Hostprobe 5b) | Sicherheit | Bewertung |
| --- | --- | --- | --- |
| wasm3 behalten, Hotspots als Hostfunktion (Etappe 3) | nur ClassiCube-Raster | TCB wächst nur um geprüften Kernelcode | läuft ohnehin; ergänzt AOT, ersetzt es nicht |
| WAMR Fast-Interpreter | langsamer als wasm3 (Doom 489 zu 555 Bilder/CPU-s) | wie wasm3 | kein Gewinn, verworfen |
| WAMR Fast-JIT oder LLVM-JIT im Kernel | nicht gemessen | Codeerzeugung im Ring 0 zur Laufzeit; Fast-JIT bringt asmjit (C++) mit, LLVM-JIT ganz LLVM; Fremdcode wird erst im Kernel übersetzt | größere TCB als AOT, verworfen |
| wasmtime/Cranelift | nicht gemessen | Rust, braucht `std`, mmap und Signale | nicht kernelfähig unter Cosmos, verworfen |
| WAMR AOT (dieser Plan) | Doom 2,9x, ClassiCube 3–7x | Übersetzer beim Herausgeber, im Kernel nur der Lader | **Empfehlung**, mit den Bedingungen aus Abschnitt 5 |

## 4 Anforderungen an die Umsetzung (aus diesem Dokument)

1. Der Kernel lädt das `.aot` nur aus dem Puffer, dessen SHA-256 gerade gegen das signierte Manifest geprüft wurde. Er liest die Datei für das Laden nicht noch einmal.
2. Der Lader nimmt nur die eigene `<aot-abi>` an. Architektur und Merkmale prüft WAMR schon selbst: `check_machine_info` vergleicht Ziel und `e_machine`, `check_feature_flags` lehnt SIMD, Bulk-Memory, Threads, Referenztypen und GC ab, wenn der Laufzeitbau sie nicht enthält (`aot_loader.c`). Dafür braucht es keinen eigenen Code. Nicht geprüft wird die CPU-Stufe: Ein `.aot` mit Erweiterungen des Baurechners lädt und stürzt erst unter TCG oder auf älteren CPUs ab. Das sichern nur das feste `--cpu` im Werkzeug und die TCG-Pflichtprüfung (Plan, Etappe B). Hinweis für Etappe B: Aktuelle wasi-sdk-Versionen erzeugen standardmäßig Bulk-Memory und Referenztypen. Der Laufzeitbau muss diese Merkmale deshalb wahrscheinlich enthalten, sonst lehnt der Lader jedes Paket ab.
3. `scripts/build-package.py --aot` setzt die Schalter fest (`--bounds-checks=1 --stack-bounds-checks=1`, festes Ziel und feste CPU) und nimmt keine weiteren `wamrc`-Schalter an. Ein Test belegt, dass ein mit dem Werkzeug erzeugtes `.aot` bei Zugriffen außerhalb des Speichers und bei tiefer Rekursion einen Trap auslöst (Plan, Etappe B und E). Weil `-bc` im Dateinamen nur eine Behauptung ist, läuft vor jeder Veröffentlichung ein Trap-Test mit genau dem `wamrc`, das die zu veröffentlichenden `.aot` erzeugt.
4. Keine Aufrufe von `wasm_runtime_module_malloc` aus Kernel oder Hostfunktionen (siehe `b46a5ef`). Gastzeiger nur über die Adressprüfung von WAMR wandeln.
5. Gastadressen (Etappe D, im Gast belegt): Der von `wamrc` erzeugte Code wandelt bei WASI-Importen, die WAMRs eigene WASI-Bibliothek kennt, Gastadressen selbst in Hostzeiger und prüft nur ein Byte. Die Kernelbindung wandelt jeden `*`-Parameter der Hostfunktionstabelle mit Bereichsprüfung zurück und prüft Adresse und Länge vollständig. Feste Anforderung: `wamrc` und die Tabelle müssen dieselben `*`-Stellen haben; weicht ein Import ab, endet sein Aufruf als Trap (fail-closed), nicht als falscher Zugriff.
6. Ein Wechsel der WAMR-Version (auch wegen Sicherheitsfixes) ist eine eigene Aufgabe mit Rückfrage: neue `<aot-abi>`, neue Pakete, Tests wie in Etappe D.

## 5 Empfehlung zur Entscheidung durch Peter

1. **AOT nur aus Paketen mit gültiger Herausgebersignatur** (heute alle installierbaren Pakete, `ProjektZeh/Packages/package-key.pub`). Zusätzlich: Vor dem ersten veröffentlichten `.aot` wird der produktive Herausgeberschlüssel festgelegt und so geschützt wie der Kernelupdate-Schlüssel, weil er mit AOT dieselbe Macht hat. Das heißt: offline oder auf einem eigenen Rechner signieren, wenige Personen, Verfahren bei Verlust des Schlüssels. Ein eigener AOT-Schlüssel wäre stärker, braucht aber ein Manifestfeld; er ist eine mögliche Folgeaufgabe, keine Voraussetzung.
2. **Das `.aot` entsteht nur beim Herausgeber aus dem `.wasm` desselben Pakets, nie beim App-Autor.** `wamrc` läuft in einer getrennten Umgebung ohne Zugriff auf den privaten Schlüssel (etwa ein Container), weil es fremde `.wasm` liest. Erst danach wird signiert.
3. **`wamrc` 2.4.5 immer mit Speicher- und Stackprüfung, erzwungen im Werkzeug und durch Trap-Tests belegt.** Der Kernel kann diese Prüfung nicht selbst feststellen (Abschnitt 3.2). Er lädt nur seine eigene `<aot-abi>`.
4. **AOT ist zur Laufzeit abschaltbar, wasm3 bleibt Rückfall.** Offen für Peter: AOT standardmäßig an oder aus.
   - Empfehlung dieses Dokuments: **aus**, bis Etappe F abgeschlossen ist und die Gasttests auf QEMU und VMware vorliegen. Danach an. Begründung: Bis dahin gibt es kaum Pakete mit `.aot`, und ein Fehler im Lader oder in der Plattformschicht soll nicht jeden Start treffen.

Restrisiko, das bei jeder Wahl bleibt: Ein noch unbekannter Fehler in `wamrc`/LLVM 18.1.8 oder im AOT-Lader 2.4.5 ist ein Kernel-Exploit für jeden, der ein `.wasm` beim Herausgeber einreichen darf. Den eigentlichen Schutz gegen fremden Code bringt erst ROADMAP 12.4 (Prozessisolation). Bis dahin sollte AOT nur für Apps gelten, deren Autor der Herausgeber vertraut. Heute ist das jede Paket-App, weil nur der Herausgeber Pakete signiert.

## Ledger

- 17.11.2026: Dokument erstellt (agent-scientist). Quellen: WAMR 2.4.5 (Tarball geprüft), GitHub Releases, Security Advisories und Commits auf `main` bis `b46a5ef`. Peter hat am 17.11.2026 um 01:55 in `#first` signiert den Start von 5a und 5c freigegeben („Du kannst mit 5a und C auch durchstarten …“). Der Koordinator hat die Signatur geprüft (id `458180351467061248`), dieser Agent nicht. Peters Entscheidung zu den vier Punkten in Abschnitt 5: siehe Eintrag vom 17.11.2026, ca. 11:00.
- 17.11.2026, ca. 11:00: **Freigabe Etappe D (WAMR-AOT im Kernel) durch Peter** im Terminal der Sitzung agent-peter-cc-lead (Antwort „du hast die freigabe“ auf die Frage nach Etappe D, nativer App-Code im Ring 0). Die vier Punkte aus Abschnitt 5 gelten wie empfohlen; zu Punkt 4: AOT zur Laufzeit standardmäßig **aus**, bis Etappe F abgeschlossen ist und die Gasttests auf QEMU und VMware vorliegen. Etappe D beginnt erst nach Etappe C (Hostfunktionstabelle).
