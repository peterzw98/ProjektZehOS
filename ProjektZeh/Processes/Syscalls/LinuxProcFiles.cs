using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>Ein Bereich des Adressraums für /proc/&lt;pid&gt;/maps (V29).</summary>
internal readonly record struct LinuxProcRegion(ulong Start, ulong End, MapAccess Access, bool Shared, string Name);

/// <summary>
/// V29: Zustand eines Prozesses für /proc/&lt;pid&gt;/status, stat und maps, als Schnappschuss genommen (die Datei formatiert ihn ohne Sperre).
/// Speicher in Seiten zu 4 KiB.
/// </summary>
internal sealed class LinuxProcSnapshot
{
    public int Pid { get; init; }

    public int ParentPid { get; init; }

    public int GroupPid { get; init; }

    /// <summary>comm wie Linux: Dateiname des Programms ohne Verzeichnis, höchstens 15 Bytes.</summary>
    public string Name { get; init; } = "linux";

    /// <summary>R (läuft: der Leser selbst), S (wartet oder rechnet auf einer anderen CPU), Z (beendet, nicht abgeholt).</summary>
    public char State { get; init; } = 'R';

    public ulong Uid { get; init; }

    public ulong Gid { get; init; }

    public int Threads { get; init; } = 1;

    /// <summary>V29c: CPU-Zeit des Prozesses in Nanosekunden (utime in /proc/&lt;pid&gt;/stat; stime bleibt 0).</summary>
    public long CpuNanos { get; init; }

    public ulong SigPending { get; init; }

    public ulong SigBlocked { get; init; }

    public ulong SigIgnored { get; init; }

    public ulong SigCaught { get; init; }

    public long RssAnonPages { get; init; }

    public long RssShmemPages { get; init; }

    public ulong BrkStart { get; init; }

    public ulong Brk { get; init; }

    public ulong StackTop { get; init; }

    public ulong StackSize { get; init; }

    public IReadOnlyList<LinuxProcRegion> Regions { get; init; } = Array.Empty<LinuxProcRegion>();
}

/// <summary>
/// Optional neben <see cref="ILinuxProcess"/> (V29): Schnappschuss eines Prozesses desselben Laufs für /proc. Ohne sie gibt es nur
/// /proc/self mit dem, was <see cref="ILinuxProcess"/> selbst weiß (Hosttests mit Ersatzprozessen).
/// </summary>
internal interface ILinuxProcView
{
    /// <summary>
    /// Zustand von <paramref name="pid"/> (eigene PID = Aufrufer) oder null, wenn es im Lauf keinen solchen Prozess (mehr) gibt.
    /// Ruft man im Threadkontext, auch unter der Dateisperre; nimmt kurz Kinderliste, Speicher- und Threadsperre des Ziels, nie zwei zugleich.
    /// </summary>
    LinuxProcSnapshot? ProcSnapshot(int pid);

    /// <summary>Anzahl laufender (nicht beendeter) Prozesse des Laufs, für /proc/stat und /proc/loadavg.</summary>
    int ProcessCount { get; }

    /// <summary>Programmpfad (wie /proc/self/exe) des Prozesses <paramref name="pid"/> im Lauf; null, wenn es ihn nicht gibt oder er keinen hat.</summary>
    string? ExePathOf(int pid);
}

