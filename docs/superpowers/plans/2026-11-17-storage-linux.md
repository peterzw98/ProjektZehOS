# Größere Datenträger und Linux-Dateibaum – Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 32–128-GiB-Datenträger und einen Linux-ähnlichen Dateibaum einschließlich lesbarer Cosmos-Systemdaten bereitstellen.

**Architecture:** Cosmos 3.0.97 und dessen VFS wiederverwenden. ext2 zunächst qualifizieren, FAT32 für EFI erhalten; dauerhafte Dateien, virtuelle Informationsdateien und Geräte hinter einer gemeinsamen Dateisicht anbieten.

**Tech Stack:** C#/.NET 10, Cosmos 3.0.97, vorhandenes System.IO/VFS, MSTest/Moq, QEMU, VMware; e2fsprogs ausschließlich als externes Prüfwerkzeug.

**Spec:** [Entwurf und Recherche](../specs/2026-11-17-storage-linux.md).

**Status:** Der Benutzer hat die Umsetzung nach Merge von #117 und #118 freigegeben. Etappe 1 hat ein **No-Go für den Cosmos-3.0.97-ext2-Formatierer** ergeben; die Folgeetappen sind gesperrt. Größenbereich und Cosmos-Dateisicht sind bestätigt, ext2/GPT bleiben bedingt zu qualifizierende Empfehlungen. Befunde, ausgeführte Prüfungen und offene Entscheidungen stehen in [STORAGE_3_0_97_FINDINGS.md](../../STORAGE_3_0_97_FINDINGS.md). Nach einer geprüften Formatterlösung die Eignungsprüfung wiederholen und Folgeetappen anhand der tatsächlichen APIs konkretisieren.

## Globale Vorgaben

- Zielversion **Cosmos 3.0.97**, Datenträger **32, 64, 128 GiB**; laufendes Cosmos-Update nicht in diesen Auftrag ziehen.
- Bestehende 64-MiB-Testplatte und `disk init`-Schutz unverändert erhalten. Neue Images nur unter explizitem neuem Pfad mit CreateNew erzeugen.
- Keine Datenmigration ohne Rückfallkopie; kein automatisches Formatieren, keine In-place-Konvertierung.
- Vorhandene Result-Typen, Fehlerursachen und explizite Ressourcenfreigabe verwenden; Unwind-Verhalten auf 3.0.97 nachweisen.
- Sicherheit und hardwarenahe Logik beim Hauptagenten belassen. Lokaler Agent kann nach Festlegung der Schnittstellen klar begrenzte Tests oder Dokumentation übernehmen; Resultate selbst prüfen.
- Jeder spätere Implementierungs-PR: Version gegenüber aktuellem Zielbranch erhöhen, unabhängiges Review, Push und PR gemäß AGENTS.md; kein Merge ohne Auftrag.

## Besonders zu prüfende Fehlerfälle

1. Große Platte, kleine Datei: keine Kapazität großen Arrays zuweisen; überlaufsichere LBA-/Größenrechnung.
2. Symlink, Case-Variante oder Mountwechsel: keine ACL- oder Wasm-Grenze umgehen.
3. Volle Platte oder unterbrochene Migration: bestehende Konten/Dateien erhalten, niemals neue Ersteinrichtung auslösen.
4. Virtuelle Datei mit Länge 0 und dynamischem Inhalt: begrenzt bis EOF lesen, konsistente Teilreads, kein Hängen.
5. Unmount-/Flushfehler oder offener Handle: Energieaktion abbrechen und Shell bedienbar halten.

## Abhängigkeiten und IRC-Unterstützung

Auf Benutzerwunsch beginnt die Codeumsetzung erst nach Merge des Cosmos-Updates #117 und des Planungs-PRs #118. Umsetzungshilfe und unabhängiges Endreview sind im IRC angefragt; vor Arbeitsbeginn Helfer, Dateien, Branches und Abnahme ausdrücklich vereinbaren. IRC-Hinweise werden am owning source geprüft, sie ersetzen keine eigene Bewertung.

