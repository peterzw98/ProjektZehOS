# AOT-Laufzeit im Kernel (ClassiCube-Plan, Etappe 5c): Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Jede Etappe ist ein eigener Branch und PR; vor jeder Etappe im IRC ankündigen.

**Goal:** Paket-Apps mit einem passenden `.aot` laufen im Kernel als nativer x86-64-Code unter WAMR; alle anderen laufen unverändert unter wasm3.
**Architecture:** WAMR läuft nur als AOT-Lader neben wasm3 (kein WAMR-Interpreter, kein JIT). Beide Laufzeiten teilen sich eine Hostfunktionstabelle mit zwei dünnen Bindungen. Das `.aot` ist eine gewöhnliche, signierte Paketdatei mit festem Namen; ältere Kernel ignorieren sie und nutzen das `.wasm`.
**Tech Stack:** C (WAMR 2.4.5, `wamrc` mit LLVM 18.1.8), C#/.NET 10, Cosmos Gen3 3.0.97, `GCCProject`, wasm3, MSTest, QEMU, VMware.
**Herkunft:** [ClassiCube-Plan](2026-11-16-classicube.md), Etappe 5; Übergabe `2026-11-17-aot-laufzeit-handoff.md` (Branch `docs/aot-handoff`); Hostprobe 5b `experiments/AOT.Probe/README.md` (Branch `experiments/aot-hostprobe`, abd999c). Erstellt von agent-orchestrator am 17. November 2026 auf Zuweisung des Koordinators.

## Stand und Belege

Geplant auf `master` 8d47277 (ZehOS 0.5.8, Cosmos 3.0.97 gemergt). Gelesen, nichts gebaut oder gemessen.

| Aussage | Beleg |
| --- | --- |
| AOT bringt auf dem Host Doom 2,9x, ClassiCube 3–7x gegenüber wasm3 | Hostprobe 5b, mit `--bounds-checks=1 --stack-bounds-checks=1` und `WAMR_DISABLE_HW_BOUND_CHECK=1` |
| NetSurf lädt unter WAMR 2.4.5 nicht (exnref) | Hostprobe 5b |
| Der WAMR-Interpreter ist langsamer als wasm3 | Hostprobe 5b; deshalb bleibt wasm3 der Interpreter |
| Ohne Hardware-Bereichsprüfung braucht der AOT-Pfad kein `setjmp`/`longjmp` | WAMR 2.4.5, `core/iwasm/aot/aot_runtime.c`: `os_setjmp` steht nur unter `OS_ENABLE_HW_BOUND_CHECK` |
| Die Plattformschicht umfasst rund 20 Funktionen (`os_malloc`, `os_mmap`, `os_mprotect`, `os_mutex_*`, `os_time_get_boot_us`, `os_thread_get_stack_boundary`, `os_printf`, Cache-Flush) | WAMR 2.4.5, `core/shared/platform/include/platform_api_vmcore.h`; Vorbilder `platform/riot`, `platform/zephyr` |
| AOT-Dateiformat Version 5 | WAMR 2.4.5, `core/config.h`, `AOT_CURRENT_VERSION` |
| Paket-Apps laufen heute ohne Gasbudget (`gas = 0`) | `ProjektZeh/Apps/Wasm/WasmGameApp.cs`, `WasmRuntime.Enqueue(..., "_start", 0, ...)` |
| Das Manifest nimmt beliebige zusätzliche `file`-Zeilen an; `entry` muss auf `.wasm` enden | `ProjektZeh/Packages/PackageManifest.cs`, `Parse` |

**Nicht belegt, in Etappe B zu klären:** ob Cosmos 3.0.97 Heap-Speicher ausführbar einblendet (NX), ob AOT-Code vom Heap aus Kernelsymbole per 32-Bit-Sprung erreicht (Codemodell), ob WAMR mit `-nostdinc -fno-builtin` und der vorhandenen freistehenden libc baut.

**Offene Voraussetzung:** Das Dokument der Sicherheitsbewertung (5a) liegt auf keinem Remote-Branch. Peter hat am 17. November 2026, 01:55 in `#first` signiert geschrieben: „Du kannst mit 5a und C auch durchstarten“ (vom Koordinator geprüft, hier nicht erneut). Ob „C“ den ganzen Teil 5c meint oder nur Etappe C dieses Plans, ist nicht geklärt. Gedeckt sind damit sicher die Etappen A, B und C; nativer Code im Ring 0 (Etappe D) braucht Peters ausdrückliche Bestätigung, und die vier Punkte aus Etappe A sind noch nicht entschieden. Etappe A hält sie schriftlich fest, bevor Etappe D WAMR in den Kernel bringt.

