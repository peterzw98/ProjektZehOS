using System;
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

    /// <summary>Prüft die Optionen von wait4 (Linux kernel/exit.c): erlaubt WNOHANG, WUNTRACED, WCONTINUED und die __W-Bits.</summary>
    public static bool ValidWait4Options(int options) => (options & ~(WNohang | WUntraced | WContinued | WNothread | WAll | WClone)) == 0;

    /// <summary>Prüft die Optionen von waitid: dieselben plus WEXITED und WNOWAIT, mindestens eins von WEXITED/WSTOPPED/WCONTINUED.</summary>
    public static bool ValidWaitidOptions(int options) =>
        (options & ~(WNohang | WStopped | WExited | WContinued | WNowait | WNothread | WAll | WClone)) == 0
        && (options & (WExited | WStopped | WContinued)) != 0;
}
