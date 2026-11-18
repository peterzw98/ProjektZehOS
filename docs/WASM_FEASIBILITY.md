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
- `ProjektZeh/Native/Wasm/host/zehos_host_wasm3.c` (wasm3-Bindung) und `zehos_host_core.c` (Hostfunktionen, Tabelle `ZEHOS_HOST_FUNCTIONS` in `zehos_host_core.h`): `zehos_wasm_run` mit Gas-, Speicher- und Stacklimit, WASI-Teilmenge (stdout/stderr, `argv[0]`, Uhren, Zufall über RDRAND/RDSEED, `proc_exit`); Dateisystem und Sockets antworten mit `EBADF`/`ENOSYS`. Nur primitive Typen über die C/C#-Grenze.
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
| WASI `random_get` (15. November 2026, KVM) | 0 mit `-cpu max`; `EIO` (29) mit `-cpu max,-rdrand,-rdseed`. Eine C#- oder wasi-libc-App ohne Hardwarezufall wurde nicht gestartet; erwartet wird ein Trap über `arc4random` statt des früheren Weiterlaufens mit Nullen. |
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

Mit `-p:ZehOSWasm=true` (ohne Probe) erscheint Doom im Programmmenü, sobald das Paket installiert ist ([PACKAGE_FORMAT.md](PACKAGE_FORMAT.md)). Die folgenden Gate-B-Messungen stammen noch aus dem Zwischenstand mit festem Ordner `/data/doom`. `WasmGameApp` lädt den Einstiegspunkt des Pakets, gibt dessen `files/`-Ordner lesend frei und läuft auf dem gemeinsamen Wasm-Worker; Bilder (Doppelpuffer) und Tasten (begrenzte Warteschlange, 64 Einträge) laufen gesperrt zwischen Worker und Desktop. Das Bild wird seitentreu eingepasst (nächster Nachbar; Doom im Standardfenster 798×498, früher ganzzahlig 2× mit Rand).

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

**Automatisierter Langlauf am 15. November 2026 (Linux, QEMU TCG, CPU `max`, 512 MiB):** Mit dem Wasm-Release-Build auf Stand `1c0e070` und einem isolierten Datenlaufwerk wurden zehn Doom-Neustarts über das Menü geprüft. Der freie Speicher nach dem Aufräumen sank von 436,3 auf 435,3 MiB. Ein weiterer Lauf mit `--starts 0 --long-seconds 300` sendete 300,5 s lang Bewegungs-, Dreh-, Schuss- und Benutzungsereignisse; nach einem Tod startete die Figur erneut. Doom endete regulär; der freie Speicher nach dem Aufräumen betrug 437,2 MiB vor und 435,8 MiB nach dem Langlauf. Bildschirmfotos zeigen Änderungen im Spiel und einen weiterhin aktualisierten Taskmanager. Dessen angezeigte längste **Desktop**-Bildzeit betrug in einzelnen Messfenstern ohne Doom 20 und 23 ms und unter Doom-Last 42 bis 84 ms; dies sind Stichproben, keine Worst-Case-Garantie und keine Messung der Doom-Bildzeit. Die automatischen Eingaben ersetzen keine menschliche Spielprüfung. Doom-Bildzeit über mehrere Minuten, VMware und echte Hardware bleiben offen.

**Doom-Bildzeit über mehrere Minuten (15. November 2026, Linux, QEMU 10.2.2, 512 MiB, 1920×1080):** `WasmGameApp` misst für das Paket `doom` den Abstand zwischen zwei Bildern von Doom (`present_indexed`). Die Werte landen in einem festen 1-ms-Histogramm ohne Allokation pro Bild; je 60 s Spielzeit erscheint seriell `APP doom Bildzeit n=… min=… median=… p95=… max=… ms pausen=… heap=… KiB`. Andere Apps werden nicht gemessen. Abstände, in denen die App pausiert war (Textshell, Minimieren, Fokuswechsel, aber auch ein Desktop ohne Zeichnen über 250 ms), zählen nicht als Bildzeit; `pausen` nennt ihre Zahl, damit solche Stillstände nicht unbemerkt aus der Messung fallen. Gemessen wurde mit dem Release-ISO 0.2.16 mit Pausenzähler und `scripts/test-wasm-doom-desktop.py --scenario full --starts 0 --source https://packages.pjoter.de/`; die automatischen Eingaben gleichen dem Langlauf oben, der Taskmanager war offen.

