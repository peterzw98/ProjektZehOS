# Apps als Pakete

Teil des Handbuchs zu ZehOS; Überblick und Schnellstart in der [README](../../README.md).

## Apps als Pakete installieren (Doom)

Der normale Kernelbuild enthält standardmäßig den WebAssembly-Loader (wasm3) und den Paketmanager `pkg`. Apps werden nicht mehr in den Kernel gebaut, sondern als signierte Pakete installiert. Sie erscheinen danach im Startmenü. Doom ist das erste Paket: doomgeneric als Wasm plus Freedoom.

Für QEMU-Start- und Testskripte setzt `ZEHOS_QEMU_SMP` optional die CPU-Topologie, z. B. `4,cores=2,threads=2`; leer bleibt die QEMU-Vorgabe. `start-qemu.ps1 -Smp` überschreibt die Umgebungsvariable. Für VMware setzt `start-vmware.ps1 -Cpus 2` die Anzahl virtueller CPUs (Standard 1).

Nach `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release` startet `scripts/start-qemu.ps1` das normale ISO einschließlich Paketmanager. Alternativ mit `dotnet build` bauen und dessen ISO ausdrücklich auswählen:

```powershell
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/start-qemu.ps1 -IsoPath ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso
```

Installieren braucht ein eingebundenes Datenlaufwerk, eine Anmeldung als Administrator und Netzwerkzugang. Doom belegt dort rund 30 MB. Danach in der Textshell oder einem Terminal:

```text
pkg install doom
```

Ändernde `pkg`-Befehle sind Administratoren vorbehalten und laufen im Hintergrund; das Ergebnis erscheint automatisch im aufrufenden Terminal. `pkg list` und `pkg remove doom` zeigen beziehungsweise entfernen Pakete. Ein laufendes Paket lässt sich weder entfernen noch aktualisieren. Beim ersten Start nach jedem Neustart werden alle Paketdateien erneut gegen das signierte Manifest geprüft.

Standardquelle ist `https://packages.pjoter.de/`; eine mit `pkg source <https-URL>` gespeicherte eigene Quelle bleibt erhalten. `pkg install` und `pkg upgrade` laden die signierte Paketliste automatisch vor dem Auftrag. `pkg update` bleibt als freiwilliger Katalogabruf verfügbar. Installierte Apps werden nur mit `pkg upgrade` aktualisiert.

Während des Auftrags zeigt dieselbe Shell eine laufend ersetzte Statuszeile: Katalog-/Signaturphase, Paket und Datei, Downloadbalken, Prozent und übertragene/Gesamtbytes. Eine abgeschlossene Übertragung ist noch keine abgeschlossene Installation: Erst nach Hashprüfung und Veröffentlichung folgt das Ergebnis. `pkg cancel` bricht ab. Der Fortschritt bleibt an die Sitzung gebunden; andere Terminals erhalten ihn nicht.

Ein Build vertraut nur dem eingebetteten öffentlichen Herausgeberschlüssel `ProjektZeh/Packages/package-key.pub`; `-p:ZehOSPackageKeyPath=<Datei>` ersetzt ihn, etwa für Tests. Den privaten Schlüssel hält der Herausgeber außerhalb des Repositorys. Das Repository wird aus dem Branch `packages` gespeist und von `deploy/package-repo` ausgeliefert (Docker-Compose mit automatischer Aktualisierung). Format, Speicherort und Werkzeug `scripts/build-package.py` beschreibt [docs/PACKAGE_FORMAT.md](../../docs/PACKAGE_FORMAT.md). QEMU-Abläufe prüft `scripts/test-wasm-doom-desktop.py` mit lokalem Testrepository und Snapshot (Aufruf und Szenarien in [docs/PACKAGE_FORMAT.md](../../docs/PACKAGE_FORMAT.md)), das Szenario `full` auch mit `--source https://packages.pjoter.de/`.