## Entscheidungen und Begründung

- **WAMR nur als AOT-Lader.** Bauschalter: Interpreter, JIT, libc-wasi, libc-builtin, Thread-Manager, SIMD und Debug aus; `WAMR_DISABLE_HW_BOUND_CHECK=1`. Das hält den Kernelanteil klein und vermeidet Signale, Schutzseiten und `setjmp`.
- **wasm3 bleibt Standard und Rückfall.** Eine App läuft unter AOT nur, wenn ihr Paket ein passendes `.aot` enthält, AOT eingeschaltet ist und das Laden gelingt. Scheitert das Laden vor dem Start, startet dieselbe App unter wasm3. Nach einem Trap gibt es keinen zweiten Versuch.
- **Kein neues Manifestfeld.** Das `.aot` steht als normale `file`-Zeile im Manifest und ist damit von Hash und Signatur gedeckt. Der Kernel sucht neben `entry` die Datei `<entry ohne .wasm>.<aot-abi>.aot`. Die Kennung `<aot-abi>` ist eine Kernelkonstante aus WAMR-Version, AOT-Formatversion, Ziel und Prüfschaltern, etwa `wamr245-aot5-x64-bc`. Ältere Kernel behandeln die Datei als Daten und starten das `.wasm`; ein Kernel mit anderer Kennung ebenso. `min-kernel` ist dafür nicht nötig.
- **Der Kernel erzeugt und prüft kein `.aot`.** Vertrauen liegt bei der Herausgebersignatur und der festen `wamrc`-Version. Nur `scripts/build-package.py` erzeugt das `.aot`, aus dem `.wasm` desselben Pakets und mit festen Schaltern.
- **Feste Zielmaschine.** `wamrc` übersetzt für eine x86-64-Grundausstattung statt für die CPU des Baurechners, sonst läuft das Paket unter TCG oder älteren CPUs nicht. Die genauen Schalter (`--target`, `--cpu`, Codemodell über `--size-level`) werden in Etappe B an der `wamrc`-Hilfe und im Gast belegt.
- **Ein Lauf zur Zeit, ein Thread.** Wie heute: gemeinsamer Worker mit 8 MiB Stack. Mutexe der Plattformschicht sind leer; `os_thread_get_stack_boundary` liefert die Grenze dieses Stacks, damit die Stackprüfung des AOT-Codes als Trap endet und nicht im Triple Fault.
- **Kein Gasbudget unter AOT.** Paket-Apps laufen heute ohne Budget, es geht also nichts verloren. Läufe mit Budget (`CosmosWasmProbe`, `CosmosNetworkProbe`) bleiben bei wasm3. Eine App in einer Endlosschleife ist unter beiden Laufzeiten nicht abbrechbar; das ändert dieser Plan nicht.

## Global Constraints

- `AGENTS.md` vollständig, besonders „Wasm-Apps und Pakete“: jeder Gastzeiger und jede Länge überlaufsicher geprüft, keine Ausnahme über `UnmanagedCallersOnly`, keine appbezogenen Buildflags.
- WAMR und `wamrc`/LLVM sind neue Abhängigkeiten. Version und Prüfsummen wie in der Hostprobe; jede Abweichung braucht Rückfrage.
- Die C#-Rückrufe `zehos_wasm_host_*` in `ProjektZeh/Runtime/Wasm/WasmRuntime.cs` bleiben unverändert. Grenzen bleiben: 256 MiB Speicher, 1 MiB Wasm-Stack, 16 offene Dateien.
- Im Team bauen Engineers nur zum Entwickeln; Tests und Gaststarts fahren die Clerks, höchstens ein nativer Build oder Gast gleichzeitig.
- **Jeder Etappen-PR:** ZehOS-Version erhöhen (Nummer erst beim PR, im IRC abstimmen), unabhängiges Review vor dem PR.
- **Jede Etappe mit Kernelanteil:** Hosttests mit `-warnaserror`, nativer Normal- und Recovery-Build, betroffene Abläufe in QEMU **und** VMware; was nicht ausführbar ist, ausdrücklich als offen nennen.
- Veröffentlichung im Branch `packages` nur per PR; Merge nur auf ausdrücklichen Auftrag.

