# Übergabe: kompletter Storage-/Linux-Dateibaum-Auftrag

Stand: 17. November 2026, Fortsetzung gegen 10:45 MEZ. **Der Gesamtplan ist nicht fertig.** Formatterkorrektur und sichere ext2-Lesekomponente sind umgesetzt; Pfadrechte und folgende Etappen laufen weiter. Dieses Dokument dient dem ausdrücklich vom Benutzer gewünschten Wechsel der Teamleitung vor einem Nutzungslimit.

## Aktuelle Übergabe vom 17. November, etwa 10:40 MEZ

Dieser Abschnitt hat Vorrang vor den historischen Ständen weiter unten. `feature/storage-linux` enthält hier
Integrationsnotizen und einen älteren Quellbaum: **nicht blind als aktuelle Codebasis verwenden oder in master mergen.**
Für Umsetzung und Tests den unten genannten aktuellen Codebranch beziehungsweise aktuellen `origin/master` verwenden.

Der Benutzer hat die technische Teamleitung wieder an `agent-nweh-storage-resume` gegeben und zuletzt ausdrücklich
erneut eine Übergabe bei niedrigem Restbudget verlangt. Zuletzt waren 84 Prozent des Codex-Fensters verbraucht.
`agent-orchestrator-junior` (Opus, bisher Stellvertreter) hat den Handoff `7d8fde6` gelesen und die technische
Gesamtleitung am 17. November um 10:44 MEZ ausdrücklich übernommen. Laufende Teamaufträge fortsetzen.

### Aktuelle Branches und Zuständigkeiten

| Branch / Stand | Inhalt und nächster Schritt |
| --- | --- |
| `master` zuletzt `f6e2e22`, Version 0.5.14 | #130, #131, #133, #134 und #135 inzwischen extern gemergt; vor jedem PR frisch prüfen |
| `task/storage-ext2-mount-ro` **`1d3b978`**, Version **0.5.15** | Fertige explizite Lesekomponente, Feature-/Geometriegate und Gastprobe; auf `master f6e2e22` umgesetzt. Coordinator öffnet RO-PR nach aktuellem KVM-/Offlinebeleg. Keine Boot-, Shell- oder `/data`-Aktivierung |
| `task/storage-etappe3-path-identity` **`624df67`** | Senior: eigene 7 Commits von `0524f72` auf RO `1d3b978` umgesetzt. Review des Home-Fixes ohne Blocker, Tests dieses rebasten Kopfes laufen. Vor PR aktuelle Base/Version erneut abstimmen |
| `docs/storage-system-files` **`82098b6`** | Geprüfte API-Matrix unter `docs/superpowers/specs/2026-11-17-system-files-inventory.md`; reiner Doku-PR beim Coordinator angefragt, keine Implementierung oder Aktivierung |
| `task/storage-7a-design` **`38c753b`** | Jensen: Installer-/GPT-/Recovery-Entwurf. Entscheidungen unten; noch keine produktive Umsetzung |
| `docs/storage-etappe3` **`25cdfa1`** | Entwurf Pfadidentität/Rechte, Freigaben im IRC bestätigt |

Team: Nachfolger führt Architektur, Integration und Abnahme; Senior implementiert Pfadrechte/ext2, Jensen Installer
und Recovery (zusätzlich SIMD-Auftrag für anderes Team), Huang reviewt unabhängig, aot-probe/tripleclerk testen.
Nur der Sitzungs-Hauptagent verbindet sich mit IRC. Zugangsdaten niemals in Übergabe oder Nachrichten kopieren.

### Tatsächliche Belege für den RO-Code

- Aktueller Code `1d3b978`: Windows-Hosttests mit `-warnaserror` **749 bestanden, 1 übersprungen**
  (Windows-Symlinktest); echte Cosmos-3.0.97-Hostprobe `readonly-selftest` **52 Prüfungen grün**;
  nativer Probe-Build `-p:ZehOSStorageProbe=true` **0 Warnungen / 0 Fehler**.
- QEMU/WHPX auf `1d3b978`, frische 72-MiB-Probeplatte und eigene 64-MiB-Appplatte: **Lauf 1 19 PASS,
  Lauf 2 9 PASS**, jeweils kein FAIL, DONE und QEMU-Exit 0. Image-SHA vor/nach Lauf 2 identisch:
  `142923b0c5fb9a5974443046a519fa2ca7e20bf9d7bee533868490ba243d4bb0`.
- QEMU/KVM auf `1d3b978` vom aot-probe um 10:44 gemeldet: **19/9 PASS, Hash identisch (`eef0e61d…4be8`),
  `e2fsck -fn` Exit 0**, beide DONE/Exit 0. Probe-Build und normaler Release-Build mit Privacy-Scan 0;
  normaler ISO bootet bis „Desktop ist bereit“ (605 ms). Vorher auch `c9ae07d` grün, 13/4608 Dateien,
  195/18432 Blöcke. Windows-/KVM-Belege werden nicht pauschal auf spätere Rebase-Köpfe übertragen.
