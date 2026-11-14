# Loader und Paketmanager: Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Dieser Plan ist ein Entscheidungsentwurf; die Umsetzung wurde nicht beauftragt. Ein unabhängiges Abschlussreview bleibt erforderlich.

**Goal:** C#-Anwendungen und Doom unabhängig vom Kernel installieren, aus Dateien ausführen und aktualisieren.

**Architecture:** WebAssembly-Programme mit vorhandenem Interpreter; zuerst wasm3 als statischer Kernelbaustein prüfen. C#-Loader und Hostadapter verbinden die VM mit den bestehenden Diensten, IApplication und dem Desktop. Signierte unveränderliche Paketversionen werden erst nach vollständiger Prüfung aktiviert.

**Tech Stack:** Bestehendes .NET 10, Cosmos Gen3 3.0.94, x64, MSTest/Moq und BouncyCastle.Cryptography 2.7.0; vorgeschlagen wasm3 und .NET-10-WASI sowie C/Wasm-Toolchain ausschließlich nach konkreter Abhängigkeitsentscheidung.

**Spec:** [Entscheidungsentwurf](../specs/2026-11-14-loader-packages-design.md). Alle dort genannten Limits, Pfadregeln und Sicherheitsgrenzen gelten für diesen Plan.

## Global Constraints

- Ausgangspunkt `origin/master` bei `b51cb868072e8c00a3f79f4b3e4c83b26e2ea4b2`; vor Umsetzung Remote-/Doom-/Konsolenstand erneut prüfen und passende integrierte Basis wählen.
- .NET 10 und Cosmos Gen3 3.0.94 erhalten. Keine bestehenden Pakete, SDKs oder Werkzeuge ohne vorherige Nutzerentscheidung aktualisieren.
- C#-App-Nachweis UND Doom-Abnahme sind Voraussetzung für den Produktloader. Fehlschlag stoppt abhängige Phasen.
- Kein Kernelrebuild pro App, keine appbezogenen Buildflags im fertigen Pfad; ein anfänglicher Kernelbuild für den allgemeinen Loader ist nötig.
- Nur isolierte neue Datenimages/Testkopien; bestehendes `.tools/qemu/data-fat32.img` niemals verwenden oder überschreiben.
- Deutsche Kommentare, bestehende Result-Typen, Fehlerursachen und Sicherungslogik erhalten. Kein eigener Interpreter, kein JIT, keine Installationsskripte.
- Nach jedem Lieferstand gezielte Hostprüfung; bei Kerneländerungen nativer Build und Gastprobe. VMware und Hardware nur bei tatsächlichem Nachweis als geprüft bezeichnen.

## Review Focus

1. Endlosschleife ohne Host-/Funktionsaufrufe: Abbruch muss innerhalb 250 ms gelingen; Aufgabe 1/3.
2. FAT32-Pfadalias, Integerüberlauf und kollidierende Namen: vor Zugriff/Allokation ablehnen; Aufgabe 3/5.
3. Logout oder Rechteentzug bei offenem Handle: weitere Dateioperationen sperren, Instanz abbrechen; Aufgabe 3/4.
4. ENOSPC/ACL-Persistenzfehler/Neustart zwischen einzelnen Installationsschritten: bisherige geprüfte Version erhalten oder gezielt sperren; Aufgabe 6/8.
5. Upgrade/Remove gleichzeitig mit Launch sowie manipulierte Datei nach Installation: Versionsreferenz schützen und Integrität erneut prüfen; Aufgabe 4/6/8.

## Aufgabe 1: Interpreter und C#-Toolchain nachweisen

**Dateien:** neu `experiments/Wasm.LoaderProbe/README.md`, `experiments/Wasm.LoaderProbe/native/`, `experiments/Wasm.CSharpProbe/`, `ProjektZeh/Runtime/CosmosWasmProbe.cs`, `scripts/test-wasm-loader.py`, `docs/WASM_FEASIBILITY.md`; ändern `ProjektZeh/ProjektZeh.csproj` nur für opt-in Probe und native Verknüpfung.

