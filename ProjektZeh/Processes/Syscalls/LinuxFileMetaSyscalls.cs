using System;
using System.Text;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Dateiaufrufe ohne Dateiinhalt (V2, Teil Metadaten): stat-Familie, access, getdents64, readlink und die Änderungen am
/// Verzeichnisbaum (unlink, mkdir, rmdir, rename, symlink) samt der *at-Formen. open, read, lseek, close, getcwd, chdir und fcntl
/// liegen in der anderen Hälfte; der Verteiler ruft <see cref="DispatchFileMeta"/> für alles, was er sonst nicht kennt.
/// </summary>
internal static partial class LinuxSyscalls
{
    // Eigene Namen in verschachtelten Typen, damit sie nicht mit den Konstanten der anderen Hälfte zusammenstoßen.
    private static class MetaNr
    {
        public const long Stat = 4, Fstat = 5, Lstat = 6, Access = 21, Rename = 82, Mkdir = 83, Rmdir = 84, Unlink = 87,
            Symlink = 88, Readlink = 89, Getdents64 = 217, Mkdirat = 258, Newfstatat = 262, Unlinkat = 263, Renameat = 264,
            Symlinkat = 266, Readlinkat = 267, Faccessat = 269, Renameat2 = 316, Faccessat2 = 439;
    }

    private static class MetaErr
    {
        public const long ENOENT = 2, EBADF = 9, ENOTDIR = 20, EFAULT = 14, EINVAL = 22, ENOSYS = 38;
    }

    private const int MetaAtFdcwd = -100, MetaAtSymlinkNofollow = 0x100, MetaAtRemovedir = 0x200, MetaAtEmptyPath = 0x1000;
    private const int MetaPathMax = 4095;

    /// <summary>Ergebnis oder -errno; -ENOSYS für Nummern, die nicht hierher gehören.</summary>
    internal static long DispatchFileMeta(ref SyscallArgs a, ILinuxProcess p, ILinuxFiles f)
    {
        switch (a.Number)
        {
            case MetaNr.Stat: return DoStatPath(p, f, MetaAtFdcwd, a.A0, a.A1, 0);
            case MetaNr.Lstat: return DoStatPath(p, f, MetaAtFdcwd, a.A0, a.A1, MetaAtSymlinkNofollow);
            case MetaNr.Fstat: return DoFstat(p, (int)a.A0, a.A1);
            case MetaNr.Newfstatat: return DoStatPath(p, f, (int)a.A0, a.A1, a.A2, (int)a.A3);
            case MetaNr.Access: return DoAccess(p, f, MetaAtFdcwd, a.A0, (int)a.A1);
            case MetaNr.Faccessat:
            case MetaNr.Faccessat2: return DoAccess(p, f, (int)a.A0, a.A1, (int)a.A2); // Flags (AT_EACCESS) ändern bei einem Benutzer nichts
            case MetaNr.Getdents64: return DoGetdents(p, (int)a.A0, a.A1, a.A2);
            case MetaNr.Readlink: return DoReadlink(p, f, MetaAtFdcwd, a.A0, a.A1, a.A2);
            case MetaNr.Readlinkat: return DoReadlink(p, f, (int)a.A0, a.A1, a.A2, a.A3);
            case MetaNr.Unlink: return DoUnlink(p, f, MetaAtFdcwd, a.A0, 0);
            case MetaNr.Unlinkat: return DoUnlink(p, f, (int)a.A0, a.A1, (int)a.A2);
            case MetaNr.Rmdir: return DoUnlink(p, f, MetaAtFdcwd, a.A0, MetaAtRemovedir);
            case MetaNr.Mkdir: return DoMkdir(p, f, MetaAtFdcwd, a.A0, (uint)a.A1);
            case MetaNr.Mkdirat: return DoMkdir(p, f, (int)a.A0, a.A1, (uint)a.A2);
            case MetaNr.Rename: return DoRename(p, f, MetaAtFdcwd, a.A0, MetaAtFdcwd, a.A1);
            case MetaNr.Renameat: return DoRename(p, f, (int)a.A0, a.A1, (int)a.A2, a.A3);
            case MetaNr.Renameat2: return (uint)a.A4 != 0 ? -MetaErr.EINVAL : DoRename(p, f, (int)a.A0, a.A1, (int)a.A2, a.A3); // NOREPLACE/EXCHANGE nicht umgesetzt
            case MetaNr.Symlink: return DoSymlink(p, f, a.A0, MetaAtFdcwd, a.A1);
            case MetaNr.Symlinkat: return DoSymlink(p, f, a.A0, (int)a.A1, a.A2);
            default:
                // V28b: chmod und link (FAT ohne Rechtebits und harte Links); V20: eventfd, statfs, inotify; sonst -ENOSYS
                return TryDispatchMode(ref a, p, f, out long moded) ? moded : DispatchSoft(ref a, p, f);
        }
    }

