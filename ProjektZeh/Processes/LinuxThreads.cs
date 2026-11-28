using System;
using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes;

/// <summary>
/// Ein Thread eines Linux-Prozesses (V10): eigene TID, eigene FS-Basis (TLS) und eigene <c>clear_child_tid</c>-Adresse. Speicher,
/// Deskriptoren und Signalhandler teilt er mit dem Prozess (<c>CLONE_VM|CLONE_FILES|CLONE_SIGHAND</c>).
/// </summary>
internal sealed class LinuxThread
{
    public LinuxThread(int tid) => Tid = tid;

    public int Tid { get; }

    /// <summary>FS-Basis dieses Threads (arch_prctl oder <c>CLONE_SETTLS</c>).</summary>
    public ulong FsBase { get; set; }

    /// <summary>set_tid_address oder <c>CLONE_CHILD_CLEARTID</c>: beim Threadende 0 schreiben und einen Wartenden wecken.</summary>
    public ulong ClearChildTid { get; set; }

    /// <summary>Stack des neuen Threads aus clone (0: der Stack des Elternthreads, wie bei Linux).</summary>
    public ulong StackPointer { get; init; }

    /// <summary>N4-S4: Warteobjekt dieses Threads für futex und Schlaf; mit dem Thread angelegt, damit Warten nichts allokiert.</summary>
    public LinuxFutexWaiter FutexWait { get; } = new();

    /// <summary>Gesetzt, sobald der Thread mit exit geendet hat (oder der Prozess mit exit_group).</summary>
    public bool Exited { get; set; }

    /// <summary>
    /// V38: Signalmaske dieses Threads (Linux <c>task_struct.blocked</c>), Bit = Nummer - 1. Bis V37 galt eine Maske je Prozess: Ladybird
    /// blockiert beim Starten eines Kinds kurz alle Signale (<c>pthread_sigmask(SIG_SETMASK, all, &amp;old)</c>) und stellt danach
    /// <c>old</c> her; lief dazwischen auf einem anderen Thread ein Handler (Maske mit dem Signal), blieb so die Maske eines fremden
    /// Zeitpunkts stehen und der Hub sah nie wieder SIGCHLD. Schreiben nur der eigene Thread (sigprocmask, Handler, rt_sigreturn, Wartemaske)
    /// unter <c>_threadGate</c> oder mit <see cref="Volatile"/>; andere Threads lesen sie bei der Zustellung unter <c>_threadGate</c>.
    /// Neue Threads und Prozesse (clone, fork, vfork, execve) erben die Maske des aufrufenden Threads.
    /// </summary>
    public ulong Blocked;

    /// <summary>
    /// V38: an diesen Thread gerichtete anhängige Signale (tgkill, tkill; Linux <c>task_struct.pending</c>), Bit = Nummer - 1, unter
    /// <c>_threadGate</c>. Nur dieser Thread nimmt sie; Signale an den Prozess stehen beim Prozess.
    /// </summary>
    public ulong Pending;

    /// <summary>
    /// V19 ppoll: Maske vor dem Warten, solange die Wartemaske gilt (Linux: saved_sigmask mit TIF_RESTORE_SIGMASK). Nur der eigene Thread
    /// schreibt und liest sie (Aufruf und Rücksprung desselben Threads), deshalb ohne Sperre.
    /// </summary>
    public ulong SavedBlocked { get; set; }

    /// <summary>V19: <see cref="SavedBlocked"/> ist gültig und muss vor dem Rücksprung in den Ring 3 zurück.</summary>
    public bool RestoreBlocked { get; set; }

    /// <summary>Gehört der Brücke des Kernels (Kernelthread, gesicherte Register); der plattformunabhängige Teil liest es nie.</summary>
    public object? Host { get; set; }

    /// <summary>
    /// V29c: Quelle der CPU-Zeit dieses Threads in Nanosekunden (der Brückenteil des Kernels liest <c>SchedulerThread.TotalRuntime</c>);
    /// null, solange kein Kernelthread zugeordnet ist oder in den Hosttests.
    /// </summary>
    public Func<long>? CpuNanosSource { get; set; }