## Etappe A: Sicherheitsgrenze festhalten (1–2 Tage, kein Kernelcode)

Ziel: Peters Entscheidung liegt schriftlich im Repository. Branch `docs/aot-sicherheit`.

- [ ] `docs/superpowers/specs/2026-11-17-aot-sicherheit.md`: Bedrohungsmodell (nativer Code im Ring 0, `wamrc`-Fehler oder manipuliertes `.aot` als Kernel-Exploit, Bezug Roadmap 12.4 und 6c), Optionen, Empfehlung.
- [ ] Empfehlung dieses Plans zur Bestätigung durch Peter:
  1. AOT nur aus Paketen mit gültiger Herausgebersignatur (heute alle installierbaren Pakete, `ProjektZeh/Packages/package-key.pub`).
  2. Das `.aot` entsteht nur beim Herausgeber aus dem `.wasm`, nie beim App-Autor.
  3. `wamrc` immer mit Speicher- und Stackprüfung; der Kernel lädt nur seine eigene `<aot-abi>`.
  4. Zur Laufzeit abschaltbar; wasm3 bleibt Rückfall. Offen für Peter: AOT standardmäßig an oder aus.
- [ ] Primärquellen prüfen und im Dokument festhalten: WAMR-Lizenz (Apache-2.0 samt LLVM-Ausnahme) und Hinweispflichten, aktuelle Version gegenüber 2.4.5, bekannte Sicherheitsmeldungen zu `wamrc`/AOT-Lader.
- [ ] Signierte Freigabe von Peter mit Datum im Ledger dieses Plans. Sie nennt ausdrücklich Etappe D (WAMR-AOT im Kernel).

**Fertig, wenn:** Dokument gemergt und Peters Entscheidung zu den vier Punkten eingetragen ist.

## Etappe B: Proben im Gast (1–2 Wochen, Abbruchpunkt)

Ziel: die drei unbelegten Annahmen klären, bevor WAMR in den Kernel kommt. Branch `task/aot-probe`. Die Probe hängt an einem eigenen Schalter nach dem Vorbild `-p:ZehOSWasmProbe=true` und gehört nicht in normale Builds.

- [ ] **Ausführbarer Speicher:** Sechs Bytes Maschinencode (`mov eax, 42; ret`) in einen Heap-Block kopieren und aus C aufrufen. Schlägt das fehl, die Cosmos-Seitentabellen und das NX-Bit lesend untersuchen und den kleinsten Weg zu ausführbaren Seiten benennen. Ergebnis in `docs/COSMOS_SUPPORT.md`.
- [ ] **WAMR baut freistehend:** WAMR 2.4.5 mit den Bauschaltern oben und einer Minimal-Plattformschicht über `GCCProject` bauen. Fehlende libc-Funktionen auflisten (`ProjektZeh/Native/Wasm/libc/libc.c` hat heute nur einen Teil von `string.h`/`stdlib.h`) und vor Änderungen dort mit laufenden Arbeiten abgleichen. Größe des Kernelabbilds vorher und nachher.
- [ ] **Kleinstes `.aot` läuft:** Eine Fixture (Rückgabewert, Speicherzugriff außerhalb, tiefe Rekursion, eine Hostfunktion) mit `wamrc` übersetzen, als Bytefeld einbinden und im Gast laden und aufrufen. Erwartet: richtiger Wert, zwei Traps statt Absturz. Dabei Codemodell und Relokationen klären: liegt der Heap weiter als 2 GiB vom Kernelabbild, braucht es das große Codemodell oder WAMRs Sprungtabelle.
- [ ] **Gleitkomma und Zustand:** Fixture mit `f64`-Rechnung über einen Threadwechsel hinweg (`sleep_ms`); belegt, dass der SSE-Zustand erhalten bleibt.
- [ ] QEMU (KVM und TCG) und VMware. TCG ist Pflicht, weil es die CPU-Grundausstattung prüft.

**Fertig, wenn:** Alle vier Proben auf QEMU und VMware grün sind. **Abbruchpunkt:** Braucht ausführbarer Speicher Eingriffe in Cosmos selbst, oder baut WAMR nur mit Patches an seinem Kern, geht die Entscheidung mit Aufwandsschätzung an Peter, und die Etappen C bis F ruhen.

