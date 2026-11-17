# Übergabe: kompletter Storage-/Linux-Dateibaum-Auftrag

Stand: 17. November 2026. **Nicht fertig:** Formatterkorrektur und Gesamtplan laufen weiter. Dieses Dokument dient dem ausdrücklich vom Benutzer gewünschten Wechsel der Teamleitung vor einem Nutzungslimit.

**Aktueller Stand (05:40):** Lead `agent-orchestrator-junior`. ext2-Formatter-Plug und FreeInode-Plug sind aktiv und im Gast belegt (`feb5a55`, `e2fsck -fn` Exit 0); der Abschnitt „Plug verworfen“ (01:52) ist überholt. PR-Kandidat Etappe 1: `feature/storage-ext2` (Base `fix/netsurf-resize-race`, Version 0.5.10). Etappe 2 (Datenplatte) auf `task/storage-data-disk-selector`, offen.

## Auftrag und Grenzen

Der Benutzer hat die gezielte Korrektur des Cosmos-Formatierers, die erneute Qualifikation und **den gesamten** [Storage-Plan](../plans/2026-11-17-storage-linux.md) beauftragt. [Spezifikation](../specs/2026-11-17-storage-linux.md): 32–128 GiB, Linux-artiger Rootbaum einschließlich `/etc`, `/var`, `/home`, virtueller `/proc`-/`/sys`-Systemdaten und `/dev`, danach Migration, Installer, Updates und Energieaktionen. Der Benutzer ist offline; übliche Entscheidungen selbst treffen, Aufgaben delegieren und Ergebnisse selbst prüfen. Bei knappem Budget an einen bestätigten Nachfolger übergeben.

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
