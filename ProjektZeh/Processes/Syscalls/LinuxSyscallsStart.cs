using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V19 harte Startlücken von Ladybird (docs/superpowers/specs/2026-11-27-ladybird-636-vorlauf.md): ppoll (271) mit atomar getauschter
/// Signalmaske, close_range (436) samt CLOSE_RANGE_CLOEXEC und ein minimales Verzeichnis /proc/self/fd (getdents64 auf die offenen Nummern).
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long Ppoll = 271, CloseRange = 436;

    private const uint CloseRangeUnshare = 2, CloseRangeCloexec = 4;
    private const long PpollMaxMs = long.MaxValue / 2;

    /// <summary>
    /// ppoll(fds, nfds, tmo_p, sigmask, sigsetsize) wie Linux <c>do_ppoll</c>: erst die Frist (EFAULT, EINVAL bei negativer oder zu großer
    /// Nanosekundenzahl), dann die Maske (sigsetsize muss 8 sein), dann poll. Die Maske gilt nur während des Wartens: Kehrt ppoll mit einem
    /// Ergebnis zurück, gilt sofort wieder die alte; bei -EINTR bleibt die Wartemaske, bis die Brücke die Handler angesprungen hat, und der
    /// Signalrahmen trägt die alte Maske (rt_sigreturn stellt sie her, wie Linux' saved_sigmask). Die verbleibende Frist steht danach in tmo_p.
    /// </summary>
    private static long DoPpoll(ILinuxProcess p, ulong fds, ulong nfds, ulong timeoutPtr, ulong maskPtr, ulong maskSize)
    {
        long timeoutMs = -1;
        long sec = 0, nsec = 0;
        Span<byte> ts = stackalloc byte[16];
        if (timeoutPtr != 0)
        {
            if (!p.Memory.TryRead(timeoutPtr, ts))
            {
                return -EFAULT;
            }

            sec = BitConverter.ToInt64(ts);
            nsec = BitConverter.ToInt64(ts[8..]);
            if (sec < 0 || nsec < 0 || nsec >= 1_000_000_000)
            {
                return -EINVAL;
            }

            // Aufrunden wie Linux (eine Frist unter einer Millisekunde wartet, statt sofort zurückzukehren).
            timeoutMs = sec >= PpollMaxMs / 1000 ? PpollMaxMs : (sec * 1000) + ((nsec + 999_999) / 1_000_000);
        }

        LinuxThread? thread = null;
        if (maskPtr != 0)
        {
            if (maskSize != 8)
            {
                return -EINVAL;
            }

            Span<byte> m = stackalloc byte[8];
            if (!p.Memory.TryRead(maskPtr, m))
            {
                return -EFAULT;
            }

            if (p is LinuxUserProcess up)
            {
                thread = up.CurrentThread;
                up.BeginWaitMask(thread, BitConverter.ToUInt64(m));
            }
        }

        long start = Stopwatch.GetTimestamp();
        long r = DoPoll(p, fds, nfds, timeoutMs);
        if (thread != null && r != -EINTR)
        {
            ((LinuxUserProcess)p).EndWaitMask(thread);
        }

        if (timeoutPtr != 0 && timeoutMs > 0)
        {
            // In Sekunden und Nanosekunden getrennt rechnen: sec * 10^9 liefe ab etwa 9,2 * 10^9 Sekunden über (Review V19, P3).
            long elapsedMs = Math.Clamp(LinuxPipe.ElapsedMs(start), 0, timeoutMs);
            long leftSec = sec - (elapsedMs / 1000);
            long leftNsec = nsec - ((elapsedMs % 1000) * 1_000_000);
            if (leftNsec < 0)
            {
                leftNsec += 1_000_000_000;
                leftSec--;
            }

            if (leftSec < 0 || elapsedMs >= timeoutMs)
            {
                leftSec = 0; // abgelaufen (die aufgerundete Frist kann die angegebene um weniger als 1 ms übersteigen)
                leftNsec = 0;
            }

            BitConverter.TryWriteBytes(ts, leftSec);
            BitConverter.TryWriteBytes(ts[8..], leftNsec);
            _ = p.Memory.TryWrite(timeoutPtr, ts); // Linux: Fehler beim Zurückschreiben ändern das Ergebnis nicht
        }

        return r;
    }

    private static long DoSigsuspend(ILinuxProcess p, ulong maskPtr, ulong maskSize)
    {
        if (maskSize != 8)
        {
            return -EINVAL;
        }

        Span<byte> m = stackalloc byte[8];
        if (!p.Memory.TryRead(maskPtr, m))
        {
            return -EFAULT;
        }

        if (p is not LinuxUserProcess up || p is not ILinuxThreads threads)
        {
            return -ENOSYS;
        }

        // Bleibt die Wartemaske nach -EINTR stehen, stellt der Signalrahmen die alte Maske her (rt_sigreturn); endet der Prozess, ist es egal.
        up.BeginWaitMask(up.CurrentThread, BitConverter.ToUInt64(m));
        return threads.Futexes.Sleep(-1);
    }


    /// <summary>
    /// close_range(first, last, flags): schließt alle offenen Nummern im Bereich (last bis ~0 erlaubt) oder setzt mit CLOSE_RANGE_CLOEXEC
    /// nur FD_CLOEXEC. CLOSE_RANGE_UNSHARE ist ohne geteilte Tabellen (CLONE_FILES gibt es nicht) wirkungslos. Unter der Dateisperre.
    /// </summary>
    private static long DoCloseRange(ILinuxProcess p, ILinuxFiles f, uint first, uint last, uint flags)
    {
        if ((flags & ~(CloseRangeUnshare | CloseRangeCloexec)) != 0 || first > last)
        {
            return -EINVAL;
        }

        lock (f.FileGate)
        {
            int end = (int)Math.Min(last, (uint)FileMaxFds - 1);
            for (int fd = (int)Math.Min(first, (uint)FileMaxFds); fd <= end; fd++)
            {
                ILinuxDescriptor? d = p.GetDescriptor(fd);
                if (d is null)
                {
                    continue;
                }

                if ((flags & CloseRangeCloexec) != 0)
                {
                    MarkCloseOnExec(f, fd, true);
                }
                else if (f.RemoveDescriptor(fd))
                {
                    ReleasePosixLocksOnClose(p, d); // V28b: wie close (Linux file_close_fd_locked -> filp_flush)
                    _ = ReleaseIfUnused(p, d); // Linux meldet Fehler beim Schließen hier nicht
                }
            }
        }

        return 0;
    }
}