/// <summary>
/// V29: das minimale /proc, das Ladybird, Skia und musl lesen (docs/superpowers/specs/2026-11-27-ladybird-636-vorlauf.md, Nr. 11).
/// Ein Dateisystem /proc gibt es nicht; der Verteiler leitet nur diese normalisierten Pfade hierher um:
/// <list type="bullet">
/// <item><c>/proc/self|&lt;pid&gt;/status</c> (Ladybird: TracerPid für <c>is_being_debugged</c>, RssAnon/RssShmem/VmSwap für den Prozessmanager),</item>
/// <item><c>/proc/self|&lt;pid&gt;/stat</c> (Felder 14 und 15, utime/stime), <c>/proc/self|&lt;pid&gt;/maps</c>,</item>
/// <item><c>/proc/self|&lt;pid&gt;/fd</c> (Verzeichnis, V19), <c>/proc/stat</c> (Zeile <c>cpu</c>), <c>/proc/meminfo</c>, <c>/proc/cpuinfo</c>.</item>
/// </list>
/// &lt;pid&gt; ist jeder Prozess desselben Laufs (auch Kinder: Ladybirds Prozessmanager liest die Dateien seiner Helfer). Inhalt entsteht wie
/// bei Linux (seq_file) bei jedem Lesen ab Position 0 neu; Ladybird liest /proc/stat mit seek(0) wiederholt aus derselben Datei.
/// <para>
/// Abweichungen: CPU-Zeit je Prozess: utime aus der Laufzeit der Kernelthreads (V29c), stime/cutime/cstime 0; /proc/stat zählt alle Zeit als idle einer CPU, passend zu
/// sched_getaffinity); keine Unterverzeichnisse <c>task</c>, kein <c>/proc/self/mem</c> (Ladybirds Absturzbehandler kommt ohne aus),
/// kein Auflisten von /proc. Alle Dateien sind nur lesbar (Modus 0444, Größe 0 wie Linux).
/// </para>
/// </summary>
internal static class LinuxProcFiles
{
    /// <summary>Rückgabe von <see cref="TryOpen"/>: der Pfad gehört nicht zu diesem /proc.</summary>
    public const long NotProc = long.MinValue;

    private const long ESrch = 3;
    private const int OCreat = 0x40, OExcl = 0x80, ODirectory = 0x10000;

    /// <summary>USER_HZ wie unter Linux x86-64 (sysconf(_SC_CLK_TCK) liefert in musl fest 100).</summary>
    public const long UserHz = 100;

    private enum Entry { None, Fd, Status, Stat, Maps, SysStat, MemInfo, CpuInfo }

    /// <summary>
    /// Öffnet <paramref name="path"/> (normalisiert), wenn er zu diesem /proc gehört; sonst <see cref="NotProc"/>. Sonst 0 oder -errno
    /// wie Linux: -ENOENT für einen fremden oder unbekannten Prozess, -EEXIST für O_CREAT|O_EXCL, -EISDIR für Schreiben auf /proc/self/fd,
    /// -EACCES für Schreiben auf eine Datei, -ENOTDIR für O_DIRECTORY auf eine Datei.
    /// </summary>
    public static long TryOpen(ILinuxProcess p, string path, int flags, out ILinuxFile? file)
    {
        file = null;
        Entry entry = Parse(p, path, out int pid);
        if (entry == Entry.None)
        {
            return NotProc;
        }

        if (pid < 0)
        {
            return -LinuxErrno.ENOENT; // Prozess gibt es im Lauf nicht (oder nicht mehr)
        }

        if ((flags & (OCreat | OExcl)) == (OCreat | OExcl))
        {
            return -LinuxErrno.EEXIST;
        }

        if (entry == Entry.Fd)
        {
            // V19: Linux (do_open) meldet O_CREAT oder Schreibzugriff auf ein Verzeichnis mit -EISDIR.
            if ((flags & 3) != 0 || (flags & OCreat) != 0)
            {
                return -LinuxErrno.EISDIR;
            }

            file = new LinuxProcFdDirectory(p, path);
            return 0;
        }

        if ((flags & ODirectory) != 0)
        {
            return -LinuxErrno.ENOTDIR;
        }

        if ((flags & 3) != 0)
        {
            return -LinuxErrno.EACCES; // 0444, auch der Eigentümer darf nicht schreiben
        }

        int target = pid;
        file = new LinuxProcTextFile(path, (uint)p.Uid, (uint)p.Gid, Ino(entry, target), () => Generate(p, entry, target));
        return 0;
    }

    /// <summary>stat auf einen /proc-Pfad; <see cref="NotProc"/>, wenn er nicht dazugehört.</summary>
    public static long TryStat(ILinuxProcess p, string path, out LinuxStat st)
    {
        st = default;
        long r = TryOpen(p, path, 0, out ILinuxFile? file);
        if (r != 0 || file is null)
        {
            return r;
        }

        r = file.Stat(out st);
        _ = file.Close();
        return r;
    }

    /// <summary>access auf einen /proc-Pfad (Rechte wie Linux für einen Nicht-root-Eigentümer); <see cref="NotProc"/>, wenn er nicht dazugehört.</summary>
    public static long TryAccess(ILinuxProcess p, string path, int mode)
    {
        long r = TryStat(p, path, out LinuxStat st);
        if (r != 0)
        {
            return r;
        }

        // Dateien 0444, Verzeichnis fd 0500: Schreiben nie, Ausführen nur beim Verzeichnis.
        bool dir = (st.Mode & LinuxMode.TypeMask) == LinuxMode.Directory;
        return (mode & 2) != 0 || ((mode & 1) != 0 && !dir) ? -LinuxErrno.EACCES : 0;
    }