    private long _cpuFinal = -1;

    /// <summary>CPU-Zeit in Nanosekunden: laufend aus der Quelle, nach <see cref="FreezeCpu"/> der eingefrorene Endwert.</summary>
    public long CpuNanos
    {
        get
        {
            long frozen = Volatile.Read(ref _cpuFinal);
            return frozen >= 0 ? frozen : CpuNanosSource?.Invoke() ?? 0;
        }
    }

    /// <summary>Friert die CPU-Zeit beim Threadende ein und gibt die Quelle (den Kernelthread) frei; liefert den Endwert.</summary>
    public long FreezeCpu()
    {
        long frozen = Volatile.Read(ref _cpuFinal);
        if (frozen >= 0)
        {
            return frozen;
        }

        long now = CpuNanosSource?.Invoke() ?? 0;
        Volatile.Write(ref _cpuFinal, now);
        CpuNanosSource = null;
        return now;
    }
}

/// <summary>V29c: CPU-Zeit je Thread und Prozess (clock_gettime CPUTIME, getrusage, /proc/&lt;pid&gt;/stat). Nur die Brücke des Kernels zählt; ohne sie ist alles 0.</summary>
internal interface ILinuxCpuTime
{
    /// <summary>CPU-Zeit des aufrufenden Threads in Nanosekunden.</summary>
    long CurrentThreadCpuNanos { get; }

    /// <summary>CPU-Zeit aller Threads des Prozesses (laufende und beendete) in Nanosekunden.</summary>
    long ProcessCpuNanos { get; }
}

/// <summary>
/// Optionale Fähigkeit eines Prozesses (wie <see cref="ILinuxFiles"/>): mehrere Threads. Ohne sie bleibt S1: ein Thread, gettid ist
/// die PID, futex kann nur sich selbst nicht wecken.
/// </summary>
internal interface ILinuxThreads
{
    /// <summary>Der Thread, auf dem der laufende Aufruf kam.</summary>
    LinuxThread CurrentThread { get; }

    LinuxFutexTable Futexes { get; }

    /// <summary>clone für Threads; liefert die TID des neuen Threads oder -errno.</summary>
    long CloneThread(ulong flags, ulong stack, ulong parentTid, ulong childTid, ulong tls);

    /// <summary>exit (60): beendet nur den aufrufenden Thread; der letzte beendet den Prozess mit seinem Code.</summary>
    void ExitThread(int code);

    /// <summary>tgkill/tkill; <paramref name="tgid"/> negativ heißt tkill (ohne Gruppenprüfung).</summary>
    long SignalThread(int tgid, int tid, int signal);

    /// <summary>Nach sigprocmask und sigaction: anhängige Signale verwerfen oder ihrer Standardaktion folgen (V10-Review P3).</summary>
    void SignalStateChanged();

    /// <summary>
    /// sigaction: liefert die bisherige Aktion (Handler, Flags, Restorer, Maske) in <paramref name="action"/> und setzt bei
    /// <paramref name="set"/> die übergebene, beides unter derselben Sperre wie TakeSignal. Sonst sähe ein anderer Thread den neuen
    /// Handler mit den alten Flags (Risiko-Review 2, P3-1).
    /// </summary>
    void ExchangeSignalAction(int signal, bool set, Span<ulong> action);

    /// <summary>Anhängige Signale (Bit = Nummer - 1), für rt_sigpending (V23).</summary>
    ulong PendingSignals { get; }
}

