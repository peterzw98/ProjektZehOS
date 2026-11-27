using System.Collections.Generic;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V28b: beratende Bereichssperren (fcntl F_SETLK/F_SETLKW/F_GETLK und F_OFD_*) aller Prozesse eines Laufs, wie Linux <c>fs/locks.c</c>.
/// Eine Sperre gehört entweder einem Prozess (POSIX, Besitzer ist die PID) oder einer offenen Datei (OFD, Besitzer ist das Objekt der offenen
/// Datei); die Datei selbst erkennt die Tabelle an (st_dev, st_ino), damit zwei open derselben Datei dieselben Sperren sehen.
/// <para>
/// Sperren desselben Besitzers stoßen nie zusammen; sie ersetzen, teilen oder verschmelzen sich (Linux <c>posix_lock_inode</c>). POSIX und OFD
/// sind verschiedene Besitzer, auch im selben Prozess. Freigabe: POSIX bei jedem close einer Nummer des Prozesses auf die Datei und am
/// Prozessende, OFD beim letzten close der offenen Datei.
/// </para>
/// <para>
/// Geschützt durch <see cref="ILinuxFiles.FileGate"/> (die Tabelle gehört zu <see cref="LinuxOpenFiles"/>, die alle Prozesse eines Laufs teilen);
/// alle Methoden setzen voraus, dass der Aufrufer sie hält. Eine Liste genügt: Programme halten wenige Sperren (sqlite: höchstens drei Bereiche
/// je Datenbank).
/// </para>
/// </summary>
internal sealed class LinuxFileLocks
{
    public const int Read = 0, Write = 1, Unlock = 2;

    /// <summary>Letztes Byte einer Sperre „bis zum Dateiende und darüber hinaus“ (l_len 0), wie Linux <c>OFFSET_MAX</c>.</summary>
    public const long ToEnd = long.MaxValue;

    private readonly List<Range> _ranges = new();

    /// <summary>Eine gehaltene Sperre. <see cref="Ofd"/> null heißt POSIX-Sperre von <see cref="Pid"/>.</summary>
    public sealed class Range
    {
        public ulong Dev;
        public ulong Ino;
        public int Pid;
        public object? Ofd;
        public int Type;
        public long Start;
        public long End; // einschließlich
    }

    /// <summary>Anzahl gehaltener Bereiche (Tests).</summary>
    public int Count => _ranges.Count;

