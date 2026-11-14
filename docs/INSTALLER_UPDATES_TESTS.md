# Installer und Updates: Backendprüfung vom 13. November 2026

## Umfang und Umgebung

Cosmos Gen3 **3.0.94**, BouncyCastle **2.7.0**, SDK **10.0.112**, Linux x64, QEMU **10.2.2**, OVMF aus `/home/laser/Projects/.cosmos-tools/usr/share/edk2/x64/`. Keine Paket-, SDK- oder Werkzeugversion wurde geändert. Alle Images, Firmware-VARS-Kopien, Payloads und Gastprotokolle liegen unter dem ignorierten `.tools/e2e/`. Es wurden ausschließlich frisch angelegte beziehungsweise für diesen Ablauf angelegte Testimages beschrieben.

Die neue Installation arbeitet über `IInstallService` und `IInstallJob`; der vorgegebene Vertrag ist unverändert. `InstallService` übernimmt Zielschutz und `InstallJob` die Zustände und 64-KiB-Kopierschritte. `CosmosInstallService`/`CosmosInstallTarget` binden Ressourcen, MBR, FAT und Geräte-Puffer an. Erfolgsabschluss und Fehlerbereinigung geben den eigenen Mount wieder frei. Der spätere grafische Assistent ist nicht Bestandteil dieser Backendänderung; der Installer startet vorerst zur Textshell.

Der Updatedienst trennt Quelle, Prüfung und Dateiaustausch. `FileUpdateTarget` verwendet die erweiterte bestehende `FileService`-Ersetzungslogik mit Wiederherstellung und fester `.bak`. Die Laufwerkswurzel ist im Gast `/data`; physische Bootpfade `/boot/...` entsprechen daher `/data/boot/...`. Die administrativen Shellbefehle prüfen ihre Berechtigung vor jedem Dienstzugriff. Das Live-Installer-ISO stellt eine lokale Einrichtungssitzung ohne persistente Benutzerkonten bereit.

## Builds und Hosttests

Die folgenden Befehle wurden vom Repository-Stamm mit den vorhandenen Werkzeugen ausgeführt:

```bash
export PATH=$PATH:$HOME/.dotnet/tools:$HOME/.cosmos/tools/qemu:$HOME/.cosmos/tools/xorriso
dotnet --version
cosmos check
dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:Version=0.1.0
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:Version=0.1.0 -p:ZehOSRecovery=true
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:Version=0.1.0 \
  -p:ZehOSInstaller=true -p:ZehOSPayloadDir="$PWD/.tools/e2e/payload"
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -p:Version=0.2.0
```

Ergebnis: **260 Hosttests bestanden**, alle vier nativen Varianten **0 Warnungen, 0 Fehler**. `cosmos check` bestätigt die benötigten Werkzeuge; `dotnet --version` liefert `10.0.112`. Zusätzlich wurde der Installer mit `-p:ZehOSRecovery=true` gebaut und im Gast zur erfolgreichen Installation verwendet.

Vor jedem weiteren Kernelbuild wurden die Ausgaben aus `ProjektZeh/bin/Release/net10.0/linux-x64/` getrennt nach `.tools/e2e/verified-normal`, `verified-recovery`, `verified-installer` und `verified-update` kopiert. Die Installer-Payload enthält den normalen 0.1.0-ELF und das unveränderte `BOOTX64.EFI` aus `cosmos.build.common/3.0.94/tools/Limine`. Die Standardversion ohne `-p:Version` wurde mit `dotnet msbuild ProjektZeh/ProjektZeh.csproj -getProperty:Version` als `0.1.0` geprüft.

Hosttests prüfen insbesondere strikt geparste Versionen und Manifestbytes, gültige und manipulierte Signaturen, gleiche/ältere Versionen, falsche Größe und SHA-256, lokale Offlinepakete ohne Netzwerk, gesperrte Nichtadministratoren, HTTP-Fehler und begrenzte Antworten, Austausch mit bestehender Sicherung und Wiederherstellung nach Umbenennungsfehlern. Installationsprüfungen erfassen Kopiergrenzen, Zustandsfortschritt, Abbruch nach Fehlern, Mindestgröße, Startmedium und explizite Löschbestätigung. Nach Beginn des Austauschs bleibt ein weiterer Updateversuch bis zum Neustart gesperrt, auch wenn Austausch oder Versionsmarker einen Fehler melden.

## QEMU-Ablauf

