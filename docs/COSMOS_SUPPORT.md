# Geprüfte Cosmos-Anbindung: Eingabe, Grafik, Desktop und Threads

Stand: 12. November 2026. Diese Datei dokumentiert die Prüfungen für Roadmap-Etappen 3 bis 6, keine vollständige Erledigung der Grundlagenetappe 0. Die Diagnoseetappe 2 wurde aus `master` (`d81d24a`) integriert; der Desktop baut auf `f0dc4b1` einschließlich Eingabe/Grafik und QEMU-Energiekorrektur auf. Protokoll, `uptime` und interaktive Anzeige verwenden dieselbe monotone Uhr.

## Aufgabenverwaltung und Round-Robin am 13. November 2026

Die anschließende Umsetzung von 6a.2–6a.5 ersetzt Stride beim normalen und Recovery-Start durch `CosmosRoundRobinScheduler`. Cosmos 3.0.94 übernimmt unverändert Threadregistrierung, Kontextwechsel, Timer und Warteprimitive. Die Policy verwendet eine feste **20-ms-Zeitscheibe** (zwei konfigurierte 10-ms-Ticks) und eine vorab angelegte FIFO in Größe der Cosmos-Threadgrenze. Thread-Daten entstehen beim Erzeugen, der Tickpfad allokiert und blockiert nicht. Blockieren/Beenden entfernt Queuereferenzen; Ready/Yield fügt ohne doppelte Einträge hinten ein. Das Diagnosering hält die letzten 128 Auswahlen als rohe Thread-IDs, die Shell formatiert sie erst im Vordergrund. Diagnosezugriffe schützen sich mit der vorhandenen Interruptmaske. SMP und Prioritätsunterschiede sind nicht Ziel dieser Ein-CPU-Policy.

`BackgroundTaskService` begrenzt verwaltete Aufgaben auf acht. Kennungen wachsen ohne Wiederverwendung, Zustände/Fortschritt/Fehler werden als Snapshot gelesen. Beendete Worker geben ihre Cancellation-Ressourcen frei; Referenzeinträge bleiben zur Diagnose und werden bei Platzbedarf entfernt. Gemeinsame Taskdaten sind synchronisiert, Fortschrittsübergaben atomar. Workers verwenden weder Anzeige, Eingabe, `SystemLog` noch Dateioperationen; dadurch bleiben deren bisherige Vordergrundzuständigkeiten erhalten. Vor Energieaktionen wird kooperativ abgebrochen und begrenzt gejoint, bei Restaktivität bleibt der Gast an.

**Nachweise:** 155/155 Hosttests mit Warnungen als Fehler, native Probe-/Normal-/Recovery-Builds jeweils null Warnungen/Fehler. Die vorhandene Verdrängungs-/Warteprobe meldet `Scheduler=ZehOS RoundRobin` und PASS in QEMU (27.603.069/26.043.825 Inkremente) und VMware (37.039.887/32.996.395). QEMU zeigt zwei wachsende Busy-Zähler und eine wartende Aufgabe; ein anderes Terminal nimmt währenddessen `echo alive` an. Alle drei lassen sich abbrechen; erneuter Start erzeugt die nächste Kennung und läuft auch nach Esc in der Textshell weiter. `shutdown` beendet anschließend auch diese aktive Aufgabe und QEMU sauber. Der bestehende Energiekommando-Test bestätigt Reboot in derselben VM und Shutdown mit Exitcode 0. Recovery startet textuell, Eingabe und F4/Esc-Desktopwechsel funktionieren. Das unabhängige Code-Review meldete keine konkreten Befunde. VMware ist seriell geprüft, echte Hardware und Dateipersistenz wurden für diese reine Thread-Erweiterung nicht neu geprüft.

### Historischer Ausgangsversuch 6a.1

Roadmap 6a.1 wurde auf Basis von `1d37a0d` mit unveränderten Cosmos-Paketen **3.0.94** geprüft. Der opt-in Build `-p:ZehOSThreadProbe=true` führt `Runtime/CosmosThreadProbe.cs` einmal vor der interaktiven Sitzung aus. Normale und Recovery-Builds ohne diese Option enthalten die Probe nicht. Die bestehenden Anwendungen und Dateioperationen bleiben im bisherigen Hauptthread.

