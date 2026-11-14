# Unabhängige Anwendungen und Paketverwaltung: Entscheidungsentwurf

Stand: 14. November 2026. Recherche und Entwurf; weder Loader noch Interpreter wurden gebaut oder im Gast erprobt. Die Architektur ist eine Empfehlung, keine bereits bestätigte Cosmos-Kompatibilität. Der Auftrag umfasst diesen Entwurf und den [Umsetzungsplan](../plans/2026-11-14-loader-packages.md), keine Implementierung oder Werkzeuginstallation.

## Ziel und bewusste Grenzen

Ein unverändert gebauter ZehOS-Kernel soll später entwickelte Programme von einem Datenlaufwerk laden, starten, beenden und aktualisieren können. Paketinstallation verändert keinen Kernel und benötigt keinen Kernelbuild. C# bleibt Entwicklungssprache für Betriebssystem und Anwendungen. Der Nutzer hat ausdrücklich bestätigt, dass Doom selbst auch ein C-Port sein darf.

Empfehlung: WebAssembly als zunächst einziges externes Programmformat; ein vorhandener Interpreter wird einmal mit dem Kernel verbunden. Als ersten Kandidaten **wasm3** prüfen, Loader und Betriebssystemanbindung in C#. Doom als C/doomgeneric nach Wasm übersetzen. Für C#-Programme zuerst einen vollständigen .NET-10/WASI-Nachweis führen. Keine Aussage, dass gewöhnliche .NET-DLLs, Linux-ELFs oder vorhandene IApplication-Klassen unverändert funktionieren.

**Wesentliche Unsicherheit:** Die kombinierte Anforderung „C#-Apps und spielbares Doom unter Cosmos 3.0.94“ besitzt hier noch keinen geprüften Ausführungsweg. Vor dem eigentlichen Produktloader müssen beide Nachweise gelingen. Bei einem Fehlschlag endet die erste Phase mit einem dokumentierten Befund und einer neuen Entscheidung, nicht mit einem stillen Wechsel zu fest eingebauten Apps.

## Geprüfter Bestand

Arbeitsbasis: lokal vorhandenes `origin/master` bei `b51cb868072e8c00a3f79f4b3e4c83b26e2ea4b2`. Dies ist keine Behauptung über einen späteren Remote-Stand. Der ursprüngliche Arbeitsbaum auf `codex/desktop-settings` bleibt unberührt; Dokumentation entsteht in einer getrennten Arbeitskopie.

| Vorhandener Baustein | Nutzen und Grenze |
| --- | --- |
| `ProjektZeh/Apps/IApplication.cs`, `Graphics/Desktop.cs`, `Graphics/WindowManager.cs` | App-Lebenszyklus, Fenster, Eingabe und Darstellung wiederverwenden. Der Desktop besitzt bisher eine feste Anwendungsliste und direkte Konstruktoraufrufe. |
| `Graphics/PixelSurface.cs`, `Input/`, `Messaging/`, `Scheduling/BackgroundTaskService.cs` | Clipping, Eingaberesets, begrenzte Nachrichten und Hintergrundaufgaben übernehmen. Keine VM-Ausführung im UI-Thread; bestehendes Aufgabenlimit berücksichtigen. |
| `Storage/FileService.cs`, `Security/FileAccessControl.cs`, `Security/UserSession.cs` | Binärzugriffe, Ersatz/Wiederherstellung und sitzungsbezogene Rechte verwenden. Dateirechte sind keine native Prozessisolation; ACL-Persistenz ist Teil der Installation. |
| `Network/CosmosNetworkService.cs`, `Network/TlsTransport.cs` | TLS-Prüfung, Abbruch, Netzwerkserialisierung und Worker-Muster nutzen. `fetch` liefert bisher Text und hat ein Bodylimit von 16 KiB; es ist kein fertiger Paketdownload. |
| `Update/UpdateService.cs`, `Update/ManifestSignature.cs` | Signiertes Manifest und SHA-256 als Vorbild. Der Updatecontroller ist kernelbezogen. `CosmosHttpUpdateSource` ist HTTP und wird nicht als HTTPS-Pakettransport ausgegeben. |
| `Shared/ResultErr`, `ResultObject<T>`, `ResultObjectFactory`, `ResultCode` | Ein bestehender Ergebnisweg; Fehler nicht in parallelen versteckten Feldern ablegen. |