Reihenfolge: **1 → 2 (ext2 nur isoliert) → 3 → 7a (Installer-Layout und ext2-unabhängige Recovery) → 4 → 5/6 → 7b (Updates/Energieaktionen) → 8**. Etappe 7 wird deshalb in zwei überprüfbare Lieferstände aufgeteilt. Große Datenplatten allein brauchen die Root-Migration nicht. App-Schreibspeicher ist ein paralleler Auftrag, dessen Pfade vor seiner Aktivierung abzustimmen sind.

## Etappe 1: Eignung von 3.0.97 und ext2 nachweisen

**Dateien:** neu `docs/STORAGE_3_0_97_FINDINGS.md`, `scripts/test-storage-filesystems.py`, `scripts/tests/test_storage_filesystems.py`; experimenteller Gastadapter erst nach API-Abgleich unter `experiments/StorageProbe/`. Keine neue Produktionsabhängigkeit.

- [ ] In isoliertem Branch vom integrierten Update ausgehen; `dotnet --version`, `cosmos check`, csproj und NuGet-Commit festhalten. Tatsächlich verwendete Version muss 3.0.97 sein.
- [ ] Vorhandene Cosmos-Verträge `IVfsFileSystemType`, `IVfsSuperblock`, `IVfsInode`, Datei-/Inodeoperationen und Gpt aus genau diesem Paket auslesen. Root-Mount, nested Mounts, Sichtbarkeit von Mountpunkten bei Enumeration und read-only testen.
- [ ] Tests des Imagehelfers zuerst schreiben und rot ausführen: vorhandener Zielpfad bleibt byteidentisch, ungültige Größe wird vor jedem Schreibzugriff verweigert, Größenberechnung für 128 GiB ist exakt `137438953472` Byte. Danach minimal implementieren; `python -m unittest discover -s scripts/tests -p test_storage_filesystems.py` muss grün sein.
- [ ] Ausschließlich neue sparse/qcow2-Testimages für 32/64/128 GiB verwenden. 512-Byte-Sektoren, GPT + FAT32-ESP + ext2; ext2 zunächst mit 4096-Byte-Blöcken. Host-Formatter und Cosmos-Formatter getrennt prüfen, Featureflags dokumentieren.
- [ ] Jedes frisch formatierte ext2-Image unmittelbar und vor Gastschreibzugriff mit `e2fsck -fn` prüfen. Formatter-Superblockkopien/Gruppenlayout ausdrücklich untersuchen; Fehler bedeutet No-Go für diesen Formatter, kein automatisches Reparieren und Weiterverwenden. Hostformatierung ist nur mit unabhängig geprüftem Featurevertrag eine Alternative.
- [ ] Feature-Allowlist aus tatsächlich unterstützten 3.0.97-Funktionen ableiten und Tests mit unbekannten Incompat-/RoCompat-Bits ergänzen: unbekanntes Incompat muss vor Mount scheitern, unbekanntes RoCompat vor jedem Schreiben; RO nur bei belegtem sicherem Lesen. Nicht unterstützte Features dürfen nicht durch Setzen eines Mountflags als gelöst gelten.
- [ ] Im Gast Create/Read/Write/Append/Seek/Rename/Delete/Mkdir/Rmdir, Unicode-Namen, Case-Unterschiede, Symlinks, Zeitwerte, freie Blöcke/Inodes, read-only und ENOSPC prüfen. Größe/Offets über 2 GiB prüfen; eine Datei über 4 GiB als Fähigkeitsprobe separat protokollieren, kein pauschales Freigabeversprechen.
- [ ] Zunächst höchstens `2147483647` Byte je Datei freigeben; Grenzüberschreitung muss ohne Metadatenkorruption verweigert werden. Größere Dateien nur in der isolierten Probe zusammen mit `large_file`-Flag, Wiederöffnung und Offlineprüfung untersuchen. Ergebnisse zum Größen-Highword allein heben die Grenze nicht auf.
- [ ] Nach Flush, Unmount und Neustart Inhalte/Hashes prüfen; Offlineprüfung `e2fsck -fn <isoliertes-ext2-Partitionsimage>` ohne Reparatur. Identisches Vorgehen unter QEMU und VMware; fehlende Werkzeuge als offen melden.
- [ ] Fehler-/Abbruchfälle auf Kopien prüfen, Speicherverbrauch und Dauer messen. Für ext2, Formatter und VFS jeweils Go/No-Go dokumentieren. Bei Fehlschlag Folgeetappen anhalten und Ursache bzw. Alternative neu entwerfen; kein stiller exFAT-/ext4-Port.