    /// <summary>
    /// "/proc/self/exe" oder "/proc/&lt;pid&gt;/exe" eines Prozesses im Lauf: true mit dem Ziel des Links (null, wenn es ihn nicht gibt).
    /// false, wenn der Pfad kein solcher Link ist.
    /// </summary>
    public static bool TryExeLink(ILinuxProcess p, string path, out string? target)
    {
        target = null;
        if (!SplitPid(p, path, out string rest, out int pid) || rest != "exe")
        {
            return false;
        }

        if (pid == p.Pid)
        {
            target = (p as ILinuxExe)?.ExePath;
        }
        else if (pid > 0 && p is ILinuxProcView view)
        {
            target = view.ExePathOf(pid);
        }

        return true;
    }

    /// <summary>comm wie Linux (<c>__set_task_comm</c>): Dateiname ohne Verzeichnis, auf 15 Bytes gekürzt.</summary>
    public static string CommOf(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return "linux";
        }

        int slash = exePath.LastIndexOf('/');
        string name = slash >= 0 ? exePath[(slash + 1)..] : exePath;
        while (Encoding.UTF8.GetByteCount(name) > 15)
        {
            name = name[..^1];
        }

        return name;
    }

    /// <summary>
    /// V29b: teilt die (verschmolzenen) Bereiche der Adressraumkarte an den Grenzen von Heap, Stack und geteilten Abbildungen und benennt
    /// die Teile. Linux führt diese als eigene VMAs (eigene Flags oder Datei), die Karte hier fasst Nachbarn gleicher Rechte zusammen; ohne
    /// Teilung trüge ein Heap direkt hinter dem Programm-BSS keinen Namen und ein memfd neben anonymem Speicher gleicher Rechte keine Datei.
    /// Ein Teil gilt als Stack, wenn er in [stackLow, stackTop) liegt (Linux nennt nur die VMA mit start_stack; bei geteiltem Stack weicht
    /// das ab).
    /// </summary>
    internal static void NameRegions(
        IReadOnlyList<(ulong Start, ulong End, MapAccess Access)> raw,
        IReadOnlyList<(ulong Start, ulong End, string Name)> shared,
        ulong heapStart,
        ulong heapEnd,
        ulong stackLow,
        ulong stackTop,
        List<LinuxProcRegion> output)
    {
        bool heap = heapEnd > heapStart;
        bool stack = stackTop > stackLow;
        foreach ((ulong start, ulong end, MapAccess access) in raw)
        {
            ulong cur = start;
            while (cur < end)
            {
                // Nächste Grenze hinter cur innerhalb des Bereichs.
                ulong next = end;
                if (heap)
                {
                    next = Cut(cur, next, heapStart);
                    next = Cut(cur, next, heapEnd);
                }

                if (stack)
                {
                    next = Cut(cur, next, stackLow);
                    next = Cut(cur, next, stackTop);
                }

                foreach ((ulong sStart, ulong sEnd, _) in shared)
                {
                    next = Cut(cur, next, sStart);
                    next = Cut(cur, next, sEnd);
                }

                // Das Stück [cur, next) liegt ganz in einer Klasse; der Anfang entscheidet.
                string name = string.Empty;
                bool isShared = false;
                foreach ((ulong sStart, ulong sEnd, string sName) in shared)
                {
                    if (sStart <= cur && cur < sEnd)
                    {
                        name = sName;
                        isShared = true;
                        break;
                    }
                }

                if (!isShared && heap && heapStart <= cur && cur < heapEnd)
                {
                    name = "[heap]";
                }
                else if (!isShared && stack && stackLow <= cur && cur < stackTop)
                {
                    name = "[stack]";
                }

                output.Add(new LinuxProcRegion(cur, next, access, isShared, name));
                cur = next;
            }
        }
    }

    private static ulong Cut(ulong cur, ulong next, ulong boundary) => boundary > cur && boundary < next ? boundary : next;

    /// <summary>
    /// V29b: comm in /proc/&lt;pid&gt;/status wie Linux (<c>proc_task_name</c> mit <c>seq_escape_str(..., ESCAPE_SPACE | ESCAPE_SPECIAL, "\n\\")</c>):
    /// nur Zeilenumbruch und Backslash werden zu <c>\n</c> und <c>\\</c>, damit ein Programmname keine Zeile der Datei vortäuscht.
    /// In stat bleibt comm roh (Linux ebenso; Leser suchen die letzte Klammer).
    /// </summary>
    internal static string EscapeStatusName(string name)
    {
        if (name.IndexOfAny(['\n', '\\']) < 0)
        {
            return name;
        }

        return name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// V29b: Pfad in /proc/&lt;pid&gt;/maps wie Linux (<c>seq_file_path(m, file, "\n")</c>, <c>mangle_path</c>): ein Zeilenumbruch wird
    /// oktal zu <c>\012</c>, sonst bleibt der Name (auch ein Backslash) unverändert.
    /// </summary>
    internal static string EscapeMapsName(string name) =>
        name.Contains('\n', StringComparison.Ordinal) ? name.Replace("\n", "\\012", StringComparison.Ordinal) : name;

    private static ulong Ino(Entry entry, int pid) => 0xF000_0000UL | ((ulong)(uint)Math.Max(pid, 0) << 4) | (ulong)entry;

    // pid: -1, wenn der Pfad einen Prozess nennt, den es nicht gibt; 0 für Dateien ohne Prozess.
    private static Entry Parse(ILinuxProcess p, string path, out int pid)
    {
        pid = 0;
        switch (path)
        {
            case "/proc/stat": return Entry.SysStat;
            case "/proc/meminfo": return Entry.MemInfo;
            case "/proc/cpuinfo": return Entry.CpuInfo;
        }

        if (!SplitPid(p, path, out string rest, out pid))
        {
            return Entry.None;
        }

        Entry entry = rest switch
        {
            "fd" => Entry.Fd,
            "status" => Entry.Status,
            "stat" => Entry.Stat,
            "maps" => Entry.Maps,
            _ => Entry.None,
        };

        // /proc/<pid>/fd gibt es nur für den Aufrufer selbst (die Tabelle eines anderen Prozesses zu lesen braucht seine Sperren).
        if (entry == Entry.Fd && pid != p.Pid)
        {
            return Entry.None;
        }

        if (entry != Entry.None && pid != p.Pid && (p is not ILinuxProcView view || view.ProcSnapshot(pid) is null))
        {
            pid = -1;
        }

        return entry;
    }

    // "/proc/<who>/<rest>": who ist "self" oder eine Dezimalzahl ohne führende Null (wie Linux' name_to_int).
    private static bool SplitPid(ILinuxProcess p, string path, out string rest, out int pid)
    {
        const string Prefix = "/proc/";
        rest = string.Empty;
        pid = 0;
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int slash = path.IndexOf('/', Prefix.Length);
        if (slash < 0 || slash == path.Length - 1)
        {
            return false;
        }

        string who = path[Prefix.Length..slash];
        rest = path[(slash + 1)..];
        if (who == "self")
        {
            pid = p.Pid;
            return true;
        }

        if (who.Length == 0 || who.Length > 10 || (who[0] == '0') || !int.TryParse(who, NumberStyles.None, CultureInfo.InvariantCulture, out pid))
        {
            return false;
        }

        return true;
    }

    // null: Der Prozess ist inzwischen abgeholt (Lesen liefert dann -ESRCH wie Linux).
    private static string? Generate(ILinuxProcess p, Entry entry, int pid)
    {
        switch (entry)
        {
            case Entry.SysStat: return SysStat(p);
            case Entry.MemInfo: return MemInfo(p);
            case Entry.CpuInfo: return CpuInfo();
        }

        LinuxProcSnapshot? s = Snapshot(p, pid);
        if (s is null)
        {
            return null;
        }

        return entry switch
        {
            Entry.Status => Status(s),
            Entry.Stat => Stat(s),
            _ => Maps(s),
        };
    }

    private static LinuxProcSnapshot? Snapshot(ILinuxProcess p, int pid)
    {
        if (p is ILinuxProcView view)
        {
            return view.ProcSnapshot(pid);
        }

        if (pid != p.Pid)
        {
            return null;
        }

        // Ersatzprozess ohne Sicht auf den Lauf: was ILinuxProcess selbst weiß.
        SignalSets(p.Signals, out ulong ign, out ulong cgt);
        return new LinuxProcSnapshot
        {
            Pid = p.Pid,
            GroupPid = p.Pid,
            Name = CommOf((p as ILinuxExe)?.ExePath),
            Uid = p.Uid,
            Gid = p.Gid,
            SigBlocked = p.Signals.Blocked,
            SigIgnored = ign,
            SigCaught = cgt,
            Brk = p.Brk,
            BrkStart = p.Brk,
        };
    }

    /// <summary>SigIgn und SigCgt aus den Handlern (Bit = Nummer - 1; SIG_DFL 0, SIG_IGN 1, sonst ein Handler).</summary>
    public static void SignalSets(LinuxSignalState signals, out ulong ignored, out ulong caught)
    {
        ignored = 0;
        caught = 0;
        for (int sig = 1; sig <= 64; sig++)
        {
            ulong h = signals.Handler[sig];
            if (h == 1)
            {
                ignored |= 1UL << (sig - 1);
            }
            else if (h > 1)
            {
                caught |= 1UL << (sig - 1);
            }
        }
    }

    private static long Kb(long pages) => pages * 4;

    private static ulong VmSizeKb(LinuxProcSnapshot s)
    {
        ulong bytes = 0;
        foreach (LinuxProcRegion r in s.Regions)
        {
            bytes += r.End - r.Start;
        }

        return bytes / 1024;
    }

    private static string StateText(char state) => state switch
    {
        'R' => "R (running)",
        'Z' => "Z (zombie)",
        _ => "S (sleeping)",
    };

    /// <summary>/proc/&lt;pid&gt;/status in der Reihenfolge von Linux 6.1 (fs/proc/array.c); TracerPid immer 0 (kein ptrace).</summary>
    internal static string Status(LinuxProcSnapshot s)
    {
        var b = new StringBuilder(1024);
        string I(long v) => v.ToString(CultureInfo.InvariantCulture);
        string H(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);
        // Linux: seq_put_decimal_ull_width(m, "VmRSS:\t", wert, 8), also Tabulator und rechtsbündig in acht Stellen.
        void Kv(string name, long kb) => b.Append(name).Append(":\t").Append(kb.ToString(CultureInfo.InvariantCulture).PadLeft(8)).Append(" kB\n");

        long rss = s.RssAnonPages + s.RssShmemPages;
        long vm = (long)VmSizeKb(s);
        long data = Math.Max(0, (long)(s.Brk > s.BrkStart ? (s.Brk - s.BrkStart) / 1024 : 0));
        b.Append("Name:\t").Append(EscapeStatusName(s.Name)).Append('\n');
        b.Append("Umask:\t0022\n");
        b.Append("State:\t").Append(StateText(s.State)).Append('\n');
        b.Append("Tgid:\t").Append(I(s.Pid)).Append('\n');
        b.Append("Ngid:\t0\n");
        b.Append("Pid:\t").Append(I(s.Pid)).Append('\n');
        b.Append("PPid:\t").Append(I(s.ParentPid)).Append('\n');
        b.Append("TracerPid:\t0\n");
        b.Append("Uid:\t").Append(s.Uid).Append('\t').Append(s.Uid).Append('\t').Append(s.Uid).Append('\t').Append(s.Uid).Append('\n');
        b.Append("Gid:\t").Append(s.Gid).Append('\t').Append(s.Gid).Append('\t').Append(s.Gid).Append('\t').Append(s.Gid).Append('\n');
        b.Append("FDSize:\t").Append(I(LinuxUserProcess.MaxDescriptors)).Append('\n');
        b.Append("Groups:\t\n");
        b.Append("NStgid:\t").Append(I(s.Pid)).Append('\n');
        b.Append("NSpid:\t").Append(I(s.Pid)).Append('\n');
        b.Append("NSpgid:\t").Append(I(s.GroupPid)).Append('\n');
        b.Append("NSsid:\t").Append(I(s.GroupPid)).Append('\n');
        if (s.State != 'Z')
        {
            // Linux lässt die Speicherzeilen bei Zombies (kein mm) weg.
            Kv("VmPeak", vm);
            Kv("VmSize", vm);
            Kv("VmLck", 0);
            Kv("VmPin", 0);
            Kv("VmHWM", Kb(rss));
            Kv("VmRSS", Kb(rss));
            Kv("RssAnon", Kb(s.RssAnonPages));
            Kv("RssFile", 0);
            Kv("RssShmem", Kb(s.RssShmemPages));
            Kv("VmData", data);
            Kv("VmStk", (long)(s.StackSize / 1024));
            Kv("VmExe", 0);
            Kv("VmLib", 0);
            Kv("VmPTE", 0);
            Kv("VmSwap", 0);
            b.Append("HugetlbPages:\t       0 kB\n");
        }

        b.Append("CoreDumping:\t0\n");
        b.Append("THP_enabled:\t0\n");
        b.Append("Threads:\t").Append(I(s.Threads)).Append('\n');
        b.Append("SigQ:\t0/0\n");
        b.Append("SigPnd:\t").Append(H(s.SigPending)).Append('\n');
        b.Append("ShdPnd:\t").Append(H(0)).Append('\n');
        b.Append("SigBlk:\t").Append(H(s.SigBlocked)).Append('\n');
        b.Append("SigIgn:\t").Append(H(s.SigIgnored)).Append('\n');
        b.Append("SigCgt:\t").Append(H(s.SigCaught)).Append('\n');
        b.Append("CapInh:\t0000000000000000\nCapPrm:\t0000000000000000\nCapEff:\t0000000000000000\n");
        b.Append("CapBnd:\t000001ffffffffff\nCapAmb:\t0000000000000000\n");
        b.Append("NoNewPrivs:\t0\nSeccomp:\t0\nSeccomp_filters:\t0\n");
        b.Append("Cpus_allowed:\t1\nCpus_allowed_list:\t0\nMems_allowed:\t1\nMems_allowed_list:\t0\n");
        b.Append("voluntary_ctxt_switches:\t0\nnonvoluntary_ctxt_switches:\t0\n");
        return b.ToString();
    }

    /// <summary>/proc/&lt;pid&gt;/stat: 52 Felder wie Linux 6.1 (do_task_stat); unbekannte Zähler 0.</summary>
    internal static string Stat(LinuxProcSnapshot s)
    {
        long rssPages = s.RssAnonPages + s.RssShmemPages;
        ulong vsize = VmSizeKb(s) * 1024;
        var f = new List<string>(52)
        {
            s.Pid.ToString(CultureInfo.InvariantCulture),
            "(" + s.Name + ")",
            s.State.ToString(),
            s.ParentPid.ToString(CultureInfo.InvariantCulture),
            s.GroupPid.ToString(CultureInfo.InvariantCulture), // pgrp
            s.GroupPid.ToString(CultureInfo.InvariantCulture), // session
            "0", "-1", // tty_nr, tpgid
            "4194304", // flags (PF_RANDOMIZE wie ein gewöhnlicher Prozess)
            "0", "0", "0", "0", // minflt, cminflt, majflt, cmajflt
            (s.CpuNanos / 10_000_000).ToString(CultureInfo.InvariantCulture), "0", "0", "0", // utime (CLK_TCK 100), stime, cutime, cstime (nicht gezählt)
            "20", "0", // priority, nice
            s.Threads.ToString(CultureInfo.InvariantCulture),
            "0", "0", // itrealvalue, starttime
            vsize.ToString(CultureInfo.InvariantCulture),
            rssPages.ToString(CultureInfo.InvariantCulture),
            ulong.MaxValue.ToString(CultureInfo.InvariantCulture), // rsslim (RLIM_INFINITY)
            "0", "0", // startcode, endcode
            s.StackTop.ToString(CultureInfo.InvariantCulture), // startstack
            "0", "0", // kstkesp, kstkeip (Linux zeigt sie nur mit ptrace-Recht)
            "0", "0", "0", "0", // signal, blocked, sigignore, sigcatch: veraltet, Linux schreibt die Masken hier mit
            "0", "0", "0", // wchan, nswap, cnswap
            "17", "0", "0", "0", // exit_signal (SIGCHLD), processor, rt_priority, policy
            "0", "0", "0", // delayacct_blkio_ticks, guest_time, cguest_time
            "0", "0", // start_data, end_data
            s.BrkStart.ToString(CultureInfo.InvariantCulture), // start_brk
            "0", "0", "0", "0", // arg_start, arg_end, env_start, env_end
            "0", // exit_code
        };

        // Signalfelder 31 bis 34 wie Linux als Dezimalzahl der unteren 32 Bit (veraltete Schnittstelle).
        f[30] = (s.SigPending & 0xffff_ffff).ToString(CultureInfo.InvariantCulture);
        f[31] = (s.SigBlocked & 0xffff_ffff).ToString(CultureInfo.InvariantCulture);
        f[32] = (s.SigIgnored & 0xffff_ffff).ToString(CultureInfo.InvariantCulture);
        f[33] = (s.SigCaught & 0xffff_ffff).ToString(CultureInfo.InvariantCulture);
        return string.Join(' ', f) + "\n";
    }

    /// <summary>/proc/&lt;pid&gt;/maps: eine Zeile je Bereich wie Linux (show_map_vma), Name ab Spalte 74.</summary>
    internal static string Maps(LinuxProcSnapshot s)
    {
        var b = new StringBuilder(s.Regions.Count * 80);
        foreach (LinuxProcRegion r in s.Regions)
        {
            int start = b.Length;
            b.Append(r.Start.ToString("x8", CultureInfo.InvariantCulture)).Append('-').Append(r.End.ToString("x8", CultureInfo.InvariantCulture)).Append(' ');
            b.Append(r.Access is MapAccess.Read or MapAccess.ReadWrite or MapAccess.ReadExecute ? 'r' : '-');
            b.Append(r.Access == MapAccess.ReadWrite ? 'w' : '-');
            b.Append(r.Access == MapAccess.ReadExecute ? 'x' : '-');
            b.Append(r.Shared ? 's' : 'p');
            b.Append(" 00000000 00:00 0");
            if (r.Name.Length > 0)
            {
                b.Append(' ', Math.Max(1, 73 - (b.Length - start)));
                b.Append(EscapeMapsName(r.Name));
            }

            b.Append('\n');
        }

        return b.ToString();
    }

    /// <summary>/proc/stat: eine CPU (wie sched_getaffinity), alle Zeit seit dem Start als idle in USER_HZ.</summary>
    internal static string SysStat(ILinuxProcess p)
    {
        long mono = p is ILinuxHost host ? host.MonotonicNanos : 0;
        long real = p is ILinuxHost h2 ? h2.RealtimeNanos : 0;
        long idle = Math.Max(0, mono / (1_000_000_000 / UserHz));
        long btime = Math.Max(0, (real - mono) / 1_000_000_000);
        int procs = p is ILinuxProcView view ? view.ProcessCount : 1;
        string i = idle.ToString(CultureInfo.InvariantCulture);
        var b = new StringBuilder(256);
        b.Append("cpu  0 0 0 ").Append(i).Append(" 0 0 0 0 0 0\n");
        b.Append("cpu0 0 0 0 ").Append(i).Append(" 0 0 0 0 0 0\n");
        b.Append("intr 0\nctxt 0\n");
        b.Append("btime ").Append(btime.ToString(CultureInfo.InvariantCulture)).Append('\n');
        b.Append("processes ").Append(procs.ToString(CultureInfo.InvariantCulture)).Append('\n');
        b.Append("procs_running 1\nprocs_blocked 0\n");
        b.Append("softirq 0 0 0 0 0 0 0 0 0 0 0\n");
        return b.ToString();
    }

    /// <summary>/proc/meminfo: Gesamt- und freier Speicher aus <see cref="ILinuxSystemInfo"/> (wie sysinfo), sonst 0; kein Swap, kein Cache.</summary>
    internal static string MemInfo(ILinuxProcess p)
    {
        ulong total = 0, free = 0;
        if (p is ILinuxSystemInfo info && info.TryGetMemory(out ulong t, out ulong fr))
        {
            total = t;
            free = Math.Min(fr, t);
        }

        var b = new StringBuilder(512);
        void Line(string name, ulong kb) => b.Append((name + ":").PadRight(16)).Append(kb.ToString(CultureInfo.InvariantCulture).PadLeft(8)).Append(" kB\n");
        Line("MemTotal", total / 1024);
        Line("MemFree", free / 1024);
        Line("MemAvailable", free / 1024);
        foreach (string name in new[] { "Buffers", "Cached", "SwapCached", "Active", "Inactive", "SwapTotal", "SwapFree", "Dirty", "Writeback",
            "AnonPages", "Mapped", "Shmem", "Slab", "SReclaimable", "SUnreclaim", "KernelStack", "PageTables", "CommitLimit", "Committed_AS" })
        {
            Line(name, 0);
        }

        return b.ToString();
    }

    /// <summary>
    /// /proc/cpuinfo: ein Prozessor (wie sched_getaffinity). Die Flags nennen nur, was jede x86-64-CPU hat; Programme, die mehr brauchen,
    /// fragen CPUID selbst (Skia, musl tun das).
    /// </summary>
    internal static string CpuInfo() =>
        "processor\t: 0\n" +
        "vendor_id\t: ZehOS\n" +
        "cpu family\t: 6\n" +
        "model\t\t: 0\n" +
        "model name\t: ZehOS x86-64 CPU\n" +
        "stepping\t: 0\n" +
        "cpu MHz\t\t: 1000.000\n" +
        "physical id\t: 0\n" +
        "siblings\t: 1\n" +
        "core id\t\t: 0\n" +
        "cpu cores\t: 1\n" +
        "apicid\t\t: 0\n" +
        "fpu\t\t: yes\n" +
        "fpu_exception\t: yes\n" +
        "cpuid level\t: 1\n" +
        "wp\t\t: yes\n" +
        "flags\t\t: fpu tsc cx8 cmov mmx fxsr sse sse2 syscall nx lm\n" +
        "bogomips\t: 2000.00\n" +
        "clflush size\t: 64\n" +
        "cache_alignment\t: 64\n" +
        "address sizes\t: 48 bits physical, 48 bits virtual\n" +
        "power management:\n\n";

    /// <summary>-ESRCH für Dateien eines inzwischen abgeholten Prozesses.</summary>
    internal static long Gone => -ESrch;
}

