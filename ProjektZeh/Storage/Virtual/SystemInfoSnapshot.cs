using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjektZeh.Shared;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Storage.Virtual;

internal sealed record SystemInfoMount(string Name, string Source, string MountPoint, bool ReadOnly);
internal sealed record SystemInfoComponent(string Name, bool Compiled);
internal sealed record SystemInfoDriver(string Name, int Priority);
/// <summary>Bewusst ohne LastFault, Offer-Grund und Rohadressen (Matrix 82098b6).</summary>
internal sealed record SystemInfoDevice(string Path, string Bus, string Driver, string State);
internal sealed record SystemInfoBlock(string Name, ulong SizeBytes, int Partitions);

/// <summary>
/// Reine Textbausteine fuer /proc und /sys (Etappe 5a, Matrix docs/superpowers/specs/2026-11-17-system-files-inventory.md).
/// Jede Funktion liefert einen vollstaendigen UTF-8-Snapshot oder einen begrenzten Fehler, nie eine gekuerzte Datei:
/// hoechstens 64 KiB, 128 Datensaetze, 256 Byte je Textfeld. Die Eingaben sind Wertkopien; Cosmos-Zugriffe liegen im Aufrufer.
/// </summary>
internal static class SystemInfoSnapshot
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxRecords = 128;
    public const int MaxFieldBytes = 256;

    // Freitext des Systemprotokolls wird nie ausgegeben; nur diese festen Kategorien (Kernel.cs/Dienste), alles andere "other".
    private static readonly string[] LogCategories = ["Boot", "Desktop", "Network", "Packages", "Setup"];

    public static ResultObject<byte[]> Version(string version) => Build(1, text => text.Append("version: ").Append(Field(version)).Append('\n'));

    /// <summary>Ohne initialisierten Timer (null) steht "unavailable"; es gibt keinen belegten Idle-Zaehler.</summary>
    public static ResultObject<byte[]> Uptime(long? elapsedMilliseconds) => Build(1, text =>
    {
        if (elapsedMilliseconds is not { } ms || ms < 0) text.Append("unavailable\n");
        else text.Append((ms / 1000).ToString(CultureInfo.InvariantCulture)).Append('.').Append((ms % 1000).ToString("D3", CultureInfo.InvariantCulture)).Append('\n');
    });

    public static ResultObject<byte[]> Memory(ulong pageSize, ulong totalPages, ulong freePages)
    {
        if (pageSize == 0 || freePages > totalPages)
            return Failure("Speicherzaehler widerspruechlich (frei > gesamt oder Seitengroesse 0).");
        if (totalPages > ulong.MaxValue / pageSize)
            return Failure("Speicherzaehler ueberlaufen.");
        ulong total = totalPages * pageSize, free = freePages * pageSize;
        return Build(3, text => text.Append("MemTotal: ").Append(total).Append(" B\nMemFree: ").Append(free).Append(" B\nPageSize: ").Append(pageSize).Append(" B\n"));
    }

    public static ResultObject<byte[]> Mounts(IReadOnlyList<SystemInfoMount> mounts) => Records(mounts, (text, mount) =>
        text.Append(Field(mount.Source)).Append(' ').Append(Field(mount.MountPoint)).Append(' ').Append(Field(mount.Name)).Append(mount.ReadOnly ? " ro\n" : " rw\n"));

    /// <summary>Zeitstempel, Stufe und Kategorie aus der Allowlist; die Nachricht (MAC, Befehle, Pfade, Exceptions) wird nie gelesen.</summary>
    public static ResultObject<byte[]> Log(IReadOnlyList<SystemLogEntry> entries) => Records(entries, (text, entry) =>
    {
        string category = Array.IndexOf(LogCategories, entry.Component) >= 0 ? entry.Component : "other";
        text.Append(entry.ElapsedMilliseconds).Append(' ').Append(entry.Level.ToString().ToUpperInvariant()).Append(' ').Append(category).Append('\n');
    });

    public static ResultObject<byte[]> Components(IReadOnlyList<SystemInfoComponent> features, IReadOnlyList<SystemInfoDriver> drivers)
    {
        if (features.Count + drivers.Count > MaxRecords) return Failure("Zu viele Datensaetze (hoechstens 128).");
        return Build(features.Count + drivers.Count, text =>
        {
            foreach (var feature in features) text.Append("feature ").Append(Field(feature.Name)).Append(feature.Compiled ? " compiled\n" : " absent\n");
            foreach (var driver in drivers) text.Append("driver ").Append(Field(driver.Name)).Append(' ').Append(driver.Priority).Append('\n');
        });
    }

    public static ResultObject<byte[]> Devices(IReadOnlyList<SystemInfoDevice> devices) => Records(devices, (text, device) =>
        text.Append(Field(device.Path)).Append(' ').Append(Field(device.Bus)).Append(' ').Append(Field(device.Driver)).Append(' ').Append(Field(device.State)).Append('\n'));

    public static ResultObject<byte[]> Blocks(IReadOnlyList<SystemInfoBlock> blocks) => Records(blocks, (text, block) =>
        text.Append(Field(block.Name)).Append(' ').Append(block.SizeBytes).Append(' ').Append(block.Partitions).Append('\n'));

    /// <summary>Sektorzahl mal Sektorgroesse mit Ueberlaufpruefung (die bisherige Projektion multipliziert ungeprueft).</summary>
    public static ResultObject<ulong> SizeBytes(ulong sectors, ulong sectorSize) =>
        sectorSize == 0 || sectors > ulong.MaxValue / sectorSize
            ? ResultObjectFactory.CreateFailure<ulong>(ResultCode.InvalidArgument, "Plattengroesse ueberlaeuft.")
            : ResultObjectFactory.CreateSuccess(sectors * sectorSize);

    /// <summary>Maskiert Leerzeichen, Steuerzeichen, Backslash und alleinstehende Surrogate als \uXXXX, damit Felder ein Token bleiben.</summary>
    internal static string Field(string value)
    {
        var text = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char c = value[index];
            if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                text.Append(c).Append(value[++index]);
                continue;
            }

            if (c <= ' ' || c == '\\' || (c >= '\u007f' && c <= '\u009f') || char.IsSurrogate(c))
                text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else
                text.Append(c);
        }

        return text.ToString();
    }

    private static ResultObject<byte[]> Records<T>(IReadOnlyList<T> items, Action<StringBuilder, T> write)
    {
        if (items.Count > MaxRecords) return Failure("Zu viele Datensaetze (hoechstens 128).");
        return Build(items.Count, text => { foreach (var item in items) write(text, item); });
    }

    private static ResultObject<byte[]> Build(int records, Action<StringBuilder> write)
    {
        var text = new StringBuilder();
        write(text);
        string result = text.ToString();
        // Ein Feld nach dem Maskieren hat hoechstens 256 Byte UTF-8: grobe Obergrenze je Zeile pruefen, dann Gesamtgroesse.
        foreach (string line in result.Split('\n'))
        {
            foreach (string field in line.Split(' '))
                if (Encoding.UTF8.GetByteCount(field) > MaxFieldBytes) return Failure("Ein Textfeld ist laenger als 256 Byte.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(result);
        return bytes.Length > MaxBytes ? Failure("Snapshot groesser als 64 KiB.") : ResultObjectFactory.CreateSuccess(bytes);
    }

    private static ResultObject<byte[]> Failure(string message) => ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, message);
}

