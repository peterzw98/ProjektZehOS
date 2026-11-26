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

/// <summary>
/// Optional neben <see cref="ILinuxProcess"/> (V15): der Prozess gehört zu einer <see cref="LinuxProcessFamily"/> und kann Kinder haben
/// (wait4, waitid, getppid). Ohne sie gibt es keine Kinder: wait4 -ECHILD, getppid 0.
/// </summary>
internal interface ILinuxFamilyMember
{
    LinuxProcessFamily Family { get; }
}

/// <summary>
/// Optional neben <see cref="ILinuxFiles"/> (V15): das Flag <c>FD_CLOEXEC</c> je Nummer. Es gehört zur Nummer, nicht zur offenen Datei
/// (dup löscht es, wie Linux); execve schließt die markierten Nummern.
/// </summary>
internal interface ILinuxFdFlags
{
    bool GetCloseOnExec(int fd);

    void SetCloseOnExec(int fd, bool on);
}

/// <summary>
/// Alle Linux-Prozesse eines Laufs (V15, posix_spawn): PID zu Prozess, Eltern und Kinder, Zombies bis wait4, und die eine Dateisperre,
/// die die Prozesse teilen. Plattformunabhängig und auf dem Host geprüft.
/// <para>
/// Warum eine Dateisperre für alle: Nach vfork und execve zeigen Kind und Eltern auf dieselben offenen Dateien und Pipe-Enden (wie Linux).
/// Die Dateien und <c>LinuxFileSystem</c> (Arbeitslisten im Objekt) setzen voraus, dass nur einer zugleich darin arbeitet; mit einer Sperre je
/// Prozess könnten zwei Prozesse dieselbe Datei gleichzeitig lesen oder schließen. Eine grobe Sperre reicht wie bei den Threads (V11):
/// Dateiaufrufe warten darin nur auf die Platte.
/// </para>
/// <para>
/// Sperrreihenfolge: <see cref="FileGate"/> vor <see cref="_gate"/> (die Prüfung, ob eine Datei noch eine Nummer hat, läuft unter beiden).
/// Unter <see cref="_gate"/> wird nur gezählt, eingetragen und gewartet: keine Datei, kein Speicher, keine Ausgabe, keine weitere Sperre.
/// Wartende schlafen auf <see cref="_gate"/> in Scheiben wie die futex-Tabelle, damit ein Abbruch sie auch ohne Wecken erreicht.
/// </para>
/// </summary>
internal sealed class LinuxProcessFamily
{
    /// <summary>Längster Schlaf eines Wartenden am Stück; danach prüft er den Abbruch.</summary>
    public const int SliceMs = 50;

    public const int WNohang = 1, WUntraced = 2, WStopped = 2, WExited = 4, WContinued = 8, WNowait = 0x0100_0000;
    public const int WNothread = 0x2000_0000, WAll = 0x4000_0000, WClone = unchecked((int)0x8000_0000);

    private const long ECHILD = 10, EINTR = 4, EINVAL = 22;
    private const int SigChld = 17;
    private const ulong SigIgn = 1;
    private const ulong SaNoCldWait = 2;

    private readonly object _gate = new();
    private readonly Dictionary<int, Member> _members = new();
    private int _rootPid;

    private sealed class Member
    {
        public int Pid;
        public int ParentPid;

        /// <summary>Lebt der Prozess, sein Objekt (für Deskriptoren und Signalzustand); null ab dem Ende.</summary>
        public ILinuxProcess? Process;

        /// <summary>Status nach <see cref="ReportExit"/>; bis wait4 ist der Prozess ein Zombie.</summary>
        public int? Status;
        public ulong Uid;
    }

    /// <summary>Die gemeinsame Dateisperre aller Prozesse der Familie (siehe Klassenkommentar).</summary>
    public object FileGate { get; } = new();

    /// <summary>PID des ersten Prozesses; sein Elternteil ist 0 (der Kernel), wie bei init.</summary>
    public int RootPid => _rootPid;

