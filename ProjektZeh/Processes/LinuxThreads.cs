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

    /// <summary>Gesetzt, sobald der Thread mit exit geendet hat (oder der Prozess mit exit_group).</summary>
    public bool Exited { get; set; }

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

    /// <summary>Anhängige Signale (Bit = Nummer - 1), für rt_sigpending (V23).</summary>
    ulong PendingSignals { get; }
}

/// <summary>
/// Wartende auf futex-Adressen eines Prozesses (privat, ein Adressraum). Ein Wartender prüft den Wert unter der Sperre der Tabelle
/// und schläft auf ihr; wer weckt, ändert den Wert vorher im Benutzerspeicher und nimmt dann dieselbe Sperre. So geht kein Wecken
/// zwischen Prüfen und Schlafen verloren (Linux: Hash-Bucket-Sperre).
/// <para>
/// Sperrreihenfolge: <see cref="_gate"/> vor der Speichersperre des Prozesses (das Lesen des Werts geschieht unter <see cref="_gate"/>).
/// Der Schlaf geschieht in Scheiben, damit ein Abbruch (exit_group, Zeitlimit der Shell) jeden Wartenden spätestens nach einer
/// Scheibe erreicht, auch wenn ein Wecken verloren ginge.
/// </para>
/// </summary>
internal sealed class LinuxFutexTable
{
    /// <summary>Längster Schlaf am Stück; danach prüft der Wartende Abbruch und Frist.</summary>
    public const int SliceMs = 50;

    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = new();
    private readonly Func<long> _monotonicNanos;
    private bool _aborted;

    private sealed class Waiter
    {
        public ulong Address;
        public uint Bitset;
        public bool Woken;
    }

    public LinuxFutexTable(Func<long> monotonicNanos) => _monotonicNanos = monotonicNanos;

    /// <summary>Anzahl Wartender (Tests).</summary>
    public int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>
    /// FUTEX_WAIT(_BITSET): schläft, solange der Wert an <paramref name="address"/> <paramref name="expected"/> ist und niemand weckt.
    /// 0 nach Wecken, -EAGAIN bei anderem Wert, -EFAULT, -ETIMEDOUT nach <paramref name="deadlineNanos"/> (monoton, negativ = keine
    /// Frist), -EINTR nach <see cref="Abort"/>.
    /// <para>
    /// V21, <paramref name="shared"/> (futex ohne FUTEX_PRIVATE_FLAG): Die Tabelle gehört einem Prozess; ein Wecken aus einem anderen Prozess
    /// über MAP_SHARED (nach fork) erreicht sie nicht. Abkürzung (shortcut) statt einer laufweiten Tabelle nach Datei und Versatz: Der
    /// Wartende liest den Wert nach jeder Scheibe neu und kehrt mit 0 zurück, sobald er sich geändert hat (ein frühes Aufwachen erlaubt futex,
    /// Aufrufer prüfen in einer Schleife). Ein Wecken ohne Wertänderung über Prozesse geht verloren; die Verzögerung ist höchstens eine Scheibe.
    /// </para>
    /// </summary>
    public long Wait(IUserMemory memory, ulong address, int expected, uint bitset, long deadlineNanos, bool shared = false)
    {
        Span<byte> w = stackalloc byte[4];
        var me = new Waiter { Address = address, Bitset = bitset };
        lock (_gate)
        {
            if (_aborted)
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

            _waiters.Add(me);
            try
            {
                while (!me.Woken)
                {
                    if (_aborted || Interrupted())
                    {
                        return -LinuxSyscalls.EINTR;
                    }

                    int slice = SliceMs;
                    if (deadlineNanos >= 0)
                    {
                        long left = deadlineNanos - _monotonicNanos();
                        if (left <= 0)
                        {
                            return -LinuxSyscalls.ETIMEDOUT;
                        }

                        slice = (int)Math.Min(SliceMs, Math.Max(1, left / 1_000_000));
                    }

                    Monitor.Wait(_gate, slice);
                    if (shared && !me.Woken && (!memory.TryRead(address, w) || BitConverter.ToInt32(w) != expected))
                    {
                        return 0;
                    }
                }

                return 0;
            }
            finally
            {
                _waiters.Remove(me);
            }
        }
    }

