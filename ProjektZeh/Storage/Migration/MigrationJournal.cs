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
    // Kopf plus hoechstens D- oder W+F-Zeilen je Inventareintrag; Reserve fuer vom Plan ergaenzte Elternordner.
    internal const int MaximumLines = 2 * MigrationInventory.MaximumEntries + 1024;
    // Laengere Zeilen sind ungueltig; ihr Rest wird ueberlesen statt gepuffert.
    internal const int MaximumLineLength = 16 * 1024;

    private MigrationJournal(string? sourceHash, List<string> directories, List<string> files)
    {
        SourceHash = sourceHash;
        Directories = directories;
        Files = files;
    }

    /// <summary>Quellinventar des abgebrochenen Laufs; <see langword="null"/>, wenn schon der Kopf nicht vollstaendig geschrieben wurde.</summary>
    public string? SourceHash { get; }
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
    /// gueltige, ist das Journal beschaedigt und eine Wiederholung wird verweigert. Das gilt auch fuer den Kopf: ist er
    /// unvollstaendig und folgt nichts Gueltiges, wurde vor dem Absturz noch nichts begonnen (leeres Journal).
    /// </summary>
    public static ResultObject<MigrationJournal> Parse(string text) => Parse(new System.IO.StringReader(text));

    /// <summary>
    /// Wie <see cref="Parse(string)"/>, aber zeilenweise aus dem Strom (Review peter-review P2-2): bei 100 000 Eintraegen waeren
    /// Text, Kopie und Zeilenfeld mehrere grosse Arrays, an denen Cosmos im E4.0-Lauf 10 null statt OutOfMemory lieferte.
    /// </summary>
    public static ResultObject<MigrationJournal> Parse(System.IO.TextReader reader)
    {
        // Eine Zeile ohne abschliessenden Umbruch wurde unvollstaendig geschrieben und wird nie ausgewertet.
        string? first = ReadCompleteLine(reader);
        bool header = first != null && first.StartsWith(Header, StringComparison.Ordinal) && LayoutMarker.IsSha256(first[Header.Length..]);

        var directories = new List<string>();
        var files = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        int firstInvalid = -1;
        int i = 0;
        for (string? line = first == null ? null : ReadCompleteLine(reader); line != null; line = ReadCompleteLine(reader))
        {
            if (++i >= MaximumLines) return Invalid("Journal hat zu viele Zeilen.");
            bool valid = TryParseLine(line, out char kind, out string path);
            if (!valid)
            {
                if (firstInvalid < 0) firstInvalid = i;
                continue;
            }
            if (!header) return Invalid("Journalkopf ungueltig vor gueltigen Eintraegen.");
            if (firstInvalid >= 0) return Invalid($"Beschaedigte Journalzeile {firstInvalid + 1} vor gueltigen Eintraegen.");
            if (kind == 'D') { if (seenDirectories.Add(path)) directories.Add(path); }
            else if (seenFiles.Add(path)) files.Add(path);
        }
        return ResultObjectFactory.CreateSuccess(new MigrationJournal(header ? first![Header.Length..] : null, directories, files));
    }

    /// <summary>Naechste Zeile ohne Umbruch; null am Ende, auch fuer einen Rest ohne Umbruch. Ueberlange Zeilen ergeben "".</summary>
    private static string? ReadCompleteLine(System.IO.TextReader reader)
    {
        var line = new StringBuilder();
        bool overlong = false;
        for (int next = reader.Read(); next != -1; next = reader.Read())
        {
            if (next == '\n') return overlong ? string.Empty : line.ToString();
            if (line.Length < MaximumLineLength) line.Append((char)next);
            else overlong = true;
        }
        return null;
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