**Schnittstelle:** Probe liefert seriell `WASM_PROBE PASS` erst nach externem C#-Nachweis, andernfalls `WASM_PROBE FAIL <Ursache>`. Noch keine Produkt-API festlegen. Der separate C-Adapter exportiert `zehos_wasm_probe_run` mit primitiven C-Typen; keine C++-Exceptions oder verwalteten Exceptions über diese Grenze.

- [ ] Verwendete lokale SDK-/Cosmos-/C-Toolchain erfassen, wasm3 auf Spec-Commit pinnen. Kleinsten nativen Build mit Cosmos-Sdk-C-Buildintegration untersuchen; alle unaufgelösten libc-/libm-/TLS-/Allokationssymbole auflisten. Lizenz-/Abhängigkeitsliste und konkret fehlende Werkzeuge vor Installation vorlegen. Keinen vorhandenen SDK-Stand ersetzen.
- [ ] Host-/Gastfixtures anlegen: Wasm-add liefert 42; ungültiges/trunkiertes Modul scheitert; `loop { br 0 }` endet per Gas/Abbruch; Überlauf bei memory.grow, tiefe Rekursion und Allokationsfehler enden kontrolliert. Helfer gegen ISO ohne Probe negativ ausführen.
- [ ] wasm3 statisch mit minimaler belegter libc-Anbindung bauen. Keine Standard-WASI-Dateirechte des Hosts übernehmen. Bei erheblichem Portbedarf Befund festhalten und WACS vergleichen, bevor eine eigene Runtime entsteht.
- [ ] .NET-10-WASI-C#-Probe als echte externe Dateien erzeugen; Importliste, Featureliste, Mono-/Runtimebestandteile und Größe dokumentieren. Arrays/Liste, virtuelle Methode, Exception, Allokation/GC, Dateioperation und Exit ausführen. `RunAOTCompilation` nur nach eigenem erfolgreichen Toolchainnachweis voraussetzen.
- [ ] Auf isolierter QEMU-Platte zehn Starts prüfen; App ändern und nur App neu bauen, erneut ausführen. Kernel-/ISOhash vorher/nachher identisch. Module werden tatsächlich von Platte gelesen; nichts appbezogen in Probe-Kernel einbetten.
- [ ] Dokumentieren: Resultate, RAM, nicht freigegebener Speicher, Abbruchzeit, native Stackgrenze und alle fehlenden Imports. Ein C-only-Nachweis ist kein C#-Erfolg. Bei Fehlschlag keine Folgephase ausführen; Alternativen mit Aufwand zur Entscheidung stellen.
- [ ] Hosttests soweit betroffen, nativen Normal-/Probe-/Recovery-Build und Gaststart prüfen. Auf VMware verfügbare Probe laufen lassen, andernfalls offen markieren. Geprüften Lieferstand separat committen.

**Gate A:** Externe C#-App funktioniert auf unverändertem Kernel mit den in der Spec genannten Funktionen und Ressourcen-/Abbruchgrenzen. Erst dann Aufgabe 2.

## Aufgabe 2: Doom-Leistung und Plattformadapter prüfen

**Dateien:** neu `experiments/Wasm.DoomProbe/README.md`, `experiments/Wasm.DoomProbe/platform/`, `docs/DOOM_WASM_TESTS.md`; Probe/Helfer aus Aufgabe 1 ergänzen. Fremdquellen kontrolliert aus festem Commit beziehen; keine WAD einchecken.

**Schnittstelle:** C-Doom-Probe verwendet `zehos_v1`-Imports für Zeit, Eingabe und Framepräsentation; Dateien nur über begrenzten WASI-Adapter. C-Port ist vom Nutzer akzeptiert.