**Ergebnis:** Ein belegter unterstützter Teilumfang und konkrete APIs, kein bloß erfolgreicher Mount. Erst danach ext2 als Standard festlegen.

## Etappe 2: Größere Datenträger sicher verwendbar machen

**Dateien:** `scripts/start-qemu.ps1`, `scripts/start-vmware.ps1`, `ProjektZeh/Storage/{CosmosStorageService,IStorageService,StorageDeviceInfo}.cs`, `ProjektZeh/Shell/FileShellCommands.cs` (tatsächlichen disk-Aufrufer erneut prüfen), neue gezielte Hosttests; README.

- [ ] Alle Storage-Aufrufer und VM-Tests erfassen. Bestehende Parameter/Befehle erhalten; explizite Auswahl eines vorhandenen größeren Images ergänzen. Ohne neue Option bleibt das alte Bestandsimage unverändert der Standard.
- [ ] Festes `sata0` beim Boot und das bisher plattenlose VMware-Profil berücksichtigen: explizite sichere Geräteauswahl, separater AHCI-/NVMe-Testdatenträger mit geprüftem Controller. Keine Annahme, VMware-Standard-SCSI werde von Cosmos erkannt. Zeitbudget anhand der beobachteten I/O-Stapelgröße messen statt 128 GiB vollständig ohne Anlass zu lesen.
- [ ] Tests zuerst: Bestandsimage mit Prüfsumme unangetastet; >32-Bit-Bytegrößen korrekt anzeigen; mehrere Partitionen einschließlich ESP eindeutig auswählen; unbekanntes/ungültiges Format nicht schreibend „probieren“.
- [ ] Unterstützten Mountpfad für FAT aus Etappe 1 ergänzen. ext2 in dieser Etappe ausschließlich in der isolierten Probe anbieten; allgemeine Einbindung und Aktivierung erst nach bestandener Etappe 3. Datenpartition über gespeicherte Identität/Layout auswählen, nicht über `partitions[0]` oder ausschließlich wechselnde Gerätenamen.
- [ ] Hosttests `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`; VM-Helfertests und `scripts/test-power-commands.ps1` nach Startkonfigurationsänderung ausführen.
- [ ] 32/64/128-GiB-Mount und Kapazitätsanzeige in beiden VMs abnehmen. Bestehenden 64-MiB-Weg gegenprüfen und Stand committen.

**Ergebnis:** Große Datenplatten funktionieren unabhängig von der späteren Root-Migration. Ein zwischenzeitlicher PR darf ext2 erst nach der Rechteetappe allgemein freigeben.

## Etappe 3: Pfadidentität und Rechte für den neuen Baum

**Dateien:** `ProjektZeh/Security/{FileAccessControl,AccountService}.cs`, `ProjektZeh/Storage/FileService.cs`, `ProjektZeh/Runtime/Wasm/WasmPackagePaths.cs`, betroffene Tests `FileAccessControlTests`, `SecuredFileServiceTests`, `AccountServiceTests`, `WasmPackagePathsTests` und Test-csproj.

