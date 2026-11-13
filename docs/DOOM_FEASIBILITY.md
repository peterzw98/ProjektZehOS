# Doom-Vorversuch (Roadmap 0.4)

Stand: 13. November 2026. Untersucht auf ZehOS-Basis `7264b34`, mit SDK **10.0.112**. Dieser Versuch verändert weder Kernel noch Cosmos-Pakete und benötigt keine WAD-Datei.

## Entscheidung für den nächsten Versuch

**Managed Doom zuerst weiter untersuchen.** Spielcode und Software-Renderer lassen sich bereits ohne die Desktopbibliotheken unter .NET 10 kompilieren. Einen eigenen Doom-Spielkern zu entwickeln ist damit nicht begründet. Dies ist eine Kandidatenbewertung für 0.4; die endgültige Auswahl und Kernelintegration aus 9.1 sind noch offen.

Geprüfter [Managed-Doom-Quellstand](https://github.com/sinshu/managed-doom/tree/9365696eb44326a3aab72c4bab217f7db8a87c96): `9365696eb44326a3aab72c4bab217f7db8a87c96`. Die Probe bindet dessen C#-Quellen außerhalb von `src/Silk/` unverändert ein. Fremdquellen, Binärdateien und Spieldaten werden nicht ins ZehOS-Repository übernommen.

## Trennbare Bausteine und verbleibende Grenzen

| Bereich | Quellbefund und Folgerung für ZehOS |
| --- | --- |
| Spiel und Rasterisierung | `src/Doom/` enthält Spielzustand, Weltlogik und WAD-Zugriff; `src/Video/` den Software-Renderer. Diese Verzeichnisse kompilieren ohne zusätzliche Desktoppakete. Nicht alle enthaltenen Klassen sind plattformunabhängig: Datei- und Zeitaufrufe bleiben enthalten. |
| Grafik | `IVideo` ist die vorhandene Grenze. `Renderer.Render(Doom, byte[], Fixed)` schreibt farbige Pixel; intern liegt ein palettenindizierter, spaltenweise angeordneter Puffer vor. Die spätere Übergabe an `PixelSurface` muss Orientierung und Farbkanäle ausdrücklich prüfen und einen festen Puffer wiederverwenden. |
| Eingabe | `IUserInput`, `DoomEvent` und `TicCmd` übernehmen. Ein ZehOS-Adapter muss Tastenzustände und Fokus an die vorhandene Eingabe anbinden und gehaltene Tasten beim Fokusverlust zurücksetzen. |
| Audio | `ISound`/`IMusic` und vorhandene `NullSound`/`NullMusic` erlauben einen ersten Versuch ohne Audio. Kein eigener Ersatz für diese Nullimplementierungen nötig. |
| Zeit | `GameConst.TicRate` beträgt 35. Die Desktop-Hauptschleife steckt im ausgeschlossenen Silk-Adapter. Später Spieltakt und Darstellung getrennt an die ZehOS-Uhr anbinden. `DateTime.Now` in `QuitConfirm`/`WipeEffect` und `Stopwatch` in `DemoPlayback` bleiben zu prüfen. |
| Dateien | `Wad` öffnet `FileStream`, verwendet Seek und hält Streams bis Dispose offen. Konfiguration, Demos und Spielstände greifen ebenfalls direkt auf Dateien zu. Der vorhandene begrenzte Text-`FileService` ersetzt keinen binären WAD-Zugriff. Größen, Offsets und fehlerhafte Daten vor Allokationen begrenzen; Einlesen, Fehler und Freigabe im Gast prüfen. |
| Hostannahmen im Kern | `ConfigUtilities.GetExeDirectory()` verwendet `Process.GetCurrentProcess().MainModule.FileName`, die WAD-Suche außerdem das globale Arbeitsverzeichnis. Diese Logik durch explizite ZehOS-Pfade ersetzen, statt Host-Prozesssemantik vorauszusetzen. Shellausgaben des Quellcodes müssen zum Anwendungsfenster passen. |
| Speicher/Laufzeit | Arrays, Collections, LINQ, Exceptions, Streams und `MemoryMarshal` bleiben erforderlich. Ihr erfolgreicher Hostbuild bestätigt weder Cosmos-Plugs noch Speicherbedarf, GC-Verhalten oder Laufzeit im Gast. |

## Pakete und Lizenz

Das [Upstream-Projekt](https://github.com/sinshu/managed-doom/blob/9365696eb44326a3aab72c4bab217f7db8a87c96/ManagedDoom/ManagedDoom.csproj) zielt bereits auf `net10.0` und setzt `PublishAot=true`. Es referenziert DrippyAL **2.1.3**, MeltySynth **2.4.1**, Silk.NET.Input.Glfw und Silk.NET.Windowing.Glfw jeweils **2.22.0** sowie TrippyGL **1.2.2**. Laut den mitgelieferten Lizenzdateien stehen diese verwalteten Bibliotheken unter MIT; GLFW unter zlib/libpng, OpenAL Soft unter LGPL. Die Desktopanbindung benötigt Grafik-/Fenster- und Audiobibliotheken des Hostsystems. Dafür ist keine Cosmos-Anbindung nachgewiesen; sie werden nicht in den Kernel übernommen. Die Probe besitzt keine eigenen `PackageReference`-Einträge; die SDK-Analysepakte sind davon unabhängig.

Managed Dooms Quellheader erlauben GPL Version 2 oder später; der [Lizenztext](https://github.com/sinshu/managed-doom/blob/9365696eb44326a3aab72c4bab217f7db8a87c96/licenses/LICENSE_ManagedDoom.txt) liegt bei. Vor einer Integration und Weitergabe sind die Lizenzbedingungen des verbundenen Gesamtwerks zu prüfen. WAD-Spieldaten haben getrennte Rechte. Hier werden weder WAD noch Soundfont heruntergeladen oder verteilt.

## Reproduzierbarer Hostbuild

Vom Repository-Stamm, mit Git, Netzwerk für den Quellabruf und einem zum `global.json` passenden .NET-10-SDK. Einen neuen Zielordner außerhalb des Repositorys verwenden; hier ist `/tmp/zehos-managed-doom` das Beispiel. Vorhandene Arbeitskopien nicht überschreiben.

```sh
git clone https://github.com/sinshu/managed-doom.git /tmp/zehos-managed-doom
git -C /tmp/zehos-managed-doom checkout --detach 9365696eb44326a3aab72c4bab217f7db8a87c96
git -C /tmp/zehos-managed-doom status --short
git -C /tmp/zehos-managed-doom rev-parse HEAD
dotnet --version
dotnet build experiments/ManagedDoom.CoreProbe/ManagedDoom.CoreProbe.csproj \
  --configuration Release -warnaserror \
  -p:ManagedDoomSource=/tmp/zehos-managed-doom
```

`ManagedDoomSource` ist ein absoluter Pfad zur sauberen Arbeitskopie am genannten Commit. Ohne gültigen Quellpfad bricht das Projekt mit einer gezielten Fehlermeldung ab. Es gehört absichtlich nicht zur Kernel- oder Testprojektabhängigkeit und wird nur ausdrücklich gebaut. Stilanalysen für die unveränderten Fremdquellen sind in diesem Projekt deaktiviert; Compiler sowie die durch `IsAotCompatible` eingeschalteten Trim-/AOT-Analysen bleiben aktiv.

**Tatsächliches Ergebnis:** Build der vollständigen oben beschriebenen Quellauswahl erfolgreich, **0 Warnungen / 0 Fehler**. Ohne Quellparameter schlägt der Build wie vorgesehen fehl. Der erste Restore scheiterte an fehlendem NuGet-Netzzugriff (`NU1900`); ein erneuter Restore mit Netzwerkzugriff und anschließendem Build gelang. Kein Unterdrücken der Auditwarnung erforderlich.

Das Ergebnis ist eine verwaltete Hostbibliothek. Es wurde kein nativer AOT-Link ausgeführt und keine Spielfunktion gestartet. Ein vollständiger Upstream-Desktopbuild war nicht Gegenstand der Probe. Cosmos-NativeAOT-Build, WAD-Laden, Bildausgabe, Takt und Eingabe in QEMU und VMware sind offen. Die `IsAotCompatible`-Analyse ersetzt diese Prüfungen nicht.

## Alternative doomgeneric

Verglichen wurde [doomgeneric am Commit `e257a09527062bfb986b6e5e0780839b6cbd5b71`](https://github.com/ozkl/doomgeneric/tree/e257a09527062bfb986b6e5e0780839b6cbd5b71). Der Header bietet sechs Plattformfunktionen für Initialisierung, Bildausgabe, Schlafen, Zeit, Tasten und Fenstertitel. Das Standard-Makefile baut jedoch eine Unix-/X11-Anwendung mit C-Laufzeit und Mathematikbibliothek. Im Spielpfad bleiben unter anderem `fopen`, `malloc` und `exit`; diese sind zusätzlich zu den sechs Funktionen anzubinden oder zu ersetzen. Die GPLv2-Lizenz liegt bei.

Für ZehOS kämen C-Toolchain, ABI, native Verknüpfung sowie Datei-, Speicher- und Fehlerpfade hinzu. Deshalb vorerst zweite Wahl. Es wurde kein doomgeneric-Build durchgeführt; eine .NET-/NativeAOT-/Cosmos-Kompatibilität wird nicht behauptet.

## Nächster begrenzter Nachweis

Nach den vorgesehenen Vorstufen aus der Roadmap: vorhandene Managed-Doom-Schnittstellen in einem getrennten opt-in Cosmos-Build mit Nullaudio verwenden, einen expliziten binären WAD-Pfad anbinden und zunächst nur ein Bild darstellen. Dafür einen rechtmäßig verfügbaren WAD-Stand festhalten. Erst nach Gastnachweis folgen Spieltakt, Fokus, Abbruch und wiederholter Start; die aktuellen Scheduling-Arbeiten werden durch diese Probe nicht verändert.