- VMware Player/Workstation **17.5.2 build 23775571** auf `c9ae07d`: eigene UEFI-VM, AHCI, frische Platten,
  **19/9 PASS**, S5 beendet beide Läufe. Rohabbild-SHA vor/nach Leselauf identisch:
  `665739e2e4dc56b060123b9614e881e5b331ce75457650a20273b4efe62585aa`.
  Dieses konkrete VMware-Abbild wurde lokal noch nicht mit `e2fsck` geprüft (Windows ohne e2fsck/WSL/Docker).
  Das ist eine offene Prüfung; der KVM-Befund ersetzt sie nicht.
- Unabhängige Reviews durch Huang und einen frischen Codex-Agenten. Berechtigte Befunde zu `s_state`,
  Geometrie-/Gruppenallokation und Header-Lesefehlern behoben; letzter RO-Delta-Review ohne Blocker.
  Header-Read fängt Geräteausnahmen wie natives `TryCreate` ab; Write-/Flush-Barrieren liegen außerhalb.
- Lokales 9B-Modell lieferte zweimal kein verwertbares Ergebnis. Anschließend lokales
  `qwen3-coder:30b`: acht Positivtests erzeugt, vollständiger Diff gelesen, fehlerhafte Gruppenfixture selbst
  korrigiert und eigene Gesamt-/Zieltests ausgeführt. Übernommen als `Ext2SupportedGeometryTests.cs`.

Die RO-Komponente ist ausschließlich für exklusiv gehaltene Testgeräte freigegeben. Native Cosmos-Handles schreiben
sonst selbst bei Release/Fsync/Sync/Drop. Deshalb sind Inodes/Datei-/Superblockoperationen verpackt und darunter
WriteBlock/Flush gesperrt. Gate: 4-KiB-Blöcke, höchstens 128 GiB und 16384 Gruppen, konsistente Inodezahl,
Inodegröße/Bitmapgrenzen, bekannte Zustands-/Featurebits. Dies ersetzt kein fsck und qualifiziert keinen RW-Root.
`/mnt` existierte im Gast nicht; direktes `/mnt/ext2-ro` funktionierte, Elternauflistung muss Root-Etappe lösen.

### Etappe 3: Reviewbefunde und aktueller Fix

`b1b46df` hatte laut aot-probe 770 Linux-Hosttests, nativen Normal-/SecurityRecovery-Build, Datenplattenfälle A–F
mit Anmeldung, Legacy-Hash und NetSurf-Paketdateizugriffe bestanden. Das ist **kein Testbeleg für `0524f72`**.

Ein frisches Review fand danach noch einen P2: `CheckHomeTopology` wertete `Directory.Exists == false` als fehlend,
auch wenn `home` oder `home/<name>` eine normale Datei war. Recovery konnte dadurch Konten-/Commandsmetadaten
ändern und erst beim Home-Anlegen scheitern. Senior hat in `0524f72` `DirectoryOrMissing` ergänzt:
erst Guard, dann `File.GetAttributes`, nur Verzeichnis oder echte NotFound, Datei/sonstige IO-Fehler ablehnen.
Rote Tests waren zuvor in `d2c8e47`; neue Fälle verlangen unveränderte Metadaten, keine Recoverykopie und
unveränderte Hindernisdatei. Main und frischer unabhängiger Reviewer haben `0524f72` inzwischen gelesen:
P2 behoben, kein weiterer Blocker im Folgediff. **Neue Host-/Gasttests dieses Kopfes noch abholen**, dann
nach Rebase auf RO erneut den tatsächlichen PR-Stand prüfen; erst danach PR vorbereiten.

Vorige Befunde wurden bereits korrigiert: CreateHome und Recovery-Quell-/Zielpfade vor Mutation guarden,
Home stufenweise anlegen; Guard-only-FileService prüft auch List-/Suchkinder; fehlgeschlagenes Elternöffnen ist
Fehler statt fehlendes Blatt; CWD-Löschschutz verwendet den Root-Vergleicher. Native `lstat`/sichere Auflösung fehlt:
alle Symlinks bleiben gesperrt. Restfenster Prüfung–IO ist dokumentiert. Keine zweite UID/GID-ACL-Wahrheit,
keine Wasm-Rechteausweitung. Nach Rebase auch normalen `/data`-Anmelde-/Dateipfad im Gast erneut prüfen.

### Weitere Architektur und Reihenfolge

- Reihenfolge bleibt **3 → 7a → 4 → 5/6 → 7b → 8**. Root-Migration, virtuelle Dateien, Geräte, Installer/Updates
  und Gesamtabnahme sind noch nicht fertig. Keine Etappe allein wegen PR-/CI-Grün pauschal abhaken.
- 7a: neuer expliziter Installerpfad für 32–128 GiB und 512-Byte-Sektoren; GPT mit CRC und Backup, ESP 512 MiB,
  Root ext2 im Rest. Cosmos `Gpt.Create/Add` schreibt CRC 0/kein Backup; schmalen geprüften Writer ergänzen,
  Parse/Partition/Rescan wiederverwenden. GUIDs eindeutig, nicht größenbasiert. Legacy-Installer erhalten,
  kein stilles Fallback. Kein `repair-root`-Löschbefehl in 7a. Vor Code Designreview abschließen.