**KVM** (`--accel kvm --long-seconds 300`, CPU `qemu64,+rdrand,+rdseed`, 300,8 s): fünf Fenster. Die Zeile des fünften Fensters begann im seriellen Protokoll mit überschriebener Cosmos-Debugausgabe (`[LowLev Bildzeit n=3667 …`) und fehlte deshalb zunächst in der Auswertung; das Skript sucht seither ab `Bildzeit n=`.

| Fenster | Bilder | min | Median | p95 | max (ms) | Pausen |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 3596 | 1 | 12 | 38 | 244 | 0 |
| 2 | 3661 | 2 | 12 | 38 | 60 | 0 |
| 3 | 3685 | 2 | 12 | 37 | 79 | 0 |
| 4 | 3700 | 2 | 12 | 37 | 64 | 0 |
| 5 | 3667 | 2 | 13 | 38 | 59 | 0 |

Insgesamt 18 309 Bilder in 300,8 s, rund 61 Bilder/s. Der freie Speicher nach dem Aufräumen betrug 438,7 MiB nach dem ersten Doom-Lauf und 437,6 MiB nach dem Langlauf.

**TCG** (`--long-seconds 600`, CPU `max`, 602,2 s). TCG emuliert die CPU in Software; diese Bildzeiten sind Emulationswerte, nicht repräsentativ für KVM, VMware oder echte Hardware, und dienen nur dem Vergleich zwischen Ständen. Der Lauf teilte sich die ersten rund acht Minuten den Host mit dem KVM-Lauf.

| Fenster | Bilder | min | Median | p95 | max (ms) | Pausen |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 841 | 6 | 60 | 169 | 663 | 0 |
| 2 | 673 | 21 | 84 | 164 | 218 | 0 |
| 3 | 726 | 21 | 75 | 157 | 234 | 0 |
| 4 | 755 | 23 | 73 | 146 | 193 | 0 |
| 5 | 858 | 21 | 58 | 143 | 247 | 0 |
| 6 | 623 | 25 | 94 | 159 | 212 | 0 |
| 7 | 717 | 7 | 78 | 161 | 549 | 0 |
| 8 | 723 | 21 | 79 | 147 | 194 | 0 |
| 9 | 711 | 24 | 77 | 148 | 198 | 0 |
| 10 | 709 | 7 | 78 | 163 | 404 | 0 |

Insgesamt 7336 Bilder, rund 12,2 Bilder/s; ein früherer TCG-Lauf vor Einführung des Pausenzählers (sonst gleicher Kernelcode, Host allein) ergab 8297 Bilder, rund 13,8 Bilder/s, Median 51–79 ms, p95 bis 157 ms. Die fensterlose Probe erreichte unter TCG 23,7 Bilder/s mit Titel und Demos; dort fehlen Desktop, Skalierung und Taskmanager, die Ursache des Unterschieds wurde nicht untersucht. Freier Speicher nach dem Aufräumen: 416,5 MiB nach dem ersten Doom-Lauf, 415,2 MiB nach dem Langlauf.

In beiden Läufen liegt das größte Maximum im ersten Fenster, das Menü und Levelstart enthält; ob es von dort stammt, zeigt die Messung nicht. Es gab keine Pausen. Die Heap-Angabe änderte sich während der Fenster nicht (KVM 28 477 KiB, TCG 51 475 KiB); erst nach dem Aufräumen stand sie bei 29 645 bzw. 52 676 KiB. Als Verlauf über die Spielzeit taugt sie deshalb nicht. Bildschirmfotos zeigen E1M1 im Spiel und einen weiter aktualisierten Taskmanager. VMware, echte Hardware und eine menschliche Spielprüfung sind damit nicht geprüft.

**Gate B ist damit in QEMU erfüllt.** Grenzen: Fehlermeldungen werden am Fensterrand abgeschnitten; VMware und echte Hardware ungeprüft. Der feste Ordner ist inzwischen durch den signierten Paketbestand ersetzt; die Prüfung mit Installation aus dem Repository steht in [PACKAGE_FORMAT.md](PACKAGE_FORMAT.md).

