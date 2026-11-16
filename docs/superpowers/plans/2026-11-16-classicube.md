# ClassiCube als Paket-App: Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Jede Etappe ist ein eigener Branch und PR; vor jeder Etappe im IRC ankündigen.

**Goal:** Ein Minecraft-artiges Voxelspiel auf ZehOS: ClassiCube (Klon von Minecraft Classic) als signiertes Wasm-Paket, spielbar, mit Speichern und optional Mehrspieler.
**Architecture:** ClassiCube läuft wie Doom und NetSurf als WASIp1-Paket unter wasm3. Fehlende Fähigkeiten kommen als allgemeine `zehos_v1`-Hostfunktionen in den Loader, nie als appbezogener Kernelcode. Beschleunigt wird erst nach Messung: zuerst der Anzeigeweg, dann ein Rasterizer als Hostfunktion, zuletzt gegebenenfalls eine AOT-Laufzeit.
**Tech Stack:** C (ClassiCube, wasi-sdk), C#/.NET 10, Cosmos 3.0.97 (nach `feature/cosmos-3.0.97`), wasm3, MSTest, QEMU.
**Herkunft:** Analyse mit Peter am 16. November 2026 (Minecraft-Editionen, Grafiktreiber, Laufzeit, Multicore). Dieser Plan ersetzt keine Roadmap-Etappe; ein Eintrag in `ROADMAP.md` folgt erst mit Etappe 2.

## Entscheidungen und Begründung

