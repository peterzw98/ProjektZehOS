# WebAssembly-Loader: Machbarkeitsnachweis (Aufgabe 1 / Gate A)

Stand: 14. November 2026. Grundlage: [Loader-Plan](superpowers/plans/2026-11-14-loader-packages.md) Aufgabe 1 und [Entwurf](superpowers/specs/2026-11-14-loader-packages-design.md).

## Ergebnis

**Gate A ist in QEMU erfüllt:** Ein außerhalb des Kernels gebautes C#-Programm (.NET 10, 25 MB Wasm) wird von der Datenplatte gelesen und auf einem unveränderten Probekernel zehnmal hintereinander erfolgreich ausgeführt. Nach Änderung und Neubau nur der App läuft die neue Fassung auf demselben ISO (gleicher SHA-256). VMware und echte Hardware sind **nicht** geprüft.

## Werkzeuge und Quellen

| Baustein | Stand | Lizenz |
| --- | --- | --- |
| wasm3 | Commit `28ecb9af6d2040e474a70f7cb7f43666740141fb`, Kern unverändert unter `ProjektZeh/ThirdParty/wasm3/` | MIT |
| C-Compiler im Kernel | Cosmos-clang 22.1.3 (`cosmos.build.cc` 3.0.94) | – |
| wasi-sdk | 25.0 (`wasi-sdk-25.0-x86_64-windows.tar.gz`, SHA-256 `ebd4868f95a6e764bf29283e3bdf54df0df3902a29737176da9c17da218b939b`), lokal unter `.tools/` | Apache-2.0 mit LLVM-Ausnahme |
| .NET | SDK 10.0.403, Workload `wasi-experimental` 10.0.115 | MIT |
| Python | 3.13 (Testhelfer) | PSF |

Bestehende Versionen von SDK, Cosmos und Paketen wurden nicht verändert. Die Workload-Installation hat ein SDK-11-MAUI-Manifest entfernt; SDK 10 ist davon unberührt.

## C# als WebAssembly

.NET 10 erzeugt für `wasi-wasm` ausschließlich **WASIp2-Komponenten** (`wasm32-unknown-wasip2`, Komponentenlinker). wasm3 führt nur Kernmodule aus. [experiments/Wasm.CSharpProbe](../experiments/Wasm.CSharpProbe/) linkt deshalb dieselben Mono-Bibliotheken mit der WASIp1-libc und `wasm-ld`:

- Compileziel und Linkantwortdatei werden in eigenen MSBuild-Targets auf `wasm32-wasip1` umgestellt; die SDK-Dateien bleiben unverändert.
- Socketfunktionen, die WASIp1 nicht kennt, liefert `zehos_p1_sockets.c` als kontrolliert scheiternde Stubs.
- Ergebnis: Kernmodul, 25 MB, SIMD aus. Importe: WASI-Preview-1 sowie einige `wasi:http`/`wasi:io`-Funktionen aus `System.Net.Http`; diese bleiben unverknüpft und würden nur bei HTTP-Nutzung als Trap enden.
- Geprüft: Liste, virtuelle Methode, Exception, Allokation/GC, Konsolenausgabe, Exitcode.

Bauen (wasi-sdk 25.0 unter `.tools/wasi-sdk-25.0-x86_64-windows` oder `WASI_SDK_PATH`):

```powershell
dotnet build experiments/Wasm.CSharpProbe/Wasm.CSharpProbe.csproj -c Release
```

## Kernelanbindung

- `ProjektZeh/Native/Wasm/libc/`: minimale freistehende C-Laufzeit (Heap über `__cosmos_heap_alloc`, Zeichenketten, `snprintf`, `fabs`/`copysign`/`rint`, gesperrte Datei-Stubs). `mem*` sind exakte eigene Implementierungen (siehe Cosmos-Befund).
- `ProjektZeh/Native/Wasm/host/zehos_wasm.c`: `zehos_wasm_run` mit Gas-, Speicher- und Stacklimit, WASI-Teilmenge (stdout/stderr, `argv[0]`, Uhren, Zufall über RDRAND/RDSEED, `proc_exit`); Dateisystem und Sockets antworten mit `EBADF`/`ENOSYS`. Nur primitive Typen über die C/C#-Grenze.
- `ProjektZeh/Runtime/CosmosWasmProbe.cs`: Probe, nur mit `-p:ZehOSWasmProbe=true`. Alle Läufe auf **einem** Worker-Thread mit 8 MiB Stack; wasm3 erhält `d_m3MaxNativeStack` = 7 MiB.
- Der normale Kernel enthält weder wasm3 noch die Probe.

