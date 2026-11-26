using System;
using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes;

/// <summary>Kodierung des Status für wait4 wie Linux (<c>WEXITSTATUS</c>, <c>WTERMSIG</c>, <c>WCOREDUMP</c>).</summary>
internal static class LinuxWaitStatus
{
    /// <summary>Normales Ende mit <paramref name="code"/> (nur die unteren 8 Bit, wie Linux).</summary>
    public static int Exited(int code) => (code & 0xff) << 8;

    /// <summary>Ende durch Signal <paramref name="signal"/>; <paramref name="core"/> setzt das Bit 0x80 (Speicherabzug).</summary>
    public static int Signaled(int signal, bool core = false) => (signal & 0x7f) | (core ? 0x80 : 0);

    public static bool IsExited(int status) => (status & 0x7f) == 0;

    public static int ExitCode(int status) => (status >> 8) & 0xff;

    public static int TermSignal(int status) => status & 0x7f;

    public static bool CoreDumped(int status) => (status & 0x80) != 0;
}

/// <summary>Optional neben <see cref="ILinuxProcess"/> (V15): Kinder abholen (wait4, waitid) und getppid. Ohne sie: -ECHILD und 0.</summary>
internal interface ILinuxWait
{
    /// <summary>PID des Elternprozesses; 0 für den ersten Prozess eines Laufs (Elternteil ist der Kernel).</summary>
    int ParentPid { get; }

    /// <summary>PID des ersten Prozesses des Laufs; alle Prozesse stehen in seiner Prozessgruppe (setpgid gibt es nicht).</summary>
    int GroupPid { get; }

    /// <summary>
    /// Wartet auf ein beendetes Kind. <paramref name="idType"/> 0 = jedes, 1 = PID <paramref name="id"/>, 2 = Prozessgruppe <paramref name="id"/>.
    /// Liefert die PID, 0 bei WNOHANG ohne fertiges Kind, -ECHILD ohne passendes Kind, -EINTR nach Abbruch des Aufrufers.
    /// </summary>
    /// <param name="consume">false bei WNOWAIT: der Zombie bleibt stehen.</param>
    long WaitChild(int idType, int id, int options, bool consume, Func<bool>? aborted, out int status, out ulong uid);

    /// <summary>
    /// kill(pid, sig) im Lauf: <paramref name="pid"/> &gt; 0 ein Prozess, 0 oder -Gruppe alle des Laufs, -1 alle außer dem Aufrufer und dem
    /// ersten Prozess. Liefert 0, -EINVAL (Signalnummer), -ESRCH (kein Ziel) oder -ENOSYS (Handler, die der Kernel nicht anspringen kann).
    /// </summary>
    long KillProcess(int pid, int signal);
}

