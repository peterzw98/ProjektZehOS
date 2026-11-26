using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>Aufrufnummer und sechs Argumente wie bei x86-64 (rax; rdi, rsi, rdx, r10, r8, r9).</summary>
internal struct SyscallArgs
{
    // Eigenschaften statt Felder: Nur Tests und die Brücke setzen sie, ein Feld wäre im Kernel CS0649.
    public long Number { get; set; }
    public ulong A0 { get; set; }
    public ulong A1 { get; set; }
    public ulong A2 { get; set; }
    public ulong A3 { get; set; }
    public ulong A4 { get; set; }
    public ulong A5 { get; set; }
}

/// <summary>
/// Linux-x86-64-Aufrufe Stufe S1 (siehe docs/LINUX_ABI.md). Rückgabe: Ergebnis oder -errno.
/// Plattformunabhängig, jeder Zeiger geht über <see cref="IUserMemory"/>.
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long Write = 1, Mmap = 9, Mprotect = 10, Munmap = 11, Brk = 12, Ioctl = 16, Writev = 20,
        Getpid = 39, Exit = 60, ArchPrctl = 158, Gettid = 186, SetTidAddress = 218, ExitGroup = 231,
        SetRobustList = 273, Rseq = 334;

    public const long EPERM = 1, EBADF = 9, ENOMEM = 12, EFAULT = 14, EINVAL = 22, ENOTTY = 25, ENOSYS = 38, ENODEV = 19;

    private const int MapPrivate = 0x02, MapShared = 0x01, MapFixed = 0x10, MapAnonymous = 0x20;
    private const int MaxIov = 1024;
    private const long MaxRw = 0x7ffff000;
    private const ulong ArchSetFs = 0x1002, ArchGetFs = 0x1003;
    private const int Chunk = 4096;

    public static long Dispatch(ref SyscallArgs a, ILinuxProcess p)
    {
        switch (a.Number)
        {
            // Terminaleingabe (V3 stdin) hier, Dateien über DispatchFile (V2 Dateien, nur mit ILinuxFiles); ohne Dateien ist jeder
            // andere fd -EBADF wie bei Linux, nicht -ENOSYS.
            case Read when p is not ILinuxFiles || p.GetDescriptor((int)a.A0) is ILinuxInput: return DoReadInput(p, (int)a.A0, a.A1, a.A2);
            case Readv when p is not ILinuxFiles || p.GetDescriptor((int)a.A0) is ILinuxInput: return DoReadvInput(p, (int)a.A0, a.A1, a.A2);
            case Write: return DoWrite(p, (int)a.A0, a.A1, a.A2);
            case Writev: return DoWritev(p, (int)a.A0, a.A1, a.A2);
            case ExitGroup:
            case Exit:
                p.Exit((int)(a.A0 & 0xff));
                return 0;
            case Brk:
                // Wie Linux: bei Fehler den alten Wert zurückgeben, nie einen Fehlercode.
                if (a.A0 != 0)
                {
                    p.TrySetBrk(a.A0);
                }

                return unchecked((long)p.Brk);
            case Mmap: return DoMmap(p, ref a);
            case Munmap: return DoMunmap(p, a.A0, a.A1);
            case Mprotect: return DoMprotect(p, a.A0, a.A1, (int)a.A2);
            case ArchPrctl: return DoArchPrctl(p, a.A0, a.A1);
            case SetTidAddress:
                p.ClearChildTid = a.A0;
                return p.Pid;
            case Getpid:
            case Gettid:
                return p.Pid;
            case SetRobustList:
                return 0;
            case Ioctl:
                return DoIoctl(p, (int)a.A0, a.A1, a.A2); // Terminal (V3 termios), sonst -ENOTTY
            case Mremap:
            case Prlimit64:
            case Getrlimit:
            case Setrlimit:
            case SchedGetaffinity:
            case Prctl:
                return DispatchLimits(ref a, p); // V9: Stackmessung, Grenzen, CPU-Maske, Threadname (js-Vorbereitung)
            default:
                return DispatchMore(ref a, p); // Zeit, Zufall, Signale, poll, madvise, futex; sonst -ENOSYS (auch rseq: musl und glibc fallen zurück)
        }
    }

    private static long DoWrite(ILinuxProcess p, int fd, ulong buf, ulong count)
    {
        var d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        return WriteFromUser(p, d, buf, count > (ulong)MaxRw ? (ulong)MaxRw : count);
    }

    /// <summary>Kopiert in Stücken aus dem Benutzerspeicher. Schlägt erst ein späteres Stück fehl, zählt das bisher Geschriebene.</summary>
    private static long WriteFromUser(ILinuxProcess p, ILinuxDescriptor d, ulong buf, ulong count)
    {
        Span<byte> tmp = new byte[(int)Math.Min(count, (ulong)Chunk)];
        ulong done = 0;
        while (done < count)
        {
            int n = (int)Math.Min(count - done, (ulong)tmp.Length);
            if (buf + done < buf || !p.Memory.TryRead(buf + done, tmp.Slice(0, n)))
            {
                return done > 0 ? (long)done : -EFAULT;
            }

            long w = d.Write(tmp.Slice(0, n));
            if (w < 0)
            {
                return done > 0 ? (long)done : w;
            }

            done += (ulong)w;
            if (w < n)
            {
                break;
            }
        }

        return (long)done;
    }

    private static long DoWritev(ILinuxProcess p, int fd, ulong iov, ulong cnt)
    {
        var d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (cnt > MaxIov)
        {
            return -EINVAL;
        }

        // Erst alle Längen prüfen (Summe über MaxRw ist EINVAL; Linux kürzt stattdessen, siehe LINUX_ABI.md), dann schreiben.
        var bases = new ulong[cnt];
        var lens = new ulong[cnt];
        ulong total = 0;
        Span<byte> entry = stackalloc byte[16];
        for (ulong i = 0; i < cnt; i++)
        {
            if (iov + i * 16 < iov || !p.Memory.TryRead(iov + i * 16, entry))
            {
                return -EFAULT;
            }

            bases[i] = BitConverter.ToUInt64(entry.Slice(0, 8));
            lens[i] = BitConverter.ToUInt64(entry.Slice(8, 8));
            if (lens[i] > (ulong)MaxRw || total + lens[i] > (ulong)MaxRw)
            {
                return -EINVAL;
            }

            total += lens[i];
        }

        long sum = 0;
        for (ulong i = 0; i < cnt; i++)
        {
            if (lens[i] == 0)
            {
                continue;
            }

            long w = WriteFromUser(p, d, bases[i], lens[i]);
            if (w < 0)
            {
                return sum > 0 ? sum : w;
            }

            sum += w;
            if ((ulong)w < lens[i])
            {
                break;
            }
        }

        return sum;
    }

    private static long DoMmap(ILinuxProcess p, ref SyscallArgs a)
    {
        ulong len = a.A1;
        int prot = (int)a.A2, flags = (int)a.A3;
        int fd = (int)a.A4; // int: ein zero-extended -1 muss EBADF ergeben
        if (len == 0 || (a.A5 & 0xfff) != 0 || ((flags & MapPrivate) != 0) == ((flags & MapShared) != 0))
        {
            return -EINVAL;
        }

        if ((prot & ~0x7) != 0)
        {
            return -EINVAL; // unbekannte Schutzbits wie Linux
        }

        if ((flags & MapAnonymous) == 0)
        {
            return fd < 0 ? -EBADF : -ENODEV; // S1: keine Dateiabbildung
        }

        if ((flags & MapFixed) != 0)
        {
            return -ENOSYS; // S1: Platzierung bestimmt der Kernel
        }

        if (len > ulong.MaxValue - 0xfff)
        {
            return -ENOMEM;
        }

        ulong r = p.MapAnonymous((len + 0xfff) & ~0xfffUL, prot);
        return r == 0 ? -ENOMEM : unchecked((long)r);
    }

    private const ulong MaxUserSpan = 1UL << 47;

    private static long DoMunmap(ILinuxProcess p, ulong addr, ulong len)
    {
        if ((addr & 0xfff) != 0 || len == 0 || len > MaxUserSpan || addr > MaxUserSpan)
        {
            return -EINVAL;
        }

        return p.Unmap(addr, (len + 0xfff) & ~0xfffUL) ? 0 : -EINVAL;
    }

    private static long DoMprotect(ILinuxProcess p, ulong addr, ulong len, int prot)
    {
        if ((addr & 0xfff) != 0 || len > MaxUserSpan || addr > MaxUserSpan || (prot & ~0x7) != 0)
        {
            return -EINVAL;
        }

        if (len == 0)
        {
            return 0;
        }

        return p.Protect(addr, (len + 0xfff) & ~0xfffUL, prot) ? 0 : -ENOMEM;
    }

    private static long DoArchPrctl(ILinuxProcess p, ulong code, ulong arg)
    {
        switch (code)
        {
            case ArchSetFs:
                // Nur Adressen der Benutzerhälfte (kanonisch, sonst #GP beim Laden von FS.base).
                if (arg >= 0x0000_8000_0000_0000UL)
                {
                    return -EPERM;
                }

                p.FsBase = arg;
                return 0;
            case ArchGetFs:
                Span<byte> b = stackalloc byte[8];
                BitConverter.TryWriteBytes(b, p.FsBase);
                return p.Memory.TryWrite(arg, b) ? 0 : -EFAULT;
            default:
                return -EINVAL;
        }
    }
}
