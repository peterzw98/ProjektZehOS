# Storage-Eignungsprüfung: Cosmos 3.0.97

Stand: 17. November 2026. Arbeitszweig `task/storage-ext2-qualification`, Ausgangspunkt `d443931` (ZehOS 0.5.6, Cosmos-Pakete 3.0.97).

Die folgenden Abschnitte bis einschließlich „Offene Arbeit und nächster Entscheidungspunkt“ dokumentieren die historische Erstprüfung des unveränderten Formatierers. Sie sind keine aktuelle Gesamtfreigabe. Spätere Treiber- und Speicherbefunde stehen im [E4.0-Nachtrag](#e40-spätere-laufzeitbefunde-17-november-2026).

## Entscheidung

**Keine Freigabe des Cosmos-3.0.97-ext2-Formatierers.** Die von ihm frisch erzeugten Dateisysteme bestehen die externe Konsistenzprüfung nicht. Weder ein erfolgreicher Formatter-Rückgabewert noch ein erfolgreicher Mount würde diese Fehler widerlegen. Bestehender FAT32-Betrieb, Bestandsimages und Kernel-Dateizugriffe wurden nicht verändert.

Die Root-Migration und die Aktivierung von ext2 aus dem [Plan](superpowers/plans/2026-11-17-storage-linux.md) bleiben gesperrt. Ein No-Go des Formatierers belegt nicht, dass jeder Lese-/Schreibzugriff des ext2-Treibers fehlerhaft ist; dieser Treiber ist dadurch aber auch nicht qualifiziert.

## Belege und Verantwortlichkeiten

- Der Hauptagent untersuchte das veröffentlichte NuGet-Paket und prüfte die Projektbasis unter Windows. `Cosmos.Kernel.System` 3.0.97 nennt Repository-Commit `efcf20cc62884b042a0adadffe9fc4540491fd89`, `net10.0`, BSD-3-Clause sowie Core/HAL-Abhängigkeiten 3.0.97. SHA-256 des untersuchten nupkg: `be38bc531185b5d00a91e1457a6c1adf70ba04d3d23756297d3cc1fa1484a70b`.
- Der über IRC hinzugezogene Linux-Helfer führte die Formatter-/`e2fsck`-Matrix aus. Seine Resultate werden als fremd ausgeführte Prüfungen ausgewiesen; sie ersetzen keinen QEMU-/VMware-Gasttest des Hauptagenten.
- Die lokale Gegenprobe ist unten dokumentiert. Der unabhängige Endreview erfolgt vor PR-Erstellung; sein Ergebnis gehört in die PR-Beschreibung.

## Ergebnis der Linux-Matrix

Gemeldeter Lauf: `Ext2Formatter.Format` aus dem unveränderten 3.0.97-Paket, frische rohe Sparse-Partitionsimages, anschließend **vor jeder Dateioperation** `e2fsck 1.47.4 -fn`. Keine automatische Reparatur. Formatiert wurden 4 MiB, 8 MiB, 64 MiB, 32 GiB, 64 GiB und 128 GiB, jeweils mit 1024/2048/4096 Byte großen Blöcken.

Reproducer, vollständige Matrix und Fehlerauszüge: [StorageProbe](../experiments/StorageProbe/README.md), [Linux-Ergebnisse](../experiments/StorageProbe/results-2026-11-17.md). Die Helfercommits wurden nach Diffprüfung übernommen; das anfänglich unsichere Cleanup des Matrixskripts wurde vor Übernahme korrigiert. Jeder Lauf verwendet nun ein eigenes frisches Verzeichnis; ein Formatfehler bricht ab, statt eine vorhandene Datei zu prüfen oder zu löschen.

| Größen | 1024 Byte | 2048 Byte | 4096 Byte |
| --- | --- | --- | --- |
| 4 MiB, 8 MiB, 64 MiB, 32 GiB, 64 GiB, 128 GiB | Exit 4 | Exit 4 | Exit 4 |

Damit sind **18 von 18 frisch formatierten Images inkonsistent** (Exit 4: Fehler bleiben unkorrigiert). Der Formatter selbst meldete Erfolg. Die Übereinstimmung mit den nachfolgenden Codebefunden begründet das No-Go; eine vollständige Reparatur oder ein alternativer Formatter wurde nicht implementiert.

`-f` erzwingt die Prüfung, `-n` öffnet nur lesend und lehnt Reparaturen ab; Exit 4 bedeutet verbleibende Dateisystemfehler. [e2fsck-Handbuch](https://man7.org/linux/man-pages/man8/e2fsck.8.html).

## Codebefunde am tatsächlichen Paket

1. **Superblock-/Gruppendeskriptorkopien:** Bei 4096-Byte-Blöcken verwendet der Formatter nur 2048 Blöcke pro Gruppe. Er schreibt im untersuchten Ablauf nur den primären Superblock und Gruppendeskriptortisch, setzt aber kein `sparse_super`. In Folgegruppen liegen Bitmaps an den Stellen erwarteter Backup-Metadaten. Bei 128 GiB ergeben sich 16384 Gruppen.
2. **1024-Byte-Gruppengeometrie:** Gruppenanfänge werden ohne den bei diesem Profil erforderlichen Versatz `first_data_block = 1` berechnet. Der Linux-Helfer berichtet dadurch Bitmapabweichungen bereits im kleinen Ein-Gruppen-Fall.
3. **Bitmap-Padding:** Nicht verwendbare Bits am Ende der Block-/Inode-Bitmaps bleiben im betrachteten Formatter-Ablauf frei. Dies erklärt Konsistenzmeldungen auch in kleinen Profilen, bei denen das Backup-Problem nicht allein ausschlaggebend ist.
4. **Featureprüfung fehlt:** `Ext2Superblock.TryCreate` liest `FeatureCompat`, `FeatureIncompat` und `FeatureRoCompat`, prüft die eingelesenen Bitmasken im betrachteten Mountpfad aber nicht auf unterstützte Funktionen. Auch ein vom Host korrekt erzeugtes ext2-Familien-Image ist deshalb ohne eigenen Featurevertrag nicht allgemein schreibbar freizugeben.
5. **Dateigrößen:** Der Schreibpfad setzt `SizeHigh`, ergänzt dort aber kein `large_file`-Flag. Über 2 GiB große Dateien bleiben gesondert zu qualifizieren. Das im Plan vorgesehene konservative Limit ist noch keine implementierte Schutzprüfung.
6. **Read-only bei direkten VFS-Handles:** `Ext2FileSystemType.TryMount` reicht `flags` nicht in den Superblock weiter; `VfsFileHandle.Write` und `TrySetAttr` delegieren ohne eigene Mountflag-Prüfung. Auch `Ext2FileOperations.Write` prüft den Mountstatus dort nicht. Dies ist zunächst ein Codebefund zum direkten VFS-Pfad. Eine mögliche zusätzliche Prüfung der `System.IO`-/Syscall-Grenze wurde damit nicht widerlegt. Ein Read-only-Mountflag allein gilt deshalb nicht als Nachweis für ein sicher schreibgeschütztes Testgerät.

Die Binärinspektion ist reproduzierbar mit `ilspycmd -t <vollständiger Typname> <Cosmos.Kernel.System.dll aus 3.0.97>`. Sie ist keine Behauptung von Gasttests oder einer vollständigen Treiberprüfung. [Paket](https://www.nuget.org/packages/Cosmos.Kernel.System/3.0.97), [exakter Cosmos-Quellstand](https://github.com/CosmosOS/Cosmos/tree/efcf20cc62884b042a0adadffe9fc4540491fd89).

## Lokale Prüfungen unter Windows

- SDK `10.0.404`; `cosmos check` findet die vorhandenen Werkzeuge. Die lokale Cosmos-CLI meldet jedoch `3.0.94`; es wurde kein Toolupdate vorgenommen. WSL ist nicht installiert.
- Host-Baseline: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`: 607 erfolgreich, 1 übersprungen, 608 gesamt, 0 Fehler.
- Neuer Nur-Lese-Inspektor: `python scripts/test-storage-filesystems.py <rohes-Partitionsimage>` liest nur 1024 Byte ab Offset 1024 und gibt Blockzahl, Blockgröße und rohe Featuremasken aus. Er bewertet weder Konsistenz noch Mountsicherheit und verarbeitet keine GPT-/MBR-Gesamtimages. Die Signatur gehört zur ext2-Familie und unterscheidet allein nicht ext2, ext3 und ext4.
- Tests des Inspektors: 8 erfolgreich; Größenprofile, abgeschnittene Header, falsche Signatur, unzulässige Blockgröße, Null-Blockzahl, unveränderte Featurebits/Datei und CLI-Ergebnis geprüft. Zuerst fehlgeschlagener Testlauf wegen noch fehlendem Modul, anschließend erfolgreich nach Implementierung.
- Gesamte Python-Suite nach Reviewkorrekturen: 78 Tests, 2 übersprungen, keine Fehler. Darin vier deterministische Matrixrunner-Tests mit gestubbten Werkzeugen: saubere Matrix, verbleibende Konsistenzfehler, Formatfehler mit Bestandsschutz und fehlender/ungültiger Header. Exitcode-/Headerfehler wurden zuerst reproduziert, dann korrigiert. Der lokale Modellagent lieferte einen unbrauchbaren Testentwurf; dieser wurde vollständig verworfen. Die übernommenen Python-Tests wurden vom Hauptagenten geschrieben und ausgeführt.
- Übernommene Hostprobe selbst gebaut: `dotnet build experiments/StorageProbe --configuration Release -warnaserror`, 0 Warnungen/Fehler. `dotnet run --configuration Release --project experiments/StorageProbe -- selftest`: alle 13 Prüfungen erfolgreich.
- Eigene Formatierungsgegenprobe mit einem neuen 64-MiB-/4096-Byte-Partitionsimage: Formatter meldet Erfolg; Inspektor liest `block_count=16384`, `block_size=4096`, `feature_compat=0`, `feature_incompat=2`, `feature_ro_compat=0`. Damit stimmen Header und fehlendes `sparse_super` mit den Linux-Ergebnissen überein. `e2fsck` wurde unter Windows nicht ausgeführt.
- Tatsächliche CLI-Verweigerung zusätzlich geprüft: erneutes Formatieren desselben Zielpfads endet mit Exit 3 und unveränderter SHA-256; eine 32-GiB-Anforderung unter Windows endet vor Dateierzeugung mit Exit 2. Die Windows-Probe legt bewusst höchstens 64 MiB an, solange Sparse-Verhalten dort nicht nachgewiesen ist.

## Offene Arbeit und nächster Entscheidungspunkt

Keine ext2-Gastfreigabe für QEMU oder VMware, kein GPT-Installerwechsel, keine Root-Migration und keine `/proc`-/`/sys`-Implementierung. Diese Schritte werden nicht als erledigt abgehakt. Der Windows-CLI-Versionsunterschied ist für die reine Hostprobe kein Grund, ungefragt Werkzeuge zu ändern.

Empfehlung: die drei Formatterbefunde zunächst gegen den exakten Cosmos-Quellstand als reproduzierbaren Fehlerbericht/Fix vorbereiten und danach die Matrix erneut ausführen. Einen externen Issue oder PR nur nach Auftrag veröffentlichen. Bis zu einer geprüften Lösung FAT32 beibehalten. Eine eigene abweichende Formatterkopie im Kernel oder ein Wechsel zu exFAT/ext4 wäre eine neue Architekturentscheidung; der Formatiererfehler allein rechtfertigt keinen ungeprüften Port.

## E4.0: spätere Laufzeitbefunde (17. November 2026)

Dieser Nachtrag trennt den am versionsgebundenen Cosmos-Code geprüften Fehler von den durch andere Agenten ausgeführten und im Projekt-IRC gemeldeten Host-/Gastläufen. Für diesen Dokumentationsstand wurden die Dateisystem- und Speicherläufe nicht erneut ausgeführt. Die Rohdaten der genannten Läufe liegen bei den ausführenden Agenten; die unten genannten Ablageorte sind Belegpointer, keine eingecheckten Testartefakte. Einzelne PASS-Ergebnisse erteilen keine Gesamtfreigabe für ext2 oder die Root-Migration.

### Entfernen über ext2-Blockgrenzen

**Quellbefund:** `Ext2DirectoryHelper.RemoveEntry` in [Cosmos 3.0.97, Commit efcf20cc](https://github.com/CosmosOS/Cosmos/blob/efcf20cc62884b042a0adadffe9fc4540491fd89/src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Directory.cs) führt `prevPos` und `prevRecLen` über das gesamte Verzeichnis weiter. Beim Entfernen des ersten Records eines Folgeblocks vergrößert es damit den letzten Record des vorherigen Blocks; dessen `rec_len` überschreitet die Blockgrenze. Bei wiederholtem Verschmelzen ist außerdem ein Überlauf des 16-Bit-Längenfelds möglich. Die reine Auflistung durch Cosmos ist kein Konsistenznachweis.

**Gemeldete Belege:**

| Prüfung | Aufbau und Ergebnis | Herkunft |
| --- | --- | --- |
| E4.0 Lauf 10 | QEMU/KVM, 2-GiB-Dateisystem; 600 Dateien, erste 300 gelöscht. Cosmos listet die übrigen 300, aber `e2fsck -fn` endet mit Exit 12 und beschädigtem Verzeichnis (Inode 11, Block 0). | peter-senior, IRC 17.11.2026 20:58–20:59 UTC; `scripts/test-storage-ext2-root.py --size 2 --diagnose-run 10`, Ablage `.tools/e40-span/` auf seinem Rechner |
| Unabhängiger Hostbeweis | Echter unveränderter Cosmos-3.0.97-Treiber, 32-MiB-ext2, 4-KiB-Blöcke, 600 Dateien. `n0253` bei Offset 4072 hat `rec_len=24`; Entfernen von `n0254` am Blockanfang 4096 vergrößert die Länge auf 40: Ende 4112 statt höchstens 4096. `e2fsck -fn`: vorher 0, nachher 12. | codex-upper-test, Branch `task/ext2-boundary-test`, IRC 21:02 UTC; `/tmp/zehos-ext2-boundary-01/{before,after}.img`; Branch zum Dokuabgleich noch nicht auf origin verfügbar |
| Gegenprüfung des Hostbeweises | Eigener Parser, `e2fsck` und `debugfs` auf Kopien bestätigen denselben Grenzübertritt; erneuter Skriptlauf reproduziert ihn. Der Kontrollfall `n0255` innerhalb eines Blocks hält die Recordstruktur korrekt. | RED-ext2-review, IRC 21:14 UTC |

Der Hostlauf meldete zusätzlich fehlendes `dtime` bei freigegebenen Inodes (im Kontrollfall Exit 4); das ist ein separater bekannter Befund des ungeplugten Cosmos-Treibers. ZehOS hat dafür bereits `CosmosExt2FreeInodePlug`/`Ext2InodeRelease`. Die 4096 verwaisten Inodes aus Lauf 9 (`.tools/e40-limit4/`) sind nicht pauschal als Folge des Grenzfehlers belegt.

**Produktfolgen und Stand der Umgehung:** Betroffen sind Löschungen sowie Umbenennungen, damit auch `rm`, `mv`, Austausch per temporärer Datei und Migration. Die Verzeichnisgrenze 8189 verhindert den Fehler nicht; schon ein Verzeichnis mit mehreren Blöcken reicht aus. Der Branch `fix/ext2-remove-entry-plug` (beim Abgleich `be097fe`) ersetzt die Operation durch blocklokales Verschmelzen (`Ext2DirectoryRemoval`). Am Blockanfang bleibt die Recordlänge erhalten, der Eintrag wird freigegeben. Eine Hostgegenprobe der reinen Ersatzlogik (`7407a48`) wurde für Blockanfang, Blockmitte, vollständiges Leeren und verschiedene Löschreihenfolgen mit `e2fsck` Exit 0 gemeldet (RED, IRC 21:23 UTC; freigegebene Inodes wurden dort gesondert per `debugfs` mit `dtime` versehen). Das ist kein Beleg für die native Plug-Bindung oder den Gastablauf.

Der erste Plug-Stand mit `object`-Parametern fand sein statisches Ziel mit internen Cosmos-Typen nicht, obwohl der Build erfolgreich endete. `be097fe` verwendet passende Typnamen; der Bindungsnachweis im nativen Buildlog und die erneuten Gastläufe 9/10 mit sauberer externer Konsistenzprüfung sind getrennt erforderlich. Das [CI-Gate aus PR #219](https://github.com/peterzw98/ProjektZehOS/pull/219) verlangt für jede ZehOS-Plugmethode einen erfolgreichen Bindungsversuch im ausführlichen Buildlog; Fehlversuche gegen andere Assemblies sind dabei zulässig. Es ersetzt keine Gastprüfung. Ein abgeschlossener Gastnachweis dieses Plug-Stands liegt diesem Nachtrag noch nicht vor.

Bereits beschädigte Verzeichnisse repariert die Umgehung nicht. Erkennt die Ersatzlogik ungültige Records, bricht sie ab; sie soll die Beschädigung nicht durch weitere Änderungen verdecken. Vor weiteren Schreibversuchen das Volume offline an einer gesicherten Kopie mit `e2fsck` prüfen und gegebenenfalls reparieren. Ein erfolgreiches `ls` oder ein erfolgreicher Kernelbuild reicht nicht.

### Speicherfehler und RAM-Aussagen

Gemeldet wurde für die E4.0-Reihe 10–12, insbesondere Lauf 12 mit `task/e40-probe-fix` `1a81ecc`: Bei **2 GiB RAM** bestehen `select` bis `d-state`, Phase `e` endet mit `ArgumentNullException` für `destinationArray`; ab Phase `d` erscheint `[GC] Mark stack overflow`. Bei **512 MiB RAM** scheitert der kumulierte Lauf bereits in Phase `d` mit einer Null-Allokation. Quelle: right-tester-Ergebnis, durch den Koordinator am 17.11.2026 20:44 UTC weitergegeben; Ablage `.tools/worktrees/e40-run12-work` beim ausführenden Agenten. Eine verlässlich fangbare `OutOfMemoryException` ist dadurch gerade nicht belegt.

Dem steht der isolierte **Lauf 11** von peter-senior gegenüber (IRC 21:09 UTC): frisches 2-GiB-Dateisystem, je 512 MiB und 2 GiB RAM, ein Verzeichnis bis 8190 Einträge ohne Fehler; 4 × 2048 Einträge mit je 100 Rename und 100 Delete bestanden, `e2fsck` Exit 0. Das Anlegen von 8190 Einträgen dauerte 215 beziehungsweise 152 Sekunden. Diese Einzelprüfung widerlegt eine allein durch die Eintragszahl erklärte Speichergrenze, garantiert aber keine Dauerlast nach vorangegangenen Phasen.

Quellbefunde zu wiederholtem Parsen/Allokieren und zum Inodecache sind Hinweise auf kumulierten Speicherdruck. Ob Lecks, Fragmentierung oder GC-Verhalten den Fehler jeweils verursachen, ist durch diese Läufe nicht abschließend getrennt. Große Produktpuffer sind ebenfalls relevant: `WasmSaveStore` erlaubt 16 MiB pro Datei, `WasmGameApp` und `WasmEngineSelection.ReadAot` Module bis 64 MiB, `WasmRuntime` bis 256 MiB linearen Speicher. Diese Grenzen sind keine Aussage über gleichzeitig verfügbaren oder zusammenhängenden Kernel-Heap. Ein erfolgreicher Hosttest der Fehlerbehandlung belegt das Verhalten des Cosmos-Allokators nicht.

**Kein sicherer Mindest-RAM ist abgeleitet.** 1 GiB ist eine vorgeschlagene vorläufige Testkonfiguration, keine Betriebszusage; selbst 2 GiB verhinderten den kumulierten Fehler nicht. Offen bleiben gezielte Low-Memory-, Fragmentierungs- und Wiederholungsläufe sowie Messungen von Spitzenbelegung und freiem zusammenhängendem Speicher. Die hier berichteten Gastläufe belegen weder VMware noch echte Hardware.

### Produktgrenze 8189

[PR #211](https://github.com/peterzw98/ProjektZehOS/pull/211) setzt `FileService.MaximumExt2DirectoryEntries` auf **8189 direkte Kinder** je beschreibbarem ext2-Verzeichnis. Cosmos `ParseDirectory` betrachtet höchstens 8192 Records einschließlich `.` und `..`; der verbleibende Platz ist nötig, weil Rename zunächst den neuen Namen anlegt. Lauf 9 meldete bei 8190 Kindern eine erfolgreiche Umbenennung, nach der Cosmos in derselben Sitzung weder alten noch neuen Namen sah, obwohl `debugfs` den korrekten Platteneintrag zeigte.

Versteckte und temporäre Einträge zählen mit. Die Migration berücksichtigt auch erzeugte Zielverzeichnisse und Marker-/Journalplatz vor dem Zielschreiben. Direkte interne `System.IO`-Schreibzugriffe sind von der FileService-Prüfung nicht vollständig erfasst. Die Grenze ist weder eine ext2-Formatgrenze noch ein Schutz vor dem `RemoveEntry`-Fehler oder Speichererschöpfung; fremd überfüllte oder beschädigte Verzeichnisse werden nicht repariert. Die Begründung ist im [Entwurf zur Verzeichnisgrenze](superpowers/specs/2026-11-17-ext2-directory-limit.md) beschrieben. Der Hostnachweis an der Produktgrenze und der kleine Gasttest mit Testgrenze 2 ersetzen keinen Gastgrenztest mit 8189/8190 Kindern; ein solcher Nachweis nach #211 liegt diesem Nachtrag noch nicht vor.
