# ClassiCube als Paket-App: Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Jede Etappe ist ein eigener Branch und PR; vor jeder Etappe im IRC ankündigen.

**Goal:** Ein Minecraft-artiges Voxelspiel auf ZehOS: ClassiCube (Klon von Minecraft Classic) als signiertes Wasm-Paket, spielbar, mit Speichern und optional Mehrspieler.
**Architecture:** ClassiCube läuft wie Doom und NetSurf als WASIp1-Paket unter wasm3. Fehlende Fähigkeiten kommen als allgemeine `zehos_v1`-Hostfunktionen in den Loader, nie als appbezogener Kernelcode. Beschleunigt wird erst nach Messung: Anzeigeweg, Rasterizer als Hostfunktion oder AOT-Laufzeit, je nach Befund.
**Tech Stack:** C (ClassiCube, wasi-sdk), C#/.NET 10, Cosmos Gen3, wasm3, MSTest, QEMU, VMware.
**Herkunft:** Analyse mit Peter am 16. November 2026 (Minecraft-Editionen, Grafiktreiber, Laufzeit, Multicore). Dieser Plan ersetzt keine Roadmap-Etappe; ein Eintrag in `ROADMAP.md` folgt mit Etappe 2.

## Voraussetzung Cosmos-Version

`master` verwendet derzeit Cosmos 3.0.94. Das Update auf 3.0.97 läuft getrennt auf `feature/cosmos-3.0.97` (Übergabedokument `docs/superpowers/plans/2026-11-16-cosmos-update-handoff.md` auf Branch `docs/cosmos-update-handoff`). Etappen mit Kernelanteil (1, 3, 4, 5) beginnen erst nach dessen Merge; Etappe 2 kann vorher beginnen, da sie keinen Kernelcode ändert. Bis zum Merge gelten die `finally`-Regeln aus `AGENTS.md`. Alle Aussagen zu Cosmos in diesem Plan beziehen sich auf 3.0.94 und sind nach dem Update erneut zu prüfen.

## Entscheidungen und Begründung

