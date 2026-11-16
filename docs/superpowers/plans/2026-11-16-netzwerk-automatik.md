# Netzwerk-Automatik und direkte Befehlsergebnisse – Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ZehOS richtet das Netzwerk nach dem Start selbst per DHCP ein, zeigt den Zustand als Symbol unten rechts in der Taskleiste (wie Windows), und `net`-/`pkg`-Befehle schreiben ihr Ergebnis selbst ins Terminal statt über `net status`/`pkg status`.

**Architecture:** Die Hintergrundaufträge bleiben (die Ereignisschleife darf nicht blockieren, der Worker schreibt nicht in die Anzeige). Neu ist nur, dass `ShellService.Poll`, das Terminal und Textshell ohnehin in jeder Runde aufrufen, fertige Ergebnisse abholt und ausgibt. Ein dauerhafter Hintergrundauftrag startet DHCP, solange ein Adapter Link, aber keine Adresse hat. Die Taskleiste liest denselben Zustand über `INetworkService.Status`.

**Tech Stack:** C#/.NET 10, Cosmos Gen3 3.0.94, MSTest. Keine neuen Pakete.

**Spec:** Kein eigenes Entwurfsdokument; der mit dem Benutzer am 16.11.2026 abgestimmte Entwurf steht in diesem Abschnitt und in „Entscheidungen".

## Entscheidungen

- `net status` und `pkg status` entfallen ersatzlos. `net cancel` und `pkg cancel` bleiben.
- Beim Start eines Auftrags erscheint genau eine Zeile: `Laeuft ... Abbruch: net cancel` bzw. `... pkg cancel`. Das Ergebnis folgt als eigene Ausgabe, sobald der Auftrag endet.
- Das Protokoll auf der seriellen Schnittstelle (`NET <Text>`, `PKG <Text>`) bleibt unverändert; die Gasttests warten darauf.
- Automatik: alle 10 s prüfen; DHCP nur bei Link ohne Adresse. `net static` bleibt als manuelle Übersteuerung und wird nicht überschrieben, weil danach eine Adresse vorhanden ist. Der Auftrag läuft dauerhaft und belegt einen der 8 Plätze von `BackgroundTaskService`.
- Anzeige: Symbol rechts in der Taskleiste zwischen Arbeitsflächen-Umschalter und Uhr. Linksklick öffnet ein kleines Flyout darüber mit Zustand und IPv4-Adresse; jeder weitere Klick schließt es.
- `pkg install` und `pkg upgrade` laden zuerst den Katalog. Schlägt das fehl, bricht der Auftrag mit dieser Meldung ab (ohne Netz scheitert die Installation ohnehin). `pkg update` bleibt als eigener Befehl.
- Keine voreingestellte Paketquelle; `pkg source <URL>` bleibt nötig (nicht beauftragt).
- Version: 0.4.0 (Feature). Vor dem PR im IRC prüfen, welche Versionen #96/#97 und der Folgebranch belegen (zuletzt 0.3.4–0.3.6), und gegenüber dem dann aktuellen `master` erhöhen.

## Global Constraints

