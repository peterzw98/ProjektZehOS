using System;
using System.Text;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Dateiaufrufe mit Dateiinhalt und Deskriptoren (V2, Teil Inhalt): open/openat, close, read/readv/pread64, lseek, dup/dup2/dup3,
/// fcntl, getcwd, chdir/fchdir; dazu V14 pwrite64, preadv/pwritev, ftruncate, fsync/fdatasync. Alles Pfadbezogene geht über <see cref="LinuxPath.Resolve(ILinuxProcess, ILinuxFiles, int, ulong, out string)"/>.
/// Nur aktiv, wenn der Prozess <see cref="ILinuxFiles"/> anbietet; was hier nicht bekannt ist, geht an <see cref="DispatchFileMeta"/>.
/// </summary>
internal static partial class LinuxSyscalls
{
    private static class FileNr
    {
        public const long Read = 0, Open = 2, Close = 3, Lseek = 8, Pread64 = 17, Pwrite64 = 18, Readv = 19, Pipe = 22, Dup = 32, Dup2 = 33, Fcntl = 72,
            Fsync = 74, Fdatasync = 75, Ftruncate = 77, Getcwd = 79, Chdir = 80, Fchdir = 81, Openat = 257, Dup3 = 292, Pipe2 = 293, Preadv = 295, Pwritev = 296,
            MemfdCreate = 319;
    }

    private static class FileErr
    {
        public const long EBADF = 9, EFAULT = 14, ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ESPIPE = 29, ERANGE = 34, EIO = 5;
    }

    // Obergrenze der Deskriptornummern (RLIMIT_NOFILE wie poll); close sucht darin nach weiteren Verweisen auf dieselbe Datei.
    private const int FileMaxFds = 1024;
    private const int FileOCloexec = 0x80000;
    private const int FdCloexec = 1;
    private const int FdDupfd = 0, FdGetfd = 1, FdSetfd = 2, FdGetfl = 3, FdSetfl = 4, FdDupfdCloexec = 1030, FdAddSeals = 1033, FdGetSeals = 1034;
    private const int FileChunk = 65536;

    private static long DispatchFile(ref SyscallArgs a, ILinuxProcess p, ILinuxFiles f)
    {
        switch (a.Number)
        {
            case FileNr.Open: return DoOpen(p, f, LinuxPath.AtFdcwd, a.A0, (int)a.A1, (uint)a.A2);
            case FileNr.Openat: return DoOpen(p, f, (int)a.A0, a.A1, (int)a.A2, (uint)a.A3);
            case FileNr.Close: return DoClose(p, f, (int)a.A0);
            case FileNr.Read: return DoRead(p, (int)a.A0, a.A1, a.A2);
            case FileNr.Readv: return DoReadv(p, (int)a.A0, a.A1, a.A2);
            case FileNr.Pread64: return DoPread(p, (int)a.A0, a.A1, a.A2, unchecked((long)a.A3));
            case FileNr.Pwrite64: return DoPositioned(p, (int)a.A0, a.A1, a.A2, unchecked((long)a.A3), write: true, vector: false);
            // preadv/pwritev: Position in A3 (pos_l); A4 (pos_h) hat auf x86-64 keine Bedeutung, der Kernel schiebt sie ganz hinaus.
            case FileNr.Preadv: return DoPositioned(p, (int)a.A0, a.A1, a.A2, unchecked((long)a.A3), write: false, vector: true);
            case FileNr.Pwritev: return DoPositioned(p, (int)a.A0, a.A1, a.A2, unchecked((long)a.A3), write: true, vector: true);
            case FileNr.Ftruncate: return DoFtruncate(p, (int)a.A0, unchecked((long)a.A1));
            case FileNr.Fsync:
            case FileNr.Fdatasync: return DoFsync(p, (int)a.A0);
            case FileNr.Lseek: return DoLseek(p, (int)a.A0, unchecked((long)a.A1), (int)a.A2);
            case FileNr.Dup: return DoDup(p, f, (int)a.A0, 0);
            case FileNr.Dup2: return DoDup2(p, f, (int)a.A0, (int)a.A1, allowSame: true);
            case FileNr.Dup3:
                if (((int)a.A2 & ~FileOCloexec) != 0 || (int)a.A0 == (int)a.A1)
                {
                    return -FileErr.EINVAL;
                }

                return WithCloseOnExec(f, DoDup2(p, f, (int)a.A0, (int)a.A1, allowSame: false), ((int)a.A2 & FileOCloexec) != 0);
            case FileNr.Fcntl: return DoFcntl(p, f, (int)a.A0, (int)a.A1, a.A2);
            case FileNr.Getcwd: return DoGetcwd(p, f, a.A0, a.A1);
            case FileNr.Chdir: return DoChdir(p, f, a.A0);
            case FileNr.Fchdir: return DoFchdir(p, f, (int)a.A0);
            case FileNr.Pipe: return DoPipe2(p, f, a.A0, 0);
            case FileNr.Pipe2: return DoPipe2(p, f, a.A0, (int)a.A1);
            case FileNr.MemfdCreate: return DoMemfdCreate(p, f, a.A0, (uint)a.A1);
            default: return DispatchFileMeta(ref a, p, f);
        }
    }

