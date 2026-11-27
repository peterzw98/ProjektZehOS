using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes;

/// <summary>
/// Optional neben <see cref="ILinuxProcess"/> (V35): Prozessgruppen und Sitzungen im Lauf. Ohne sie ist der Prozess allein in Gruppe und
/// Sitzung seiner PID (LinuxSyscallsTerminal.cs, DoProcessGroup).
/// </summary>
internal interface ILinuxProcessGroups
{
    /// <summary>getpgrp: aktuelle Prozessgruppe.</summary>
    int ProcessGroup { get; }

    /// <summary>getsid(0): aktuelle Sitzung.</summary>
    int Session { get; }

    /// <summary>getpgid(pid): Gruppe eines Prozesses im Lauf (0 = Aufrufer), -ESRCH, wenn es ihn nicht gibt.</summary>
    long GetProcessGroup(int pid);

    /// <summary>getsid(pid): Sitzung eines Prozesses im Lauf (0 = Aufrufer), -ESRCH, wenn es ihn nicht gibt.</summary>
    long GetSession(int pid);

    /// <summary>setpgid(pid, pgid) nach den Regeln von Linux; 0 oder -errno.</summary>
    long SetProcessGroup(int pid, int pgid);

    /// <summary>setsid: neue Sitzung und Gruppe mit der eigenen PID; liefert sie oder -EPERM.</summary>
    long CreateSession();
}

