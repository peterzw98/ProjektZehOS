# Storage-Eignungsprüfung: Cosmos 3.0.97

Stand: 17. November 2026. Arbeitszweig `task/storage-ext2-qualification`, Ausgangspunkt `d443931` (ZehOS 0.5.6, Cosmos-Pakete 3.0.97).

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
- Gesamte Python-Suite: 74 Tests, 2 übersprungen, keine Fehler. Der lokale Modellagent lieferte einen unbrauchbaren Testentwurf; dieser wurde vollständig verworfen. Die übernommenen Tests wurden vom Hauptagenten geschrieben und ausgeführt.
- Übernommene Hostprobe selbst gebaut: `dotnet build experiments/StorageProbe --configuration Release -warnaserror`, 0 Warnungen/Fehler. `dotnet run --configuration Release --project experiments/StorageProbe -- selftest`: alle 13 Prüfungen erfolgreich.
- Eigene Formatierungsgegenprobe mit einem neuen 64-MiB-/4096-Byte-Partitionsimage: Formatter meldet Erfolg; Inspektor liest `block_count=16384`, `block_size=4096`, `feature_compat=0`, `feature_incompat=2`, `feature_ro_compat=0`. Damit stimmen Header und fehlendes `sparse_super` mit den Linux-Ergebnissen überein. `e2fsck` wurde unter Windows nicht ausgeführt.
- Tatsächliche CLI-Verweigerung zusätzlich geprüft: erneutes Formatieren desselben Zielpfads endet mit Exit 3 und unveränderter SHA-256; eine 32-GiB-Anforderung unter Windows endet vor Dateierzeugung mit Exit 2. Die Windows-Probe legt bewusst höchstens 64 MiB an, solange Sparse-Verhalten dort nicht nachgewiesen ist.

## Offene Arbeit und nächster Entscheidungspunkt

Keine ext2-Gastfreigabe für QEMU oder VMware, kein GPT-Installerwechsel, keine Root-Migration und keine `/proc`-/`/sys`-Implementierung. Diese Schritte werden nicht als erledigt abgehakt. Der Windows-CLI-Versionsunterschied ist für die reine Hostprobe kein Grund, ungefragt Werkzeuge zu ändern.

Empfehlung: die drei Formatterbefunde zunächst gegen den exakten Cosmos-Quellstand als reproduzierbaren Fehlerbericht/Fix vorbereiten und danach die Matrix erneut ausführen. Einen externen Issue oder PR nur nach Auftrag veröffentlichen. Bis zu einer geprüften Lösung FAT32 beibehalten. Eine eigene abweichende Formatterkopie im Kernel oder ein Wechsel zu exFAT/ext4 wäre eine neue Architekturentscheidung; der Formatiererfehler allein rechtfertigt keinen ungeprüften Port.
