using System;
using System.Globalization;
using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Storage.Migration;

internal enum LayoutState { Fresh, Migrating, Ready }

/// <summary>
/// Root-Marker /etc/zehos/.zehos-layout (Spec E4.4 §4): Zustand des Root-Baums. Der Installer schreibt "fresh", die Migration
/// "migrating" vor dem ersten Kopieren und "ready" mit Quellinventar und Dateizahl als letzten Schreibzugriff.
/// </summary>
internal sealed record LayoutMarker(LayoutState State, Guid DiskGuid, string? SourceHash = null, long? Files = null)
{
    public const string Path = "/etc/zehos/.zehos-layout";
    public const int MaximumBytes = 512;
    private const int MaximumLines = 6;

    public string Serialize()
    {
        string body = $"format=1\nstate={Name(State)}\ndisk={DiskGuid:D}\n";
        if (SourceHash != null) body += $"source={SourceHash}\nfiles={Files!.Value.ToString(CultureInfo.InvariantCulture)}\n";
        return ChecksummedText.Seal(body);
    }

    public static ResultObject<LayoutMarker> Parse(string text)
    {
        string[]? lines = ChecksummedText.Open(text, MaximumBytes, MaximumLines);
        if (lines == null) return Failure("MarkerChecksum", "Root-Marker zu gross oder Pruefsumme falsch.");
        // Feste Reihenfolge; Quelle und Dateizahl nur zusammen und nur bei "ready" (nach einer Migration).
        if (lines.Length is not (3 or 5) || lines[0] != "format=1" || !TryState(lines[1], out LayoutState state) ||
            !lines[2].StartsWith("disk=", StringComparison.Ordinal) || !Guid.TryParseExact(lines[2][5..], "D", out Guid disk) || disk == Guid.Empty)
            return Failure("MarkerField", "Felder fehlen, sind unbekannt oder ungueltig.");
        if (lines.Length == 3) return ResultObjectFactory.CreateSuccess(new LayoutMarker(state, disk));
        if (state != LayoutState.Ready || !lines[3].StartsWith("source=", StringComparison.Ordinal) || !IsSha256(lines[3][7..]) ||
            !lines[4].StartsWith("files=", StringComparison.Ordinal) ||
            !long.TryParse(lines[4][6..], NumberStyles.None, CultureInfo.InvariantCulture, out long files))
            return Failure("MarkerField", "Felder fehlen, sind unbekannt oder ungueltig.");
        return ResultObjectFactory.CreateSuccess(new LayoutMarker(state, disk, lines[3][7..], files));
    }

    /// <summary>
    /// Marker direkt aus einem eingehaengten Root lesen (Profilwahl 4.3, Zielpruefung 4.5): null = Datei fehlt; eine zu grosse
    /// Datei und jeder andere Lesefehler ergeben einen ungueltigen Marker (Unclear), nie "fehlt".
    /// </summary>
    internal static ResultObject<LayoutMarker>? ReadFile(string path) => StorageLock.Run(() => ReadFileStorageLocked(path));

    // Hoechstens MaximumBytes: Oeffnen, Lesen und Schliessen als ein Abschnitt.
    private static ResultObject<LayoutMarker>? ReadFileStorageLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (stream.Length > MaximumBytes) return Parse(string.Empty);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return Parse(string.Empty);
        }
    }

    internal static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static string Name(LayoutState state) => state switch
    {
        LayoutState.Fresh => "fresh",
        LayoutState.Migrating => "migrating",
        _ => "ready",
    };

    private static bool TryState(string line, out LayoutState state)
    {
        state = LayoutState.Fresh;
        switch (line)
        {
            case "state=fresh": return true;
            case "state=migrating": state = LayoutState.Migrating; return true;
            case "state=ready": state = LayoutState.Ready; return true;
            default: return false;
        }
    }

    private static ResultObject<LayoutMarker> Failure(string cause, string message) =>
        ResultObjectFactory.CreateFailure<LayoutMarker>(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