/// <summary>
/// V35: Prozessgruppen und Sitzungen wie Linux (kernel/sys.c, setpgid, setsid). Der erste Prozess eines Laufs führt Gruppe und Sitzung
/// (wie ein Programm, das Linux direkt in einem neuen Pseudoterminal startet); fork und vfork erben beide, execve behält sie.
/// <para>
/// Alle Änderungen laufen unter <see cref="LinuxIds.GroupGate"/> (eine Sperre je Lauf wie Linux' tasklist_lock): Prüfen und Setzen sind
/// so atomar gegen ein gleichzeitiges setpgid, setsid, fork oder execve. Gelesen wird ohne Sperre (ein int, Volatile). Reihenfolge:
/// GroupGate vor ChildGate (unter ihr werden Kinderlisten gelesen und bei fork/vfork erweitert), nie unter einer anderen Sperre genommen.
/// </para>
/// <para>
/// Abweichung von Linux: Eine PID, die noch als Gruppe oder Sitzung dient, kann nach dem Umlauf der Nummern (<see cref="TidMax"/>) neu vergeben
/// werden; Linux hält sie fest. Stoppsignale (SIGTSTP, SIGTTIN, SIGTTOU) halten weiter nicht an, auch nicht in einer nicht verwaisten Gruppe.
/// </para>
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxProcessGroups
{
    // Unter _ids.GroupGate geschrieben; der erste Prozess setzt beide im Konstruktor auf seine PID.
    private int _pgid;
    private int _sid;

    // Unter _ids.GroupGate: Dieses Abbild stammt aus execve, oder dieses Objekt hat sein Abbild per execve abgegeben. Linux: PF_FORKNOEXEC
    // ist gelöscht, der Elternprozess darf die Gruppe nicht mehr ändern (setpgid -EACCES).
    private bool _execed;

    public int ProcessGroup => Volatile.Read(ref _pgid);

    public int Session => Volatile.Read(ref _sid);

    public long GetProcessGroup(int pid) => Lookup(pid) is LinuxUserProcess t ? t.ProcessGroup : -LinuxSyscalls.ESRCH;

    public long GetSession(int pid) => Lookup(pid) is LinuxUserProcess t ? t.Session : -LinuxSyscalls.ESRCH;

    public long SetProcessGroup(int pid, int pgid)
    {
        if (pgid < 0)
        {
            return -LinuxSyscalls.EINVAL;
        }

        if (pid == 0)
        {
            pid = Pid;
        }

        if (pgid == 0)
        {
            pgid = pid;
        }

        lock (_ids.GroupGate)
        {
            LinuxUserProcess? target = pid == Pid ? this : OwnChild(pid);
            if (target == null)
            {
                // Linux: ein fremder Prozess, der weder der Aufrufer noch sein Kind ist, ebenso -ESRCH.
                return -LinuxSyscalls.ESRCH;
            }

            if (!ReferenceEquals(target, this))
            {
                if (target._sid != _sid)
                {
                    return -LinuxSyscalls.EPERM;
                }

                if (target._execed)
                {
                    return -LinuxSyscalls.EACCES;
                }
            }

            // Ein Sitzungsführer wechselt nie die Gruppe (Linux prüft das vor der Zielgruppe, auch ohne Änderung).
            if (target._sid == target.Pid)
            {
                return -LinuxSyscalls.EPERM;
            }

            // Eine fremde Gruppe muss es in der Sitzung des Aufrufers schon geben; nur die eigene PID gründet eine neue.
            if (pgid != pid && !GroupExists(RunRoot(), pgid, _sid))
            {
                return -LinuxSyscalls.EPERM;
            }

            Volatile.Write(ref target._pgid, pgid);
            return 0;
        }
    }

    public long CreateSession()
    {
        lock (_ids.GroupGate)
        {
            // Linux: scheitert, solange irgendein Prozess (auch ein Zombie) in der Gruppe mit der eigenen PID steht, also auch für den Führer.
            if (GroupExists(RunRoot(), Pid, sid: null))
            {
                return -LinuxSyscalls.EPERM;
            }

            Volatile.Write(ref _sid, Pid);
            Volatile.Write(ref _pgid, Pid);
            return Pid;
        }
    }

    /// <summary>fork und vfork: Das Kind erbt Gruppe und Sitzung. Unter GroupGate rufen, zusammen mit dem Eintrag in die Kinderliste.</summary>
    private void InheritGroup(LinuxUserProcess parent)
    {
        _pgid = parent._pgid;
        _sid = parent._sid;
    }

    /// <summary>execve (aus <see cref="TakeOver"/>): Das neue Abbild behält Gruppe und Sitzung; ab hier ändert der Elternprozess sie nicht mehr.</summary>
    private void TakeOverGroup(LinuxUserProcess from)
    {
        lock (_ids.GroupGate)
        {
            _pgid = from._pgid;
            _sid = from._sid;
            _execed = true;
            from._execed = true; // ein setpgid, das noch das alte Objekt findet, scheitert wie danach
        }
    }

    /// <summary>Prozess mit <paramref name="pid"/> im Lauf (0 = Aufrufer), auch als Zombie wie bei Linux; null, wenn es ihn nicht gibt.</summary>
    private LinuxUserProcess? Lookup(int pid)
    {
        if (pid == 0 || pid == Pid)
        {
            return this;
        }

        return Find(RunRoot(), pid, out LinuxUserProcess? found, out _) ? found : null; // RunRoot: LinuxProcView.cs
    }

    /// <summary>Eigenes Kind mit <paramref name="pid"/>, auch als Zombie (Linux findet es, bis wait es austrägt).</summary>
    private LinuxUserProcess? OwnChild(int pid)
    {
        lock (_children)
        {
            foreach (LinuxChild c in _children)
            {
                if (c.Pid == pid && !(c.WaitStatus != null && c.AutoReaped))
                {
                    return c.Process;
                }
            }
        }

        return null;
    }

    /// <summary>Gibt es im Lauf einen Prozess (auch Zombie) in Gruppe <paramref name="pgid"/>, bei <paramref name="sid"/> in dieser Sitzung?</summary>
    private static bool GroupExists(LinuxUserProcess root, int pgid, int? sid)
    {
        var all = new List<LinuxUserProcess>();
        CollectWithZombies(root, all);
        foreach (LinuxUserProcess p in all)
        {
            if (p._pgid == pgid && (sid == null || p._sid == sid))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Wie <see cref="Collect"/>, aber mit Zombies (sie halten ihre Gruppe bei Linux bis zum Austragen); selbst geerntete Prozesse sind weg.
    /// </summary>
    private static void CollectWithZombies(LinuxUserProcess p, List<LinuxUserProcess> into)
    {
        into.Add(p);
        CollectBelowWithZombies(p, into);
        foreach (LinuxUserProcess holder in p.OrphanHolders)
        {
            CollectBelowWithZombies(holder, into);
        }
    }

    private static void CollectBelowWithZombies(LinuxUserProcess p, List<LinuxUserProcess> into)
    {
        foreach (LinuxChild c in p.Children)
        {
            if (!(c.WaitStatus != null && c.AutoReaped))
            {
                into.Add(c.Process);
            }

            CollectBelowWithZombies(c.Process, into);
        }
    }
}
