using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>Angaben für statfs (V20): Art und Größe des Dateisystems der Prozesswurzel.</summary>
internal readonly struct LinuxFsStats(long type, long blockSize, long blocks, long freeBlocks, long files, long freeFiles)
{
    public long Type { get; } = type; // f_type (*_SUPER_MAGIC)
    public long BlockSize { get; } = blockSize;
    public long Blocks { get; } = blocks;
    public long FreeBlocks { get; } = freeBlocks;
    public long Files { get; } = files;
    public long FreeFiles { get; } = freeFiles;
}

/// <summary>Optional neben <see cref="ILinuxFileSystem"/>: Größe und Art für statfs; ohne sie meldet statfs ein leeres tmpfs.</summary>
internal interface ILinuxFsStats
{
    LinuxFsStats FsStats { get; }
}

/// <summary>
/// V20 „weiche Lücken“ aus der Lesestudie zum Ladybird-Startweg (docs/superpowers/specs/2026-11-27-ladybird-636-vorlauf.md, Punkte 4, 5, 9):
/// eventfd/eventfd2, statfs/fstatfs, inotify_init/inotify_init1/inotify_add_watch/inotify_rm_watch. Läuft unter der Dateisperre
/// (<see cref="DispatchFileMeta"/> gibt Unbekanntes hierher); Lesen und Warten auf eventfd und inotify laufen wie bei Pipes ohne sie
/// (<see cref="ILinuxInput"/>, <see cref="ILinuxPollable"/>).
/// </summary>
internal static partial class LinuxSyscalls
{
    private static class SoftNr
    {
        public const long Statfs = 137, Fstatfs = 138, InotifyInit = 253, InotifyAddWatch = 254, InotifyRmWatch = 255, Eventfd = 284, Eventfd2 = 290,
            InotifyInit1 = 294;
    }

    private const int SoftOCloexec = 0x80000, SoftONonblock = 0x800;

    // f_type wie Linux (include/uapi/linux/magic.h).
    internal const long MsdosSuperMagic = 0x4d44, TmpfsMagic = 0x01021994, PipefsMagic = 0x50495045, SockfsMagic = 0x534F434B,
        AnonInodeFsMagic = 0x09041934, DevptsSuperMagic = 0x1cd1;

    // inotify-Masken: alle Ereignisse und die Steuerbits, die add_watch kennt.
    private const uint InAllEvents = 0xfff, InOnlydir = 0x01000000, InDontFollow = 0x02000000, InExclUnlink = 0x04000000, InMaskCreate = 0x10000000,
        InMaskAdd = 0x20000000, InOneshot = 0x80000000;

    private const uint InAllBits = InAllEvents | 0x2000 /* IN_UNMOUNT */ | 0x4000 /* IN_Q_OVERFLOW */ | LinuxInotify.InIgnored | InOnlydir | InDontFollow
        | InExclUnlink | InMaskCreate | InMaskAdd | 0x40000000 /* IN_ISDIR */ | InOneshot;

    private static long DispatchSoft(ref SyscallArgs a, ILinuxProcess p, ILinuxFiles f)
    {
        switch (a.Number)
        {
            case SoftNr.Eventfd: return DoEventfd(f, (uint)a.A0, 0);
            case SoftNr.Eventfd2: return DoEventfd(f, (uint)a.A0, (int)a.A1);
            case SoftNr.Statfs: return DoStatfs(p, f, a.A0, a.A1);
            case SoftNr.Fstatfs: return DoFstatfs(p, f, (int)a.A0, a.A1);
            case SoftNr.InotifyInit: return DoInotifyInit(f, 0);
            case SoftNr.InotifyInit1: return DoInotifyInit(f, (int)a.A0);
            case SoftNr.InotifyAddWatch: return DoInotifyAddWatch(p, f, (int)a.A0, a.A1, (uint)a.A2);
            case SoftNr.InotifyRmWatch: return DoInotifyRmWatch(p, (int)a.A0, (int)a.A1);
            case EpollCreate: return DoEpollCreate(f, (int)a.A0); // V22
            case EpollCreate1: return DoEpollCreate1(f, (int)a.A0);
            case EpollCtl: return DoEpollCtl(p, (int)a.A0, (int)a.A1, (int)a.A2, a.A3);
            case TimerfdCreate: return DoTimerfdCreate(p, f, (int)a.A0, (int)a.A1); // V22b
            case TimerfdSettime: return DoTimerfdSettime(p, (int)a.A0, (int)a.A1, a.A2, a.A3);
            case TimerfdGettime: return DoTimerfdGettime(p, (int)a.A0, a.A1);
            default: return -ENOSYS;
        }
    }

    /// <summary>eventfd2: Flags EFD_CLOEXEC, EFD_NONBLOCK, EFD_SEMAPHORE, sonst -EINVAL (eventfd ohne Flags).</summary>
    private static long DoEventfd(ILinuxFiles f, uint initial, int flags)
    {
        if ((flags & ~(SoftOCloexec | SoftONonblock | LinuxEventFd.EfdSemaphore)) != 0)
        {
            return -EINVAL;
        }

        var e = new LinuxEventFd(initial, (flags & LinuxEventFd.EfdSemaphore) != 0, (flags & SoftONonblock) != 0);
        return AddAnon(f, e, (flags & SoftOCloexec) != 0);
    }

    /// <summary>inotify_init1: Flags IN_CLOEXEC, IN_NONBLOCK, sonst -EINVAL.</summary>
    private static long DoInotifyInit(ILinuxFiles f, int flags)
    {
        if ((flags & ~(SoftOCloexec | SoftONonblock)) != 0)
        {
            return -EINVAL;
        }

        return AddAnon(f, new LinuxInotify((flags & SoftONonblock) != 0), (flags & SoftOCloexec) != 0);
    }