Die erweiterten Gastprüfungen liefen mit **TCG**, da `/dev/kvm` in dieser Sitzung nicht verfügbar war. Der direkte QEMU-Aufruf umgeht die Ablehnung zusätzlicher Argumente durch `cosmos run`. Dies ist die tatsächlich verwendete Konfiguration des abschließenden Installationslaufs:

```bash
truncate -s 256M .tools/e2e/verified-install256.img
cp /home/laser/Projects/.cosmos-tools/usr/share/edk2/x64/OVMF_VARS.4m.fd .tools/e2e/verified-install-vars.fd
$HOME/.cosmos/tools/qemu/qemu-system-x86_64 \
  -machine q35 -accel tcg -cpu max -m 512 \
  -drive if=pflash,format=raw,readonly=on,file=/home/laser/Projects/.cosmos-tools/usr/share/edk2/x64/OVMF_CODE.4m.fd \
  -drive if=pflash,format=raw,file=.tools/e2e/verified-install-vars.fd \
  -drive if=none,id=osdisk,format=raw,file=.tools/e2e/verified-install256.img \
  -device ide-hd,drive=osdisk,bus=ide.0 \
  -drive if=none,id=setup,media=cdrom,format=raw,file=.tools/e2e/verified-installer/ProjektZeh.iso \
  -device ide-cd,drive=setup,bus=ide.1 -boot order=d \
  -display none -serial file:.tools/e2e/verified-install-serial.log \
  -qmp unix:.tools/e2e/verified-install.qmp,server=on,wait=off \
  -netdev user,id=net0 -device e1000e,netdev=net0 \
  -action shutdown=poweroff,reboot=reset
```

`truncate` wurde jeweils nur nach Prüfung verwendet, dass der neue Imagename noch nicht existiert. QMP sendete echte Tastaturereignisse; Bildschirmaufnahmen und serielle Diagnosen dienten der Kontrolle. Der Plattenstart verwendet dieselben Firmware-/Plattenargumente, entfernt beide `setup`-Argumente und startet mit `-boot order=c`.

Der geprüfte Ablauf:

1. `setup list` bietet `sata0` mit 268435456 Bytes und null Partitionen an. `setup install sata0` führt Partitionierung, Formatierung, Kopieren, Bootkonfiguration und Aushängen erfolgreich aus; anschließend `shutdown`.
2. `sfdisk --json` bestätigt einen MBR und genau eine Partition vom Typ `ef`, Start 2048, Länge 522240 Sektoren. Ohne ISO startet ZehOS 0.1.0 und bindet die Partition über das unveränderte `Mount("sata0")` unter `/data` ein. Die lokale Administratoranlage gelingt.
3. Der 0.2.0-Kernel wird mit einem Manifest und einer 64-Byte-Signatur auf dem Host bereitgestellt. Signiert wurde mit `openssl pkeyutl -sign -rawin -inkey "$HOME/.config/zehos/update-signing.pem" -in .tools/e2e/update020/update-manifest.txt -out .tools/e2e/update020/update-manifest.sig`. Der private Schlüssel wurde nicht in das Repository kopiert.
4. `python3 -m http.server 8000 --bind 0.0.0.0 --directory .tools/e2e/update020` bedient `update source 10.0.2.2:8000`, `update check` und `update install`. Der Gast erhält per DHCP `10.0.2.15`; der Server protokolliert erfolgreiche HTTP/1.0-GETs für Manifest, Signatur und Kernel. Der Gast bestätigt Signatur/Version und erfolgreiche Installation.
5. `reboot` startet den installierten 0.2.0-Kernel; `/data` bleibt eingebunden und das Konto ladbar. Nach sauberem Shutdown sind aktiver ELF und `.bak` SHA-256-identisch zu den gebauten 0.2.0- und 0.1.0-Payloads. Der Versionsmarker enthält `0.2.0`.
6. Im tatsächlichen Limine-Menü wird **ZehOS (vorherige Version)** ausgewählt. Der Gast startet 0.1.0 und mountet `/data` erneut. Die Menüdatei enthält beide verlangten Namen, drei Sekunden Timeout und 1024×768×32.

Weitere Gastprüfungen: Installer plus Recovery auf einer zweiten frischen Platte; ein zusätzlich als SATA-Platte angeschlossenes ISO erscheint nicht in `setup list` und wird auch bei ausdrücklichem Namen und `--erase` als Startmedium abgewiesen. Sein SHA-256 bleibt unverändert. Eine erneute Installation auf die eingerichtete Zielplatte ohne `--erase` wird verweigert. Normal- und Recovery-ISO ohne Datenplatte starten; `version`, `echo`, `uptime`, F4/Esc im normalen Build und geordnetes `shutdown` funktionieren. Ein Rückfallstart ersetzt keine Dateien und behält die beiden Kernel bei.