    /// <summary>Trägt den ersten Prozess eines Laufs ein (ohne Elternteil). false, wenn die PID schon vergeben ist.</summary>
    public bool AddRoot(int pid, ILinuxProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            if (_members.ContainsKey(pid))
            {
                return false;
            }

            _members.Add(pid, new Member { Pid = pid, ParentPid = 0, Process = process, Uid = process.Uid });
            if (_rootPid == 0)
            {
                _rootPid = pid;
            }

            return true;
        }
    }

    /// <summary>
    /// Trägt ein Kind von <paramref name="parentPid"/> ein (clone ohne CLONE_THREAD, vor seinem ersten Befehl). false, wenn die PID belegt ist
    /// (auch durch einen Zombie) oder der Elternteil nicht lebt.
    /// </summary>
    public bool AddChild(int parentPid, int childPid, ILinuxProcess child)
    {
        ArgumentNullException.ThrowIfNull(child);
        lock (_gate)
        {
            if (_members.ContainsKey(childPid) || !_members.TryGetValue(parentPid, out Member? parent) || parent.Process is null)
            {
                return false;
            }

            _members.Add(childPid, new Member { Pid = childPid, ParentPid = parentPid, Process = child, Uid = child.Uid });
            return true;
        }
    }

    /// <summary>true, solange die PID vergeben ist (lebend oder Zombie); für die PID- und TID-Vergabe.</summary>
    public bool Contains(int pid)
    {
        lock (_gate)
        {
            return _members.ContainsKey(pid);
        }
    }

    /// <summary>getppid: PID des Elternteils; 0 für den ersten Prozess und für Waisen (siehe <see cref="ReportExit"/>).</summary>
    public int ParentOf(int pid)
    {
        lock (_gate)
        {
            return _members.TryGetValue(pid, out Member? m) ? m.ParentPid : 0;
        }
    }

    /// <summary>Lebende Prozesse (für Tests und die Suche nach Nummern auf eine Datei).</summary>
    public int LiveCount
    {
        get
        {
            lock (_gate)
            {
                int n = 0;
                foreach (var m in _members.Values)
                {
                    if (m.Process != null)
                    {
                        n++;
                    }
                }

                return n;
            }
        }
    }

    /// <summary>Vergebene PIDs einschließlich Zombies (Tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _members.Count;
            }
        }
    }

    /// <summary>
    /// true, wenn ein anderer lebender Prozess der Familie als <paramref name="self"/> eine Nummer auf <paramref name="d"/> hat. Der Aufrufer hält
    /// <see cref="FileGate"/>, sonst könnte zwischen Prüfen und Schließen eine Nummer dazukommen. Das Terminal zählt nicht (es gehört dem Prozess).
    /// </summary>
    public bool ReferencedByOthers(ILinuxDescriptor d, ILinuxProcess self)
    {
        lock (_gate)
        {
            foreach (var m in _members.Values)
            {
                ILinuxProcess? other = m.Process;
                if (other is null || ReferenceEquals(other, self))
                {
                    continue;
                }

                for (int fd = 0; fd < LinuxUserProcess.MaxDescriptors; fd++)
                {
                    if (ReferenceEquals(other.GetDescriptor(fd), d))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Der Prozess <paramref name="pid"/> ist zu Ende (letzter Thread, exit_group oder Signal), <paramref name="status"/> nach
    /// <see cref="LinuxWaitStatus"/>. Er bleibt Zombie, bis der Elternteil wartet; ignoriert der Elternteil SIGCHLD (SIG_IGN oder SA_NOCLDWAIT)
    /// oder gibt es keinen, verschwindet er sofort (Linux: autoreap). Seine Kinder werden Waisen mit Elternteil 0; ihre Zombies verschwinden.
    /// <para>
    /// Abweichung von Linux: Waisen gehen nicht an init (PID 1). PID 1 ist hier der erste Linux-Prozess des Laufs (etwa der Browser), kein init,
    /// das fremde Kinder abholt; wait4(-1) würde ihm sonst Enkel liefern. SIGCHLD an den Elternteil wird nicht zugestellt (V15: nur Warten).
    /// </para>
    /// Ruft der Prozess vorher seine Dateien zu (CloseAllFiles), damit die Nummern des Toten nicht mehr zählen; spätestens hier zählen sie nicht mehr.
    /// </summary>
    public void ReportExit(int pid, int status)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(pid, out Member? me) || me.Process is null)
            {
                return; // doppelt gemeldet (exit_group und letzter Thread): der erste Status gilt
            }

            me.Process = null;
            me.Status = status;

            // Kinder des Toten: Zombies verschwinden, lebende werden Waisen.
            var gone = new List<int>();
            foreach (var m in _members.Values)
            {
                if (m.ParentPid != pid)
                {
                    continue;
                }

                if (m.Process is null)
                {
                    gone.Add(m.Pid);
                }
                else
                {
                    m.ParentPid = 0;
                }
            }

            foreach (int g in gone)
            {
                _members.Remove(g);
            }

            if (!ParentWaitsFor(me))
            {
                _members.Remove(pid);
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// wait4 und waitid ohne Benutzerspeicher. <paramref name="idType"/> 0 = jedes Kind, 1 = PID <paramref name="id"/>, 2 = Prozessgruppe
    /// <paramref name="id"/> (alle Prozesse der Familie sind in der Gruppe des ersten, setpgid scheitert immer). <paramref name="options"/> sind
    /// die WNOHANG/WEXITED/WNOWAIT-Bits; gestoppte und fortgesetzte Kinder gibt es nicht (kein SIGSTOP), WUNTRACED/WCONTINUED warten daher nur
    /// auf das Ende. Liefert die PID des Kindes, 0 bei WNOHANG ohne fertiges Kind, -ECHILD ohne passendes Kind, -EINTR nach Abbruch
    /// des Aufrufers.
    /// </summary>
    /// <param name="consume">false bei WNOWAIT: der Zombie bleibt stehen.</param>
    public long Wait(int callerPid, int idType, int id, int options, bool consume, Func<bool>? aborted, out int status, out ulong uid)
    {
        status = 0;
        uid = 0;
        lock (_gate)
        {
            while (true)
            {
                bool any = false;
                foreach (var m in _members.Values)
                {
                    if (m.ParentPid != callerPid || m.Pid == callerPid || !Matches(m, idType, id))
                    {
                        continue;
                    }

                    any = true;
                    if (m.Status is int s)
                    {
                        status = s;
                        uid = m.Uid;
                        int pid = m.Pid;
                        if (consume)
                        {
                            _members.Remove(pid);
                        }

                        return pid;
                    }
                }

                if (!any)
                {
                    return -ECHILD;
                }

                if ((options & WNohang) != 0)
                {
                    return 0;
                }

                if (aborted != null && aborted())
                {
                    return -EINTR;
                }

                Monitor.Wait(_gate, SliceMs);
            }
        }
    }

    /// <summary>Prüft die Optionen von wait4 (Linux kernel/exit.c): erlaubt WNOHANG, WUNTRACED, WCONTINUED und die __W-Bits.</summary>
    public static bool ValidWait4Options(int options) => (options & ~(WNohang | WUntraced | WContinued | WNothread | WAll | WClone)) == 0;

    /// <summary>Prüft die Optionen von waitid: dieselben plus WEXITED und WNOWAIT, mindestens eins von WEXITED/WSTOPPED/WCONTINUED.</summary>
    public static bool ValidWaitidOptions(int options) =>
        (options & ~(WNohang | WStopped | WExited | WContinued | WNowait | WNothread | WAll | WClone)) == 0
        && (options & (WExited | WStopped | WContinued)) != 0;

    private bool Matches(Member m, int idType, int id) => idType switch
    {
        1 => m.Pid == id,
        2 => id == _rootPid,
        _ => true,
    };

    /// <summary>true, wenn der Elternteil lebt und SIGCHLD weder ignoriert noch SA_NOCLDWAIT gesetzt hat (dann bleibt ein Zombie).</summary>
    private bool ParentWaitsFor(Member child)
    {
        if (child.ParentPid == 0 || !_members.TryGetValue(child.ParentPid, out Member? parent) || parent.Process is not ILinuxProcess pp)
        {
            return false;
        }

        // Handler und Flags schreibt rt_sigaction ohne diese Sperre; ein Wort zu lesen ist atomar, ein gleichzeitiges Ändern trifft Linux genauso
        // (dort entscheidet der Zustand im Augenblick des Endes).
        LinuxSignalState sig = pp.Signals;
        return sig.Handler[SigChld] != SigIgn && (sig.Flags[SigChld] & SaNoCldWait) == 0;
    }
}