- [ ] Tests zuerst für getrennte ext2-Dateien `A`/`a`, FAT-Aliase, relative Pfade, Präfixnachbarn `/home/a` und `/home/ab`, Links in Zwischenverzeichnissen, Linkschleifen und mountübergreifende Pfade ergänzen. Verwalteten Root `/`, direkte Kinder `/etc` und `/home` sowie die vollständige Elternrechteprüfung bis `/` testen; `RootDirectory + Separator` darf hier nicht den falschen Präfix `//` erzeugen.
- [ ] Eine maßgebliche Pfad-/Rechteentscheidung auf Basis des tatsächlich aufgelösten Ziels einführen. Cosmos-Auflösung wiederverwenden, sofern sicher zugänglich; andernfalls unsichere Zugriffe verweigern und erforderliche API-Lücke dokumentieren. Keine parallele zweite ACL-Wahrheit durch UID/GID.
- [ ] Für Systemkonfiguration, Kontendaten, Logs, Benutzer- und Paketdaten Lese-/Schreibrechte ausdrücklich festlegen; unprivilegierte Nutzer dürfen keine Systemdateien verändern oder Kontohashes lesen.
- [ ] Hosttests rot/grün und Gasttests gegen vorab auf dem Host erzeugte Symlinks ausführen. Wasm darf weder `/etc` noch `/proc`, `/sys`, `/dev` neu erreichen.

**Ergebnis:** Die neue Dateisicht wird freigegeben, ohne bestehende Zugriffstrennung aufzuheben. Freigabebedingung für Etappen 4–7.

## Etappe 4: Root-Baum, Konfiguration und nichtdestruktive Migration

**Dateien:** neu `ProjektZeh/Storage/SystemPaths.cs`, `Storage/LayoutMigration.cs` mit Hosttests; `Kernel.cs`, `AccountService.cs`, `Packages/PackageStore.cs`, `Graphics/DesktopSettings.cs`, `Apps/Settings/SettingsApp.cs`, `Apps/FileManager/FileManagerApp.cs`, `Update/{FileUpdateTarget,UpdateController}.cs`, `Runtime/CosmosSystemInfo.cs` und sämtliche per Suche gefundenen `/data`-Aufrufer/Fixtures.

- [ ] Verantwortlichkeiten aus dem Entwurf als feste Pfade zentralisieren; keine frei konfigurierbare Pfadplattform bauen. Produktionsaufrufer und Tests gemeinsam umstellen, nicht nur Shell-Aliase erzeugen.
- [ ] Vor Implementierung Tests für Altbestand → neuer Baum schreiben: Konten/ACLs, Homes, Pakete, Desktopeinstellungen, sonstige Dateien, Zielkonflikte, ENOSPC und Abbruch vor/nach jedem Migrationsschritt. Wiederholung muss sicher sein.
- [ ] Migration nur auf ein neues Ziel; Inventar und Prüfsummenprotokoll, gesperrte Rechte während Übernahme, überprüfter Abschlussmarker. Fehler oder unvollständige Metadaten müssen gesperrte Recovery ergeben, niemals Administrator-Neuanlage. Alte Platte unverändert bootfähig lassen.
- [ ] ext2 als `/` einhängen, darunter echte Mountpunkte für `/boot`, `/dev`, `/proc`, `/sys` und spätere Fremdplatten bereitstellen. RAM-Backends für `/run` und `/tmp` nur mit Kapazitäts-/Benutzergrenzen; nach Neustart leer.
- [ ] Normale Benutzer starten in `/home/<name>`, persönliche Konfiguration wandert nach `.config/zehos`. Kein künstliches root-Konto anlegen; `/usr/bin` nur für tatsächlich vorhandene Starter befüllen.
- [ ] Paketvertrauen, Updatepfade, unbekannte Dateien und Daten außerhalb der bisherigen ACL-Verwaltung separat prüfen. Dokumentation und relevante Tests umstellen; vollständige Host- und Gastprüfung, danach committen.
- [ ] Vor Beginn muss Etappe 7a abgenommen sein: zweite neue Platte mit Ziel-Layout und funktionierender FAT-/ISO-Recovery bei nicht einhängbarem ext2-Root. Fehlende Root-Platte oder beschädigte Root-Metadaten dürfen niemals Konto-Ersteinrichtung auslösen.
- [ ] Mit dem App-Speicher-Auftrag (ClassiCube 13.4) Pfad `/var/lib/zehos/apps/<id>`, Benutzer-/Instanztrennung und Migration eventuell vorhandener `/data`-App-Daten abstimmen. Keine neuen Wasm-Rechte allein durch die Root-Migration vergeben.

**Ergebnis:** Durchgängiger neuer Dateibaum mit überprüfter Rückfallmöglichkeit, kein Layout allein aus leeren Ordnern.