## Etappe C: Eine Hostfunktionstabelle, zwei Bindungen (2–3 Wochen)

Ziel: Die Hostlogik existiert einmal; wasm3 verhält sich danach wie vorher. Branch `feature/wasm-host-core`. Unabhängig von B und parallel möglich.

- [ ] `ProjektZeh/Native/Wasm/host/zehos_wasm.c` (783 Zeilen) teilen: `zehos_host_core.c` mit allen WASI- und `zehos_v1`-Funktionen als gewöhnliche C-Funktionen (Kontext, Speicherbasis, Speichergröße, Argumente; jede Adresse und Länge geprüft), `zehos_host_wasm3.c` nur mit Argumentübernahme und Tabelle. Vorbild und Vergleich: `experiments/NetSurf.Wasi/host/zehos_host_core.c` und `experiments/AOT.Probe/host/zehos_host_wamr.c`.
- [ ] Verhalten erhalten: Reihenfolge der Prüfungen, WASI-Fehlercodes, Stubs (`wasi_badf`, `wasi_nosys`), `proc_exit`, Schließen offener Dateien nach Traps.
- [ ] Namen und Signaturen stehen in **einer** Tabelle, aus der beide Bindungen ihre Einträge ableiten.
- [ ] **Abstimmung mit Etappe 3** (Rasterizer, `feature/classicube-rasterizer`): Sie fügt Einträge in dieselbe Tabelle. Vor Beginn im Kanal klären, wer zuerst mergt; der zweite rebased.
- [ ] Prüfungen: vorhandene Gastproben (`-p:ZehOSWasmProbe=true`), `scripts/test-wasm-doom-desktop.py --scenario all`, `scripts/test-netsurf-desktop.py`, `scripts/test-classicube-desktop.py`.

**Fertig, wenn:** Alle bestehenden Pakete unter wasm3 unverändert laufen, die Gastproben der Fehlerwege (Trap, `proc_exit`, offene Dateien nach Trap, Budget erschöpft) grün sind und `zehos_wasm.c` keine Hostlogik mehr doppelt enthält.

## Etappe D: WAMR-AOT im Kernel (3–5 Wochen)

Ziel: Der Kernel führt ein `.aot` mit allen Hostfunktionen aus. Branch `feature/wasm-aot-runtime`. Braucht A, B und C.

- [ ] `ProjektZeh/ThirdParty/wamr/` mit `ORIGIN.txt` (Quelle, Version 2.4.5, Tarball-SHA-256 aus der Hostprobe, übernommene Verzeichnisse, Änderungen: keine) und Lizenztext; nur die für AOT-Laden nötigen Quellen.
- [ ] `ProjektZeh/Native/Wasm/wamr-platform/`: Plattformschicht auf den Ergebnissen aus B. Speicher über die vorhandene libc, Zeit über den vorhandenen Rückruf, Ausgabe auf die serielle Diagnose, Mutexe leer, Stackgrenze des Workers.
- [ ] `zehos_host_wamr.c`: zweite Bindung der Tabelle aus C. Zeiger nur über WAMRs Adressprüfung in Hostzeiger wandeln; dieselben Kernfunktionen wie wasm3.
- [ ] `zehos_wasm_run_aot(...)` mit denselben Statuscodes wie `zehos_wasm_run`: Laden, Instanz mit Speicherobergrenze 256 MiB, Einstieg `_start`, Trap-Text in die Meldung, `proc_exit` als Exitcode, Freigabe von Instanz, Modul, ausführbarem Speicher und offenen Dateien auf jedem Weg.
- [ ] `WasmRuntime.Execute` nimmt optional die `.aot`-Bytes und wählt den nativen Einstieg; ohne sie bleibt alles beim Alten.
- [ ] Gastprobe erweitern: dieselben Fehlerfall-Fixtures wie für wasm3 als `.aot` (ungültige Datei, falsche Formatversion, Speicherverletzung, Rekursion, fehlender Einstieg, Speicherobergrenze); 20 Starts hintereinander ohne wachsenden Speicher (vorhandene Zeile „WASM Speicher frei=…“).
- [ ] `docs/COSMOS_SUPPORT.md` und `README.md` (Kennzeichnung im Wasm-Abschnitt).

**Fertig, wenn:** Die Gastprobe auf QEMU und VMware grün ist und ein von Hand eingebundenes Doom-`.aot` im Gast bis zum ersten Bild läuft.