| Schnittstelle | Tatsächlicher Nachweis |
| --- | --- |
| `Thread.Start`, `Interlocked`, `Volatile` | Zwei echte Arbeitsthreads erhöhen getrennte Zähler ohne Sleep/Yield. Beide Zähler wachsen in zwei aufeinanderfolgenden Messfenstern; der Hauptthread schläft dazwischen. Das belegt Timer-Verdrängung im verwendeten Gastprofil. |
| `Thread.Sleep`, `Thread.Join(timeout)` | Der Hauptthread wartet zeitgesteuert und läuft trotz beider Rechenaufgaben weiter. Nach gemeinsamem Stopflag enden beide Threads innerhalb des Join-Zeitlimits. |
| `ManualResetEvent`, `AutoResetEvent`, `WaitOne(timeout)` | Gemeinsames Startsignal; eigener Wartethread nachweislich in `WaitSleepJoin`, anschließend durch Signal aufgeweckt und gejoint. Ein vorher gesetztes Signal bleibt erhalten, ein nicht gesetztes Signal läuft in das Zeitlimit. |
| `Thread.Yield` | Aufruf im Gast erfolgreich; kein Fairnessnachweis allein aus dessen Rückgabewert abgeleitet. |
| `SchedulerManager.IsReady`, `Current`, `DefaultQuantumNs` | Im Gast: `Stride`, konfigurierter Tick **10.000.000 ns**. Der Tickwert ist die Cosmos-Konstante, keine Messung der realen Verdrängungslatenz. |
| `IScheduler`, `SchedulerThread`, `PerCpuState`, `SetScheduler` | Öffentliche Schnittstelle in den installierten Assemblies und offiziellen Quellen des Paketcommits `5d0b9535251472db7ecfe012f0435f0a04ffca16` vorhanden. `COSMOS0001` wird nur lokal in der Probe bestätigt. Ein Policywechsel/eigener Round-Robin-Scheduler wurde hier noch nicht ausgeführt. |

**QEMU:** `q35`, CPU `max`, 512 MiB RAM, Standard-VGA, kein Datenlaufwerk. Native Probe-, Normal- und Recovery-Builds erfolgreich mit Warnungen als Fehler und jeweils null Warnungen/Fehlern. Die Probe meldet alle Teilprüfungen und `THREAD_PROBE PASS`; im ersten Lauf erreichten die Worker 63.786.422 beziehungsweise 65.008.773 Inkremente. Die Zähler sind ein Fortschrittsnachweis, kein Leistungsvergleich. Der Testhelfer wurde zuerst gegen ein bisheriges normales ISO negativ geprüft: kein PASS und Fehler nach zehn Sekunden. Er beendet seine eigene Test-VM auch bei Fehlern. Die abschließenden 149 Hosttests bestehen. Normaler Boot, Eingabe in beiden Terminals, Textshell, F4/Esc mit erhaltenen Sitzungen sowie Recovery-Boot mit Shell-Eingabe und Desktopwechsel wurden zusätzlich geprüft. Normal- und Recovery-Logs enthalten keine Threadprobe.

**VMware Player:** Separater plattenloser UEFI-Gast mit einem vCPU und 1024 MiB RAM über das bestehende Startprofil. Derselbe Probe-Build meldete seriell alle Teilprüfungen und `THREAD_PROBE PASS` (47.392.219/44.298.557 Inkremente), danach `Desktop ist bereit`. Die Test-VM wurde anschließend beendet. Kein visueller Bediennachweis in VMware: `vmrun captureScreen` wurde mangels Gastanmeldung abgelehnt; eine Anmeldung oder VMware-Tools-Installation wurde nicht vorgenommen.