    /// <summary>true, wenn <paramref name="pid"/> irgendeine POSIX-Sperre hält; close spart sich sonst das stat der Datei.</summary>
    public bool HasPosix(int pid)
    {
        for (int i = 0; i < _ranges.Count; i++)
        {
            if (_ranges[i].Ofd == null && _ranges[i].Pid == pid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>true, wenn die offene Datei <paramref name="ofd"/> OFD-Sperren hält.</summary>
    public bool HasOfd(object ofd)
    {
        for (int i = 0; i < _ranges.Count; i++)
        {
            if (ReferenceEquals(_ranges[i].Ofd, ofd))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// F_GETLK: die erste Sperre eines anderen Besitzers, die eine Sperre vom Typ <paramref name="type"/> auf [start, end] verhindern würde;
    /// null, wenn keine.
    /// </summary>
    public Range? FindConflict(ulong dev, ulong ino, int pid, object? ofd, int type, long start, long end)
    {
        for (int i = 0; i < _ranges.Count; i++)
        {
            Range r = _ranges[i];
            if (r.Dev == dev && r.Ino == ino && !SameOwner(r, pid, ofd) && r.Start <= end && start <= r.End && (type == Write || r.Type == Write))
            {
                return r;
            }
        }

        return null;
    }

    /// <summary>
    /// F_SETLK: setzt oder löst [start, end] für den Besitzer. false (nichts geändert), wenn ein anderer Besitzer im Weg steht; Lösen gelingt
    /// immer. Wie Linux wird der Bereich des Besitzers erst ganz frei gemacht (Nachbarn geteilt), dann neu eingetragen und mit angrenzenden
    /// Bereichen gleichen Typs verschmolzen.
    /// </summary>
    public bool TrySet(ulong dev, ulong ino, int pid, object? ofd, int type, long start, long end)
    {
        if (type != Unlock && FindConflict(dev, ino, pid, ofd, type, start, end) != null)
        {
            return false;
        }

        // Eigene Bereiche, die [start, end] überlappen, verlieren diesen Teil; ragen sie auf beiden Seiten heraus, bleiben zwei Stücke.
        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            Range r = _ranges[i];
            if (r.Dev != dev || r.Ino != ino || !SameOwner(r, pid, ofd) || r.End < start || end < r.Start)
            {
                continue;
            }

            bool left = r.Start < start, right = end < r.End;
            if (left && right)
            {
                _ranges.Add(new Range { Dev = dev, Ino = ino, Pid = r.Pid, Ofd = r.Ofd, Type = r.Type, Start = end + 1, End = r.End });
                r.End = start - 1;
            }
            else if (left)
            {
                r.End = start - 1;
            }
            else if (right)
            {
                r.Start = end + 1;
            }
            else
            {
                _ranges.RemoveAt(i);
            }
        }

        if (type == Unlock)
        {
            return true;
        }

        // Angrenzende eigene Bereiche gleichen Typs verschmelzen (Linux meldet sie in F_GETLK als einen Bereich). Nach dem Freimachen
        // überlappt nichts mehr; End + 1 läuft bei ToEnd nicht über, weil ein Bereich bis ToEnd keinen rechten Nachbarn hat.
        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            Range r = _ranges[i];
            if (r.Dev != dev || r.Ino != ino || !SameOwner(r, pid, ofd) || r.Type != type)
            {
                continue;
            }

            if (r.End != ToEnd && r.End + 1 == start)
            {
                start = r.Start;
                _ranges.RemoveAt(i);
            }
            else if (end != ToEnd && end + 1 == r.Start)
            {
                end = r.End;
                _ranges.RemoveAt(i);
            }
        }

        _ranges.Add(new Range { Dev = dev, Ino = ino, Pid = pid, Ofd = ofd, Type = type, Start = start, End = end });
        return true;
    }

    /// <summary>close einer Nummer: alle POSIX-Sperren von <paramref name="pid"/> auf der Datei fallen (Linux <c>locks_remove_posix</c>).</summary>
    public void ReleasePosix(int pid, ulong dev, ulong ino) =>
        _ranges.RemoveAll(r => r.Ofd == null && r.Pid == pid && r.Dev == dev && r.Ino == ino);

    /// <summary>Prozessende: alle POSIX-Sperren von <paramref name="pid"/>, auf jeder Datei.</summary>
    public void ReleaseProcess(int pid) => _ranges.RemoveAll(r => r.Ofd == null && r.Pid == pid);

    /// <summary>Letztes close der offenen Datei: ihre OFD-Sperren fallen (Linux <c>locks_remove_file</c>).</summary>
    public void ReleaseOfd(object ofd) => _ranges.RemoveAll(r => ReferenceEquals(r.Ofd, ofd));

    private static bool SameOwner(Range r, int pid, object? ofd) => ofd == null ? r.Ofd == null && r.Pid == pid : ReferenceEquals(r.Ofd, ofd);
}

/// <summary>
/// V28b: Optional neben <see cref="ILinuxFiles"/>: die Sperrtabelle des Laufs und der POSIX-Besitzer des Aufrufers. Ohne sie liefern die
/// Sperrbefehle von fcntl -ENOLCK (Hosttests mit Ersatzprozessen).
/// </summary>
internal interface ILinuxFileLocking
{
    LinuxFileLocks FileLocks { get; }

    /// <summary>Besitzer der POSIX-Sperren (Linux: die Dateitabelle; Threads teilen sie, ein vfork-Kind hat eine eigene).</summary>
    int LockOwner { get; }
}