- [ ] doomgeneric-Quellstand aus bestehendem `docs/DOOM_FEASIBILITY.md` übernehmen, Wasm-/C-Laufzeitbedarf und Engine-/Datenlizenzen getrennt prüfen. Benötigte C/Wasm-Werkzeuge exakt benennen, ohne Versionen des Kernels zu ändern.
- [ ] Erst festen 320×200-Testframe inklusive Kanalreihenfolge und Zeilenausrichtung prüfen; danach tatsächliches Level aus rechtmäßig bereitgestellter WAD laden.
- [ ] 60 Sekunden messen: mindestens 20 neue Spielbilder/s und 35 Spielticks/s bei festgehaltenem QEMU-Profil mit 512 MiB. Eingabe, Fokusverlust, F4/Esc, Schließen, zehn Neustarts und fehlende/beschädigte WAD prüfen. Kein Desktop-FPS als Spielmesswert verwenden.
- [ ] Resultat/Engpass und RAM protokollieren, ggf. eng begrenzte Portanpassung messen. Bei nicht erfülltem Ziel Architektur erneut entscheiden; WACS/native Alternative nicht als automatisch gleichwertigen Ersatz einbauen. Lieferstand committen.

**Gate B:** Doom tatsächlich spielbar und beendbar. Erst mit A+B Aufgaben 3–8 umsetzen; keine Aussage, dass Managed Doom damit automatisch portiert ist.

## Aufgabe 3: Begrenzter Loader und geprüfte Hostgrenze

**Dateien:** neu `ProjektZeh/Applications/ApplicationDescriptor.cs`, `WasmApplicationRuntime.cs`, `WasmHost.cs`, `ProjektZeh/Native/Wasm/` (jeweils im genannten Applications-Verzeichnis außer Native), `ProjektZeh.Tests/WasmHostTests.cs`; ändern `ProjektZeh/ProjektZeh.csproj`, `ProjektZeh.Tests/ProjektZeh.Tests.csproj`, `docs/COSMOS_SUPPORT.md`. Falls der Probeadapter wiederverwendbar ist, ihn verschieben statt duplizieren.

**Schnittstellen, vorgeschlagen:** `ApplicationDescriptor` enthält `Id`, `Version`, `EntryPath`, `RuntimeProfile`, `ManifestSha256`; `WasmApplicationRuntime.Start(ApplicationDescriptor app, UserSession session)` liefert `ResultObject<int>`; `Stop(int instanceId)` liefert `ResultErr`. IDs sind ausschließlich hostseitig. Pro Instanz eigener VM-Zustand; interner nativer C-Adapter mit expliziten create/run/cancel/destroy-Operationen und Statusrückgaben.

- [ ] Hosttests zuerst: `(uint.MaxValue, 8)` als Pointer/Länge, stride×height-Überlauf, fremde/geschlossene Handles, übergroßer Frame, unbekannter Import und nicht unterstütztes Runtimeprofil scheitern ohne Seiteneffekt. Grenzfälle exakt an den Spec-Limits testen.
- [ ] Loader wendet Modul-/Memory-/Gesamt-/Instanz-/Handlelimits aus Spec an, validiert bevor Startfunktionen laufen und aktiviert Gas vor Kompilierung. CPU-Gasverbrauch, Parser-/Metacode-RAM und C-Stack separat begrenzen; alle Alloc-/Reallocpfade erfassen. Nur nachgewiesenes Wasm-Profil zulassen.
- [ ] `WasmHost` mappt benötigte WASI-Funktionen auf kontrollierte Dateien und speichergeprüfte Kopien. Bei jedem Zugriff aktuelle Berechtigung und gebundene Benutzeridentität prüfen. Kernelobjekte/Zeiger niemals im Gast veröffentlichen. C#-Exceptions im Hostcallback fangen und in VM-Fehler übersetzen; native Statuswerte zurückübersetzen.
- [ ] Destruktion schließt Dateien/Queues und gibt native Speicherbereiche frei, auch bei Parse-/Start-/Hostfehlern. Abbruch und Shutdown erhalten Shellbedienbarkeit. Keine Fehler ausschließlich in `finally` behandeln, wo die bekannte Cosmos-Unwindgrenze dagegen spricht.
- [ ] Gastnegativtests aus Aufgabe 1 mit Produktloader wiederholen; zusätzlich verweigerte Datei, Logout mit offenem Handle, zweimaliges Stop und teilinitialisierte Instanz prüfen. Hosttests/nativer Build/Recovery/Gastprüfung ausführen und committen.

