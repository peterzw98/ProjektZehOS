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
    /// <summary>Fuer den CPU-Ausnahmepfad (Roadmap 12.4-I1): unter dieser Sperre darf kein Thread aufgegeben werden.</summary>
    internal static bool IsHeldByCurrentThread => System.Threading.Monitor.IsEntered(operations);
    private static readonly MigrationWriteGate writes = new();
    // Etappe 7b: Schreiber auf das Root-Volume, getrennt vom /data-Block der Datentraegerbefehle (Review 7b P1-1), sonst hoebe
    // disk mount/unmount die Sperre der Energieaktion auf oder sperrte Root beim Aushaengen einer Zusatzplatte.
    private static readonly MigrationWriteGate rootWrites = new();

    public static IDisposable? TryBeginRootWrite() => rootWrites.TryBeginWrite();
    public static IDisposable? TryBeginRootWrite(string activity) => rootWrites.TryBeginWrite(activity);
    /// <summary>Sperrt alle Root-Schreiber ohne zu warten; false, solange einer schreibt. Nur die Energieaktion haelt sie.</summary>
    public static bool TryLockRoot() => rootWrites.TryBlock();
    public static void ReleaseRoot() => rootWrites.ReleaseBlock();

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
            // Im Root-Profil liegt /save auf dem Root; ausserhalb sperrt niemand rootWrites.
            using var root = writer == null ? null : rootWrites.TryBeginWrite();
            return writer == null || root == null ? busy : operation();
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