## Bildzeit-Anteile (ClassiCube-Etappe 1, 17. November 2026)

`frametimes on` (Shell, beliebiges Terminal) zerlegt die Bildzeit laufender Paket-Apps; je 60-s-Fenster der App schreibt der Kernel neben `Bildzeit …` eine Zeile `Anteile (ms, median/p95/max) …` ins serielle Protokoll. Anteile: `app` (Wanduhrzeit von der Rückkehr aus `present` bis zum nächsten `present`, ohne `sleep_ms`; enthält auch die Zeit, in der der Hauptthread mit `desktop` und `anzeige` die eine CPU belegt, die Anteile überlappen sich also), `schlaf` (`sleep_ms` der App), `kopie` (Bild in den Kernelpuffer), `skalierung` (`WasmImageFit`), `desktop` (Zusammensetzen des Desktops) und `anzeige` (`Canvas.Display()` bzw. Zeigerausgabe). Desktop und Anzeige laufen auf dem Hauptthread, die übrigen auf dem Wasm-Worker; alle teilen sich eine CPU. Mehrere gleichzeitig laufende Apps landen in denselben Histogrammen. Ausgeschaltet kostet jede Messstelle nur eine Abfrage, pro Messung wird nicht allokiert. Messskript: `scripts/measure-wasm-frametimes.py --iso <ISO> --accel kvm|tcg` (Doom 1.1.0 aus `packages`, Titel-/Demoablauf ohne Eingabe, frische Platte).

**Befund:** Bei laufender App setzte der Desktop jedes 60-Hz-Bild neu zusammen und gab es vollständig aus, unabhängig davon, ob die App ein neues Bild geliefert hatte. Unter KVM (Host-Last 32 durch andere Gäste) waren das rund 58 Desktopbilder/s bei rund 25 Doom-Bildern/s; aus Median × Anzahl ergeben sich rund 19,7 s Desktop- und Anzeigearbeit je 60 s, also etwa ein Drittel der einen CPU. **Behoben:** `WasmGameApp.NeedsAnimation` verlangt ein Zeichnen nur noch bei neuem Bild, wartendem `present` oder als Lebenszeichen alle 100 ms. Ohne das Lebenszeichen pausierte Doom unter TCG (Bilder über 250 ms) bei jedem `present` (`pausen=44–52` je Fenster).

| QEMU 10.2.2, Doom 1.1.0, je 60 s | Bildzeit median/p95 ms | app | schlaf | kopie | skalierung | desktop | anzeige | Desktopbilder/Doom-Bilder |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| KVM vorher (2 Fenster) | 32/102, 41/106 | 25,6–39,7 | 0,1 | 0,1 | 0,6 | 4,3–5,5 | 1,4–1,8 | 3462/1492, 2672/1220 |
| KVM nachher (ohne Lebenszeichen) | 39/98, 24/86 | 22,2–38,0 | 0,1 | 0,1 | 0,5–0,6 | 4,4–4,6 | 1,4 | 1418/1421, 1544/1693 |
| TCG nachher, mit Lebenszeichen (3 Fenster) | 300/892, 339/822, 295/672 | 295–339 | 0,0–0,1 | 0,2 | 3,8–5,1 | 27,6–35,1 | 9,9–13,4 | 894/163, 557/157, 561/168 |

Medianwerte der Anteile in ms. Unter TCG ist `app` ≈ Bildzeit; darin stecken aber auch rund 33 s Desktop- und Anzeigearbeit je 60 s (894 Desktopbilder, davon rund 600 Lebenszeichen), reine Rechenbegrenzung von Doom ist damit nicht belegt; unter KVM dominieren ebenfalls `app` und die Konkurrenz um die CPU, die Bildkopie und `Canvas.Display()` sind klein. Die KVM-Bildzeiten schwanken wegen der Host-Last stark; ein Vorher/Nachher-Vergleich der Doom-Bildzeit ist daraus nicht belastbar, belegt ist die Halbierung der Desktoparbeit. Der Limine-Bildpuffer ist laut Codebefund voraussichtlich bereits write-combining ([COSMOS_SUPPORT.md](COSMOS_SUPPORT.md)); `anzeige` (1,4 ms unter KVM) bestätigt keinen Engpass dort. Offen: Messung unter geringer Host-Last, KVM mit Lebenszeichen (ausgelieferter Stand; jedes Lebenszeichen setzt den Desktop vollständig zusammen), VMware (VM ohne Datenlaufwerk, Paket-Apps dort nicht prüfbar), Bereichsübertragung statt Vollbild-Zusammensetzung (Anteil `desktop`).