## Aufgabe 4: Desktop und Shell verwenden externe Apps

**Dateien:** neu `ProjektZeh/Applications/ApplicationCatalog.cs`, `ProjektZeh/Apps/Wasm/WasmApplication.cs`, `ProjektZeh/Shell/ApplicationShellCommands.cs`, `ProjektZeh.Tests/ExternalApplicationTests.cs`; ändern `Graphics/Desktop.cs`, `Kernel.cs`, `Shell/ShellService.cs`, Testprojekt und README.

**Schnittstellen:** `ApplicationCatalog.List()` liefert `ResultObject<ApplicationDescriptor[]>`; `Acquire(string id)` liefert eine hostseitige Versionsreferenz mit explizitem Release. `WasmApplication` implementiert das vorhandene `IApplication`; Shell `run <id>` startet denselben Katalogeintrag. Keine zweite Fensterverwaltung.

- [ ] Tests zuerst: unbekannte App, maximale Fenster/Instanzen, zweites Terminal, gesperrte Sitzung und fehlende Platte. Built-ins bleiben verfügbar, Fehler betreffen nur die betreffende App.
- [ ] Externe Apps dynamisch zur vorhandenen Liste ergänzen, UI aus IApplication bedienen. Interpreter im vorhandenen Hintergrunddienst, abgeschlossene Frames mit begrenztem Puffer an UI übergeben. Launcher nicht an Doom oder Paketnamen koppeln.
- [ ] Versionsreferenz gegen Remove/Upgrade schützen, Ressourcen nach Schließen freigeben. Fokusverlust/Logout löschen gehaltene Tasten und sperren Hostzugriffe; Recovery verwendet dieselbe Kataloglogik für CLI, ohne Grafik zu benötigen.
- [ ] Gast: zwei extern gebaute Apps, Doom, Terminaleingabe, Menü, F4/Esc, Logout und Rückkehr prüfen; keine Vollbildallokation pro Spielbild. Normal-/Recovery-Build und verfügbare VMware-Prüfung dokumentieren, committen.

## Aufgabe 5: Signiertes Paketformat und Hostwerkzeug

**Dateien:** neu `ProjektZeh/Packages/PackageManifest.cs`, `PackageCatalog.cs`, `PackageTrust.cs`, `scripts/build-package.py`, `ProjektZeh.Tests/PackageManifestTests.cs`, `PackageTrustTests.cs`, `docs/PACKAGE_FORMAT.md`; Testprojekt ergänzen.

**Schnittstellen:** `PackageManifest.Parse(byte[] bytes)` liefert `ResultObject<PackageManifest>`; `PackageCatalog.Parse(byte[] bytes)` liefert `ResultObject<PackageCatalog>`; `PackageTrust.Verify(byte[] content, byte[] signature)` liefert `bool`. Manifest-/Katalogserialisierung ist kanonisches UTF-8/LF; Signatur über exakt gespeicherte Bytes. Eigenen Paket-Vertrauensschlüssel konfigurieren, bestehende Kernelvertrauenswurzel nicht verändern.

- [ ] Tests zuerst für alle Spec-Pfad-/Kennungs-/Versionsregeln, 16-KiB-/128-Dateien-/128-MiB-Limits und Summenüberlauf. Namen `Doom.wasm`/`doom.wasm`, doppelte Felder, ungültiges UTF-8, leere/absolute/Traversalpfade und Entrypoint außerhalb der Dateiliste ablehnen.
- [ ] Signaturtest: richtige/falsche Signatur, ein verändertes Byte, falscher Schlüssel und falsche Manifesthashzuordnung. BouncyCastle 2.7.0 wiederverwenden. Vertrauenskatalog maximal 256 KiB und 128 Einträge; stabile Kennung/Version/Manifesthash/Generation enthalten.
- [ ] Hostwerkzeug erzeugt Manifest/Dateiverzeichnis ohne Archiv und ohne Skripte im Gast. Für Tests separaten temporären Schlüssel verwenden, niemals Testschlüssel in normale Builds übernehmen. Produktiver Herausgeberschlüssel bleibt vor Veröffentlichung eine Nutzerentscheidung.
- [ ] Format exakt dokumentieren, kanonische Roundtripfixtures prüfen, Hosttests ausführen und committen. Kein separater Interpreter- oder Kompressionsparser.

