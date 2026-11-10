using System;

namespace ProjektZeh.SystemServices;

/// <summary>
/// Hält die juengsten Diagnosen im Speicher und leitet sie optional weiter.
/// </summary>
internal sealed class SystemLog
{
    private readonly SystemLogEntry?[] entries;
    private readonly Func<long> elapsedMilliseconds;
    private readonly ISystemLogSink? sink;
    private int nextEntry;
    private int count;

    public int Capacity => entries.Length;
    public int Count => count;

    public SystemLog(int capacity, Func<long> elapsedMilliseconds, ISystemLogSink? sink)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Die Logkapazitaet muss positiv sein.");
        }

        this.elapsedMilliseconds = elapsedMilliseconds ?? throw new ArgumentNullException(nameof(elapsedMilliseconds));
        entries = new SystemLogEntry[capacity];
        this.sink = sink;
    }

    /// <summary>
    /// Speichert eine Information zum Systemzustand.
    /// </summary>
    /// <param name="component"></param>
    /// <param name="message"></param>
    public void Information(string component, string message)
    {
        Write(LogLevel.Information, component, message);
    }

    /// <summary>
    /// Speichert eine Warnung, nach der der Kernel weiterarbeiten kann.
    /// </summary>
    /// <param name="component"></param>
    /// <param name="message"></param>
    public void Warning(string component, string message)
    {
        Write(LogLevel.Warning, component, message);
    }

    /// <summary>
    /// Speichert einen Fehler zur spaeteren Diagnose.
    /// </summary>
    /// <param name="component"></param>
    /// <param name="message"></param>
    public void Error(string component, string message)
    {
        Write(LogLevel.Error, component, message);
    }

    /// <summary>
    /// Gibt die Eintraege vom aeltesten zum neuesten zurueck.
    /// </summary>
    /// <returns></returns>
    public SystemLogEntry[] GetEntries()
    {
        var snapshot = new SystemLogEntry[count];
        int start = count == entries.Length ? nextEntry : 0;
        for (int index = 0; index < count; index++)
        {
            snapshot[index] = entries[(start + index) % entries.Length]!;
        }

        return snapshot;
    }

    private void Write(LogLevel level, string component, string message)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            throw new ArgumentException("Die Komponente darf nicht leer sein.", nameof(component));
        }

        ArgumentNullException.ThrowIfNull(message);

        var entry = new SystemLogEntry(elapsedMilliseconds(), level, component, message);
        entries[nextEntry] = entry;
        nextEntry = (nextEntry + 1) % entries.Length;
        if (count < entries.Length)
        {
            count++;
        }

        sink?.Write(entry);
    }
}