## Grenzen und fehlgeschlagene Vorprüfungen

Die Sandbox blockierte zunächst den lokalen VSTest-Socket und den MSBuild-Taskhost. Die autorisierten Prüfungen wurden anschließend mit dem erforderlichen lokalen Socketzugriff erfolgreich ausgeführt. Ein anfangs zu streng formulierter HTTP-Test erwartete exakt `IOException` statt der zutreffenden Unterklasse `EndOfStreamException`; die Assertion wurde korrigiert. Ein weiterer Test hatte versehentlich schon die Konstruktion einer Quelle als Netzwerkzugriff behandelt; nun prüft er den tatsächlichen Lesezugriff. Beide Tests bestehen im genannten Gesamtstand.

`pwsh` ist nicht installiert. Die PowerShell-Helfer wurden daher nicht ausgeführt; direkte QEMU-Läufe prüften geordnetes Shutdown und Reboot mit `-action shutdown=poweroff,reboot=reset`. Kein zusätzlicher GDB-/Energie-Helfertest wurde behauptet.

VMware und echte Hardware bleiben offen. Im Gast wurden numerische IPv4-Quellen und `e1000e` verwendet; die DNS-API und die vorhandenen `E1000EDriver`-/`VirtioNetDriver`-Typen wurden am installierten Paket geprüft, aber DNS-Namensauflösung und virtio-net nicht im Gast nachgewiesen. Das vollständig lokale Update ist hostgetestet; eine gesonderte Offline-Gastprüfung steht aus. Der Installer unterstützt als Startmedium das ausgelieferte ISO, einschließlich roh auf einen Datenträger geschriebener ISO-Kopien; eine auf FAT entpackte Installation des Live-Mediums ist nicht unterstützt. Stromausfallsicherheit, BIOS-Start installierter Systeme und Datenmigration gehören nicht zum Entwurf.

## Grafischer Assistent nach der Zusammenführung

Nach Zusammenführung von Backend, Release-Werkzeugen und `SetupWizard` wurde der vollständige Ablauf erneut mit den Release-Artefakten aus `scripts/build-release.sh 0.1.0` und `0.2.0` geprüft (QEMU 10.2.2, KVM, `q35`, OVMF, 512 MiB, `e1000e`). 275 Hosttests und die Python-Helfertests bestehen.

1. `ZehOS-Setup-0.1.0.iso` startet direkt in den Assistenten. Ziel war eine frische 256-MiB-Platte mit vorhandener Linux-Partition (`sfdisk`, Typ `83`). Das ISO-Laufwerk erscheint nicht in der Liste; die vorhandene Partition löst Warnung und die Pflichtbestätigung „Alle Daten auf sata0 löschen“ aus. Hinweise, Bestätigung, Fortschritt und Abschlussseite wurden per QMP-Tastatureingaben bedient; „Neu starten“ löst den Neustart aus.
2. Danach enthält die Platte genau eine Partition Typ `ef` ab 2048. Ohne ISO startet ZehOS 0.1.0, bindet `/data` ein und verlangt die Administratoranlage.
3. `scripts/sign-update.py` signiert das 0.2.0-Manifest, `scripts/update-mirror.py --dir` stellt es bereit. `update check` gegen einen fremden Dienst auf Port 8080 meldet sauber `404 Not Found`; nach `update source 10.0.2.2:8090` folgen geprüfte Version, `update install` und nach `reboot` ZehOS 0.2.0.
4. Im Limine-Menü startet **ZehOS (vorherige Version)** wieder 0.1.0.
5. `ZehOS-0.2.0.iso` startet ohne Datenplatte bis Textshell und Desktop.

Nicht erneut geprüft: Recovery-Build nach der Zusammenführung (die Verdrahtung betrifft nur `ZEHOS_INSTALLER`), VMware, echte Hardware, Mausbedienung des Assistenten im Gast und der GitHub-Release-Workflow selbst.

## Korrekturen nach dem unabhängigen Review

Das Review fand keine Blocker. Behoben und erneut geprüft (278 Hosttests, 11 Python-Tests, native Releases 0.1.0/0.2.0 mit 0 Warnungen, QEMU/KVM/OVMF):