- **ClassiCube statt echtem Minecraft:** Pocket Edition, Bedrock und Java sind proprietär, nicht weiterverteilbar und bräuchten ELF-Lader, Android-/JVM-Nachbildung, GLES mit Shadercompiler, Threads und GB-weise RAM. ClassiCube ist reines C, steht unter einer BSD-Lizenz (`license.txt`, „Copyright (c) 2014 - 2024, UnknownShadow200“; vor der Übernahme vollständig prüfen), bringt Software-Renderer mit (`src/Graphics_SoftMin.c`, `src/Graphics_SoftGPU.c`), hat einen Web-Client (`src/webclient`; ob er ohne Threads läuft, ist nicht im Code belegt) und spricht im Mehrspieler das Classic-Protokoll über TCP.
- **Kein GPU-Treiber:** Cosmos 3.0.94 hat nur virtio-gpu 2D. virgl (nur QEMU) oder SVGA3D (nur VMware) kosten jeweils Monate und sind für Grafik auf Classic-Niveau nicht nötig.
- **Kein Multicore:** Cosmos-SMP ist offen (CosmosOS/Cosmos#3336, nur Phase 0 umgesetzt). Ein Wasm-Thread würde dadurch ohnehin nicht schneller.
- **Messen vor Investieren:** Doom braucht unter wasm3 auf dem Entwicklungsrechner 2,3–3,2 ms Rechenzeit je Bild (`docs/WASM_FEASIBILITY.md`, Titel/Demo). Die 12–13 ms Median unter KVM sind dagegen der Abstand zwischen zwei `present_indexed` einschließlich Warten und Taktung, bei rund 61 Bildern/s. Wo die Bildzeit tatsächlich hingeht, ist offen und wird in Etappe 1 gemessen.

## Global Constraints

- `AGENTS.md` vollständig: neue Apps sind Pakete; keine appbezogenen Buildflags; Hostfunktionen prüfen jeden Gastzeiger und jede Länge überlaufsicher; keine Ausnahmen über `UnmanagedCallersOnly`; Apps erhalten nur Zahlen, Kopien und instanzgebundene Handles.
- Neue Hostfunktionen, Imports und Limits in `docs/PAKETE_ERSTELLEN.md` und `docs/PACKAGE_FORMAT.md` nachtragen.
- Lizenztexte ins Paket. Keine Mojang-Grafiken oder -Daten: nur Texturen mit belegter freier Lizenz. Prüfen, ob ClassiCube beim Start Ressourcen von Mojang oder eigenen Servern nachlädt, und das abschalten.
- Keine Abhängigkeits- oder Werkzeugupdates ohne Rückfrage (wasi-sdk-Version wie bei Doom/NetSurf verwenden).
- Veröffentlichung im Branch `packages` nur per PR; Merge nur auf ausdrücklichen Auftrag.
- **Jeder Etappen-PR:** ZehOS-Version in `ProjektZeh/ProjektZeh.csproj` gegenüber dem Zielbranch erhöhen (Nummer erst beim PR, Abstimmung im IRC), unabhängiges Review vor dem PR, Reviewer nach `AGENTS.md`.
- **Jede Etappe mit Kernelanteil:** Hosttests mit `-warnaserror`, nativer Normal- und Recovery-Build, betroffene Abläufe in QEMU **und VMware** prüfen; nicht ausführbare Prüfungen ausdrücklich als offen nennen.

## Etappe 1: Messen und Anzeigeweg (1–2 Wochen)

Ziel: wissen, wo die Bildzeit einer Paket-App hingeht, und belegte Anzeige-Engpässe beseitigen. Nützt allen Apps.

- [ ] Vorhandene Messung wiederverwenden: `WasmFrameTimes` (`ProjektZeh/Runtime/Wasm/WasmFrameTimes.cs`) misst in `WasmGameApp` bisher nur für die Paket-ID `doom`. Für alle Paket-Apps öffnen und um Anteile erweitern: Wasm-Ausführung bis `present`, Pixelumrechnung/Skalierung (`WasmImageFit`), Desktop-Zusammensetzung, `Canvas.Display()`, Warten. Zuschalten zur Laufzeit (etwa per Shellbefehl), kein appbezogenes Buildflag; ohne Zuschaltung keine Ausgabe und keine Allokation je Bild.
- [ ] Doom unter KVM und TCG je 60 s messen; Werte mit Datum in `docs/WASM_FEASIBILITY.md`.
- [ ] Prüfen, wie Cosmos den Limine-Bildpuffer einblendet (Caching/Write-Combining, PAT). Ergebnis in `docs/COSMOS_SUPPORT.md`.
- [ ] Nur belegte Engpässe beheben, Kandidaten: geänderte Bereiche statt Vollbild übertragen, Skalierung nur bei Größenwechsel neu berechnen, unnötige Vollbildkopien entfernen. Vorher/Nachher-Messung.
- [ ] Hosttests für geänderte Logik (Bereichsberechnung, Skalierung, Messung); Prüfungen nach den Global Constraints. QEMU und VMware: Desktop, Doom, NetSurf, Fensterwechsel, F4/Esc.

**Fertig, wenn:** Für Doom ist die Bildzeit in ihre Anteile zerlegt dokumentiert, und belegte Anzeige-Engpässe sind behoben oder als nicht behebbar begründet.

## Etappe 2: ClassiCube-Port, erster spielbarer Stand (3–4 Wochen)

Ziel: Einzelspieler im Fenster, Welt nur im Speicher, ohne Kernelerweiterung.

- [ ] Quellstand festlegen (Commit von `ClassiCube/ClassiCube`), Lizenz vollständig prüfen und dokumentieren.
- [ ] Buildweg unter `experiments/ClassiCube.Wasi/` nach dem Muster von `experiments/NetSurf.Wasi/` und `experiments/Wasm.DoomProbe/`: wasi-sdk, eigene Patches statt Fork, reproduzierbarer Build mit Hash.
- [ ] Plattformschicht: Fenster, Bild und Eingabe über `zehos_v1` (`poll_event`, Zeigerfang mit relativer Maus Typ 9 wie bei Doom, Fenstergröße Typ 10), Zeit und Zufall über WASI, `Audio_Null`, kein HTTP-Worker. Grafik zuerst über `SoftMin`.
- [ ] Ohne Threads bauen (Web-Client-Pfad als Vorlage). Falls ClassiCube zwingend Threads braucht: Befund dokumentieren und vor dem Weiterbauen mit Peter abstimmen.
- [ ] Schreibzugriffe (Optionen, Welt, Texturcache) auf Speicher umleiten oder abschalten; Pakete sind nur lesbar.
- [ ] Freies Texturpaket wählen, Lizenz belegen, ins Paket legen.
- [ ] Limits einhalten: Modul höchstens 64 MiB, höchstens 256 MiB Speicher, Paket höchstens 128 MiB, 1 MiB Wasm-Stack. Speichermangel und Traps beenden nur die App mit sichtbarer Ursache.
- [ ] Paket nach `docs/PACKAGE_FORMAT.md`: Paket-ID festlegen, Manifest ohne `capability=net` (erst Etappe 6), Lizenz- und Quellenangaben im Paket, Menüeintrag. Lokal mit Testschlüssel bauen und installieren (nie der Herausgeberschlüssel).
- [ ] Gastprüfung als Skript (Muster `scripts/test-netsurf-desktop.py`): Start aus dem Menü, Welt erzeugt, Bewegung, Blick per Maus, Block setzen/entfernen, Beenden über Fenster, drei Starts mit Schließen und jeweils freiem Speicher (wie beim Doom-Langlauf).
- [ ] **Messung** mit der Messung aus Etappe 1 (falls schon vorhanden) oder ClassiCube-eigenen Zeiten: Bilder/s bei fester Fenstergröße, Dauer der Weltgenerierung, Anteil Rasterisierung vs. Spiellogik. Ergebnis mit Datum dokumentieren.
- [ ] `ROADMAP.md` um einen Abschnitt für das Spiel ergänzen (Ziel, Abnahme, Stand).
- [ ] Veröffentlichung: neue Paketversion und höhere Katalog-Generation per PR in `packages`, Merge nur auf ausdrücklichen Auftrag.

**Fertig, wenn:** ClassiCube startet als Paket, eine Welt ist spielbar, das Spiel endet ohne Neustart von ZehOS, der Speicher wird nach dem Beenden freigegeben, und die Messwerte liegen vor.

## Entscheidungspunkt nach Etappe 2

Anhand der Messung entscheiden, mit Peter abstimmen:

| Befund | Nächster Schritt |
| --- | --- |
| Rasterisierung dominiert die Bildzeit | Etappe 3 (Rasterizer-Hostfunktion) |
| Spiellogik, Chunk-Geometrie oder Weltgenerierung dominieren | Etappe 5 (AOT-Laufzeit) vor Etappe 3 |
| Bildrate bereits ausreichend | direkt Etappe 4 |

Erwartung (nicht gemessen): Die Rasterisierung bremst die Bildrate, die Spiellogik die Ladezeiten.

## Etappe 3: Software-Rasterizer als Hostfunktion (6–10 Wochen)

Ziel: Das Rasterisieren läuft nativ im Kernel statt im Interpreter, auf QEMU, VMware und echter Hardware.

- [ ] Funktionsumfang aus dem tatsächlichen Bedarf von ClassiCubes Grafikschicht ableiten: texturierte Dreiecke/Quads, Tiefenpuffer, Alphatest/-mischung, Nebel, Matrizen, Clipping, Texturen als Handles. Nicht mehr.
- [ ] ABI entwerfen: ein Befehlspuffer je Bild in den Gastspeicher, Texturen hochladen per Kopie, Ziel ist das eigene Fenster. Jeder Zeiger, jede Länge, jeder Index und jede Texturgröße wird überlaufsicher geprüft; Obergrenzen für Dreiecke je Bild, Texturspeicher und Handles. Ältere Kernel ohne diese Funktion müssen für die App erkennbar sein (Rückfall auf `SoftMin`). Kurzer Entwurf unter `docs/superpowers/specs/` vor der Umsetzung.
- [ ] Vorab-Probe: SIMD (`Vector128`/SSE) unter Cosmos/NativeAOT im Gast nachweisen; ohne SIMD skalare Variante.
- [ ] Rasterizer in C# (oder C über `GCCProject`, dann mit Begründung), plattformunabhängiger Kern mit Hosttests (Pixelvergleich gegen Referenzbilder, Grenz- und Fehlerfälle, ungültige Befehle).
- [ ] Hostfunktion im Loader, Handles an die App-Instanz gebunden und beim Beenden freigegeben, auch bei Traps.
- [ ] ClassiCube-Grafik-Backend `Graphics_ZehOS.c` auf die neue Hostfunktion; `SoftMin` bleibt als Rückfall.
- [ ] `docs/PAKETE_ERSTELLEN.md`, `docs/PACKAGE_FORMAT.md` ergänzen; Vorher/Nachher-Messung.
- [ ] Prüfungen nach den Global Constraints, ausdrücklich ClassiCube und Doom in QEMU und VMware; echte Hardware nur nennen, wenn dort getestet.

**Fertig, wenn:** Gleiches Bild wie mit `SoftMin` (Referenzvergleich), messbar höhere Bildrate, Fehlerfälle beenden nur die App, QEMU- und VMware-Ergebnis dokumentiert.

## Etappe 4: App-Speicher, Welten speichern (3–5 Wochen)

Ziel: Apps können begrenzt und nur im eigenen Bereich dauerhaft schreiben. Nützt allen Apps (z. B. dauerhafter JS-Schalter in NetSurf).

- [ ] Entwurf: Speicherort je Paket-ID auf `/data`, Größen- und Dateizahl-Grenzen, Verhalten ohne Datenlaufwerk (VMware-VM hat keins), Löschen mit dem Paket, Rechte nach Roadmap-Etappe 6c (Benutzer- und Dateirechte).
- [ ] Umsetzung über WASI-Schreibaufrufe nur in einem freigegebenen Verzeichnis oder als eigene `zehos_v1`-Funktionen; Entscheidung im Entwurf begründen.
- [ ] Bestehende Sicherungs-/Ersetzungslogik verwenden; Tests mit isolierter Imagekopie (Schreiben, voller Datenträger, Abbruch, Neustart).
- [ ] ClassiCube speichert Welt und Optionen; Vertragsdoku nachtragen.
- [ ] Prüfungen nach den Global Constraints; VMware ohne Datenlaufwerk: App bleibt bedienbar und meldet fehlenden Speicher.

**Fertig, wenn:** Eine Welt übersteht Beenden und Neustart von ZehOS; Fehler (voll, kein Laufwerk) bleiben bedienbar.

## Etappe 5: AOT-Laufzeit (1,5–3 Monate, nur nach Messung)

Ziel: Rechenlastige Apps laufen nahezu nativ.

- [ ] Kandidat WAMR (Apache-2.0) mit `wamrc`-AOT prüfen: Lizenz, Plattformschicht für Cosmos (Speicher für ausführbaren Code, Bereichsprüfungen ohne Schutzseiten, `setjmp`/`longjmp`), NativeAOT-Verträglichkeit. Erfahrungen aus der Firefox-Probe (#54) einbeziehen.
- [ ] Sicherheitsbewertung im Bezug zu Roadmap 12.4 (Speicher- und Prozessisolation): Nativer AOT-Code im Ring 0 kann im Kernel nicht mehr validiert werden und hebt die heutige Wasm-Grenze auf; Vertrauen liegt bei Signatur und Compiler. Mit Peter abstimmen, bevor umgesetzt wird.
- [ ] Hostfunktionen aus `ProjektZeh/Native/Wasm/host/zehos_wasm.c` auf die neue Laufzeit übertragen, wasm3 als Rückfall erhalten; Paketformat um `.aot` je Laufzeitversion erweitern. Ältere Kernel lehnen `.aot`-Pakete sauber ab oder nutzen das enthaltene `.wasm`.
- [ ] Vergleich wasm3 vs. AOT für Doom, NetSurf und ClassiCube; Prüfungen nach den Global Constraints.

**Fertig, wenn:** Messbarer Gewinn, alle bestehenden Pakete laufen weiter, Sicherheitsgrenze dokumentiert.

## Etappe 6: Mehrspieler (1–2 Wochen)

- [ ] Classic-Protokoll über die vorhandene Netz-ABI (`capability=net`, höchstens vier Verbindungen, 64 KiB Puffer) anbinden; neue Paketversion mit Netzberechtigung.
- [ ] Gegen einen lokalen Testserver prüfen: Verbinden, Bewegung, Blockänderungen, Abbruch und Serverausfall ohne Einfrieren des Desktops.

## Risiken

- ClassiCube braucht Threads oder schreibt beim Start zwingend Dateien → Patches oder Etappe 4 vorziehen.
- Bildrate unter wasm3 auch mit Rasterizer zu niedrig → Etappe 5 vorziehen.
- Texturlizenz ungeklärt → kein Paket veröffentlichen.
- Rasterizer-ABI zu eng auf ClassiCube zugeschnitten → Entwurf so halten, dass andere 3D-Apps sie nutzen können, ohne Funktionen auf Vorrat.
- Cosmos-Update verschiebt Grenzen (Zeitgeber, Speicher, Treiber) → Aussagen dieses Plans nach dem Merge von `feature/cosmos-3.0.97` prüfen.

## Zeitrahmen (Schätzung, ein Agent in Vollzeit)

| Meilenstein | ungefähr |
| --- | --- |
| Etappen 1–2: erster spielbarer Stand | 1–1,5 Monate |
| Etappen 3–4: flüssig mit Speichern | weitere 2–3 Monate |
| Etappen 5–6: AOT und Mehrspieler | insgesamt 4–7 Monate |

## Ledger

- 16.11.2026: Plan erstellt (agent-peter-classicube-plan). Grundlage: gelesene Quellen und Paketinhalte, nichts gebaut oder gemessen.
- 16.11.2026: Unabhängiges Review ohne P1; eingearbeitet: Messgrößen Doom getrennt (Rechenzeit vs. Bildabstand), Wiederverwendung von `WasmFrameTimes`, Cosmos-Voraussetzung geklärt, VMware/Recovery/Version/Review je Etappe, Paketkatalog und Manifest, Speicherfreigabe nach Starts, Roadmap 12.4 und 6c benannt, Rückfall für ältere Kernel.