## ClassiCube: Sichtweite unter KVM und WHPX (17. November 2026)

Frage: Begrenzt die Geometrie (Sichtweite) oder ein fester Aufwand die Bildrate von ClassiCube unter wasm3 mit Host-Raster? Kernel `master` 1227e60 (0.5.71) mit Testschlüssel, Paket aus ClassiCube 2b01bee mit `build.py --world-seed 1` (Messpunkte an), Fenster 798×519, Spieler ohne Eingabe am Spawnpunkt. Gemessen wurde mit `measure-wasm-frametimes.py` vom Branch `task/aot-measure` (085cc54, noch nicht in `master`): `--runs 3 --seconds 130`, Starttasten `ret wait:25` und danach 0, 2 oder 4× `shift-f` für die Sichtweiten 512, 128 und 32. Gewertet ist das letzte 60-s-Fenster jedes Laufs.

| Sichtweite | KVM Bildzeit median/p95 ms | KVM Bilder je 60 s | WHPX median/p95 ms |
| --- | --- | --- | --- |
| 512 (Standard) | 23/120, 24/102, 23/101 | 1462, 1495, 1564 | 28/83, 59/104, 44/100 |
| 128 | 33/101, 25/120, 24/124 | 1476, 1466, 1426 | 44/97, 54/98, 61/108 |
| 32 | 22/100, 19/77, 20/82 | 1703, 2284, 1990 | 39/76, 53/100, 37/82 |

KVM: Linux-Host mit 22 Kernen, Last 1–2 durch andere Gäste. WHPX: Messung von agent-peter-cc-lead auf dem Windows-Entwicklungsrechner; dort liefen zeitweise zwei `dotnet`-Builds parallel, die Streuung ist deshalb größer.

- **Nur 32 verringert die Geometrie.** Mit Sichtweite 128 und 512 zeigen die Endfotos dieselbe Szene (108 618 bzw. 108 630 Vertices, mit 32 dagegen 71 478). Die Welt mit Seed 1 ist für 128 zu klein; laut cc-lead gilt das ebenso für die WHPX-Läufe. Der Vergleich 512/128 zeigt deshalb nur die Streuung zwischen Läufen.
- **Sichtweite 32 bringt unter KVM rund 30 % mehr Bilder je Minute** und ein niedrigeres p95. Der Median ändert sich kaum (20 statt 23 ms), weil die Bildzeiten zweigipflig verteilt sind; die Bildzahl je Fenster ist hier das bessere Maß.
- **Engpass ist die App, nicht Desktop oder Anzeige.** Medianwerte der Anteile bei allen drei Sichtweiten: `app` 19–30 ms, `desktop` 2,1–2,6 ms (p95 10–12 ms), `anzeige` 0,8–0,9 ms, `kopie` 0,2 ms, `skalierung` 0,3–0,4 ms. In `app` liegen wasm3 und die Aufrufe des Host-Rasters. Die eigene Messung von ClassiCube (nur auf den Endfotos lesbar) zeigt Raster 31–32 ms je Bild bei 512 und 20–21 ms bei 32; Mesh, Present und Rest liegen bei 0–3 ms.

Nicht geprüft: Bewegung und andere Blickrichtungen, andere Fenstergrößen (ohne Kerneländerung nicht einstellbar), TCG und VMware.

## Messung wasm3 gegen AOT (AOT-Plan Etappe F)

Aufrufe für den Tester unter Linux, vom Repository-Stamm. `KEY` ist ein eigenes Testschlüsselpaar (`python3 scripts/build-package.py keygen <dir>`), nie der Herausgeberschlüssel. `<wamrc>` ist das zugelassene wamrc 2.4.5 (Hash in `scripts/build-package.py`); `aot` legt den Beleg `<app.aot>.json` daneben, den `package --aot-file` prüft.

