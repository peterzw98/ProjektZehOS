# Cosmos-Threadprobe (Roadmap 6a.1)

Der nächste eigenständig prüfbare Schritt ist die Thread- und Warteprobe aus 6a.1. Grundlage bleibt Cosmos 3.0.94, dessen Pakete die experimentelle öffentliche Scheduler-Schnittstelle bereits enthalten. Die bestehende Desktop-Schleife wird erst nach diesem Nachweis erweitert.

## Entscheidung

Ein ausdrücklich mit `-p:ZehOSThreadProbe=true` gebautes ISO prüft echte `System.Threading.Thread`-Instanzen im Gast vor der interaktiven Sitzung. Zwei rechenintensive Aufgaben müssen ohne freiwilliges Yield Fortschritt machen; anschließend werden Sleep, Join, Ereignis-Warten, Zeitlimit und geordnetes Ende geprüft. Begrenzte Wartezeiten und ein äußerer QEMU-Watchdog verhindern einen unbegrenzten Testlauf. Die Probe schreibt eindeutige PASS-/FAIL-Zeilen seriell und startet danach den vorhandenen Desktop. Normale Builds enthalten die Probe nicht.

Vorhandene Cosmos-Threads und .NET-Warteprimitive werden wiederverwendet. Ein eigener Kontextwechsel oder eine kooperative Update-Verteilung lösen die Versionsprüfung nicht. Eine sofortige Umstellung des produktiven Schedulers würde Prüfung und neue Funktion vermischen. Round-Robin und die Aufgabenverwaltung folgen daher in 6a.2–6a.5.

## Umfang und Nachweis

Betroffen sind `Kernel.Run`, die bedingte Buildkonstante, eine isolierte Laufzeitprobe und ein QEMU-Testhelfer. Die Probe greift weder auf Dateien noch auf Desktopzustand zu. Gemeinsame Testzustände verwenden `Interlocked`/`Volatile`; Arbeitsthreads zeichnen und protokollieren nicht. Auf jedem Fehler werden die eigenen Threads zum Ende aufgefordert und begrenzt abgewartet.

Hosttests prüfen weiterhin die bestehenden Funktionen. Der native Probe-Build und QEMU müssen die echten Thread-/Warteoperationen belegen; anschließend werden normaler Boot und Recovery separat gebaut/geprüft. VMware bleibt ein eigenständiger Nachweis. Nur erfüllte Roadmap-Kriterien werden abgehakt.

Die Umsetzung erfolgt gemäß bestehendem Auftrag selbstständig; kein Paket- oder Werkzeugupdate ist vorgesehen.
