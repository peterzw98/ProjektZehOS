using System;
using System.Globalization;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Partitioning;

/// <summary>
/// ESP-Marker des installierten Systems (Spec 7a, Abschnitt 5): Disk-GUID, Root-Partitions-GUID, Root-Start und -Groesse.
/// Textformat mit fester Feldreihenfolge und abschliessender crc32-Zeile, damit ein Teilschreiben nie als gueltig gilt.
/// Der Installer schreibt ihn als letzten Schritt (Temp + Rename); der Name traegt .zehos-, damit FileService ihn sperrt.
/// </summary>
internal sealed record InstallMarker(Guid DiskGuid, Guid RootGuid, ulong RootFirstLba, ulong RootSectors)
{
    /// <summary>Relativ zur ESP-Wurzel; im Gast also /data/boot/.zehos-install.id.</summary>
    public const string RelativePath = "boot/.zehos-install.id";
    public const int MaximumBytes = 512;
    public const int MaximumLines = 12;
    private const string Header = "zehos-install 1";
    private const string CrcKey = "crc32=";

    public string Serialize()
    {
        var culture = CultureInfo.InvariantCulture;
        string body = $"{Header}\ndisk={DiskGuid:D}\nroot={RootGuid:D}\nroot-first={RootFirstLba.ToString(culture)}\nroot-sectors={RootSectors.ToString(culture)}\n";
        return body + CrcKey + Crc32.Compute(Encoding.UTF8.GetBytes(body)).ToString("x8", culture) + "\n";
    }

    public static ResultObject<InstallMarker> Parse(string text)
    {
        int lines = 0;
        foreach (char value in text) if (value == '\n') lines++;
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes || lines > MaximumLines)
            return Failure("MarkerTooLarge", "Marker groesser als 512 Byte oder mehr als 12 Zeilen.");

        int crcLine = text.LastIndexOf("\n" + CrcKey, StringComparison.Ordinal);
        if (crcLine < 0 || !text.EndsWith('\n'))
            return Failure("MarkerChecksum", "crc32-Zeile fehlt.");
        string body = text[..(crcLine + 1)];
        string crcText = text[(crcLine + 1 + CrcKey.Length)..^1];
        if (crcText.Length != 8 || !uint.TryParse(crcText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint stored)
            || stored != Crc32.Compute(Encoding.UTF8.GetBytes(body)))
            return Failure("MarkerChecksum", "Pruefsumme passt nicht.");

        string[] fields = body.Split('\n');
        if (fields.Length != 6 || fields[0] != Header || fields[5].Length != 0
            || !TryGuid(fields[1], "disk=", out Guid disk) || !TryGuid(fields[2], "root=", out Guid root)
            || !TryNumber(fields[3], "root-first=", out ulong first) || !TryNumber(fields[4], "root-sectors=", out ulong sectors) || sectors == 0)
            return Failure("MarkerField", "Felder fehlen, sind unbekannt oder ungueltig.");
        return ResultObjectFactory.CreateSuccess(new InstallMarker(disk, root, first, sectors));
    }

    private static bool TryGuid(string line, string key, out Guid value)
    {
        value = Guid.Empty;
        return line.StartsWith(key, StringComparison.Ordinal) && Guid.TryParseExact(line[key.Length..], "D", out value) && value != Guid.Empty;
    }

    private static bool TryNumber(string line, string key, out ulong value)
    {
        value = 0;
        return line.StartsWith(key, StringComparison.Ordinal) && ulong.TryParse(line[key.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static ResultObject<InstallMarker> Failure(string cause, string message) =>
        ResultObjectFactory.CreateFailure<InstallMarker>(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
