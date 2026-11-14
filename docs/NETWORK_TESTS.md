# Netzwerk und HTTPS – Etappe 10

## Umfang und Aufbau

Der Arbeitsstand folgt dem [Umsetzungsplan](superpowers/plans/2026-11-14-network-https.md). Cosmos 3.0.94 und die bestehenden Paketversionen bleiben erhalten. Netzwerkkartentreiber und IPv4-Stack kommen aus Cosmos; ZehOS ergänzt die Shellintegration, Ressourcenbegrenzungen und die Zuständigkeit für Hintergrundzugriffe. Ein Desktopbrowser ist nicht Teil dieser Etappe.

Die Netzwerkbefehle starten einen Hintergrundauftrag je Terminal. `net status` holt dessen Ergebnis im Vordergrund ab; `net cancel` fordert den Abbruch an. Der Worker schreibt nicht direkt in die Anzeige. Globale Netzwerkoperationen müssen serialisiert werden, weil Cosmos seinen Stack nicht für beliebige parallele Aufrufer absichert. Änderungen der globalen IPv4-Konfiguration sind administrativ.

## Recherche und Wiederverwendung

Maßgeblicher Kernelquellstand ist der Paketcommit [5d0b9535251472db7ecfe012f0435f0a04ffca16](https://github.com/CosmosOS/Cosmos/tree/5d0b9535251472db7ecfe012f0435f0a04ffca16). Die [aktuelle Netzwerkdokumentation](https://cosmosos.github.io/articles/user/network.html) beschreibt einen neueren Stand und beweist keine Verfügbarkeit in 3.0.94.

Der vorhandene Updateadapter liefert bereits DHCP, DNS, TCP und einen begrenzten HTTP-Parser. Diese Bausteine sind gegenüber einem neuen Treiber oder einem zweiten allgemeinen HTTP-Parser vorzuziehen. Die Diagnostik verwendet die in der installierten Version verfügbaren APIs. Diagnoseabfragen konfigurieren das Netzwerk nicht nebenbei.

HTTPS wird getrennt von TCP bewertet. [Cosmos.Network.Http](https://github.com/CosmosOS/Cosmos.Network.Http) bietet einen vollständig verwalteten TLS-Weg statt des vom Host-OpenSSL abhängigen `SslStream`. Paket 3.0.0 ist auf Quellcommit `b188be67e89d7ea99a4594bf698892ee4dfc357f` festgelegt, zielt auf .NET 10 und benötigt BouncyCastle.Cryptography 2.7.0. Die bestehende BouncyCastle-Version passt. Die Bibliothek erklärt NativeAOT-Kompatibilität; erst ein nativer Build und ein Gastlauf belegen die verwendeten Pfade.

Die öffentliche HTTP-API dieses Pakets bietet keine geeignete Kontrolle über Gesamtfrist und Headerbegrenzung; sein TLS-Stream ist intern. Deshalb wird kein zweiter HTTP-Client als Paket eingeführt. ZehOS nutzt direkt die bereits vorhandene BouncyCastle-TLS-Protokolllogik, übernimmt den angehefteten Zertifikatsprüfer und das Rootbundle aus Cosmos.Network.Http unter `ProjektZeh/ThirdParty/CosmosHttp` und legt TLS über seinen eigenen begrenzten Transport. Die Herkunft und mitgelieferten Lizenzhinweise stehen dort in `ORIGIN.txt`.

TLS ist auf Version 1.2/1.3 mit ECDHE/AEAD, acht Zertifikate und 32768 Bytes je Handshake-Nachricht begrenzt. `CosmosSaltSource` liefert Zufallsbytes unmittelbar an BouncyCastle, einschließlich der `GenerateSeed`-Überladungen. Der nur warnende Entropierückfall von Cosmos `KernelRandom` wird dafür nicht verwendet. Ohne RDSEED/RDRAND oder bei fehlgeschlagener Hardwareabfrage scheitert der Handshake. UTC muss als UTC gekennzeichnet im Bereich 2026–2100 liegen; danach prüfen die tatsächlichen Zertifikatsdaten die Gültigkeit. Dieser Plausibilitätsbereich ersetzt keinen sicheren Zeitabgleich. Der Hypervisor muss die RTC korrekt liefern.

Produktion verwendet ausschließlich das eingebettete Mozilla-Bundle. Die lokale Test-CA wird über eine zusätzliche Buildoption nur in den ausdrücklich aktivierten Probe-Build aufgenommen und ausschließlich vom Probenaufruf verwendet. Die normale `fetch`-Implementierung lädt weiterhin das Produktionsbundle. OCSP/CRL, AIA-Nachladen fehlender Zwischenzertifikate, Truststore-Aktualisierung und ein authentifizierter Zeitdienst bleiben spätere Arbeit. Kritische nicht unterstützte Zertifikatserweiterungen werden abgelehnt.

Lizenzrahmen: Cosmos.Network.Http BSD-3-Clause mit MIT-Anteilen aus nanoFramework, BouncyCastle MIT, eingebettete Mozilla-Wurzeln MPL-2.0. Die Hinweise des Pakets sind bei Weitergabe mitzuführen. Der Paketumfang ersetzt keinen Browser. TLS verlangt sichere Entropie, korrekte UTC-Zeit, vertrauenswürdige Wurzeln sowie Ketten- und Hostnamenprüfung. Eine Möglichkeit zum Abschalten der Zertifikatsprüfung gehört nicht zur ZehOS-Bedienung.

## Bekannte Plattformgrenzen

Cosmos 3.0.94 bietet keinen gepluggten asynchronen TCP-Verbindungsaufbau. `Connect` wartet intern begrenzt; dessen Timer ist grob und kann deutlich länger als die nominellen fünf Sekunden benötigen. Auch `Send` und DHCP besitzen interne Warteabschnitte. Ein Abbruch während dieser Abschnitte wird nach ihrer Rückkehr verarbeitet. Die gemeinsame ZehOS-Abruffrist beträgt zehn Sekunden ab DNS und wird vor/nach jedem kontrollierbaren Transportzugriff geprüft; sie kann einen laufenden Cosmos-Aufruf nicht unterbrechen. DNS und ICMP verwenden einzeln fünf Sekunden. Der UI-Thread wartet weder auf den Netzwerkworker noch auf dessen Sperre. Ein fehlgeschlagenes Schließen bleibt sichtbar. Der vorhandene Warmstartschutz für lokale TCP-Ports bleibt erhalten.

VMware und echte Hardware benötigen eigene Nachweise. Ein QEMU-Ergebnis gilt ausschließlich für das jeweils dokumentierte Geräteprofil.

## Prüfstand

- Ausgangsstand: SDK 10.0.112, Cosmos-Tools-Check erfolgreich, 318 Hosttests mit Warnungen als Fehler bestanden.
- Erste HTTP-Probe: nativer Releasebuild ohne Warnungen/Fehler, plattenloser QEMU-Gast mit E1000e/User-Netz. Statische IPv4, fragmentierte Antwort, unvollständige und zu große Antwort, Abbruch mit weiterlaufendem Hauptthread und DHCP bestanden (`NETWORK_PROBE PASS`). Dies war vor den zusätzlichen DNS-/TLS-Prüfungen.
- Abschließende Hosttests: 323 bestanden, keine übersprungen, Release mit Warnungen als Fehler. Enthalten sind Ziel-/IPv4-Validierung, sitzungsbezogene Aufträge und Abbruch, HTTP-Parserfehler sowie TLS 1.2/1.3, konkrete `bad_certificate`-Fehler, UTC-Sperre und fehlgeschlagene Entropie.
- Nativer HTTPS-Probe-Build ohne Warnungen/Fehler. QEMU mit E1000e/User-Netz, CPU `max`, 512 MiB, ohne Datenplatte: statische IPv4, Gateway-Ping, DNS-Erfolg und `.invalid`-Fehler, fragmentiertes HTTP, gültiges TLS, falscher SAN, fremde CA, abgelaufenes und künftiges Zertifikat, Folgeabruf nach Zertifikatsfehlern, TLS-Bodylimits, langsamer Body, schweigender TLS-Handshake mit Deadline und Abbruch, TCP-Verweigerung, HTTP-Timeout und Folgeabruf, Abbruch mit Hauptthread-Heartbeat sowie DHCP bestanden. Zertifikatsfehler zählen nur bei konkretem BouncyCastle-Alert `bad_certificate` als bestanden. Der DNS-Erfolgstest verwendet `example.com` über QEMU-DNS und benötigt eine funktionierende Host-Namensauflösung.
- Sichtprüfung des anschließenden QEMU-Ablaufs: `echo NETWORK_UI_ALIVE`, Start eines wartenden `/stall`-Abrufs, weitere Shell-Eingabe und Ausgabe, Abbruch, F4/Esc und sauberes `shutdown` mit QEMU-Exitcode 0. Ohne Datenplatte zeigt die grafische Sitzung erwartungsgemäß die Kontensperre; dieser Lauf belegt keinen angemeldeten Desktop mit Terminalfenstern. Lokaler Nachweis: `.tools/network-probe/run-nl7w5zwr/serial-u7grc3fe.log` und gleichnamige Screenshotdateien.
- Der abschließende Recovery-/Sicherheits-Recovery-Probe-Build enthält auch die strikte Zielvalidierung. Der vollständige Netzwerk-/TLS-Probenlauf besteht erneut. Auf einer exklusiv neu erzeugten 64-MiB-AHCI-Testplatte wurden `disk init`, Mount und die reguläre Administrator-Ersteinrichtung ausgeführt. Sichtprüfung bestätigt den angemeldeten Desktop, Terminal 1 mit gestartetem `/stall`-Abruf, Start von Terminal 2 mit vollständiger Echo-Ausgabe, F4/Esc mit erhaltenen Fenstern sowie Abbruch und weitere Texteingabe in der Textshell. `shutdown` hängt die Platte aus und beendet QEMU mit Exitcode 0. Nachweis: `serial-vtrhr18p.log` und insbesondere die Bilder `-unlocked-desktop`, `-terminal-fetch`, `-second-terminal`, `-cancel` und `-desktop` im selben Fixture-Verzeichnis. Ein erster Helferlauf ließ den Suchtext im Programmmenü stehen und belegte den zweiten Terminalstart nicht; nach Korrektur des Helfers wurde der Ablauf auf einer weiteren neuen Platte wiederholt.
- Normaler Cosmos-CLI-Releasebuild und anschließender normaler nativer Build mit Warnungen als Fehler erfolgreich; letzterer null Warnungen/Fehler. Reiner Recovery-Releasebuild ohne Probe ebenfalls null Warnungen/Fehler. Die Varianten wurden als `Normal.iso`, `Recovery.iso` und `RecoveryNetworkProbe.iso` im lokalen Fixture-Verzeichnis getrennt gehalten. Der abschließende normale Build enthält `ZehOS.TlsRoots`, aber keine `ZehOS.NetworkProbeCa`-Ressource.
- Normaler Release ohne Netzwerkkarte (`-nic none`), ohne Datenplatte und ohne Probe: Bootlog meldet `Adapter=0, Link=False`, grafische Kontensperre erreichbar, Esc zur Shell, `netinfo` zeigt `Adapter: 0`, `echo NONIC_ALIVE` wird ausgegeben, `shutdown` endet mit QEMU-Exitcode 0. Nachweise: `serial-nonic-manual.log` und geprüfte `serial-nonic-manual-*.png`.
- Reiner Recovery-Build ohne Probe/Datenplatte: textueller Start, vollständige Echo-Ausgabe, F4 zur erwarteten grafischen Kontensperre, Esc zurück zur Shell und Shutdown mit Exitcode 0 sichtbar geprüft (`serial-g040yxv7.log` und gleichnamige Bilder). Der dort gestartete Abruf meldet erwartungsgemäß fehlende IPv4-Konfiguration; dieser reine Bootlauf ist kein Netzwerkübertragungsnachweis.
- VMware und echte Hardware sind nicht geprüft. `vmrun`, `powershell` und `pwsh` sind in dieser Umgebung nicht vorhanden; der PowerShell-Energiehelfer wurde nicht ausgeführt. Die VM-Startkonfiguration wurde für diese Änderung nicht geändert. Die eigenen QEMU-Läufe prüfen den tatsächlichen Shell-Shutdown; ein neuer Energiekommando-/GDB-Matrixlauf wird damit nicht behauptet. Dateioperationen der Anwendungen und Dateipersistenz wurden für diese Netzwerkänderung nicht vollständig erneut geprüft; das vorhandene Datenimage blieb unangetastet.

## Reproduzierbare Gastprobe

Benötigt werden Python 3, OpenSSL, die vorhandenen Cosmos-Bauwerkzeuge und QEMU. Der Helfer installiert oder aktualisiert keine Werkzeuge. Vom Repository-Stamm aus:

```sh
python3 scripts/test-network.py --prepare-fixtures
# Den ausgegebenen absoluten Verzeichnispfad als FIXTURES einsetzen.
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -warnaserror \
  -p:ZehOSNetworkProbe=true -p:ZehOSNetworkProbeCaPath=FIXTURES/ca.pem
python3 scripts/test-network.py --fixtures FIXTURES \
  --iso ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso --smoke
```

`--qemu PFAD` überschreibt die Suche im PATH beziehungsweise im Cosmos-Werkzeugverzeichnis. Der Helfer verwendet ausschließlich seine eigene VM; HTTP/TLS-Fixtures lauschen lokal auf 18080 und 18443–18448. Log, Testschlüssel und Screenshots bleiben im neu erzeugten ignorierten Verzeichnis. Eine fehlende Probe oder `NETWORK_PROBE FAIL` ergibt einen Fehlerstatus; der Helfer meldet bei einem unveränderten Normal-ISO keinen Erfolg. Oberflächenscreenshots werden erzeugt und müssen sichtbar geprüft werden; ihre bloße Existenz ist kein Bediennachweis.

Für die Recovery-Probe zusätzlich `-p:ZehOSRecovery=true` setzen und den Helfer mit `--smoke --recovery` starten. `--boot-smoke` prüft Normal-/Recovery-Start, Eingabe und Anzeigewechsel ohne Probe; ohne vorherige IPv4-Konfiguration belegt dieser Modus keine laufende Netzwerkübertragung.

`--provision-desktop` verlangt `--smoke --recovery` und erzeugt eine neue eigene 64-MiB-Testplatte. Der ausdrücklich gewählte Sicherheits-Recovery-Build (`-p:ZehOSSecurityRecovery=true`) erlaubt deren Einrichtung. Das Skript formatiert nur diese neu erzeugte Platte über die vorhandenen Schutzprüfungen von `disk init`, richtet den Testadministrator ein und erzeugt weitere Desktopbilder. `--test-disk` akzeptiert zur Wiederverwendung nur eine vom Helfer erzeugte und markierte Datei im angegebenen Fixture-Verzeichnis. Das normale Datenimage wird nie geöffnet.

Die Probe-CA wird nicht in einen abschließenden normalen Releasebuild übernommen. Vor einem späteren normalen Start erneut ohne Probe-Eigenschaften bauen; Varianten verwenden dieselben Ausgabepfade.
