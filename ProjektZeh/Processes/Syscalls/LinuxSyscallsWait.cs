using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V15: Warten auf Kindprozesse (wait4, waitid) und getppid über <see cref="ILinuxWait"/>; ein Prozess ohne sie hat keine Kinder (-ECHILD)
/// und den Elternteil 0.
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long Wait4 = 61, Getppid = 110, Waitid = 247;

    private const long ECHILD = 10;
    private const int PAll = 0, PPid = 1, PPgid = 2;
    private const int RusageSize = 144;
    private const int SigChldNr = 17, CldExited = 1, CldKilled = 2, CldDumped = 3;

    private static long DispatchWait(ref SyscallArgs a, ILinuxProcess p)
    {
        switch (a.Number)
        {
            case Getppid:
                return p is ILinuxWait waiter ? waiter.ParentPid : 0;
            case Wait4:
                return DoWait4(p, (int)a.A0, a.A1, (int)a.A2, a.A3);
            default:
                return DoWaitid(p, (int)a.A0, (int)a.A1, a.A2, (int)a.A3, a.A4);
        }
    }

    /// <summary>wait4(pid, wstatus, options, rusage): PID des abgeholten Kindes, 0 bei WNOHANG ohne fertiges Kind, oder -errno.</summary>
    private static long DoWait4(ILinuxProcess p, int pid, ulong statusPtr, int options, ulong rusagePtr)
    {
        if (!LinuxUserProcess.ValidWait4Options(options))
        {
            return -EINVAL;
        }

        // Linux: -INT_MIN lässt sich nicht verneinen, kernel_wait4 meldet ESRCH.
        if (pid == int.MinValue)
        {
            return -ESRCH;
        }

        if (p is not ILinuxWait member)
        {
            return -ECHILD;
        }

        int idType = pid > 0 ? PPid : pid == -1 ? PAll : PPgid;
        int id = pid > 0 ? pid : pid == 0 ? member.GroupPid : -pid;
        long r = member.WaitChild(idType, id, options, consume: true, () => Aborted(p), out int status, out _);
        if (r <= 0)
        {
            return r;
        }

        // Das Kind ist schon abgeholt; scheitert das Zurückschreiben, meldet Linux trotzdem -EFAULT (kernel_wait4 nach do_wait).
        if (statusPtr != 0 && !p.Memory.TryWrite(statusPtr, BitConverter.GetBytes(status)))
        {
            return -EFAULT;
        }

        return WriteEmptyRusage(p, rusagePtr) ? r : -EFAULT;
    }

    /// <summary>
    /// waitid(idtype, id, infop, options, rusage): 0 oder -errno. Bei WNOHANG ohne fertiges Kind schreibt Linux Nullen in si_signo und si_pid;
    /// das tun wir auch, damit ein Programm das am Wert erkennt.
    /// </summary>
    private static long DoWaitid(ILinuxProcess p, int idType, int id, ulong infoPtr, int options, ulong rusagePtr)
    {
        if (!LinuxUserProcess.ValidWaitidOptions(options))
        {
            return -EINVAL;
        }

        switch (idType)
        {
            case PAll:
                break;
            case PPid when id > 0:
                break;
            case PPgid when id >= 0:
                break;
            default:
                return -EINVAL; // auch P_PIDFD (3): es gibt keine pidfds
        }

        if (p is not ILinuxWait member)
        {
            return -ECHILD;
        }

        if (idType == PPgid && id == 0)
        {
            id = member.GroupPid;
        }

        bool consume = (options & LinuxUserProcess.WNowait) == 0;
        long r = member.WaitChild(idType, id, options, consume, () => Aborted(p), out int status, out ulong uid);
        if (r < 0)
        {
            return r;
        }

        if (infoPtr != 0)
        {
            // x86-64 siginfo_t für SIGCHLD: si_signo @0, si_errno @4, si_code @8, si_pid @16, si_uid @20, si_status @24. Der übrige Teil bleibt
            // unberührt wie bei Linux (waitid schreibt nur diese Felder).
            Span<byte> info = stackalloc byte[28];
            info.Clear();
            if (r > 0)
            {
                int code = LinuxWaitStatus.IsExited(status) ? CldExited : LinuxWaitStatus.CoreDumped(status) ? CldDumped : CldKilled;
                int value = LinuxWaitStatus.IsExited(status) ? LinuxWaitStatus.ExitCode(status) : LinuxWaitStatus.TermSignal(status);
                BitConverter.TryWriteBytes(info.Slice(0, 4), SigChldNr);
                BitConverter.TryWriteBytes(info.Slice(8, 4), code);
                BitConverter.TryWriteBytes(info.Slice(16, 4), (int)r);
                BitConverter.TryWriteBytes(info.Slice(20, 4), (uint)uid);
                BitConverter.TryWriteBytes(info.Slice(24, 4), value);
            }

            if (!p.Memory.TryWrite(infoPtr, info))
            {
                return -EFAULT;
            }
        }

        return r > 0 && !WriteEmptyRusage(p, rusagePtr) ? -EFAULT : 0;
    }

    /// <summary>
    /// rusage des Kindes: ZehOS zählt keine Rechenzeit je Prozess; Nullen sind der ehrliche Wert (Linux liefert dieselbe Struktur, nur gefüllt).
    /// </summary>
    private static bool WriteEmptyRusage(ILinuxProcess p, ulong rusagePtr) => rusagePtr == 0 || p.Memory.TryWrite(rusagePtr, new byte[RusageSize]);
}
