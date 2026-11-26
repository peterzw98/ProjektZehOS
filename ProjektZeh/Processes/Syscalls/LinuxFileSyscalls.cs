using System;
using System.Text;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Dateiaufrufe mit Dateiinhalt und Deskriptoren (V2, Teil Inhalt): open/openat, close, read/readv/pread64, lseek, dup/dup2/dup3,
/// fcntl, getcwd, chdir/fchdir. Alles Pfadbezogene geht über <see cref="LinuxPath.Resolve(ILinuxProcess, ILinuxFiles, int, ulong, out string)"/>.
/// Nur aktiv, wenn der Prozess <see cref="ILinuxFiles"/> anbietet; was hier nicht bekannt ist, geht an <see cref="DispatchFileMeta"/>.
/// </summary>
internal static partial class LinuxSyscalls
{
    private static class FileNr
    {
        public const long Read = 0, Open = 2, Close = 3, Lseek = 8, Pread64 = 17, Readv = 19, Dup = 32, Dup2 = 33, Fcntl = 72,
            Getcwd = 79, Chdir = 80, Fchdir = 81, Openat = 257, Dup3 = 292;
    }

    private static class FileErr
    {
        public const long EBADF = 9, EFAULT = 14, ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ESPIPE = 29, ERANGE = 34, EIO = 5;
    }

    // Obergrenze der Deskriptornummern (RLIMIT_NOFILE wie poll); close sucht darin nach weiteren Verweisen auf dieselbe Datei.
    private const int FileMaxFds = 1024;
    private const int FileOCloexec = 0x80000;
    private const int FdDupfd = 0, FdGetfd = 1, FdSetfd = 2, FdGetfl = 3, FdSetfl = 4, FdDupfdCloexec = 1030;
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
            case FileNr.Lseek: return DoLseek(p, (int)a.A0, unchecked((long)a.A1), (int)a.A2);
            case FileNr.Dup: return DoDup(p, f, (int)a.A0, 0);
            case FileNr.Dup2: return DoDup2(p, f, (int)a.A0, (int)a.A1, allowSame: true);
            case FileNr.Dup3:
                if (((int)a.A2 & ~FileOCloexec) != 0 || (int)a.A0 == (int)a.A1)
                {
                    return -FileErr.EINVAL;
                }

                return DoDup2(p, f, (int)a.A0, (int)a.A1, allowSame: false);
            case FileNr.Fcntl: return DoFcntl(p, f, (int)a.A0, (int)a.A1, a.A2);
            case FileNr.Getcwd: return DoGetcwd(p, f, a.A0, a.A1);
            case FileNr.Chdir: return DoChdir(p, f, a.A0);
            case FileNr.Fchdir: return DoFchdir(p, f, (int)a.A0);
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
    /// Schließt eine Datei erst, wenn keine Nummer mehr auf sie zeigt (dup teilt die offene Datei wie bei Linux).
    /// Die Tabelle hat keine Aufzählung; FileMaxFds Abfragen je close sind billig gegenüber einer Datei-I/O.
    /// </summary>
    private static long ReleaseIfUnused(ILinuxProcess p, ILinuxDescriptor d)
    {
        if (d is not ILinuxFile file)
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

        return file.Close();
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

        // pread lässt die Position unverändert. Über Seek nachgebildet: Ein zweiter Thread desselben Prozesses könnte dazwischen
        // lesen; Linux-Prozesse laufen hier bisher mit einem Thread (V3), das steht in LINUX_ABI.md.
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
            case FdDupfdCloexec:
                return DoDup(p, f, fd, (int)arg);
            case FdGetfd:
                return 0; // FD_CLOEXEC wird nicht geführt: es gibt noch kein execve
            case FdSetfd:
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
