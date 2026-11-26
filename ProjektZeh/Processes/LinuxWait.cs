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

    // Unter _threadGate: Signale aus kill, die während vfork ankamen (das Kind läuft auf dem Kernelthread des Elternprozesses und lässt sich
    // von außen nicht aus dem Ring 3 holen), als Maske wie Linux' anhängige Signale (Bit signal-1); execve stellt sie dem neuen Abbild zu.
    // Eine Maske statt eines Werts, damit ein späteres SIGKILL nicht hinter einem früheren, folgenlosen Signal verloren geht.
    // Nach execve zeigt _execSuccessor auf das neue Abbild.
    private ulong _killsInVfork;
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
                    // Handler und blockierte Signale entscheidet das neue Abbild nach execve.
                    if (signal != 0)
                    {
                        target._killsInVfork |= 1UL << (signal - 1);
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
    private ulong HandOverKill(LinuxUserProcess next)
    {
        lock (_threadGate)
        {
            _execSuccessor = next;
            ulong signals = _killsInVfork;
            _killsInVfork = 0;
            return signals;
        }
    }

    /// <summary>
    /// Stellt die während vfork gemerkten Signale dem neuen Abbild zu. SIGKILL und SIGSTOP zuerst (sie beenden immer, LinuxUserProcess.cs),
    /// danach die übrigen aufsteigend; ein Signal nach dem Ende ist wirkungslos, der Exitstatus bleibt.
    /// </summary>
    private static void DeliverKillsFromVfork(LinuxUserProcess next, ulong signals)
    {
        const ulong Ending = (1UL << (9 - 1)) | (1UL << (19 - 1));
        if ((signals & Ending) != 0)
        {
            _ = next.DeliverSignal((signals & (1UL << (9 - 1))) != 0 ? 9 : 19, fromOutside: true);
            return;
        }

        for (int signal = 1; signal <= 64; signal++)
        {
            if ((signals & (1UL << (signal - 1))) != 0)
            {
                _ = next.DeliverSignal(signal, fromOutside: true);
            }
        }
    }

    /// <summary>
    /// Sucht <paramref name="pid"/> im Baum ab <paramref name="p"/> und unter den toten Zwischenprozessen, die der erste Prozess für Waisen
    /// aufhebt (<see cref="OrphanHolders"/>); ein selbst geernteter Prozess (SIG_IGN) ist schon weg.
    /// </summary>
    private static bool Find(LinuxUserProcess p, int pid, out LinuxUserProcess? found, out bool zombie)
    {
        found = null;
        zombie = false;
        if (p.Pid == pid)
        {
            found = p;
            return true;
        }

        if (FindBelow(p, pid, out found, out zombie))
        {
            return true;
        }

        foreach (LinuxUserProcess holder in p.OrphanHolders)
        {
            // Nur unterhalb suchen: Die PID des toten Zwischenprozesses selbst ist frei und kann schon einem anderen gehören.
            if (FindBelow(holder, pid, out found, out zombie))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FindBelow(LinuxUserProcess p, int pid, out LinuxUserProcess? found, out bool zombie)
    {
        found = null;
        zombie = false;
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

            // Auch unter beendeten Einträgen weitersuchen: Die noch laufenden Kinder eines toten Zwischenprozesses bleiben in seiner Liste
            // (FreeIds räumt nur beendete aus); Linux hängt sie an init, hier bleiben sie im Baum erreichbar.
            if (FindBelow(c.Process, pid, out found, out zombie))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Alle laufenden Prozesse unter <paramref name="p"/>, auch die Waisen toter Zwischenprozesse (Schnappschüsse der Kinderlisten,
    /// keine Sperre gleichzeitig). Zombies selbst sind kein Ziel, ihre noch laufenden Kinder schon.
    /// </summary>
    private static void Collect(LinuxUserProcess p, List<LinuxUserProcess> into)
    {
        into.Add(p);
        CollectBelow(p, into);
        foreach (LinuxUserProcess holder in p.OrphanHolders)
        {
            CollectBelow(holder, into);
        }
    }

    private static void CollectBelow(LinuxUserProcess p, List<LinuxUserProcess> into)
    {
        foreach (LinuxChild c in p.Children)
        {
            if (c.WaitStatus == null)
            {
                into.Add(c.Process);
            }

            CollectBelow(c.Process, into);
        }
    }

    /// <summary>Prüft die Optionen von wait4 (Linux kernel/exit.c): erlaubt WNOHANG, WUNTRACED, WCONTINUED und die __W-Bits.</summary>
    public static bool ValidWait4Options(int options) => (options & ~(WNohang | WUntraced | WContinued | WNothread | WAll | WClone)) == 0;

    /// <summary>Prüft die Optionen von waitid: dieselben plus WEXITED und WNOWAIT, mindestens eins von WEXITED/WSTOPPED/WCONTINUED.</summary>
    public static bool ValidWaitidOptions(int options) =>
        (options & ~(WNohang | WStopped | WExited | WContinued | WNowait | WNothread | WAll | WClone)) == 0
        && (options & (WExited | WStopped | WContinued)) != 0;
}