## Aufgabe 6: Lokale Installation, Zustandswechsel und Katalog

**Dateien:** neu `ProjektZeh/Packages/PackageService.cs`, `PackageStore.cs`, `PackageTransaction.cs`, `ProjektZeh.Tests/PackageInstallTests.cs`; ändern `Security/FileAccessControl.cs` nur an nötiger Reservierung/Verwaltung, `Applications/ApplicationCatalog.cs`, Testprojekt.

**Schnittstellen:** `PackageService.InstallLocal(string directory, UserSession session)` und `Remove(string id, UserSession session)` liefern `ResultErr`; `PackageStore.ReadInstalled()` liefert `ResultObject<PackageManifest[]>`; Katalog ermittelt aktive vollständig geprüfte Versionen aus Markern. Operationen mit versionsgebundener Referenz synchronisieren; keine separate widersprüchliche Installed-Datenbank.

- [ ] Tests zuerst: Erfolg, Nicht-Admin, fehlende Platte, zu wenig Platz, manipulierte Payload, ACL-Persistenzfehler, fehlgeschlagener Versionsmarker und Wiederherstellung. Bei jeder unterbrochenen Phase bleibt die alte geprüfte Version aktiv oder der Eintrag gesperrt.
- [ ] Neue Stagingverzeichnisse, Binärkopie mit gepuffertem SHA-256, immutable Versionsverzeichnis, ACLs und aktiven Marker in definierter Reihenfolge schreiben. `.zehos-packages` nur über internen kontrollierten Dienst zugänglich machen; normale geschützte Pfade nicht entsperren.
- [ ] Transaktionsprotokoll und Boot-Reconciliation prüfen; keine angebliche FAT32-Stromausfallgarantie. Vor Start Integrität der gespeicherten Dateien erneut prüfen. Physisch veränderte Dateien nicht starten, auch wenn früher installiert.
- [ ] Laufende/verwendete Version bei Upgrade/Remove ablehnen; Launch vs. Mutation unter gemeinsamer Referenzregel. Remove löscht ausschließlich eigene registrierte Inhalte/ACLs, behält Home-/Spielstände. ACL- und Dateibereinigungsfehler sichtbar erhalten.
- [ ] QEMU mit isolierter FAT32-Kopie: Installieren, Bootpersistenz, Aktualisieren, Entfernen, ENOSPC und Recovery. Kernelhash bleibt identisch. Hosttests/nativer Build ausführen und committen.

## Aufgabe 7: HTTPS-Repository und Paketbefehle

**Dateien:** neu `ProjektZeh/Packages/PackageRepository.cs`, `PackageJob.cs`, `ProjektZeh/Shell/PackageShellCommands.cs`, `ProjektZeh.Tests/PackageRepositoryTests.cs`, `PackageCommandTests.cs`; ändern `Network/CosmosNetworkService.cs`, `Network/TlsTransport.cs` nur für benötigte Binärstromfunktion, `Shell/ShellService.cs`, `Kernel.cs`, Testprojekt und README.

**Schnittstellen:** `PackageRepository.Refresh(CancellationToken cancellation)` liefert `ResultObject<PackageCatalog>`; `Download(PackageManifest package, string stagingDirectory, CancellationToken cancellation)` liefert `ResultErr`. Binärtransport separat von Text-`fetch` behandeln; bestehende Transport-/TLS-Implementierung wiederverwenden. PackageJob folgt dem bereits vorhandenen Netzwerkjob-Muster für Start/Status/Cancel.