## Gastprüfung

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -t:Rebuild -p:ZehOSWasmProbe=true
python scripts/test-wasm-loader.py --iso <ISO> --app experiments/Wasm.CSharpProbe/bin/Release/net10.0/wasi-wasm/AppBundle/Wasm.CSharpProbe.wasm
```

Der Helfer stellt die App über QEMU-vvfat als `/data/csprobe.wasm` bereit (Schreibzugriffe verworfen); vorhandene Datenimages werden nicht verwendet. `--expect-fail` prüft ein ISO ohne Probe negativ.

Ergebnisse am 14. November 2026 (QEMU, TCG, 512 MiB):

| Prüfung | Ergebnis |
| --- | --- |
| `add` | 42 |
| Endlosschleife `loop { br 0 }` | Gas-Abbruch nach 43–95 ms (Grenze 250 ms) |
| `memory.grow` über Limit | -1, kein Absturz |
| unbegrenzte Rekursion | Trap `stack overflow` |
| abgeschnittenes Modul / fehlender Einstieg | Parsefehler / `function lookup failed` |
| C#-App, 10 Läufe | Exit 0, `CSPROBE 26`, 3,4–4,4 s je Lauf |
| Freier Speicher | 420 MiB vor und nach allen 10 Läufen (kein Wachstum) |
| geänderte App, gleiches ISO | neue Ausgabe, ISO-Hash unverändert |
| normales ISO (Negativprobe) | kein `WASM_PROBE PASS` |

Normaler Release- und Recovery-Build: 0 Warnungen, 0 Fehler. Hosttests: 322/323; `TlsChecksChainNameAndDates` schlägt bereits auf dem unveränderten Ausgangsstand fehl.

## Doom (Aufgabe 2 / Gate B) – Leistung

[experiments/Wasm.DoomProbe](../experiments/Wasm.DoomProbe/) baut doomgeneric (Commit `e257a09`) mit ZehOS-Plattformschicht als 720-KB-WASIp1-Modul. Die C-Brücke stellt dafür einen nur lesenden WASI-Dateizugriff (ein freigegebener Ordner, keine absoluten Pfade, kein `..`, Schreib-/Anlegeabsichten werden abgelehnt) und die `zehos_v1`-Imports bereit. Die Probe startet Doom 60 s ohne Fenster, wenn `/data/doom.wasm` vorhanden ist (`test-wasm-loader.py --doom … --wad …`).

Der erste Stand zeichnete unter QEMU TCG nur rund 10 Bilder/s und war vollständig rechenbegrenzt (88 ms Wartezeit in 20 s). Die Rechenzeit pro Bild auf dem Host (wasm3 nativ, Titel-/Demoablauf) zeigte die Ursache:

| Variante | ms pro Bild (Host) | QEMU TCG, 20 s |
| --- | --- | --- |
| 32-Bit-Ausgabe (Palettenumrechnung im Wasm-Code) | 6,0 | 9,9–10,5 Bilder/s |
| 8-Bit-Ausgabe, Umrechnung nativ im Kernel (`present_indexed`) | 3,2 | 28,5 Bilder/s |
| zusätzlich niedrige Detailstufe | 2,3 | 32,8 Bilder/s (durch Spieltakt begrenzt) |

`-O3`, Warten per `Thread.Yield` und das Umleiten von `memcpy` änderten nichts (wasm3 nutzt auf x86 direkte Speicherzugriffe und garantierte Endaufrufe). Übernommen wurde nur die 8-Bit-Ausgabe; sie ändert das Bild nicht.

**Abschlussmessung** (QEMU TCG, 512 MiB, CPU `max`, 60 s Titel und Demos, Freedoom 0.13.0): **23,7 Bilder/s**, **34,3 Spieltakte/s**, Prüfbild `ff0000/00ff00/0000ff/ffffff` korrekt, regulärer Exit nach Beendigungsanforderung. QEMU mit WHPX scheitert beim Cosmos-Start (`WHPX: Unexpected VP exit code 4`, auch mit `kernel-irqchip=off`).

Die Probe startet Doom danach viermal je 5 s neu; der freie Speicher bleibt bei 443 MiB.

## Doom im Desktop (Gate B)

Mit `-p:ZehOSWasm=true` (ohne Probe) erscheint Doom im Programmmenü, sobald das Paket installiert ist ([PACKAGE_FORMAT.md](PACKAGE_FORMAT.md)). Die folgenden Gate-B-Messungen stammen noch aus dem Zwischenstand mit festem Ordner `/data/doom`. `WasmGameApp` lädt den Einstiegspunkt des Pakets, gibt dessen `files/`-Ordner lesend frei und läuft auf dem gemeinsamen Wasm-Worker; Bilder (Doppelpuffer) und Tasten (begrenzte Warteschlange, 64 Einträge) laufen gesperrt zwischen Worker und Desktop. Das Bild wird ganzzahlig skaliert (2× im Standardfenster).

- **Pause:** Ist das Fenster nicht aktiv oder wurde es über 250 ms nicht gezeichnet (Textshell, minimiert), wartet die App in `present`; ihre Uhr steht still, damit sie keine Spieltakte nachholt.
- **Fokus:** Gehaltene Tasten werden bei Fokusverlust und `Reset` losgelassen.
- **Beenden:** Doom-Menü (Exit 0) schließt das Fenster; das X fordert über `should_exit` das Ende an. Exitcode ≠ 0 zeigt die letzte stderr-Zeile im Fenster, Traps den Wasm-Fehler.
- **Einzelinstanz:** Ein zweiter Start holt das vorhandene Fenster nach vorn; eine zweite Instanz würde bis zum Ende der ersten auf den einzigen Worker warten.
- **Quit/Error:** doomgeneric registriert `G_CheckDemoStatus` per Cast als `void`-Exitfunktion; Wasm bricht den Aufruf mit `indirect call type mismatch` ab, und `I_Quit` beendet das Programm nicht. `I_Quit`/`I_Error` werden deshalb per `--wrap` in der Plattformschicht ersetzt.
- **Speicher:** Ohne Sammeln nach jedem Lauf blieben im Desktop rund 30 MiB je Doom-Start liegen (vermutlich Lesepuffer der 28-MB-WAD); der Worker ruft danach `GC.Collect()` auf.

Geprüft mit `scripts/test-wasm-doom-desktop.py` (QEMU TCG, 512 MiB, 1920×1080, QMP-Tastatur und USB-Tablet; Bewertung anhand der Bildschirmfotos) am 15. November 2026:

| Prüfung | Ergebnis |
| --- | --- |
| Start aus dem Programmmenü | Titel/Demo im Fenster |
| Neues Spiel, laufen, drehen, schießen | E1M1, Ansicht ändert sich, Munition 50 → 48 |
| Alt+Tab mit gehaltener Taste | Figur läuft nach Rückkehr nicht weiter |
| 10 s Textshell (F4), zurück | Zustand unverändert, danach weiter spielbar |
| Esc → Textshell, F4 zurück | Fenster und Spielstand erhalten |
| Schließen über X | Fenster weg, Desktop bedienbar |
| 10 Starts, jeweils Beenden über Doom-Menü | alle Starts laufen; frei 446 MiB vorher, 405 MiB bei laufendem Doom, 445 MiB danach |
| fehlende WAD | „App beendet (Exitcode 1): IWAD file 'freedoom1.wad' …“ |
| abgeschnittene WAD (100 000 Byte) | „App beendet (Exitcode 1): W_GetNumForName: PNAME…“ |

**Gate B ist damit in QEMU erfüllt.** Grenzen: Fehlermeldungen werden am Fensterrand abgeschnitten; VMware und echte Hardware ungeprüft. Der feste Ordner ist inzwischen durch den signierten Paketbestand ersetzt; die Prüfung mit Installation aus dem Repository steht in [PACKAGE_FORMAT.md](PACKAGE_FORMAT.md).

## Grenzen und offene Punkte

- Apps kommen nur aus dem signierten Paketbestand und lesen nur ihr eigenes Paket. Es gibt keinen Shellbefehl `run` für Konsolen-Apps und nur eine Paket-App zur Zeit. Einen Abbruch von außen gibt es nur über `should_exit` und Gas.
- Die C#-App läuft doppelt interpretiert (Mono-Interpreter in wasm3); Start rund 4 s. Für Doom ist der C-Weg (doomgeneric) vorgesehen.
- Der WASIp1-Umbau hängt an internen Targets des .NET-10-WASI-SDK und muss bei SDK-Updates neu geprüft werden.
- `wasi:http`-Importe sind unverknüpft; HttpClient in Apps funktioniert nicht.
- VMware und echte Hardware ungeprüft.
