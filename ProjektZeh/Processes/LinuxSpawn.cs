using System;
using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Processes;

/// <summary>
/// V15: gemeinsame Vergabe von PIDs und TIDs aller Prozesse eines Laufs. Linux führt beide in einem Namensraum; eine TID darf mit
/// keiner PID eines anderen Prozesses zusammenfallen (kill, tgkill, wait4 und futex-Eigentümer meinen sonst den falschen).
/// Sperrreihenfolge: zuunterst, darin keine andere Sperre; <c>_threadGate</c> eines Prozesses darf davor gehalten werden.
/// </summary>
internal sealed class LinuxIds
{
    private readonly object _gate = new();
    private readonly HashSet<int> _used = new();
    private int _last;

    /// <param name="start">Die erste Vergabe liefert die nächste freie Nummer nach <paramref name="start"/> (wie bisher PID + 1).</param>
    public LinuxIds(int start) => _last = start;

    /// <summary>V16: Namen der Unix-Sockets; wie die PIDs ein Namensraum je Lauf (eigene Blattsperre).</summary>
    public LinuxUnixNames UnixNames { get; } = new();

    /// <summary>
    /// V35: Sperre für Prozessgruppen und Sitzungen des Laufs (LinuxProcessGroups.cs); vor ChildGate, nie unter einer anderen Sperre genommen.
    /// </summary>
    public object GroupGate { get; } = new();

    public void Add(int id)
    {
        lock (_gate)
        {
            _used.Add(id);
        }
    }

    public void Remove(int id)
    {
        lock (_gate)
        {
            _used.Remove(id);
        }
    }

    public bool InUse(int id)
    {
        lock (_gate)
        {
            return _used.Contains(id);
        }
    }

    /// <summary>Nächste freie Nummer (mit Umlauf nach <see cref="LinuxUserProcess.TidMax"/>), gleich als belegt eingetragen.</summary>
    public int Allocate()
    {
        lock (_gate)
        {
            _last = LinuxUserProcess.NextTid(_last, 0, _used.Contains);
            _used.Add(_last);
            return _last;
        }
    }
}

/// <summary>
/// V15: ein Kindprozess aus Sicht des Elternprozesses (für wait4, waitid, getppid). <see cref="Process"/> wechselt bei execve auf das
/// neue Abbild; die PID bleibt. Geschrieben unter <see cref="LinuxUserProcess.ChildGate"/>.
/// </summary>
internal sealed class LinuxChild
{
    public LinuxChild(int pid, LinuxUserProcess process)
    {
        Pid = pid;
        Process = process;
    }

    public int Pid { get; }

    public LinuxUserProcess Process { get; internal set; }

    /// <summary>Wartestatus wie Linux (<c>exit n</c>: n &lt;&lt; 8, Signal: Nummer), null, solange das Kind läuft.</summary>
    public int? WaitStatus { get; internal set; }

    /// <summary>Elternprozess ignoriert SIGCHLD: beendet, aber kein Zombie für wait4 (LinuxWait.cs).</summary>
    public bool AutoReaped { get; internal set; }
}

/// <summary>Optional neben <see cref="ILinuxProcess"/> (V15): execve. Ohne sie ist execve -ENOSYS.</summary>
internal interface ILinuxSpawn
{
    /// <summary>
    /// Lädt <paramref name="path"/> (absolut im Prozess, aufgelöst) in einen neuen Adressraum. Liefert -errno, solange das alte Abbild
    /// weiterläuft; 0 heißt: Das neue Abbild läuft, der aufrufende Thread kehrt nicht in das alte Programm zurück.
    /// </summary>
    long Exec(string path, string[] argv, string[] envp);
}