/// <summary>
/// Ein geoeffneter Snapshot: Die Bytes werden beim Oeffnen einmal erzeugt; Teilreads lesen immer dieselben Bytes,
/// EOF bleibt EOF, die Laenge ist von Anfang an bekannt (nicht 0), erneutes Oeffnen erzeugt neu.
/// </summary>
internal sealed class SystemInfoFile
{
    private readonly byte[] bytes;

    private SystemInfoFile(byte[] bytes) => this.bytes = bytes;

    public long Length => bytes.Length;

    /// <summary>Ruft die Erzeugung genau einmal je Open auf; Fehler werden unveraendert weitergegeben.</summary>
    public static ResultObject<SystemInfoFile> Open(Func<ResultObject<byte[]>> produce)
    {
        var snapshot = produce();
        return snapshot.IsOK() && snapshot.Value is { } data
            ? ResultObjectFactory.CreateSuccess(new SystemInfoFile(data))
            : ResultObjectFactory.CreateFailure<SystemInfoFile>(snapshot.Status);
    }

    /// <summary>Liest ab <paramref name="offset"/>; 0 Bytes ab oder hinter dem Ende, negativer Offset ist ein Fehler.</summary>
    public ResultObject<int> Read(long offset, Span<byte> buffer)
    {
        if (offset < 0) return ResultObjectFactory.CreateFailure<int>(ResultCode.InvalidArgument, "Negativer Offset.");
        if (offset >= bytes.Length) return ResultObjectFactory.CreateSuccess(0);
        int count = (int)Math.Min(buffer.Length, bytes.Length - offset);
        bytes.AsSpan((int)offset, count).CopyTo(buffer);
        return ResultObjectFactory.CreateSuccess(count);
    }
}