/// <summary>
/// V29: eine nur lesbare /proc-Textdatei. Der Inhalt entsteht wie bei Linux' seq_file bei jedem Lesen ab Position 0 neu (nach lseek(0)
/// liest man den aktuellen Stand); Lesen ab einer anderen Position setzt den zuletzt erzeugten Text fort. Der Verteiler ruft unter der
/// Dateisperre.
/// </summary>
internal sealed class LinuxProcTextFile : ILinuxFile
{
    private readonly Func<string?> _generate;
    private readonly uint _uid, _gid;
    private readonly ulong _ino;
    private byte[] _text = Array.Empty<byte>();
    private long _pos;

    public LinuxProcTextFile(string path, uint uid, uint gid, ulong ino, Func<string?> generate)
    {
        Path = path;
        _uid = uid;
        _gid = gid;
        _ino = ino;
        _generate = generate;
    }

    public string Path { get; }

    public bool IsDirectory => false;

    public long DirIndex { get; set; }

    public long Write(ReadOnlySpan<byte> data) => -LinuxSyscalls.EBADF;

    public long Read(Span<byte> dst)
    {
        if (_pos == 0 || _text.Length == 0)
        {
            string? text = _generate();
            if (text is null)
            {
                return LinuxProcFiles.Gone;
            }

            _text = Encoding.UTF8.GetBytes(text);
        }

        if (_pos >= _text.Length)
        {
            return 0;
        }

        int n = (int)Math.Min(dst.Length, _text.Length - _pos);
        _text.AsSpan((int)_pos, n).CopyTo(dst);
        _pos += n;
        return n;
    }

    public long Seek(long offset, int whence)
    {
        // seq_file: SEEK_SET und SEEK_CUR; SEEK_END kennt die Länge nicht (-EINVAL).
        long target = whence switch
        {
            0 => offset,
            1 => _pos + offset,
            _ => -1,
        };

        if (target < 0)
        {
            return -LinuxErrno.EINVAL;
        }

        _pos = target;
        return target;
    }

    public long Stat(out LinuxStat st)
    {
        st = new LinuxStat { Mode = LinuxMode.Regular | 0x124, Nlink = 1, Ino = _ino, BlockSize = 1024, Uid = _uid, Gid = _gid };
        return 0;
    }

    public long ReadDir(long index, out LinuxDirEntry entry)
    {
        entry = default;
        return -LinuxErrno.ENOTDIR;
    }

    public long Truncate(long length) => -LinuxErrno.EINVAL;

    public long Sync() => -LinuxErrno.EINVAL;

    public long Close() => 0;
}