/// <summary>
/// Wartender auf einer futex-Adresse oder im Schlaf ohne Adresse (N4-S4). Jeder Thread hat genau einen (<see cref="LinuxThread.FutexWait"/>), angelegt
/// mit dem Thread, weil ein Thread nur an einer Stelle zugleich wartet. Wer weckt, setzt <see cref="Woken"/> unter der Sperre dieses Objekts und weckt
/// nur dieses; der Wartende schläft auf ihm. So weckt FUTEX_WAKE(1) einen Thread statt aller Wartenden des Prozesses.
/// <para>
/// <see cref="Address"/>, <see cref="Bitset"/> und <see cref="Bucket"/> ändern sich nur unter der Sperre des Eimers, in dem der Wartende steht
/// (Requeue hält beide Eimer); <see cref="Woken"/> nur unter Eimer- und eigener Sperre. Sperrreihenfolge: Eimer vor Wartendem.
/// </para>
/// </summary>
internal sealed class LinuxFutexWaiter
{
    public ulong Address;
    public uint Bitset;
    public bool Woken;
    /// <summary>Eimer, in dem der Wartende steht; -1 außerhalb, <see cref="LinuxFutexTable.SleepBucket"/> im Schlaf ohne Adresse.</summary>
    public volatile int Bucket = -1;
}

/// <summary>
/// Wartende auf futex-Adressen eines Prozesses (privat, ein Adressraum). Ein Wartender prüft den Wert unter der Sperre seines Eimers
/// und reiht sich dort ein; wer weckt, ändert den Wert vorher im Benutzerspeicher und nimmt dann dieselbe Eimersperre. So geht kein Wecken
/// zwischen Prüfen und Schlafen verloren (Linux: Hash-Bucket-Sperre).
/// <para>
/// N4-S4: 16 Eimer nach <c>(address &gt;&gt; 2) % 16</c>, je eigene Sperre, damit Mutexe verschiedener Threads auf mehreren CPUs nicht um eine
/// Sperre je Prozess streiten. Geweckt wird nur der Wartende selbst (<see cref="LinuxFutexWaiter"/>), nicht mehr jeder Wartende des Prozesses.
/// </para>
/// <para>
/// Sperrreihenfolge: Eimer (aufsteigend; nur Requeue hält zwei) vor dem Wartenden (<see cref="LinuxFutexWaiter"/>) und vor der Speichersperre
/// des Prozesses (das Lesen des Werts geschieht unter dem Eimer). Der Schlafeimer (<see cref="SleepBucket"/>) wird nie zusammen mit einem
/// Adresseimer gehalten. Der Schlaf geschieht in Scheiben, damit ein Abbruch (exit_group, Zeitlimit der Shell) jeden Wartenden spätestens nach
/// einer Scheibe erreicht, auch wenn ein Wecken verloren ginge.
/// </para>
/// </summary>
internal sealed class LinuxFutexTable
{
    /// <summary>Längster Schlaf am Stück; danach prüft der Wartende Abbruch und Frist.</summary>
    public const int SliceMs = 50;

    /// <summary>Anzahl der Adresseimer; eine Zweierpotenz.</summary>
    public const int BucketCount = 16;

    /// <summary>Index des Eimers für Schlaf ohne Adresse (nanosleep, pause); er erreicht Abort und Kick wie die Adresseimer.</summary>
    public const int SleepBucket = BucketCount;

    private sealed class FutexBucket
    {
        public readonly List<LinuxFutexWaiter> Waiters = new();
    }

    private readonly FutexBucket[] _buckets = new FutexBucket[BucketCount + 1];
    private readonly Func<long> _monotonicNanos;
    private readonly int _sliceMs;
    private bool _aborted;
    private long _wakeups;

    public LinuxFutexTable(Func<long> monotonicNanos, int sliceMs = SliceMs)
    {
        _monotonicNanos = monotonicNanos;
        _sliceMs = sliceMs;
        for (int i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new FutexBucket();
        }
    }

    /// <summary>Eimer einer Adresse (Wörter auf 4 Byte ausgerichtet, deshalb ohne die unteren zwei Bits).</summary>
    public static int BucketOf(ulong address) => (int)((address >> 2) & (BucketCount - 1));

