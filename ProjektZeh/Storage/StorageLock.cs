using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ProjektZeh.Storage;

/// <summary>
/// Eine gemeinsame, reentrante Sperre um jeden Plattenzugriff. Cosmos 3.0.97 schuetzt ext2 und FAT nicht gegen
/// Nebenlaeufigkeit: Diagnoselauf 13 (18.11.) las ohne Sperre unveraenderte Dateien falsch (etwa 1 in 30 000 Lesungen) und
/// hinterliess beschaedigte Metadaten (e2fsck: doppelt belegte Bloecke, ueberschriebene Inode-Tabelle), sobald zwei Threads
/// gleichzeitig zugriffen, auch nur ein Leser neben einem Schreiber. Hauptschleife, Wasm-Worker und Hintergrundaufgaben
/// greifen ueber diese Sperre zu.
/// </summary>
/// <remarks>
/// Regeln (Plan senior, vom Koordinator angenommen 18.11. 00:32):
/// <list type="bullet">
/// <item>Gesperrt wird je einzelner Plattenoperation in den Speicher-Grenzklassen, nie um die Hauptschleife, weil der
/// Hauptthread auf Worker wartet (Join, Block).</item>
/// <item>Unter der Sperre nie auf andere Threads, Nutzer oder Netz warten.</item>
/// <item>Reihenfolge: AppStorageGate.operations, dann PackageStore.gate, dann MigrationWriteGate, StorageLock immer
/// innen. Wer auf Schreiber wartet (Block, TryLockSource), darf die Sperre nicht halten (<see cref="EnsureNotHeld"/>).</item>
/// <item>Kein Abbruch nach Zeit: ein abgebrochener Plattenschreibvorgang waere schlimmer als Warten. Lange Warte- oder
/// Haltezeiten werden mit Aufrufer gemeldet.</item>
/// </list>
/// </remarks>
internal static class StorageLock
{
    private static readonly object Gate = new();

    /// <summary>Ab dieser Warte- oder Haltezeit meldet die Sperre den Aufrufer (Diagnose, kein Abbruch).</summary>
    internal static TimeSpan SlowThreshold { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Empfaenger langer Warte- und Haltezeiten; der Kernel setzt das Bootprotokoll ein.</summary>
    internal static Action<string>? Report { get; set; }

    /// <summary>Haelt der aktuelle Thread die Sperre?</summary>
    public static bool IsHeldByCurrentThread => Monitor.IsEntered(Gate);

    /// <summary>Fuehrt eine Plattenoperation unter der Sperre aus.</summary>
    public static T Run<T>(Func<T> operation, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        long requested = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            long entered = Stopwatch.GetTimestamp();
            try
            {
                return operation();
            }
            finally
            {
                Measure(requested, entered, caller, file);
            }
        }
    }

    /// <inheritdoc cref="Run{T}(Func{T}, string, string)"/>
    public static void Run(Action operation, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        Run(() =>
        {
            operation();
            return true;
        }, caller, file);
    }

    /// <summary>
    /// Fuer Aufrufer, die auf andere Threads warten (Schreiber-Sperre, Join): unter der Speicher-Sperre waere das ein
    /// Deadlock, weil der erwartete Thread fuer seinen naechsten Plattenzugriff dieselbe Sperre braucht.
    /// </summary>
    public static void EnsureNotHeld(string activity)
    {
        if (IsHeldByCurrentThread)
            throw new InvalidOperationException($"{activity} darf nicht unter der Speicher-Sperre warten (Deadlock).");
    }

    private static void Measure(long requested, long entered, string caller, string file)
    {
        if (Report is not { } report) return;
        TimeSpan waited = Stopwatch.GetElapsedTime(requested, entered);
        TimeSpan held = Stopwatch.GetElapsedTime(entered);
        if (waited < SlowThreshold && held < SlowThreshold) return;
        string source = System.IO.Path.GetFileNameWithoutExtension(file);
        report($"StorageLock {source}.{caller}: gewartet {waited.TotalMilliseconds:F0} ms, gehalten {held.TotalMilliseconds:F0} ms");
    }
}
