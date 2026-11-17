using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Migration;

/// <summary>
/// Journal /etc/zehos/.zehos-migration-journal (Spec E4.4 §4): je Ereignis eine Zeile, Pfade Base64 wie in der Rechtedatei.
/// "D" und "W" sind Absichten vor der Aktion, "F" bestaetigt eine zurueckgelesene Datei. Nur reines Formatieren und Lesen;
/// geschrieben wird ueber <see cref="IMigrationTarget.AppendJournal"/>.
/// </summary>
internal sealed class MigrationJournal
{
    public const string Path = "/etc/zehos/.zehos-migration-journal";
    private const string Header = "ZEHOS-MIGRATION-1 ";

    private MigrationJournal(string sourceHash, List<string> directories, List<string> files)
    {
        SourceHash = sourceHash;
        Directories = directories;
        Files = files;
    }

    public string SourceHash { get; }
    /// <summary>Verzeichnisse in Schreibreihenfolge (Eltern vor Kindern).</summary>
    public IReadOnlyList<string> Directories { get; }
    /// <summary>Dateien mit Absicht oder Bestaetigung, jeweils einmal.</summary>
    public IReadOnlyList<string> Files { get; }

    public static string HeaderLine(string sourceHash) => Header + sourceHash + "\n";
    public static string DirectoryLine(string path) => "D " + Encode(path) + "\n";
    public static string WriteLine(string path) => "W " + Encode(path) + "\n";
    public static string DoneLine(string path, string sha256) => "F " + Encode(path) + " " + sha256 + "\n";

    /// <summary>
    /// ext2 ohne Journal kann nach einem Absturz die Dateigroesse schon erhoeht haben: eine abgeschnittene letzte Zeile und ein
    /// Schwanz aus ungueltigen Zeilen (Nullbytes, Muell) gelten als nicht geschrieben. Folgt auf eine ungueltige Zeile noch eine
    /// gueltige, ist das Journal beschaedigt und eine Wiederholung wird verweigert.
    /// </summary>
    public static ResultObject<MigrationJournal> Parse(string text)
    {
        string[] lines = text.Split('\n');
        // Das letzte Element steht hinter dem letzten Umbruch: unvollstaendig geschrieben, nie ausgewertet.
        int complete = lines.Length - 1;
        if (complete < 1 || !lines[0].StartsWith(Header, StringComparison.Ordinal) || !LayoutMarker.IsSha256(lines[0][Header.Length..]))
            return Invalid("Journalkopf fehlt oder ist ungueltig.");

        var directories = new List<string>();
        var files = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        int firstInvalid = -1;
        for (int i = 1; i < complete; i++)
        {
            bool valid = TryParseLine(lines[i], out char kind, out string path);
            if (!valid)
            {
                if (firstInvalid < 0) firstInvalid = i;
                continue;
            }
            if (firstInvalid >= 0) return Invalid($"Beschaedigte Journalzeile {firstInvalid + 1} vor gueltigen Eintraegen.");
            if (kind == 'D') { if (seenDirectories.Add(path)) directories.Add(path); }
            else if (seenFiles.Add(path)) files.Add(path);
        }
        return ResultObjectFactory.CreateSuccess(new MigrationJournal(lines[0][Header.Length..], directories, files));
    }

    private static bool TryParseLine(string line, out char kind, out string path)
    {
        kind = '\0';
        path = "";
        string[] parts = line.Split(' ');
        if (parts.Length < 2 || parts[0].Length != 1) return false;
        kind = parts[0][0];
        bool shape = kind switch
        {
            'D' or 'W' => parts.Length == 2,
            'F' => parts.Length == 3 && LayoutMarker.IsSha256(parts[2]),
            _ => false,
        };
        return shape && TryDecode(parts[1], out path) && KernelPath.IsAbsoluteNormalized(path);
    }

    private static string Encode(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

    private static bool TryDecode(string value, out string path)
    {
        path = "";
        try
        {
            path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static ResultObject<MigrationJournal> Invalid(string message) =>
        ResultObjectFactory.CreateFailure<MigrationJournal>(ResultCode.Failure, $"cause='JournalInvalid' message='{message}'");
}
