# Wasm-Host-Ausnahmeschutz: Umsetzungsplan

**Ziel:** Issue #204 abschließen, ohne dass verwaltete Ausnahmen aus present, present_indexed, open, file_size, file_read oder close die native Grenze verlassen.

**Architektur:** Vorhandene Autorencommits e56e103/15efd94 auf fix/wasm-host-exception-guard erhalten. Die vorhandenen try/catch-Grenzen und einmalige Present-Diagnose verwenden; ABI und C-seitige Gastbereichsprüfung erhalten. Keine neue Abhängigkeit oder öffentliche Test-API.

**Anforderung:** https://github.com/peterzw98/ProjektZehOS/issues/204; Wiederaufnahme durch IRC-Koordinator am 17. November 2026. Cosmos 3.0.97, .NET 10, keine Werkzeugupdates. Sicherheit und Grenzlogik prüft der Hauptagent selbst.

- [x] Bestehenden Branch und alle betroffenen Aufrufer lesen; aktuellen master einarbeiten, Autorencommits erhalten.
- [x] Produktive WasmRuntime-Quelle im Hosttestprojekt einbinden (ZEHOS_WASM, unsafe); nur Cosmos-Protokoll, HW-Zufall und physische Speicherabfrage testseitig ersetzen. Kein nativer Loaderaufruf in Hosttests.
- [x] Echte private UnmanagedCallersOnly-Rückrufe über Funktionszeiger testen: werfende IWasmSession.Present, Indexed-Konvertierung, Folgeaufruf, werfende Root-Abfrage sowie FileStream.Length/Read/Dispose. Fehler dürfen nicht entweichen; Dateien liefern -1, Close entfernt Handle, Present-Diagnose höchstens einmal je Lauf.
- [x] Regression mit unveränderter master-Runtime in isoliertem Testlauf nachweisen; erwartetes Fehlersignal ist die entwichene Ausnahme (ein Prozessabbruch ist unter NativeAOT ebenfalls möglich). Danach geschützte Autorenfassung wiederherstellen und Tests wiederholen.
- [x] Hostsuite mit Warnungen als Fehler, normaler nativer Release-Build und gezielter QEMU-Appstart; Gastfehlerprobe soweit verfügbar. Hosttest belegt keine Cosmos-Allokationsfehler, Nullzeigerfehler oder VMware-Kompatibilität.
- [ ] Version 0.5.82 vor PR mit Koordinator abgleichen, unabhängiges Review, Push und PR ohne Merge. Offene Plattformprüfungen nennen.

Betroffene Dateien: WasmRuntime.cs (vorhandene Korrektur), ProjektZeh.Tests.csproj, WasmRuntimeCallbackTests.cs, WasmRuntimeHostStubs.cs, ProjektZeh.csproj (Version). Dokumentation nur um tatsächlich geprüfte Erkenntnisse ergänzen.

Prüfstand: 1304 Hosttests bestanden, vier Windows-Skips. Die Gegenprobe auf ungeschütztem master liefert InvalidOperationException aus HostFileSize. Normaler nativer Release-Build: 0 Warnungen und Fehler; Gate: sechs Plugs gebunden. QEMU/WHPX-Desktopablauf in 55 Sekunden bestanden. Ergänzte CosmosWasmProbe: zwei Läufe mit je zwei werfenden Bildrückrufen enden mit Ok/42 und je einer Diagnose. Geprüft wurde nur dieser gezielte Abschnitt, nicht die gesamte Probe mit externer C#-App. VMware, Hardware und Cosmos-Null-Allokation bleiben offen. Die neue Gastprüfung verwendet CosmosWasmProbe.cs ohne neues Buildflag. Für das frische Probe-ISO ist der Publish-Pfad maßgeblich; output-x64 wird nur durch Cosmos-CLI aktualisiert.
