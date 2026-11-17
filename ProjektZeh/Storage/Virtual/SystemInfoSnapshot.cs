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

    /// <summary>
    /// Schreibt den Snapshot und zaehlt die UTF-8-Bytes laufend mit: Ein zu langes Feld oder eine Summe ueber 64 KiB bricht
    /// sofort ab, ohne den Rest zu bauen. Nach einem Abbruch ist jede weitere Ausgabe wirkungslos.
    /// </summary>
    private sealed class Writer
    {
        private readonly StringBuilder text = new();
        private int bytes;

        public string? Error { get; private set; }

        /// <summary>Feste ASCII-Anteile (Schluesselwoerter, Zahlen); ohne Maskierung.</summary>
        public Writer Raw(string ascii)
        {
            if (Error is null)
            {
                bytes += ascii.Length;
                text.Append(ascii);
                CheckTotal();
            }

            return this;
        }

        /// <summary>Ein Wert als ein Token: leer/null wird "-", Trenner und Steuerzeichen werden maskiert.</summary>
        public Writer Field(string? value)
        {
            if (Error is not null) return this;
            if (string.IsNullOrEmpty(value)) return Raw("-");
            // Jedes Zeichen braucht mindestens ein Byte: so lange Werte fallen vor dem Maskieren und ohne Allokation.
            if (value.Length > MaxFieldBytes) return Fail("Ein Textfeld ist laenger als 256 Byte.");
            int fieldBytes = 0;
            for (int index = 0; index < value.Length; index++)
            {
                int length = char.IsSurrogatePair(value, index) ? 2 : 1;
                string piece = SystemInfoSnapshot.Mask(value, index, length);
                fieldBytes += Encoding.UTF8.GetByteCount(piece);
                if (fieldBytes > MaxFieldBytes) return Fail("Ein Textfeld ist laenger als 256 Byte.");
                text.Append(piece);
                index += length - 1;
            }

            bytes += fieldBytes;
            CheckTotal();
            return this;
        }

        public ResultObject<byte[]> Result() => Error is null
            ? ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetBytes(text.ToString()))
            : ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, Error);

        private void CheckTotal()
        {
            if (bytes > MaxBytes) Fail("Snapshot groesser als 64 KiB.");
        }

        private Writer Fail(string message)
        {
            Error = message;
            text.Clear();
            return this;
        }
    }

    public static ResultObject<byte[]> Version(string version) => Build(w => w.Raw("version: ").Field(version).Raw("\n"));

    /// <summary>Ohne initialisierten Timer (null) steht "unavailable"; es gibt keinen belegten Idle-Zaehler.</summary>
    public static ResultObject<byte[]> Uptime(long? elapsedMilliseconds) => Build(w =>
    {
        if (elapsedMilliseconds is not { } ms || ms < 0) w.Raw("unavailable\n");
        else w.Raw((ms / 1000).ToString(CultureInfo.InvariantCulture)).Raw(".").Raw((ms % 1000).ToString("D3", CultureInfo.InvariantCulture)).Raw("\n");
    });

    public static ResultObject<byte[]> Memory(ulong pageSize, ulong totalPages, ulong freePages)
    {
        if (pageSize == 0 || freePages > totalPages)
            return Failure("Speicherzaehler widerspruechlich (frei > gesamt oder Seitengroesse 0).");
        if (totalPages > ulong.MaxValue / pageSize)
            return Failure("Speicherzaehler ueberlaufen.");
        ulong total = totalPages * pageSize, free = freePages * pageSize;
        return Build(w => w.Raw("MemTotal: " + total.ToString(CultureInfo.InvariantCulture) + " B\nMemFree: " + free.ToString(CultureInfo.InvariantCulture)
            + " B\nPageSize: " + pageSize.ToString(CultureInfo.InvariantCulture) + " B\n"));
    }

    public static ResultObject<byte[]> Mounts(IReadOnlyList<SystemInfoMount> mounts) => Records(mounts, (w, mount) =>
        w.Field(mount.Source).Raw(" ").Field(mount.MountPoint).Raw(" ").Field(mount.Name).Raw(mount.ReadOnly ? " ro\n" : " rw\n"));

    /// <summary>Zeitstempel, Stufe und Kategorie aus der Allowlist; die Nachricht (MAC, Befehle, Pfade, Exceptions) wird nie gelesen.</summary>
    public static ResultObject<byte[]> Log(IReadOnlyList<SystemLogEntry> entries) => Records(entries, (w, entry) =>
    {
        string category = Array.IndexOf(LogCategories, entry.Component) >= 0 ? entry.Component : "other";
        w.Raw(entry.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " " + entry.Level.ToString().ToUpperInvariant() + " " + category + "\n");
    });

    /// <summary>
    /// Zeigt nur "kompiliert"/"nicht kompiliert". shortcut: Die Matrix will das Buildflag vom initialisierten Dienst unterscheiden,
    /// das braucht die Dienstzustaende aus dem Aufrufer; nachziehen bei der Aktivierung (E5b).
    /// </summary>
    public static ResultObject<byte[]> Components(IReadOnlyList<SystemInfoComponent> features, IReadOnlyList<SystemInfoDriver> drivers)
    {
        if (features.Count + drivers.Count > MaxRecords) return Failure("Zu viele Datensaetze (hoechstens 128).");
        return Build(w =>
        {
            foreach (var feature in features) w.Raw("feature ").Field(feature.Name).Raw(feature.Compiled ? " compiled\n" : " absent\n");
            foreach (var driver in drivers) w.Raw("driver ").Field(driver.Name).Raw(" " + driver.Priority.ToString(CultureInfo.InvariantCulture) + "\n");
        });
    }

    public static ResultObject<byte[]> Devices(IReadOnlyList<SystemInfoDevice> devices) => Records(devices, (w, device) =>
        w.Field(device.Path).Raw(" ").Field(device.Bus).Raw(" ").Field(device.Driver).Raw(" ").Field(device.State).Raw("\n"));

    public static ResultObject<byte[]> Blocks(IReadOnlyList<SystemInfoBlock> blocks) => Records(blocks, (w, block) =>
        w.Field(block.Name).Raw(" " + block.SizeBytes.ToString(CultureInfo.InvariantCulture) + " " + block.Partitions.ToString(CultureInfo.InvariantCulture) + "\n"));

    /// <summary>Sektorzahl mal Sektorgroesse mit Ueberlaufpruefung (die bisherige Projektion multipliziert ungeprueft).</summary>
    public static ResultObject<ulong> SizeBytes(ulong sectors, ulong sectorSize) =>
        sectorSize == 0 || sectors > ulong.MaxValue / sectorSize
            ? ResultObjectFactory.CreateFailure<ulong>(ResultCode.InvalidArgument, "Plattengroesse ueberlaeuft.")
            : ResultObjectFactory.CreateSuccess(sectors * sectorSize);

    /// <summary>Wie ein Feld im Snapshot erscheint (Test und Aufrufer); leer/null ergibt "-".</summary>
    internal static string Field(string? value)
    {
        var writer = new Writer().Field(value);
        return writer.Error is null ? Encoding.UTF8.GetString(writer.Result().Value!) : throw new ArgumentException(writer.Error, nameof(value));
    }

    /// <summary>
    /// Maskiert, was ein Konsument als Trenner oder Steuerung lesen koennte: Leerraum, Backslash und die Kategorien
    /// Control, Format, Zeilen-/Absatztrenner, Leerzeichen-Trenner, Surrogat (allein), privat und nicht zugewiesen
    /// als \uXXXX bzw. \UXXXXXXXX.
    /// </summary>
    private static string Mask(string value, int index, int length)
    {
        char c = value[index];
        if (length == 1 && !char.IsSurrogate(c) && c != '\\' && !char.IsWhiteSpace(c) && !Masked(CharUnicodeInfo.GetUnicodeCategory(c)))
            return c.ToString();
        if (length == 2 && !Masked(CharUnicodeInfo.GetUnicodeCategory(value, index)))
            return value.Substring(index, 2);
        return length == 2
            ? "\\U" + char.ConvertToUtf32(c, value[index + 1]).ToString("x8", CultureInfo.InvariantCulture)
            : "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);
    }

    private static bool Masked(UnicodeCategory category) => category is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator
        or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;

    private static ResultObject<byte[]> Records<T>(IReadOnlyList<T> items, Action<Writer, T> write)
    {
        if (items.Count > MaxRecords) return Failure("Zu viele Datensaetze (hoechstens 128).");
        return Build(w => { foreach (var item in items) write(w, item); });
    }

    private static ResultObject<byte[]> Build(Action<Writer> write)
    {
        var writer = new Writer();
        write(writer);
        return writer.Result();
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

    /// <summary>Ruft die Erzeugung genau einmal je Open auf; Fehler und Ausnahmen der Erzeugung werden zu einem Fehlerergebnis.</summary>
    public static ResultObject<SystemInfoFile> Open(Func<ResultObject<byte[]>> produce)
    {
        ResultObject<byte[]> snapshot;
        try
        {
            snapshot = produce();
        }
        catch (Exception exception)
        {
            // Der Aufrufer liest Cosmos-Zustand; eine Ausnahme dort darf die Dateioperation nicht abbrechen.
            return ResultObjectFactory.CreateFailure<SystemInfoFile>(ResultCode.Exception, exception.Message);
        }

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