    private static long WriteStat(ILinuxProcess p, ulong buf, in LinuxStat st)
    {
        // struct stat, x86-64: 144 Bytes; nlink ist 64 Bit breit, hinter mode/uid/gid steht ein Füllfeld.
        byte[] b = new byte[144];
        BitConverter.TryWriteBytes(b.AsSpan(0, 8), st.Dev);
        BitConverter.TryWriteBytes(b.AsSpan(8, 8), st.Ino);
        BitConverter.TryWriteBytes(b.AsSpan(16, 8), (ulong)st.Nlink);
        BitConverter.TryWriteBytes(b.AsSpan(24, 4), st.Mode);
        BitConverter.TryWriteBytes(b.AsSpan(28, 4), st.Uid);
        BitConverter.TryWriteBytes(b.AsSpan(32, 4), st.Gid);
        BitConverter.TryWriteBytes(b.AsSpan(40, 8), st.Rdev);
        BitConverter.TryWriteBytes(b.AsSpan(48, 8), st.Size);
        BitConverter.TryWriteBytes(b.AsSpan(56, 8), st.BlockSize);
        BitConverter.TryWriteBytes(b.AsSpan(64, 8), st.Blocks);
        PutTime(b, 72, st.AtimeNanos);
        PutTime(b, 88, st.MtimeNanos);
        PutTime(b, 104, st.CtimeNanos);
        return p.Memory.TryWrite(buf, b) ? 0 : -MetaErr.EFAULT;
    }

    private static void PutTime(byte[] b, int at, long nanos)
    {
        long sec = nanos / 1_000_000_000L, rest = nanos % 1_000_000_000L;
        if (rest < 0)
        {
            sec--;
            rest += 1_000_000_000L;
        }

        BitConverter.TryWriteBytes(b.AsSpan(at, 8), sec);
        BitConverter.TryWriteBytes(b.AsSpan(at + 8, 8), rest);
    }

    private static long DoFstat(ILinuxProcess p, int fd, ulong buf)
    {
        var d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -MetaErr.EBADF;
        }

        LinuxStat st;
        if (d is ILinuxFile file)
        {
            long r = file.Stat(out st);
            if (r < 0)
            {
                return r;
            }
        }
        else if (d is LinuxUnixSocket)
        {
            // V16: Socket (S_IFSOCK, 0777 wie Linux bei socketpair), Größe 0.
            st = new LinuxStat { Mode = 0xC000 | 0x1FF, Nlink = 1, BlockSize = 4096 };
        }
        else if (d is ILinuxAnonFile)
        {
            // V20: eventfd und inotify sind bei Linux anon_inode-Dateien: Modus 0600 ohne Dateityp, Größe 0.
            st = new LinuxStat { Mode = 0x180, Nlink = 1, BlockSize = 4096 };
        }
        else if (d is LinuxPipeEnd)
        {
            // V13: Pipe (S_IFIFO, 0600); Größe 0 wie bei Linux, Blockgröße eine Seite.
            st = new LinuxStat { Mode = 0x1000 | 0x180, Nlink = 1, BlockSize = 4096 };
        }
        else
        {
            // Terminal und andere Deskriptoren ohne Datei: Zeichengerät (C-Laufzeiten fragen damit "ist das ein Terminal?" und die Puffergröße).
            st = new LinuxStat { Mode = 0x2000 | 0x190, Nlink = 1, Rdev = 0x8800 /* 136:0 wie pts/0 */, BlockSize = 1024 };
        }