- [ ] Tests zuerst: falsches Zertifikat/Host, HTTPS ohne Entropie/Zeit, ungültige Signatur, alter Katalog, Downgrade, Redirect auf fremde Quelle, zu großer/abgebrochener/unvollständiger Body, Hashfehler und gegenseitig konkurrierende Aufträge. Katalogstand nur nach Prüfung persistent erhöhen.
- [ ] Binärdownload mit festen Obergrenzen, Deadline und Abbruch in Staging schreiben; TLS-Validierung und Netzwerksperre erhalten. Keine 16-KiB-Textausgabe als Paketdatei verwenden und keine unbeschränkte Bytearray-Kopie. Keine ungeprüften Redirects.
- [ ] Befehle gemäß Spec über gemeinsame Shell registrieren. Installation/Upgrade/Remove/Quellenwechsel erneut autorisieren, Fehlermeldungen mit Ursache und Status ausgeben, Sitzungsschließen bricht Auftrag ab. `upgrade` meldet pro Paket Erfolg/Fehler; laufende App nicht überschreiben.
- [ ] Gast mit lokalem signierten HTTPS-Testrepository: zwei Pakete suchen/installieren, C# und Doom starten, Paketupdate, Entfernung und erneuter Startversuch, Abbruch plus weiter bedienbares zweites Terminal. Test-CA nur in Probe. Hosttests/nativer Build/Recovery prüfen, committen.

## Aufgabe 8: Fehlerfälle und Abschluss

**Dateien:** neu `docs/EXTERNAL_APPLICATION_TESTS.md`, `docs/PACKAGE_MANAGER_TESTS.md`; ändern `scripts/test-wasm-loader.py`, `README.md`, `docs/COSMOS_SUPPORT.md`, `ROADMAP.md` nur entsprechend wirklichem Abnahmestand.

- [ ] Fehlereinspritzung nach jeder Installationsphase, beschädigter Marker/ACL/Katalog, fehlendes Laufwerk, ENOSPC beim Restore und Start/Upgrade/Remove-Rennen als Hosttests. Keine fremden Dateien oder Benutzerhomes löschen. Eingangsfehler und Wiederherstellungsfehler gemeinsam berichten.
- [ ] Neustart während Staging/Veröffentlichung ausschließlich mit Testkopie prüfen; unklare Einträge sperren und geprüften Fallback erhalten. Persistenz und Recovery nach Reboot tatsächlich beobachten.
- [ ] Vollständige Hostprüfung: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`. Nativer Build: `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release`. Recovery zusätzlich: `dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSRecovery=true`. Normale ISO nach Probe ohne Probeeigenschaften neu bauen.
- [ ] QEMU-Endabnahme einschließlich unabhängiger Appänderung bei identischem Kernel, C#-Funktionsumfang, 60-Sekunden-Doommessung, Shell/Desktop/Eingabe/F4/Esc, Konto-/Dateirechte und Shutdown. VMware wiederholen, fehlende Werkzeuge/Datenträgerprofile ausdrücklich offen führen. Echte Hardware bleibt ungeprüft, sofern kein eigener Test stattfindet.
- [ ] Unabhängiges lesendes Review des fertigen Codes, Anforderungen und frischen Ergebnisse. Berechtigte Befunde beheben und betroffene Prüfungen wiederholen. Erst danach committen/pushen/PR gemäß AGENTS.md; keine Bots auslösen, kein Merge. Roadmap nur bei erfüllten Kriterien abhaken.

## Aktueller Prüfstand dieses Plans

Es wurden Projekt- und Primärquellen gelesen und die Dokumentation auf Konsistenz geprüft. Keine Interpreterinstallation, kein C#-/Wasm-/Doom-Build und keine Host-/Gasttests dieser vorgeschlagenen Architektur ausgeführt. Freigaben für neue Abhängigkeiten und Werkzeuge sowie Gate A/B fehlen noch. Der nächste Implementierungsauftrag wäre ausschließlich Aufgabe 1, nicht die gesamte Paketverwaltung auf Verdacht.
