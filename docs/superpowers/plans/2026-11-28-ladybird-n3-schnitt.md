# N3 · Arbeitspakete für den nativen Paketdienst

Entwurf für Peter, Stand 28. November 2026. P1–P4 sind auf `master` vorhanden.
Der N2-Z-Torplan sieht den Schnitt für Index, sysroot und Laufzeitlader nach
N2-Z vor. Bau- und Serverentwürfe können parallel vorbereitet werden. Die
Gastintegration beginnt auf dem an N2-Z abgenommenen Stand. Das jeweilige
Paket erhält einen eigenen Board-Block, Branch und PR nach Freigabe des
Zuschnitts.
[N2-Z-Torplan](2026-11-28-ladybird-n2z-tor.md) ·
[N3-Umriss](2026-11-24-ladybird-n3-paketdienst.md) ·
[P1–P4](2026-11-24-ladybird-p-paketdienst-vorarbeit.md) ·
[Paketentwurf](../specs/2026-11-24-native-pakete-design.md#7-paketdienst-v2)

## Vorhandene Schnittstellen

- `PackageManifest2`, `PackageCatalog2` und `PackageResolver` liefern
  kanonisches Format, Metadaten und Abhängigkeitsreihenfolge. `depends`
  bestimmt die benötigten Paketversionen, `provides=so:<soname>` dient dem
  Konfliktabgleich. N3-A setzt die vorhandene Vertrauensprüfung vor das
  Parsen des bezogenen Katalogs.
- `PackageStore2` bietet `ReadActive`, `Stage`, `Activate` und `Reconcile`.
  `active2` speichert die aktive Menge. `PackageShellCommands` und der
  Startpfad nutzen bisher den v1-Dienst. `package2.py` signiert Manifest und
  Katalog und schreibt die v2-Verzeichnisstruktur.
- `ElfLoader` lädt bereits `PT_INTERP` aus der Prozesswurzel. `ld-musl` lädt
  und reloziert Bibliotheken im Ring 3. Der vorhandene Gastnachweis nutzt
  separat bereitgestellte Dateien. N3 verbindet denselben Ladeweg mit dem
  verifizierten Paketbestand.
  [Format 2](../../PACKAGE_FORMAT.md) · [Linux-ABI](../../LINUX_ABI.md)

## Pakete und Übergaben

| Paket | Schnittstelle und Umfang | Voraussetzung und Abnahme |
| --- | --- | --- |
| N3-A · Katalog und `pkg` | Vertrauensgeprüften `catalog2` über `IPackageSource` und `PackageTrust` an `PackageResolver` anschließen. Für Installation/Upgrade Zielmenge und Platz vorab prüfen, alle Versionen vollständig bereitstellen und einen `Activate`-Wechsel durchführen. Entfernen schützt benötigte Abhängigkeiten, `--unused` räumt automatische Pakete ab. `pkg update` lädt den Katalog. `pkg upgrade` ändert den Bestand. | P1–P4. Hosttests mit lokalem Testrepo für fehlende/zu alte Abhängigkeit, Signaturfehler, Generationsrückschritt, volle Platte und Abbruch. Gastanschluss mit B. V1-Katalog und v1-Bestand bleiben benutzbar. |
| N3-B · Paketindex und Prozesswurzel | Aus der vollständigen Zielmenge einen unveränderlichen Index vorbereiten. Auflösung eines Pfads liefert Paketversion, verifizierten Dateiort und Ausführrecht. Manifest-Links bleiben virtuelle Einträge des Index. Prozessstart bindet einen Indexstand bis zum Ende seiner Nutzer. Beim Upgrade neue Starts nach erfolgreichem `Activate` auf den neuen Stand schalten. `PackageStore2` um Nutzungszählung je Paketversion ergänzen. Laufzeitbereinigung entfernt inaktive Dateien erst nach Freigabe der letzten Nutzung. Das vollständige `Reconcile` beim Einbinden vor Paketstarts aufrufen, sobald keine Paketnutzer mehr leben. | N2-Z und A für Gastintegration. Position von `StorageLock` und Sperre für Start/Aktivierung festlegen. Pfadkonflikte, FAT-Großschreibung, Linkkette/Schleife, Wurzelausbruch und gleichzeitigen Start/Upgrade samt Bereinigung testen. Beim Neustart Index aus gültigem `active2` neu bilden. |
| N3-C · Bauwerkzeug | `pkgs/<id>/package.toml`, `build.sh` und `patches/` als Eingaben. `scripts/build-native-package.py` erhält feste Werkzeuge und Quellen mit Prüfsummen. Nach Quellbezug läuft der Bau ohne Netz. Ausgabe ist ein Dateibaum für `package2.py` plus Lizenz- und Herkunftsnachweise. CI verwendet einen Testschlüssel. | Hostarbeit parallel zu A und Server. Reproduzierbare Fixture bauen und Format durch bestehende Hosttests lesen. Toolchain-Versionen zur Freigabe vorlegen. |
| N3-D · Sysroot | Aus `musl`, `libcxx`, `libzehos`, den jeweiligen `-dev`-Paketen und `toybox` den definierten Sysroot zusammensetzen. Die Build-Schnittstelle dokumentiert Clang/CMake-Ziel `x86_64-linux-musl` und Rust-Ziel `x86_64-unknown-linux-musl`. Compiler-, Bibliotheks- und ABI-Auswahl gemeinsam festhalten. Den `libzehos`-Header früh mit F vereinbaren. | N2-Z und C. Ein unverändertes CMake- und Cargo-Projekt gegen denselben Sysroot bauen. Toolchain, Paketreleases und effektive Linkziele im Protokoll festhalten. Die Geräteabnahme folgt in F. |
| N3-E · Laufzeitlader und S2 | `PT_INTERP` über B aus dem Paketbestand öffnen, `ld-musl` löst die Bibliotheken relativ zur gebundenen Prozesswurzel auf. Bestehende ELF- und S2-Pfade wiederverwenden und fehlende Aufrufe aus dem [Entwurf](../specs/2026-11-24-native-pakete-design.md#6-systemaufrufe) schließen. | N2-Z, B und D. Dynamisches Programm mit gemeinsamer Bibliothek starten, auch ohne das bisherige Gast-Dateibündel. Fehlender Interpreter, fehlende Bibliothek, kaputtes ELF und Upgrade bei laufendem Prozess mit demselben Programm prüfen. Ein Neustart nutzt die neue Paketversion. |
| N3-F · Anzeige und Menü | `/dev/zehos` für kopierende Anzeige und Eingabe, `libzehos` als C-Schnittstelle. Startmenü nutzt `entry` des aktiven, geprüften Pakets und denselben Prozessstart wie E. | B, D und E. Minimale native App über Menü starten, Eingabe, Fenster schließen und sauberes Prozessende prüfen. `epoll` am Gerät ist Teil von N4. |
| N3-G · Server und große Dateien | Bestehenden statischen HTTPS-Server erweitern. `catalog2` und `catalog2.sig` neben v1 ausliefern. Inhaltsdateien unveränderlich unter ID und Version ablegen. Vorschlag für die Umschaltung ist ein vorbereitetes Verzeichnis je Kataloggeneration und ein atomarer Wechsel des Serverpfads für beide Dateien. Feste Inhaltslänge, keine Umleitung, Prüfsumme und Signatur über den vorhandenen Clientpfad. | Lokale Serverprobe parallel zu A/C. Bei Abbruch vor der Umschaltung bleibt das alte Paar sichtbar. Liegen die zwei HTTP-Abrufe auf verschiedenen Seiten der Umschaltung, verwirft der Client das ungültige Paar und kann beide Dateien erneut laden. Ein sichtbarer Stand kann mit neu signierter höherer Generation berichtigt werden. Gasttest mit Inhaltsdatei über 100 MiB und abgebrochenem Transfer. Veröffentlichung erst nach Freigabe. [Serveranforderungen](../../../deploy/package-repo/README.md) |

## Entscheidungen für Peter

1. **Ablage über 100 MiB.** Vorschlag ist derselbe HTTPS-Ursprung mit großen,
   unveränderlichen Objekten außerhalb des Git-Branches, etwa auf einem
   Server-Volume. `packages` behält Katalog und kleinere Dateien. Für eine
   externe Objektablage müsste der Server die Objekte unter denselben Pfaden
   ohne Weiterleitung ausliefern. Peter entscheidet über Betreiber, Quota,
   Sicherung und Aufbewahrung alter Versionen. Die bestehende Paketgrenze
   von 1 GiB gilt bis zu einer eigenen Änderung.
2. **Update während laufender Programme.** Vorgeschlagen ist ein neuer
   Indexstand nur für neue Prozesse. Laufende Programme behalten ihre
   gebundenen Versionen einschließlich Kindprozessen und noch offenen
   Dateibezügen. Falls der erste Schnitt Upgrades während der Nutzung
   verschieben soll, muss Peter diese Abweichung vom Entwurf freigeben.
3. **Toolchain und erste Veröffentlichung.** Peter bestätigt Geräte- und
   Bibliotheksumfang des ersten Sysroots, Toolchain-Versionen und nach der
   Gastabnahme den freigegebenen Paketserver. Stichproben im lokalen Repo
   verwenden ausschließlich Testschlüssel.

## Tor N3-Z

1. Ein lokales signiertes Testrepo liefert drei Pakete in einer Kette.
   Eine Anwendung startet mit der gemeinsamen Bibliothek. Ein Upgrade
   wechselt die gesamte Kette. Entfernen in Abhängigkeitsreihenfolge
   und `--unused` räumen den Bestand wie erwartet auf.
2. Unterbrechung, Schreibfehler und voller Datenträger werden an den
   `Step`-Marken von `Stage`, `Activate` und `Reconcile` injiziert, samt
   Abbruch direkt vor und nach dem Ersetzen von `active2`. Vor dem Commit bleibt der
   alte Bestand startbar. Nach dem Commit startet der vollständige neue
   Bestand, alte laufende Programme können fertiglaufen. Ein Neustart
   rekonstruiert die aktive Menge aus `active2`. V1/Wasm bleibt funktionsfähig.
3. Das unveränderte CMake-Projekt und Cargo-Projekt bauen gegen denselben
   versionierten Sysroot. Start und tatsächlich benötigte Bibliotheken werden
   im Gast protokolliert. Die KVM-/TCG-Matrix und Wiederholungen gelten wie
   im [Gesamtplan](2026-11-24-ladybird-nativ.md#feste-werte).

Nach erfolgreichem N3-Z kann die Migration der ersten Apps in N5 starten.
N6 und SDL brauchen zusätzlich N4-Z. [Abnahmetore](2026-11-24-ladybird-nativ.md#tore)
