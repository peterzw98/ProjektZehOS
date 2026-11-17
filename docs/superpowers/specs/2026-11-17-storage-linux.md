# Größere Datenträger und Linux-ähnlicher Dateibaum

Stand: 17. November 2026. Umsetzung nach Merge von #117/#118 freigegeben; die erste Hostprüfung ergibt ein No-Go für den Cosmos-ext2-Formatierer. Keine Speicheränderung im Kernel aktiviert oder im Gast freigegeben. Aktuelle Belege: [Prüfbericht](../../STORAGE_3_0_97_FINDINGS.md).

## Auftrag und Entscheidungen

- Ursprünglich beauftragt war die Planung; der Benutzer hat anschließend die Umsetzung freigegeben. Bestehende Platten dürfen weiterhin nicht formatiert werden.
- Der Benutzer benötigt zunächst **32–128 GiB** große virtuelle Datenträger.
- Technische Zielbasis ist auf ausdrücklichen Wunsch **Cosmos 3.0.97**; das laufende Update gehört einem separaten Arbeitsauftrag.
- Gewünscht sind ein neuer Datenträger-Dateisystemkandidat, ein Linux-ähnlicher Baum mit `/etc`, `/var` usw. und das Prinzip „alles ist eine Datei“, ausdrücklich auch zum Anschauen von Cosmos-Systemdaten.
- Vorschlag des Planerstellers: vorhandenes Cosmos-ext2 zuerst qualifizieren; FAT32 für die EFI-Systempartition behalten. Keine Eigenentwicklung eines Plattenformats.

## Drei getrennte Aufgaben

1. Blockgeräte, Partitionen und VM-Helfer müssen große Images korrekt behandeln.
2. Das Plattenformat speichert dauerhafte Dateien und Metadaten.
3. Der virtuelle Dateibaum vereinigt diese Dateien mit Geräten und live erzeugten Systeminformationen. `/etc` und `/proc` entstehen nicht automatisch durch die Wahl von ext2 oder exFAT.

Linux-ähnlich bedeutet hier eine gemeinsame Dateischnittstelle und vertraute Verzeichniszuständigkeiten. Es bedeutet keine Linux-Binärkompatibilität, keinen Linux-Kernel und noch keine vollständige POSIX-Implementierung. Netzwerkverbindungen können Handles bleiben; aus „alles ist eine Datei“ folgt keine sinnvolle Textdarstellung jedes Kernelobjekts.

## Befund im Projekt

Erstrecherche: `43da4aa`, ZehOS 0.3.7, Cosmos 3.0.94. Umsetzungsbasis nach den Merges: `d443931`, ZehOS 0.5.6, Cosmos 3.0.97. Folgende Speicherpfade wurden dort erneut betrachtet; weitere Änderungen sind vor jeder Folgeetappe abzugleichen.

- `Storage/CosmosStorageService.cs`: FAT unter `/data`, genau eine Partition. Nur `InitializeTestDisk` beschränkt die Einrichtung auf eine vollständig leere 64-MiB-Platte mit 512-Byte-Sektoren und MBR.
- `scripts/start-qemu.ps1`: erzeugt bzw. akzeptiert derzeit genau das 64-MiB-Image `.tools/qemu/data-fat32.img`. Größere Datenträger brauchen einen gesonderten, expliziten Weg; das Bestandsimage darf nicht ersetzt werden.
- `Installer/CosmosInstallService.cs`: eigener Installationsweg ab 128 MiB, derzeit eine FAT32-EFI-Partition über MBR, eigene Größenprüfung bis ungefähr 2 TiB. Das ist von der 64-MiB-Testinitialisierung zu unterscheiden.
- `Kernel.cs`, `Update/`, `Packages/`, `Apps/FileManager/`, Shell und Probes verwenden `/data` bzw. darunterliegende Pfade.
- `Security/AccountService.cs`: Konten und ACLs in `.zehos-security.db`, Befehlsrechte in `.zehos-commands.db`, Home-Verzeichnisse unter `/data/home`.
- `Security/FileAccessControl.cs`: eigene pfadbasierte ACLs, derzeit Groß-/Kleinschreibung ignorierend. ext2-Metadaten ersetzen diese Sicherheitslogik nicht automatisch.
- `FileService` verwendet bereits `System.IO`; das ist die gewünschte gemeinsame Schnittstelle. `ReadText` besitzt absichtlich ein Größenlimit, das mit einer größeren Platte nicht entfallen soll.
- Wasm-Pakete haben bereits eigene Pfad- und Zugriffsgrenzen. Ein neuer Host-Dateibaum darf diese nicht erweitern.

