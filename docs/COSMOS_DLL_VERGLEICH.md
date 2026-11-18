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
| `IL-gleich` | nur Reihenfolge oder Nummern der Metadaten weichen ab; gleich sind Typen (Flags, Basistyp, Interfaces, Layout), Felder (Flags, `FieldOffset`, Konstante, Anfangsdaten per FieldRVA), Methoden (Flags, ImplAttributes, Aufrufkonvention, `DllImport`, Körper als IL mit aufgelösten Token samt Lokalen und Ausnahmebereichen), Attributtexte, Assemblyverweise und eingebettete Ressourcen. Nicht verglichen: Constraints generischer Parameter, Events, Properties. Anfangsdaten, deren Länge sich nicht aus der Feldsignatur ergibt, zählen als Hinweis und damit als `VERSCHIEDEN` (in den DLLs von 3.0.97: keine von 135 Feldern) |
| `VERSCHIEDEN` | mit Hinweisen: abweichende Attributtexte, Verweise, Felder, Methoden und die erste abweichende IL-Zeile je Methode |

Ende 0, wenn nichts `VERSCHIEDEN` ist und beide Seiten dieselben DLLs haben.

## Voraussetzungen für einen Vergleich mit den NuGet-Paketen

- **PathMap:** Die NuGet-DLLs enthalten Quellpfade unter `/home/runner/work/Cosmos/Cosmos/…` (etwa aus `CallerFilePath`).
  Ohne `-p:PathMap=<Quellbaum>=/home/runner/work/Cosmos/Cosmos` stehen stattdessen lokale Pfade in den DLLs.
- **Commit:** Ein Bau aus dem Branch `cosmos` trägt dessen Commit statt `efcf20c` in der Version; deshalb ist
  Bytegleichheit nicht zu erwarten, sondern höchstens `bauangaben`.
- **SDK:** Der Quellbaum verlangt SDK 10.0.201 oder neuer (`global.json`, `rollForward: latestFeature`). Gleichstand mit
  den NuGet-Paketen setzt dasselbe SDK voraus wie deren Bau.

## Erster Befund (18.11.2026, Linux-Entwicklungsrechner mit KVM)

Bau von `cosmos` bd5889b mit SDK 10.0.201 gegen NuGet 3.0.97 (Ende 1): 23 DLLs `gleich`, 21 `bauangaben`, 3 `VERSCHIEDEN`, nur in A 6 (fünf linux-arm64-DLLs, gewollt nur x64 gebaut, und `lib/netstandard2.0/Cosmos.Build.Analyzer.Patcher.dll`), nur in B 38 (vier DLLs der Analyzer-Hülle, dazu 21 aus `Cosmos.Patcher` und 13 aus `Cosmos.Tools`, die als Werkzeugpakete nicht im NuGet-Cache liegen und deshalb in A fehlten)
(`Cosmos.Kernel.Core`, `Cosmos.Kernel.Drivers`, `Cosmos.Kernel.System`). Alle Abweichungen passen zu einem anderen
Compiler: anderer `GeneratedCode`-Stand des Quellgenerators (10.0.15.2326 gegen 10.0.14.15411) und in elf Methoden mit
`Span`-Code andere Stackgrößen und Lokale. 3.0.97 erschien am 1. 11. 2026; das damals neueste SDK war 10.0.403. Dass
Cosmos mit diesem SDK gebaut hat, ist eine Vermutung, nicht geprüft.

Gegenprobe auf Peters Rechner (jensen, SDK 10.0.404, `--version 3.0.97`, 64 s, Ende 1): 54 `gleich`, 26 `bauangaben`,
1 `VERSCHIEDEN`, nur in A 6, nur in B 4 (dieselbe Analyzer-Hülle; Werkzeugpakete lagen dort auf beiden Seiten vor). Die eine Abweichung ist nur der `GeneratedCode`-Text in `Cosmos.Kernel.Core` (10.0.15.2326 gegen
10.0.15.3008), kein IL-Unterschied. Mit SDK 10.0.404 liefert der Quellbau also denselben Code wie die NuGet-Pakete;
`deploy/cosmos/SDK` nennt deshalb 10.0.404.

