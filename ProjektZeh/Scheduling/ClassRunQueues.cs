using System;
using System.Threading;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Die Run-Queues einer CPU, je Klasse eine. Feste Größe, keine Allokation nach dem Konstruktor.
/// Die Kapazität je Klasse muss mindestens der Threadzahl entsprechen: Yielded und PickFailed haben keinen Fehlerweg,
/// weil der Thread kurz zuvor aus der Queue genommen wurde und dort wieder Platz hat.
/// </summary>
internal sealed class ClassRunQueues<T>(int capacityPerClass, Func<T, ClassThreadState?> stateOf) where T : class
{
    public const ulong TickNs = 10_000_000;
    public const int StarvationLimit = 8;
    private const int ClassCount = 3;

    private readonly T?[][] items = [new T[capacityPerClass], new T[capacityPerClass], new T[capacityPerClass]];
    private readonly int[] counts = new int[ClassCount];
    // Abgelaufene Scheiben höherer Klassen, während diese Klasse wartet (Index = Klasse; Interactive bleibt 0).
    private readonly int[] starvation = new int[ClassCount];
    // Von Pick gemerkt: dieser Thread wurde verdrängt, ohne seine Scheibe verbraucht zu haben, und kommt vorn zurück.
    private T? requeueFront;
    private int waiting;

    /// <summary>1, 2, 4 Ticks für Interactive, Normal, Background.</summary>
    public static ulong SliceNs(ThreadClass threadClass) => threadClass switch
    {
        ThreadClass.Interactive => TickNs,
        ThreadClass.Normal => 2 * TickNs,
        _ => 4 * TickNs,
    };

    /// <summary>Wartende Threads aller Klassen.</summary>
    public int Count => counts[0] + counts[1] + counts[2];
    /// <summary>
    /// Wartende Threads aller Klassen, ohne Sperre lesbar: Der laufende Thread steht nicht darin (Pick entnimmt ihn), ein Wert
    /// größer 0 heißt also, dass außer ihm jemand bereit ist. Nur ein Hinweis, veröffentlicht am Ende jeder ändernden Methode.
    /// </summary>
    public int Waiting => Volatile.Read(ref waiting);
    public int CountOf(ThreadClass threadClass) => counts[(int)threadClass];

    /// <summary>Diagnose: Klassen aufsteigend, je Klasse in Queue-Reihenfolge.</summary>
    public T? At(int index)
    {
        if (index < 0) return null;
        for (int c = 0; c < ClassCount; c++)
        {
            if (index < counts[c]) return items[c][index];
            index -= counts[c];
        }
        return null;
    }

    public bool Contains(T thread) => Find(thread, out _, out _);

    /// <summary>Ans Ende seiner Klasse; false, wenn die Klasse voll ist. RemainingNs bleibt unberührt.</summary>
    public bool Ready(T thread)
    {
        if (Contains(thread)) return true;
        bool added = Insert(ClassOf(thread), thread, atFront: false);
        Publish();
        return added;
    }

    public void Blocked(T thread)
    {
        if (Find(thread, out int c, out int i)) RemoveAt(c, i);
        Publish();
    }

    /// <summary>Die Auswahl ist gescheitert: zurück an den Anfang seiner Klasse.</summary>
    public void PickFailed(T thread)
    {
        if (!Contains(thread)) Insert(ClassOf(thread), thread, atFront: true);
        Publish();
    }

    /// <summary>Nach Verdrängung: vorn mit Rest der Scheibe, wenn Pick das gemerkt hat, sonst hinten mit neuer Scheibe.</summary>
    public void Yielded(T thread)
    {
        if (!Contains(thread)) Reinsert(thread);
        Publish();
    }

    private void Reinsert(T thread)
    {
        bool front = ReferenceEquals(requeueFront, thread);
        requeueFront = null;
        if (!front && stateOf(thread) is { } state) state.RemainingNs = 0;
        Insert(ClassOf(thread), thread, front);
    }

    /// <summary>Liefert running zurück, wenn er weiterlaufen soll; null, wenn nichts wartet.</summary>
    public T? Pick(T? running)
    {
        var picked = PickCore(running);
        Publish();
        return picked;
    }

    private T? PickCore(T? running)
    {
        int starved = StarvedClass();
        int serve = starved >= 0 ? starved : FirstNonEmpty();
        requeueFront = null;
        if (serve < 0) return null;
        var runningState = running == null ? null : stateOf(running);
        int runningClass = running == null ? 0 : ClassOf(running);
        bool preempted = running != null && serve < runningClass && (runningState?.RemainingNs ?? 0) > 0;
        if (running != null && starved < 0 && serve > runningClass && (runningState?.RemainingNs ?? 0) > 0)
        {
            return running;
        }
        T? thread = items[serve][0];
        RemoveAt(serve, 0);
        starvation[serve] = 0;
        for (int c = serve + 1; c < ClassCount; c++) if (counts[c] > 0) starvation[c]++;
        if (stateOf(thread!) is { RemainingNs: 0 } state) state.RemainingNs = SliceNs((ThreadClass)serve);
        if (preempted) requeueFront = running;
        return thread;
    }