        return WriteStat(p, buf, st);
    }

    private static long DoStatPath(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong pathPtr, ulong buf, int flags)
    {
        if ((flags & ~(MetaAtSymlinkNofollow | MetaAtEmptyPath | 0x800 /* AT_NO_AUTOMOUNT */)) != 0)
        {
            return -MetaErr.EINVAL;
        }

        if ((flags & MetaAtEmptyPath) != 0 && p.Memory.TryReadCString(pathPtr, 1, out string probe) && probe.Length == 0)
        {
            if (dirfd != MetaAtFdcwd)
            {
                return DoFstat(p, dirfd, buf); // leerer Pfad: der Deskriptor selbst
            }

            // AT_FDCWD ist kein Deskriptor: Linux liefert dann das Arbeitsverzeichnis.
            long rc = f.FileSystem.Stat(f.Cwd, true, out LinuxStat cst);
            return rc < 0 ? rc : WriteStat(p, buf, cst);
        }

        long r = LinuxPath.Resolve(p, f, dirfd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        r = f.FileSystem.Stat(path, (flags & MetaAtSymlinkNofollow) == 0, out LinuxStat st);
        return r < 0 ? r : WriteStat(p, buf, st);
    }

    private static long DoAccess(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong pathPtr, int mode)
    {
        if ((mode & ~7) != 0)
        {
            return -MetaErr.EINVAL;
        }

        long r = LinuxPath.Resolve(p, f, dirfd, pathPtr, out string path);
        return r < 0 ? r : f.FileSystem.Access(path, mode);
    }

    private static long DoGetdents(ILinuxProcess p, int fd, ulong buf, ulong count)
    {
        if (p.GetDescriptor(fd) is not ILinuxFile file)
        {
            return -MetaErr.EBADF;
        }

        if (!file.IsDirectory)
        {
            return -MetaErr.ENOTDIR;
        }

        int cap = (int)Math.Min(count, (ulong)MaxRw);
        byte[] outb = new byte[Math.Min(cap, 65536)];
        int used = 0;
        long index = file.DirIndex;
        while (true)
        {
            long r = file.ReadDir(index, out LinuxDirEntry e);
            if (r < 0)
            {
                return used > 0 ? used : r;
            }

            if (r > 0)
            {
                break; // Ende des Verzeichnisses
            }

            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            int reclen = (19 + name.Length + 1 + 7) & ~7; // ino 8, off 8, reclen 2, type 1, Name mit NUL, auf 8 aufgefüllt
            if (used + reclen > outb.Length)
            {
                if (used == 0)
                {
                    return -MetaErr.EINVAL; // der erste Eintrag passt nicht in den Puffer
                }

                break;
            }

            BitConverter.TryWriteBytes(outb.AsSpan(used, 8), e.Ino);
            BitConverter.TryWriteBytes(outb.AsSpan(used + 8, 8), index + 1);
            BitConverter.TryWriteBytes(outb.AsSpan(used + 16, 2), (ushort)reclen);
            outb[used + 18] = e.Type;
            name.CopyTo(outb, used + 19);
            used += reclen;
            index++;
        }

        if (used > 0 && !p.Memory.TryWrite(buf, outb.AsSpan(0, used)))
        {
            return -MetaErr.EFAULT; // Position bleibt, nichts ist verbraucht
        }

        file.DirIndex = index;
        return used;
    }

    private static long DoReadlink(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong pathPtr, ulong buf, ulong size)
    {
        if ((long)size <= 0)
        {
            return -MetaErr.EINVAL;
        }

        long r = LinuxPath.Resolve(p, f, dirfd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        string target;
        if (IsProcSelfExe(p, path))
        {
            // V12: ein /proc gibt es nicht; nur dieser eine Link wird nachgebildet (Ladybird sucht damit seine ICU-Daten neben dem
            // Programm). Liegt das Programm außerhalb der Prozesswurzel, fehlt er wie ein nicht vorhandener Pfad.
            string? exe = (p as ILinuxExe)?.ExePath;
            if (exe is null)
            {
                return -MetaErr.ENOENT;
            }

            target = exe;
        }
        else
        {
            r = f.FileSystem.ReadLink(path, out target);
            if (r < 0)
            {
                return r;
            }
        }

        byte[] bytes = Encoding.UTF8.GetBytes(target);
        int n = (int)Math.Min((ulong)bytes.Length, Math.Min(size, (ulong)MaxRw)); // abgeschnitten, ohne NUL (wie Linux)
        return p.Memory.TryWrite(buf, bytes.AsSpan(0, n)) ? n : -MetaErr.EFAULT;
    }

    /// <summary>"/proc/self/exe" oder "/proc/&lt;eigene PID&gt;/exe" (normalisierter Pfad).</summary>
    private static bool IsProcSelfExe(ILinuxProcess p, string path)
    {
        const string Prefix = "/proc/", Suffix = "/exe";
        if (!path.StartsWith(Prefix, StringComparison.Ordinal) || !path.EndsWith(Suffix, StringComparison.Ordinal)
            || path.Length <= Prefix.Length + Suffix.Length)
        {
            return false;
        }

        string who = path.Substring(Prefix.Length, path.Length - Prefix.Length - Suffix.Length);
        return who == "self" || who == p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long DoUnlink(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong pathPtr, int flags)
    {
        if ((flags & ~MetaAtRemovedir) != 0)
        {
            return -MetaErr.EINVAL;
        }

        long r = LinuxPath.Resolve(p, f, dirfd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        if ((flags & MetaAtRemovedir) != 0)
        {
            return f.FileSystem.Rmdir(path);
        }

        r = f.FileSystem.Unlink(path);
        if (r == 0 && p is ILinuxUnixNames named)
        {
            named.UnixNames.Unlinked(path); // V16: unlink löst einen Socketnamen wie bei Linux
        }

        return r;
    }

    private static long DoMkdir(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong pathPtr, uint mode)
    {
        long r = LinuxPath.Resolve(p, f, dirfd, pathPtr, out string path);
        return r < 0 ? r : f.FileSystem.Mkdir(path, mode & 0x1ff);
    }

    private static long DoRename(ILinuxProcess p, ILinuxFiles f, int oldDirfd, ulong oldPtr, int newDirfd, ulong newPtr)
    {
        long r = LinuxPath.Resolve(p, f, oldDirfd, oldPtr, out string from);
        if (r < 0)
        {
            return r;
        }

        r = LinuxPath.Resolve(p, f, newDirfd, newPtr, out string to);
        if (r < 0)
        {
            return r;
        }

        r = f.FileSystem.Rename(from, to);
        if (r == 0 && p is ILinuxUnixNames named)
        {
            named.UnixNames.Renamed(from, to); // V16: ein umbenannter Socket ist unter dem neuen Pfad erreichbar
        }

        return r;
    }

    private static long DoSymlink(ILinuxProcess p, ILinuxFiles f, ulong targetPtr, int dirfd, ulong linkPtr)
    {
        // Das Ziel ist ein beliebiger Text (auch relativ, auch ins Leere) und wird nicht aufgelöst.
        if (!p.Memory.TryReadCString(targetPtr, MetaPathMax, out string target))
        {
            return -MetaErr.EFAULT;
        }

        if (target.Length == 0)
        {
            return -MetaErr.ENOENT;
        }

        long r = LinuxPath.Resolve(p, f, dirfd, linkPtr, out string link);
        return r < 0 ? r : f.FileSystem.Symlink(target, link);
    }
}