## Etappe E: Paketformat, Werkzeug und Auswahl (2–3 Wochen)

Ziel: Ein Paket mit `.aot` installiert sich überall und läuft auf neuen Kerneln nativ. Branch `feature/pkg-aot`. Das Werkzeug kann ab B beginnen, die Kernelseite braucht D.

- [ ] `scripts/build-package.py`: Option `--aot` ruft das festgelegte `wamrc` mit den festen Schaltern auf, legt `<entry>.<aot-abi>.aot` ins Paket, fügt die `file`-Zeile ordinal sortiert ein (das Manifest lehnt Unsortiertes ab) und prüft Version und Prüfsumme des Werkzeugs. Ohne `--aot` unverändert. Tests unter `scripts/tests`.
- [ ] Bauanleitung für `wamrc` und LLVM 18.1.8 (Prüfsummen und Signaturprüfung aus der Hostprobe) in `docs/PAKETE_ERSTELLEN.md`. Die Werkzeuge liegen unter `.tools/aot/`, nicht im Repository. Der Windows-Entwicklungsrechner hat kein WSL: AOT-Pakete entstehen bis auf Weiteres nur unter Linux; das als Grenze nennen.
- [ ] Kernel: `WasmGameApp.Load` sucht die `.aot`-Datei im Paket, beachtet die Größenobergrenze, liest sie einmal in den Speicher und prüft genau diese Bytes gegen den SHA-256 aus dem signierten Manifest. Nur dieser Puffer geht an WAMR; die Datei wird nicht erneut geöffnet. `VerifyOnce` genügt dafür nicht: Es prüft je Paketversion nur einmal je Kernellauf (`PackageStore.VerifyOnce`), eine spätere Änderung der Datei bliebe unbemerkt. Auswahl und Namensregel als plattformunabhängige Funktion mit Hosttests (passende Kennung, fremde Kennung, keine Datei, AOT aus, Hash passt nicht).
- [ ] Rückfall: Ladefehler des `.aot` vor dem Start führt zu wasm3 mit einer Protokollzeile, die Gasttests abfragen können.
- [ ] Schalter zur Laufzeit nach dem Muster von `frametimes on|off` (Branch `feature/wasm-frametimes`), Standard nach Peters Entscheidung aus A. Die laufende Laufzeit einer App ist in der Protokollzeile zum Start sichtbar.
- [ ] `docs/PACKAGE_FORMAT.md` und `docs/PAKETE_ERSTELLEN.md`: Namensregel, `<aot-abi>`, Verhalten älterer Kernel, Grenzen (kein Gasbudget, NetSurf).
- [ ] Gasttest mit Testschlüssel: Paket mit `.aot` auf einem Kernel **vor** dieser Etappe installieren und starten (läuft unter wasm3), auf dem neuen Kernel mit AOT an und aus, mit beschädigtem `.aot` (Installation scheitert am Hash) und mit `.aot` fremder Kennung (wasm3).

**Fertig, wenn:** Alle fünf Fälle des Gasttests auf QEMU grün sind und der Fall „neuer Kernel, AOT an“ auch auf VMware.

## Etappe F: Messen, Pakete, Abschluss (1–2 Wochen)

Branch `docs/aot-messung`; Paketänderungen als eigener PR gegen `packages`.

- [ ] Doom und ClassiCube unter wasm3 und AOT messen, je drei Läufe zu 60 s: QEMU KVM, QEMU TCG, VMware. Werkzeug ist die Messung aus `feature/wasm-frametimes` (`scripts/measure-wasm-frametimes.py`); ist der Branch dann nicht gemergt, die Abhängigkeit im Kanal klären. Werte mit Datum in `docs/WASM_FEASIBILITY.md`.
- [ ] Alle bestehenden Pakete laufen weiter: die drei Desktop-Testskripte aus Etappe C.
- [ ] Paketgröße je App mit und ohne `.aot` nennen: Das `.aot` vergrößert den Download auch für ältere Kernel und zählt gegen die 128 MiB Paketinhalt.
- [ ] Neue Paketversionen von Doom und ClassiCube mit `.aot`, höhere Katalog-Generation, PR gegen `packages`; Merge nur auf ausdrücklichen Auftrag.
- [ ] Vergleich mit Etappe 3 für Peter: lohnt der Rasterizer als Hostfunktion mit AOT noch.
- [ ] `ROADMAP.md` und Ledger des ClassiCube-Plans.