    /// <summary>true = neu auswählen. current darf selbst in der Queue stehen (geweckt, bevor er die CPU verlassen hat).</summary>
    public bool Tick(T current, ulong elapsedNs)
    {
        bool preempt = TickCore(current, elapsedNs);
        Publish();
        return preempt;
    }

    private bool TickCore(T current, ulong elapsedNs)
    {
        var state = stateOf(current);
        int own = ClassOf(current);
        bool queued = Find(current, out _, out _);
        ulong remaining = state == null ? 0 : (elapsedNs >= state.RemainingNs ? 0 : state.RemainingNs - elapsedNs);
        if (state != null) state.RemainingNs = remaining;
        if (remaining > 0) return false;
        int waitingSameOrHigher = 0;
        for (int c = 0; c <= own; c++) waitingSameOrHigher += counts[c];
        if (waitingSameOrHigher - (queued ? 1 : 0) > 0)
        {
            MoveToBack(current);
            return true;
        }
        bool starving = false;
        for (int c = own + 1; c < ClassCount; c++)
        {
            if (counts[c] == 0) continue;
            if (++starvation[c] >= StarvationLimit) starving = true;
        }
        if (starving) return true;
        if (state != null) state.RemainingNs = SliceNs((ThreadClass)own);
        return false;
    }

    /// <summary>Setzt die Klasse; ein wartender Thread zieht in die neue Queue um. false, wenn die neue Queue voll ist oder der Thread keinen Zustand hat.</summary>
    public bool SetClass(T thread, ThreadClass threadClass)
    {
        bool changed = SetClassCore(thread, threadClass);
        Publish();
        return changed;
    }

    private bool SetClassCore(T thread, ThreadClass threadClass)
    {
        if (stateOf(thread) is not { } state) return false;
        if (Find(thread, out int c, out int i))
        {
            if (c == (int)threadClass) { state.Class = threadClass; return true; }
            if (counts[(int)threadClass] == capacityPerClass) return false;
            RemoveAt(c, i);
            state.Class = threadClass;
            Insert((int)threadClass, thread, atFront: false);
            return true;
        }
        state.Class = threadClass;
        return true;
    }

    /// <summary>Vorderster verschiebbarer Thread der höchsten Klasse; entnimmt nichts.</summary>
    public T? PeekStealable(Func<T, bool> mayMove)
    {
        for (int c = 0; c < ClassCount; c++)
            for (int i = 0; i < counts[c]; i++)
                if (mayMove(items[c][i]!)) return items[c][i];
        return null;
    }

    private void Publish() => Volatile.Write(ref waiting, Count);

    private int ClassOf(T thread) => (int)(stateOf(thread)?.Class ?? ThreadClass.Normal);

    // Die höchste Klasse unter Normal und Hintergrund, die wartet und das Limit erreicht hat; sonst -1.
    private int StarvedClass()
    {
        for (int c = (int)ThreadClass.Normal; c < ClassCount; c++) if (counts[c] > 0 && starvation[c] >= StarvationLimit) return c;
        return -1;
    }

    private int FirstNonEmpty()
    {
        for (int c = 0; c < ClassCount; c++) if (counts[c] > 0) return c;
        return -1;
    }

    private bool Find(T thread, out int threadClass, out int index)
    {
        for (threadClass = 0; threadClass < ClassCount; threadClass++)
            for (index = 0; index < counts[threadClass]; index++)
                if (ReferenceEquals(items[threadClass][index], thread)) return true;
        threadClass = index = -1;
        return false;
    }

    private bool Insert(int threadClass, T thread, bool atFront)
    {
        if (counts[threadClass] == capacityPerClass) return false;
        var queue = items[threadClass];
        int at = atFront ? 0 : counts[threadClass];
        for (int i = counts[threadClass]; i > at; i--) queue[i] = queue[i - 1];
        queue[at] = thread;
        counts[threadClass]++;
        return true;
    }

    private void RemoveAt(int threadClass, int index)
    {
        var queue = items[threadClass];
        for (int i = index; i < counts[threadClass] - 1; i++) queue[i] = queue[i + 1];
        queue[--counts[threadClass]] = null;
        // Eine leere Klasse hungert nicht; ein später bereiter Thread erbt den alten Zähler nicht.
        if (counts[threadClass] == 0) starvation[threadClass] = 0;
    }

    private void MoveToBack(T thread)
    {
        if (!Find(thread, out int c, out int i)) return;
        int kept = starvation[c];
        RemoveAt(c, i);
        Insert(c, thread, atFront: false);
        starvation[c] = kept; // Umreihen ändert nicht, wie lange die Klasse wartet.
    }
}