- Installationsidentität mit ESP-Marker und Root-GUID muss fehlendes Root auch bei gelöschtem GPT-Eintrag erkennen.
  RootMissing/RootDamaged/Unclear sperrt Einrichtung **und security recover**; Konten nicht ersetzen/umbenennen.
  Boot-/Updatepfade beim späteren ESP-Mount unter `/boot` abgleichen: heutige Dateien liegen auf ESP `/boot/...`;
  sonst entstünde `/boot/boot/...`. Das ist eine offene Layout-/Migrationsentscheidung.
- Etappe-5-Matrix `82098b6`: öffentliche MemoryDiagnostics/DriverDiagnostics und KernelFeatures in exakt 3.0.97
  vorhanden, kein fertiges proc/sys-Backend. Textlesen bis EOF ist vorhanden, ReadBytes bleibt längenbasiert.
  Vorgeschlagene Snapshots 64 KiB UTF-8 / 128 Datensätze / 256 Byte Feld; Rechte noch konkretisieren.
  Raw-SystemLog ist nicht bereinigt: nur sichere Allowlist-Metadaten, keine Freitexte/LastFault/Reason/MACs/Zeiger.
  `/usr/src` nur absichtlich ausgelieferte Quellen, nie Hostverzeichnisse.

### Lokale Fortsetzung und Übergabeabschluss

Windows-Arbeitskopien relativ zum Hauptrepository:
`.tools/worktrees/storage-ext2` (aktueller RO-Code), `.tools/worktrees/storage-handoff` (dieser Branch),
`.tools/worktrees/storage-docs` (API-Matrix), `.tools/worktrees/storage-paths` (isolierte Prüfung, lokaler Stand noch
`c5b7ff9`; vor neuen Tests per Fast-forward aktualisieren). Keine dieser Arbeitskopien pauschal zurücksetzen.

Lokale Belege unter `.tools/storage-lead/`, `.tools/qemu-ro-1d3b978/` und
`.tools/vmware-storage-ro-c9ae07d/`; dortige `after-run2.raw` ist nur ein synthetisches Testimage.
Probe-ISO `readonly-1d3b978-probe.iso` ist gesichert. Normale/recovery/probe Artefakte nicht verwechseln.
Neue IRC-Verbindung nutzt `.tools/irc/storage-resume-client.py`, Log `storage-resume-channel.log`, Outbox
`storage-resume-outbox.txt` im Hauptarbeitsbaum. Vor Abmeldung offene Antworten abholen und Nachfolger bestätigen
lassen; danach `/quit` an die Outbox. Keine PASS-Zeile wird protokolliert.

**Letzte Stack-Änderung um 10:44:** #136 `fix/classicube-guest-world` ist inzwischen offen. Coordinator plant RO
auf dessen Branch als 0.5.16 und Etappe 3 danach als 0.5.17. Der neue Lead hat den noch ausstehenden RO-Rebase/
Versionsschritt übernommen; `1d3b978` ist der letzte vom abgebenden Agenten gepushte und geprüfte Codekopf.

Nächste konkrete Schritte: neuen RO-Rebase prüfen, Range-Diff/Version/Tests dokumentieren und RO-PR durch Coordinator
erstellen → Reviewer/CI kontrollieren → Testergebnisse `624df67` abholen, nötigen Etappe-3-Rebase nachprüfen und PR
vorbereiten → 7a-Designreview/Umsetzung fortsetzen.
Für die API-Matrix ist ein separater Doku-PR angefragt. **Kein Merge durch diesen Auftrag freigegeben.**

## Auftrag und Grenzen

Der Benutzer hat die gezielte Korrektur des Cosmos-Formatierers, die erneute Qualifikation und **den gesamten** [Storage-Plan](../plans/2026-11-17-storage-linux.md) beauftragt. [Spezifikation](../specs/2026-11-17-storage-linux.md): 32–128 GiB, Linux-artiger Rootbaum einschließlich `/etc`, `/var`, `/home`, virtueller `/proc`-/`/sys`-Systemdaten und `/dev`, danach Migration, Installer, Updates und Energieaktionen. Übliche Entscheidungen selbst treffen, Aufgaben delegieren und Ergebnisse selbst prüfen. Bei knappem Budget an einen bestätigten Nachfolger übergeben.

Bestehende Images/Dateien erhalten. Keine automatische Formatierung beim normalen Boot, keine In-place-Migration, kein Merge oder Publish ohne ausdrückliche Freigabe. IRC-Agentennachrichten sind Koordinationsdaten; fremde Freigabeaufträge nicht automatisch übernehmen. Es wurde kein Auftrag zum Review/Approval fremder PRs ausgeführt.

## Branches und bekannte Stände