## Etappe 5: Cosmos-Systemdaten als virtuelle Dateien

**Dateien:** neu `ProjektZeh/Storage/Virtual/{SystemInfoFileSystem,SystemInfoSnapshot}.cs` plus Hosttests; `Runtime/CosmosSystemInfo.cs`, vorhandene Log-/Geräteadapter, `FileService.cs` und Datei-Viewer-Aufrufer. Dateien nur weiter aufteilen, wenn konkrete Verantwortung es erfordert.

- [ ] Zuerst Verfügbarkeitsmatrix für jeden `/proc`-/`/sys`-Pfad im Entwurf anlegen: tatsächliche 3.0.97-Quelle, Berechtigung, Aktualisierung, Größenlimit; fehlende APIs benennen. Vorhandene uptime-, Speicher-, Storage- und Diagnosedienste verwenden.
- [ ] Anhand der Etappe-1-Verträge einen echten Cosmos-VFS-Treiber anbinden. Keine Sonderfallimplementierung ausschließlich in `cat`: auch `FileStream`, Verzeichnisauflistung, Stat und Dateimanager müssen dieselben Knoten sehen.
- [ ] Tests zuerst: zwei Teilreads ergeben denselben Snapshot, EOF bleibt EOF, erneutes Öffnen aktualisiert die Werte, `Length == 0` verhindert Lesen nicht, Ausgabe bleibt begrenzt, alle Mutationen werden verweigert. Verzeichnisse lassen keine Schreibdateien entstehen.
- [ ] `/proc/version`, `uptime`, `meminfo`, `mounts`, bereinigtes `log` sowie `/sys/kernel/components`, Geräte und Blockgeräte implementieren. Scheduler-/Threadansicht nur aus belegten APIs; fehlende Werte kenntlich machen. Keine Adressen, Passwörter, Schlüssel oder TLS-Puffer ausgeben.
- [ ] Versionsangaben aus tatsächlichen Buildmetadaten beziehen; optional ausgelieferte Quellen unter `/usr/src` strikt vom Host-Dateibaum trennen. Verweis auf Paket-/Quellstand und Lizenztexte anbieten.
- [ ] Gastabnahme: dieselben Dateien mit Shell und Dateimanager lesen; neue Mounts/Geräte werden beim erneuten Öffnen sichtbar; Deny-Tests für normale Benutzer und Wasm; Boot und Eingabe bleiben bedienbar.

**Ergebnis:** Cosmos-Zustand lässt sich wie gewünscht über echte virtuelle Dateien anschauen. Nicht verfügbare Cosmos-Schnittstellen bleiben ausdrücklich offene Punkte.

## Etappe 6: Geräte über Dateihandles

**Dateien:** neu `ProjektZeh/Storage/Virtual/DeviceFileSystem.cs` und Hosttests; bestehende Input-/Terminaladapter und Wasm-Host nur bei ausdrücklich freigegebenem Bedarf.

- [ ] `/dev/null`: Read liefert EOF, Write akzeptiert die ganze Länge; `/dev/zero`: Read füllt exakt den angeforderten Puffer. Null-/Teilpuffer, Schließen und parallele Handles testen.
- [ ] Über Cosmos-VFS einbinden und mit normalen Streams im Gast prüfen; Geräte nicht mit `StreamReader.ReadToEnd` ohne Limit lesen.
- [ ] Terminalgeräte erst nach separatem Vertrag für sitzungsgebundenes Lesen, nicht blockierende Eingabe und Abbruch konkretisieren. Keine globale Tastatureingabe eines anderen Terminals abgreifen.
- [ ] Blockgeräte-Dateien erst nach definierten Berechtigungen und überlaufsicherem Offset-/Längenvertrag implementieren. Erste Freigabe nur lesend, administrativ, mit exklusiven Regeln gegen parallele Dateisystemzugriffe. Rohschreiben bleibt gesondert freizugeben.

**Ergebnis:** Geräte sind echte Streams; Hardwarezugriff wird nicht durch bloße Sichtbarkeit allgemein freigegeben.

## Etappe 7: Installer, Updates und Energieaktionen

