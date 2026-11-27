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
        SetRobustList = 273, Rseq = 334, Clone = 56, Tkill = 200, Tgkill = 234;

    public const long EPERM = 1, EBADF = 9, ENOMEM = 12, EFAULT = 14, EINVAL = 22, ENOTTY = 25, ENOSYS = 38, ENODEV = 19, EACCES = 13;

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
            case Exit when p is ILinuxThreads threads:
                // V10: exit beendet nur den Thread; der letzte beendet den Prozess.
                threads.ExitThread((int)(a.A0 & 0xff));
                return 0;
            case ExitGroup:
            case Exit:
                p.Exit((int)(a.A0 & 0xff));
                return 0;
            case Clone:
                return p is ILinuxThreads cloner ? cloner.CloneThread(a.A0, a.A1, a.A2, a.A3, a.A4) : -ENOSYS;
            case Fork:
            case Vfork:
            case Execve:
            case Clone3:
                return DispatchSpawn(ref a, p); // V15: posix_spawn; V21: fork, clone3
            case Tgkill:
                return p is ILinuxThreads tg ? tg.SignalThread((int)a.A0, (int)a.A1, (int)a.A2) : -ENOSYS;
            case Tkill:
                return p is ILinuxThreads tk ? tk.SignalThread(-1, (int)a.A0, (int)a.A1) : -ENOSYS;
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
                return p is ILinuxThreads me ? me.CurrentThread.Tid : p.Pid;
            case Gettid:
                return p is ILinuxThreads cur ? cur.CurrentThread.Tid : p.Pid;
            case Getpid:
                return p.Pid;
            case SetRobustList:
                return 0;
            case Ioctl:
                return DoIoctl(p, (int)a.A0, a.A1, a.A2); // Terminal (V3 termios), sonst -ENOTTY
            case Getpgrp:
            case Getpgid:
            case Getsid:
            case Setpgid:
            case Setsid:
                return DoProcessGroup(p, ref a); // V3 raw
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
        ulong n = count > (ulong)MaxRw ? (ulong)MaxRw : count;
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is ILinuxFile && p is ILinuxFiles f)
        {
            // V11: Dateien nur unter der Dateisperre, sonst könnte ein anderer Thread sie mitten im Schreiben schließen; unter der
            // Sperre neu nachsehen. Das Terminal gehört dem Prozess und wird nie über die Tabelle geschlossen: Es schreibt ohne
            // Sperre (wie Linux mit dem Verweis vom Anfang des Aufrufs), damit eine volle Ausgabe keine Dateiaufrufe aufhält.
            lock (f.FileGate)
            {
                d = p.GetDescriptor(fd);
                return d is null ? -EBADF : WriteFromUser(p, d, buf, n);
            }
        }

        return d is null ? -EBADF : WriteFromUser(p, d, buf, n);
    }

    /// <summary>
    /// true, wenn das Programm abgebrochen ist (Strg+C, Zeitlimit, exit_group). Kopierschleifen bis <see cref="MaxRw"/> (2 GiB) prüfen das
    /// je Stück und enden mit dem bisher Kopierten oder -EINTR (Issue #735 P2-2): Ring 3 verlässt das Programm erst nach dem Aufruf.
    /// </summary>
    private static bool Aborted(ILinuxProcess p) => p is ILinuxAbort abort && abort.IsAborted;

    /// <summary>Kopiert in Stücken aus dem Benutzerspeicher. Schlägt erst ein späteres Stück fehl, zählt das bisher Geschriebene.</summary>
    private static long WriteFromUser(ILinuxProcess p, ILinuxDescriptor d, ulong buf, ulong count)
    {
        Span<byte> tmp = new byte[(int)Math.Min(count, (ulong)Chunk)];
        ulong done = 0;
        while (done < count)
        {
            if (Aborted(p))
            {
                return done > 0 ? (long)done : -EINTR;
            }

            int n = (int)Math.Min(count - done, (ulong)tmp.Length);
            if (buf + done < buf || !p.Memory.TryRead(buf + done, tmp.Slice(0, n)))
            {
                return done > 0 ? (long)done : -EFAULT;
            }

            // V16: Pipes und Unix-Sockets warten mit dem Abbruch des schreibenden Prozesses, nicht des Erzeugers (vfork, SCM_RIGHTS).
            long w = d is ILinuxPollable pollable ? pollable.WriteAs(tmp.Slice(0, n), p as ILinuxAbort) : d.Write(tmp.Slice(0, n));
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
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is ILinuxFile && p is ILinuxFiles f)
        {
            // V11: wie DoWrite, Dateien unter der Dateisperre, das Terminal ohne.
            lock (f.FileGate)
            {
                d = p.GetDescriptor(fd);
                return d is null ? -EBADF : WritevTo(p, d, iov, cnt);
            }
        }

        return d is null ? -EBADF : WritevTo(p, d, iov, cnt);
    }

    private static long WritevTo(ILinuxProcess p, ILinuxDescriptor d, ulong iov, ulong cnt)
    {
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

        if (len > ulong.MaxValue - 0xfff)
        {
            return -ENOMEM;
        }

        if ((flags & MapAnonymous) == 0)
        {
            return MapFile(p, a.A0, (len + 0xfff) & ~0xfffUL, prot, flags, fd, a.A5);
        }

        // V21: MAP_SHARED|MAP_ANONYMOUS bleibt über fork geteilt. Dahinter steht eine namenlose memfd ohne Deskriptor (Linux: shmem
        // "/dev/zero (deleted)"); die Abbildung hält sie. Mit MAP_FIXED bleibt es privat wie bisher (Abweichung; MapFixed ersetzt nur anonym).
        if ((flags & MapShared) != 0 && (flags & MapFixed) == 0 && p is ILinuxSharedMemory sharedAnon)
        {
            return MapSharedAnonymous(sharedAnon, (len + 0xfff) & ~0xfffUL, prot);
        }

        if ((flags & MapFixed) != 0)
        {
            ulong flen = (len + 0xfff) & ~0xfffUL;
            if ((a.A0 & 0xfff) != 0 || a.A0 > MaxUserSpan || flen > MaxUserSpan - a.A0)
            {
                return -EINVAL;
            }

            return p.MapFixed(a.A0, flen, prot) ? unchecked((long)a.A0) : -ENOMEM;
        }

        ulong r = p.MapAnonymous((len + 0xfff) & ~0xfffUL, prot);
        return r == 0 ? -ENOMEM : unchecked((long)r);
    }

    private const ulong MaxUserSpan = 1UL << 47;

    private static long MapSharedAnonymous(ILinuxSharedMemory shared, ulong flen, int prot)
    {
        if (flen > MaxUserSpan)
        {
            return -ENOMEM;
        }

        // V24: Die Länge ist frei (CreateAnonymousSharedFile); die Speichergrenze greift wie bei privaten Bereichen erst beim Zugriff.
        LinuxMemFile mem = shared.CreateAnonymousSharedFile();
        long r = mem.Truncate((long)flen);
        long at = r < 0 ? -ENOMEM : shared.MapShared(mem, flen, prot, 0);

        // Kein Deskriptor: Nach Close hält nur noch die Abbildung die Datei; scheiterte sie, gibt Close die Frames sofort frei.
        _ = mem.Close();
        return at;
    }

    private const int ProtWrite = 0x2, ProtReadWrite = 0x3;

    /// <summary>
    /// V12: Dateiabbildung als Kopie. Der Bereich wird anonym belegt, ab <paramref name="offset"/> mit dem Dateiinhalt gefüllt (hinter
    /// dem Dateiende Nullen) und erst danach auf <paramref name="prot"/> gesetzt. Das entspricht MAP_PRIVATE bis auf eines: spätere
    /// Änderungen an der Datei sieht die Abbildung nicht. MAP_SHARED gibt es deshalb nur ohne PROT_WRITE (Ladybird bildet ICU-Daten
    /// so ab); beschreibbar geteilt bleibt -ENODEV, weil nichts in die Datei zurückfließen würde.
    /// Ausnahme seit V17: Eine memfd (<see cref="LinuxMemFile"/>) bildet MAP_SHARED ihre eigenen Frames ab, auch beschreibbar.
    /// V25: MAP_FIXED mit Datei (der dynamische Lader von musl bildet jedes Segment einer Bibliothek so in seine Reservierung ab, auch
    /// mit PROT_EXEC). Der alte Bereich wird ersetzt, bevor feststeht, ob die Kopie gelingt; scheitert sie, bleibt an der Stelle ein Loch.
    /// Das tut Linux ebenso (mmap_region gibt den Altbestand vor dem Neuabbilden frei). Ausnahme: MAP_SHARED|MAP_FIXED einer memfd bleibt -EINVAL.
    /// </summary>
    private static long MapFile(ILinuxProcess p, ulong addr, ulong flen, int prot, int flags, int fd, ulong offset)
    {
        if (fd < 0)
        {
            return -EBADF;
        }

        if (p is not ILinuxFiles f)
        {
            return -ENODEV; // Prozess ohne Dateien (Hosttests mit Ersatzprozess)
        }

        bool fixedAt = (flags & MapFixed) != 0;
        if (fixedAt && ((addr & 0xfff) != 0 || addr > MaxUserSpan || flen > MaxUserSpan - addr))
        {
            return -EINVAL;
        }

        // V17: MAP_SHARED einer memfd bildet ihre Frames selbst ab (auch beschreibbar); alle Prozesse sehen dieselben Seiten.
        if ((flags & MapShared) != 0 && p is ILinuxSharedMemory shared)
        {
            lock (f.FileGate)
            {
                if (p.GetDescriptor(fd) is LinuxMemFile mem)
                {
                    if (fixedAt)
                    {
                        return -EINVAL; // geteilte Frames an fester Stelle gibt es nicht (siehe oben)
                    }

                    // Wie Linux 6.12 (file_mmap_ok mit MAX_LFS_FILESIZE): Offset plus Länge über LONG_MAX gibt -EOVERFLOW; darunter
                    // passt die Dateiseite in long und der Bereich hinter dem Dateiende scheitert erst beim Zugriff.
                    return flen > long.MaxValue || offset / 0x1000 > (ulong)(long.MaxValue - (long)flen) >> 12
                        ? -Vfs.LinuxErrno.EOVERFLOW
                        : shared.MapShared(mem, flen, prot, (long)(offset / 0x1000));
                }
            }
        }

        if ((flags & MapShared) != 0 && (prot & ProtWrite) != 0)
        {
            return -ENODEV;
        }

        if (flen > MaxUserSpan || offset > long.MaxValue - flen)
        {
            return -ENOMEM;
        }

        // Reihenfolge der Sperren wie bei read: Dateisperre, darin die Speichersperre (MapAnonymous, Kopieren, Protect).
        lock (f.FileGate)
        {
            ILinuxDescriptor? d = p.GetDescriptor(fd);
            if (d is null)
            {
                return -EBADF;
            }

            if (d is not ILinuxFile file || file.IsDirectory)
            {
                return -ENODEV; // Terminal und Verzeichnisse lassen sich unter Linux auch nicht abbilden
            }

            // Vor dem Ersetzen prüfen, was Linux vorab prüft (Datei zum Lesen offen): Ein O_WRONLY-Deskriptor lässt den Altbestand stehen.
            if (fixedAt)
            {
                if ((prot & 0x6) == 0x6)
                {
                    return -ENOMEM; // W^X wie MapFixed bei anonymen Bereichen, aber bevor der Altbestand weicht
                }

                long readable = ProbeReadable(file, (long)offset);
                if (readable < 0)
                {
                    return readable;
                }
            }

            // Zum Füllen beschreibbar, ohne PROT_EXEC (W^X); den gewünschten Schutz setzt erst Protect. MAP_FIXED ersetzt den Altbestand
            // (Frames frei, Bereich neu und leer), sodass die Kopie in genullte Seiten geht wie bei einem frischen Bereich.
            ulong at = fixedAt ? (p.MapFixed(addr, flen, ProtReadWrite) ? addr : 0) : p.MapAnonymous(flen, ProtReadWrite);
            if (at == 0)
            {
                return -ENOMEM;
            }

            long result = FillFromFile(p, file, at, flen, (long)offset);
            if (result >= 0 && !p.Protect(at, flen, prot))
            {
                result = -ENOMEM;
            }

            if (result < 0)
            {
                p.Unmap(at, flen); // keine teilgefüllte Abbildung bleibt zurück; bei MAP_FIXED ein Loch wie bei Linux
                return result;
            }

            return unchecked((long)at);
        }
    }

    /// <summary>Liest ein Byte ab <paramref name="offset"/> zur Probe (0 oder -errno; nur zum Schreiben offen ergibt -EACCES). Die Dateiposition bleibt.</summary>
    private static long ProbeReadable(ILinuxFile file, long offset)
    {
        long saved = file.Seek(0, 1);
        if (saved < 0)
        {
            return saved;
        }

        long r = file.Seek(offset, 0);
        if (r >= 0)
        {
            Span<byte> one = stackalloc byte[1];
            r = file.Read(one);
        }

        file.Seek(saved, 0);
        return r == -EBADF ? -EACCES : r < 0 ? r : 0;
    }

    /// <summary>Liest wie pread ab <paramref name="offset"/> nach <paramref name="at"/>; 0 oder -errno. Die Dateiposition bleibt.</summary>
    private static long FillFromFile(ILinuxProcess p, ILinuxFile file, ulong at, ulong flen, long offset)
    {
        long saved = file.Seek(0, 1);
        if (saved < 0)
        {
            return saved;
        }

        long r = file.Seek(offset, 0);
        if (r >= 0)
        {
            // Über MaxRw (2 GiB) hinaus bleibt der Rest Null; so große Abbildungen braucht kein Programm, das hier läuft.
            r = CopyToMapping(p, file, at, Math.Min(flen, (ulong)MaxRw));
            if (r == -EBADF)
            {
                r = -EACCES; // nur zum Schreiben geöffnet: Linux meldet EACCES
            }
        }

        file.Seek(saved, 0);
        return r < 0 ? r : 0;
    }

    /// <summary>
    /// Kopiert bis zum Dateiende oder <paramref name="count"/> Bytes; 0 oder -errno. Anders als ReadToUser (read liefert nach einer
    /// Teilkopie die Kurzlänge) ist hier jeder Abbruch ein Fehler: Eine still teilgenullte Abbildung sähe aus wie gültige Daten.
    /// Nur ein Lesen mit 0 Bytes ist das Dateiende.
    /// </summary>
    private static long CopyToMapping(ILinuxProcess p, ILinuxFile file, ulong at, ulong count)
    {
        byte[] tmp = new byte[(int)Math.Min(Math.Max(count, 1), (ulong)FileChunk)];
        ulong done = 0;
        while (done < count)
        {
            if (Aborted(p))
            {
                return -EINTR;
            }

            int want = (int)Math.Min(count - done, (ulong)tmp.Length);
            long n = file.Read(tmp.AsSpan(0, want));
            if (n < 0)
            {
                return n;
            }

            if (n == 0)
            {
                return 0;
            }

            // Der Bereich ist belegt und beschreibbar; scheitert das Schreiben, fehlt ein Frame.
            if (!p.Memory.TryWrite(at + done, tmp.AsSpan(0, (int)n)))
            {
                return -ENOMEM;
            }

            done += (ulong)n;
        }

        return 0;
    }

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

        ulong flen = (len + 0xfff) & ~0xfffUL;
        if (p is ILinuxSharedMemory shared)
        {
            return shared.ProtectShared(addr, flen, prot); // V17: -EACCES für versiegelte geteilte Abbildungen
        }

        return p.Protect(addr, flen, prot) ? 0 : -ENOMEM;
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