/// <summary>
/// V15 "Prozess startet Prozess" (Ladybird startet WebContent mit posix_spawn; musl: <c>clone(CLONE_VM|CLONE_VFORK|SIGCHLD)</c>,
/// dann execve im Kind). Kein fork mit eigenem Speicher: Das vfork-Kind ist ein eigener Prozess (PID, Deskriptortabelle, Signalhandler,
/// Arbeitsverzeichnis) auf dem Speicher des Elternprozesses, bis es execve oder exit ruft; so lange ist der aufrufende Thread des
/// Elternprozesses blockiert (im Kernel läuft das Kind auf genau diesem Kernelthread). execve lädt das Programm in einen neuen Adressraum
/// (eigene Seitentabelle) und gibt den Elternthread frei.
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxUnixNames
{
    /// <summary>Größter ladbarer Teil einer Datei, den execve liest (V28: Kopf bis Ende des letzten Segments, ohne Debugabschnitte;
    /// wie <c>CosmosLinuxRun.MaxFileSize</c>). Der Teil liegt beim Laden zweimal im Speicher (Puffer und Seiten).</summary>
    public const int MaxExecFileSize = 128 << 20;

    /// <summary>N2-Z: Anfang der Programmdatei, den execve mit einem Lesevorgang holt (ELF-Kopf und Programmtabelle).</summary>
    private const int ExecPrefixBytes = 4096;

    // N2-Z: Der Kernel gibt kurzlebige Puffer nur über die Müllabfuhr zurück; je execve ein neuer 4-KiB-Puffer und ein neues 16-Byte-Array
    // blieb als Heapwachstum stehen. Je ein Puffer wird wiederverwendet (Interlocked: ein Nutzer zur Zeit, jeder weitere legt neu an).
    private static byte[]? s_prefixPool;
    private static byte[]? s_randomPool;

    private const int ORdOnly = 0;
    private const long E2Big = 7, ENoExec = 8;

    // Speicherbesitzer im vfork-Kind (bis execve oder exit); null in jedem Prozess mit eigenem Adressraum.
    private readonly LinuxUserProcess? _vforkOf;
    private readonly LinuxIds _ids;

    /// <summary>V16: Namenstabelle der Unix-Sockets des Laufs (geteilt über <see cref="LinuxIds"/>).</summary>
    public LinuxUnixNames UnixNames => _ids.UnixNames;

    // Kinder dieses Prozesses; die Liste ist zugleich die Sperre (ChildGate), zuunterst in der Reihenfolge wie _threadGate.
    private readonly List<LinuxChild> _children = new();

    // Unter _children: Dieser Prozess ist beendet; beendete Kinder trägt niemand mehr per wait4 aus (Linux: init übernimmt und erntet),
    // ihre PID wird beim Ende sofort frei.
    private bool _reapsOrphans;

    // Nur im ersten Prozess, eigene Sperre (zuunterst, nie mit einer anderen gehalten): tote Prozesse, deren Eintrag ausgetragen ist, die aber
    // noch laufende Kinder haben. Linux hängt solche Waisen an init; hier bleiben sie in der Liste ihres toten Elternprozesses, und kill
    // erreicht sie über diese Liste (LinuxWait.cs). Sie wächst höchstens um einen Eintrag je beendetem Prozess des Laufs.
    private readonly List<LinuxUserProcess> _orphanHolders = new();

    // Nach execve gehört die PID dem neuen Abbild; Release dieses vfork-Kinds gibt sie dann nicht frei.
    private bool _pidPassed;

    // Einmal: die IDs dieses Prozesses sind abgerechnet (Destroy oder Release).
    private bool _idsFreed;

    /// <summary>vfork-Kind: eigener Prozess auf dem Speicher von <paramref name="parent"/> (V15).</summary>
    private LinuxUserProcess(LinuxUserProcess parent)
        : this(parent._pages, parent._terminal, parent._ids.Allocate(), parent._maxFrames * Page, parent._host, parent.FileSystem, parent._openFiles,
            parent._ids)
    {
        _vforkOf = parent;
        Parent = parent;
        _uid = parent._uid;
        _gid = parent._gid;
        ExePath = parent.ExePath;
        StartPath = parent.StartPath;
        HwCap = parent.HwCap;
        CopySignals(parent.Signals, Signals, resetHandlers: false);
        MainThread.Blocked = parent.CurrentThread.Blocked; // V38: Maske des Threads, der vfork ruft
        InheritFiles(parent);
    }

    /// <summary>Elternprozess (getppid); null für den ersten Prozess eines Laufs.</summary>
    public LinuxUserProcess? Parent { get; private set; }

    /// <summary>True im vfork-Kind vor execve oder exit: Es teilt den Speicher des Elternprozesses.</summary>
    public bool InVfork => _vforkOf != null;

    /// <summary>Sperre für <see cref="Children"/> und <see cref="LinuxChild.WaitStatus"/>; wer wartet, nutzt Monitor.Wait auf ihr.</summary>
    public object ChildGate => _children;

    /// <summary>Schnappschuss der Kinder (laufend und beendet, bis wait4 sie austrägt).</summary>
    public LinuxChild[] Children
    {
        get
        {
            lock (_children)
            {
                return _children.ToArray();
            }
        }
    }

    /// <summary>Schnappschuss der toten Prozesse mit noch laufenden Kindern (nur im ersten Prozess gefüllt).</summary>
    public LinuxUserProcess[] OrphanHolders
    {
        get
        {
            lock (_orphanHolders)
            {
                return _orphanHolders.ToArray();
            }
        }
    }

    /// <summary>
    /// Ein Eintrag für <paramref name="dead"/> wurde ausgetragen. Hat er noch laufende Kinder, hebt der erste Prozess ihn auf, damit kill sie
    /// weiter findet. Höchstens unter ChildGate des Elternprozesses von <paramref name="dead"/> rufen (wait4 → Reap): Eltern- vor Kindliste,
    /// nie umgekehrt; die Liste des ersten Prozesses zuunterst.
    /// </summary>
    private void KeepOrphansReachable(LinuxUserProcess dead)
    {
        if (!Array.Exists(dead.Children, c => c.WaitStatus == null))
        {
            return;
        }

        LinuxUserProcess root = this;
        while (root.Parent != null)
        {
            root = root.Parent;
        }

        lock (root._orphanHolders)
        {
            if (!root._orphanHolders.Contains(dead))
            {
                root._orphanHolders.Add(dead);
            }
        }
    }

    /// <summary>AT_HWCAP für execve (Kernel: CPUID 1 EDX); erbt jedes Kind.</summary>
    public uint HwCap { get; set; }

    /// <summary>Speicherobergrenze in Bytes; erbt das Abbild nach execve.</summary>
    public ulong MaxBytes => _maxFrames * Page;

    /// <summary>Seiten dieses Prozesses (die Brücke des Kernels braucht die Tabelle für den Kernelthread des neuen Abbilds).</summary>
    public IUserPages Pages => _pages;

    /// <summary>
    /// Kernel: der aufrufende Kernelthread läuft ab dem Rücksprung als <paramref name="child"/> weiter, mit dem Stack aus clone (0: der
    /// bisherige) und rax 0; die Register des Elternthreads sichert er bis zum Ende der vfork-Phase. false ohne Mittel.
    /// </summary>
    public Func<LinuxUserProcess, ulong, bool>? StartVfork { get; set; }

    /// <summary>Kernel: neue, leere Seitentabelle für execve; null ohne Speicher.</summary>
    public Func<IUserPages?>? CreatePages { get; set; }

    /// <summary>
    /// Kernel: startet <paramref name="next"/> (nach execve, eigener Adressraum) auf einem eigenen Kernelthread am Einsprung des Abbilds
    /// und gibt den Elternthread frei. false, wenn kein Thread startet; der Elternthread wird trotzdem frei.
    /// </summary>
    public Func<LinuxUserProcess, UserImage, bool>? StartExec { get; set; }

    /// <summary>
    /// V19, Kernel: execve ohne vfork. Der aufrufende Thread läuft ab dem Rücksprung als <paramref name="next"/> am Einsprung des Abbilds
    /// weiter (eigene Seitentabelle, Register und FPU frisch). false nur ohne Brücke; dann endet der Prozess mit SIGKILL.
    /// </summary>
    public Func<LinuxUserProcess, UserImage, bool>? ReplaceImage { get; set; }

    // V19: Unter _children: Nach execve ohne vfork gehören die Kinder dem neuen Abbild; ein spätes ChildEnded oder FreeIfOrphaned auf dem
    // alten Objekt (das Kind las Parent vor dem Umzug) geht dorthin weiter.
    private LinuxUserProcess? _childrenMovedTo;

    // V19: prctl PR_SET_PDEATHSIG; Signal an diesen Prozess, wenn sein Elternprozess endet (0: keins). Bleibt über execve (Linux: außer bei
    // setuid-Programmen, die es hier nicht gibt), gilt nicht für ein vfork-Kind des Prozesses (Linux: copy_process setzt es auf 0).
    private int _parentDeathSignal;

    /// <summary>V19 prctl PR_SET_PDEATHSIG / PR_GET_PDEATHSIG.</summary>
    public int ParentDeathSignal
    {
        get => Volatile.Read(ref _parentDeathSignal);
        set => Volatile.Write(ref _parentDeathSignal, value);
    }

    private long CloneVforkProcess(ulong flags, ulong stack)
    {
        // Nur das Austrittssignal (SIGCHLD) neben CLONE_VM|CLONE_VFORK; geteilte Dateien, Handler oder TLS im neuen Prozess gibt es nicht.
        if ((flags & ~(CloneVm | CloneVfork | 0xffUL)) != 0)
        {
            return -LinuxSyscalls.ENOSYS;
        }

        if (InVfork || StartVfork == null)
        {
            return -LinuxSyscalls.ENOSYS;
        }

        var child = new LinuxUserProcess(this);
        var entry = new LinuxChild(child.Pid, child);
        lock (_ids.GroupGate)
        {
            // V35: Gruppe erben und sichtbar werden in einem Schritt, damit ein gleichzeitiges setpgid des Elternprozesses nicht verloren geht.
            child.InheritGroup(this);
            lock (_children)
            {
                _children.Add(entry);
            }
        }

        if (!StartVfork(child, stack))
        {
            lock (_children)
            {
                _children.Remove(entry);
            }

            // Das Kind war nie sichtbar (kein Wartestatus, kein wait4): seine PID wird hier frei, nicht erst beim Ernten.
            child._pidPassed = true;
            child.Release();
            _ids.Remove(child.Pid);
            return -LinuxSyscalls.EAGAIN;
        }

        // Der Thread läuft jetzt als Kind weiter (Rückgabe 0); die PID bekommt der Elternthread, wenn die vfork-Phase endet.
        return 0;
    }

    public long Exec(string path, string[] argv, string[] envp)
    {
        ArgumentNullException.ThrowIfNull(path);
        bool inVfork = InVfork;
        if (CreatePages == null || (inVfork ? StartExec == null || Parent == null : ReplaceImage == null))
        {
            return -LinuxSyscalls.ENOSYS;
        }

        // V19: execve ohne vfork tauscht den Adressraum unter dem aufrufenden Thread. Linux beendet dafür alle anderen Threads (de_thread);
        // das fehlt hier noch (Abweichung): Mit weiteren Threads oder aus einem Nebenthread bleibt execve -ENOSYS, bevor etwas geladen wird.
        if (!inVfork && !OnlyMainThreadLives())
        {
            return -LinuxSyscalls.ENOSYS;
        }

        // V30: Vom Programm nur der Kopfteil; die Segmente liest der neue Prozess beim Zugriff über source (eigener Verweis je Segment).
        long read = ReadProgram(path, true, out byte[] elf, out long fileLength, out ILinuxPageSource? source);
        try
        {
            return ExecRead(path, argv, envp, inVfork, read, elf, fileLength, ref source);
        }
        finally
        {
            source?.Release();
        }
    }

    private long ExecRead(string path, string[] argv, string[] envp, bool inVfork, long read, byte[] elf, long fileLength, ref ILinuxPageSource? source)
    {
        bool startsSelf = ExePath != null && string.Equals(LinuxPath.Normalize(path), ExePath, StringComparison.OrdinalIgnoreCase);
        if (read == -LinuxErrno.ENOENT && ExePath != null && StartPath != null
            && string.Equals(LinuxPath.Normalize(path), StartPath, StringComparison.OrdinalIgnoreCase))
        {
            // V26: argv[0] ist der Pfad, unter dem der Starter das Programm kennt (Gastprobe: "exec" im Arbeitsverzeichnis, Shell:
            // Kernelpfad außerhalb der Prozesswurzel). Ein Programm, das sich damit selbst startet, findet es unter seinem Pfad in der Wurzel.
            path = ExePath;
            startsSelf = true;
            source?.Release();
            source = null;
            read = ReadProgram(path, true, out elf, out fileLength, out source);
        }

        if (read < 0)
        {
            return read;
        }

        byte[] random = System.Threading.Interlocked.Exchange(ref s_randomPool, null) ?? new byte[16];
        try
        {
            return ExecWithRandom(path, argv, envp, inVfork, elf, fileLength, source, startsSelf, random);
        }
        finally
        {
            s_randomPool = random; // ElfLoader kopiert die Bytes in den Stack, nichts hält den Verweis
        }
    }

    private long ExecWithRandom(string path, string[] argv, string[] envp, bool inVfork, byte[] elf, long fileLength, ILinuxPageSource? source, bool startsSelf, byte[] random)
    {
        if (!TryFillRandom(random))
        {
            return -LinuxSyscalls.EAGAIN;
        }

        UserImage image;
        try
        {
            // V25: Der Interpreter (PT_INTERP) kommt wie das Programm aus dem Dateisystem des Prozesses.
            image = ElfLoader.Load(elf, argv, envp, new ElfLoadOptions
            {
                Random = random,
                HwCap = HwCap,
                ExecFn = path,
                Uid = _uid,
                Gid = _gid,
                ReadInterpreter = ReadExecutable,
                FileLength = source != null ? fileLength : null,
            });
        }
        catch (ElfLoadException exception)
        {
            return exception.Kind switch
            {
                ElfLoadError.ArgumentsTooLong => -E2Big,
                ElfLoadError.InvalidArgument => -LinuxSyscalls.EINVAL,
                ElfLoadError.InterpreterUnavailable => -exception.Errno,
                _ => -ENoExec,
            };
        }

        IUserPages? pages = CreatePages?.Invoke(); // Exec hat CreatePages geprüft
        if (pages == null)
        {
            return -LinuxSyscalls.ENOMEM;
        }

        var next = new LinuxUserProcess(pages, _terminal, Pid, MaxBytes, _host, FileSystem, _openFiles, _ids)
        {
            HwCap = HwCap,
            ExePath = path,
            // V26: Nur ein Neustart des eigenen Programms behält den Startpfad; ein anderes Programm hat keinen (sonst würde ein späteres
            // execve auf argv[0] fälschlich das ursprüngliche Programm statt ENOENT starten).
            StartPath = startsSelf ? StartPath : null,
        };
        if (!(source != null ? next.Load(image, elf, source) : next.Load(image, elf)).IsOK())
        {
            next.DestroyMemory();
            return -LinuxSyscalls.ENOMEM;
        }

        // Ab hier kein Rückweg (Linux: point of no return): Dateien, Arbeitsverzeichnis und Signalmaske wandern ins neue Abbild.
        next.TakeOver(this);
        if (!inVfork)
        {
            return ReplaceOwnImage(next, image);
        }

        Parent!.ReplaceChild(Pid, next);
        ulong killed = HandOverKill(next); // kill während vfork (LinuxWait.cs)
        if (!StartExec!(next, image))
        {
            next.ReportEnd(9);
            next.Destroy();
        }
        else if (killed != 0)
        {
            DeliverKillsFromVfork(next, killed);
        }

        return 0;
    }

    /// <summary>Liest den ladbaren Teil des Programms über das Dateisystem des Prozesses (reguläre Dateien, Teil bis <see cref="MaxExecFileSize"/>).</summary>
    private long ReadExecutable(string path, out byte[] elf) => ReadProgram(path, false, out elf, out _, out _);

    /// <summary>
    /// V30: Mit <paramref name="lazy"/> und einer regulären Datei des Speichers nur der Kopfteil (<see cref="ElfFile.HeadLength"/>) und eine
    /// Quelle mit eigenem Verweis für die Segmente (der Aufrufer gibt ihn zurück); sonst wie bisher der ganze ladbare Teil, Quelle null.
    /// </summary>
    private long ReadProgram(string path, bool lazy, out byte[] elf, out long fileLength, out ILinuxPageSource? source)
    {
        elf = [];
        fileLength = 0;
        source = null;
        lock (_fileGate)
        {
            long r = FileSystem.Open(path, ORdOnly, 0, out ILinuxFile? file);
            if (r < 0 || file == null)
            {
                return r < 0 ? r : -LinuxErrno.ENOENT;
            }

            byte[]? rentedPrefix = null;
            try
            {
                r = file.Stat(out LinuxStat st);
                if (r < 0)
                {
                    return r;
                }

                if ((st.Mode & LinuxMode.TypeMask) != LinuxMode.Regular)
                {
                    return -LinuxSyscalls.EACCES;
                }

                // Kein Ausführrecht in keinem der drei Teile: -EACCES (Linux), vor der Größen- und ELF-Prüfung.
                if ((st.Mode & 0x49) == 0)
                {
                    return -LinuxSyscalls.EACCES;
                }

                if (st.Size < 0)
                {
                    return -LinuxSyscalls.ENOMEM;
                }

                // V28: nur Kopf, Programmköpfe und Segmente; die Grenze gilt für diesen Teil, nicht für Debugabschnitte dahinter.
                // N2-Z: Kopf, Programmtabelle und der Rückgabepuffer des Kopfteils liegen meist in den ersten Bytes. Jeder Lesevorgang der Platte
                // kostet im Kern Puffer (FAT liest je Aufruf Clustergröße): ein Lesevorgang am Anfang statt drei je Start.
                ILinuxFile f = file;
                byte[] prefix = rentedPrefix = System.Threading.Interlocked.Exchange(ref s_prefixPool, null) ?? new byte[ExecPrefixBytes];
                int wanted = (int)Math.Min(st.Size, ExecPrefixBytes);
                long prefixLength = 0;
                if (wanted > 0 && f.Seek(0, 0) >= 0)
                {
                    long got = f.Read(prefix.AsSpan(0, wanted));
                    prefixLength = got > 0 ? got : 0;
                }

                long ReadAt(long offset, Span<byte> dst)
                {
                    if (offset >= 0 && offset < prefixLength)
                    {
                        int n = (int)Math.Min(dst.Length, prefixLength - offset);
                        prefix.AsSpan((int)offset, n).CopyTo(dst);
                        return n;
                    }

                    long at = f.Seek(offset, 0);
                    return at < 0 ? at : f.Read(dst);
                }

                ILinuxPageSourceFile? sourceFile = lazy ? file as ILinuxPageSourceFile : null;
                long needed;
                r = sourceFile != null ? ElfFile.HeadLength(st.Size, ReadAt, out needed) : ElfFile.LoadedLength(st.Size, ReadAt, out needed);
                if (r < 0)
                {
                    return r;
                }

                if (needed > MaxExecFileSize)
                {
                    return -LinuxSyscalls.ENOMEM;
                }

                byte[] bytes = new byte[needed];
                r = ElfFile.ReadFull(ReadAt, 0, bytes);
                if (r < 0)
                {
                    return r;
                }

                elf = r == bytes.Length ? bytes : bytes.AsSpan(0, (int)r).ToArray();
                fileLength = st.Size;
                source = sourceFile?.OpenPageSource();
                return 0;
            }
            finally
            {
                if (rentedPrefix != null)
                {
                    s_prefixPool = rentedPrefix; // ReadAt endet mit ReadProgram; nur der Kopf wird kopiert (bytes)
                }

                _ = file.Close();
            }
        }
    }

    /// <summary>execve: übernimmt Deskriptoren (die Tabelle des vfork-Kinds wird leer), Arbeitsverzeichnis, Eltern und Signalzustand.</summary>
    private void TakeOver(LinuxUserProcess from)
    {
        Parent = from.Parent;
        from._pidPassed = true;
        _uid = from._uid;
        _gid = from._gid;
        ParentDeathSignal = from.ParentDeathSignal;
        lock (_fileGate)
        {
            // Die Nummern wandern, die Zählung in LinuxOpenFiles bleibt gleich.
            Array.Copy(from._descriptors, _descriptors, MaxDescriptors);
            Array.Copy(from._closeOnExec, _closeOnExec, MaxDescriptors);
            Array.Clear(from._descriptors);
            Array.Clear(from._closeOnExec);
            _cwd = from._cwd;
        }

        // Linux: Handler fallen auf SIG_DFL, SIG_IGN und die Signalmaske bleiben, der Alternativstack nicht.
        CopySignals(from.Signals, Signals, resetHandlers: true);
        MainThread.Blocked = from.CurrentThread.Blocked; // V38: Maske des Threads, der execve ruft (im vfork-Kind dessen Hauptthread)
        TakeOverGroup(from); // V35: Gruppe und Sitzung bleiben, setpgid des Elternprozesses ist ab hier -EACCES
        CloseOnExec();
    }

    /// <summary>
    /// fork-Regel für die Deskriptortabelle (V15): eine Kopie der Nummern samt FD_CLOEXEC auf dieselben offenen Dateien (gleiche Position),
    /// dazu das Arbeitsverzeichnis. Jede kopierte Nummer zählt in <see cref="LinuxOpenFiles"/>; die Datei schließt erst die letzte Nummer
    /// irgendeines Prozesses (close im Kind lässt die Pipe der Eltern offen).
    /// </summary>
    private void InheritFiles(LinuxUserProcess parent)
    {
        lock (_fileGate)
        {
            for (int fd = 0; fd < MaxDescriptors; fd++)
            {
                ILinuxDescriptor? d = parent._descriptors[fd];
                _descriptors[fd] = d;
                _closeOnExec[fd] = d != null && parent._closeOnExec[fd];
                if (d != null)
                {
                    _openFiles.Add(d);
                }
            }

            _cwd = parent._cwd;
        }
    }

    private static void CopySignals(LinuxSignalState from, LinuxSignalState to, bool resetHandlers)
    {
        for (int sig = 0; sig < from.Handler.Length; sig++)
        {
            bool keep = !resetHandlers || from.Handler[sig] == 1;
            to.Handler[sig] = keep ? from.Handler[sig] : 0;
            to.Flags[sig] = keep ? from.Flags[sig] : 0;
            to.Restorer[sig] = keep ? from.Restorer[sig] : 0;
            to.Mask[sig] = keep ? from.Mask[sig] : 0;
        }

        to.Blocked = from.Blocked; // nur Buchführung; die Maske eines LinuxUserProcess trägt seit V38 der Thread (LinuxThread.Blocked)
        if (!resetHandlers)
        {
            to.AltSp = from.AltSp;
            to.AltSize = from.AltSize;
            to.AltFlags = from.AltFlags;
        }
    }

    /// <summary>
    /// Ende der vfork-Phase ohne eigenes Abbild (exit vor execve oder nach execve): Das Objekt wird nicht mehr gebraucht. Seine Nummern
    /// fallen weg wie bei exit; eine Datei schließt nur, wenn sonst niemand sie hält (nach execve ist die Tabelle leer).
    /// </summary>
    public void Release()
    {
        if (_vforkOf == null)
        {
            throw new InvalidOperationException("Release nur für vfork-Kinder; ein Prozess mit eigenem Speicher endet mit Destroy.");
        }

        CloseAllFiles();
        FreeIds();
    }

    /// <summary>
    /// Ende des Prozesses (Destroy, Release): TIDs der Nebenthreads werden frei; die PID erst, wenn niemand mehr den Wartestatus abholen
    /// kann (kein Elternprozess oder Elternprozess beendet), sonst mit <see cref="Reap"/> wie bei Linux. Beendete Kinder dieses Prozesses
    /// geben ihre PID jetzt frei, laufende beim eigenen Ende (<see cref="ChildEnded"/>).
    /// Sperren nacheinander, nie verschachtelt außer <c>_children</c> vor <see cref="LinuxIds"/> (zuunterst).
    /// </summary>
    private void FreeIds()
    {
        lock (_threadGate)
        {
            if (_idsFreed)
            {
                return;
            }

            _idsFreed = true;
            foreach (LinuxThread t in _threads)
            {
                if (!ReferenceEquals(t, MainThread))
                {
                    _ids.Remove(t.Tid);
                }
            }
        }

        List<LinuxUserProcess>? removed = null;
        lock (_children)
        {
            _reapsOrphans = true;
            foreach (LinuxChild c in _children)
            {
                if (c.WaitStatus != null)
                {
                    _ids.Remove(c.Pid);
                    (removed ??= new List<LinuxUserProcess>()).Add(c.Process);
                }
            }

            _children.RemoveAll(c => c.WaitStatus != null);
        }

        if (removed != null)
        {
            foreach (LinuxUserProcess dead in removed)
            {
                KeepOrphansReachable(dead);
            }
        }

        if (!_pidPassed && Parent == null)
        {
            _ids.Remove(Pid);
        }
        else if (!_pidPassed)
        {
            Parent!.FreeIfOrphaned(Pid);
        }
    }

    /// <summary>Elternprozess schon beendet: niemand erntet mehr; die PID des beendeten Kinds wird frei.</summary>
    private void FreeIfOrphaned(int pid)
    {
        LinuxChild? removed = null;
        LinuxUserProcess? successor;
        lock (_children)
        {
            successor = _childrenMovedTo;
            if (successor == null && (_reapsOrphans || _children.Exists(c => c.Pid == pid && c.AutoReaped)))
            {
                removed = _children.Find(c => c.Pid == pid);
                _children.RemoveAll(c => c.Pid == pid);
                _ids.Remove(pid);
            }
        }

        if (successor != null)
        {
            successor.FreeIfOrphaned(pid);
        }
        else if (removed != null)
        {
            KeepOrphansReachable(removed.Process);
        }
    }

    /// <summary>
    /// wait4/waitid ohne WNOWAIT: trägt das beendete Kind <paramref name="pid"/> aus und gibt seine PID frei. false, wenn es kein
    /// beendetes Kind mit dieser PID gibt (läuft noch oder unbekannt). Ruft man unter <see cref="ChildGate"/> oder ohne Sperre.
    /// </summary>
    public bool Reap(int pid)
    {
        LinuxUserProcess dead;
        lock (_children)
        {
            int i = _children.FindIndex(c => c.Pid == pid && c.WaitStatus != null);
            if (i < 0)
            {
                return false;
            }

            dead = _children[i].Process;
            _children.RemoveAt(i);
            _ids.Remove(pid);
        }

        KeepOrphansReachable(dead);
        return true;
    }

    /// <summary>Test und Diagnose: ist <paramref name="id"/> als PID oder TID im Lauf belegt?</summary>
    internal bool IdInUse(int id) => _ids.InUse(id);

    /// <summary>Ein Thread endete mit einer Ausnahme im Ring 3 (Kernel): Linux beendet den Prozess mit dem Signal der Ausnahme.</summary>
    public void RecordFault(int signal)
    {
        lock (_threadGate)
        {
            TerminatedBySignal ??= signal;
        }
    }

    /// <summary>
    /// Meldet das Ende an den Elternprozess (einmal); weckt Wartende auf <see cref="ChildGate"/>. Schließt vorher alle offenen Dateien:
    /// Linux schließt sie in <c>do_exit</c> vor dem Zombie, wait4 kehrt also erst zurück, wenn Pipe-Enden und Dateien des Kinds zu sind
    /// (sonst sähe der Elternprozess nach wait4 noch kein EOF), und die PID wird nicht geerntet und neu vergeben, solange das alte Objekt
    /// noch Dateien hält. <see cref="Destroy"/> und <see cref="Release"/> finden danach eine leere Tabelle.
    /// </summary>
    public void ReportEnd(int waitStatus)
    {
        CloseAllFiles();
        SignalParentDeath();
        Parent?.ChildEnded(Pid, waitStatus);
    }

    /// <summary>Wartestatus aus dem Ende: Signal (tgkill, Abbruch) oder exit; <paramref name="fallbackSignal"/>, wenn keins von beiden.</summary>
    public int WaitStatusOf(int fallbackSignal) =>
        TerminatedBySignal is int signal ? signal & 0x7f : ExitCode is int code ? (code & 0xff) << 8 : fallbackSignal & 0x7f;

    private void ChildEnded(int pid, int waitStatus)
    {
        bool ended = false;
        LinuxUserProcess? successor;
        lock (_children)
        {
            successor = _childrenMovedTo;
            LinuxChild? child = _children.Find(c => c.Pid == pid);
            if (child != null && child.WaitStatus == null)
            {
                child.WaitStatus = waitStatus;
                // Kein Zombie (LinuxWait.cs): wait4 sieht das Kind nicht mehr; Eintrag und PID räumt FreeIfOrphaned, sobald das Kind
                // seine IDs abrechnet (danach kann die PID nicht mehr an einen noch laufenden Endpfad vergeben werden).
                child.AutoReaped = ReapsChildrenAutomatically();
                ended = true;
            }

            Monitor.PulseAll(_children);
        }

        if (successor != null && !ended)
        {
            successor.ChildEnded(pid, waitStatus); // V19: Kinder sind mit execve umgezogen
            return;
        }

        // V18: SIGCHLD an die Eltern wie Linux (do_notify_parent), außerhalb von ChildGate (DeliverSignal nimmt _threadGate und weckt).
        // Standard und SIG_IGN verwerfen es (IsIgnored); siginfo nennt das Kind: CLD_EXITED 1 mit Exitcode, CLD_KILLED 2/CLD_DUMPED 3 mit Signal.
        if (ended)
        {
            bool exited = LinuxWaitStatus.IsExited(waitStatus);
            int code = exited ? 1 : (waitStatus & 0x80) != 0 ? 3 : 2;
            int value = exited ? LinuxWaitStatus.ExitCode(waitStatus) : LinuxWaitStatus.TermSignal(waitStatus);
            _ = DeliverSignal(SigChld, fromOutside: true, code, pid, value);
        }
    }

    /// <summary>V19: Der aufrufende Thread ist der Hauptthread und der einzige, der noch läuft (Bedingung für execve ohne vfork).</summary>
    private bool OnlyMainThreadLives()
    {
        lock (_threadGate)
        {
            return _liveThreads == 1 && !MainThread.Exited && ReferenceEquals(CurrentThread, MainThread);
        }
    }

    /// <summary>
    /// V19 execve ohne vfork, nach <see cref="TakeOver"/>: Kinder, Waisenliste und kill-Weiterleitung ziehen zu <paramref name="next"/>, der
    /// Elternprozess sieht unter derselben PID das neue Abbild, die Brücke setzt den Thread auf dessen Einsprung. Danach gibt dieses Objekt
    /// seinen Speicher frei (die PID bleibt, sie gehört jetzt <paramref name="next"/>). Liefert 0; der Thread kehrt in das neue Programm zurück.
    /// </summary>
    private long ReplaceOwnImage(LinuxUserProcess next, UserImage image)
    {
        MoveChildren(next);
        Parent?.ReplaceChild(Pid, next);
        _ = HandOverKill(next); // ein späteres kill trifft das neue Abbild; ohne vfork gibt es keine gemerkten Signale

        // Ein kill, der vor der Übergabe ankam, hat dieses Objekt schon beendet: Der Status gilt für das neue Abbild, dessen Thread verlässt
        // den Ring 3 mit der schon angeforderten Rückkehr.
        lock (_threadGate)
        {
            if (ExitCode != null)
            {
                next.ExitCode = ExitCode;
                next.TerminatedBySignal = TerminatedBySignal;
            }
        }

        if (!ReplaceImage!(next, image))
        {
            next.ReportEnd(9);
            next.Destroy();
            Terminate(9);
            return 0;
        }

        Destroy();
        return 0;
    }

    /// <summary>V19: Kinder (laufend und beendet) gehören nach execve dem neuen Abbild; ihr getppid und wait4 bleiben gleich.</summary>
    private void MoveChildren(LinuxUserProcess next)
    {
        lock (_children)
        {
            lock (next._children)
            {
                foreach (LinuxChild c in _children)
                {
                    next._children.Add(c);
                    c.Process.Parent = next;
                }

                _children.Clear();
                _childrenMovedTo = next;
            }
        }

        lock (_orphanHolders)
        {
            lock (next._orphanHolders)
            {
                next._orphanHolders.AddRange(_orphanHolders);
                _orphanHolders.Clear();
            }
        }
    }

    /// <summary>
    /// V19 PR_SET_PDEATHSIG: Endet dieser Prozess, bekommen seine laufenden Kinder ihr gewähltes Signal (Linux: si_code SI_USER, si_pid 0).
    /// Ein vfork-Kind auf diesem Thread gibt es dann nicht mehr (es blockierte den Thread); Kinder nach execve tragen den Wert aus TakeOver.
    /// </summary>
    private void SignalParentDeath()
    {
        foreach (LinuxChild c in Children)
        {
            int signal = c.Process.ParentDeathSignal;
            if (c.WaitStatus == null && signal != 0)
            {
                _ = c.Process.DeliverSignal(signal, fromOutside: true, SiUser, 0);
            }
        }
    }

    private void ReplaceChild(int pid, LinuxUserProcess next)
    {
        lock (_children)
        {
            LinuxChild? child = _children.Find(c => c.Pid == pid);
            if (child != null)
            {
                child.Process = next;
            }
        }
    }
}