    /// <summary>FUTEX_WAKE(_BITSET): weckt höchstens <paramref name="count"/> Wartende auf der Adresse, in Reihenfolge ihres Wartens.</summary>
    public int Wake(ulong address, int count, uint bitset = uint.MaxValue)
    {
        if (count <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            int woken = 0;
            foreach (var w in _waiters)
            {
                if (woken >= count)
                {
                    break;
                }

                if (!w.Woken && w.Address == address && (w.Bitset & bitset) != 0)
                {
                    w.Woken = true;
                    woken++;
                }
            }

            if (woken > 0)
            {
                Monitor.PulseAll(_gate);
            }

            return woken;
        }
    }

    /// <summary>
    /// FUTEX_(CMP_)REQUEUE: weckt bis <paramref name="wake"/> auf <paramref name="from"/> und hängt bis <paramref name="requeue"/>
    /// weitere an <paramref name="to"/>. <paramref name="expected"/> null heißt ohne Vergleich (FUTEX_REQUEUE).
    /// </summary>
    public long Requeue(IUserMemory memory, ulong from, ulong to, int wake, int requeue, int? expected)
    {
        Span<byte> w = stackalloc byte[4];
        lock (_gate)
        {
            if (!memory.TryRead(from, w))
            {
                return -LinuxSyscalls.EFAULT;
            }

            if (expected is int e && BitConverter.ToInt32(w) != e)
            {
                return -LinuxSyscalls.EAGAIN;
            }

            int woken = 0, moved = 0;
            foreach (var x in _waiters)
            {
                if (x.Woken || x.Address != from)
                {
                    continue;
                }

                if (woken < wake)
                {
                    x.Woken = true;
                    woken++;
                }
                else if (moved < requeue)
                {
                    x.Address = to;
                    moved++;
                }
                else
                {
                    break;
                }
            }

            if (woken > 0)
            {
                Monitor.PulseAll(_gate);
            }

            // Linux zählt bei CMP_REQUEUE geweckte und umgehängte zusammen, bei REQUEUE nur die geweckten.
            return expected.HasValue ? woken + moved : woken;
        }
    }

    /// <summary>
    /// Schlaf ohne Adresse (nanosleep, clock_nanosleep, pause): 0 nach der Frist (monoton, negativ = ohne Frist), -EINTR, sobald
    /// der Prozess endet. So beendet exit_group auch schlafende Threads, nicht erst nach ihrer Frist.
    /// </summary>
    public long Sleep(long deadlineNanos)
    {
        lock (_gate)
        {
            while (!_aborted && !Interrupted())
            {
                int slice = SliceMs;
                if (deadlineNanos >= 0)
                {
                    long left = deadlineNanos - _monotonicNanos();
                    if (left <= 0)
                    {
                        return 0;
                    }

                    slice = (int)Math.Min(SliceMs, Math.Max(1, left / 1_000_000));
                }

                Monitor.Wait(_gate, slice);
            }

            return -LinuxSyscalls.EINTR;
        }
    }

    /// <summary>
    /// V18: true, solange ein Signal mit Handler wartet (der Prozess setzt es); Wait und Sleep kehren dann mit -EINTR zurück. Nur flüchtige
    /// Felder lesen: Der Aufruf geschieht unter der Sperre der Tabelle, und _threadGate steht in der Reihenfolge darunter.
    /// </summary>
    public Func<bool>? SignalWaiting { get; set; }

    private bool Interrupted() => SignalWaiting?.Invoke() == true;

    /// <summary>V18: Ein Signal mit Handler ist angekommen: Wartende prüfen sofort, nicht erst nach ihrer Scheibe.</summary>
    public void Kick()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>true nach <see cref="Abort"/> (Strg+C, Zeitlimit, exit_group); lange Kopierschleifen fragen das je Stück ab.</summary>
    public bool IsAborted => Volatile.Read(ref _aborted);

    /// <summary>Der Prozess endet: jeder Wartende kehrt mit -EINTR zurück, neue Wartende auch.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            _aborted = true;
            Monitor.PulseAll(_gate);
        }
    }
}