- **ClassiCube statt echtem Minecraft:** Pocket Edition, Bedrock und Java sind proprietär, nicht weiterverteilbar und bräuchten ELF-Lader, Android-/JVM-Nachbildung, GLES mit Shadercompiler, Threads und GB-weise RAM. ClassiCube ist reines C, steht unter einer BSD-artigen Lizenz (`license.txt`, „Copyright (c) 2014 - 2024, UnknownShadow200“; vor der Übernahme vollständig prüfen), bringt Software-Renderer mit (`src/Graphics_SoftMin.c`, `src/Graphics_SoftGPU.c`), hat einen Web-Client (Betrieb ohne Threads vorgesehen) und spricht im Mehrspieler das Classic-Protokoll über TCP.
- **Kein GPU-Treiber:** Cosmos 3.0.94 hat nur virtio-gpu 2D. virgl (nur QEMU) oder SVGA3D (nur VMware) kosten jeweils Monate und sind für Grafik auf Classic-Niveau nicht nötig.
- **Kein Multicore:** Cosmos-SMP ist offen (CosmosOS/Cosmos#3336, nur Phase 0 umgesetzt). Ein Wasm-Thread würde dadurch ohnehin nicht schneller.
- **Messen vor Investieren:** Doom braucht unter wasm3 auf dem Entwicklungsrechner 2,3–3,2 ms je Bild, im KVM-Gast aber 12–13 ms Median (`docs/WASM_FEASIBILITY.md`). Der größere Teil liegt vermutlich im Anzeigeweg, nicht im Interpreter (abgeleitet, nicht gemessen).

## Global Constraints

- `AGENTS.md` vollständig: neue Apps sind Pakete; keine appbezogenen Buildflags; Hostfunktionen prüfen jeden Gastzeiger und jede Länge überlaufsicher; keine Ausnahmen über `UnmanagedCallersOnly`; Apps erhalten nur Zahlen, Kopien und instanzgebundene Handles.
- Neue Hostfunktionen, Imports und Limits in `docs/PAKETE_ERSTELLEN.md` und `docs/PACKAGE_FORMAT.md` nachtragen.
- Lizenztexte ins Paket. Keine Mojang-Grafiken oder -Daten: nur Texturen mit belegter freier Lizenz. Prüfen, ob ClassiCube beim Start Ressourcen von Mojang oder eigenen Servern nachlädt, und das abschalten.
- Keine Abhängigkeits- oder Werkzeugupdates ohne Rückfrage (wasi-sdk-Version wie bei Doom/NetSurf verwenden).
- Veröffentlichung im Branch `packages` nur per PR; Merge nur auf ausdrücklichen Auftrag.
- Basis ist `master` mit Cosmos 3.0.97. Bis zu dessen Merge gelten die `finally`-Regeln aus `AGENTS.md`.

## Etappe 1: Messen und Anzeigeweg (1–2 Wochen)

Ziel: wissen, wo die Bildzeit einer Paket-App hingeht, und die Anzeige als Engpass beseitigen. Nützt allen Apps.

- [ ] Zeitmessung je Bild einbauen (abschaltbar, keine Ausgabe im Normalbetrieb): Wasm-Ausführung bis `present`, Pixelumrechnung/Skalierung (`WasmImageFit`), Desktop-Zusammensetzung, `Canvas.Display()`. Doom unter KVM und TCG je 60 s messen; Werte mit Datum in `docs/WASM_FEASIBILITY.md`.
- [ ] Prüfen, wie Cosmos den Limine-Bildpuffer einblendet (Caching/Write-Combining, PAT). Ergebnis in `docs/COSMOS_SUPPORT.md`.
- [ ] Nur belegte Engpässe beheben, Kandidaten: geänderte Bereiche statt Vollbild übertragen, Skalierung nur bei Größenwechsel neu berechnen, unnötige Vollbildkopien entfernen. Vorher/Nachher-Messung.
- [ ] Hosttests für geänderte Logik (Bereichsberechnung, Skalierung); nativer Normal- und Recovery-Build; QEMU: Desktop, Doom, NetSurf, Fensterwechsel, F4/Esc.

**Fertig, wenn:** Für Doom ist die Bildzeit in ihre Anteile zerlegt dokumentiert, und belegte Anzeige-Engpässe sind behoben oder als nicht behebbar begründet.

## Etappe 2: ClassiCube-Port, erster spielbarer Stand (3–4 Wochen)

Ziel: Einzelspieler im Fenster, Welt nur im Speicher, ohne Kernelerweiterung.

- [ ] Quellstand festlegen (Commit von `ClassiCube/ClassiCube`), Lizenz vollständig prüfen und dokumentieren.
- [ ] Buildweg unter `experiments/ClassiCube.Wasi/` nach dem Muster von `experiments/NetSurf.Wasi/` und `experiments/Wasm.DoomProbe/`: wasi-sdk, eigene Patches statt Fork, reproduzierbarer Build mit Hash.
- [ ] Plattformschicht: Fenster, Bild und Eingabe über `zehos_v1` (`poll_event`, Zeigerfang mit relativer Maus wie bei Doom, Fenstergröße Typ 10), Zeit und Zufall über WASI, `Audio_Null`, kein HTTP-Worker. Grafik zuerst über `SoftMin`.
- [ ] Ohne Threads bauen (Web-Client-Pfad als Vorlage). Falls ClassiCube zwingend Threads braucht: Befund dokumentieren und vor dem Weiterbauen mit Peter abstimmen.
- [ ] Schreibzugriffe (Optionen, Welt, Texturcache) auf Speicher umleiten oder abschalten; Pakete sind nur lesbar.
- [ ] Freies Texturpaket wählen, Lizenz belegen, ins Paket legen.
- [ ] Limits einhalten: Modul höchstens 64 MiB, höchstens 256 MiB Speicher, Paket höchstens 128 MiB, 1 MiB Wasm-Stack.
- [ ] Gastprüfung als Skript (Muster `scripts/test-netsurf-desktop.py`): Start aus dem Menü, Welt erzeugt, Bewegung, Blick per Maus, Block setzen/entfernen, Beenden über Fenster, drei Starts mit Schließen.
- [ ] **Messung:** Bilder/s bei fester Fenstergröße, Dauer der Weltgenerierung, Anteil Rasterisierung vs. Spiellogik (über Etappe-1-Messung und ClassiCube-eigene Zeiten). Ergebnis mit Datum dokumentieren.
- [ ] Paket bauen und lokal installieren (Testschlüssel, nie der Herausgeberschlüssel). Veröffentlichung erst nach Freigabe.
- [ ] `ROADMAP.md` um einen Abschnitt für das Spiel ergänzen (Ziel, Abnahme, Stand).

**Fertig, wenn:** ClassiCube startet als Paket, eine Welt ist spielbar, das Spiel endet ohne Neustart von ZehOS, und die Messwerte liegen vor.

## Entscheidungspunkt nach Etappe 2

Anhand der Messung entscheiden, mit Peter abstimmen:

| Befund | Nächster Schritt |
| --- | --- |
| Rasterisierung dominiert die Bildzeit | Etappe 3 (Rasterizer-Hostfunktion) |
| Spiellogik, Chunk-Geometrie oder Weltgenerierung dominieren | Etappe 5 (AOT-Laufzeit) vor Etappe 3 |
| Bildrate bereits ausreichend | direkt Etappe 4 |

Erwartung (nicht gemessen): Die Rasterisierung bremst die Bildrate, die Spiellogik die Ladezeiten.

## Etappe 3: Software-Rasterizer als Hostfunktion (6–10 Wochen)

Ziel: Das Rasterisieren läuft nativ im Kernel statt im Interpreter; funktioniert auf QEMU, VMware und echter Hardware.

- [ ] Funktionsumfang aus dem tatsächlichen Bedarf von ClassiCubes Grafikschicht ableiten: texturierte Dreiecke/Quads, Tiefenpuffer, Alphatest/-mischung, Nebel, Matrizen, Clipping, Texturen als Handles. Nicht mehr.
- [ ] ABI entwerfen: ein Befehlspuffer je Bild in den Gastspeicher, Texturen hochladen per Kopie, Ziel ist das eigene Fenster. Jeder Zeiger, jede Länge, jeder Index und jede Texturgröße wird überlaufsicher geprüft; Obergrenzen für Dreiecke je Bild, Texturspeicher und Handles. Kurzer Entwurf unter `docs/superpowers/specs/` vor der Umsetzung.
- [ ] Vorab-Probe: SIMD (`Vector128`/SSE) unter Cosmos/NativeAOT im Gast nachweisen; ohne SIMD skalare Variante.
- [ ] Rasterizer in C# (oder C über `GCCProject`, dann mit Begründung), plattformunabhängiger Kern mit Hosttests (Pixelvergleich gegen Referenzbilder, Grenz- und Fehlerfälle, ungültige Befehle).
- [ ] Hostfunktion im Loader, Handles an die App-Instanz gebunden und beim Beenden freigegeben, auch bei Traps.
- [ ] ClassiCube-Grafik-Backend `Graphics_ZehOS.c` auf die neue Hostfunktion; `SoftMin` bleibt als Rückfall.
- [ ] `docs/PAKETE_ERSTELLEN.md`, `docs/PACKAGE_FORMAT.md` ergänzen; Vorher/Nachher-Messung.

**Fertig, wenn:** Gleiches Bild wie mit `SoftMin` (Referenzvergleich), messbar höhere Bildrate, Fehlerfälle beenden nur die App.

## Etappe 4: App-Speicher, Welten speichern (3–5 Wochen)

Ziel: Apps können begrenzt und nur im eigenen Bereich dauerhaft schreiben. Nützt allen Apps (z. B. dauerhafter JS-Schalter in NetSurf).

- [ ] Entwurf: Speicherort je Paket-ID auf `/data`, Größen- und Dateizahl-Grenzen, Verhalten ohne Datenlaufwerk (VMware-VM hat keins), Löschen mit dem Paket, Rechte (Etappe 6c).
- [ ] Umsetzung über WASI-Schreibaufrufe nur in einem freigegebenen Verzeichnis oder als eigene `zehos_v1`-Funktionen; Entscheidung im Entwurf begründen.
- [ ] Bestehende Sicherungs-/Ersetzungslogik verwenden; Tests mit isolierter Imagekopie (Schreiben, voller Datenträger, Abbruch, Neustart).
- [ ] ClassiCube speichert Welt und Optionen; Vertragsdoku nachtragen.

**Fertig, wenn:** Eine Welt übersteht Beenden und Neustart von ZehOS; Fehler (voll, kein Laufwerk) bleiben bedienbar.

## Etappe 5: AOT-Laufzeit (1,5–3 Monate, nur nach Messung)

Ziel: Rechenlastige Apps laufen nahezu nativ.

- [ ] Kandidat WAMR (Apache-2.0) mit `wamrc`-AOT prüfen: Lizenz, Plattformschicht für Cosmos (Speicher für ausführbaren Code, Bereichsprüfungen ohne Schutzseiten, `setjmp`/`longjmp`), NativeAOT-Verträglichkeit. Erfahrungen aus der Firefox-Probe (#54) einbeziehen.
- [ ] Sicherheitsbewertung: Nativer AOT-Code kann im Kernel nicht mehr validiert werden; Vertrauen liegt bei Signatur und Compiler. Mit Peter abstimmen, bevor umgesetzt wird.
- [ ] Hostfunktionen aus `ProjektZeh/Native/Wasm/host/zehos_wasm.c` auf die neue Laufzeit übertragen, wasm3 als Rückfall erhalten; Paketformat um `.aot` je Laufzeitversion erweitern.
- [ ] Vergleich wasm3 vs. AOT für Doom, NetSurf und ClassiCube.

**Fertig, wenn:** Messbarer Gewinn, alle bestehenden Pakete laufen weiter, Sicherheitsgrenze dokumentiert.

## Etappe 6: Mehrspieler (1–2 Wochen)

- [ ] Classic-Protokoll über die vorhandene Netz-ABI (`capability=net`, höchstens vier Verbindungen, 64 KiB Puffer) anbinden.
- [ ] Gegen einen lokalen Testserver prüfen: Verbinden, Bewegung, Blockänderungen, Abbruch und Serverausfall ohne Einfrieren des Desktops.

## Risiken

- ClassiCube braucht Threads oder schreibt beim Start zwingend Dateien → Patches oder Etappe 4 vorziehen.
- Bildrate unter wasm3 auch mit Rasterizer zu niedrig → Etappe 5 vorziehen.
- Texturlizenz ungeklärt → kein Paket veröffentlichen.
- Rasterizer-ABI zu eng auf ClassiCube zugeschnitten → Entwurf so halten, dass andere 3D-Apps sie nutzen können, ohne Funktionen auf Vorrat.
- VMware und echte Hardware sind für jede Etappe getrennt zu prüfen; fehlende Prüfungen offen benennen.

## Zeitrahmen (Schätzung, ein Agent in Vollzeit)

| Meilenstein | ungefähr |
| --- | --- |
| Etappen 1–2: erster spielbarer Stand | 1–1,5 Monate |
| Etappen 3–4: flüssig mit Speichern | weitere 2–3 Monate |
| Etappen 5–6: AOT und Mehrspieler | insgesamt 4–7 Monate |

## Ledger

- 16.11.2026: Plan erstellt (agent-peter-classicube-plan). Grundlage: gelesene Quellen und Paketinhalte, nichts gebaut oder gemessen. Cosmos-Update auf 3.0.97 läuft getrennt (`feature/cosmos-3.0.97`, Übergabe `2026-11-16-cosmos-update-handoff.md`).
