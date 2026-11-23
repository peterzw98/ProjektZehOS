#if ZEHOS_WASM
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ProjektZeh.Runtime.Wasm;

// Roadmap 12.4-I2-A: Schliessen beendet eine App auch ohne should_exit. Eigene Datei, damit die Aenderung neben den
// Umbauten von WasmRuntime.cs im Multicore-Zweig (PollFaultIsolation, Worker-Start) klein bleibt.
internal static unsafe partial class WasmRuntime
{
    private const string AbortedMessage = "App beendet: Sie reagierte nicht auf das Schließen.";
    private static readonly AbortWatchdog watchdog = new();
    // Uhr wie in WasmGameApp: Stopwatch-Millisekunden.
    private static readonly Func<long> abortClock = () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
    // Lauf, dessen Job der Worker gerade ausfuehrt; unter gate gesetzt (Jobbeginn) und geloescht (Jobende).
    private static IWasmSession? abortRun;
    // 1, solange der Watchdog einen Lauf beobachtet; erspart PollAbort in der Hauptschleife sonst die Sperre.
    private static int abortWatching;

    /// <summary>
    /// Von WasmGameApp.RequestClose und Close: Endet der laufende Lauf dieser Sitzung nicht binnen
    /// <see cref="AbortWatchdog.GraceMilliseconds"/>, bricht PollAbort ihn ab. Fuer einen noch eingereihten Lauf ohne
    /// Wirkung: Der endet beim Laden selbst (WasmGameApp.Load prueft exitRequested), und ein gemerkter fremder Lauf
    /// koennte sonst den Platz des laufenden verdraengen.
    /// </summary>
    public static void CloseRequested(IWasmSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (gate)
        {
            if (!ReferenceEquals(session, abortRun)) return;
            watchdog.CloseRequested(session, abortClock());
            Volatile.Write(ref abortWatching, 1);
        }
    }

    /// <summary>
    /// Hauptschleife, neben PollFaultIsolation: Nach Ablauf der Frist setzt sie den Abbruchmerker der C-Schicht; der
    /// Worker endet dann am naechsten Pruefpunkt mit <see cref="WasmStatus.Aborted"/>. Nimmt nur gate und ruft weder
    /// Storage noch Netz, damit die Reihenfolge StorageLock vor AppStorageGate unberuehrt bleibt.
    /// </summary>
    public static void PollAbort()
    {
        if (Volatile.Read(ref abortWatching) == 0) return;
        bool first;
        lock (gate)
        {
            first = watchdog.ShouldAbort(abortRun, abortClock());
            // Auch nach dem ersten Mal je Runde erneut setzen: Der Lauf loescht den Merker beim Start der Laufzeit, etwa
            // wenn er waehrend des Ladens geschlossen wurde oder von AOT auf wasm3 zurueckfaellt.
            if (!first && !watchdog.IsAborting(abortRun)) return;
            NativeSetAbort(1);
        }
        if (first) Cosmos.Kernel.System.Diagnostics.Log.WriteString("WASM Abbruch: App reagierte 5 s nicht auf das Schliessen\n");
    }

    // Unter gate gerufen, am Beginn jedes Jobs aus EnqueueLockedJob.
    private static void BeginAbortWatch(IWasmSession session) => abortRun = session;

    // Unter gate gerufen, am Ende jedes Jobs: Danach trifft weder PollAbort noch ein spaetes Schliessen diesen Lauf.
    // Der Merker der C-Schicht bleibt bis zum naechsten Laufstart stehen; den loescht zehos_wasm_run selbst.
    private static void EndAbortWatch(IWasmSession session)
    {
        watchdog.RunEnded(session);
        abortRun = null;
        Volatile.Write(ref abortWatching, 0);
    }

    // Unter gate gerufen, wenn PollFaultIsolation einen Lauf nach einer CPU-Ausnahme von aussen beendet: Ohne das
    // schriebe ein spaeteres Schliessen des CpuFault-Fensters nach der Frist eine falsche Abbruchzeile.
    private static void ForgetAbortRun()
    {
        if (abortRun != null) watchdog.RunEnded(abortRun);
        abortRun = null;
        Volatile.Write(ref abortWatching, 0);
    }

    [DllImport("*", EntryPoint = "zehos_host_set_abort", ExactSpelling = true)]
    private static extern void NativeSetAbort(int on);
}
#endif
