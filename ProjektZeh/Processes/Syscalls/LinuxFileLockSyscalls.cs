using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V28b: fcntl F_GETLK/F_SETLK/F_SETLKW und F_OFD_GETLK/F_OFD_SETLK/F_OFD_SETLKW über <see cref="LinuxFileLocks"/>. Anlass: sqlite (Ladybird-Profil
/// <c>Ladybird.db</c>) bricht ohne F_SETLK mit „disk I/O error“ ab.
/// </summary>
internal static partial class LinuxSyscalls
{
    // struct flock, x86-64: l_type (short), l_whence (short), 4 Bytes Füllung, l_start, l_len (off_t), l_pid (int), 4 Bytes Füllung.
    private const int FlockSize = 32;

    /// <summary>Wartezeit zwischen zwei Versuchen von F_SETLKW; gehalten wird dabei keine Sperre.</summary>
    private const long LockRetryNanos = 1_000_000;

    /// <summary>true für F_SETLKW und F_OFD_SETLKW: Der Verteiler ruft dann <see cref="DoFcntlLockWait"/> statt der groben Dateisperre.</summary>
    private static bool IsWaitingLock(ref SyscallArgs a) =>
        a.Number == FileNr.Fcntl && (int)a.A1 is FdSetlkw or FdOfdSetlkw;

    /// <summary>
    /// F_SETLKW: versucht unter FileGate zu sperren; steht ein anderer Besitzer im Weg, wartet der Aufruf außerhalb der Sperre und versucht
    /// es erneut, bis es gelingt, ein Signal mit Handler kommt (-EINTR) oder das Programm abgebrochen wird. Eine Verklemmung zweier Prozesse
    /// erkennt ZehOS nicht (Linux: -EDEADLK für POSIX-Sperren); sie endet erst mit Signal oder Abbruch (LINUX_ABI.md).
    /// </summary>
    private static long DoFcntlLockWait(ref SyscallArgs a, ILinuxProcess p, ILinuxFiles f)
    {
        while (true)
        {
            long r;
            lock (f.FileGate)
            {
                r = p.GetDescriptor((int)a.A0) is null ? -EBADF : DoFcntlLock(p, (int)a.A0, (int)a.A1, a.A2);
            }

            if (r != -EAGAIN)
            {
                return r;
            }

            if (LinuxInterrupt.Check(p as ILinuxAbort))
            {
                return -EINTR;
            }

            if (p is not ILinuxHost host)
            {
                return -FileENOLCK; // ohne Schlafen kein Warten (Hosttests mit Ersatzprozess)
            }

            host.SleepNanos(LockRetryNanos);
        }
    }

    /// <summary>
    /// Ein Sperrbefehl mit gehaltener FileGate. Setzen ohne Erfolg liefert -EAGAIN (Linux für F_SETLK; der Wartende versucht es erneut).
    /// Abweichung: Linux verlangt für F_RDLCK eine zum Lesen und für F_WRLCK eine zum Schreiben offene Datei (-EBADF); die Öffnungsart ist
    /// über <see cref="ILinuxFile"/> nicht sichtbar, ZehOS prüft sie nicht.
    /// </summary>
    private static long DoFcntlLock(ILinuxProcess p, int fd, int cmd, ulong arg)
    {
        if (p.GetDescriptor(fd) is not ILinuxFile file)
        {
            return -EINVAL; // Pipes, Sockets, Terminal: ohne Dateikennung keine Sperre (Abweichung, LINUX_ABI.md)
        }

        if (p is not ILinuxFileLocking locking)
        {
            return -FileENOLCK;
        }

        Span<byte> b = stackalloc byte[FlockSize];
        if (!p.Memory.TryRead(arg, b))
        {
            return -EFAULT;
        }

        int type = BitConverter.ToInt16(b.Slice(0, 2));
        int whence = BitConverter.ToInt16(b.Slice(2, 2));
        long start = BitConverter.ToInt64(b.Slice(8, 8));
        long len = BitConverter.ToInt64(b.Slice(16, 8));
        int pid = BitConverter.ToInt32(b.Slice(24, 4));
        bool ofd = cmd is FdOfdGetlk or FdOfdSetlk or FdOfdSetlkw;
        bool get = cmd is FdGetlk or FdOfdGetlk;

        // Linux (fcntl_getlk/fcntl_setlk): OFD-Befehle verlangen l_pid 0; F_GETLK fragt nur nach Lese- oder Schreibsperren.
        if ((ofd && pid != 0) || type is < LinuxFileLocks.Read or > LinuxFileLocks.Unlock || (get && type == LinuxFileLocks.Unlock))
        {
            return -EINVAL;
        }

        long r = file.Stat(out LinuxStat st);
        if (r < 0)
        {
            return r;
        }

        r = LockRange(file, st.Size, whence, start, len, out long first, out long last);
        if (r < 0)
        {
            return r;
        }

        object? owner = ofd ? file : null;
        if (!get)
        {
            return locking.FileLocks.TrySet(st.Dev, st.Ino, locking.LockOwner, owner, type, first, last) ? 0 : -EAGAIN;
        }

        LinuxFileLocks.Range? conflict = locking.FileLocks.FindConflict(st.Dev, st.Ino, locking.LockOwner, owner, type, first, last);
        if (conflict is null)
        {
            BitConverter.TryWriteBytes(b.Slice(0, 2), (short)LinuxFileLocks.Unlock); // Linux ändert sonst nichts an der Struktur
        }
        else
        {
            BitConverter.TryWriteBytes(b.Slice(0, 2), (short)conflict.Type);
            BitConverter.TryWriteBytes(b.Slice(2, 2), (short)0);
            BitConverter.TryWriteBytes(b.Slice(8, 8), conflict.Start);
            BitConverter.TryWriteBytes(b.Slice(16, 8), conflict.End == LinuxFileLocks.ToEnd ? 0L : conflict.End - conflict.Start + 1);
            BitConverter.TryWriteBytes(b.Slice(24, 4), conflict.Ofd is null ? conflict.Pid : -1); // OFD-Sperren haben keinen Prozess
        }

        return p.Memory.TryWrite(arg, b) ? 0 : -EFAULT;
    }

    /// <summary>
    /// Bereich wie Linux <c>flock_to_posix_lock</c>: Bezug SEEK_SET/CUR/END, l_len 0 bis zum Ende, negatives l_len zählt rückwärts.
    /// Ein Anfang unter 0 ist -EINVAL, ein Überlauf über OFFSET_MAX -EOVERFLOW.
    /// </summary>
    internal static long LockRange(ILinuxFile file, long size, int whence, long start, long len, out long first, out long last)
    {
        first = last = 0;
        long origin;
        switch (whence)
        {
            case 0:
                origin = 0;
                break;
            case 1:
                origin = file.Seek(0, 1);
                if (origin < 0)
                {
                    return origin;
                }

                break;
            case 2:
                origin = size;
                break;
            default:
                return -EINVAL;
        }

        if (start > 0 && origin > long.MaxValue - start)
        {
            return -Vfs.LinuxErrno.EOVERFLOW;
        }

        first = origin + start;
        if (first < 0)
        {
            return -EINVAL;
        }

        if (len > 0)
        {
            if (len - 1 > long.MaxValue - first)
            {
                return -Vfs.LinuxErrno.EOVERFLOW;
            }

            last = first + len - 1;
        }
        else if (len < 0)
        {
            last = first - 1;
            first += len; // first >= 0 und len < 0: kein Unterlauf
            if (first < 0)
            {
                return -EINVAL;
            }
        }
        else
        {
            last = LinuxFileLocks.ToEnd;
        }

        return 0;
    }

    /// <summary>
    /// V28b: close einer Nummer löst alle POSIX-Sperren des Prozesses auf der Datei, auch wenn andere Nummern sie noch offen halten (Linux,
    /// bekannte POSIX-Eigenheit). Das stat kostet nur, wenn der Prozess überhaupt Sperren hält. Der Aufrufer hält FileGate.
    /// </summary>
    internal static void ReleasePosixLocksOnClose(ILinuxProcess p, ILinuxDescriptor d)
    {
        if (d is ILinuxFile file && p is ILinuxFileLocking locking && locking.FileLocks.HasPosix(locking.LockOwner) && file.Stat(out LinuxStat st) == 0)
        {
            locking.FileLocks.ReleasePosix(locking.LockOwner, st.Dev, st.Ino);
        }
    }
}
