using System;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Storage;

/// <summary>
/// Gemeinsame Schreibsperre fuer den App-Speicher /save (Etappe 13.4). Apps schreiben auf dem Wasm-Worker, waehrend der
/// praeemptive Scheduler andere Threads weiterlaufen laesst; Aushaengen und Energieaktionen duerfen deshalb nicht mitten in
/// ein Ersetzen (Temp, Sicherung, Rename) fallen. <see cref="Block"/> sperrt neue Schreiber und wartet auf einen laufenden;
/// danach melden schreibende App-Aufrufe EBUSY, Lesen bleibt. Die Migration (4.5) kann dieselbe Sperre nutzen.
/// </summary>
internal static class AppStorageGate
{
    private static readonly object operations = new();
#if ZEHOS_ISOLATION_PROBE
    internal static bool IsHeldByCurrentThread => System.Threading.Monitor.IsEntered(operations);
#endif
    private static readonly MigrationWriteGate writes = new();

    public static bool IsBlocked
    {
        get => writes.IsLocked;
    }

    public static bool IsMigrationLocked => writes.IsMigrationLocked;
    public static IDisposable? TryBeginWrite() => writes.TryBeginWrite();
    public static IDisposable? TryBeginWrite(string activity) => writes.TryBeginWrite(activity);
    public static string BusyDescription => writes.BusyDescription;
    public static IDisposable? TryLockSource() => writes.TryLockSource();
    public static bool TryBlock() => writes.TryBlock();

    /// <summary>Fuehrt eine Plattenoperation einer App aus; ist gesperrt, liefert sie <paramref name="busy"/> ohne Zugriff.</summary>
    public static int Run(Func<int> operation, int busy)
    {
        lock (operations)
        {
            using var writer = writes.TryBeginWrite();
            return writer == null ? busy : operation();
        }
    }

    /// <summary>Sperrt neue Schreiber; kehrt erst zurueck, wenn ein laufender fertig ist.</summary>
    public static void Block()
    {
        writes.Block();
    }

    public static void Release()
    {
        writes.ReleaseBlock();
    }
}