```sh
KEY=~/zehos-testkey   # enthält private.pem und public.b64
# 1. Kernel mit WAMR, vertraut dem Testschlüssel
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:ZehOSWasmAot=true -p:ZehOSPackageKeyPath=$KEY/public.b64
ISO=ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso
# 2. Paketdateien aus packages (ClassiCube samt texpacks/ und licenses/) und .aot erzeugen
mkdir -p .tools/aot/doom .tools/aot/classicube
git archive origin/packages doom/1.1.0/files | tar -x -C .tools/aot/doom --strip-components=3
git archive origin/packages classicube/1.0.0/files | tar -x -C .tools/aot/classicube --strip-components=3
python3 scripts/build-package.py aot <wamrc> .tools/aot/doom/doom.wasm -o .tools/aot/doom.aot
python3 scripts/build-package.py aot <wamrc> .tools/aot/classicube/classicube.wasm -o .tools/aot/classicube.aot
# 3. Messen: je Beschleunigung, Laufzeit und App drei Läufe auf frischer Platte
DOOM="--package .tools/aot/doom --key $KEY/private.pem --aot-file .tools/aot/doom.aot"
CC="--package .tools/aot/classicube --key $KEY/private.pem --aot-file .tools/aot/classicube.aot \
  --id classicube --version 1.0.0 --name ClassiCube --entry classicube.wasm --network --min-kernel 0.5.13 --start-keys ret"
for ACCEL in kvm tcg; do for AOT in off on; do
  python3 scripts/measure-wasm-frametimes.py --iso $ISO --accel $ACCEL --runs 3 --seconds 130 --aot $AOT $DOOM
  python3 scripts/measure-wasm-frametimes.py --iso $ISO --accel $ACCEL --runs 3 --seconds 130 --aot $AOT $CC
done; done
```

Der Gastspeicher ist für `--id classicube` 1 GiB, sonst 512 MiB (`-m`/`--memory` überschreibt): ClassiCube braucht mindestens 768 MiB, und mit 512 MiB lag die Bildrate im Vergleich vom 18. November 2026 (Seed 5, stehend, KVM, je 2 Läufe) bei 16,3 statt 20,0 avg fps. Ältere ClassiCube-Reihen in diesem Dokument liefen mit 512 MiB und sind dadurch zu niedrig. Mit `--move` hält das Skript während der Messung abwechselnd W/A/S/D (bewegte Kamera, fester Tastenpfad); jede Zusammenfassung nennt zusätzlich die durchschnittliche Bildrate (`avg fps` = Bilder des letzten 60-s-Fensters / 60), die Messlatte für ClassiCube. Beide Laufzeiten nutzen dasselbe Paket mit `.aot`: `--aot off` lässt nur `wasmaot on` weg (Ausgabe `Laufzeit: wasm3`), `--aot on` bricht ab, wenn nicht `Laufzeit=aot …` erscheint. Gewertet wird das letzte 60-s-Fenster je Lauf (Zusammenfassung des Skripts); das erste enthält Start, den ClassiCube-Launcher (Enter startet Singleplayer) und das Laden der Welt. ClassiCube wird ohne Eingabe im Stand am Spawnpunkt gemessen. VMware ist mit dem Skript nicht messbar (QEMU-Werkzeug, VMware-VM ohne Datenlaufwerk).

### Ergebnisse (Stand 17. November 2026)

Gemessen von agent-laser-right-tester (Linux, KVM und TCG, Stand `3060e5a`), agent-laser-RED (TCG, master `1227e60`) und cc-lead (Windows, WHPX). Bildzeit ist der Median des letzten 60-s-Fensters je Lauf in ms.

