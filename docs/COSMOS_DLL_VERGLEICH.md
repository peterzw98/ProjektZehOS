# Cosmos-DLLs vergleichen (Paket E1-D)

`scripts/compare-cosmos-dlls.cs` prüft, ob ein Cosmos-Bau aus dem Branch `cosmos` dieselben Assemblies liefert wie eine
Vergleichsquelle, etwa die NuGet-Pakete 3.0.97 oder ein zweiter Bau desselben Commits. Es braucht nur das vorhandene
.NET-10-SDK und läuft als dateibasierte App:

```bash
dotnet run scripts/compare-cosmos-dlls.cs -- <A> <B> [--show <n>]
```

`A` und `B` sind Ordner mit gleich aufgebauten Bäumen, verglichen wird jede `*.dll` gleichen relativen Pfads. Für die
NuGet-Pakete eignet sich ein Ordner mit je einem Unterordner pro Paket-ID, kopiert aus `~/.nuget/packages/<id>/<version>/`;
für den Quellbau dieselbe Gliederung mit dem entpackten Inhalt der `*.nupkg`.

## Urteile

| Urteil | Bedeutung |
| --- | --- |
| `gleich` | bytegleich |
| `bauangaben` | gleich nach Maskieren von COFF-Zeitstempel, PE-Prüfsumme, Debug-Verzeichnis (PDB-Pfad, -GUID, -Prüfsumme), MVID und dem Quell-Commit `+<40 Hex-Zeichen>` (UTF-8 in `AssemblyInformationalVersion`, UTF-16 in der Win32-Versionsressource) |
| `IL-gleich` | gleiche Typen, Felder, Methoden, Methodenkörper (IL mit aufgelösten Token, Lokale, Ausnahmebereiche), Attributtexte und Assemblyverweise; nur Reihenfolge oder Nummern der Metadaten weichen ab |
| `VERSCHIEDEN` | mit Hinweisen: abweichende Attributtexte, Verweise, Felder, Methoden und die erste abweichende IL-Zeile je Methode |

Ende 0, wenn nichts `VERSCHIEDEN` ist und beide Seiten dieselben DLLs haben.

## Voraussetzungen für einen Vergleich mit den NuGet-Paketen

- **PathMap:** Die NuGet-DLLs enthalten Quellpfade unter `/home/runner/work/Cosmos/Cosmos/…` (etwa aus `CallerFilePath`).
  Ohne `-p:PathMap=<Quellbaum>=/home/runner/work/Cosmos/Cosmos` stehen stattdessen lokale Pfade in den DLLs.
- **Commit:** Ein Bau aus dem Branch `cosmos` trägt dessen Commit statt `efcf20c` in der Version; deshalb ist
  Bytegleichheit nicht zu erwarten, sondern höchstens `bauangaben`.
- **SDK:** Der Quellbaum verlangt SDK 10.0.201 oder neuer (`global.json`, `rollForward: latestFeature`). Gleichstand mit
  den NuGet-Paketen setzt dasselbe SDK voraus wie deren Bau.

## Erster Befund (18.11.2026, HPz)

Bau von `cosmos` bd5889b mit SDK 10.0.201 gegen NuGet 3.0.97: 23 DLLs `gleich`, 21 `bauangaben`, 3 `VERSCHIEDEN`
(`Cosmos.Kernel.Core`, `Cosmos.Kernel.Drivers`, `Cosmos.Kernel.System`). Alle Abweichungen passen zu einem anderen
Compiler: anderer `GeneratedCode`-Stand des Quellgenerators (10.0.15.2326 gegen 10.0.14.15411) und in elf Methoden mit
`Span`-Code andere Stackgrößen und Lokale. 3.0.97 erschien am 1. 11. 2026; das damals neueste SDK war 10.0.403. Dass
Cosmos mit diesem SDK gebaut hat, ist eine Vermutung, nicht geprüft.

Gegenprobe auf Peters Rechner (jensen, SDK 10.0.404, `--version 3.0.97`, 64 s): 54 `gleich`, 26 `bauangaben`,
1 `VERSCHIEDEN`. Die eine Abweichung ist nur der `GeneratedCode`-Text in `Cosmos.Kernel.Core` (10.0.15.2326 gegen
10.0.15.3008), kein IL-Unterschied. Mit SDK 10.0.404 liefert der Quellbau also denselben Code wie die NuGet-Pakete;
`deploy/cosmos/SDK` nennt deshalb 10.0.404.

Paketlayout `Cosmos.Build.Analyzer.Patcher`: Im Quellstand trägt das Hüllprojekt
`src/Cosmos.Build.Analyzer.Patcher.Package` diese Paket-ID (mit `build/*.props` und den Analyzern unter
`analyzers/dotnet/cs/`), die Analyzer-Bibliothek selbst heißt `_Cosmos.Build.Analyzer.Patcher`. Das NuGet-Paket gleichen
Namens enthält dagegen nur die Bibliothek unter `lib/netstandard2.0/` mit anderen Abhängigkeiten. Der Quellbau folgt den
Projektdateien; ob der Patcher-Analyzer im Kernelbau damit gleich wirkt, zeigt erst der Vergleich der Build-Logs
(Analyzer-Meldungen, `check-plug-binding.py`).

Reproduzierbarkeit: derselbe Commit, zweimal gebaut in zwei Arbeitsbäumen unter verschiedenen Pfaden, je mit eigenem
NuGet-Cache und gleicher PathMap: 85 DLLs, davon 54 `gleich` und 31 `bauangaben`, keine `VERSCHIEDEN`; kein lokaler Pfad
in einer DLL.

`monodis` eignet sich für diesen Vergleich nicht: Es lässt Methoden neuerer Assemblies still aus und hätte die
Abweichungen in `Cosmos.Kernel.Drivers` und `Cosmos.Kernel.System` übersehen.
