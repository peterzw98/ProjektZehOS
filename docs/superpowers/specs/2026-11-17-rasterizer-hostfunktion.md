# Entwurf: Software-Rasterizer als Hostfunktion (ClassiCube-Plan, Etappe 3)

Stand 17. November 2026, agent-peter-classicube-port. Grundlage: [ClassiCube-Plan](../plans/2026-11-16-classicube.md), Etappe 3; Messwerte aus Etappe 2 in `experiments/ClassiCube.Wasi/README.md` (PR #121). Dieser Entwurf geht der Umsetzung voraus (Plan: „Kurzer Entwurf unter `docs/superpowers/specs/` vor der Umsetzung“). Entscheidungen, die Peter treffen sollte, stehen in Abschnitt 9.

## 1 Ziel und Messgrundlage

ClassiCube verbringt unter wasm3 rund 70–90 % der Bildzeit in der Rasterisierung (`Graphics_SoftGPU.c`): unter QEMU/WHPX 110–180 ms je Bild bei 5–6 Bildern/s. Das Rasterisieren soll nativ im Kernel laufen. Das Bild bleibt gleich, die Bildrate steigt messbar, Fehler beenden nur die App. Spiellogik, Mesh-Bau und Matrizen bleiben im Gast; sie kosten zusammen unter 20 % der Bildzeit.

Erwartung (nicht gemessen): wasm3 ist für solche Schleifen etwa 5- bis 15-mal langsamer als nativer Code. Fällt das Rastern von 140 ms auf 10–30 ms, steigt ClassiCube unter WHPX von 5–6 auf etwa 15–25 Bilder/s.

## 2 Bedarf, abgeleitet aus SoftGPU (ClassiCube 2b01bee8)

Die Funktion bildet genau das ab, was `Graphics_SoftGPU.c` heute im Gast tut, nicht mehr:

- **Primitive:** nur Quads (je 4 Vertices, als 2 Dreiecke 0-2-1 und 2-0-3). `Gfx_DrawVb_Lines` ist auch in SoftGPU leer.
- **Vertexformate:** farbig, 16 Byte `{float x, y, z; u32 rgba}`; texturiert, 24 Byte `{float x, y, z; u32 rgba; float u, v}`. Farbe in Speicherreihenfolge R, G, B, A.
- **3D-Pfad:** MVP-Matrix (4×4 float, Zeilen wie ClassiCube `struct Matrix`), Texturversatz (u, v), Near-Clipping an z = 0 mit den Fällen aus `DrawClipped` (1–3 sichtbare Ecken), Viewport-Transformation, perspektivisch korrekte Interpolation von u, v und z über 1/w, Kantenfunktionen auf ganzzahligen Ecken (Abtastpunkt Pixelmitte).
- **2D-Pfad** (`gfx_rendering2D`): Positionen ohne Matrix. Sprite-/Rechteck-Abkürzung bei `DRAW_HINT_SPRITE|DRAW_HINT_RECT`, sonst Dreiecke ohne Tiefe.
- **Zustände:** Tiefentest (float, `z < 0 || z > depth` verwirft), Tiefe schreiben, Farbe schreiben (an/aus), Alphatest (A < 0x80 verwirft), Alphablending (`dst + (A * (src - dst) >> 8)`), Backface-Culling (Fläche < 0), Scissor als Obergrenze (`fb_maxX/Y`).
- **Texturen:** 32 Bit `0xAARRGGBB`, Breite und Höhe Zweierpotenzen bis 4096. Adressierung mit Maske (Wiederholung), nächster Nachbar, Farbmodulation `(vertex * texel) >> 8` je Kanal. Ist die Textur 1 Pixel breit, gilt eine Abkürzung mit fester Farbe.
- **Ziele:** Farbpuffer 32 Bit `0xAARRGGBB` mit Zeilenabstand, Tiefenpuffer float mit eigenem Zeilenabstand, beide im Gastspeicher.
- **Löschen:** Farbe auf einen Wert, Tiefe auf 1e8. Zählt in der Messung zur Rasterisierung.
- **Nicht nötig:** Nebel (SoftGPU ignoriert ihn), Mipmaps, Linien, Mehrfach-Ziele, Shader.

## 3 Schnittstelle (`zehos_v1`, Version 1)

### 3.1 Kein Ressourcenzustand im Kernel

Anders als im Plan skizziert („Texturen als Handles, Befehlspuffer je Bild“) arbeitet der Host **direkt auf Gastspeicher**: Vertexdaten, Textur, Farb- und Tiefenpuffer bleiben, wo SoftGPU sie heute hat. Jeder Aufruf beschreibt einen Zeichenauftrag vollständig.

- Keine Handles, keine Uploads, keine Lebensdauer: Nach einem Trap oder Ende der App ist nichts freizugeben, Obergrenzen für Handles und Texturspeicher entfallen.
- Keine Kopie der Texturen: ClassiCube hält den Terrain-Atlas ohnehin im Gast; eine Kopie würde nur Speicher verdoppeln.
- Ein Aufruf je `Gfx_DrawVb_*` (heute einige hundert je Bild) statt eines Befehlspuffers. Der Übergang Gast→Host kostet unter wasm3 wenige Mikrosekunden; ein Befehlspuffer lohnt erst, wenn die Messung das Gegenteil zeigt.
- Die ABI hängt nicht an ClassiCube: Jede 3D-App mit Quads, Matrix, Tiefe und Textur kann sie nutzen (Risiko „zu eng zugeschnitten“ aus dem Plan).

### 3.2 Imports

| Import | Signatur | Zweck |
| --- | --- | --- |
| `raster_draw(call)` | `i(i)` | Zeichnet `count` Vertices als Quads nach der Beschreibung `zehos_raster_call` an Adresse `call` |
| `raster_fill(ptr, count, value)` | `i(iii)` | Füllt `count` 32-Bit-Werte ab `ptr` mit `value` (Farbe löschen, Tiefe 1e8 als Bitmuster) |

Rückgabe: WASI-Fehlercode, 0 bei Erfolg, `EINVAL` (28) bei unzulässigen Werten (Format, Zweierpotenz, Grenzen aus 3.4). Ungültige Gastbereiche (Zeiger und Länge außerhalb des linearen Speichers, Überlauf) **trappen**, wie bei den übrigen `zehos_v1`-Funktionen; geprüft wird vor jedem Zugriff.

### 3.3 `zehos_raster_call` (Little Endian, 140 Byte, Ausrichtung beliebig)

| Offset | Typ | Feld |
| --- | --- | --- |
| 0 | u32 | `version` = 1 |
| 4 | u32 | `flags`: Bit 0 Tiefentest, 1 Tiefe schreiben, 2 Farbe schreiben, 3 Alphatest, 4 Alphablending, 5 Backface-Culling, 6 2D, 7 Sprite-Abkürzung (nur 2D), 8 texturiert |
| 8 | u32 | `vertices` (Gastadresse) |
| 12 | u32 | `count` (Vielfaches von 4) |
| 16 | u32 | `color` (Gastadresse), 20 `color_stride` (in Pixeln), 24 `width`, 28 `height` |
| 32 | u32 | `depth` (Gastadresse, 0 = kein Tiefenpuffer), 36 `depth_stride` (in Werten) |
| 40 | u32 | `texture` (Gastadresse), 44 `tex_width`, 48 `tex_height` |
| 52 | u32 | `scissor_max_x`, 56 `scissor_max_y` (einschließlich) |
| 60 | f32 | `viewport_half_width`, 64 `viewport_half_height` |
| 68 | f32 | `tex_offset_u`, 72 `tex_offset_v` |
| 76 | f32[16] | `mvp` (row1.x, row1.y, … row4.w), nur 3D; endet bei Offset 140 |

`version` erlaubt spätere Erweiterungen; ein Kernel lehnt unbekannte Versionen mit `EINVAL` ab. Vertexfarben sind u32 mit den Bytes R, G, B, A (`0xAABBGGRR`), Farbpuffer und Textur `0xAARRGGBB`; Vertexoffsets: Position 0/4/8, Farbe 12, u/v 16/20.

Die Umwandlung float→int bildet clang für wasm32 ohne `nontrapping-fptoint` nach (außerhalb von int und NaN ergeben `INT_MIN`). `experiments/ClassiCube.Wasi/build.py` setzt deshalb `-mno-nontrapping-fptoint` ausdrücklich, damit ein späteres wasi-sdk das Bild nicht verändert.

### 3.4 Grenzen und Prüfungen

- `width`, `height` 1…1920 bzw. 1…1200 (wie `present`), `color_stride ≥ width`, `depth_stride ≥ width`; Puffergröße `stride * height * 4` überlaufsicher in 64 Bit gerechnet und vollständig im Gastspeicher.
- `scissor_max_x < width`, `scissor_max_y < height`.
- Texturiert: `tex_width`, `tex_height` Zweierpotenzen 1…4096, Bereich `w * h * 4` im Gastspeicher. Untexturiert: Feld `texture` wird ignoriert.
- `count ≤ 262 144` Vertices je Aufruf (65 536 Quads); Bereich `count * stride` im Gastspeicher. Die Arbeit je Aufruf ist damit durch Quads × Zielfläche begrenzt; einen Gaszähler gibt es für Paket-Apps auch sonst nicht.
- Gleitkommawerte aus dem Gast (NaN, ±∞, riesige Koordinaten) dürfen keinen undefinierten Zustand erzeugen: Ecken werden vor der Umwandlung in `int` auf einen sicheren Bereich begrenzt, Dreiecke mit nicht endlichen Ecken verworfen. Das ist bewusst strenger als SoftGPU, das hier undefiniert in `int` umwandelt.
- Ausnahmen überqueren die native Grenze nicht (`UnmanagedCallersOnly`, `AGENTS.md`).

### 3.5 Erkennbar auf älteren Kerneln

wasm3 übersetzt eine Funktion erst bei ihrem ersten Aufruf (`op_Compile`), und der Loader ignoriert unbekannte Imports (`zehos_host_wasm3.c`, `m3Err_functionLookupFailed`). Ein Modul, das `raster_draw` importiert, lädt also auch auf älteren Kerneln. Es trappt erst, wenn der fehlende Import tatsächlich aufgerufen wird: wasm3 setzt auch für Imports das Modul (`m3_module.c`), übersetzt den Aufruf deshalb als `op_Compile` und meldet `functionImportMissing` erst zur Laufzeit (`m3_compile.c`). (Ursprünglich hieß es hier, schon das Übersetzen des Aufrufers trappe; das unabhängige Review hat das am Code widerlegt.)

Der Kernel meldet die Fähigkeit deshalb über die WASI-Umgebung: `ZEHOS_FEATURES=raster_v1` (kommagetrennte Liste, künftig erweiterbar). Ältere Kernel liefern eine leere Umgebung (`docs/PAKETE_ERSTELLEN.md`). Die App prüft die Variable einmal beim Start und ruft den Import nur, wenn `raster_v1` gemeldet ist; sonst bleibt sie bei SoftGPU. So läuft dasselbe Paket ohne `min-kernel`-Sprung auf alten und neuen Kerneln. Die Umgebungsvariable ist eine neue, allgemeine Zusage im Vertrag (`docs/PAKETE_ERSTELLEN.md`, `docs/PACKAGE_FORMAT.md`).

## 4 Ort der Umsetzung im Kernel

| | A: C neben `zehos_host_core.c` (`GCCProject`) | B: C# in `ProjektZeh/Graphics/Raster/` |
| --- | --- | --- |
| Nähe zum Gast | Zeiger direkt aus wasm3 | C-Brücke prüft Bereiche, ruft einmal je Aufruf `zehos_wasm_host_raster` (`UnmanagedCallersOnly`) |
| Hosttests | kein C-Testpfad auf dem Windows-Entwicklungsrechner (kein WSL, kein nativer C-Compiler im Projekt) | MSTest in `ProjektZeh.Tests`, wie die übrige plattformunabhängige Logik |
| Leistung | clang -O2 mit SSE2 | NativeAOT; SIMD nur, wenn die Probe (Abschnitt 6) `Vector128` im Gast belegt |
| Übernahme aus SoftGPU | nahezu wörtlich (BSD-3, Hinweis behalten) | Übertragung nach C#, gleiche Rechenschritte |

**Empfehlung B**, weil der Plan Hosttests mit Pixelvergleich, Grenz- und Fehlerfällen verlangt und das Projekt sie nur für C# bereitstellt. Die Bereichsprüfung des Gastspeichers liegt in C (wie bei den übrigen Hostfunktionen), der Rasterkern in C# arbeitet nur auf bereits geprüften Spans. Zeigt die Gastmessung, dass B mehr als etwa 30 % langsamer ist als nötig für das Ziel aus Abschnitt 1, wird der Kern nach A verschoben; die ABI bleibt gleich. Die Lizenz von SoftGPU (BSD-3) erlaubt beide Wege; der Hinweis kommt in die Quelldatei und `docs/`.

Der Kern ist plattformunabhängig (keine Cosmos-Typen) und kommt in `ProjektZeh.Tests/ProjektZeh.Tests.csproj`. Die C-Brücke (Prüfung, Weitergabe) liegt in `ProjektZeh/Native/Wasm/host/zehos_host_core.c`, Tabelle `ZEHOS_HOST_FUNCTIONS` in `zehos_host_core.h`. Die Umgebung `ZEHOS_FEATURES` setzt `wasi_environ_*` in `zehos_host_core.c`.

## 5 Gastseite (ClassiCube)

- Patch `0003` in `experiments/ClassiCube.Wasi/patches/`: In `Graphics_SoftGPU.c` ruft `DrawQuads` und `Gfx_ClearBuffers` unter `CC_BUILD_ZEHOS` zuerst eine Plattformfunktion `ZehOS_RasterQuads(...)` bzw. `ZehOS_RasterFill(...)` auf, die den aktuellen Zustand übergibt. Liefert sie 0, ist der Auftrag erledigt, sonst zeichnet SoftGPU wie bisher.
- `platform/Raster_ZehOS.c` füllt `zehos_raster_call`, liest `ZEHOS_FEATURES` einmal (`getenv`) und ruft den Import aus einer `noinline`-Funktion.
- Schalter `build.py --raster host|soft` nur für Vergleichsmessungen; Standard ist „host, wenn verfügbar“.
- `Measure_ZehOS.c` misst unverändert: „Raster“ enthält dann die Hostzeit.

## 6 Vorab-Probe: SIMD unter Cosmos 3.0.97/NativeAOT

Bisher nutzt ZehOS aus `System.Runtime.Intrinsics` nur skalare X86-Befehle (`CosmosSaltSource`, `CosmosVmwareClockPlug`). Ob `Vector128<T>`-Operationen im Gast als SSE übersetzt werden und laufen, ist nicht belegt.

- Probe: eine kleine Funktion mit `Vector128<int>`/`Vector128<float>` (Laden, Multiplizieren, Vergleich, Speichern) und `Vector128.IsHardwareAccelerated`, aufgerufen über einen Shellbefehl nur im Probe-Build (vorhandenes Muster `ZehOSWasmProbe`), Ausgabe seriell.
- Ergebnis in `docs/COSMOS_SUPPORT.md`. Ohne SIMD bleibt der Kern skalar; der Entwurf hängt nicht davon ab.

## 7 Tests und Abnahme

**Hosttests (MSTest):**
- Pixelvergleich gegen Referenzbilder: kleine Szenen (Einzelquad, Textur mit Wiederholung, Alphatest, Alphablending, Tiefentest, Culling, Near-Clipping in allen vier Fällen, 2D-Sprite). Referenzen erzeugt SoftGPU selbst: ein Wasm-Testmodul aus `Graphics_SoftGPU.c` ist auf dem Host nicht ausführbar, deshalb werden die Referenzbilder einmal im Gast mit `--raster soft` aufgenommen und als kleine Rohdateien eingecheckt (Herkunft und Skript dokumentiert).
- Fehlerfälle: Version, Format, `count` nicht durch 4 teilbar, Textur keine Zweierpotenz, Grenzen überschritten, Überlauf in `stride * height`, NaN/∞-Ecken, `scissor` außerhalb.
- Grenzen in der C-Brücke (Bereichsprüfung) wie bei den vorhandenen Hostfunktionen: im Gast über ein kleines Wasm-Testmodul, das absichtlich ungültige Bereiche übergibt und den Trap prüft.

**Gast (QEMU und VMware):**
- `scripts/test-classicube-desktop.py` mit Raster-Hostfunktion: gleiche Abläufe wie Etappe 2; zusätzlich Bildvergleich `--raster host` gegen `--raster soft` an fester Position (Saat und Blick fest), höchstens wenige abweichende Pixel.
- Doom und NetSurf unverändert (`test-wasm-doom-desktop.py --scenario quick`, `test-netsurf-desktop.py`).
- Messung vorher/nachher mit `Measure_ZehOS.c` unter WHPX/KVM und TCG, mit Datum.
- Ein Paket mit Raster-Import auf einem Kernel **ohne** die Funktion (ISO 0.5.7 oder 0.5.8 von `master`): startet und zeichnet mit SoftGPU.

**Fertig, wenn** (Plan): gleiches Bild wie SoftGPU (Referenzvergleich), messbar höhere Bildrate, Fehlerfälle beenden nur die App, QEMU- und VMware-Ergebnis dokumentiert; dazu Hosttests mit `-warnaserror`, nativer Normal- und Recovery-Build.

**Nachtrag Beschleunigung (17. November 2026, `task/raster-speed`):** Der Kern bestimmt bei exakt darstellbaren Kantenwerten (alle Ecken des Rechtecks unter 2^23) die abgedeckten Pixel einer Zeile ganzzahlig statt sie abzutasten, liest Puffer nach einer Prüfung je Aufruf ohne Bereichsprüfung je Pixel und rechnet im 3D-Pfad vier Pixel je Schritt mit SSE2 (`Vector128`, `cvttps2dq`, keine Ganzzahl-Vektormultiplikation). Bitgleichheit sichert `WasmRasterizerEquivalenceTests` gegen eine eingefrorene Kopie der alten Fassung (Spielszene und 1500 Zufallsszenen); einzige Ausnahme ist das Vorzeichen von NaN-Tiefen, das schon zwischen Übersetzungsstufen der alten Fassung schwankt und keinen Vergleich beeinflusst. Liegen Farb- und Tiefenpuffer überlappend im Gastspeicher, bleibt der Kern skalar. Gastmessungen unter WHPX schwankten stark mit der Last des Entwicklungsrechners und dem laufenden Mesh-Aufbau (Werte im Bericht des Branches).

## 8 Abstimmung

- **Etappe 1** (agent-laser-purple, `feature/wasm-frametimes`): ändert `ProjektZeh/Runtime/Wasm/` außer der Importtabelle. Meine Änderungen dort: nur `zehos_wasm.c`, heute `zehos_host_core.c`/`.h` (Tabelle, Brücke, Umgebung) und eine neue Rückruffunktion in `WasmRuntime.cs`. Vor dem Umbau im Kanal abgleichen.
- **Etappe 5** (AOT, agent-laser-pink-aot, Übergabe `docs/aot-handoff`): Die ABI ist laufzeitunabhängig (Gastadressen und Längen). Eine AOT-Laufzeit braucht nur eine zweite Bindung derselben Brücke.
- Lizenz: Der übertragene Algorithmus stammt aus ClassiCube (BSD-3, UnknownShadow200); Hinweis in der Quelldatei und in den Lizenzangaben des Kernels.

## 9 Entscheidungen

Peter hat am 17. November 2026 allen drei Punkten zugestimmt („Ja, so umsetzen“): C#-Kern mit Rückfall auf C nach Messung, Erkennung über `ZEHOS_FEATURES`, Zeichnen auf Gastspeicher ohne Handles und Befehlspuffer.

Ursprüngliche Fragen:

1. Ort der Umsetzung: B (C#, testbar) mit Rückfall auf A nach Messung — oder gleich A?
2. Feature-Erkennung über `ZEHOS_FEATURES` (neue allgemeine Zusage an App-Entwickler) — einverstanden?
3. Abweichung vom Plan: keine Handles und kein Befehlspuffer, sondern Zeichnen auf Gastspeicher je Aufruf (Begründung 3.1).
