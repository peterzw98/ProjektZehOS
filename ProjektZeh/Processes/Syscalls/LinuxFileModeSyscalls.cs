namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V28b: chmod/fchmod/fchmodat/fchmodat2 und link/linkat. Die Prozesswurzel liegt auf FAT: Sie kennt weder Rechtebits noch harte Links.
/// chmod prüft deshalb nur, ob das Ziel da ist, und lässt den Modus unverändert (stat meldet weiter den abgeleiteten Modus); link liefert nach
/// den Prüfungen von Linux -EPERM wie vfat (<c>msdos_link</c> fehlt, <c>vfs_link</c>: „Operation not permitted“). fontconfig fällt dann für
/// seine Sperrdatei auf mkdir zurück (<c>FcAtomicLock</c>).
/// </summary>
internal static partial class LinuxSyscalls
{
    private static class ModeNr
    {
        public const long Link = 86, Chmod = 90, Fchmod = 91, Linkat = 265, Fchmodat = 268, Fchmodat2 = 452;
    }

    private const long ModeEPERM = 1, ModeEEXIST = 17, ModeEOPNOTSUPP = 95;
    private const int ModeAtSymlinkFollow = 0x400;

    /// <summary>Ergebnis oder -ENOSYS-Weg: false, wenn die Nummer nicht hierher gehört.</summary>
    private static bool TryDispatchMode(ref SyscallArgs a, ILinuxProcess p, ILinuxFiles f, out long result)
    {
        switch (a.Number)
        {
            case ModeNr.Chmod: result = DoChmod(p, f, MetaAtFdcwd, a.A0, 0); return true;
            case ModeNr.Fchmod: result = p.GetDescriptor((int)a.A0) is null ? -MetaErr.EBADF : 0; return true;
            case ModeNr.Fchmodat: result = DoChmod(p, f, (int)a.A0, a.A1, 0); return true; // ohne Flagargument (Linux 268)
            case ModeNr.Fchmodat2: result = DoChmod(p, f, (int)a.A0, a.A1, (int)a.A3); return true;
            case ModeNr.Link: result = DoLink(p, f, MetaAtFdcwd, a.A0, MetaAtFdcwd, a.A1, 0); return true;
            case ModeNr.Linkat: result = DoLink(p, f, (int)a.A0, a.A1, (int)a.A2, a.A3, (int)a.A4); return true;
            default: result = 0; return false;
        }
    }

    private static long DoChmod(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong pathPtr, int flags)
    {
        if ((flags & ~(MetaAtSymlinkNofollow | MetaAtEmptyPath)) != 0)
        {
            return -MetaErr.EINVAL;
        }

        long r = LinuxPath.Resolve(p, f, dirfd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        bool follow = (flags & MetaAtSymlinkNofollow) == 0;
        r = f.FileSystem.Stat(path, follow, out LinuxStat st);
        if (r < 0)
        {
            return r;
        }

        // Linux (chmod_common über fchmodat2): Symlinks selbst haben keine änderbaren Rechte.
        return !follow && (st.Mode & 0xF000) == 0xA000 ? -ModeEOPNOTSUPP : 0;
    }

    private static long DoLink(ILinuxProcess p, ILinuxFiles f, int oldDir, ulong oldPtr, int newDir, ulong newPtr, int flags)
    {
        if ((flags & ~(ModeAtSymlinkFollow | MetaAtEmptyPath)) != 0)
        {
            return -MetaErr.EINVAL;
        }

        long r = LinuxPath.Resolve(p, f, oldDir, oldPtr, out string oldPath);
        if (r < 0)
        {
            return r;
        }

        r = LinuxPath.Resolve(p, f, newDir, newPtr, out string newPath);
        if (r < 0)
        {
            return r;
        }

        r = f.FileSystem.Stat(oldPath, (flags & ModeAtSymlinkFollow) != 0, out _);
        if (r < 0)
        {
            return r;
        }

        // Ein vorhandenes Ziel und einen fehlenden Elternordner meldet Linux vor der Frage, ob das Dateisystem Links kann (filename_create).
        r = f.FileSystem.Stat(newPath, false, out _);
        if (r == 0)
        {
            return -ModeEEXIST;
        }

        if (r != -MetaErr.ENOENT)
        {
            return r;
        }

        int slash = newPath.LastIndexOf('/');
        r = f.FileSystem.Stat(slash <= 0 ? "/" : newPath.Substring(0, slash), true, out LinuxStat parent);
        if (r < 0)
        {
            return r; // Elternordner fehlt: -ENOENT wie Linux
        }

        return (parent.Mode & 0xF000) != 0x4000 ? -MetaErr.ENOTDIR : -ModeEPERM;
    }
}