/// <summary>
/// V15: Warten auf Kinder über <see cref="Children"/> unter <see cref="ChildGate"/> (Struktur aus LinuxSpawn.cs). Ein Eintrag mit
/// <see cref="LinuxChild.WaitStatus"/> ist ein Zombie, bis wait4 ihn austrägt.
/// <para>
/// Abweichungen von Linux: Waisen gehen nicht an init (PID 1 ist hier der erste Prozess des Laufs, etwa der Browser, kein init, das
/// fremde Kinder abholt); getppid eines Waisen liefert weiter die PID des toten Elternprozesses. SIGCHLD wird nicht zugestellt (nur
/// Warten); gestoppte und fortgesetzte Kinder gibt es nicht, WUNTRACED/WCONTINUED warten nur auf das Ende.
/// </para>
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxWait
{
    /// <summary>Längster Schlaf eines Wartenden am Stück; danach prüft er den Abbruch (wie die futex-Tabelle).</summary>
    public const int WaitSliceMs = 50;

    public const int WNohang = 1, WUntraced = 2, WStopped = 2, WExited = 4, WContinued = 8, WNowait = 0x0100_0000;
    public const int WNothread = 0x2000_0000, WAll = 0x4000_0000, WClone = unchecked((int)0x8000_0000);

    private const int SigChld = 17;
    private const ulong SigIgnHandler = 1, SaNoCldWait = 2;
    private const long EChild = 10;

    public int ParentPid => Parent?.Pid ?? 0;

    public int GroupPid
    {
        get
        {
            LinuxUserProcess root = this;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            return root.Pid;
        }
    }

    public long WaitChild(int idType, int id, int options, bool consume, Func<bool>? aborted, out int status, out ulong uid)
    {
        status = 0;
        uid = 0;
        int group = GroupPid;
        lock (_children)
        {
            while (true)
            {
                bool any = false;
                for (int i = 0; i < _children.Count; i++)
                {
                    LinuxChild c = _children[i];
                    if (c.AutoReaped || (idType == 1 && c.Pid != id) || (idType == 2 && id != group))
                    {
                        continue;
                    }

                    any = true;
                    if (c.WaitStatus is int s)
                    {
                        status = s;
                        uid = c.Process.Uid;
                        if (consume)
                        {
                            _ = Reap(c.Pid); // trägt aus und gibt die PID frei (LinuxSpawn.cs)
                        }

                        return c.Pid;
                    }
                }

                if (!any)
                {
                    return -EChild;
                }

                if ((options & WNohang) != 0)
                {
                    return 0;
                }

                if (aborted != null && aborted())
                {
                    return -LinuxSyscalls.EINTR;
                }

                Monitor.Wait(_children, WaitSliceMs);
            }
        }
    }

    /// <summary>
    /// Linux (autoreap): Ignoriert der Elternprozess SIGCHLD (SIG_IGN) oder setzt SA_NOCLDWAIT, bleibt kein Zombie. Handler und Flags schreibt
    /// rt_sigaction ohne ChildGate; ein Wort zu lesen ist atomar, und Linux entscheidet ebenso nach dem Zustand im Augenblick des Endes.
    /// </summary>
    private bool ReapsChildrenAutomatically() =>
        Signals.Handler[SigChld] == SigIgnHandler || (Signals.Flags[SigChld] & SaNoCldWait) != 0;

    /// <summary>
    /// Kernel (V15 kill): Ein anderer Prozess hat diesen beendet. Anders als <see cref="OnExit"/> gehört der aufrufende Thread nicht zu diesem
    /// Prozess und darf den Ring 3 nicht verlassen; die Brücke schickt nur die Threads dieses Prozesses hinaus.
    /// </summary>
    public Action<int>? OnKilled { get; set; }

    // Unter _threadGate: Signal aus kill, das während vfork ankam (das Kind läuft auf dem Kernelthread des Elternprozesses und lässt sich
    // von außen nicht aus dem Ring 3 holen); execve stellt es dem neuen Abbild zu. Nach execve zeigt _execSuccessor auf das neue Abbild.
    private int _killInVfork;
    private LinuxUserProcess? _execSuccessor;

    public long KillProcess(int pid, int signal)
    {
        if ((uint)signal > 64)
        {
            return -LinuxSyscalls.EINVAL;
        }

        // Linux: -INT_MIN lässt sich nicht verneinen (kill_something_info meldet ESRCH).
        if (pid == int.MinValue)
        {
            return -LinuxSyscalls.ESRCH;
        }

        LinuxUserProcess root = this;
        while (root.Parent != null)
        {
            root = root.Parent;
        }

        if (pid > 0)
        {
            return Find(root, pid, out LinuxUserProcess? target, out bool zombie)
                ? zombie ? 0 : SignalProcess(target!, signal) // Linux: kill auf einen Zombie gelingt ohne Wirkung
                : -LinuxSyscalls.ESRCH;
        }

        // Prozessgruppen gibt es nicht (setpgid -EPERM): Gruppe 0 und -GroupPid sind der ganze Lauf, eine andere Gruppe ist leer.
        if (pid < -1 && -pid != root.Pid)
        {
            return -LinuxSyscalls.ESRCH;
        }

        var all = new List<LinuxUserProcess>();
        Collect(root, all);
        long result = -LinuxSyscalls.ESRCH;
        bool self = false;
        foreach (LinuxUserProcess target in all)
        {
            if (ReferenceEquals(target, this))
            {
                self = pid != -1;
                continue;
            }

            if (pid == -1 && ReferenceEquals(target, root))
            {
                continue; // wie init im Namensraum
            }

            long r = SignalProcess(target, signal);
            result = result == -LinuxSyscalls.ESRCH || r == 0 ? r : result;
        }

        // Der Aufrufer zuletzt: Beendet ihn das Signal, sind die anderen schon erreicht.
        if (self)
        {
            long r = SignalProcess(this, signal);
            result = result == -LinuxSyscalls.ESRCH || r == 0 ? r : result;
        }

        return result;
    }

    private long SignalProcess(LinuxUserProcess target, int signal)
    {
        if (ReferenceEquals(target, this))
        {
            return DeliverSignal(signal, fromOutside: false);
        }

        while (true)
        {
            lock (target._threadGate)
            {
                if (target._execSuccessor != null)
                {
                    target = target._execSuccessor;
                    continue;
                }

                if (target.InVfork)
                {
                    // Nur Signale merken, die beenden würden; Handler und blockierte Signale entscheidet execve beim neuen Abbild.
                    if (signal != 0 && target._killInVfork == 0)
                    {
                        target._killInVfork = signal;
                    }

                    return 0;
                }
            }

            return target.DeliverSignal(signal, fromOutside: true);
        }
    }

    /// <summary>Beendet diesen Prozess von außen (kill eines anderen Prozesses).</summary>
    private void Kill(int signal)
    {
        lock (_threadGate)
        {
            // Endet der Prozess schon (exit_group, Absturz), bleibt sein Status; seine Threads verlassen den Ring 3 ohnehin.
            if (ExitCode != null)
            {
                return;
            }

            ExitCode = 128 + signal;
            TerminatedBySignal = signal;
        }

        Futexes.Abort();
        OnKilled?.Invoke(128 + signal);
    }

    /// <summary>execve, Punkt ohne Rückweg: Spätere kill-Aufrufe treffen <paramref name="next"/>; ein Signal aus der vfork-Zeit bekommt es nach dem Start.</summary>
    private int HandOverKill(LinuxUserProcess next)
    {
        lock (_threadGate)
        {
            _execSuccessor = next;
            int signal = _killInVfork;
            _killInVfork = 0;
            return signal;
        }
    }

    /// <summary>Sucht <paramref name="pid"/> im Baum ab <paramref name="p"/>; ein selbst geernteter Prozess (SIG_IGN) ist schon weg.</summary>
    private static bool Find(LinuxUserProcess p, int pid, out LinuxUserProcess? found, out bool zombie)
    {
        found = null;
        zombie = false;
        if (p.Pid == pid)
        {
            found = p;
            return true;
        }

        foreach (LinuxChild c in p.Children)
        {
            if (c.Pid == pid)
            {
                if (c.WaitStatus != null && c.AutoReaped)
                {
                    return false;
                }

                found = c.Process;
                zombie = c.WaitStatus != null;
                return true;
            }

            if (c.WaitStatus == null && Find(c.Process, pid, out found, out zombie))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Alle laufenden Prozesse unter <paramref name="p"/> (Schnappschüsse der Kinderlisten, keine Sperre gleichzeitig).</summary>
    private static void Collect(LinuxUserProcess p, List<LinuxUserProcess> into)
    {
        into.Add(p);
        foreach (LinuxChild c in p.Children)
        {
            if (c.WaitStatus == null)
            {
                Collect(c.Process, into);
            }
        }
    }

    /// <summary>Prüft die Optionen von wait4 (Linux kernel/exit.c): erlaubt WNOHANG, WUNTRACED, WCONTINUED und die __W-Bits.</summary>
    public static bool ValidWait4Options(int options) => (options & ~(WNohang | WUntraced | WContinued | WNothread | WAll | WClone)) == 0;

    /// <summary>Prüft die Optionen von waitid: dieselben plus WEXITED und WNOWAIT, mindestens eins von WEXITED/WSTOPPED/WCONTINUED.</summary>
    public static bool ValidWaitidOptions(int options) =>
        (options & ~(WNohang | WStopped | WExited | WContinued | WNowait | WNothread | WAll | WClone)) == 0
        && (options & (WExited | WStopped | WContinued)) != 0;
}