## Prüfung der Zielversion 3.0.97

Das veröffentlichte NuGet-Paket wurde separat nach `.tools/storage-plan/` geladen und mit `ilspycmd` untersucht, ohne Projektabhängigkeiten zu ändern. Paketmetadaten: `Cosmos.Kernel.System` **3.0.97**, `net10.0`, BSD-3-Clause, Abhängigkeiten auf Core und HAL **3.0.97**, Repository-Commit **`efcf20cc62884b042a0adadffe9fc4540491fd89`**. [Paket](https://www.nuget.org/packages/Cosmos.Kernel.System/3.0.97), [zugehöriger Quellstand](https://github.com/CosmosOS/Cosmos/tree/efcf20cc62884b042a0adadffe9fc4540491fd89).

Im tatsächlichen Paket vorhanden:

- `Ext2FileSystemType`, `Ext2FormatOptions`, `Ext2Formatter`, Inode- und Dateioperationen einschließlich Erzeugen, Lesen und Schreiben; Formatoptionen enthalten 1024, 2048 und 4096 Byte große Blöcke.
- `Storage.Gpt`, FAT, ISO9660 und der gemeinsame `VfsManager`.
- `VfsManager.RegisterFileSystem`, Mounts mit Partition oder Quelle, Formatieren, Aushängen, Stat und Dateihandles; Mounts können als read-only markiert werden.

Wichtige Grenzen aus der Binärinspektion: ext2 legt in den betrachteten Create-Pfaden UID/GID zunächst als 0 an. Der Formatter verwendet 32-Bit-Blockzähler; aus dem Formatnamen folgt keine beliebige Plattengröße. Der VFS löst vorhandene Symlinks auf und begrenzt Linkverfolgung auf acht Schritte. Eine reine Prüfung des ursprünglichen Pfadtexts reicht deshalb für Sicherheitsgrenzen nicht aus. Das sind Codebefunde, keine Gasttestergebnisse. Rechte, Link-Erzeugung, Zeitattribute, Fehlerwege, Root-Mount und virtuelle Treiber sind gezielt im integrierten 3.0.97-Gast zu prüfen.

Nachprüfung des IRC-Reviews am selben 3.0.97-Paket: Der Formatter wählt bei 4096-Byte-Blöcken 2048 Blöcke je Gruppe (16384 Gruppen bei 128 GiB), setzt kein `sparse_super` und schreibt im untersuchten Ablauf nur den primären Superblock/Gruppendeskriptortisch. Ob sein Ergebnis `e2fsck -fn` besteht, ist deshalb eine ausdrückliche Go/No-Go-Bedingung unmittelbar nach Formatierung, noch vor jedem Gastschreiben. `Ext2Superblock.TryCreate` liest Featureflags, lehnt unbekannte Incompat-/RoCompat-Bits im betrachteten Pfad aber nicht ab. Vor jedem Mount ist deshalb ein geprüfter Featurevertrag nötig: unbekannte Incompat-Bits verweigern, unbekannte RoCompat-Bits nur dann read-only zulassen, wenn Lesen nachweislich sicher ist; niemals blind schreiben. Der Dateischreibpfad setzt `SizeHigh`, ergänzt aber dort kein `large_file`-Featureflag. Bis zur erfolgreichen gesonderten Prüfung gelten maximal **2 GiB minus 1 Byte je Datei** als konservatives Freigabelimit. Das ist eine vorläufige Implementierungsgrenze, keine ext2-Formatgrenze. Zeitstempelsetzen und Link-Erzeugung über `System.IO` bleiben ungeprüft und dürfen nicht aus internen VFS-Funktionen abgeleitet werden.

## Kandidaten und Empfehlung

| Kandidat | Nutzen | Aufwand und Grenze | Entscheidungsvorschlag |
| --- | --- | --- | --- |
| FAT32 | Vorhandener ZehOS-Pfad und EFI-Boot | Einzeldateien höchstens 4 GiB minus 1 Byte; keine nativen POSIX-Rechte | EFI und Bestands-/Übergangsbetrieb behalten |
| exFAT | Große Dateien und Datenträger, Austausch mit anderen Betriebssystemen | Kein exFAT-Treiber in der untersuchten System-Assembly; keine POSIX-Rechte oder Symlinks; Portierung notwendig | Kein erstes Root-Dateisystem, späteres Austauschformat |
| ext2 aus Cosmos 3.0.97 | Bereits vorhandener C#-Treiber, Unix-Metadaten, kein zusätzlicher nativer Port | Kein Journal; tatsächliche Konsistenz, Performance und Rechteanbindung noch nachzuweisen | Erster Kandidat, Freigabe ausschließlich nach Qualifikation |
| ext4 | Linux-Format mit Journaling und weiteren Funktionen | Kein ext4-Treiber im untersuchten Paket; deutlich größerer Integrations- und Prüfaufwand | Später neu bewerten, falls ext2 an belegten Anforderungen scheitert |
| Eigenes Plattenformat | Frei gestaltbar | Eigene Reparaturwerkzeuge, Konsistenz- und Kompatibilitätsrisiken | Nicht planen; vorhandene Bausteine reichen als Ausgangspunkt |

Die exFAT-Spezifikation nennt 64-Bit-Dateilängen. Microsoft dokumentiert fehlende POSIX-Rechte und Links für exFAT. Die FAT32-Dateigrenze ist unabhängig von der ZehOS-64-MiB-Testregel. [exFAT-Spezifikation](https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification), [Formatvergleich](https://learn.microsoft.com/en-us/windows/win32/fileio/filesystem-functionality-comparison).

Als externer ext4-Kandidat wurde `lwext4` betrachtet: C-Bibliothek, Standardbibliotheks- und Blockgeräteanbindung nötig; kein direkt verwendbares .NET-/Cosmos-Paket. Das Upstream-README weist ausdrücklich auf GPLv2-Dateien hin, durch die die vollständige Bibliothek GPLv2 wird. Daher nach Projektregeln kein unveränderter Import in den Kernel. Ein möglicher lizenzbereinigter Teilumfang wäre eine eigene Prüfung, keine hier getroffene Entscheidung. [Upstream](https://github.com/gkostka/lwext4). Ein Journal allein garantiert auch keine Sicherheit sämtlicher Nutzdaten. [Linux-ext4-Dokumentation](https://docs.kernel.org/admin-guide/ext4.html).

## Recherche in Cosmos-Community und Dokumentation

- Die aktuelle Gen3-Dokumentation beschreibt FAT, ext2, ISO9660 und GPT. Sie nennt Gasttests für FAT/ISO9660, bisher Hosttests für ext2. Das ist ein Grund für die Qualifikationsetappe, kein Stabilitätsnachweis für 3.0.97. Die Dokumentation folgt einem beweglichen Stand; für die Paket-API gilt die obige Binärprüfung. [Gen3-Dateisysteme](https://cosmosos.github.io/articles/user/filesystem.html).
- Discussion #3233 zur Installation behandelt Controller- und Generationsunterschiede. Daraus folgt keine bestätigte ext2-/exFAT-Unterstützung. [Discussion](https://github.com/CosmosOS/Cosmos/discussions/3233).
- Issue #1331 betrifft FAT-Formatierungsprobleme mit dem alten User Kit 20190628. Die alten `CosmosVFS`-/`0:\\`-Rezepte gelten nicht für dieses Gen3-Projekt. [Issue](https://github.com/CosmosOS/Cosmos/issues/1331).
- In der durchgeführten Suche wurde kein belastbarer Community-Nachweis für einen produktionsreifen exFAT-Treiber unter Gen3 3.0.97 gefunden. Das ist kein Beweis, dass es weltweit keinen gibt. Es wurden keine neuen Forenbeiträge veröffentlicht.

## Zielstruktur

An der üblichen Unix-Aufteilung orientiert, ohne vollständige FHS-Konformität zu behaupten. [FHS-Referenz](https://www.pathname.com/fhs/pub/fhs-2.3.html).

```text
/
├── boot/                  FAT32-EFI-Partition; Kerneldatei, EFI/BOOT, Bootkonfiguration
├── etc/zehos/             Systemkonfiguration, Mountkonfiguration, geschützte Kontendaten
├── home/<name>/           persönliche Dateien und .config/zehos/
├── root/                  optionales Home eines tatsächlich eingerichteten root-Kontos
├── usr/bin/               ausführbare Pakete bzw. geprüfte Starter
├── usr/share/zehos/       mitgelieferte Daten
├── usr/share/licenses/    Lizenzen/Komponentenliste
├── usr/src/               optional mitgelieferte Quellen, kein Zugriff aufs Host-Repository
├── var/lib/zehos/         Paketbestand, Kataloge und dauerhafter Systemzustand
│   └── apps/<id>/        reservierter instanzgebunden abzusichernder App-Zustand
├── var/log/               begrenzte persistente Protokolle
├── var/cache/zehos/       löschbare Downloads/Caches
├── run/                   flüchtiger Laufzeitzustand, begrenzter RAM-Speicher
├── tmp/                   begrenzte temporäre Dateien, beim Neustart leer
├── dev/                   virtuelle Geräte: null, zero, später tty und Blockgeräte
├── proc/                  live erzeugte Systeminformationen
├── sys/                   Geräte-/Treiberbaum und Eigenschaften
├── mnt/                   explizite weitere Datenträger
└── media/                 später Wechselmedien, soweit Treiber verfügbar
```

Für die erste Installation vorgeschlagen: GPT, 512-MiB-EFI-Systempartition mit FAT32, restliche nutzbare Kapazität ext2 für `/`, 1-MiB-Ausrichtung und reservierte GPT-Endsektoren. 4096 Byte ext2-Blockgröße ist ein zu qualifizierender Vorschlag. 32, 64 und 128 GiB sind Testziele. Über 2 TiB, 4Kn, Online-Vergrößerung und zusätzliche Storage-Treiber sind zunächst nicht beauftragt. GPT wird wegen des sauberen getrennten Layouts vorgeschlagen, nicht weil 128 GiB zwingend GPT erfordern.

Die EFI-Partition wird unter `/boot` eingehängt. Der Bootloader muss den Kernel dann weiterhin von FAT32 laden können; seine Pfade sind aus Sicht dieser Partition zu prüfen, nicht blind von `/data/boot` zu übernehmen. ext2-Bootloaderunterstützung ist dafür nicht nötig.

## „Alles als Datei“ einschließlich Cosmos-Systemdaten

**Verbindlicher Planumfang nach Benutzerergänzung:** Die Dateisicht muss auch die vom Kernel bereitstellbaren Cosmos-Systemdaten sichtbar machen. Leere Platzhalterverzeichnisse allein erfüllen die Anforderung nicht.

| Pfad | Geplanter Inhalt und Verhalten |
| --- | --- |
| `/proc/version` | ZehOS-, Cosmos- und Buildversion aus dem tatsächlichen Build |
| `/proc/uptime` | monotone Laufzeit, dieselbe Quelle wie der Shellbefehl |
| `/proc/meminfo` | belegbare Speicherkennzahlen mit Einheiten; nicht verfügbare Größen ausdrücklich so kennzeichnen |
| `/proc/mounts` | tatsächliche Mounttabelle mit Dateisystem und Schreibschutz |
| `/proc/threads` | vorhandene Schedulerdaten, soweit öffentlich und sicher zugänglich; keine erfundenen Linux-Prozesse |
| `/proc/log` | begrenzte, bereinigte Sicht auf das vorhandene Diagnoseprotokoll |
| `/sys/devices/<id>/` | stabile Gerätekennung, Typ, Hersteller-/Geräte-IDs und zugeordneter Treiber, soweit verfügbar |
| `/sys/block/<id>/` | Kapazität, Sektorgröße, Partitionen und Einbindungszustand |
| `/sys/kernel/components` | tatsächlich eingebundene Komponenten mit Version/Lizenz, ausdrücklich keine Liste dynamischer Linux-Module |
| `/dev/null`, `/dev/zero` | echte Streamsemantik für Schreiben/EOF bzw. Nullbytes, keine Textdatei-Attrappen |
| `/dev/tty*` | später sitzungsgebundene Terminalstreams, ohne die Ereignisschleife blockierend umzubauen |
| `/dev/<block-id>` | später begrenzter Gerätezugriff über Dateien/Handles; Schreibzugriff nur nach eigener Freigabe |

`/proc` und `/sys` sind virtuelle Dateisysteme. Beim Öffnen entsteht ein kleiner konsistenter Snapshot; partielle Reads lesen denselben Snapshot, EOF beendet ihn, erneutes Öffnen liefert neue Werte. Für reine Informationsdateien sind Write, Truncate, Rename und Delete verboten. Verzeichnisse und Stat müssen über dieselbe Dateischnittstelle funktionieren wie bei normalen Dateien. Virtuelle Dateien können Länge 0 melden; der Dateibetrachter muss trotzdem bis EOF innerhalb seines Limits lesen.

Nicht jede interne Cosmos-Struktur ist öffentlich abfragbar. Die erste Umsetzung erstellt eine Verfügbarkeitsmatrix pro obigem Pfad: vorhandene Quelle, benötigter schmaler Adapter oder konkrete Upstream-Lücke. Fehlende Schnittstellen werden als offene Arbeit sichtbar, nicht mit Dummywerten verdeckt. Keine privaten Speicherstrukturen per unsicherem Cast auslesen. Erweiterungen an Cosmos möglichst als schmale, prüfbare Schnittstelle vorschlagen; kein ungefragtes Abhängigkeitsupdate.

Der Kernel bleibt kompiliert. Die tatsächlich gestartete Kernel-Binärdatei kann unter `/boot` betrachtet werden, sofern von dort gestartet bzw. als identisches Artefakt verfügbar. Es gibt keine automatisch vorhandenen Cosmos-Quelldateien im RAM. Quellen können optional als versioniertes Paket unter `/usr/src` geliefert werden; Versionsbezug und Lizenzen gehören dazu. Systeminformationen sind zunächst lesbar. Schreibbare Steuerdateien sind ein späterer, jeweils explizit entworfener Ausbau.

## Sicherheit und Kompatibilität

- Ein neuer Root-Baum macht Systemdateien sichtbar, aber nicht pauschal les- oder schreibbar. Kontohashes, Schlüssel, TLS-Geheimnisse, fremde private Dateien und Rohspeicher bleiben geschützt und fehlen in Diagnoseansichten.
- Vor ext2-Freigabe: eindeutige Pfadidentität, Case-Sensitivität und Linkauflösung mit ACLs und Wasm-Grenzen zusammenführen. Bei fehlender sicherer Auflösung den betroffenen Zugriff verweigern. Links nicht durch bloßes Entfernen eines Shellbefehls als verhindert ansehen; importierte Images können sie schon enthalten.
- Eine einzige maßgebliche Rechteprüfung; native UID/GID nicht zusätzlich als wirksame Mehrbenutzersicherheit versprechen. Bestehende ACLs zunächst erhalten, danach gezielt auf die neue Identität abbilden.
- Paket-Apps erhalten keine neue Sicht auf `/proc`, `/sys`, `/dev` oder fremde Home-Verzeichnisse. Eventuell parallel entstehender App-Schreibspeicher ist separat abzustimmen und instanzgebunden zu halten.
- `/run` und `/tmp` benötigen Limits und getrennte Benutzerbereiche; keine unbegrenzten RAM-Dateien. Protokolle werden begrenzt und bereinigt.
- Alle persistenten Dateisysteme vor Energieaktionen in Abhängigkeitsreihenfolge synchronisieren/aushängen; Fehler brechen Shutdown/Reboot weiterhin ab. Virtuelle Mounts und offene Handles berücksichtigen.

## Migration und Freigabe

Keine In-place-Konvertierung und kein automatisches Formatieren beim Boot. Bestands-FAT bleibt erhalten, Migration erfolgt auf eine neue Platte mit Protokoll und Rückfallweg. Zuerst nur lesend inventarisieren, freien Platz prüfen, dann Dateien, Konten/ACLs, Paketvertrauen und Konfiguration übernehmen und gegenprüfen. Unterbrechung darf niemals eine neue Administrator-Ersteinrichtung über bestehende Konten auslösen.

Bei heutigen Installationen liegen Bootartefakte und Daten gemeinsam auf der FAT-ESP. Für die Migration ist deshalb eine zweite, neue Platte mit dem Ziel-Layout nötig; der Installer-Layoutteil muss vor der Root-Migration fertig sein. Ist das ext2-Root nicht einhängbar, startet eine unabhängig von ext2 verfügbare Recovery von FAT/ISO, ohne Konto-Neuanlage und ohne Schreibzugriff auf das defekte Root. Gerätesuche darf weder `sata0` noch den VMware-Standardcontroller voraussetzen: Das derzeitige VMware-Profil ist ohne Datenplatte; ein geprüftes AHCI-/NVMe-Profil und explizite Geräte-/Partitionsauswahl gehören vor die VMware-Speicherabnahme.

Parallel geplanter App-Schreibspeicher (ClassiCube-Etappe 13.4) wird vor seiner Freigabe mit dieser Pfadentscheidung abgeglichen: Ziel `/var/lib/zehos/apps/<id>`, zusätzliche Benutzer-/Instanztrennung nach dessen Sicherheitsvertrag. Ein gegebenenfalls schon implementierter `/data`-Pfad erhält eine explizite Migration. Dieser Plan erweitert selbst keine Wasm-Schreibrechte und setzt die App-Erweiterung nicht voraus.

Vorgeschlagene Zuordnung: `/data/home` → `/home`, Sicherheitsdaten → geschütztes `/etc/zehos`, Paketablage → `/var/lib/zehos/packages`, persönliche Desktopeinstellungen → jeweiliges `.config/zehos`, Bootartefakte → EFI-Partition. Sonstige Benutzerdateien erhalten einen explizit protokollierten Zielort unter `/mnt/legacy-data` bzw. einem übernommenen Datenverzeichnis. Keine stillen Überschreibungen. Pfadkonflikte und ungültige Metadaten stoppen die Freigabe.

Abnahme: Hostprüfungen plus nativer Build und tatsächliche Gasttests mit 3.0.97; QEMU und VMware getrennt protokollieren. Je 32/64/128 GiB schreiben, erneut lesen, umbenennen, löschen, voll laufen lassen und nach sauberem Neustart prüfen. ext2 zusätzlich nach Gastschreibvorgängen offline mit `e2fsck -fn` prüfen. Abbruch-/Stromausfallversuche ausschließlich mit isolierten Kopien; kein Journal und keine behauptete Stromausfallsicherheit. Quellen-/Lizenzablage nur als optionalen Paketumfang bewerten.

Umsetzungsfolge und Prüfschritte: [Arbeitsplan](../plans/2026-11-17-storage-linux.md).