**7a vor Etappe 4:** Layout/Formatierung, Bootpfade und unabhängig vom ext2-Root startbare Recovery einschließlich Fehlertests. **7b nach Etappen 4–6:** Updates, Flush-/Unmount-Reihenfolge und vollständige Energieabnahme.

**Dateien:** `ProjektZeh/Installer/{CosmosInstallService,InstallService,InstallJob}.cs`, `Update/`, `Kernel.cs`, betroffene `Install*Tests`, `Update*Tests`, `scripts/test-power-commands.ps1` und Gastprüfskripte.

- [ ] Tests zuerst: GPT-Anfang/Ende und Ausrichtung innerhalb des Datenträgers; ESP-Typ und FAT32-Bootartefakte; Root ext2; Installationsmedium und Bestandsdaten geschützt. Schlichte Änderung der Partitionszahl-Prüfung genügt nicht.
- [ ] Installer erzeugt auf ausdrücklich gewähltem neuem/gelöschtem Ziel das qualifizierte Layout. EFI/Bootloaderpfade aus Partitionssicht anpassen, Recovery und Rückfallkernel weiter verwendbar halten.
- [ ] Updateziel auf getrennte Boot- und Systempartition ausrichten. Vorhandene Sicherungs-/Wiederherstellungslogik erhalten; Fehler zwischen zwei Dateisystemen als unvollständiges Update behandeln, keine atomare Gesamttransaktion behaupten.
- [ ] Alle persistenten Mounts vor Shutdown/Reboot behandeln; offene Handles, Busy-, Flush- und Unmountfehler reproduzieren. Aktionen müssen bei Fehler abbrechen und die Shell bedienbar lassen.
- [ ] Native Release- und Recovery-Builds, `scripts/test-power-commands.ps1`, frische Installation, Kernelupdate, Rückfall und Neustart auf QEMU und VMware prüfen.

**Ergebnis:** Der neue Baum funktioniert auch nach Installation, Update und Neustart.

## Etappe 8: Gesamtabnahme und Dokumentation

**Dateien:** README, `docs/COSMOS_SUPPORT.md`, `docs/PAKETE_ERSTELLEN.md`, `docs/PACKAGE_FORMAT.md` nur bei Vertragsänderung; ROADMAP erst nach erfüllter Abnahme; neuer datierter Speicher-Prüfbericht.

- [ ] Hosttests mit Warnungen als Fehler ausführen; nativer Build `cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release` und Recovery-Build gemäß AGENTS.md.
- [ ] Matrix 32/64/128 GiB × QEMU/VMware: Boot, Anmeldung, Shell, zwei unabhängige Terminals, Dateimanager, Lesen/Schreiben, Paketinstallation, voller Datenträger, virtuelle Dateien, Update, Shutdown/Reboot. Reale Hardware ausdrücklich ungeprüft lassen.
- [ ] Offlinekonsistenz und Hashvergleich nach sauberen Gastschreibvorgängen prüfen; getrennte Abbruchversuche nur auf Wegwerfkopien. Keine Stromausfallsicherheit behaupten.
- [ ] Grenzen, Wiederherstellung und neue Pfade dokumentieren. Nicht unterstützte POSIX-Funktionen und Cosmos-API-Lücken sichtbar lassen.
- [ ] Unabhängiges lesendes Review mit Anforderungen und aktuellen Prüfergebnissen; berechtigte Befunde beheben und betroffene Prüfungen wiederholen. Cohesive nutzbare Etappen separat pushen/als PR bereitstellen; keine unfertige Root-Migration als Standard aktivieren.

## Nicht Teil der ersten Umsetzung

Kein exFAT-/ext4-Eigenport, kein automatisches Resize, keine Platten über 2 TiB, keine vollständige POSIX-/Linux-ABI, keine automatisch beschreibbaren `/sys`-Steuerdateien und kein allgemeiner Wasm-Zugriff auf Kerneldateien. Optional ausgelieferte Cosmos-Quellen sind ein Paket-/Lizenzthema; Systemzustand als virtuelle Datei bleibt fester Umfang dieses Plans.