- **Rückfallstart verdrängt den funktionierenden Kernel:** `update check` vergleicht zusätzlich mit `/boot/zehos-version.txt`. Nach einem Rückfallstart muss das Update neuer als die installierte Version sein und ersetzt nur den Hauptkernel; `.bak` bleibt. Im Gast nach Start von **ZehOS (vorherige Version)** (0.1.0 bei installierter 0.2.0) geprüft: Das erneute Angebot von 0.2.0 wird verweigert. Das Einspielen einer neueren Version im Rückfallstart ist hostgetestet.
- **Daten ohne erkannte Partition:** Leer gilt nur eine vollständig als leer gelesene Platte bis 1 GiB (zweites Review durch Codex: Randprüfung genügte nicht); sonst verlangen Assistent, `setup install` und `InstallService` die Löschbestätigung. Im Gast: frische leere Platte ohne Bestätigung, eine ganze FAT32-Platte ohne Partitionstabelle wird von Cosmos als eine Partition erkannt und ebenfalls bestätigungspflichtig. Der Fall „Daten ohne Partition“ ist hostgetestet, nicht im Gast nachgestellt.
- **Versionsmarker:** Er wird vor dem Kernel geschrieben und nach einem Austauschfehler zurückgesetzt, damit ein Rückfallstart nie als normaler Start erscheint (hostgetestet).
- **Download-Grenze:** Der Kernel wird höchstens in der signierten Manifestgröße gelesen; das Manifest begrenzt die Größe auf 64 MiB.
- **Rückfalleintrag nach Neuinstallation:** Der Installer schreibt auch `ProjektZeh.elf.bak`; der Eintrag startet im Gast direkt nach der Installation.
- **Workflow:** Kein `workflow_dispatch` mehr; Releases entstehen nur aus `v*.*.*`-Tags.
- **Doppelter Fehler beim Ersetzen:** Die ältere Sicherung bleibt unter ihrem Ausweichnamen, solange das Original noch unter `.bak` liegt.
- **Installer-Build:** registriert `update` nicht mehr.
- **Neu gefunden:** Nach einem Warmstart hing die erste Updateverbindung; Ursache und Abhilfe siehe `docs/COSMOS_SUPPORT.md`. Danach geprüft: Installation, Updateprüfung, `reboot`, erneute Prüfung, Installation und Start von 0.2.0.

Offen: Zeitlimit bei einem Spiegel, der nach Verbindungsaufbau nicht antwortet; Schutz des Signaturgeheimnisses über eine GitHub-Umgebung mit Tag-Regel (Repository-Einstellung).

## Abschluss nach Rebase auf master und Desktop-Stil (14. November 2026)

Der Branch wurde auf master `aa35e79` (Benutzerverwaltung #27, Desktop-Einstellungen #28) umgesetzt. Der Assistent verwendet jetzt Theme, Hintergrund, Fensterrahmen und `UiButton` des Desktops und skaliert für 1920×1080; das installierte Bootmenü folgt dem ISO (Full HD, 1024×768, vorherige Version). Ein weiteres Codex-Review fand zwei Punkte, beide behoben: Der Versionsmarker wird vor dem Kernel geschrieben und nach einem Austauschfehler zurückgesetzt; leer gilt nur eine vollständig gelesene Platte bis 1 GiB.

Nachweise: 312 Hosttests und 11 Python-Tests; native Builds 0.1.0/0.2.0/0.3.0 (normal und Installer) sowie Recovery jeweils 0 Warnungen/0 Fehler. Recovery startet in die Textshell. Vollständiger Gastablauf in einem Skript (QEMU/KVM, OVMF, 1024 MiB, `e1000e`, frische 256-MiB-Platte):

1. Assistent in Full HD: Hinweise, Zielplatte, Installation, Abschluss; danach genau eine Partition Typ `ef`.
2. Plattenstart 0.1.0, Administratoranlage, `update check`/`update install` über `scripts/update-mirror.py`, `reboot` (Warmstart) → 0.2.0.
3. Rückfalleintrag → 0.1.0; `update check` lehnt 0.2.0 ab („nicht neuer als die installierte Version“); Spiegel mit 0.3.0: Prüfung und Installation im Rückfallstart.
4. `reboot` → 0.3.0; Rückfalleintrag startet weiterhin 0.1.0, `.bak` blieb also unverändert.

Weiterhin offen: VMware, echte Hardware, PowerShell-Helfer (`pwsh` nicht installiert), Zeitlimit bei einem nach Verbindungsaufbau schweigenden Spiegel, ein tatsächlicher Lauf des Release-Workflows auf GitHub sowie die Mausbedienung des Assistenten im Gast.
