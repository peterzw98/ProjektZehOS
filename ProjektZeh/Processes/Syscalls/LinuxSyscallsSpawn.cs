using System;
using System.Collections.Generic;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>V15: vfork (58) und execve (59); posix_spawn von musl nimmt clone mit CLONE_VM|CLONE_VFORK (siehe <see cref="LinuxUserProcess"/>).</summary>
internal static partial class LinuxSyscalls
{
    public const long Vfork = 58, Execve = 59;

    /// <summary>Linux: MAX_ARG_STRLEN (32 Seiten) je Zeichenkette.</summary>
    private const int MaxArgString = 32 * 4096;

    /// <summary>Höchstzahl Einträge in argv und envp zusammen; Linux begrenzt über den Platz (ein Viertel des Stacks, 2 MiB).</summary>
    private const int MaxArgCount = 32768;

    /// <summary>Gesamtgröße aller Zeichenketten aus argv und envp (wie das Linux-Standardlimit bei 8 MiB Stack).</summary>
    private const long MaxArgBytes = 2L << 20;

    private const long E2BIG = 7;

    private static long DispatchSpawn(ref SyscallArgs a, ILinuxProcess p)
    {
        switch (a.Number)
        {
            case Vfork:
                // vfork ist clone(CLONE_VM|CLONE_VFORK|SIGCHLD) ohne eigenen Stack.
                return p is ILinuxThreads cloner
                    ? cloner.CloneThread(LinuxUserProcess.CloneVm | LinuxUserProcess.CloneVfork | 17, 0, 0, 0, 0)
                    : -ENOSYS;
            case Execve:
                return DoExecve(p, a.A0, a.A1, a.A2);
            default:
                return -ENOSYS;
        }
    }

    private static long DoExecve(ILinuxProcess p, ulong pathPtr, ulong argvPtr, ulong envpPtr)
    {
        if (p is not ILinuxSpawn spawn || p is not ILinuxFiles f)
        {
            return -ENOSYS;
        }

        string path;
        lock (f.FileGate)
        {
            long r = LinuxPath.Resolve(p, f, LinuxPath.AtFdcwd, pathPtr, out path);
            if (r < 0)
            {
                return r;
            }
        }

        long total = 0;
        long got = ReadStringArray(p, argvPtr, ref total, out string[] argv);
        if (got < 0)
        {
            return got;
        }

        got = ReadStringArray(p, envpPtr, ref total, out string[] envp);
        return got < 0 ? got : spawn.Exec(path, argv, envp);
    }

    /// <summary>Liest ein nullterminiertes Zeigerfeld aus dem Benutzerspeicher; 0 als Feld gilt wie unter Linux als leer.</summary>
    private static long ReadStringArray(ILinuxProcess p, ulong array, ref long total, out string[] values)
    {
        values = [];
        if (array == 0)
        {
            return 0;
        }

        var list = new List<string>();
        Span<byte> slot = stackalloc byte[8];
        for (ulong i = 0; ; i++)
        {
            if (i >= MaxArgCount || total > MaxArgBytes)
            {
                return -E2BIG;
            }

            if (array + i * 8 < array || !p.Memory.TryRead(array + i * 8, slot))
            {
                return -EFAULT;
            }

            ulong ptr = BitConverter.ToUInt64(slot);
            if (ptr == 0)
            {
                break;
            }

            if (!p.Memory.TryReadCString(ptr, MaxArgString, out string s))
            {
                return -EFAULT;
            }

            total += s.Length + 1;
            list.Add(s);
        }

        values = list.ToArray();
        return total > MaxArgBytes ? -E2BIG : 0;
    }
}