NetSurf 3.11 ist als separates Offline-Paket baubar: lokale HTML-/CSS-Seiten, Links, Zurück, Scrollen und
Formulareingabe. Der Kernel enthält nur die allgemeinen Hostdienste; NetSurf und seine Bibliotheken bleiben im
Paket. NetSurf ist noch nicht im öffentlichen Paketkatalog veröffentlicht. Build, lokale Testinstallation und
geprüfte Grenzen stehen in [docs/NETSURF.md](../../docs/NETSURF.md). JavaScript ist standardmäßig aus;
Strg+J schaltet es für die laufende Sitzung ein (Statusleiste „JS an“), siehe dort.

ClassiCube 1.1.0 (`pkg install classicube`) braucht deutlich mehr Arbeitsspeicher als die übrigen Pakete. Gemessen
unter QEMU/KVM mit einem Kernel ohne Speichersammlung vor dem App-Start (`master` a4a598c), acht Läufe zu je drei
Minuten in einer Zufallswelt: Belegt waren rund 425 MB vor dem Start und rund 455 MB nach dem Spiel, unabhängig vom
Arbeitsspeicher der VM. Frei blieben danach bei 512 MiB rund 50 MB, bei 768 MiB rund 318 MB, bei 1 GiB rund 586 MB
und bei 2 GiB rund 1660 MB. Empfohlen ist 1 GiB, mindestens 768 MiB. Mit 512 MiB kann ClassiCube mit
„Out of VRAM?“ abbrechen (unter WHPX beobachtet, unter KVM in diesen Läufen nicht). Seit 0.5.86 sammelt der Kernel
vor jedem App-Start Speicher ein; das mildert diesen Fall, garantiert aber keinen sicheren Betrieb mit 512 MiB.
Längere Spielzeit, größere Welten und VMware sind nicht gemessen.

Eigene Apps baut man als wasm32-WASIp1-Modul, etwa mit wasi-sdk für C/C++. Was eine App darf, erklärt [docs/PAKETE_ERSTELLEN.md](../../docs/PAKETE_ERSTELLEN.md), ebenso den Weg bis zur Veröffentlichung. Die wichtigsten Grenzen:
- Nur grafische Fensterapps; eine App zur Zeit.
- Dateien nur lesend und nur aus dem eigenen Paket.
- Kein Netzwerk, keine Threads, keine Speicherstände.
- Höchstens 256 MiB Speicher; die App muss `should_exit` abfragen.
- Der Interpreter ist unter QEMU deutlich langsamer als nativer Code.

**In Arbeit, nicht für Apps freigegeben:** Ein nativer AOT-Pfad (WAMR 2.4.5 als reiner AOT-Lader, [AOT-Plan](../../docs/superpowers/plans/2026-11-17-aot-kernel.md), Etappe D) ist nur in Kernel gebaut, die ausdrücklich `-p:ZehOSWasmAot=true` oder die Gastprobe `-p:ZehOSAotProbe=true` verwenden. Der normale Build enthält ihn nicht; dort laufen Paket-Apps ausschließlich unter wasm3. Mit dem Schalter wählt der Kernel seit Etappe E ein `.aot` mit eigener Kennung aus dem Paket, aber erst nach `wasmaot on`; AOT bleibt bis Etappe F standardmäßig aus. Namensregel, Hashprüfung und Rückfall: [docs/PACKAGE_FORMAT.md](../../docs/PACKAGE_FORMAT.md#aot-datei-optional-aot-plan-etappe-e). Probe und Fixtures: [ProjektZeh/Native/AotProbe/fixture/README.md](../../ProjektZeh/Native/AotProbe/fixture/README.md).

`help` zeigt `pkg` mit seinen Unterbefehlen. Nur ausdrücklich mit `-p:ZehOSWasm=false` gebaute Kernel und das Installer-ISO bieten keinen `pkg`-Befehl.
