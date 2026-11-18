using System;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Run-Queue und Verdraengung des Round-Robin-Schedulers ohne Cosmos-Typen, damit die Hosttests sie pruefen koennen.
/// Die Zeitscheibe eines Threads beginnt, wenn er ausgewaehlt wird, nicht wenn er bereit wird.
/// </summary>
/// <remarks>
/// Grund (18.11.2026, ClassiCube-Bursts): Unsere Hauptschleife ist in Cosmos der Idle-Thread. Cosmos parkt ihn in
/// Thread.Sleep nur mit Halt, ohne die CPU abzugeben, und weckt ihn im naechsten Timer-Tick wieder (CheckSleepingThreads
/// vor OnTick). Fuellte das Wecken seine Scheibe neu, lief sie nie ab, und ein bereiter Wasm-Worker wartete bis zu ~100 ms.
/// </remarks>
internal sealed class RoundRobinCore<T>(int capacity, Func<T, SchedulerTimeSlice?> sliceOf, Func<T, bool> isIdle) where T : class
{
    private readonly RoundRobinQueue<T> queue = new(capacity);

    public int Count => queue.Count;
    public T? At(int index) => queue.At(index);
    public void Ready(T thread) => queue.Add(thread);
    public void Blocked(T thread) => queue.Remove(thread);
    public void PickFailed(T thread) => queue.AddFirst(thread);

    public T? Pick()
    {
        T? next = queue.Take();
        if (next != null) sliceOf(next)?.Reset();
        return next;
    }

    /// <summary>Timer-Tick fuer den laufenden Thread; true heisst verdraengen, danach waehlt Cosmos ueber <see cref="Pick"/>.</summary>
    public bool Tick(T current, ulong elapsedNs)
    {
        // Der laufende Thread kann selbst in der Queue stehen, wenn der Tick ihn geweckt hat, ohne dass er gewechselt hat.
        bool queued = queue.Contains(current);
        int waiting = queue.Count - (queued ? 1 : 0);
        if (isIdle(current))
        {
            // Der Idle-Thread gibt die CPU beim Parken nicht ab; wartet ein anderer, gehoert sie diesem sofort.
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
        }
        return true;
    }
}