Die [offizielle Scheduler-Anleitung](https://cosmosos.github.io/articles/user/scheduler-plugging.html) beschreibt die experimentelle Policy-Schnittstelle. Der passende [Paketquellstand](https://github.com/CosmosOS/Cosmos/tree/5d0b9535251472db7ecfe012f0435f0a04ffca16) enthält bereits ein Round-Robin-Beispiel in den Threading-Tests. Cosmos stellt Kontextwechsel, Timer und Warteprimitive bereit; eine eigene Thread-Laufzeit ist für den nächsten Schritt nicht nötig. ZehOS-eigene Aufgabenverwaltung, Round-Robin-Installation, Absicherung gemeinsamer Dienste und bedienbarer Desktop während laufender Hintergrundaufgaben bleiben Gegenstand von 6a.2–6a.5. Echte Hardware wurde nicht geprüft.

## Referenzumgebung

Cosmos Gen3 **3.0.94**, .NET 10, x64, QEMU `q35`, CPU `max`, 512 MiB RAM, Standard-VGA und PS/2-Tastatur/-Maus. Limine fordert **1024×768×32** an; die tatsächliche Bildschirmgröße wurde im Gast geprüft. Der vorhandene QEMU-Start über Cosmos bleibt verwendbar. Für Dateiprüfungen wurde eine separate Kopie des vorhandenen 64-MiB-AHCI-Datenimages verwendet.

## Verwendete Schnittstellen

| Funktion | Anbindung und Ergebnis |
| --- | --- |
| Tastatur ohne blockierendes Lesen | `KeyboardManager.HandleScanCode` wird durch einen kleinen Plug in einen vorab angelegten Rohpuffer geleitet. Normales Tippen, Wiederholung und Loslassen sind im Gast geprüft. |
| Texteingabe | Das vorhandene `KeyboardLayout.ConvertScanCode` übersetzt im Vordergrund. Ein .NET-`UnsafeAccessor` bindet diese in 3.0.94 interne Methode an; das US-Layout bleibt erhalten. |
| Eigene Ereignisse | `InputService` trennt KeyDown, KeyUp und Text; Mausbewegung und Maustasten haben eigene Ereignisse. Zustände sind abfragbar. |
| Begrenzung | Je 256 Einträge für Rohdaten und eigene Ereignisse; pro Runde höchstens 64 rohe Tastenereignisse. Bei Überlauf werden wartende Ereignisse verworfen, verlorene Ereignisse gezählt und ein Reset löst gehaltene Tasten. |
| Interruptschutz | Das Lesen des Rohpuffers verwendet `SchedulerManager.MaskInterrupts()`. Nur für diesen kleinen Bereich wird die zugehörige Experimental-Warnung der festgelegten Version ausgenommen. |
| Maus | `MouseManager` liefert Position und drei Tastenzustände; `SetScreenSize` und `InputService` begrenzen die Position. Bewegung, Drücken/Loslassen und Bildschirmränder sind im Gast geprüft. |
| Monotone Zeit | `Stopwatch.GetTimestamp`/Frequenz sind in der installierten x64-Version an TSC und dessen Kalibrierung gebunden. Fortschreitende Statuszeit ohne Eingabe wurde im Gast geprüft. `Environment.TickCount64` wird nicht verwendet: Die installierte Laufzeit zählt dort Aufrufe statt Millisekunden. |
| Kurze Pause | `Thread.Sleep(1)` am Ende jeder Runde. `TimerManager.Wait(1)` nutzte im QEMU-Referenzgast einen 18-Hz-Timer und begrenzte dadurch Eingabe und Bildausgabe auf etwa 15–16 FPS. Die bereits geprüfte Scheduler-Wartefunktion beseitigt diese grobe Pause, ohne die HAL-Timerfrequenz zu verändern. [Messung](DESKTOP_PERFORMANCE.md). |
| Grafik | `Canvas.GetFullScreen()`, `GetBuffer()` und `Display()` verwenden den schon vorhandenen Bildpuffer. Keine neue Vollbildallokation pro Bild. |
| Bitmaptext | Daten des eingebauten `PCScreenFont.DefaultFont`; `PixelSurface` beschneidet jeden sichtbaren Glyphenbereich. |
| Zeichnen | Rechtecke, vor dem Rasterisieren beschnittene Linien, Bitmaptext und Pixelkopien. Ein Zeichenbereich kann auf ein Teilrechteck begrenzt werden. Hosttests decken auch negative und extreme Koordinaten ab. |

Die Methoden und ihre Sichtbarkeit wurden an den installierten Assemblies der Version 3.0.94 geprüft. Die [offizielle Plug-Dokumentation](https://cosmosos.github.io/articles/dev/plugs.html) beschreibt das verwendete Ersetzen einzelner Methoden. Die Paketmetadaten verweisen auf Cosmos-Quellstand `5d0b9535251472db7ecfe012f0435f0a04ffca16`.

## Grenzen

- Cosmos 3.0.94 verwirft in seiner normalen Tastaturabfrage Break-Ereignisse. Daher übernimmt ZehOS den Rohdatenpfad vollständig; `Console.ReadLine` und `Console.ReadKey` gehören nicht mehr zum interaktiven Pfad. Sobald Cosmos eine öffentliche Rohereignis-API bietet, sollen Plug und Zugriff auf die interne Layoutmethode entfallen.
- Die PS/2-Anbindung von Cosmos faltet manche erweiterten Scancodes zusammen, beispielsweise linkes/rechtes Strg und Tasten des Navigations-/Ziffernblocks. ZehOS übernimmt diese Codes; eine vollständige Unterscheidung dieser physischen Tasten ist damit noch nicht gegeben. Getestet wurden die gewöhnlichen Buchstaben, Shift, F4, Escape und die Editier-/Verlaufstasten des Referenzprofils.
- Mauszustände werden pro Schleifendurchlauf gelesen. Es gibt noch keine vollständige Rohereignishistorie für Maus und Hotplug-Geräte.
- Die Desktop-Schleife verarbeitet Anwendungen kooperativ; die feste Thread-Anbindung aus 6a ermöglicht daneben präemptive Hintergrundaufgaben mit Abbruch. Laufwerksinitialisierung und Dateioperationen bleiben synchron und können die Desktop-Schleife während ihrer Ausführung anhalten.
- Fenster und Terminal laufen gemeinsam im Kernel. Größenänderung, Minimieren, Prozessisolation und fremde Programme sind nicht implementiert. Der Desktop begrenzt offene Fenster auf acht und die Ausgabe je Terminal auf 256 umgebrochene Zeilen. Beim Ansichtswechsel wird die Textkonsolenausgabe geleert; offene Terminals sowie Zeilenentwurf und Verlauf beider Ansichten bleiben erhalten.
- VMware ist für die Thread-/Warteprobe seriell geprüft. Visuelle Bedienung in VMware, echte Hardware, USB-Eingabe und mehrere CPUs wurden für diese Erweiterung nicht geprüft.
- Cosmos 3.0.94 überspringt beim Exception-Unwind teilweise verschachtelte `finally`-Blöcke. Zwei gezielt abgebrochene Worker erreichten ihre äußeren Cancellation-Catches, aber keines der inneren Finally-Abschlussflags (`first-finally=0 second-finally=0 remaining=2 caught=2`). Reguläre Demo-Abbrüche verwenden deshalb Rückgabewerte statt Exceptions; die Aufgabenverwaltung bereinigt auch nach abgefangenen Workerfehlern auf einem expliziten normalen Fortsetzungspfad. Dies ist keine Garantie für beliebige fremde Exception-/Ressourcenpfade.

## Nachprüfung

Am 10.11.2026 bestanden **104 Hosttests** mit Warnungen als Fehler. Der native Release-x64-Build bestand mit **0 Warnungen und 0 Fehlern**. Eine unabhängige Codeprüfung fand einen Fehler bei druckbaren Ziffernblocktasten; zwei zunächst fehlgeschlagene Regressionstests sichern die Korrektur ab. Ziffernblock-8 und Dezimalpunkt wurden danach zusätzlich im Gast geprüft.

QEMU-Prüfungen: fortschreitende Zeit bei ungesendetem Entwurf, Einfügen mit Pfeiltasten, Verlauf, KeyDown/KeyUp, wiederholter Grafikwechsel mit erhaltenem Entwurf, Mausbewegung und Klicks bis zum Bildschirmrand, keine zurückbleibenden Zeigerspuren, Fehlerbehandlung der Shell, Datei schreiben/lesen sowie unveränderter Inhalt nach Unmount und VM-Neustart. Ein zusätzlicher Start ohne Datenplatte bestätigte Shell und Grafikprobe einschließlich der Rückkehr zur Shell.

Nach Integration von `master` bei `d81d24a` bestehen **131 Hosttests** mit Warnungen als Fehler. Normaler Release-Build und ausdrücklich mit `-p:ZehOSRecovery=true` erzeugter Recovery-Build sind erfolgreich. In QEMU wurden zusätzlich `version`, steigende `uptime`, `log`, die gemeinsame Statuszeit, Grafikwechsel, Dateilesen und `shutdown` geprüft; QEMU beendete sich über ACPI S5. Der Recovery-Start ohne Datenplatte blieb mit `log` und `echo` bedienbar. Die normale ISO unter `output-x64` wurde dabei beibehalten.

Nach dem Review von PR #5 bestehen **134 Hosttests** mit Warnungen als Fehler. Zwei zunaechst fehlgeschlagene Regressionen sichern kurze und leere bestaetigte Befehle gegen stehengebliebene Zeichen des vorherigen Entwurfs ab; ein weiterer Test erhaelt ueberbreite Befehle vollstaendig. Der native Release-x64-Build und ein erneuter QEMU-Test sind erfolgreich: Verlauf und Enter im selben Ereignisblock hinterlassen keine Textreste. F4/Esc erhaelt weiterhin den Entwurf; die Shell bleibt mit `echo` und `uptime` bedienbar.

Auf dem Host: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`.

## Fenster und Desktop am 12. November 2026

**149 Hosttests** mit Warnungen als Fehler bestehen auf SDK **10.0.403**. Fensterfokus/Z-Ordnung, Drag-Capture/Reset, Inhaltsclipping, Schließen, Starter-Limit und Neuzeichnen werden ebenso geprüft wie Ringpuffer, Umbruch/Leerzeilen, Scrollgrenzen, getrennte Eingabe/History und Arbeitsverzeichnisse. Zwei zunächst fehlgeschlagene Ziffernblocktests sichern ab, dass NumLock-9/3 Texteingaben statt PageUp/PageDown auslösen. Eine weitere rote/grüne Pixelregression verhindert, dass eine Scrollanzeige Text in der ersten Ausgabezeile verdeckt; die überlagerte Anzeige wurde entfernt.

Native Release- und Recovery-Builds sind erfolgreich. Ein allein installiertes SDK 11 wählte zuvor einen inkompatiblen ILC-Compiler und .NET-11-Laufzeitquellen für Cosmos 3.0.94. `global.json` beschränkt den Build deshalb auf SDK 10.0; es sind keine Cosmos-Pakete geändert worden. Für diese Prüfung wurde SDK 10.0.403 projektlokal unter `.tools/dotnet10` bereitgestellt und dessen `dotnet.exe` direkt aufgerufen. Visual Studio benötigt ein regulär installiertes .NET-10-SDK.

In QEMU ohne Datenlaufwerk geprüft: normaler Boot zu zwei Terminalfenstern, `echo` in beiden Fenstern, Fokuswechsel bei erhaltenem Entwurf, Ziehen außerhalb der Titelleiste bis an den Arbeitsflächenrand, freigelegter Hintergrund, Drag-Reset bei Esc/F4, PageUp/PageDown-Ausgabe, NumLock-9/3, separate Textshell, Schließen aller Fenster, Wiederöffnen und das Limit von acht Terminals. Der Recovery-Start bleibt mit `log` und `echo` bedienbar und kann per F4 zum Desktop und per Esc zurück wechseln.

Mit einer separaten Kopie des 64-MiB-Datenimages geprüft: Datei im Terminal schreiben/lesen, eigene Arbeitsverzeichnisse beider Terminals, Weiterarbeit nach fehlender Datei, Reboot und erneutes Lesen desselben Inhalts sowie Shutdown. Das ursprüngliche Image blieb SHA256-gleich. Der vorhandene `scripts/test-power-commands.ps1 -WithDebugger` besteht mit dem Desktop-ISO: GDB bleibt bei Reboot verbunden, Shutdown beendet Gast und Debugger normal. Ein vollständiger Desktop-Test mit vollem Datenträger, VMware und echte Hardware bleiben offen.

Gastprüfung wiederholen: normal und mit `ZehOSRecovery=true` bauen; im Desktop Befehle in beiden Terminals eingeben, einen Entwurf stehen lassen, das andere Fenster aktivieren/verschieben/schließen und über `+ Terminal` erneut öffnen. Mit `help` Ausgabe erzeugen und PageUp/PageDown nutzen. Esc/F4 muss Entwürfe und Fenster erhalten. Auch ohne Datenlaufwerk muss der Desktop starten. Dateiprüfungen nur mit einer isolierten Imagekopie durchführen.

## Doom-Vorversuch am 13. November 2026

Der [getrennte Hostversuch für Roadmap 0.4](DOOM_FEASIBILITY.md) baut Managed Dooms Kern ohne Desktoppakete unter .NET 10 und erfasst verbleibende Datei-, Prozess-, Zeit- und Speicherabhängigkeiten. Es wurden keine neuen Cosmos-Schnittstellen geprüft: NativeAOT-Link für Cosmos, WAD-Zugriff, Renderausgabe und Spielbetrieb in QEMU/VMware bleiben offen. Kernel und Paketversionen sind unverändert.

## Desktop-Dateifehler am 13. November 2026

Die [Nachprüfung von Roadmap 7.5](DESKTOP_STORAGE_TESTS.md) besteht mit dem unveränderten Kernel auf Basis `7264b34`, Cosmos 3.0.94, SDK 10.0.112 und QEMU 10.2.2. Ohne Datenplatte startet der Desktop und bleibt nach Datei-/Pfadfehlern bedienbar. Auf einer vollständig belegten isolierten FAT32-Kopie scheitern Ersetzen und Neuanlegen mit ENOSPC; Originalinhalt und Bereinigung sind vor und nach Reboot geprüft. Esc/F4 und anschliessende Befehle funktionieren. 155 Hosttests und sieben Python-Helfertests bestehen. Native Release-ISO erfolgreich gebaut; VMware und echte Hardware bleiben offen.

Der FAT32-Formatter dieser Version schreibt den primären FSInfo-Sektor und den Sicherungs-Bootsektor, lässt aber in dieser Probe den FSInfo-Sicherungssektor leer. Der Offline-Testhelfer erhält diesen Zustand; daraus wird kein allgemeiner Nachweis für alle FAT32-Layouts abgeleitet.

## Desktopanwendungen am 13. November 2026

[Etappe 8](DESKTOP_APPS.md) verwendet die vorhandenen Grafik-, Eingabe- und Dateischnittstellen für gemeinsame Bedienelemente, Betrachter, Editor und Pong. Native Normal-/Recovery-Builds und QEMU-Prüfungen mit isolierten Datenkopien bestehen. Dateiinhalt ist nach Reboot erhalten, der ENOSPC-Schließdialog erhält den Entwurf, Pong bleibt mit der übrigen Oberfläche bedienbar. 173 Hosttests prüfen unter anderem die wiederverwendete Shellbearbeitung, Fokus, Clipping, Datei-/Schließfehler sowie zeitskaliertes Spiel und Ressourcenfreigabe. VMware und echte Hardware bleiben offen.

Die Speicherdiagnose verwendet `GC.GetGCMemoryInfo().HeapSizeBytes` (Heapstand der letzten Sammlung, vor der ersten gegebenenfalls null) und `GC.GetTotalAllocatedBytes(false)` (kumulativ). `GC.GetTotalMemory(false)` ist für diesen Paketstand nicht verwendbar: Der native Link verlangt dabei das fehlende `RhWaitForPendingFinalizers`. Es wurde kein Ersatz für die Cosmos-GC eingeführt. Die bereits geprüfte Core-Interruptmaske der Eingabe erhält zusätzlich zur Experimental-Ausnahme eine eng begrenzte `NAOT0007`-Ausnahme; der `using`-Schutz bleibt erhalten. Renderkosten und Grenzen der Beobachtung dokumentiert die Prüfanleitung.