| App | Beschleunigung | wasm3 | AOT |
| --- | --- | --- | --- |
| Doom 1.1.0 | KVM | 19 (App-Anteil 18) | 11 (App-Anteil 0,4); 3 von 3 Läufen ohne Fehler |
| Doom 1.1.0 | TCG | 281 | Stand `5f04669`: kein erstes Bild in 600 s. Stand `1227e60`: in 1 von 5 Läufen Kernel-Panik (#GP, nicht kanonische RIP) vor dem ersten Bild |
| ClassiCube 1.0.0 | KVM | 41 / 53 / 59 (p95 159–218) | Trap „out of bounds memory access“ vor oder kurz nach dem ersten Bild (KVM und TCG) |
| ClassiCube 1.0.0 | WHPX | 29–61 je nach Lauf | in 2 von 3 Läufen Trap; mit Diagnosekernel in 1 von 3 Läufen Hänger bei der Weltgenerierung, sonst 40 bzw. 54 ms |

Doom zeigt den erwarteten Gewinn: Der App-Anteil sinkt unter AOT von 18 ms auf 0,4 ms je Bild. Was übrig bleibt, sind Desktop, Skalierung und Anzeige. ClassiCube ist unter wasm3 durch das Rastern im Host begrenzt (siehe die Messung der Sichtweite weiter oben). AOT würde dort nur den Gastanteil verkleinern.

**Befund AOT: Red Zone (Hypothese mit Belegen).** Die Fehler unter AOT sind zufällig: Trap, Hänger, #GP mit Müll-RIP, je nach Lauf. Kein Hostaufruf trappt dabei, und keine Speicheranforderung scheitert (Diagnosekernel mit Protokoll jedes `os_mmap`/`os_mremap`: `memory.grow` bis 16 MiB ohne NULL). Wahrscheinliche Ursache ist die 128-Byte-Red-Zone der System-V-ABI:

- Der von wamrc erzeugte Code legt in Blattfunktionen Werte unterhalb von RSP ab. `objdump` zählt Schreibzugriffe `mov reg,-0x..(%rsp)`: 229 in `classicube.aot`, 107 in `doom.aot`.
- Cosmos 3.0.97 betritt Interrupts im Ring 0 ohne IST und ohne Stackwechsel (`CPU/Interrupts.s`). CPU-Frame und gesicherte Register landen direkt unter dem aktuellen RSP. Ein Timer-Interrupt in einer solchen Blattfunktion überschreibt deren lokale Werte.
- Der Kernel-C-Code (wasm3, WAMR-Lader, Hostfunktionen, C-Laufzeit) nutzt die Red Zone gemessen nicht: 0 Treffer in allen C-Objekten. Das passt dazu, dass die Fehler nur unter AOT auftreten.

wamrc 2.4.5 hat keinen Schalter dafür (`--mllvm=-help-hidden` kennt keine Red-Zone-Option). Nötig ist ein wamrc, der allen erzeugten Funktionen das LLVM-Attribut `noredzone` gibt. Das bedeutet eine neue zugelassene Prüfsumme in `scripts/build-package.py` und eine neue AOT-Kennung, damit vorhandene `.aot` nicht mehr geladen werden. Die Entscheidung darüber liegt bei Peter. Kernelseitig ginge es nur mit einem Umbau des Cosmos-Kontextwechsels (eigenes TSS mit IST für den Timer-Interrupt; der Kontext müsste dann vom IST-Stack auf den Thread-Stack kopiert werden, vgl. ROADMAP 12.4).

Für Nutzer besteht derzeit kein Risiko: Normale und Release-Kernel enthalten WAMR gar nicht (nur mit `-p:ZehOSWasmAot=true`), `wasmaot` ist auch dort standardmäßig aus, und kein veröffentlichtes Paket enthält ein `.aot`. Bis zu einem wamrc mit `noredzone` bleibt AOT ein Diagnoseweg; die Entscheidungen aus Etappe F (AOT einschalten, NetSurf unter AOT, Ziel 30 Bilder/s für ClassiCube) sind damit offen. Bewiesen ist die Ursache erst, wenn ein Diagnosebau mit `noredzone` die Fehler beseitigt; dieser Bau mit je 10 Vergleichsläufen ist beauftragt.

## Grenzen und offene Punkte

- Apps kommen nur aus dem signierten Paketbestand und lesen nur ihr eigenes Paket. Es gibt keinen Shellbefehl `run` für Konsolen-Apps und nur eine Paket-App zur Zeit. Einen Abbruch von außen gibt es nur über `should_exit` und Gas.
- Die C#-App läuft doppelt interpretiert (Mono-Interpreter in wasm3); Start rund 4 s. Für Doom ist der C-Weg (doomgeneric) vorgesehen.
- Der WASIp1-Umbau hängt an internen Targets des .NET-10-WASI-SDK und muss bei SDK-Updates neu geprüft werden.
- `wasi:http`-Importe sind unverknüpft; HttpClient in Apps funktioniert nicht.
- VMware und echte Hardware ungeprüft.