## Warum kein gewöhnlicher C#-DLL- oder ELF-Loader?

[NativeAOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) unterstützt kein gewöhnliches dynamisches Assemblyladen. [NativeAOT-Bibliotheken](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries) können native Exporte bereitstellen, setzen aber die passende Plattform/Laufzeit voraus; Entladen wird nicht unterstützt. Das ist keine freestanding Cosmos-App-Toolchain.

Am zu Cosmos 3.0.94 gehörenden Commit `5d0b9535251472db7ecfe012f0435f0a04ffca16` zeigen die Quellen konkrete Hindernisse:

- [ManagedModule.cs](https://github.com/CosmosOS/Cosmos/blob/5d0b9535251472db7ecfe012f0435f0a04ffca16/src/Cosmos.Kernel.Core/Runtime/ManagedModule.cs) initialisiert die beim Boot bekannten Modulheader, statische GC-Wurzeln und eingefrorene Objekte. Das ist keine öffentliche Load/Unload-API.
- [ModuleHelpers.cs](https://github.com/CosmosOS/Cosmos/blob/5d0b9535251472db7ecfe012f0435f0a04ffca16/src/Cosmos.Kernel.Core/Runtime/ModuleHelpers.cs) hält einen einzelnen OS-Modulwert; die Codeadresse-zu-Classlib-Auflösung ist unvollständig.
- [MethodGcInfoLookup.cs](https://github.com/CosmosOS/Cosmos/blob/5d0b9535251472db7ecfe012f0435f0a04ffca16/src/Cosmos.Kernel.Core/Runtime/GcInfo/MethodGcInfoLookup.cs) verwendet den globalen nativen `.eh_frame`-Bereich. Zusätzlicher AOT-Code braucht passende GC-/Unwindregistrierung; Relokationen allein reichen nicht.
- [Sdk.targets](https://github.com/CosmosOS/Cosmos/blob/5d0b9535251472db7ecfe012f0435f0a04ffca16/src/Cosmos.Sdk/Sdk/Sdk.targets) baut über den Cosmos-Patcher und eigenes ILC. Ein normales `dotnet publish -r linux-x64` liefert deshalb keine ZehOS-Anwendung.

Ein nativer C#-Modulweg würde Compiler-/Linkerarbeit, Laufzeitregistrierung, Objekt-/ABI-Regeln und Lebensdauerregeln erfordern. Das wäre ein eigenes Laufzeitprojekt mit enger Cosmos-Kopplung und ohne automatische Speicherisolation.

## Ansätze und Entscheidung

| Ansatz | Vorteil | Entscheidender Nachteil | Bewertung |
| --- | --- | --- | --- |
| Cosmos-bewusste native AOT-Module | Native Geschwindigkeit, C# als Quelle | Keine fertige Modul-API in 3.0.94; GC, Typen, Exceptions und Entladen ungelöst | Für den ersten Loader zurückstellen |
| CIL-Interpreter wie ILRuntime | Externe C#-DLLs prinzipiell interpretierbar | AOT-Bindings, Reflection, Hosttypen und Bibliotheksumfang; keine belastbare Sandbox; Doom-Leistung offen | Zweite Wahl bei zwingendem DLL-Format |
| WebAssembly mit vorhandenem Interpreter | Definiertes Dateiformat, explizite Hostfunktionen, mehrere Quellsprachen; keine dynamische Maschinenkodeerzeugung nötig | C#-WASI experimentell, Interpreterleistung und Cosmos-Port nachzuweisen | Empfohlene erste Untersuchung |
| Mono/CoreCLR direkt auf Cosmos portieren | Umfassendere .NET-Laufzeit denkbar | Sehr großer Plattformport, nicht bloß Loader | Außerhalb dieser Etappe |

Cosmos verfolgt selbst [Wasm-Ausführung in #3306](https://github.com/CosmosOS/Cosmos/issues/3306). Parallel existieren [Userspace #3271](https://github.com/CosmosOS/Cosmos/issues/3271) und [ELF-Loader #3358](https://github.com/CosmosOS/Cosmos/issues/3358). Die geprüften Issues sind offen; ihre Pläne sind kein Nachweis ausgelieferter Funktionen. Die aktuelle indizierte [Cosmos-Roadmap](https://github.com/CosmosOS/Cosmos/blob/77b0cd1fd57d61d431c3f3175835f9c05d2e82bf/docs/roadmap.md) bezeichnet Codeausführung als nicht begonnen. Die Entwürfe unterscheiden sich bei nativen Prozessen und VM-Grenzen; daraus wird keine verbindliche Cosmos-Zusage abgeleitet.

### Interpreterkandidaten und Abhängigkeiten

| Kandidat / untersuchter Stand | Lizenz und Abhängigkeiten | .NET 10 / NativeAOT / Cosmos |
| --- | --- | --- |
| [wasm3, Commit 28ecb9af6d2040e474a70f7cb7f43666740141fb](https://github.com/wasm3/wasm3/tree/28ecb9af6d2040e474a70f7cb7f43666740141fb) | MIT; C-Kern mit Allokations-, Speicher-, Mathematik- und ggf. Diagnosefunktionen. Eingebautes WASI/uvwasi und Plattformadapter nicht ungeprüft übernehmen. README nennt minimale Wartung. | Kein verwaltetes NuGet-Paket nötig; statisch über Cosmos-C-Toolchain anbinden. Native Imports, Stackbedarf, Abbruch und Gaslimit sind im Gast offen. Erster Kandidat wegen Doom und begrenzter Hostgrenze. |
| [WACS 0.16.14, Commit cad6530f5d5765f5de79ca77ed166b99802372ea](https://github.com/kelnishi/WACS/blob/cad6530f5d5765f5de79ca77ed166b99802372ea/Wacs.Core/Wacs.Core/Wacs.Core.csproj) | Apache-2.0; FluentValidation 11.10.0 und ObjectPool 9.0.0; zusätzlich Compiler-/Generator-/Fody-Buildabhängigkeiten | net8.0/netstandard2.1, net8.0 mit IsAotCompatible; auf .NET 10 ein Kandidat, Cosmos ungeprüft. Interpreter nutzen, keinen zur Laufzeit erzeugten C#-Code. Bekannte Cancellation-Prüfung an Funktionsgrenzen genügt allein nicht für eine Endlosschleife ohne Aufrufe. |
| [DotWasm](https://github.com/nuskey8/DotWasm) | MIT, getrennte Model-/Encoding-/Validation-/Runtime-Projekte, Alpha laut README | net10.0/net11.0 und Preview-Sprachstand im geprüften Runtimeprojekt; laut Projekt ohne dynamische Codeerzeugung. Kein Cosmos-Nachweis; nicht erste Wahl für stabile Grundlage. |
| [ILRuntime, Commit 482cb88c114d493f7acacf07e06f6b9e020b43f0](https://github.com/Ourpalm/ILRuntime/blob/482cb88c114d493f7acacf07e06f6b9e020b43f0/ILRuntime/ILRuntime.csproj) | MIT; u.a. eigener Mono.Cecil, DynamicExpresso und Roslyn-Referenzen | netstandard2.0/net8.0. AOT-Adapter erforderlich; weder .NET-10-/Cosmos-Gastnachweis noch Sicherheitsgrenze belegt. |

Es wird kein Interpreter selbst entwickelt. Erst Quellen und transitive Abhängigkeiten festschreiben und Lizenzhinweise mitführen. Bestehende SDK-, Cosmos- und Werkzeugversionen bleiben erhalten; ein notwendiger Versionswechsel braucht vorher die Nutzerentscheidung. Neue Werkzeuge und Laufzeitabhängigkeiten vor Einbau konkret benennen, nicht nebenbei installieren.

## Zwei zwingende Machbarkeitsprüfungen

### A: Loader und echte C#-App

Ein neuer Probe-Kernel erhält den Interpreter und einen schmalen C-Adapter. Er lädt das Programm ausschließlich vom isolierten Datenimage, nicht als eingebettete Ressource. Danach nur die App verändern und erneut bauen; Kernel und ISO müssen denselben SHA-256 behalten.

Zuerst kleines Wasm-Testmodul, dann eine echte C#-App aus .NET 10 mit `wasi-experimental`. Die [.NET-10-WASI-Dokumentation](https://github.com/dotnet/runtime/blob/release/10.0/src/mono/wasi/README.md) bezeichnet diesen Weg ausdrücklich als nicht unterstützt/experimentell. Sie beschreibt Mono-Interpretation; vollständiges AOT wird hier nicht vorausgesetzt. Browser-Wasm mit JavaScript-Startcode ist kein passendes Ersatzartefakt.

C#-Probe: Ausgabe, Liste/Array, virtuelle Methode, abgefangene Exception, Allokation/GC, Datei lesen/schreiben und sauberer Exit; zehn Starts ohne anhaltendes Speicherwachstum. Runtime und Assemblydateien dürfen Paketbestandteile sein; eine reine Demo in C erfüllt diesen Nachweis nicht. Erforderliche Wasm-Features und alle WASI-Imports am tatsächlichen Artefakt erfassen. Fehlende SIMD-/Threads-/EH-Funktionen nicht stillschweigend ignorieren. AOT ist nur nach realem Buildnachweis eine Optimierungsoption.

### B: Doom unter realistischen Bedingungen

Ein C-Port ist erlaubt. Als Kandidat den bereits untersuchten doomgeneric-Stand aus `docs/DOOM_FEASIBILITY.md` verwenden, Plattform-/libc- und Wasm-Buildanforderungen separat erfassen. Kein Windows-/Linux-Binary starten. Keine WAD herunterladen oder verteilen; rechtmäßig vorhandene lokale Daten oder gesondert lizenzierte Testdaten verwenden.

Vorgeschlagene Abnahme: 320×200 Softwarebild, 35 Spielticks/s, mindestens 20 tatsächlich neue Spielbilder/s über 60 Sekunden in derselben festgehaltenen QEMU-Umgebung; kein bloßer Desktop-FPS-Zähler. Tastatur, Fokusverlust, F4/Esc, Schließen und zehn erneute Starts prüfen. 512 MiB VM-RAM, CPU `max`, Geräteprofil und Messmethode protokollieren. VMware separat prüfen; ohne Werkzeug als offen führen.

Scheitert C#-Nachweis A oder Doom-Nachweis B, gibt es noch keine Freigabe für den Produktloader und Paketmanager. Dann Ergebnisse vorlegen: eng begrenzte Interpreteranpassung, WACS als Vergleich oder späterer nativer Userspace. Kein automatischer Cosmos-Wechsel und kein neuer Eigenbauinterpreter.

## Zielarchitektur nach bestandenen Prüfungen

```text
pkg / lokales Installieren -> signierte Metadaten -> geschützter Paketbestand
                                                        |
Shell run / Desktop -> ApplicationCatalog -> WasmApplication (IApplication)
                                                        |
                                              wasm3 pro App-Instanz
                                                        |
                          geprüfte ZehOS-/WASI-Hostfunktionen in C#
                                  Dateien | Zeit | Eingabe | Bildausgabe
```

- `IApplication` bleibt intern. Externe Apps erhalten keine verwalteten Kernelobjekte, Typzeiger oder Methodentabellen; nur Zahlen, Kopien und instanzgebundene Handles.
- Formatprofil zunächst wasm32, eine lineare Memory, keine Gastthreads, kein Netzwerkimport, kein dynamisches Linken. Zusätzliche für das nachgewiesene C#-Artefakt erforderliche Features ausdrücklich in den ABI-Stand aufnehmen.
- Probeobergrenzen: 64 MiB Modul, 128 MiB lineare Memory/Instanz, 256 MiB gesamte VM-bezogene Allokation, zwei Instanzen und 64 Datei-/Ressourcenhandles pro Instanz. Parser-, Compiler-/Metacode-, Tabellen- und native Stackallokationen mitzählen; ein Memory-Grow-Limit allein reicht nicht. Falls C# damit nicht läuft, vor Produktumsetzung Ressourcenprofil begründet anpassen oder Weg verwerfen.
- Gasbudget vor Kompilierung setzen; Endlosschleife ohne Hostaufrufe und tiefe Rekursion müssen kontrolliert enden. Abbruch spätestens nach 250 ms im Referenzgast, ohne Thread.Abort. Das ist ein Abnahmekriterium, keine aktuelle Garantie. Für langlaufende Apps Budget an definierten Tick-/Hostgrenzen erneuern, nicht unendlich deaktivieren.
- VM-Arbeit in vorhandener Hintergrundaufgabe. UI liest nur einen abgeschlossenen begrenzten Bildpuffer; Zeichnen/WindowManager bleiben im UI-Thread. Keine Vollbildallokation pro Frame. Eingabe mit Fokus-/Logout-Reset und begrenzter Queue.
- `zehos_v1`: Zeit, Eingabeabfrage und Präsentation eines 32-Bit-XRGB-Bildes; 320×200 zuerst. Pointer/Length, stride, Format und Multiplikationen vor Kopien prüfen. WASI Preview 1 nur für tatsächlich benötigte Datei-/Argument-/Zeit-/Exitfunktionen; keine ungeprüfte Standard-WASI-Hostimplementierung.
- Dateigrants: Paketinhalt nur lesbar, eigene Daten im Benutzerhome beschreibbar. Benutzeridentität beim Start binden; Logout, Kontolöschung oder Rechteentzug müssen weitere Zugriffe einschließlich offener Handles sperren und Instanzabbruch auslösen. Keine Wiederverwendung einer inzwischen anders angemeldeten Sitzung.
- Der Wasm-Interpreter bildet eine logische Grenze. Ein Bug in nativem Interpreter oder Hostadapter kann weiterhin den Kernel beschädigen. Keine Behauptung von Ring-3-/Prozessisolation.

## Paketverwaltung v1

`pkg update`, `pkg search <text>`, `pkg list`, `pkg info <id>`, `pkg install <id>`, `pkg install-local <verzeichnis>`, `pkg remove <id>`, `pkg upgrade [id]`, `pkg status`, `pkg cancel`. Installation/Entfernung/Quellenwechsel nur als Administrator; Lesen und Starten gemäß Sitzung und Datei-/Paketrechten.

Ein Paket ist zunächst ein signiertes UTF-8-Manifest plus einzelne unveränderliche Dateien. Kein ZIP-Decoder, keine Dekompressionsbomben, keine Installationsskripte. Offlineinstallation durchläuft dieselben Signatur-/Hash-/Rechteprüfungen. Module führen bei Installation keinen Code aus, auch keine Startfunktionen.

Manifestprofil 1: Kennung `[a-z0-9][a-z0-9-]{0,47}`, Version mit drei nichtnegativen 32-Bit-Komponenten ohne Präfixe, Runtimeprofil `zehos-wasm32-v1`, Entrypoint, deklarierte Hostrechte und Dateiliste mit relativem Pfad/Bytezahl/SHA-256. Maximal 16 KiB Manifest, 128 Dateien, 128 MiB Gesamtinhalt. Für jede Datei ASCII-Pfadkomponenten aus Buchstaben, Ziffern, Punkt, Unterstrich und Bindestrich; Slash als Trenner; keine leeren Komponenten, `.`/`..`, absoluten Pfade, Backslashes, Doppelpunkt, NUL oder abschließenden Punkte/Leerzeichen. Duplikate und FAT32-Groß-/Kleinschreibkollisionen ablehnen. Entrypoint muss genau eine gelistete Wasm-Datei sein.

Der signierte Katalog bindet Manifesthash, Kennung und Version und trägt eine monoton steigende Generation. Bereits angenommene höhere Generation und installierte Version bilden eine lokale Rückstufungssperre. Keine volle Frischegarantie bei einer Neuinstallation ohne bekannten Katalogstand. Ed25519 und SHA-256 mit vorhandener BouncyCastle-Abhängigkeit nutzen; **eigener Paket-Vertrauensschlüssel**, nicht ungefragt der Kernelupdateschlüssel. Vor echter Veröffentlichung muss der Nutzer den Herausgeber und dessen öffentlichen Schlüssel festlegen; keine privaten Schlüssel ins Repository oder in Logs.

Speicherung unter dem bestehenden Mount: `/data/apps/<id>/<version>/`, intern geschütztes Verwaltungsverzeichnis `/data/.zehos-packages/`; Nutzerdaten unter dem tatsächlichen Home in `apps/<id>/`. Den internen Pfad explizit für den Verwaltungsdienst reservieren; die normale FileService-Sperre für `.zehos-` nicht global aufweichen. Beim Boot keinen Datenträger formatieren.

Ablauf: Download/Offlinekopie in neues Staging, alle Inhalte prüfen, ACLs persistent eintragen, dann einen kleinen aktiven Versionsmarker über die vorhandene Ersatz-/Sicherungslogik veröffentlichen. Unvollständige Versionen sind niemals startbar. Katalog und Desktop werden erst nach erfolgreicher Veröffentlichung aktualisiert. Ein persistentes Transaktionsprotokoll ermöglicht Wiederaufnahme/Bereinigung; Marker plus gehashter Manifeststand ist maßgeblich, keine zweite unabhängig beschreibbare Installed-Datenbank. Fehlgeschlagene ACL-Persistenz sperrt Veröffentlichung. FAT32 liefert dadurch keine zugesagte Stromausfallatomizität; beim nächsten Start unklare Zustände sperren und letzte vollständig geprüfte Version anbieten.

Pro Paket nur eine Mutation; `upgrade` bearbeitet Pakete einzeln und meldet Teilerfolge. Laufende Versionen bleiben unverändert; Update/Entfernen eines laufenden Pakets in v1 ablehnen. Beim Start sichern Registry und Launch eine Versionsreferenz, sodass Remove/Upgrade nicht dazwischen löschen kann. `remove` entfernt ausschließlich registrierte Paketdateien/ACLs und lässt Benutzerdaten stehen. Keine geteilten Bibliothekspakete, kein SAT-Solver: Apps liefern ihre privaten Laufzeitdateien mit und benötigen nur ein unterstütztes Runtime-/Hostprofil. Doom-WAD-Rechte bleiben getrennt vom Enginepaket.

HTTPS-Binärdownload muss begrenzt und abbrechbar sein; Netzwerksperre und bestehende Zertifikatsprüfung erhalten. Dateikopien gepuffert in Staging, SHA-256 währenddessen; keine ganzen 128-MiB-Pakete zusätzlich im RAM sammeln. Einheitliche Prüfung erneut beim Laden der gespeicherten Programmdatei, da Signaturprüfung beim Download spätere Änderungen nicht ausschließt.

## Umsetzung, Aufwand und Abnahme

Reihenfolge: Interpreter-/Toolchainprobe und C# -> Doom -> begrenzter Produktloader und Hostfunktionen -> lokales signiertes Installieren -> HTTPS-Katalog und CLI -> Fehlersicherheit und beide VMs. Der [Plan](../plans/2026-11-14-loader-packages.md) macht daraus prüfbare Lieferstände.

Grobe Planungsgröße, keine Zusage: Machbarkeit mehrere Arbeitstage; belastbarer Loader mit Hostanbindung mehrere Wochen; Paketverwaltung danach eine weitere größere Funktion. C#-WASI- oder Cosmos-Portprobleme können den Aufwand dominieren. Erst nach den Messungen seriöser enger schätzen.

Gesamtabnahme: Ohne Änderung des Kernels zwei nachträglich gebaute Apps installieren und starten; eine davon C#, zusätzlich spielbares Doom; Upgrade/Remove samt Fehlerfällen; Shell, Desktop, Konten und Recovery bleiben bedienbar. QEMU, VMware und fehlende Plattformnachweise separat dokumentieren. Keine Roadmapetappe allein durch diesen Entwurf abhaken.