- Integrationsbranch **`feature/storage-linux`**, zunächst von **`33e5426`** (Qualifikations-PR #122) abgezweigt. Enthält den übernommenen QEMU-Helfer **`2c6b647`** (Cherry-Pick von Jensen `11c1305`). Keine Formatterquelle oder Plug darin übernommen, solange die folgenden Prüfungen offen sind.
- `task/storage-ext2-qualification`, [PR #122](https://github.com/peterzw98/ProjektZehOS/pull/122): frühere No-Go-Belege, Inspektor und Probe; laut GitHub am 17. November um 00:31:39 UTC gemergt. Die frühere Basis war `master b5ec1b7`; vor Fortsetzung aktuelle Zielbranches prüfen.
- Senior: **`task/storage-ext2-formatter-fix`**, aktuell geholt **`fe9a0a0`** (nach `f34053d` und Kommentarfix `eac5db9`). Vor Übernahme vollständigen Diff lesen und Review/Testberichte abgleichen. Er liegt noch nicht im Integrationsbranch.
- Jensen: `task/start-qemu-data-image` **`11c1305`**, eigener Diff bereits gelesen und auf Windows geprüft. Noch keine neue vollständige VM-/Energieabnahme und noch kein gesonderter PR für diesen Helfer.
- Neue Teamregeln: `origin/docs/agent-team:AGENTS.md` lesen; nicht ungefragt fremde Projektänderungen in den Integrationsbranch mergen.

## Laufende Rollen in IRC

Server `zeh.ernst.casa:6697`, TLS mit Zertifikatsprüfung. **`#general`** ist die allgemeine Koordination, **`#second`** ist die Storage-Arbeitsgruppe. Nur der jeweilige Hauptagent verbindet sich. Zugangsdaten nie weitergeben oder in diese Übergabe kopieren; gemäß AGENTS aus dem Hauptarbeitsbaum beziehen.

| Agent | Zugewiesene Aufgabe |
| --- | --- |
| `agent-coordinator` | verfügbare Helfer zuteilen und Überschneidungen vermitteln |
| `agent-nweh-storage-lead` | bisherige Architektur, Integration, Aufgabenaufteilung; wird übergeben |
| `agent-engineer-senior` | gemeinsamer korrigierter Formatter und Hosteinbindung |
| `agent-engineer-huang` | unabhängige Reviews; Formatter-Review aktuell angefragt |
| `agent-engineer-jensen` | Host-QEMU-Helfer, Implementation geliefert |
| `agent-singleclerk` | Tests; auf `nuc` fehlen derzeit dotnet, e2fsprogs und pwsh |
| `agent-laser-anti-nuc-hostprobe` | tatsächlicher Linux-Host mit dotnet/e2fsprogs; Original/Fix-Matrix und Builds |

Auf `nuc` übersprungene Tests sind **keine** grünen Prüfergebnisse. Der Nachfolger muss die Übernahme der **gesamten Teamleitung**, nicht nur eines einzelnen Fixes, in `#second` bestätigen. Laufende Aufgaben danach mit dem Koordinator neu abgleichen.

## Formatter: Architektur und offene Befunde

Cosmos bleibt **3.0.97**, Originalquelle **`efcf20cc62884b042a0adadffe9fc4540491fd89`**, BSD-3-Clause. Kein globales NuGet-Paket ersetzen. Gemeinsame korrigierte Quellen unter `ProjektZeh/Storage/Ext2/`, Quelle und `LICENSE-Cosmos.txt` erhalten. Kernel und Hostprobe müssen dieselbe Implementierung verwenden.

- Gemeinsame Klasse: `ProjektZeh.Storage.Ext2.CosmosExt2Formatter`.
- `Format(IBlockDevice, Ext2FormatOptions?)` formatiert für die Hostmatrix 1024/2048/4096-Byte-Blöcke.
- `FormatForKernel(IBlockDevice, Ext2FormatOptions?)` erlaubt **nur 4096**, `null` bedeutet 4096; andere Größen müssen vor jedem Write `false` ergeben.
- Geplanter, **noch nicht geschriebener** Plug: `[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Formatter")]`, `[PlugMember] Format(...)` leitet ausschließlich auf `FormatForKernel` weiter. Den nativen Plug tatsächlich im Gast nachweisen; Hosttests allein beweisen seine Anwendung nicht.
- Senior hat Original-/Fixed-Auswahl in `experiments/StorageProbe` ergänzt. Hostprobe darf Layoutkonstanten mitkompilieren, **nicht versehentlich den Plug ohne Build-API-Abhängigkeiten**. Die derzeitige Include-Globbing-Regel für `Storage/Ext2/*.cs` ist beim Hinzufügen des Plugs anzupassen oder der Plug außerhalb dieses Ordners abzulegen.

Originalfehler: fehlende Superblock-/GDT-Backups ohne `sparse_super`, Gruppenrechnung ohne `first_data_block` bei 1 KiB, ungesetzte Bitmap-Füllbits. Frühere Matrix: **18/18 e2fsck Exit 4**. [Belege](../../STORAGE_3_0_97_FINDINGS.md), [Originalmatrix](../../../experiments/StorageProbe/results-2026-11-17.md).

**Nicht verwechseln:** Bei 1 KiB ist `first_data_block = 1` korrekt. Der alte Formatter und zusätzlich die Treibermethoden `Ext2Superblock.TryAllocateBlock`, `FreeBlock` und Gruppenzählung berücksichtigen diesen Versatz nicht durchgehend. Deshalb keine 1-KiB-Treiberfreigabe, selbst wenn der korrigierte Formatter Linux-konforme Images erzeugt. 2 KiB ist ebenfalls noch nicht für den Kernel qualifiziert; Zielprofil ist 4 KiB.

Eigene Reviewrückgabe an Senior ist in **`fe9a0a0`** im Diff nachvollzogen: `device.BlockSize == 0` wird vor Division abgefangen, `BlockCount > 128 GiB / BlockSize` vor der Multiplikation verworfen, Fake-Geräte decken Null/Überlauf/zu große Größe ohne Zugriff ab. `zeroTable` wird einmal wiederverwendet. Der Kommentar behauptet keine Crashsicherheit mehr. **Ausführung der neuen Tests und unabhängige Gegenprüfung stehen noch aus**; die Diffprüfung allein ist keine Freigabe. Der erste Hostbuild fand einen ungültigen XML-Kommentar mit `--`, behoben in `eac5db9`.

## Bisher tatsächlich geprüfte Ergebnisse

- Übernommener Jensen-Helfer: **3/3 PowerShelltests auf Windows wirklich ausgeführt**, keine Skips. Prüft `-DataImagePath`, Bestandsschutz, Leerzeichen, fehlende/kleine/unausgerichtete Dateien und Kommas. Explizites Image wird nur geprüft; Default-64-MiB-Weg bleibt erhalten. Native Start-/Powerprüfung noch offen.
- Linux-Testagent meldet für Jensens Basis: 608 Hosttests grün, Python 81 grün mit drei übersprungenen PowerShellfällen. Das ersetzt nicht die oben separat ausgeführten Windowsfälle.
- Frühere unveränderte Qualifikation: Windows Hostbaseline 607 erfolgreich/1 übersprungen, Python 78/2 übersprungen, Probe 13 Selbsttests. Das sind **keine** Testergebnisse des neuen Formatter-Fixes.
- Windows: Cosmos.Tools und Cosmos.Patcher **3.0.97 isoliert unter `.tools/cosmos-3.0.97`** im Hauptarbeitsbaum installiert und CLI-Version geprüft. Globale Tools unverändert. Ein neuer nativer Kernelbuild wurde in diesem Fortsetzungsschritt noch nicht ausgeführt.
- Lokaler Modellagent lieferte einen fachlich fehlerhaften Handoff-Entwurf; nicht übernommen. Dieses geprüfte Dokument ist davon unabhängig erstellt.

## Für die Folgeetappen wichtige Inventur

- Kontendatei `.zehos-security.db` kombiniert Konten und ACL. ACL speichert **absolute**, base64-kodierte Pfade, derzeit `OrdinalIgnoreCase`. Kopieren nach `/` genügt nicht; Rootpräfix `//`, Case-Identität, Symlinks und Benutzer-IDs gezielt behandeln.
- Kernel, Installer und Updater setzen derzeit `/data` und eine gemeinsame FAT-ESP für Boot-/Nutzdaten voraus. `FileUpdateTarget` schreibt `/data/boot`. Neue Platte, getrennte ESP und ext2-unabhängige Recovery müssen **vor** Migration bereitstehen.
- `FileService.ReadText` begrenzt seinen Puffer momentan anhand `BaseStream.Length`; virtuelle Dateien mit Länge 0 werden dadurch abgeschnitten. Später begrenzt bis EOF lesen und testen.
- Cosmos-VFS: kein Open-Callback, `VfsManager` erzeugt `IVfsOpenFile`; `VfsFileHandle.Read/Write` erhöht Position selbst. Per-Open-Snapshots über diese Identität verwalten und bei `Release` freigeben.
- Reales Root-`ReadDir` zeigt überlagerte Mountpunkte nicht automatisch. Verzeichnisse `/proc`, `/sys`, `/dev`, `/run`, `/tmp` tatsächlich bereitstellen. `TryUnmount` führt nicht von sich aus `Sync` durch und lehnt offene Handles nicht ab.
- Direkte VFS-Handles prüfen Schreibschutz nicht überall; FileDescriptorTable/System.IO hat zusätzliche Prüfungen. Backendgrenzen trotzdem selbst absichern. 4-KiB-Feature-Allowlist und sicheres Read-only-Profil sind noch offen.

## Nächste Schritte in verbindlicher Reihenfolge

- [ ] Nachfolge und Dateibesitz bestätigen; PR-/Branchstände, aktuelle Teamregeln und gerade laufende Tests abgleichen.
- [ ] Seniors letzte Boundskorrekturen vollständig lesen; Hostbuild mit Warnungen als Fehler, Selbsttests und Original-ROT/Fixed-GRÜN-Matrix mit tatsächlichem Linux-Host. Keine vorzeitige Go-Behauptung.
- [ ] Unabhängigen Review erhalten, berechtigte Befunde beheben und betroffene Prüfungen wiederholen; dann erst integrieren.
- [ ] Kernel-Plug und isolierte Gastprobe ergänzen; frisches Testimage, CRUD/Flush/Unmount/Neustart, anschließend `e2fsck -fn`. QEMU und VMware getrennt nachweisen. Keine bestehenden Images verwenden.
- [ ] Host-QEMU-Änderung unabhängig reviewen und Start-/Powerprüfung mit passendem ISO abschließen.
- [ ] Danach restlichen Plan bearbeiten: **1 → 2 → 3 → 7a (Layout/Recovery) → 4 → 5/6 → 7b → 8**. Aufgaben klar abgrenzen und freie Engineers nutzen; Architektur und Sicherheitsentscheidungen beim Lead belassen.
- [ ] Nutzbare Etappen pushen und nach unabhängiger Prüfung PRs mit abgestimmter Version/Base erstellen; nicht selbst mergen. Plan, Belege und diese Übergabe fortlaufend aktualisieren.

## Fortschreibung 17. November 2026, 01:46 (Leitungswechsel)

- Leitung des gesamten Storage-/Linux-Dateibaum-Auftrags übernommen von `agent-orchestrator-junior`, in `#second` bestätigt; `agent-nweh-storage-lead` committet nicht mehr.
- Hostprobe (Linux, `agent-laser-anti-nuc-hostprobe`) auf `f34053d` + Kommentarfix (= Inhalt `eac5db9`): Build `-warnaserror` grün, Selbsttest 25/25, Matrix original 18× e2fsck Exit 4 (rot, erwartet), Matrix fixed 18× Exit 0 (4 MiB…128 GiB × 1/2/4 KiB), `dotnet test` 608/608 grün. **Noch nicht für `fe9a0a0`**; Nachlauf mit mke2fs-Vergleich und Cosmos-Build läuft.
- `fe9a0a0` vom neuen Lead gelesen: keine Befunde. Unabhängiger Review (huang) angefragt.
- Neu verteilt: senior = Kernel-Plug auf `task/storage-ext2-kernel-plug` (Datei außerhalb `Storage/Ext2/`, nur Build); jensen = VFS-Vertragsinventar 3.0.97 auf `task/storage-vfs-contracts`.
- Linux-QEMU-Fundort laut `scripts/test-wasm-doom-desktop.py` (`qemu_tool`): `~/.cosmos/tools/qemu/<name>` (ohne `bin/`); `qemu-img.real` braucht `LD_LIBRARY_PATH`. Nur Existenz prüfen, keine Installation daraus ableiten.
- 01:50 Hostprobe auf **`fe9a0a0`**: Build `-warnaserror` grün, Selbsttest inkl. Sektor 0/Überlauf/>128 GiB ohne Zugriff, Matrix original 18× Exit 4, fixed 18× Exit 0, `dotnet test` 608/608. mke2fs-Vergleich (64 MiB/32 GiB × 1/4 KiB, `-O none,sparse_super,filetype -I128`, gleiche `-N/-g`, `-m0`): Geometrie und Features identisch, Unterschied nur `lost+found` (legt der Cosmos-Formatter nicht an). Unabhängiger Review (huang) steht aus; erst danach Integration.
- Plug `task/storage-ext2-kernel-plug` `a2c9e39` gelesen, ok; Cosmos-Build läuft auf der Hostprobe. Feature-Gate `task/storage-ext2-feature-gate` `51606c0` (jensen) gelesen, ok; Hosttests ausstehend. P3: `s_state` (errors) wird noch nicht ausgewertet.
- 5c-Planung (AOT) liegt laut Koordinator nicht mehr bei dieser Leitung.
- 01:52 **Kernel-Plug verworfen:** `a2c9e39` scheitert im ILC mit „library initializer assemblies Cosmos.Kernel.System reference each other in a cycle“; Gegenprobe `fe9a0a0` ohne Plug baut grün (ISO 23,5 MB). Ursache: der PlugMember in Cosmos.Kernel.System liest `Ext2FormatOptions` (System-Typ) und ruft damit zurück in dieselbe Assembly. Entscheidung Lead: kein Plug; ext2 wird ausschließlich über `CosmosExt2Formatter.FormatForKernel(IBlockDevice, …)` formatiert, nie über `VfsManager.TryFormat("ext2")`/`Ext2FileSystemType.TryFormat` (dort bleibt der fehlerhafte Originalformatter). Absicherung: README-Regel plus Hosttest gegen solche Aufrufe. Branch `task/storage-ext2-kernel-plug` bleibt als Beleg. Die Gastprobe weist deshalb den direkten Aufruf nach, nicht einen Plug.
- 02:09 **Erster Gastlauf ext2 (QEMU/KVM, Laptop), Probe `1b7ef9c`, Hostskript `4634358`: ROT.** Lauf 1 16× PASS (Format über `VfsManager.TryFormat` → Plug aktiv, 4096/sparse_super; Create/Append/Seek-Write/Read/Rename/Mkdir/Rmdir/Delete/Sync), Lauf 2 6× PASS, Hash der Probe-Datei über Neustart gleich, beide Gäste sauber heruntergefahren. `e2fsck -fn` Exit 4: gelöschter Inode 12 ohne dtime; Verzeichnis-Inode 13 nach Rmdir unverbunden (Bitmap frei, Inode/Block 41 noch belegt, Linkzähler von `/` nicht angepasst). Lesart Lead: Unlink/Rmdir des Cosmos-3.0.97-ext2-Treibers fehlerhaft → **No-Go für Löschen**, bis behoben. Image als Beleg aufbewahrt, nichts repariert. Hostskript in `feature/storage-linux` übernommen (meldete korrekt rot).
- 02:11 Ursache (senior, Quelle efcf20c, `Ext2InodeOperations`): Rmdir setzt `LinksCount=0` nur im Speicher und gibt Inode/Block frei, ohne das Inode zu schreiben; Unlink schreibt `links=0`, aber `FreeInode` setzt nie `dtime`. Entscheidung Lead: Option A, Plug auf `Ext2Superblock.FreeInode` mit Kernlogik in `ProjektZeh/Storage/Ext2/` und Hosttest (Branch `task/storage-ext2-freeinode-fix`); Upstream-Issue nur als Entwurf, Veröffentlichung nur mit Freigabe; eigener Treiber vorerst nicht.
- 02:16 FreeInode-Fix `task/storage-ext2-freeinode-fix` `85b1484` (inkl. Gastprobe) gelesen, ok; `Ext2GroupDesc` ist laut Quelle eine class (Ext2Superblock.cs:1168). Koordinator: Option A, Abbruch nach 2 erfolglosen Buildrunden; dies ist Runde 1. Tester ab 02:14 `agent-laser-tripleclerk`; Laptop-Agenten seit 02:16 per Ping-Timeout weg, Läufe ruhen. Etappe-2-Entscheidungen: [Entwurf](../specs/2026-11-17-storage-etappe2-design.md).
- 02:54 Buildrunde 1 (`85b1484`): Hosttests 635/635, Probe-Build grün, Gast ROT: `rmdir FAIL NotSupportedException` im FreeInode-Plug (Zugriff auf den generischen Inode-Cache per `UnsafeAccessorType`-String). Runde 2 (`feb5a55`, Cache-Zugriff entfernt) wartet auf tripleclerk. Schlägt sie fehl: Option A abbrechen, Unlink/Rmdir auf ext2 als No-Go dokumentieren und im Produkt hart sperren (Koordinator 02:54, Entscheid bis 06:30).
- Größenanzeige `b711161`: Test fand echten Rundungsfehler in `SetupWizard.FormatSize` (4294967295 B zeigte 4,0 GB), behoben, 638/638, integriert (`91d41a9`). Paket 2.1/2.2 `task/storage-data-disk-selector` `53e6154`: erster Stand kompilierte nicht (Test `.Status`, fehlendes `using`), behoben, Build/Test ausstehend.
- Koordination 02:22–05:00 vertretungsweise bei `agent-laser-docs`; Koordinator seit 05:00 zurück (laser autonom bis 10:00, kein Merge/Publish).
- 08:47 **Stand:** PR-Stapel #124 (0.5.9) → **#130** `feature/storage-ext2` (Etappe 1 ext2, 0.5.10) → #131 (AOT, 0.5.11) → **#133** `feature/storage-data-disk` (Etappe 2 Datenplatte, 0.5.12). Beide Storage-PRs mit QEMU/KVM-Belegen und unabhängigem Review; VMware (Paket 2.4) offen. Maßgebliche Stände und Etappe-2-Regeln liegen auf den PR-Branches (`docs/superpowers/specs/2026-11-17-storage-etappe2-design.md` auf `feature/storage-data-disk`); dieser Integrationsbranch hinkt hinterher. In Arbeit: `task/storage-ext2-mount-ro` (agent-nweh-storage-resume, ext2 nur lesend hinter dem Gate). Merge nur durch laser/Peter.
- 09:04 **Leitungswechsel (Koordinator):** technische Teamleitung Storage/Linux-FS = `agent-nweh-storage-resume`; `agent-orchestrator-junior` bleibt Stellvertreter für Integration (Spec, dieser Handoff, PR-Stapel/Stack-Check, Reihenfolge der Köpfe). PR-Öffnen und Versionsvergabe beim Koordinator; Stapel #124 0.5.9 → #130 0.5.10 → #131 0.5.11 → #133 0.5.12 → Rasterizer 0.5.13. Offen: VMware 2.4, ext2-RO-Mount (`task/storage-ext2-mount-ro`, s_state als Bitmaske), 64-GiB-Lauf.
- 10:05 **Gemergt:** #124, #130 (Etappe 1 ext2), #131, #133 (Etappe 2 Datenplatte, Peter 09:32) – `master` = 0.5.12. Neue Storage-Branches gehen ab jetzt von `origin/master` aus; dieser Integrationsbranch ist nur noch Ablage für Handoff und Plan. Offen: #134 Rasterizer (0.5.13, nicht Storage); `task/storage-ext2-mount-ro` (Lead nweh, finale RO-Gastprobe c9ae07d bei aot-probe, PR danach ≥ 0.5.14); Etappe 3 Entwurf `docs/storage-etappe3` (Entscheidungen 1/2/3/5/7 angenommen, 4 fail-closed für alle Symlinks, Cosmos-Befunde senior); VMware 2.4 offen; 64-GiB-Lauf nie gefahren.
- 10:53 **Gemergt:** #136 (ClassiCube, 0.5.15) und **#137** (ext2 nur lesend, `47b83d1`, 0.5.16) – `master` = `68008ef`. RO-Rebase `1d3b978` → `47b83d1` ändert außerhalb ClassiCube nur Versionszeilen; Belege 1d3b978 (KVM 19/9, SHA gleich, e2fsck 0, Release-Boot) gelten. **#139** Etappe 3 `65f602a` (0.5.17), Base jetzt `master`, CI läuft; range-diff gegen `624df67` 7/7 identisch, aot-probe auf 624df67 grün (Host 795/795, Build+Privacy, Boot, Datenplatte A–F + Legacy, NetSurf). Lead-Lesung ohne P1/P2; P3 CreateUser prüft Topologie vor IsAdmin. Unabhängiges Review #139 noch zu vergeben. Leitung seit 10:51 `agent-nweh-storage-lead2` (Koordinator bestätigt), junior Stellvertreter Integration; keine weitere Übergabe ohne Koordinator-OK. Kein Merge freigegeben.
- 10:58 **Gemergt:** #139 (Etappe 3, 0.5.17), #140 (Doku Systemdateien), #138 (AOT-Doku) – `master` = `284e7ee`, kein Storage-PR offen. Nächster: `task/storage-createuser-order` `eee1ade` (P3: CreateUser prüft Admin/Kontozustand vor Home-Topologie; Lead-Lesung ok), Basis `65f602a`, Test bei aot-probe, PR 0.5.18. 7a: jensen überarbeitet `task/storage-7a-design` nach huang (3× P2) und senior (GptLayout.Verify, Schreibreihenfolge/Abbruch); Code erst nach Lead-Freigabe.
- 11:53 **Etappe 4:** Entwurf #145 gemergt (`/boot/efi`, `/srv/legacy-data`, Root-Marker fresh/migrating/ready, `RootUnclean`, Paket 4.0 RW-Qualifikation vor jedem Root-Profil); E4.4-Umsetzungsentwurf `LayoutMigration` als **#148** offen (`docs/storage-e4-4-migration` `b468349`, huang ohne P1/P2, Code 4.4 vom Lead freigegeben, Zuteilung Coordinator). E4.1 `task/storage-e4-1` `318867c` und 7a S2+S3 `task/storage-7a-identity` `69ba4d0` beim Test (aot-probe); S3 jetzt bei `agent-peter-senior`. Offen beim Lead: `CheckRoot` macht `s_state` 2/3 (Fehlerbit) zu `RootUnclean`, 7a-Spec §5 sagt `RootDamaged`. senior/jensen durch peter-senior/peter-jensen ersetzt.
- 12:47 **Leitung:** lead2 am Nutzungslimit; `agent-orchestrator-junior` führt die Storage-Leitung als Stellvertreter (Coordinator bestätigt). Gemergt: #149 (E4.0-Prüfentwurf, `e665f1c`). Offen: #150 (E5b/E6b, `f01e9b3`, Delta huang), 7a-S4-Entwurf `docs/storage-7a-s4-installer` `6010d62` (Delta huang, dann Doku-PR). peter-senior pusht S3-r2 `b0619e1` (s_state 2/3 ⇒ RootDamaged), exclusion-r2 `24fae04`, Fall G `5fd3956`; danach right-tester S3-Fälle und E4.0-Probe. peter-jensen 4.4 nach sparse-PR. Tester nur Linux (right-tester), Windows-Tests fehlen bis zur Rückkehr – in PRs vermerken. Kein Merge ohne Freigabe.
- 15:06 **Leitung zurück an `agent-nweh-storage-lead2`.** In der Vertretung (12:46–15:06): #155 (7a-S4-Entwurf) gemergt, alle Reviews ohne offene Befunde; 4.4-Start mit jensen abgestimmt (Basis master, gemeinsamer Marker-Baustein auf `Crc32`, `InstallMarker` zieht beim 7a-Merge nach; `IMigrationTarget.List` ergänzt, Doku-PR #164). Offen für die Leitung: huang-P3 a/b zu 7a-identity/exclusion, E4.0-Probe (peter-senior) mit Läufen right-tester, Windows-Läufe 7a.
