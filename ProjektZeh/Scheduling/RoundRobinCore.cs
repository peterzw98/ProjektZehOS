using System;
using System.Threading;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Run-Queue und Verdraengung des Round-Robin-Schedulers ohne Cosmos-Typen, damit die Hosttests sie pruefen koennen.
/// Die Zeitscheibe eines Threads beginnt, wenn er ausgewaehlt wird, nicht wenn er bereit wird.
/// </summary>
/// <remarks>
/// Grund (18.11.2026, ClassiCube-Bursts): Unsere Hauptschleife ist in Cosmos der Idle-Thread. Ihr Thread.Sleep endet in
/// ConditionVariable.WaitPolling: Sie bleibt Running und wartet mit Halt, ohne die CPU abzugeben. Bisher bekam sie wie jeder
/// Thread eine volle Scheibe, die jede Wiedereinreihung neu fuellte; ein bereiter Wasm-Worker wartete bis zu 137 ms.
/// Der Idle-Thread wird deshalb im Tick immer verdraengt, sobald ein anderer wartet. Eine Ausnahme fuer eine rechnende
/// Hauptschleife (volle Scheibe) brachte unter KVM keinen Gewinn: Ob sie gerade wartet, ist im Tick nicht erkennbar.
/// Unter Dauerlast bekommt die Hauptschleife so einen Tick je Runde (ein Drittel der Ticks, bei n Rechnenden 1/(2n+1)).
/// </remarks>
internal sealed class RoundRobinCore<T>(int capacity, Func<T, SchedulerTimeSlice?> sliceOf, Func<T, bool> isIdle) where T : class
{
    private readonly RoundRobinQueue<T> queue = new(capacity);
    private int waiting;

    public int Count => queue.Count;
    /// <summary>
    /// Anzahl der Threads in der Run-Queue, ohne Sperre lesbar: Der laufende Thread steht nicht darin (Pick entnimmt ihn),
    /// ein Wert groesser 0 heisst also, dass ausser ihm jemand bereit ist. Nur ein Hinweis fuer die Hauptschleife.
    /// </summary>
    public int Waiting => Volatile.Read(ref waiting);
    public T? At(int index) => queue.At(index);
    public void Ready(T thread) { queue.Add(thread); Publish(); }
    public void Blocked(T thread) { queue.Remove(thread); Publish(); }
    public void PickFailed(T thread) { queue.AddFirst(thread); Publish(); }

    public T? Pick()
    {
        T? next = queue.Take();
        if (next != null) sliceOf(next)?.Reset();
        Publish();
        return next;
    }

    private void Publish() => Volatile.Write(ref waiting, queue.Count);

    /// <summary>Timer-Tick fuer den laufenden Thread; true heisst verdraengen, danach waehlt Cosmos ueber <see cref="Pick"/>.</summary>
    public bool Tick(T current, ulong elapsedNs)
    {
        // Der laufende Thread kann selbst in der Queue stehen, wenn ihn ein Ready-Aufruf ohne Wechsel eingereiht hat.
        bool queued = queue.Contains(current);
        int waiting = queue.Count - (queued ? 1 : 0);
        if (isIdle(current))
        {
            // Der Idle-Thread gibt die CPU beim Warten nicht ab (WaitPolling); wartet ein anderer, gehoert sie ihm ab diesem Tick.
            if (waiting == 0) return false;
        }
        else
        {
            SchedulerTimeSlice? slice = sliceOf(current);
            if (slice != null && !slice.Charge(elapsedNs)) return false;
            if (waiting == 0) { slice?.Reset(); return false; }
        }
        // Ans Ende statt vorn bleiben, damit Pick einen wartenden Thread waehlt und nicht den laufenden erneut.
        if (queued)
        {
            queue.Remove(current);
            queue.Add(current);
            Publish();
        }
        return true;
    }
}