**Fertig, wenn:** Der Gewinn für Doom und ClassiCube auf beiden Zielplattformen dokumentiert ist und die Abweichung zu NetSurf (unten) von Peter angenommen oder als Folgeaufgabe beauftragt ist.

## Abweichung von der Abnahme des ClassiCube-Plans

Der Plan verlangt einen messbaren Gewinn auch für NetSurf. WAMR 2.4.5 lädt NetSurf nicht, weil das Paket Ausnahmen im exnref-Format nutzt (Duktape, `setjmp`). NetSurf bleibt deshalb unter wasm3 und erhält kein `.aot`. Ob ein Neubau von NetSurf mit dem älteren Ausnahmeformat unter WAMR-AOT lädt, ist nicht geprüft und nicht Teil dieses Plans. Peter entscheidet in Etappe F, ob das eine Folgeaufgabe wird.

## Reihenfolge und Zuteilung (Vorschlag an den Koordinator)

| Etappe | Braucht | Parallel zu | Rolle |
| --- | --- | --- | --- |
| A Sicherheitsgrenze | – | B, C | agent-scientist; Entscheidung Peter |
| B Proben im Gast | – | A, C | agent-engineer-senior; Clerk mit QEMU und VMware |
| C Hostfunktionstabelle | Abstimmung mit Etappe 3 | A, B | agent-engineer-jensen oder -huang |
| D WAMR im Kernel | A, B, C | E (Werkzeug) | agent-engineer-senior |
| E Paketformat und Auswahl | D (Kernelseite) | – | agent-engineer-jensen oder -huang |
| F Messen und Pakete | E | – | Clerks; PRs agent-engineer-junior |

Summe 9–15 Wochen bei einem Agenten je Etappe; A, B und C zusammen dauern so lange wie die längste von ihnen. Das passt zur Schätzung 1,5–3 Monate nur, wenn B ohne Eingriff in Cosmos grün wird.

## Risiken

- **Kein ausführbarer Heap unter Cosmos** → Abbruchpunkt in B; Alternative ist ein fester ausführbarer Bereich, der eine Cosmos-Änderung braucht.
- **Stacküberlauf im nativen Code** endet ohne Schutzseite im Triple Fault → Stackgrenze des Workers setzen und in B und D mit tiefer Rekursion prüfen.
- **`wamrc`-Fehler ist ein Kernel-Exploit** → feste Version, nur Herausgeberpakete, abschaltbar; Restrisiko steht im Dokument aus A.
- **Paket läuft auf dem Baurechner, aber nicht im Gast** (CPU-Erweiterungen) → feste Zielmaschine, TCG als Pflichtprüfung.
- **Zwei Etappen ändern dieselbe Tabelle** (C und Etappe 3) → Reihenfolge vor Beginn klären.
- **Kernelabbild wächst** und der Recovery-Build mit → Größe in B messen; bei Bedarf WAMR nur im Normalbuild.
- **WAMR-Update ändert das AOT-Format** → `<aot-abi>` ändert sich, alte `.aot` werden ignoriert, Pakete laufen unter wasm3 weiter, bis neue Versionen veröffentlicht sind.
- **VMware-Prüfungen nicht ausführbar** (Team läuft auf Linux, VMware-Profil auf dem Windows-Rechner) → je Etappe ausdrücklich als offen führen und von einem Benutzeragenten nachholen lassen.

## Ledger

- 17.11.2026: Plan erstellt (agent-orchestrator) auf `master` 8d47277. Grundlage: Übergabe, Hostprobe 5b, gelesener Loader- und Paketcode, WAMR-2.4.5-Quellen. Nichts gebaut oder gemessen; kein unabhängiges Review.
- 17.11.2026: Peters signierte Zeile von 01:55 (`#first`) laut Koordinator eingetragen; Etappe A an agent-scientist, Review des Plans an agent-engineer-huang, B und C warten auf freie Engineers.
- 17.11.2026: Review agent-engineer-huang auf 9cd0e25, kein P1. Eingearbeitet: Umfang von Peters Freigabe (Etappe D braucht ausdrückliche Bestätigung), Hashprüfung der gelesenen `.aot`-Bytes statt `VerifyOnce`, Gastproben der Fehlerwege als Abnahme von Etappe C, sortierte `file`-Zeile, Paketgröße in Etappe F.