Paketlayout `Cosmos.Build.Analyzer.Patcher`: Im Quellstand trägt das Hüllprojekt
`src/Cosmos.Build.Analyzer.Patcher.Package` diese Paket-ID (mit `build/*.props` und den Analyzern unter
`analyzers/dotnet/cs/`), die Analyzer-Bibliothek selbst heißt `_Cosmos.Build.Analyzer.Patcher`. Das NuGet-Paket gleichen
Namens enthält dagegen nur die Bibliothek unter `lib/netstandard2.0/` mit anderen Abhängigkeiten. Der Quellbau folgt den
Projektdateien; ob der Patcher-Analyzer im Kernelbau damit gleich wirkt, zeigt erst der Vergleich der Build-Logs
(Analyzer-Meldungen, `check-plug-binding.py`).

Kernelbau alt gegen neu (18.11.2026, derselbe Rechner, beide Seiten SDK 10.0.404 und eigener NuGet-Cache, damit nur die
Cosmos-Pakete verschieden sind): alt `master` 6ec4e93 mit NuGet 3.0.97 und den global installierten Werkzeugen 3.0.97,
neu E1-B (41470b4, wiederholt auf ae7af03) mit `scripts/build-cosmos.sh`, Feed 3.0.97-zehos.1 und Werkzeugen per
`--tool-path`. Beide Seiten: `cosmos build … --verbose` Ende 0, `check-plug-binding.py` „11 ZehOS-Plugs gebunden“ mit
gleicher Liste, Hosttests mit `-warnaserror` 1488/1488, ISO 25 063 424 Byte, ELF 21 109 600 beziehungsweise 21 109 536
Byte (die ZehOS-Version aus Git ist je Branch anders lang), keine Compiler- oder Analyzer-Warnung. Der Patcher-Analyzer
wird auf beiden Seiten geladen. Die alte Seite meldet zwölf NuGet-Warnungen „Expected package
_Cosmos.Build.Analyzer.Patcher 3.0.97, but got package Cosmos.Build.Analyzer.Patcher 3.0.97“: nuget.org liefert unter
der ID mit Unterstrich das andere Paket aus, daher auch das abweichende Layout oben. Der eigene Feed hat das nicht.
Gaststarts: siehe „Gastzeilen alt gegen neu“ unten; Doom und VMware sind noch offen.

Reproduzierbarkeit (Ende 0): derselbe Commit, zweimal gebaut in zwei Arbeitsbäumen unter verschiedenen Pfaden, je mit eigenem
NuGet-Cache und gleicher PathMap: 85 DLLs, davon 54 `gleich` und 31 `bauangaben`, keine `VERSCHIEDEN`; kein lokaler Pfad
in einer DLL.

`monodis` eignet sich für diesen Vergleich nicht: Es lässt Methoden neuerer Assemblies still aus und hätte die
Abweichungen in `Cosmos.Kernel.Drivers` und `Cosmos.Kernel.System` übersehen.

## Gastzeilen alt gegen neu (18.11.2026, Peters Rechner, QEMU/KVM bzw. TCG)

**Stände:** Verglichen wurden zwei Kernelstände, die sich nur im Cosmos-Bau unterscheiden; dazwischen ändern sich nur
Bau- und CI-Dateien, keine Kernelquelle.

- **alt:** `1e9a5aa` (`master` direkt vor E1-B, NuGet 3.0.97, global installierte Werkzeuge 3.0.97).
- **neu:** `100a55f` (Merge von E1-B, Feed 3.0.97-zehos.1 aus `cosmos` bd5889b, Werkzeuge je Arbeitskopie).