- Regeln aus `AGENTS.md` gelten vollständig: Kommentare auf Deutsch, Zeilenenden und Schreibweise bestehender Dateien erhalten, keine `finally`-Blöcke auf Fehlerwegen (Cosmos 3.0.94 überspringt sie), `ResultErr`/`ResultObject<T>` verwenden.
- Kein `Console.ReadLine()`/`ReadKey()` und kein blockierender Aufruf in der Ereignisschleife. `INetworkService.Execute` wartet auf `CosmosNetworkService.Gate` und darf deshalb nur aus Hintergrundaufträgen laufen.
- Cosmos-abhängige Dateien (`CosmosNetworkService.cs`, `Kernel.cs`, `DisplayService.cs`, `InteractiveSession.cs`) gehören nicht in den Hostbuild. Neue plattformunabhängige Dateien in `ProjektZeh.Tests/ProjektZeh.Tests.csproj` einbinden.
- Hosttests: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`.
- IRC-Abstimmung nach `AGENTS.md`; Relay: `py -3 .tools/irc/irc-relay.py --nick agent-peter-netzwerk-auto`, Nachrichten über `.tools/irc/outbox.txt`.

## Review Focus

1. **Link vorhanden, aber kein DHCP-Server:** `net static` muss weiter funktionieren. Wenn `DhcpClient.SendDiscoverPacket()` ohne Antwort nicht zurückkehrt, hält die Automatik die Netzwerksperre dauerhaft. → Task 3, Schritt 1 (Gastprüfung vor der Umsetzung).
2. **Ergebnis trifft ein, während der Benutzer in der Textshell tippt:** Die begonnene Eingabe darf nicht verloren gehen. → Task 6 (QEMU).
3. **Terminal wird geschlossen, bevor der Auftrag endet:** kein Schreiben in eine geschlossene Sitzung. → Task 1, Test `ClosedSessionDropsResult`.
4. **Zwei Terminals:** Das Ergebnis erscheint nur in dem Terminal, das den Auftrag gestartet hat. → Task 2, bestehender Test `WorkAndCancellationStayWithinSession`.
5. **Update-Prüfung während der DHCP-Suche:** `CosmosHttpUpdateSource` nimmt die Sperre ohne Warten und meldet dann „belegt". Verhalten prüfen und in `README.md` nennen. → Task 6.

---

### Task 1: `ShellJob` und Ausgabe über `ShellService.Poll`

**Files:**
- Create: `ProjektZeh/Shell/ShellJob.cs`
- Modify: `ProjektZeh/Shell/ShellService.cs:41-53` (`Poll`)
- Modify: `ProjektZeh.Tests/ProjektZeh.Tests.csproj` (neue Datei einbinden)
- Test: `ProjektZeh.Tests/ShellJobTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class ShellJob(BackgroundTaskService tasks, string command, string label, Action<string>? log = null)` – `command` ist `net`/`pkg`, `label` ist `Netzwerkauftrag`/`Paketauftrag`.
  - `ResultErr Start(string taskName, ITextOutput output, Func<CancellationToken, string> work)` – Fehler `Ein {label} dieser Sitzung laeuft bereits.`; schreibt `Laeuft ... Abbruch: {command} cancel`.
  - `ResultErr Cancel(ITextOutput output)` – Fehler `Kein {label} laeuft.`; schreibt `Abbruch angefordert.`
  - `string? TakeResult()` – liefert das Ergebnis genau einmal.
  - `void Close()` – bricht einen laufenden Auftrag ab.
  - `ShellService.TakeJobResult` (`internal Func<string?>?`), von `Poll` geleert.
- Ergebnistexte: Rückgabe von `work`; bei `OperationCanceledException` `{label} abgebrochen.`; bei anderer Ausnahme `{label} fehlgeschlagen: {Message}`. `log` erhält denselben Text.

- [ ] **Step 1:** Tests schreiben: `ResultIsDeliveredOnce` (nach Ende liefert `TakeResult` den Text, danach `null`), `SecondStartIsRejectedWhileRunning`, `CancelReportsCancelled` (`Netzwerkauftrag abgebrochen.`), `FailureIsReported`, `PollWritesFinishedResult` (`ShellService` mit `TakeJobResult`; `Poll` schreibt den Text und liefert `true`), `ClosedSessionDropsResult` (nach `Close()` schreibt `Poll` nichts).
- [ ] **Step 2:** Tests ausführen; sie schlagen fehl, weil `ShellJob` fehlt.
- [ ] **Step 3:** `ShellJob` aus der doppelten Logik in `NetworkShellCommands.cs:54-87` und `PackageShellCommands.cs:165-194` bilden (Sperre, `running`, `taskId`, `result`).
- [ ] **Step 4:** `Poll` erweitern: solange die Sitzung offen ist und `TakeJobResult` Text liefert, `output.WriteLine(text)` und `true` zurückgeben; die Kennwortlogik bleibt unverändert.
- [ ] **Step 5:** Tests grün, Commit.

### Task 2: `net` und `pkg` auf `ShellJob` umstellen

**Files:**
- Modify: `ProjektZeh/Shell/NetworkShellCommands.cs`, `ProjektZeh/Shell/PackageShellCommands.cs`
- Modify: `ProjektZeh/Kernel.cs:118-151` (`CreateShell`)
- Test: `ProjektZeh.Tests/NetworkCommandTests.cs`, `ProjektZeh.Tests/PackageTests.cs:408-486`

**Interfaces:**
- Consumes: `ShellJob` aus Task 1.
- Produces: `NetworkShellCommands.TakeResult()` und `PackageShellCommands.TakeResult()`; Kernel setzt `shell.TakeJobResult = () => network.TakeResult() ?? packageCommands.TakeResult();`.

- [ ] **Step 1:** Tests anpassen: `status` durch `TakeResult()` ersetzen (Hilfsmethode `Status(pkg)` in `PackageTests` wartet auf `TakeResult() != null`); neue Tests `StatusIsNoLongerACommand` für `net status` und `pkg status` (Ergebnis `InvalidArgument`).
- [ ] **Step 2:** `status` aus Befehlen und Verwendungstexten entfernen; beide Klassen auf `ShellJob` umstellen. Verhalten erhalten: Aufgabenname `net <Operation>` bzw. `pkg`; ein abgebrochener, aber erfolgreicher Netzaufruf meldet `Netzwerkauftrag abgebrochen.`; Administratorprüfung und Reihenfolge der Fälle bleiben.
- [ ] **Step 3:** `PackageShellCommands`: `InstallFromCatalog` und `Upgrade` rufen zuerst `Update(token)` auf und brechen mit dessen Meldung ab, wenn es fehlschlägt. Dafür liefert `Update` ein `ResultErr` plus Text oder wird in `LoadCatalog` (Result) und Textbildung getrennt. Die Meldungen `zuerst pkg update` entfallen.
- [ ] **Step 4:** Test `CommandsInstallFromSignedCatalogAndUpgrade` anpassen: `install doom` gelingt direkt nach `pkg source`; `upgrade` findet 1.1.0 ohne vorheriges `update`; neuer Fall: Katalog nicht ladbar → Meldung beginnt mit `Katalog nicht geladen:`.
- [ ] **Step 5:** Hosttests grün, Commit.

### Task 3: Netzwerkzustand und automatische DHCP-Suche

**Files:**
- Modify: `ProjektZeh/Network/INetworkService.cs`, `ProjektZeh/Network/CosmosNetworkService.cs`
- Create: `ProjektZeh/Network/NetworkAutoConfiguration.cs`
- Modify: `ProjektZeh/Kernel.cs` (`Run`, nach den Probes, vor `interactive.Run`; nicht im Installer-Build), `ProjektZeh/Runtime/CosmosSystemInfo.cs:84`
- Test: `ProjektZeh.Tests/NetworkAutoConfigurationTests.cs`, Fake in `NetworkCommandTests.cs:55` ergänzen

**Interfaces:**
- Produces:
  - `internal readonly record struct NetworkStatus(bool Adapter, bool Link, string? Address)` mit `string Text`: `Verbunden` / `Suche nach Netzwerk ...` / `Keine Verbindung` / `Kein Netzwerkadapter`.
  - `NetworkStatus INetworkService.Status { get; }` – liest nur, ohne die Netzwerksperre.
  - `internal static class NetworkAutoConfiguration` mit `const int RetryMilliseconds = 10000` und `static void Run(INetworkService network, BackgroundTaskContext context, Action<string> log, int retryMilliseconds = RetryMilliseconds)`.

- [ ] **Step 1 (Gastprüfung zuerst):** In QEMU ohne DHCP-Server (`-nic user,restrict=on` bzw. Netz ohne DHCP) `net dhcp` ausführen und messen, ob und wann der Auftrag endet. Die Cosmos-Quellen liegen lokal nicht vor (nur NuGet-Binärpakete). Kehrt `SendDiscoverPacket()` nicht zurück, die Automatik auf einen Versuch je Link-Wechsel begrenzen und das Ergebnis in `docs/COSMOS_SUPPORT.md` festhalten; sonst weiter wie geplant.
- [ ] **Step 2:** Tests schreiben: `RequestsDhcpWhenLinkHasNoAddress`, `DoesNothingWithoutLink`, `DoesNothingWhenAddressExists` (statische Konfiguration bleibt), `LogsRepeatedFailureOnce`, `StopsOnCancellation`; dazu `NetworkStatus.Text` für alle vier Zustände.
- [ ] **Step 3:** Umsetzen. Schleife: Zustand lesen → nur bei `Link && Address == null` `Execute("dhcp", [], token)` → Text nur protokollieren, wenn er sich vom letzten unterscheidet → `context.Sleep(retryMilliseconds)`.
- [ ] **Step 4:** Kernel: eine gemeinsame `CosmosNetworkService`-Instanz als Feld; `tasks.Start("net auto", ...)` mit Protokoll `NET <Text>` über `Cosmos.Kernel.System.Diagnostics.Log.WriteString`. Die Zeile „ZehOS startet noch keinen DHCP-/Internetdienst." in `CosmosSystemInfo.ReadNetwork` entfernen.
- [ ] **Step 5:** Hosttests grün, Commit.

### Task 4: Netzwerksymbol in der Taskleiste

**Files:**
- Modify: `ProjektZeh/Graphics/Desktop.cs` (Konstruktor `:84`, Klickbehandlung `:384-391`, Layout `:514-523`, `Update` `:546`, `RenderTaskbar` `:639-696`), `ProjektZeh/Graphics/AppIcons.cs`
- Modify: `ProjektZeh/Graphics/DisplayService.cs:27-40`, `ProjektZeh/Runtime/InteractiveSession.cs:14-17`, `ProjektZeh/Kernel.cs:72-79`
- Test: `ProjektZeh.Tests/DesktopRedesignTests.cs`

**Interfaces:**
- Consumes: `NetworkStatus`, `INetworkService.Status` aus Task 3.
- Produces: zusätzlicher optionaler Parameter `Func<NetworkStatus>? getNetwork = null` am Ende der Konstruktoren von `Desktop`, `DisplayService` und `InteractiveSession`; `AppIcon.Network`.

- [ ] **Step 1:** Tests schreiben: `TrayIconShiftsPagerLeft` (mit `getNetwork` liegt der Umschalter 40 px weiter links, ohne unverändert), `ClickOnTrayTogglesFlyout`, `AnyOtherClickClosesFlyout`, `FlyoutShowsAddress` (über die gezeichnete Fläche oder einen internen Text-Getter wie bei bestehenden Desktoptests).
- [ ] **Step 2:** Layout: `TrayWidth => getNetwork == null ? 0 : 40`; `TrayBounds => new(width - ClockWidth - TrayWidth, height - 48, TrayWidth - 4, 40)`; `PagerBounds` um `TrayWidth` nach links.
- [ ] **Step 3:** Zustand nur im 500-ms-Takt von `Update` lesen und zwischenspeichern (keine Zeichenkettenbildung pro Bild).
- [ ] **Step 4:** Symbol (Monitor mit Kabel, 20×20, Linien wie die übrigen `AppIcons`): verbunden in `UiPainter.Foreground`; Suche in `UiPainter.Secondary` mit im 500-ms-Takt wechselndem Akzentpunkt; ohne Link/Adapter in `Secondary` mit Kreuz.
- [ ] **Step 5:** Flyout (abgerundetes Panel wie das Startmenü, rechtsbündig über dem Symbol): Zeile 1 `Netzwerk`, Zeile 2 `status.Text`, Zeile 3 `IPv4 <Adresse>` falls vorhanden. Öffnen/Schließen setzt `sceneDirty`.
- [ ] **Step 6:** Hosttests grün, Commit.

### Task 5: Dokumentation, Gasttest-Skript, Version

**Files:**
- Modify: `README.md:241`, `README.md:267`, `docs/PACKAGE_FORMAT.md:97`, `docs/NETWORK_TESTS.md:7`, `scripts/test-network.py:252-253`, `ProjektZeh/ProjektZeh.csproj` (Version), ggf. `docs/COSMOS_SUPPORT.md`
- Nicht ändern: datierte Pläne/Entwürfe unter `docs/superpowers/` (historische Stände).

- [ ] **Step 1:** Texte auf das neue Verhalten umstellen (automatisches DHCP, Symbol, Ergebnis im Terminal, kein `status`, `install`/`upgrade` laden den Katalog).
- [ ] **Step 2:** `scripts/test-network.py`: `net status` entfernen; auf die serielle Zeile `NET Netzwerkauftrag abgebrochen.` warten. Weitere Skripte mit `pkg status`/`net status` per Suche prüfen (`scripts/test-wasm-*.py`, `scripts/tests/`).
- [ ] **Step 3:** Version erhöhen (siehe Entscheidungen), Commit.

### Task 6: Kernelprüfung, Review, PR

- [ ] **Step 1:** `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release`.
- [ ] **Step 2:** QEMU über `scripts/start-qemu.ps1`: Boot → serielle Zeile `NET DHCP IPv4: ...` ohne Eingabe; Symbol zeigt verbunden, Flyout zeigt die Adresse; im Terminal `ping 10.0.2.2`, `nslookup`, `fetch` liefern das Ergebnis ohne weiteren Befehl; zweites Terminal bleibt unberührt; `net cancel`; Textshell (Esc/F4): Ergebnis während einer begonnenen Eingabe; `pkg install` auf einer isolierten Kopie des Datenimages; Update-Prüfung während der Suche; `shutdown` (der dauerhafte Auftrag muss sich von `StopAll(2000)` beenden lassen).
- [ ] **Step 3:** `py -3 scripts/test-network.py` und `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-power-commands.ps1`.
- [ ] **Step 4:** VMware über `scripts/start-vmware.ps1` prüfen oder ausdrücklich als offen führen.
- [ ] **Step 5:** Unabhängiges, nur lesendes Review durch einen frischen Agenten; Befunde bewerten und beheben.
- [ ] **Step 6:** IRC lesen, pushen, PR mit Reviewern `larsernst` und `azazel55605` (Autor `peterzw98` als Assignee), Abschluss im Kanal melden, `QUIT`.
