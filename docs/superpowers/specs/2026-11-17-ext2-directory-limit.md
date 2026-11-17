# ext2-Verzeichnisgrenze

Auftrag des IRC-Koordinators vom 17. November 2026: Auf ext2 hoechstens 8190 sichtbare Eintraege je Verzeichnis. FileService meldet bei einem weiteren Eintrag `cause='DirectoryEntryLimit'`; eine Migration mit einem groesseren Quellverzeichnis endet vor Zielschreibzugriffen als Konflikt. FAT und Host-Dateisysteme behalten ihr Verhalten.

## Entwurf

- FileService erhaelt wie bei seiner Speicherstatistik einen optionalen plattformneutralen Callback fuer die Grenze des konkreten Elternverzeichnisses. Der Cosmos-Adapter ermittelt den innersten Mount und dessen `VfsStatFs.Type`; nur beschreibbares ext2 bekommt die Grenze. Ohne Callback gilt keine kuenstliche Hostgrenze.
- Pruefung unmittelbar vor neuen Verzeichniseintraegen: Touch, mkdir (auch fehlende Eltern), temporaere Dateien fuer Schreiben/Kopieren und Verschieben in einen anderen Ordner. Vorhandene Touch-/mkdir-Ziele und Umbenennungen im selben Ordner erzeugen dauerhaft keinen zusaetzlichen Eintrag. Bestehende Schreib-, Sicherungs-, Wiederherstellungs- und ACL-Fehlerpfade bleiben erhalten.
- Die Grenze zaehlt alle direkten Kinder, auch versteckte Dateien, und Dateitypen gemeinsam. Temporaere Dateien zaehlen mit. Eine Aktualisierung per temporaerer Datei in einem bereits vollen Ordner kann deshalb fehlschlagen; das Original bleibt erhalten. Es entsteht keine neue Garantie bei konkurrierenden Fremdschreibzugriffen.
- Alle produktiven FileService-Instanzen erhalten den Callback: Shell/Dateimanager, Kontenmetadaten und Updateziel/-konfiguration. Paketinterne direkte System.IO-Aufrufe sind durch diesen Auftrag nicht umgebaut.
- MigrationPlan zaehlt die direkten Kinder der Quelle je Elternpfad und fuegt ab 8191 einen DirectoryEntryLimit-Konflikt hinzu. Nach der Zuordnung zaehlt es auch eindeutige Zielkinder einschliesslich erzeugter Eltern, Installereintraegen, Journal und Marker-Tempdatei. LayoutMigration prueft vorhandene Plankonflikte vor jedem Zielschreiben.
- Gemeinsame Grenze als interne FileService-Konstante, keine neue Konfiguration oder Abhaengigkeit.

## Quellbeleg und Grenzen

Cosmos 3.0.97 stammt laut NuGet aus `efcf20cc62884b042a0adadffe9fc4540491fd89`. [Ext2DirectoryHelper](https://github.com/CosmosOS/Cosmos/blob/efcf20cc62884b042a0adadffe9fc4540491fd89/src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Directory.cs) begrenzt ParseDirectory auf 8192 Datensaetze inklusive Punkt/Punktpunkt. [Rename](https://github.com/CosmosOS/Cosmos/blob/efcf20cc62884b042a0adadffe9fc4540491fd89/src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2InodeOperations.cs) fuegt den neuen Namen ein und entfernt danach den alten; dessen RemoveEntry liest nicht ueber den begrenzten Parser. Deshalb ist eine gleichverzeichnige Umbenennung bei 8190 vorhandenen Kindern weiterhin moeglich.

Das ist eine Produktgrenze fuer den vorhandenen Treiber, keine Aenderung des ext2-Formats oder der Cosmos-Abhaengigkeit. Bereits extern ueberfuellte Verzeichnisse werden hierdurch nicht repariert. Gastnachweise werden getrennt vom Quellbefund dokumentiert.