/// <summary>
/// V19: Verzeichnis /proc/self/fd, nur lesen. Ein /proc gibt es sonst nicht; Ladybird (LibCore) und Bibliotheken zählen damit ihre offenen
/// Nummern, wenn close_range fehlt. Die Einträge (".", "..", dann jede offene Nummer als Link wie Linux) liest getdents64 ab Position 0 neu
/// aus der Tabelle; der Verteiler ruft unter der Dateisperre.
/// </summary>
internal sealed class LinuxProcFdDirectory : ILinuxFile
{
    private const byte DtDir = 4, DtLnk = 10;
    private const long EIsdir = 21, EInval = 22;
    private readonly ILinuxProcess _process;
    private List<int> _fds = new();

    public LinuxProcFdDirectory(ILinuxProcess process, string path)
    {
        _process = process;
        Path = path;
    }

    public string Path { get; }

    public bool IsDirectory => true;

    public long DirIndex { get; set; }

    public long Write(ReadOnlySpan<byte> data) => -LinuxSyscalls.EBADF;

    public long Read(Span<byte> dst) => -EIsdir;

    public long Seek(long offset, int whence)
    {
        // Wie ein Verzeichnis unter Linux: nur der Anfang ist eine sinnvolle Position (rewinddir).
        if (whence != 0 || offset < 0)
        {
            return -EInval;
        }

        DirIndex = offset;
        return offset;
    }

    public long Stat(out LinuxStat st)
    {
        st = new LinuxStat { Mode = LinuxMode.Directory | 0x16D, Nlink = 2, Ino = 0x2F, BlockSize = 1024, Uid = (uint)_process.Uid, Gid = (uint)_process.Gid };
        return 0;
    }

    public long ReadDir(long index, out LinuxDirEntry entry)
    {
        entry = default;
        if (index == 0)
        {
            var fds = new List<int>();
            for (int fd = 0; fd < LinuxUserProcess.MaxDescriptors; fd++)
            {
                if (_process.GetDescriptor(fd) != null)
                {
                    fds.Add(fd);
                }
            }

            _fds = fds;
        }

        if (index < 0)
        {
            return -EInval;
        }

        if (index < 2)
        {
            entry = new LinuxDirEntry(index == 0 ? 0x2FUL : 0x2EUL, DtDir, index == 0 ? "." : "..");
            return 0;
        }

        if (index - 2 >= _fds.Count)
        {
            return 1;
        }

        int n = _fds[(int)(index - 2)];
        entry = new LinuxDirEntry(0x1000UL + (ulong)n, DtLnk, n.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    public long Truncate(long length) => -EInval;

    public long Sync() => 0;

    public long Close() => 0;
}