    private static long DoOpen(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong pathPtr, int flags, uint mode)
    {
        long r = LinuxPath.Resolve(p, f, dirfd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        r = f.FileSystem.Open(path, flags, mode & 0xfff, out ILinuxFile? file);
        if (r < 0)
        {
            return r;
        }

        if (file is null)
        {
            return -FileErr.EIO; // Vertragsbruch der Implementierung, kein Programmfehler
        }

        int fd = f.AddDescriptor(file, 0);
        if (fd < 0)
        {
            _ = file.Close(); // -EMFILE: die Datei hat keinen Deskriptor bekommen und wäre sonst nie geschlossen
        }
        else
        {
            MarkCloseOnExec(f, fd, (flags & FileOCloexec) != 0);
        }

        return fd;
    }

    private static long DoClose(ILinuxProcess p, ILinuxFiles f, int fd)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null || !f.RemoveDescriptor(fd))
        {
            return -EBADF;
        }

        // Wie Linux: Die Nummer ist auch bei einem Fehler frei; der Fehler beim Zurückschreiben erreicht das Programm (nur beim letzten close).
        return ReleaseIfUnused(p, d);
    }

    /// <summary>
    /// Schließt eine Datei erst, wenn keine Nummer mehr auf sie zeigt (dup teilt die offene Datei wie bei Linux). Der Aufrufer hält FileGate.
    /// Die Tabelle hat keine Aufzählung; FileMaxFds Abfragen je close sind billig gegenüber einer Datei-I/O.
    /// </summary>
    internal static long ReleaseIfUnused(ILinuxProcess p, ILinuxDescriptor d)
    {
        if (d is not ILinuxFile and not LinuxPipeEnd and not LinuxUnixSocket and not ILinuxAnonFile)
        {
            return 0; // Terminal und andere gehören dem Prozess, nicht der Tabelle
        }

        for (int i = 0; i < FileMaxFds; i++)
        {
            if (ReferenceEquals(p.GetDescriptor(i), d))
            {
                return 0;
            }
        }

        // V15: Nach vfork teilen Prozesse eines Laufs die offenen Dateien; erst die letzte Nummer irgendeines Prozesses schließt.
        if (p is ILinuxOpenFileCount count && count.IsOpenAnywhere(d))
        {
            return 0;
        }

        if (d is LinuxUnixSocket socket)
        {
            socket.Close(); // V16: Gegenseite sieht EOF/EPIPE, Ungelesenes und mitgeschickte Dateien verfallen
            return 0;
        }

        if (d is ILinuxAnonFile anon)
        {
            anon.Close(); // V20: eventfd, inotify
            return 0;
        }

        if (d is LinuxPipeEnd end)
        {
            end.Close(); // V13: erst das letzte close eines Endes meldet dem anderen EOF oder EPIPE
            return 0;
        }

        return ((ILinuxFile)d).Close();
    }

    private static long DoRead(ILinuxProcess p, int fd, ulong buf, ulong count)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not ILinuxFile file)
        {
            return -EBADF; // Terminal ist bisher nur zum Schreiben offen
        }

        if (file.IsDirectory)
        {
            return -FileErr.EISDIR;
        }

        return ReadToUser(file, p, buf, Math.Min(count, (ulong)MaxRw));
    }

    /// <summary>
    /// Liest in Stücken in den Benutzerspeicher. Scheitert das Zurückschreiben, wird die Position um das nicht Abgelieferte
    /// zurückgesetzt, damit kein Inhalt verloren geht; zählt dann das bisher Abgelieferte oder -EFAULT.
    /// </summary>
    private static long ReadToUser(ILinuxFile file, ILinuxProcess p, ulong buf, ulong count)
    {
        if (count == 0)
        {
            return 0;
        }

        byte[] tmp = new byte[(int)Math.Min(count, (ulong)FileChunk)];
        ulong done = 0;
        while (done < count)
        {
            if (Aborted(p))
            {
                return done > 0 ? (long)done : -EINTR;
            }

            int want = (int)Math.Min(count - done, (ulong)tmp.Length);
            long n = file.Read(tmp.AsSpan(0, want));
            if (n < 0)
            {
                return done > 0 ? (long)done : n;
            }

            if (n == 0)
            {
                break;
            }

            if (buf + done < buf || !p.Memory.TryWrite(buf + done, tmp.AsSpan(0, (int)n)))
            {
                file.Seek(-n, 1);
                return done > 0 ? (long)done : -FileErr.EFAULT;
            }

            done += (ulong)n;
            if (n < want)
            {
                break; // kurzer Lesevorgang: Ende der Datei oder des Geräts
            }
        }

        return (long)done;
    }

    private static long DoReadv(ILinuxProcess p, int fd, ulong iov, ulong cnt)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null || d is not ILinuxFile file)
        {
            return -EBADF;
        }

        if (cnt > MaxIov)
        {
            return -EINVAL;
        }

        if (file.IsDirectory)
        {
            return -FileErr.EISDIR;
        }

        return ReadvFrom(p, file, iov, cnt);
    }

    /// <summary>readv auf eine geprüfte Datei (kein Verzeichnis, cnt höchstens <see cref="MaxIov"/>).</summary>
    private static long ReadvFrom(ILinuxProcess p, ILinuxFile file, ulong iov, ulong cnt)
    {
        // Wie writev: erst alle Einträge lesen und prüfen, dann lesen.
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

            long n = ReadToUser(file, p, bases[i], lens[i]);
            if (n < 0)
            {
                return sum > 0 ? sum : n;
            }

            sum += n;
            if ((ulong)n < lens[i])
            {
                break;
            }
        }

        return sum;
    }

    private static long DoPread(ILinuxProcess p, int fd, ulong buf, ulong count, long offset)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not ILinuxFile file)
        {
            return -FileErr.ESPIPE;
        }

        if (file.IsDirectory)
        {
            return -FileErr.EISDIR;
        }

        if (offset < 0)
        {
            return -EINVAL;
        }

        // pread lässt die Position unverändert. Über Seek nachgebildet; das ist atomar, weil alle Dateiaufrufe unter der
        // Dateisperre laufen (V11, DispatchFileGated).
        long saved = file.Seek(0, 1);
        if (saved < 0)
        {
            return saved;
        }

        long r = file.Seek(offset, 0);
        if (r < 0)
        {
            return r;
        }

        long n = ReadToUser(file, p, buf, Math.Min(count, (ulong)MaxRw));
        file.Seek(saved, 0);
        return n;
    }

    /// <summary>
    /// pwrite64, preadv und pwritev (V14): wie write/readv/writev ab <paramref name="offset"/>, die Position des Deskriptors bleibt.
    /// Nachgebildet wie <see cref="DoPread"/> über Seek unter der Dateisperre. Reihenfolge der Prüfungen wie Linux: negative Position
    /// vor dem Deskriptor (-EINVAL), dann -EBADF, Terminal -ESPIPE, Verzeichnis beim Lesen -EISDIR, beim Schreiben -EBADF (nur lesend offen).
    /// Bei O_APPEND hängt pwrite wie unter Linux ans Ende an (bekannte Linux-Eigenheit), die Position bleibt trotzdem.
    /// </summary>
    private static long DoPositioned(ILinuxProcess p, int fd, ulong buf, ulong count, long offset, bool write, bool vector)
    {
        if (offset < 0)
        {
            return -EINVAL;
        }

        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not ILinuxFile file)
        {
            return -FileErr.ESPIPE;
        }

        if (file.IsDirectory)
        {
            return write ? -EBADF : -FileErr.EISDIR;
        }

        if (vector && count > MaxIov)
        {
            return -EINVAL;
        }

        ulong n = vector ? count : Math.Min(count, (ulong)MaxRw);
        if (!vector && offset > long.MaxValue - (long)n)
        {
            return -EINVAL; // Linux: Position und Länge laufen über (rw_verify_area)
        }

        long saved = file.Seek(0, 1);
        if (saved < 0)
        {
            return saved;
        }

        long r = file.Seek(offset, 0);
        if (r < 0)
        {
            return r;
        }

        if (write)
        {
            r = vector ? WritevTo(p, file, buf, count) : WriteFromUser(p, file, buf, n);
        }
        else
        {
            r = ReadvFrom(p, file, buf, count);
        }

        file.Seek(saved, 0);
        return r;
    }

    private static long DoFtruncate(ILinuxProcess p, int fd, long length)
    {
        if (length < 0)
        {
            return -EINVAL; // Linux prüft die Länge vor dem Deskriptor
        }

        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        // Terminal und andere Nicht-Dateien: keine reguläre Datei, -EINVAL wie Linux für Pipes und Geräte.
        return d is ILinuxFile file ? file.Truncate(length) : -EINVAL;
    }

    private static long DoFsync(ILinuxProcess p, int fd)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        // Terminal: Linux hat für Terminals und Pipes kein fsync (-EINVAL).
        return d is ILinuxFile file ? file.Sync() : -EINVAL;
    }

    private static long DoLseek(ILinuxProcess p, int fd, long offset, int whence)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not ILinuxFile file)
        {
            return -FileErr.ESPIPE; // Terminal
        }

        if (whence is < 0 or > 2)
        {
            return -EINVAL;
        }

        if (file.IsDirectory)
        {
            // Bei Verzeichnissen ist die Position der getdents64-Index (d_off = nächster Index): rewinddir/seekdir/telldir.
            long target = whence == 0 ? offset : whence == 1 ? file.DirIndex + offset : -1;
            if (target < 0)
            {
                return -EINVAL; // SEEK_END hat bei Verzeichnissen keine Bedeutung
            }

            file.DirIndex = target;
            return target;
        }

        return file.Seek(offset, whence);
    }

    private static long DoDup(ILinuxProcess p, ILinuxFiles f, int oldFd, int min)
    {
        ILinuxDescriptor? d = p.GetDescriptor(oldFd);
        if (d is null)
        {
            return -EBADF;
        }

        if (min < 0 || min >= FileMaxFds)
        {
            return -EINVAL;
        }

        return f.AddDescriptor(d, min);
    }

    private static long DoDup2(ILinuxProcess p, ILinuxFiles f, int oldFd, int newFd, bool allowSame)
    {
        ILinuxDescriptor? d = p.GetDescriptor(oldFd);
        if (d is null || newFd < 0 || newFd >= FileMaxFds)
        {
            return -EBADF;
        }

        if (oldFd == newFd)
        {
            return allowSame ? newFd : -EINVAL;
        }

        // Wie Linux: newfd wird still geschlossen; danach ist es die kleinste freie Nummer ab newfd.
        ILinuxDescriptor? old = p.GetDescriptor(newFd);
        if (old is not null && f.RemoveDescriptor(newFd))
        {
            _ = ReleaseIfUnused(p, old); // dup2 verwirft den Fehler des stillen close wie Linux
        }

        return f.AddDescriptor(d, newFd);
    }

    private static long DoFcntl(ILinuxProcess p, ILinuxFiles f, int fd, int cmd, ulong arg)
    {
        if (p.GetDescriptor(fd) is null)
        {
            return -EBADF;
        }

        switch (cmd)
        {
            case FdDupfd:
                return DoDup(p, f, fd, (int)arg);
            case FdDupfdCloexec:
                return WithCloseOnExec(f, DoDup(p, f, fd, (int)arg), true);
            case FdGetfd:
                // V15: FD_CLOEXEC (Bit 0) gilt bei execve; ohne Flagtabelle (Hosttests mit Ersatzprozessen) wie bisher 0.
                return f is ILinuxFdFlags getFlags && getFlags.GetCloseOnExec(fd) ? FdCloexec : 0;
            case FdSetfd:
                MarkCloseOnExec(f, fd, ((int)arg & FdCloexec) != 0); // Linux kennt nur dieses Bit, der Rest wird ignoriert
                return 0;
            case FdAddSeals:
                // V17: Siegel gibt es nur auf memfd (Linux: -EINVAL für andere Dateien).
                return p.GetDescriptor(fd) is LinuxMemFile toSeal ? toSeal.AddSeals((int)arg) : -EINVAL;
            case FdGetSeals:
                return p.GetDescriptor(fd) is LinuxMemFile sealedFile ? sealedFile.Seals : -EINVAL;
            case FdGetfl when p.GetDescriptor(fd) is ILinuxStatusFlags flagged:
                return flagged.StatusFlags;
            case FdSetfl when p.GetDescriptor(fd) is ILinuxStatusFlags flagged:
                // Wie Linux ändert F_SETFL nur O_NONBLOCK (und O_APPEND/O_ASYNC/O_DIRECT, die Pipe und Socket nicht brauchen); der Rest
                // wird ignoriert. V16: gilt für Pipe-Enden und Unix-Sockets.
                flagged.NonBlocking = ((int)arg & LinuxPipeEnd.ONonblock) != 0;
                return 0;
            case FdGetfl:
                // Die Öffnungsflags liegen in der Datei und sind über ILinuxFile nicht sichtbar; O_RDWR ist für die Laufzeiten
                // (stdio-Puffer, isatty-Wege) die unschädliche Antwort. Abweichung in LINUX_ABI.md.
                return 2;
            case FdSetfl:
                return 0; // O_NONBLOCK/O_APPEND nachträglich: nicht umgesetzt, still angenommen (LINUX_ABI.md)
            default:
                return -EINVAL;
        }
    }

    /// <summary>
    /// pipe/pipe2 (V13): legt eine Pipe im Prozess an und schreibt die Nummern {lesen, schreiben} nach <paramref name="fdsPtr"/>.
    /// O_CLOEXEC setzt FD_CLOEXEC auf beiden Nummern (V15); O_NONBLOCK gilt für beide Enden.
    /// Andere Flags (O_DIRECT, Paketmodus) -EINVAL. Scheitert das Zurückschreiben, sind beide Nummern wieder frei (wie Linux).
    /// </summary>
    private static long DoPipe2(ILinuxProcess p, ILinuxFiles f, ulong fdsPtr, int flags)
    {
        if ((flags & ~(FileOCloexec | LinuxPipeEnd.ONonblock)) != 0)
        {
            return -EINVAL;
        }

        var (reader, writer) = LinuxPipe.Create(p as ILinuxAbort, (flags & LinuxPipeEnd.ONonblock) != 0);
        int rfd = f.AddDescriptor(reader, 0);
        if (rfd < 0)
        {
            return rfd;
        }

        int wfd = f.AddDescriptor(writer, 0);
        if (wfd < 0)
        {
            f.RemoveDescriptor(rfd);
            return wfd;
        }

        Span<byte> b = stackalloc byte[8];
        BitConverter.TryWriteBytes(b.Slice(0, 4), rfd);
        BitConverter.TryWriteBytes(b.Slice(4, 4), wfd);
        if (!p.Memory.TryWrite(fdsPtr, b))
        {
            f.RemoveDescriptor(rfd);
            f.RemoveDescriptor(wfd);
            return -EFAULT;
        }

        bool cloexec = (flags & FileOCloexec) != 0;
        MarkCloseOnExec(f, rfd, cloexec);
        MarkCloseOnExec(f, wfd, cloexec);
        return 0;
    }

    private const uint MfdCloexec = 1, MfdAllowSealing = 2, MfdHugetlb = 4, MfdNoexecSeal = 8, MfdExec = 0x10;

    /// <summary>
    /// memfd_create (V17): namenlose Datei im Arbeitsspeicher zum Teilen per MAP_SHARED. Name höchstens 249 Bytes (Linux: 255 mit Präfix
    /// „memfd:“), sonst -EINVAL; MFD_HUGETLB gibt es nicht (-EINVAL). MFD_NOEXEC_SEAL setzt F_SEAL_EXEC und erlaubt Siegel wie Linux 6.3.
    /// </summary>
    private static long DoMemfdCreate(ILinuxProcess p, ILinuxFiles f, ulong namePtr, uint flags)
    {
        if ((flags & ~(MfdCloexec | MfdAllowSealing | MfdNoexecSeal | MfdExec)) != 0 || (flags & MfdHugetlb) != 0
            || (flags & (MfdNoexecSeal | MfdExec)) == (MfdNoexecSeal | MfdExec))
        {
            return -EINVAL;
        }

        if (p is not ILinuxSharedMemory shared)
        {
            return -ENOSYS;
        }

        if (!p.Memory.TryReadCString(namePtr, 249, out string name))
        {
            return p.Memory.TryReadCString(namePtr, 4096, out _) ? -EINVAL : -EFAULT;
        }

        bool sealing = (flags & (MfdAllowSealing | MfdNoexecSeal)) != 0;
        LinuxMemFile file = shared.CreateMemFile(name, sealing);
        if ((flags & MfdNoexecSeal) != 0)
        {
            file.AddSeals(LinuxMemFile.SealExec);
        }

        int fd = f.AddDescriptor(file, 0);
        if (fd < 0)
        {
            file.Close();
            return fd;
        }

        MarkCloseOnExec(f, fd, (flags & MfdCloexec) != 0);
        return fd;
    }

    /// <summary>V15: setzt FD_CLOEXEC der Nummer, wenn der Prozess Flags führt (Hosttests mit Ersatzprozessen führen keine).</summary>
    private static void MarkCloseOnExec(ILinuxFiles f, int fd, bool on)
    {
        if (f is ILinuxFdFlags flags)
        {
            flags.SetCloseOnExec(fd, on);
        }
    }

    /// <summary>Setzt FD_CLOEXEC auf das Ergebnis eines dup-Aufrufs, wenn es eine Nummer ist; Fehler gehen unverändert durch.</summary>
    private static long WithCloseOnExec(ILinuxFiles f, long fd, bool on)
    {
        if (fd >= 0)
        {
            MarkCloseOnExec(f, (int)fd, on);
        }

        return fd;
    }

    private static long DoGetcwd(ILinuxProcess p, ILinuxFiles f, ulong buf, ulong size)
    {
        byte[] cwd = Encoding.UTF8.GetBytes(f.Cwd + "\0");
        if (size < (ulong)cwd.Length)
        {
            return -FileErr.ERANGE;
        }

        // Rückgabe des Systemaufrufs ist die Länge einschließlich NUL (glibc und musl verlassen sich darauf).
        return p.Memory.TryWrite(buf, cwd) ? cwd.Length : -EFAULT;
    }

    private static long DoChdir(ILinuxProcess p, ILinuxFiles f, ulong pathPtr)
    {
        long r = LinuxPath.Resolve(p, f, LinuxPath.AtFdcwd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        r = f.FileSystem.Stat(path, true, out LinuxStat st);
        if (r < 0)
        {
            return r;
        }

        if ((st.Mode & 0xf000) != 0x4000)
        {
            return -FileErr.ENOTDIR;
        }

        r = f.FileSystem.Access(path, 1); // X_OK: Suchrecht wie Linux
        if (r < 0)
        {
            return r;
        }

        f.Cwd = path.Length > 1 ? path.TrimEnd('/') : path;
        return 0;
    }

    private static long DoFchdir(ILinuxProcess p, ILinuxFiles f, int fd)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not ILinuxFile file || !file.IsDirectory)
        {
            return -FileErr.ENOTDIR;
        }

        // Path kann ein abschließendes "/" tragen (open("dir/")); cwd ist wie bei chdir ohne, sonst endet getcwd auf "/".
        string cwd = file.Path;
        f.Cwd = cwd.Length > 1 ? cwd.TrimEnd('/') : cwd;
        return 0;
    }
}