    /// <summary>Anzahl Wartender auf Adressen, ohne Schlafende (Tests).</summary>
    public int WaitingCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                lock (_buckets[i])
                {
                    n += _buckets[i].Waiters.Count;
                }
            }

            return n;
        }
    }

    /// <summary>Wie oft ein Wartender aus Monitor.Wait zurückkam, durch Wecken oder Scheibenende (Tests: Einzelwecken weckt keinen anderen).</summary>
    public long WaitReturns => Interlocked.Read(ref _wakeups);

    /// <summary>
    /// FUTEX_WAIT(_BITSET): schläft, solange der Wert an <paramref name="address"/> <paramref name="expected"/> ist und niemand weckt.
    /// 0 nach Wecken, -EAGAIN bei anderem Wert, -EFAULT, -ETIMEDOUT nach <paramref name="deadlineNanos"/> (monoton, negativ = keine
    /// Frist), -EINTR nach <see cref="Abort"/>. <paramref name="waiter"/> ist das Warteobjekt des aufrufenden Threads (null: ein neues).
    /// <para>
    /// V21, <paramref name="shared"/> (futex ohne FUTEX_PRIVATE_FLAG): Die Tabelle gehört einem Prozess; ein Wecken aus einem anderen Prozess
    /// über MAP_SHARED (nach fork) erreicht sie nicht. Abkürzung (shortcut) statt einer laufweiten Tabelle nach Datei und Versatz: Der
    /// Wartende liest den Wert nach jeder Scheibe neu und kehrt mit 0 zurück, sobald er sich geändert hat (ein frühes Aufwachen erlaubt futex,
    /// Aufrufer prüfen in einer Schleife). Ein Wecken ohne Wertänderung über Prozesse geht verloren; die Verzögerung ist höchstens eine Scheibe.
    /// </para>
    /// </summary>
    public long Wait(IUserMemory memory, ulong address, int expected, uint bitset, long deadlineNanos, bool shared = false,
        LinuxFutexWaiter? waiter = null)
    {
        Span<byte> w = stackalloc byte[4];
        var me = waiter ?? new LinuxFutexWaiter();
        int b = BucketOf(address);
        lock (_buckets[b])
        {
            if (Volatile.Read(ref _aborted))
            {
                return -LinuxSyscalls.EINTR;
            }

            if (!memory.TryRead(address, w))
            {
                return -LinuxSyscalls.EFAULT;
            }

            if (BitConverter.ToInt32(w) != expected)
            {
                return -LinuxSyscalls.EAGAIN;
            }

            // Noch in keinem Eimer: niemand sonst greift auf das Objekt zu, die Felder dürfen ohne seine Sperre zurückgesetzt werden.
            me.Address = address;
            me.Bitset = bitset;
            me.Woken = false;
            me.Bucket = b;
            _buckets[b].Waiters.Add(me);
        }

        long result = -LinuxSyscalls.EINTR;
        try
        {
            result = WaitQueued(memory, address, expected, deadlineNanos, shared, me, w);
        }
        finally
        {
            // S4-P2: Zwischen der letzten Prüfung von Woken und dem Austragen kann ein Wecker den Wartenden noch wählen und zählen. Remove
            // liest Woken unter der Eimersperre, unter der jedes Wecken geschieht; danach steht er in keinem Eimer mehr. Ein gezähltes Wecken
            // liefert deshalb 0 statt -ETIMEDOUT oder -EINTR (Linux: unqueue_me). Bei einer Ausnahme bleibt es beim Austragen.
            if (Remove(me) && result != 0)
            {
                result = 0;
            }
        }

        return result;
    }

    /// <summary>Schlafschleife von <see cref="Wait"/>, nachdem der Wartende eingereiht ist; austragen tut der Aufrufer.</summary>
    private long WaitQueued(IUserMemory memory, ulong address, int expected, long deadlineNanos, bool shared, LinuxFutexWaiter me,
        Span<byte> w)
    {
        while (true)
        {
            lock (me)
            {
                // Abbruch und Signal werden vor dem Kick gesetzt, der Kick nimmt diese Sperre: Ein Prüfen hier vor dem Wait verpasst ihn nicht.
                if (me.Woken)
                {
                    return 0;
                }

                if (Volatile.Read(ref _aborted) || Interrupted())
                {
                    return -LinuxSyscalls.EINTR;
                }

                int slice = _sliceMs;
                if (deadlineNanos >= 0)
                {
                    long left = deadlineNanos - _monotonicNanos();
                    if (left <= 0)
                    {
                        return -LinuxSyscalls.ETIMEDOUT;
                    }

                    slice = (int)Math.Min(_sliceMs, Math.Max(1, left / 1_000_000));
                }

                Monitor.Wait(me, slice);
                Interlocked.Increment(ref _wakeups);
                if (me.Woken)
                {
                    return 0;
                }
            }

            // Außerhalb jeder Sperre: TryRead nimmt die Speichersperre, und ein Wecker hält den Eimer, nicht den Wartenden.
            if (shared && (!memory.TryRead(address, w) || BitConverter.ToInt32(w) != expected))
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Nimmt den Wartenden aus seinem Eimer und liefert, ob er bis dahin geweckt wurde (gelesen unter der Eimersperre). Requeue kann ihn
    /// bis dahin umgehängt haben; deshalb unter der Sperre prüfen, ob er noch in dem gelesenen Eimer steht, sonst den neuen nehmen.
    /// </summary>
    private bool Remove(LinuxFutexWaiter me)
    {
        while (true)
        {
            int b = me.Bucket;
            if (b < 0)
            {
                return me.Woken;
            }

            lock (_buckets[b])
            {
                if (me.Bucket == b)
                {
                    _buckets[b].Waiters.Remove(me);
                    me.Bucket = -1;
                    return me.Woken;
                }
            }
        }
    }

    /// <summary>Weckt genau diesen Wartenden; der Aufrufer hält dessen Eimer.</summary>
    private static void WakeOne(LinuxFutexWaiter x)
    {
        lock (x)
        {
            x.Woken = true;
            Monitor.Pulse(x);
        }
    }

    /// <summary>FUTEX_WAKE(_BITSET): weckt höchstens <paramref name="count"/> Wartende auf der Adresse, in Reihenfolge ihres Wartens.</summary>
    public int Wake(ulong address, int count, uint bitset = uint.MaxValue)
    {
        if (count <= 0)
        {
            return 0;
        }

        var bucket = _buckets[BucketOf(address)];
        lock (bucket)
        {
            int woken = 0;
            foreach (var w in bucket.Waiters)
            {
                if (woken >= count)
                {
                    break;
                }

                if (!w.Woken && w.Address == address && (w.Bitset & bitset) != 0)
                {
                    WakeOne(w);
                    woken++;
                }
            }

            return woken;
        }
    }

    /// <summary>
    /// FUTEX_(CMP_)REQUEUE: weckt bis <paramref name="wake"/> auf <paramref name="from"/> und hängt bis <paramref name="requeue"/>
    /// weitere an <paramref name="to"/>. <paramref name="expected"/> null heißt ohne Vergleich (FUTEX_REQUEUE). Liegen die Adressen in
    /// verschiedenen Eimern, hält der Aufruf beide, in aufsteigender Nummer.
    /// </summary>
    public long Requeue(IUserMemory memory, ulong from, ulong to, int wake, int requeue, int? expected)
    {
        int bf = BucketOf(from), bt = BucketOf(to);
        var first = _buckets[Math.Min(bf, bt)];
        var second = _buckets[Math.Max(bf, bt)];
        lock (first)
        {
            if (bf == bt)
            {
                return RequeueLocked(memory, from, to, wake, requeue, expected, bf, bt);
            }

            lock (second)
            {
                return RequeueLocked(memory, from, to, wake, requeue, expected, bf, bt);
            }
        }
    }

    private long RequeueLocked(IUserMemory memory, ulong from, ulong to, int wake, int requeue, int? expected, int bf, int bt)
    {
        Span<byte> w = stackalloc byte[4];
        if (!memory.TryRead(from, w))
        {
            return -LinuxSyscalls.EFAULT;
        }

        if (expected is int e && BitConverter.ToInt32(w) != e)
        {
            return -LinuxSyscalls.EAGAIN;
        }

        var src = _buckets[bf].Waiters;
        int woken = 0, moved = 0;
        for (int i = 0; i < src.Count;)
        {
            var x = src[i];
            if (x.Woken || x.Address != from)
            {
                i++;
                continue;
            }

            if (woken < wake)
            {
                WakeOne(x);
                woken++;
                i++;
            }
            else if (moved < requeue)
            {
                x.Address = to;
                moved++;
                if (bf == bt)
                {
                    i++;
                }
                else
                {
                    // In den Zieleimer, hinten an (wie Linux); Bucket zuletzt, Remove liest ihn unter der Sperre neu.
                    src.RemoveAt(i);
                    _buckets[bt].Waiters.Add(x);
                    x.Bucket = bt;
                }
            }
            else
            {
                break;
            }
        }

        // Linux zählt bei CMP_REQUEUE geweckte und umgehängte zusammen, bei REQUEUE nur die geweckten.
        return expected.HasValue ? woken + moved : woken;
    }

    /// <summary>
    /// Schlaf ohne Adresse (nanosleep, clock_nanosleep, pause): 0 nach der Frist (monoton, negativ = ohne Frist), -EINTR, sobald
    /// der Prozess endet. So beendet exit_group auch schlafende Threads, nicht erst nach ihrer Frist.
    /// </summary>
    public long Sleep(long deadlineNanos, LinuxFutexWaiter? waiter = null)
    {
        var me = waiter ?? new LinuxFutexWaiter();
        var bucket = _buckets[SleepBucket];
        lock (bucket)
        {
            me.Address = 0;
            me.Bitset = 0;
            me.Woken = false;
            me.Bucket = SleepBucket;
            bucket.Waiters.Add(me);
        }

        try
        {
            lock (me)
            {
                while (!Volatile.Read(ref _aborted) && !Interrupted())
                {
                    int slice = _sliceMs;
                    if (deadlineNanos >= 0)
                    {
                        long left = deadlineNanos - _monotonicNanos();
                        if (left <= 0)
                        {
                            return 0;
                        }

                        slice = (int)Math.Min(_sliceMs, Math.Max(1, left / 1_000_000));
                    }

                    Monitor.Wait(me, slice);
                    Interlocked.Increment(ref _wakeups);
                }

                return -LinuxSyscalls.EINTR;
            }
        }
        finally
        {
            Remove(me);
        }
    }

    /// <summary>
    /// V18: true, solange ein Signal mit Handler wartet (der Prozess setzt es); Wait und Sleep kehren dann mit -EINTR zurück. Nur flüchtige
    /// Felder lesen: Der Aufruf geschieht unter der Sperre des Wartenden, und _threadGate steht in der Reihenfolge darunter.
    /// </summary>
    public Func<bool>? SignalWaiting { get; set; }

    private bool Interrupted() => SignalWaiting?.Invoke() == true;

    /// <summary>Weckt jeden Wartenden aller Eimer, ohne <see cref="LinuxFutexWaiter.Woken"/> zu setzen: Er prüft Abbruch und Signal neu.</summary>
    private void PulseEveryone()
    {
        foreach (var bucket in _buckets)
        {
            lock (bucket)
            {
                foreach (var x in bucket.Waiters)
                {
                    lock (x)
                    {
                        Monitor.Pulse(x);
                    }
                }
            }
        }
    }

    /// <summary>V18: Ein Signal mit Handler ist angekommen: Wartende prüfen sofort, nicht erst nach ihrer Scheibe.</summary>
    public void Kick() => PulseEveryone();

    /// <summary>true nach <see cref="Abort"/> (Strg+C, Zeitlimit, exit_group); lange Kopierschleifen fragen das je Stück ab.</summary>
    public bool IsAborted => Volatile.Read(ref _aborted);

    /// <summary>
    /// Der Prozess endet: jeder Wartende kehrt mit -EINTR zurück, neue Wartende auch. Das Flag steht vor dem Durchgang über die Eimer: Wer sich
    /// danach einreiht, sieht es unter der Eimersperre; wer schon steht, bekommt den Puls oder sieht es unter seiner eigenen Sperre vor dem Wait.
    /// </summary>
    public void Abort()
    {
        Volatile.Write(ref _aborted, true);
        PulseEveryone();
    }
}