**Builds:** Je Seite sieben Builds, alle mit 0 Warnungen und 0 Fehlern: normal, `ZehOSRecovery`, `ZehOSThreadProbe`,
`ZehOSIsolationProbe`, `…Negative`, `…Nmi`, `ZehOSStorageProbe`. Die ELF-Größen sind je Build auf beiden Seiten
bytegenau gleich, etwa normal 21 109 600 Byte und Storage-Probe 21 414 544 Byte.

**Inhalt:** Die ELFs sind nicht bytegleich. Die ersten Abweichungen in `.rodata` sind 16-Byte-GUIDs (MVIDs). Der
größte Block (654 KB) ist derselbe Inhalt, um wenige Byte verschoben. Vermutung, nicht geprüft: Das sind die längeren
Versionstexte des Eigenbaus (`3.0.97-zehos.1+bd5889b…` statt `3.0.97+efcf20c…`).

| Fall | Befehl | alt | neu |
| --- | --- | --- | --- |
| Desktop KVM | `test-desktop-guest.py --accel kvm` | Erfolg (42 s) | Erfolg (41 s) |
| Desktop TCG | `test-desktop-guest.py --accel tcg` | Erfolg (82 s) | Erfolg (80 s) |
| Recovery | `test-desktop-guest.py --recovery --accel kvm` | Erfolg | Erfolg |
| Threads | `test-thread-probe.ps1`, nach Python übertragen (TCG) | `THREAD_PROBE PASS` | `THREAD_PROBE PASS` |
| Isolation | `test-isolation-probe.py --accel kvm --runs 5` | 5/5 | 5/5 |
| Isolation negativ | `… --negative` | 1/1 | 1/1 |
| Isolation NMI | `… --nmi`, je 4 Läufe | 1/4, sonst „Gegenprobe ohne Aussage“ | 0/4, jeweils „Gegenprobe ohne Aussage“ |
| ext2 | `test-storage-ext2-guest.py --accel kvm` | Lauf 1 PASS=20, Lauf 2 PASS=9, `DONE` | gleich |
| Energie | `reboot` in derselben VM, dann `shutdown` (KVM) | beide PASS, Exit 0 | beide PASS, Exit 0 |
| Doom | `test-wasm-doom-desktop.py --scenario quick` | offen | offen |
| VMware | `start-vmware.ps1` | offen | offen |

**Anmerkungen:**

- **Isolation NMI:** Kein Hinweis auf eine Abweichung durch den Cosmos-Bau.
  - Beide Stände enthalten die LINT1-Freischaltung der Probe (#265). Die NMI kommt in jedem Lauf an
    („CPU EXCEPTION: Non-Maskable Interrupt“, Vektor 2).
  - In den gescheiterten Läufen trifft sie Thread 0 statt des Workers im Gastcode. Welcher Thread gerade läuft, hängt
    vom Zeitpunkt ab; das Skript wertet einen solchen Lauf ausdrücklich als „ohne Aussage, wiederholen“.
  - 1/4 gegen 0/4 ist bei dieser Laufzahl kein belastbarer Unterschied. Dass der Worker heute seltener getroffen
    wird als in #265 (2/2), ist offen.
- **Threads:** Ohne PowerShell als 1:1-Übertragung des Skripts nach Python ausgeführt, mit denselben QEMU-Argumenten
  und Marken.
- **Energie:** `test-power-commands.ps1` scheitert heute auf beiden Seiten gleich. Es tippt direkt nach „Textshell ist
  bereit.“, der Kernel zeigt dann aber den Desktop (ohne Datenplatte „Konten gesperrt“). Geprüft wurde deshalb
  derselbe Ablauf mit vorherigem Esc zur Textshell: `reboot` startet in derselben VM bis zum Desktop, `shutdown`
  beendet QEMU mit Exit 0. Das veraltete Skript ist eine eigene Aufgabe.
- **Doom:** Es braucht wasi-sdk 25 für die Testapp und ein Doom-Testrepository; beides fehlt auf diesem Rechner. Die
  Installation von wasi-sdk wartet auf eine Entscheidung.
