using System.IO;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Migration;

/// <summary>
/// Schreibseite der Migration (Spec E4.4 §1), Kernelpfade relativ zur Root-Partition des Ziels. Die Cosmos-Umsetzung folgt
/// mit 4.5; Hosttests verwenden einen Fake, weil ENOSPC, Inodemangel und Abstuerze zwischen zwei Schritten nur so
/// reproduzierbar sind. Ein fehlender Pfad meldet <see cref="ResultCode.NotFound"/>.
/// </summary>
internal interface IMigrationTarget
{
    /// <summary>
    /// Endung der Temp-Datei von <see cref="WriteFile"/> neben dem Ziel (pfad + Endung). Fester Bestandteil des Vertrags, damit
    /// eine Wiederholung die Reste eines abgebrochenen Schreibens als eigene Spur erkennt und entfernt.
    /// </summary>
    const string TemporarySuffix = ".zehos-migration-tmp";

    /// <summary>Legt ein Verzeichnis an; ein vorhandenes gilt als Erfolg (Wiederholung).</summary>
    ResultErr CreateDirectory(string path);
    /// <summary>
    /// Schreibt eine Datei vollstaendig ueber Temp (<see cref="TemporarySuffix"/>) + Rename. Dauerhaft ist sie erst nach
    /// <see cref="Sync"/>; der Ablauf setzt deshalb vor dem Marker "ready" eine Sync-Schranke.
    /// </summary>
    ResultErr WriteFile(string path, Stream content);
    /// <summary>Haengt eine Journalzeile an und flusht sie auf das Geraet, bevor der Aufrufer die zugehoerige Aktion beginnt.</summary>
    ResultErr AppendJournal(string line);
    /// <summary>Loescht eine Datei oder ein leeres Verzeichnis.</summary>
    ResultErr Delete(string path);
    /// <summary>Oeffnet eine geschriebene Datei zum Zuruecklesen; der Aufrufer gibt den Strom frei.</summary>
    ResultObject<Stream> ReadBack(string path);
    /// <summary>
    /// Alle Dateien und Verzeichnisse unter "/" ohne Mountgrenzen zu ueberschreiten, "/" selbst darf fehlen (Pruefung auf
    /// fremde Eintraege, §4).
    /// </summary>
    ResultObject<string[]> List();
    /// <summary>Verfuegbare, nicht reservierte Bloecke mal Blockgroesse.</summary>
    ResultObject<long> FreeBytes();
    ResultObject<long> FreeInodes();
    ResultErr Sync();
}