    private static long AddAnon(ILinuxFiles f, ILinuxAnonFile d, bool cloexec)
    {
        int fd = f.AddDescriptor(d, 0);
        if (fd < 0)
        {
            d.Close(); // -EMFILE: ohne Nummer wäre das Objekt nie geschlossen
            return fd;
        }

        MarkCloseOnExec(f, fd, cloexec);
        return fd;
    }

    /// <summary>
    /// inotify_add_watch in der Reihenfolge von Linux: Maske ohne bekannte Bits oder IN_MASK_ADD mit IN_MASK_CREATE -EINVAL, dann fd
    /// (-EBADF, kein inotify -EINVAL), dann Pfad (Fehler der Auflösung, IN_ONLYDIR ohne Verzeichnis -ENOTDIR, ohne Leserecht -EACCES).
    /// </summary>
    private static long DoInotifyAddWatch(ILinuxProcess p, ILinuxFiles f, int fd, ulong pathPtr, uint mask)
    {
        if ((mask & InAllBits) == 0 || ((mask & InMaskAdd) != 0 && (mask & InMaskCreate) != 0))
        {
            return -EINVAL;
        }

        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not LinuxInotify inotify)
        {
            return -EINVAL;
        }

        long r = LinuxPath.Resolve(p, f, LinuxPath.AtFdcwd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        r = f.FileSystem.Stat(path, (mask & InDontFollow) == 0, out LinuxStat st);
        if (r < 0)
        {
            return r;
        }

        if ((mask & InOnlydir) != 0 && (st.Mode & 0xF000) != 0x4000)
        {
            return -MetaErr.ENOTDIR;
        }

        r = f.FileSystem.Access(path, 4 /* R_OK */);
        if (r < 0)
        {
            return r;
        }

        // Dieselbe Datei unter zwei Namen ergibt bei Linux denselben Watch (Inode); ohne Inodenummer zählt der aufgelöste Pfad.
        string key = st.Ino != 0 ? "i" + st.Dev + ":" + st.Ino : "p" + path;
        return inotify.AddWatch(key, mask & ~(InMaskAdd | InMaskCreate), (mask & InMaskAdd) != 0, (mask & InMaskCreate) != 0);
    }

    private static long DoInotifyRmWatch(ILinuxProcess p, int fd, int wd)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        return d is LinuxInotify inotify ? inotify.RemoveWatch(wd) : -EINVAL;
    }

    private static long DoStatfs(ILinuxProcess p, ILinuxFiles f, ulong pathPtr, ulong buf)
    {
        long r = LinuxPath.Resolve(p, f, LinuxPath.AtFdcwd, pathPtr, out string path);
        if (r < 0)
        {
            return r;
        }

        r = f.FileSystem.Stat(path, true, out _);
        return r < 0 ? r : WriteStatfs(p, buf, RootFsStats(f));
    }

    /// <summary>fstatfs: Dateien wie statfs; Pipes, Sockets, eventfd/inotify und das Terminal melden ihr Pseudodateisystem wie Linux.</summary>
    private static long DoFstatfs(ILinuxProcess p, ILinuxFiles f, int fd, ulong buf)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        LinuxFsStats st;
        switch (d)
        {
            case null: return -EBADF;
            case ILinuxFile: st = RootFsStats(f); break;
            case LinuxPipeEnd: st = new LinuxFsStats(PipefsMagic, 4096, 0, 0, 0, 0); break;
            case LinuxUnixSocket: st = new LinuxFsStats(SockfsMagic, 4096, 0, 0, 0, 0); break;
            case ILinuxAnonFile: st = new LinuxFsStats(AnonInodeFsMagic, 4096, 0, 0, 0, 0); break;
            default: st = new LinuxFsStats(DevptsSuperMagic, 4096, 0, 0, 0, 0); break; // Terminal (fstat: Zeichengerät 136:0)
        }

        return WriteStatfs(p, buf, st);
    }

    private static LinuxFsStats RootFsStats(ILinuxFiles f) =>
        f.FileSystem is ILinuxFsStats stats ? stats.FsStats : new LinuxFsStats(TmpfsMagic, 4096, 0, 0, 0, 0);

    /// <summary>struct statfs, x86-64: 120 Bytes (elf 64-Bit-Felder, dann f_spare[4]); f_fsid 0, f_namelen 255, f_flags ST_VALID.</summary>
    private static long WriteStatfs(ILinuxProcess p, ulong buf, in LinuxFsStats st)
    {
        Span<byte> b = stackalloc byte[120];
        b.Clear();
        BitConverter.TryWriteBytes(b.Slice(0, 8), st.Type);
        BitConverter.TryWriteBytes(b.Slice(8, 8), st.BlockSize); // f_bsize
        BitConverter.TryWriteBytes(b.Slice(16, 8), st.Blocks);
        BitConverter.TryWriteBytes(b.Slice(24, 8), st.FreeBlocks); // f_bfree
        BitConverter.TryWriteBytes(b.Slice(32, 8), st.FreeBlocks); // f_bavail: keine Reserve für root
        BitConverter.TryWriteBytes(b.Slice(40, 8), st.Files);
        BitConverter.TryWriteBytes(b.Slice(48, 8), st.FreeFiles);
        BitConverter.TryWriteBytes(b.Slice(64, 8), 255L); // f_namelen (V7: Teil bis 255 Bytes)
        BitConverter.TryWriteBytes(b.Slice(72, 8), st.BlockSize); // f_frsize
        BitConverter.TryWriteBytes(b.Slice(80, 8), 0x20L); // f_flags: ST_VALID
        return p.Memory.TryWrite(buf, b) ? 0 : -EFAULT;
    }
}
