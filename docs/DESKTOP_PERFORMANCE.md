# QEMU: Mausbewegung und 60-Hz-Bildtermine

Stand: 13. November 2026. Grundlage ist der Desktopstand aus PR 17, Commit `358bf75`. Eigener Branch `codex/desktop-fps`; die laufenden Etappen 6a–6c werden nicht verändert.

## Ursache und Änderung

Die gemeldeten etwa 15 FPS ließen sich bei ständiger Mausbewegung reproduzieren. Das bisherige `ms` im Desktop war lediglich die maximale Dauer von Zeichnen und `Canvas.Display` im letzten Messintervall, keine FPS-Anzeige. Eine vorübergehende Diagnose zählte deshalb Bildausgaben und maß Zeichnen und Präsentieren getrennt.

Im installierten Cosmos 3.0.94 leitet `TimerManager.Wait(1)` an den HAL-`TickSource` weiter. Der wartet über einen registrierten Timer und Halt-Aufrufe auf dessen Ablauf. Im Referenzgast meldet `TimerManager.Frequency` nur 18 Hz. Die gewünschte 1-ms-Pause kann daher erst mit einem wesentlich gröberen Timertakt enden. Die reine Bildarbeit blieb meist deutlich unter dem beobachteten Abstand zwischen Bildern.

Die gemeinsame Ereignisschleife verwendet nun das bereits in 3.0.94 geprüfte `Thread.Sleep(1)`. Es werden weder Timerfrequenz noch Schedulerstrategie oder Werkzeugversion geändert. Nur dieser Austausch hob die Bildrate auf ungefähr 50–52 FPS.

Zusätzlich berechnet `FrameTiming.NextDeadline` den nächsten festen 1/60-Sekunden-Termin in monotonen Millisekunden. Die alten Fristen `jetzt + 16` übernahmen jede verspätete Weckzeit in alle folgenden Fristen. Bei beispielsweise 10-ms-Weckschritten ergeben sie nur 50 Bilder pro Sekunde; die neuen Fristen ergeben dabei 60. Verpasste Termine werden übersprungen, statt eine Warteschlange alter Bilder abzuarbeiten. Es gibt weder eine aktive Warteschleife noch einen zusätzlichen UI-Thread.

`DesktopMetrics` zählt jetzt tatsächlich abgeschlossene Render-/Display-Aufrufe pro gemessenem Zeitintervall. Die bestehende maximale Bilddauer und die Speicherdiagnosen bleiben erhalten. Das zählt Ausgaben zum Cosmos-Framebuffer, nicht nachgewiesene Bildwechsel eines physischen Monitors oder der QEMU-Fensteroberfläche. Der ruhende Desktop bleibt ereignisgesteuert und muss keine 60 identischen Bilder pro Sekunde erzeugen.

## Vergleichsmessung

Windows, .NET SDK 10.0.403, Cosmos 3.0.94, QEMU 10.2.2, q35, CPU max, 512 MiB RAM, Standard-VGA, 1024×768×32, PS/2-Eingabe, ohne Datenplatte. Alle drei Varianten verwenden dieselbe vorübergehende Diagnose und dieselbe QMP-Mausbewegung: alle etwa 8 ms kleine relative Bewegungen mit periodischem Richtungswechsel. Je zwölf Sekunden auf leerem Desktop, mit einem Terminal und danach mit zusätzlichem aktivem Pong.

Die Tabelle verwendet die vollständigen stabilen Gastzeitfenster 3–12 s, 15–24 s und 29–38 s. Start und Programmwechsel liegen außerhalb dieser Fenster. Je Zeile liegen acht beziehungsweise neun Ein-Sekunden-Messwerte vor. Spalten zeigen Median und beobachtete Spanne der FPS.

| Szene | Bisher | Nur Thread.Sleep | Thread.Sleep und feste Termine |
| --- | --- | --- | --- |
| Freier Desktop, Maus bewegt | 16 (15–16) | 51 (49–52) | 59 (59–60) |
| Terminal, Maus bewegt | 16 (15–16) | 51 (50–52) | 59 (58–61) |
| Pong und Terminal, Maus bewegt | 15 (14–15) | 50 (43–52) | 59 (58–60) |

Die Spitzen der gemessenen vollständigen Bildarbeit lagen in der letzten Variante in diesen Fenstern bei 10,50 / 10,69 / 12,23 ms. Werte um 61 FPS entstehen an den Grenzen der einzelnen Messfenster und durch ganzzahlige Millisekunden; die Zielkadenz bleibt 60 Hz. Einzelne lange Frames, Anwendungsstart und synchrone Dateioperationen können weiterhin ruckeln. Daraus wird keine garantierte Mindestbildrate für beliebige Last oder andere Hosts abgeleitet.

Die Diagnoseausgaben und die zusätzliche Profilierung pro Bild wurden nach diesem Vergleich entfernt. Der produktive Stand enthält nur die eigentliche Korrektur und die FPS-Anzeige. Keine Änderung am Renderer, keine neuen Vollbildpuffer und keine Bibliothek waren für die gemessene Verbesserung erforderlich.

## Prüfungen und Grenzen

193 Hosttests in Release mit Warnungen als Fehler bestanden. Die sechs Kadenztests wurden zuerst gegen das alte `jetzt + 16` ausgeführt und schlugen erwartungsgemäß fehl; nach der Änderung bestanden sie. Auch die fehlende FPS-Anzeige wurde zuerst mit einem fehlgeschlagenen Test belegt. Die gesamte bestehende Desktop-/App-/Dateilogik bleibt in der Suite enthalten.

Normale und Recovery-Release-Builds des endgültigen Stands ohne Diagnoseausgaben bestanden mit Warnungen als Fehler (jeweils 0 Warnungen/0 Fehler). Der normale Stand wurde anschließend auch mit `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release` am üblichen Ausgabeort bereitgestellt.

QEMU am endgültigen ISO: je sechs Sekunden Mausbewegung auf Desktop, im Terminal und mit Pong; sichtbare FPS-Anzeige um 59–60, `echo fps-final-ok`, Pongbewegung und Punkte, Esc zur Textshell, `echo shell-ok`, F4 zurück bei erhaltenen Fenstern und sauberes Shutdown. Recovery: direkter Textshellstart, `echo recovery-fps-ok`, F4 zum Desktop, vier Sekunden Mausbewegung mit FPS-Anzeige um 60, Menü/Terminal und `echo recovery-terminal-ok`, anschließend Textshell und Shutdown. Alle Test-VMs waren plattenlos und wurden beendet.

VMware Player: Derselbe finale normale Kernel startete in der eigenen plattenlosen UEFI-Test-VM bis zur seriellen Meldung `Desktop ist bereit`. Eine interaktive Bildraten-/Mausprüfung in VMware wurde nicht ausgeführt. Ein unabhängiger Agent prüfte den gesamten Änderungsstand einschließlich der neuen Dateien und Aufrufer ohne relevante Befunde.

Offen bleiben VMware-Bildrate, sichtbare Aktualisierungsrate der QEMU-Fensteroberfläche, echte Hardware und die spätere Integration mit den laufenden Etappen 6a–6c. Vorhandene Datenimages wurden für diese Bildratenmessung nicht verwendet. Die messbare Verbesserung betrifft die Gast-Ereignisschleife; Hostlast, QEMU-Anzeige und synchrone Dateioperationen können weiterhin zusätzliche Verzögerungen erzeugen.
