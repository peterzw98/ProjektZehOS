# Desktop-Dateifehler prüfen (Roadmap 7.5)

## Geprüfter Stand am 13. November 2026

Basis `7264b34`, Cosmos **3.0.94**, SDK **10.0.112**, gebündeltes QEMU **10.2.2**. Keine Kernelquellen oder Paketversionen geändert. Der normale native x64-Release-Build ist erfolgreich; **155 MSTest-Hosttests** bestehen mit Warnungen als Fehler. Sechs neue Desktop-Testfälle leiten Eingaben durch `Desktop`, `WindowManager`, `TerminalApp`, Shell und Dateidienst. Sie prüfen Fehlerausgabe, Weiterarbeit, getrennte Entwürfe und einen injizierten Ersetzungsfehler mit Wiederherstellung und erneutem Schreiben. Die injizierte Ausnahme ist kein Hostnachweis für einen echten vollen Datenträger.

Die zusätzliche Gastprüfung lief mit `q35`, CPU `max`, 512 MiB RAM, Standard-VGA und PS/2-Eingabe, einmal ohne Datenlaufwerk und einmal mit einer eigenen 64-MiB-SATA/AHCI-Testkopie. Eingaben wurden über QEMU gesendet, die Bildausgabe anhand von Screenshots geprüft. Die normalen Datenimages des Projekts wurden nicht verwendet.

| Fall | Tatsächliches QEMU-Ergebnis |
| --- | --- |
| Kein Datenlaufwerk | Boot zu zwei Terminals. `ls`, `cat fehlt.txt` und `write neu.txt Inhalt` liefern Pfadfehler; `cat ""` liefert `InvalidPath`. Danach funktioniert `echo weiter-ohne-disk`. |
| Fehlende Datei/Verzeichnis auf eingebundener Platte | `cat fehlt.txt` und `cd fehlt` liefern einen Fehler zum angeforderten Pfad. `cat ""` wird weiterhin abgelehnt. Nachfolgende Befehle werden ausgeführt. |
| Voller FAT32-Datenträger | `write original.txt nachher` und `write neu.txt Inhalt` melden `No space left on device`. `cat original.txt` zeigt weiterhin `vorher`. |
| Bereinigung nach Schreibfehler | `ls` zeigt nur `FILLER.BIN` und `original.txt`, keine `.tmp`-/`.bak`-Datei und keine neu angelegte Zieldatei. |
| Bedienung nach Fehlern | `echo` funktioniert. Esc öffnet die bedienbare Textshell; F4 kehrt zum erhaltenen Desktop zurück. |
| Neustart | `reboot` startet erneut zum Desktop; `cat original.txt` zeigt weiterhin `vorher`, anschließend funktioniert `echo nach-neustart`. `shutdown` beendet QEMU. |

Der Ablauf wurde nochmals mit einer durch den eingecheckten Helfer erzeugten vollen Kopie ausgeführt. Das Ausgangsimage blieb SHA256-gleich: `dfc5eb8fb87afc19d7343feaaed1c90decad326201bb1829da4f2f74b8f256b0`. Dieser Hash beschreibt nur die lokale Probe, keine herunterzuladende Referenzdatei.

**Offen:** VMware und echte Hardware wurden hierbei nicht geprüft. Das bestehende VMware-Profil besitzt kein Datenlaufwerk. Es wird keine Sicherheit bei Stromausfall behauptet. Die synchronen Dateioperationen können die kooperative Desktop-Schleife weiterhin anhalten; die Entkopplung bleibt Gegenstand von 3.5 und 6a.

## Wiederholen mit einer isolierten Testplatte

Voraussetzungen: bestehende passende Cosmos-Werkzeuge und .NET 10, QEMU sowie Python 3 auf dem Host. `python3` heißt auf manchen Systemen `python`. Keine Werkzeuge oder Pakete werden durch diese Befehle installiert.

1. Den normalen Release-Build erstellen:

   ```sh
   cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release
   ```

2. Unter `.tools/storage-error-probe` eine **neue** leere 64-MiB-Datei erstellen. Dieser Befehl bricht bei vorhandenem Ziel ab:

   ```sh
   python3 -c "from pathlib import Path; p=Path('.tools/storage-error-probe'); p.mkdir(parents=True, exist_ok=True); f=(p/'source.img').open('xb'); f.truncate(64*1024*1024); f.close()"
   cosmos run --iso ProjektZeh/output-x64/ProjektZeh.iso --arch x64 --disk .tools/storage-error-probe/source.img,ahci -- -action shutdown=poweroff,reboot=reset
   ```

3. Im Gast mit `disk list` den Namen der eigenen leeren Testplatte prüfen. Im Referenzprofil heißt sie `sata0`. Nur auf dieser Platte ausdrücklich `disk init sata0`, dann `disk mount sata0` ausführen. Mit `write original.txt vorher` eine Datei anlegen, mit `cat original.txt` vergleichen und mit `shutdown` sauber aushängen und beenden. Kein laufendes oder unsauber ausgehängtes Image weiterverarbeiten.

4. Die vollständig belegte **neue Kopie** erzeugen und ausschließlich diese starten:

   ```sh
   python3 scripts/create-full-fat32-test-copy.py .tools/storage-error-probe/source.img .tools/storage-error-probe/full.img
   cosmos run --iso ProjektZeh/output-x64/ProjektZeh.iso --arch x64 --disk .tools/storage-error-probe/full.img,ahci -- -action shutdown=poweroff,reboot=reset
   ```

5. Im Desktop die Befehle aus der Ergebnistabelle ausführen. Vor dem Reboot Originalinhalt und Verzeichnisliste prüfen, danach den Inhalt nochmals vergleichen. Die Probe ohne Datenplatte mit demselben `cosmos run`-Befehl **ohne** `--disk` wiederholen. Bei manuellen Prüfungen auch im anderen Terminal einen Entwurf stehen lassen und nach dem Fehler wieder aktivieren.

Der Helfer ist ausschließlich ein Offline-Testdatenwerkzeug. Er liest das Ausgangsimage und erzeugt das Ziel mit exklusivem `CreateNew`-Verhalten. Er bearbeitet keine vorhandene Datei. Unterstützt wird eine einzelne FAT32-LBA-Partition mit 512-Byte-Sektoren, zwei identischen gespiegelten FATs und einem einzelnen Wurzelcluster mit freiem Verzeichniseintrag. Abweichende oder beschädigte Profile werden abgelehnt; dies ist kein allgemeiner FAT-Reparaturdienst und keine vollständige Dateisystemprüfung.

Die hinzugefügte Datei `FILLER.BIN` verkettet alle noch freien Cluster. Beide FATs und vorhandene FSInfo-Zähler werden angepasst. Vorhandene Dateiketten bleiben unverändert; Datenbereiche der Fülldatei werden nicht neu beschrieben. Cosmos 3.0.94 lässt den FSInfo-Sicherungssektor leer; ein solcher Sektor bleibt leer. Ein vorhandener gültiger Sicherungssektor wird aktualisiert. Die Testkopie darf keine wichtigen Daten enthalten und wird nicht als normales Datenlaufwerk weiterverwendet.

Die Hostprüfungen des Helfers decken die vollständige Clusterkette, Dateierhalt, beide FATs, FSInfo, Verzeichnis-Endmarker, gelöschte Lücken, unveränderte Quelle, Schutz vorhandener Ziele und ungültige Eingaben ab:

```sh
python3 -m unittest discover -s scripts/tests -v
dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror
```
