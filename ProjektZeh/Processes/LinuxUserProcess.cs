using System;
using System.Collections.Generic;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shared;

namespace ProjektZeh.Processes;

/// <summary>
/// Seiten eines Benutzeradressraums, wie sie <see cref="LinuxUserProcess"/> braucht. Im Kernel steckt dahinter eine
/// <c>UserPageTable</c> (<c>CosmosUserPages</c>), in den Hosttests ein Wörterbuch. Ein Frame gehört dem Prozess, bis er ihn
/// zurückgibt; eine Seite mit <see cref="MapAccess.None"/> wird nie abgebildet.
/// </summary>
internal interface IUserPages
{
    /// <summary>Ein genullter Frame oder 0 ohne Speicher.</summary>
    ulong AllocateFrame();

    /// <summary>Gibt einen Frame zurück, den keine Seite mehr abbildet.</summary>
    void FreeFrame(ulong frame);

    /// <summary>Die 4096 Bytes des Frames, für den Kernel lesbar und schreibbar.</summary>
    Span<byte> Frame(ulong frame);

    /// <summary>Bildet die Seite mit dem Zugriff ab (nicht <see cref="MapAccess.None"/>); false, wenn schon abgebildet oder abgelehnt.</summary>
    bool Map(ulong page, ulong frame, MapAccess access);

    /// <summary>Hebt die Abbildung auf; der Frame bleibt beim Prozess. true, wenn die Seite danach nicht (mehr) abgebildet ist.</summary>
    bool Unmap(ulong page);

    /// <summary>Gibt die Tabelle selbst frei, nicht die Frames; danach bildet nichts mehr ab.</summary>
    void DestroyTable();
}

/// <summary>
/// V38, optional neben <see cref="IUserPages"/>: Ist die Seite schon mit diesem Frame abgebildet (mit <paramref name="write"/> auch
/// schreibbar)? Für den Seitenfehler, den ein anderer Thread zwischen Ausnahme und <c>_memGate</c> schon erledigt hat (Linux: "spurious
/// fault", der Befehl wird einfach wiederholt); V38b auch für ein mprotect, das die Seite inzwischen schreibbar gemacht hat.
/// </summary>
internal interface IUserPagesQuery
{
    bool MapsTo(ulong page, ulong frame, bool write);
}

/// <summary>
/// N4-S2, optional neben <see cref="IUserPages"/>: zusammenhängende Seiten mit einem TLB-Abschuss für alle statt einem je Seite. Auf
/// mehreren CPUs wartet jeder Abschuss auf die Bestätigung jeder CPU, die die Tabelle geladen hat; munmap über viele Seiten zahlte sonst
/// diese Wartezeit je Seite. Bedeutung wie die Einzelaufrufe: Nach der Rückkehr erreicht keine CPU die Seite mehr über den alten Eintrag.
/// </summary>
internal interface IUserPagesRange
{
    /// <summary>Wie <see cref="IUserPages.Unmap"/> für <paramref name="count"/> Seiten ab <paramref name="start"/>; <paramref name="gone"/>[i]: Seite danach nicht (mehr) abgebildet.</summary>
    void UnmapRange(ulong start, int count, Span<bool> gone);

    /// <summary>Setzt den Zugriff (nicht <see cref="MapAccess.None"/>) aller Seiten; false ohne jede Änderung, wenn eine nicht abgebildet ist oder die Tabelle ablehnt.</summary>
    bool ProtectRange(ulong start, int count, MapAccess access);
}

/// <summary>
/// Ein Linux-Prozess mit einem Thread (V3 des Plans <c>2026-11-26-ladybird-vorstellung.md</c>): Speicher, brk, anonyme
/// Abbildungen, Deskriptoren 0 bis 2 auf das Terminal und Dateien über ein <see cref="ILinuxFileSystem"/> (V8: Deskriptortabelle und
/// Arbeitsverzeichnis, <see cref="ILinuxFiles"/>). Die Bereiche führt <see cref="AddressSpaceMap"/>; Frames kommen erst bei
/// Bedarf (<see cref="HandlePageFault"/> oder ein Kernelzugriff über <see cref="IUserMemory"/>), nur die Seiten mit Dateiinhalt
/// und der vorbereitete Teil des Stacks werden beim Laden gefüllt. Plattformunabhängig und auf dem Host geprüft.
/// <para>
/// Threads (V10): Aufrufe und Seitenfehler kommen von allen Threads des Prozesses (die Handler von <c>UserMode</c> laufen auf dem
/// Kernelstack des jeweiligen Threads); Speicher schützt <c>_memGate</c>, die Threadtabelle <c>_threadGate</c>, Dateien und
/// Arbeitsverzeichnis <c>_fileGate</c> (V11, genommen vom Verteiler). Laden vor dem
/// Start und <see cref="Destroy"/> nach dem Ende aller Threads laufen ohne Nebenläufigkeit.
/// </para>
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxCpuTime, ILinuxProcess, IUserMemory, ILinuxHost, ILinuxFiles, ILinuxAddressSpace, ILinuxThreads, ILinuxAbort, ILinuxExe,
    ILinuxSystemInfo, ILinuxCpus, ILinuxSpawn, ILinuxFdFlags, ILinuxOpenFileCount, ILinuxWait, ILinuxInFlight, ILinuxFileLocking
{
    /// <summary>
    /// Größe der Deskriptortabelle; gleich der Grenze, bis zu der der Verteiler bei close nach weiteren Nummern derselben Datei sucht
    /// (<c>LinuxSyscalls.FileMaxFds</c>). Wie RLIMIT_NOFILE unter Linux standardmäßig 1024.
    /// </summary>
    public const int MaxDescriptors = 1024;

    private const ulong Page = AddressSpaceMap.PageSize;
    private const int ProtRead = 1, ProtWrite = 2, ProtExec = 4;
    private const ulong FaultPresent = 1, FaultWrite = 2, FaultFetch = 0x10;

    /// <summary>Wunschlage anonymer Abbildungen (16 TiB): weit über brk eines Programms bei 4 MiB und unter statisch-PIE.</summary>
    public const ulong MmapBase = 0x0000_1000_0000_0000;

    private readonly IUserPages _pages;
    private readonly AddressSpaceMap _map = new(ElfLoader.MinAddress, ElfLoader.UserEnd);
    private readonly Dictionary<ulong, ulong> _frames = new();

    // V30: Bereiche, deren Seiten beim ersten Zugriff aus einer Datei kommen (PT_LOAD, mmap MAP_PRIVATE); unter _memGate wie _frames.
    private readonly LinuxFileExtents _fileExtents = new();
    private readonly ILinuxDescriptor _terminal;
    private readonly ILinuxHost? _host;
    private readonly ILinuxDescriptor?[] _descriptors = new ILinuxDescriptor?[MaxDescriptors];

    // V15: FD_CLOEXEC je Nummer (gehört zur Nummer, nicht zur Datei); geschützt wie die Tabelle durch FileGate.
    private readonly bool[] _closeOnExec = new bool[MaxDescriptors];
    private string _cwd = "/";

    // V11: Threads teilen Deskriptortabelle, Arbeitsverzeichnis und Dateisystem. Der Verteiler nimmt die Sperre für jeden
    // Dateiaufruf (ILinuxFiles.FileGate), im Threadkontext, nie im Interrupt. Platz in der Reihenfolge: vor _memGate (Kopieren in
    // den Benutzerspeicher) und vor StorageLock (Platte); nie zusammen mit der Sperre der futex-Tabelle.
    // V15: Die Sperre ist die Zähltabelle LinuxOpenFiles, die alle Prozesse eines Laufs teilen, weil sie nach vfork dieselben offenen
    // Dateien haben (Referenzzählung über Tabellen hinweg).
    private readonly object _fileGate;
    private readonly LinuxOpenFiles _openFiles;
    private readonly ulong _maxFrames;

    /// <summary>
    /// V43b: Grenze über private Seiten und die Frames der memfds dieses Prozesses (<see cref="LinuxFrameBudget"/>). Privat heißt hier:
    /// alle Seiten in <c>_frames</c> außer geteilten aus einer gebuchten Datei (<c>_budgetedShared</c>), die schon beim Erzeuger zählen.
    /// </summary>
    private readonly LinuxFrameBudget _budget;
    private int _budgetedShared;

    /// <summary>V43: Ein <see cref="NewFrame"/> unter <see cref="_memGate"/> bekam keinen Frame; nur für den laufenden Seitenfehler.</summary>
    private bool _noFrame;
    /// <summary>V47: <see cref="_noFrame"/> kam vom Kernel (kein freier Frame), nicht von der Prozessgrenze; nur dann lohnt GC.Collect.</summary>
    private bool _frameShort;

    // V10: Threads teilen den Adressraum. Diese Sperre schützt Karte, Frames und brk; sie wird in Aufrufen und Seitenfehlern
    // genommen (Threadkontext, Interrupts an), nie im Interrupt. Platz in der Reihenfolge: nach der Sperre der futex-Tabelle
    // (LinuxFutexTable liest den Wert unter ihrer Sperre), vor der Sperre der UserPageTable (in IUserPages) und des PageAllocator.
    private readonly object _memGate = new();
    private ulong _brkStart;
    private ulong _brk;
    private ulong _uid = 1000;
    private ulong _gid = 1000;
    private ulong _stackSize = 8UL << 20;
    private ulong _stackTop; // V29: Ende des Hauptstacks für /proc ([stack], startstack); unter _memGate wie _stackSize

    /// <param name="maxBytes">Obergrenze für den Speicher des Prozesses; darüber endet ein Seitenfehler mit Abbruch.</param>
    /// <param name="host">Zeit, Zufall und Schlafen des Kernels (V2b); ohne Host liefern Uhren 0 und getrandom scheitert.</param>
    /// <param name="fileSystem">Dateisystem des Prozesses (V7); ohne Angabe ein leeres im Arbeitsspeicher, das mit dem Prozess endet.</param>
    /// <param name="openFiles">V15: Zähltabelle offener Dateien und zugleich Dateisperre, die alle Prozesse eines Laufs teilen; ohne Angabe eine eigene.</param>
    /// <param name="ids">V15: gemeinsame Vergabe von PIDs und TIDs aller Prozesse eines Laufs; ohne Angabe eine eigene ab <paramref name="pid"/>.</param>
    public LinuxUserProcess(IUserPages pages, ILinuxDescriptor terminal, int pid, ulong maxBytes, ILinuxHost? host = null,
        ILinuxFileSystem? fileSystem = null, LinuxOpenFiles? openFiles = null, LinuxIds? ids = null)
    {
        _openFiles = openFiles ?? new LinuxOpenFiles();
        _fileGate = _openFiles;
        _ids = ids ?? new LinuxIds(pid);
        _ids.Add(pid);
        _host = host;
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        // V34: Eigentümer wie die Standard-Benutzernummer des Laders (ElfLoader.Options.Uid/Gid), damit stat und getuid übereinstimmen.
        FileSystem = fileSystem ?? new LinuxFileSystem(new MemoryFileStore(), terminal, uid: 1000, gid: 1000);

        // 0 bis 2 zeigen auf das Terminal wie bisher; das Terminal gehört dem Prozess, nicht der Tabelle (close schließt es nicht).
        _descriptors[0] = terminal;
        _descriptors[1] = terminal;
        _descriptors[2] = terminal;
        Pid = pid;
        _pgid = pid; // V35: allein in Gruppe und Sitzung, bis fork, vfork oder execve sie vom Elternprozess übernehmen
        _sid = pid;
        _maxFrames = maxBytes / Page;
        _budget = new LinuxFrameBudget(_maxFrames);

        // Der Hauptthread trägt die PID als TID (Linux: Thread-Gruppenführer); weitere TIDs vergibt _ids (NextTid über alle Prozesse).
        MainThread = new LinuxThread(pid);
        _threads.Add(MainThread);
        _liveThreads = 1;
        Futexes = new LinuxFutexTable(() => MonotonicNanos);
        Futexes.SignalWaiting = () => SignalWaiting; // V18: ein Signal mit Handler unterbricht futex-Wartende
    }

    /// <summary>Wird beim Exit gerufen; im Kernel verlässt das den Ring 3 (<c>UserMode.LeaveCurrent</c>).</summary>
    public Action<int>? OnExit { get; set; }

    /// <summary>Wird beim Setzen der FS-Basis gerufen; im Kernel <c>UserMode.SetFsBase</c>.</summary>
    public Action<ulong>? OnFsBase { get; set; }

    /// <summary>Exitcode, sobald der Prozess exit oder exit_group gerufen hat.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Anzahl der Frames, die der Prozess gerade hält.</summary>
    public int FrameCount => _vforkOf?.FrameCount ?? _frames.Count;

    public IUserMemory Memory => this;

    public int Pid { get; }

    public ulong Brk => _vforkOf?.Brk ?? _brk;

    /// <summary>Kommt aus dem geladenen Abbild (AT_UID); vor <see cref="Load"/> gilt 1000.</summary>
    public ulong Uid => _uid;

    public ulong Gid => _gid;

    /// <summary>Größe der Stackabbildung aus dem geladenen Abbild (RLIMIT_STACK, V9); vor <see cref="Load"/> der Linux-Standard.</summary>
    public ulong StackSize => _vforkOf?.StackSize ?? _stackSize;

    public bool IsMapped(ulong addr, ulong len)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.IsMapped(addr, len);
        }

        // V10: Die Karte ändern andere Threads (munmap, mmap) unter derselben Sperre.
        lock (_memGate)
        {
            return _map.IsMapped(addr, len);
        }
    }

    public ulong ClearChildTid
    {
        get => CurrentThread.ClearChildTid;
        set => CurrentThread.ClearChildTid = value;
    }

    /// <summary>FS-Basis des aufrufenden Threads (V10: je Thread).</summary>
    public ulong FsBase
    {
        get => CurrentThread.FsBase;
        set
        {
            CurrentThread.FsBase = value;
            OnFsBase?.Invoke(value);
        }
    }

    // ---- V10 Threads ----

    /// <summary>Höchstzahl gleichzeitiger Threads; darüber scheitert clone mit -EAGAIN (wie RLIMIT_NPROC).</summary>
    public const int MaxThreads = 64;

    /// <summary>
    /// Größte TID (Linux <c>PID_MAX_LIMIT</c>, 2^22). Danach beginnt die Vergabe wieder unten; robuste Mutexe und futex-Eigentümer
    /// brauchen TIDs unter <c>FUTEX_TID_MASK</c> (2^30), ein Überlauf ins Negative darf nie entstehen (V10-Review P3).
    /// </summary>
    public const int TidMax = 1 << 22;

    public const ulong CloneVm = 0x100, CloneFs = 0x200, CloneFiles = 0x400, CloneSighand = 0x800, CloneVfork = 0x4000,
        CloneParent = 0x8000, CloneThreadFlag = 0x10000, CloneSysvsem = 0x40000, CloneSettls = 0x80000, CloneParentSettid = 0x100000,
        CloneChildCleartid = 0x200000, CloneDetached = 0x400000, CloneChildSettid = 0x01000000;

    /// <summary>Was ein Thread von pthread_create (musl, glibc) teilt; ohne alle diese Bits wäre es ein neuer Prozess (fork).</summary>
    public const ulong CloneThreadSet = CloneVm | CloneFs | CloneFiles | CloneSighand | CloneThreadFlag | CloneSysvsem;

    private const ulong CloneKnownExtras = CloneSettls | CloneParentSettid | CloneChildCleartid | CloneChildSettid | CloneDetached | 0xff;

    // Sperrreihenfolge: _threadGate steht zuunterst; darin keine Speicher-, Datei- oder futex-Sperre (Schreiben und Wecken geschehen danach).
    private readonly object _threadGate = new();

    // Laufende Threads und der Hauptthread; beendete Kinder fallen beim nächsten clone heraus, damit die Liste nicht mit jedem
    // kurzlebigen Thread wächst und ihre TIDs wieder frei werden.
    private readonly List<LinuxThread> _threads = new();
    private int _liveThreads;

    // Anhängige Signale (Bit = Nummer - 1), unter _threadGate: tgkill eines blockierten Signals; je Prozess (die Maske seit V38 je Thread).
    private ulong _pendingSignals;

    /// <summary>Liefert den Thread des laufenden Aufrufs; im Kernel über den Kernelthread, ohne Brücke der Hauptthread.</summary>
    public Func<LinuxThread>? CurrentThreadSource { get; set; }

    /// <summary>Startet einen Thread aus clone (Kernel: Kernelthread mit den Registern des Elternthreads); false ohne Mittel.</summary>
    public Func<LinuxThread, bool>? StartThread { get; set; }

    /// <summary>Ein Thread endet mit exit, andere laufen weiter; im Kernel verlässt nur er den Ring 3.</summary>
    public Action<LinuxThread, int>? OnThreadExit { get; set; }

    /// <summary>Signal, das den Prozess beendet hat (tgkill ohne Handler), sonst null.</summary>
    public int? TerminatedBySignal { get; private set; }

    public LinuxThread MainThread { get; }

    public LinuxThread CurrentThread => CurrentThreadSource?.Invoke() ?? MainThread;

    public LinuxFutexTable Futexes { get; }

    /// <summary>V29c: CPU-Zeit des aufrufenden Threads in Nanosekunden (Quelle: Scheduler-Ticks, siehe LinuxThreads.cs).</summary>
    public long CurrentThreadCpuNanos => CurrentThread.CpuNanos;

    // V29c: Summe der CPU-Zeit beendeter Threads (die Threadliste verwirft sie beim nächsten clone), unter _threadGate.
    private long _exitedCpuNanos;

    public long ProcessCpuNanos
    {
        get
        {
            lock (_threadGate)
            {
                long sum = _exitedCpuNanos;
                foreach (LinuxThread t in _threads)
                {
                    if (!t.Exited)
                    {
                        sum += t.CpuNanos;
                    }
                }

                return sum;
            }
        }
    }

    /// <summary>Anzahl Threads, die noch nicht geendet haben.</summary>
    public int LiveThreads
    {
        get
        {
            lock (_threadGate)
            {
                return _liveThreads;
            }
        }
    }

    /// <summary>Alle Threads, die noch laufen (Schnappschuss, für exit_group und Abbruch in der Brücke).</summary>
    public LinuxThread[] RunningThreads()
    {
        lock (_threadGate)
        {
            return _threads.FindAll(t => !t.Exited).ToArray();
        }
    }

    public long CloneThread(ulong flags, ulong stack, ulong parentTid, ulong childTid, ulong tls)
    {
        // Linux: CLONE_THREAD braucht CLONE_SIGHAND, CLONE_SIGHAND braucht CLONE_VM (EINVAL).
        if (((flags & CloneThreadFlag) != 0 && (flags & CloneSighand) == 0) || ((flags & CloneSighand) != 0 && (flags & CloneVm) == 0))
        {
            return -LinuxSyscalls.EINVAL;
        }

        // V15: vfork und posix_spawn (CLONE_VM|CLONE_VFORK ohne CLONE_THREAD) werden ein neuer Prozess auf geteiltem Speicher.
        if ((flags & (CloneVm | CloneVfork)) == (CloneVm | CloneVfork) && (flags & (CloneThreadFlag | CloneSighand)) == 0)
        {
            return CloneVforkProcess(flags, stack);
        }

        // V21: fork und clone ohne CLONE_VM werden ein neuer Prozess mit kopiertem Adressraum (LinuxFork.cs).
        if ((flags & (CloneVm | CloneThreadFlag | CloneSighand)) == 0)
        {
            return CloneForkProcess(flags, stack, parentTid, childTid, tls);
        }

        // Teilweise geteilter Thread (CLONE_VM ohne die übrigen Bits) gibt es nicht; ehrlich ablehnen statt halb teilen.
        if ((flags & CloneThreadSet) != CloneThreadSet || (flags & ~(CloneThreadSet | CloneKnownExtras)) != 0)
        {
            return -LinuxSyscalls.ENOSYS;
        }

        LinuxThread child;
        lock (_threadGate)
        {
            if (ExitCode.HasValue || _liveThreads >= MaxThreads || StartThread == null)
            {
                return -LinuxSyscalls.EAGAIN;
            }

            // Beendete Kinder austragen; der Hauptthread bleibt (seine TID ist die PID, Linux hält sie bis zum Prozessende).
            foreach (LinuxThread t in _threads)
            {
                if (t.Exited && !ReferenceEquals(t, MainThread))
                {
                    _ids.Remove(t.Tid);
                }
            }

            _threads.RemoveAll(t => t.Exited && !ReferenceEquals(t, MainThread));
            child = new LinuxThread(_ids.Allocate())
            {
                StackPointer = stack,
                FsBase = (flags & CloneSettls) != 0 ? tls : CurrentThread.FsBase,
                ClearChildTid = (flags & CloneChildCleartid) != 0 ? childTid : 0,
                Blocked = CurrentThread.Blocked, // V38: ein neuer Thread erbt die Maske des Threads, der clone ruft (Linux copy_process)
            };
            _threads.Add(child);
            _liveThreads++;
        }

        // Linux schreibt die TIDs vor dem ersten Lauf des Kindes und übergeht Fehler beim Schreiben.
        Span<byte> tid = stackalloc byte[4];
        BitConverter.TryWriteBytes(tid, child.Tid);
        if ((flags & CloneParentSettid) != 0)
        {
            TryWrite(parentTid, tid);
        }

        if ((flags & CloneChildSettid) != 0)
        {
            TryWrite(childTid, tid);
        }

        if (!StartThread(child))
        {
            lock (_threadGate)
            {
                _threads.Remove(child);
                _liveThreads--;
            }

            _ids.Remove(child.Tid);
            return -LinuxSyscalls.EAGAIN;
        }

        return child.Tid;
    }

    /// <summary>
    /// Nächste freie TID nach <paramref name="last"/>, nach <see cref="TidMax"/> wieder ab 1; übersprungen werden die PID und belegte
    /// TIDs. Seit V15 (mehrere Prozesse) ruft das <see cref="LinuxIds"/>, die PIDs und TIDs aller Prozesse eines Laufs gemeinsam führt.
    /// </summary>
    internal static int NextTid(int last, int pid, Func<int, bool> inUse)
    {
        // Höchstens MaxThreads belegt: nach MaxThreads + 2 Schritten ist eine freie TID gefunden; die Grenze schützt nur vor Endlosschleifen.
        int tid = last;
        for (int i = 0; i <= TidMax; i++)
        {
            tid = tid >= TidMax || tid < 1 ? 1 : tid + 1;
            if (tid != pid && !inUse(tid))
            {
                return tid;
            }
        }

        throw new InvalidOperationException("keine freie TID");
    }

    // Unter _threadGate: belegt ist jede TID eines Threads in der Liste (laufend oder der Hauptthread).
    private bool TidInUse(int tid)
    {
        foreach (LinuxThread t in _threads)
        {
            if (t.Tid == tid)
            {
                return true;
            }
        }

        return false;
    }

    public void ExitThread(int code)
    {
        LinuxThread me = CurrentThread;
        bool last;
        lock (_threadGate)
        {
            if (me.Exited)
            {
                return;
            }

            me.Exited = true;
            _exitedCpuNanos += me.FreezeCpu();
            last = --_liveThreads == 0;
        }

        // CLONE_CHILD_CLEARTID: 0 schreiben und einen Wartenden wecken (pthread_join wartet so auf das Ende).
        ReleaseClearChildTid(me);
        if (last)
        {
            Exit(code);
        }
        else
        {
            OnThreadExit?.Invoke(me, code);
        }
    }

    /// <summary>Ein Thread endet ohne exit (die Brücke räumt einen abgebrochenen Kernelthread auf); zählt wie exit ohne Prozessende.</summary>
    public void ThreadGone(LinuxThread thread)
    {
        lock (_threadGate)
        {
            if (thread.Exited)
            {
                return;
            }

            thread.Exited = true;
            _exitedCpuNanos += thread.FreezeCpu();
            _liveThreads--;
        }
    }

    private void ReleaseClearChildTid(LinuxThread t)
    {
        if (t.ClearChildTid == 0)
        {
            return;
        }

        Span<byte> zero = stackalloc byte[4];
        if (TryWrite(t.ClearChildTid, zero))
        {
            Futexes.Wake(t.ClearChildTid, 1);
        }
    }

    public long SignalThread(int tgid, int tid, int signal)
    {
        // tkill kommt mit tgid -1; tgkill mit tgid <= 0 oder tid <= 0 ist EINVAL, ebenso eine Signalnummer über 64.
        if (tid <= 0 || (tgid != -1 && tgid <= 0) || (uint)signal > 64)
        {
            return -LinuxSyscalls.EINVAL;
        }

        LinuxThread? target = null;
        lock (_threadGate)
        {
            if (tgid == -1 || tgid == Pid)
            {
                target = _threads.Find(t => t.Tid == tid && !t.Exited);
            }
        }

        if (target == null)
        {
            return -LinuxSyscalls.ESRCH;
        }

        return DeliverSignal(signal, fromOutside: false, SiTkill, Pid, target: target);
    }

    /// <summary>
    /// Zustellen an den Prozess (tgkill, tkill, kill). <paramref name="fromOutside"/>: Der Aufrufer ist ein Thread eines anderen Prozesses
    /// (kill aus LinuxWait.cs); dann verlässt nicht der aufrufende Thread den Ring 3, sondern die Threads dieses Prozesses (<see cref="OnKilled"/>).
    /// </summary>
    private long DeliverSignal(int signal, bool fromOutside, int code = SiUser, int senderPid = 0, int status = 0, LinuxThread? target = null)
    {
        if (signal == 0)
        {
            return 0;
        }

        // SIGKILL beendet immer; SIGSTOP ebenfalls, denn Anhalten gibt es nicht und niemand könnte mit SIGCONT fortsetzen.
        if (signal is 9 or 19)
        {
            End(signal, fromOutside);
            return 0;
        }

        // Blockiert oder mit Handler (V18): anhängig, bis ein Thread den Ring 3 nach einem Aufruf wieder betritt (TakeSignal, LinuxSignals.cs),
        // sigprocmask es freigibt oder sigaction es ignoriert (Linux reiht auch ignorierte blockierte ein, denn der Handler kann sich bis zur
        // Freigabe ändern). Prüfen und Eintragen unter derselben Sperre wie SignalStateChanged und TakeSignal.
        // V38: Maske je Thread. An den Prozess: blockiert heißt in jedem lebenden Thread, sonst kann einer es nehmen. An einen Thread (tgkill):
        // dessen Maske; ohne Handler und frei beendet es trotzdem den ganzen Prozess wie bei Linux.
        ulong bit = 1UL << (signal - 1);
        bool wake = false;
        lock (_threadGate)
        {
            bool blocked = ((target?.Blocked ?? BlockedEverywhereLocked()) & bit) != 0;
            if (blocked || Signals.Handler[signal] > 1)
            {
                Queue(signal, code, senderPid, status, target);
                wake = !blocked;
                if (!wake)
                {
                    return 0;
                }
            }
        }

        if (wake)
        {
            Futexes.Kick(); // Wartende (futex, pause, nanosleep) kehren mit -EINTR zurück, die Brücke springt den Handler an
            return 0;
        }

        if (!IsIgnored(signal))
        {
            End(signal, fromOutside);
        }

        return 0;
    }

    private void End(int signal, bool fromOutside)
    {
        if (fromOutside)
        {
            Kill(signal);
        }
        else
        {
            Terminate(signal);
        }
    }

    /// <summary>Anhängige Signale (Bit = Nummer - 1), etwa für Tests und rt_sigpending.</summary>
    public ulong PendingSignals
    {
        get
        {
            lock (_threadGate)
            {
                // V38: mit den an Threads gerichteten (rt_sigpending und /proc nennen sie zusammen; Linux je Thread, hier für alle).
                ulong all = _pendingSignals;
                foreach (LinuxThread t in _threads)
                {
                    all |= t.Exited ? 0 : t.Pending;
                }

                return all;
            }
        }
    }

    /// <summary>
    /// Nach sigprocmask oder sigaction: anhängige Signale, die jetzt ignoriert werden, fallen weg (Linux beim Setzen von SIG_IGN);
    /// das niedrigste freigegebene mit Standardaktion "beenden" beendet den Prozess. Eines mit Handler bleibt anhängig; die Brücke stellt
    /// es beim Rücksprung dieses Aufrufs zu (V18).
    /// </summary>
    public void SignalStateChanged()
    {
        int deliver = 0;
        bool kick;
        lock (_threadGate)
        {
            // V38: Frei ist ein Signal an den Prozess, sobald es ein Thread nicht blockiert, eins an einen Thread, wenn dieser es nicht
            // blockiert; wartende Threads sollen es sehen.
            ulong all = _pendingSignals;
            ulong free = _pendingSignals & ~BlockedEverywhereLocked();
            foreach (LinuxThread t in _threads)
            {
                if (!t.Exited)
                {
                    all |= t.Pending;
                    free |= t.Pending & ~t.Blocked;
                }
            }

            kick = free != 0;
            for (int sig = 1; sig <= 64 && all != 0; sig++)
            {
                ulong bit = 1UL << (sig - 1);
                if ((all & bit) == 0)
                {
                    continue;
                }

                if (IsIgnored(sig) || (deliver == 0 && (free & bit) != 0 && Signals.Handler[sig] == 0))
                {
                    deliver = IsIgnored(sig) ? deliver : sig;
                    _pendingSignals &= ~bit;
                    foreach (LinuxThread t in _threads)
                    {
                        t.Pending &= ~bit;
                    }
                }
            }
        }

        if (kick)
        {
            Futexes.Kick(); // andere Threads, die warten, sollen es ebenfalls sehen
        }

        // Außerhalb der Sperre: Exit nimmt _threadGate selbst und weckt die futex-Tabelle.
        if (deliver != 0)
        {
            Terminate(deliver);
        }
    }

    /// <summary>
    /// SIG_IGN, Standard "ignorieren" (SIGCHLD 17, SIGCONT 18, SIGURG 23, SIGWINCH 28) und die Stoppsignale SIGTSTP 20, SIGTTIN 21,
    /// SIGTTOU 22 mit Standardaktion: Der Prozess ist allein in einer verwaisten Prozessgruppe (getpgrp = getsid = PID, kein Elternprozess),
    /// und dort verwirft Linux diese drei, statt anzuhalten (POSIX; kernel/signal.c, get_signal, is_current_pgrp_orphaned).
    /// </summary>
    private bool IsIgnored(int signal) =>
        Signals.Handler[signal] == 1 || (Signals.Handler[signal] == 0 && signal is 17 or 18 or 20 or 21 or 22 or 23 or 28);

    // Standardaktion "beenden": den ganzen Prozess, wie bei Linux (Status "signal <n>").
    private void Terminate(int signal)
    {
        // P3-9 (Risiko-Review 2): Signal und Status unter derselben Sperre wie ExitCode in Exit. Kam exit_group eines anderen Threads
        // zuvor, gilt dessen Code allein; sonst meldete Describe "signal n" zu ExitCode 0. Auf mehreren CPUs ist das Fenster breit.
        lock (_threadGate)
        {
            if (ExitCode == null)
            {
                TerminatedBySignal ??= signal;
            }
        }

        Exit(128 + signal);
    }

    /// <summary>
    /// sigaction (<see cref="ILinuxThreads.ExchangeSignalAction"/>): unter <c>_threadGate</c>, wie TakeSignal, TakeFaultSignal und
    /// SA_RESETHAND, damit kein Thread eine halb geschriebene Aktion zustellt (Risiko-Review 2, P3-1).
    /// </summary>
    public void ExchangeSignalAction(int signal, bool set, Span<ulong> action)
    {
        lock (_threadGate)
        {
            LinuxSyscalls.ExchangeAction(Signals, signal, set, action);
        }
    }

    public LinuxSignalState Signals { get; } = new();

    // ILinuxHost: der Verteiler fragt den Prozess (p as ILinuxHost); weitergereicht an den Host des Kernels.
    public long RealtimeNanos => _host?.RealtimeNanos ?? 0;

    public long MonotonicNanos => _host?.MonotonicNanos ?? 0;

    public bool TryFillRandom(Span<byte> dst) => _host != null && _host.TryFillRandom(dst);

    public void SleepNanos(long ns) => _host?.SleepNanos(ns);

    // ILinuxSystemInfo (V13 sysinfo): nur, wenn der Host des Kernels Speicher melden kann.
    public bool TryGetMemory(out ulong totalBytes, out ulong freeBytes)
    {
        if (_host is ILinuxSystemInfo info)
        {
            return info.TryGetMemory(out totalBytes, out freeBytes);
        }

        totalBytes = 0;
        freeBytes = 0;
        return false;
    }

    // ILinuxCpus (N4-S2): die CPU-Menge kennt nur der Host des Kernels; ohne ihn eine CPU wie vor S2.
    public int CpuCount => _host is ILinuxCpus cpus ? Math.Clamp(cpus.CpuCount, 1, 64) : 1;

    public int CurrentCpu => _host is ILinuxCpus cpus ? cpus.CurrentCpu : 0;

    /// <summary>Abbruch des Programms (über <see cref="LinuxFutexTable.Abort"/>, das Strg+C, Zeitlimit und exit_group auslösen).</summary>
    public bool IsAborted => Futexes.IsAborted;

    /// <summary>Programmpfad im Prozess (V12, /proc/self/exe); setzt der Starter vor dem ersten Befehl, danach nur gelesen.</summary>
    public string? ExePath { get; set; }

    /// <summary>V26: absoluter, normalisierter Pfad von argv[0] des Starters (relativ zum Start-Arbeitsverzeichnis); execve darauf findet <see cref="ExePath"/>.</summary>
    public string? StartPath { get; set; }

    public ILinuxDescriptor? GetDescriptor(int fd) => (uint)fd < MaxDescriptors ? _descriptors[fd] : null;

    // ILinuxFiles: Tabelle und Arbeitsverzeichnis; Pfade, dup und close setzt der Verteiler darauf um (V2 Dateien).
    public ILinuxFileSystem FileSystem { get; }

    public object FileGate => _fileGate;

    public string Cwd
    {
        get => _cwd;
        set => _cwd = value ?? throw new ArgumentNullException(nameof(value));
    }

    public int AddDescriptor(ILinuxDescriptor d, int min)
    {
        ArgumentNullException.ThrowIfNull(d);
        for (int fd = Math.Max(min, 0); fd < MaxDescriptors; fd++)
        {
            if (_descriptors[fd] == null)
            {
                _descriptors[fd] = d;
                _closeOnExec[fd] = false; // neue Nummern (open, dup, dup2) beginnen ohne FD_CLOEXEC; Flags setzt der Verteiler danach
                _openFiles.Add(d);
                return fd;
            }
        }

        return (int)-LinuxErrno.EMFILE;
    }

    public bool RemoveDescriptor(int fd)
    {
        if ((uint)fd >= MaxDescriptors || _descriptors[fd] is not ILinuxDescriptor d)
        {
            return false;
        }

        _descriptors[fd] = null;
        _closeOnExec[fd] = false;
        _openFiles.Remove(d);
        return true;
    }

    public bool GetCloseOnExec(int fd) => (uint)fd < MaxDescriptors && _descriptors[fd] != null && _closeOnExec[fd];

    public void SetCloseOnExec(int fd, bool on)
    {
        if ((uint)fd < MaxDescriptors && _descriptors[fd] != null)
        {
            _closeOnExec[fd] = on;
        }
    }

    /// <summary>V15: true, solange irgendeine Nummer irgendeines Prozesses dieses Laufs auf <paramref name="d"/> zeigt. Der Aufrufer hält FileGate.</summary>
    public bool IsOpenAnywhere(ILinuxDescriptor d) => _openFiles.IsReferenced(d);

    /// <summary>V28b: Sperrtabelle des Laufs (geteilt wie die Zähltabelle).</summary>
    public LinuxFileLocks FileLocks => _openFiles.Locks;

    /// <summary>V28b: POSIX-Sperren gehören der PID; execve (TakeOver) behält sie, wie Linux.</summary>
    public int LockOwner => Pid;

    /// <summary>V16 (SCM_RIGHTS): eine Unix-Socket-Nachricht hält <paramref name="d"/> wie eine Nummer. Der Aufrufer hält FileGate.</summary>
    public void HoldInFlight(ILinuxDescriptor d) => _openFiles.Add(d);

    /// <summary>V16: die Nachricht gibt <paramref name="d"/> ab; war es der letzte Verweis im Lauf, schließt die Datei. Der Aufrufer hält FileGate.</summary>
    public void ReleaseInFlight(ILinuxDescriptor d)
    {
        _openFiles.Remove(d);
        _ = LinuxSyscalls.ReleaseIfUnused(this, d);
    }

    /// <summary>Leert Nummer <paramref name="fd"/> und schließt die Datei, wenn es die letzte Nummer im Lauf war. Der Aufrufer hält FileGate.</summary>
    private void DropDescriptor(int fd)
    {
        ILinuxDescriptor? d = _descriptors[fd];
        _closeOnExec[fd] = false;
        if (d is null)
        {
            return;
        }

        _descriptors[fd] = null;
        _openFiles.Remove(d);
        LinuxSyscalls.ReleasePosixLocksOnClose(this, d); // V28b: vor dem Schließen, das stat braucht die offene Datei

        // Close fängt Dateifehler ab und gibt das Budget in jedem Fall zurück; ein -EIO beim Zurückschreiben hat am Prozessende oder
        // bei execve keinen Empfänger mehr (wie Linux in exit und do_close_on_exec) und darf die übrigen Dateien nicht offen lassen.
        // Pipe-Enden zählen mit: sonst sähe der Elternteil nach dem Ende eines Kindes nie EOF.
        _ = LinuxSyscalls.ReleaseIfUnused(this, d);
    }

    /// <summary>V15 (execve): schließt jede Nummer mit FD_CLOEXEC; die Datei selbst erst, wenn keine Nummer im Lauf mehr auf sie zeigt.</summary>
    private void CloseOnExec()
    {
        lock (_fileGate)
        {
            for (int fd = 0; fd < MaxDescriptors; fd++)
            {
                if (_closeOnExec[fd])
                {
                    DropDescriptor(fd);
                }
            }
        }
    }

    /// <summary>
    /// Schließt jede noch offene Datei genau einmal (dup teilt eine Datei über mehrere Nummern, vfork über mehrere Prozesse) und leert
    /// die Tabelle. Ruft <see cref="Destroy"/> und <see cref="Release"/>; ohne das blieben Dateien auf der Datenplatte bis zum nächsten Boot offen.
    /// </summary>
    public void CloseAllFiles()
    {
        // V15: unter der geteilten Sperre, weil andere Prozesse dieselben Dateien halten und gleichzeitig schließen können.
        lock (_fileGate)
        {
            for (int fd = 0; fd < MaxDescriptors; fd++)
            {
                DropDescriptor(fd);
            }

            // V28b: Rest für Sperren, deren Datei sich nicht mehr erkennen ließ (stat gescheitert). Nach execve hat der neue Prozess die PID
            // übernommen (_pidPassed); seine Sperren bleiben.
            if (!_pidPassed)
            {
                _openFiles.Locks.ReleaseProcess(Pid);
            }
        }
    }

    /// <summary>exit_group (und das Ende des letzten Threads): beendet den Prozess; Wartende in futex kehren zurück.</summary>
    /// <remarks>
    /// <c>clear_child_tid</c> der übrigen Threads wird hier absichtlich nicht geschrieben (V10-Review P3). Linux schreibt es in
    /// <c>mm_release</c> nur, solange noch jemand anderes den Adressraum nutzt (<c>mm_users &gt; 1</c>); sehen kann das nur ein zweiter
    /// Prozess über gemeinsamen Speicher oder ptrace. ZehOS hat genau einen Linux-Prozess, keinen gemeinsamen Speicher zwischen
    /// Prozessen (Datei-mmap ist eine Kopie) und kein ptrace; alle futex-Wartenden des Prozesses kehren über <see cref="LinuxFutexTable.Abort"/>
    /// zurück und verlassen den Ring 3. Ein Schreiben hätte keinen Leser und würde in den Speicher eines sterbenden Prozesses fallen,
    /// während andere Threads ihn noch auf ihrer CPU benutzen.
    /// </remarks>
    public void Exit(int code)
    {
        lock (_threadGate)
        {
            ExitCode ??= code;
        }

        Futexes.Abort();
        OnExit?.Invoke(code);
    }

    /// <summary>
    /// Belegt die Bereiche des Abbilds und füllt die Seiten mit Dateiinhalt und den vorbereiteten Stack; alle übrigen Seiten
    /// (.bss jenseits der Dateibytes, der Rest des Stacks) kommen genullt beim ersten Zugriff.
    /// </summary>
    public ResultErr Load(UserImage image, ReadOnlySpan<byte> elf) => LoadCore(image, elf, lazyProgram: false);

    // V30: Mit lazyProgram füllt Load nur Stack und Interpreter; die Programmsegmente kommen aus _fileExtents (Populate).
    private ResultErr LoadCore(UserImage image, ReadOnlySpan<byte> elf, bool lazyProgram)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_vforkOf != null)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Ein vfork-Kind lädt nur über execve in einen neuen Adressraum.");
        }

        _uid = image.Uid;
        _gid = image.Gid;
        ResultErr reserved = image.Reserve(_map);
        if (!reserved.IsOK())
        {
            return reserved;
        }

        _stackSize = image.StackTop - image.StackBottom;
        _stackTop = image.StackTop;

        ulong stackContent = image.StackPointer & ~(Page - 1);
        foreach (UserRegion region in image.Regions)
        {
            if (region.Access == MapAccess.None)
            {
                continue;
            }

            for (ulong page = region.Start; page < region.End; page += Page)
            {
                bool content = page >= stackContent && page < image.StackTop;
                foreach (UserSegment segment in image.Segments)
                {
                    content |= segment.FileSize > 0 && (!lazyProgram || segment.Interpreter)
                        && page < segment.Address + segment.FileSize && page + Page > segment.Address;
                }

                if (!content)
                {
                    continue;
                }

                ulong frame = NewFrame();
                if (frame == 0)
                {
                    return ResultErr.CreateFailure(ResultCode.Failure, "Kein Speicher für das Programmabbild.");
                }

                image.FillPage(elf, page, _pages.Frame(frame), skipProgram: lazyProgram);
                if (!_pages.Map(page, frame, region.Access))
                {
                    _pages.FreeFrame(frame);
                    return ResultErr.CreateFailure(ResultCode.Failure, "Seite 0x" + page.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + " nicht abbildbar.");
                }

                _frames[page] = frame;
                SyncBudget();
            }
        }

        _brkStart = image.BreakStart;
        _brk = image.BreakStart;
        return ResultErr.CreateSuccess();
    }

    /// <summary>
    /// Ein Seitenfehler im Ring 3. true: die Seite ist jetzt abgebildet und der Befehl läuft noch einmal. false: Schutzverletzung,
    /// unbelegte Adresse oder kein Speicher mehr; der Prozess wird abgebrochen.
    /// </summary>
    public bool HandlePageFault(ulong address, ulong error) => HandlePageFault(address, error, out _);

    /// <summary>
    /// Wie <see cref="HandlePageFault(ulong, ulong)"/>; <paramref name="busError"/> (V30-P3) meldet bei false, dass eine Dateiseite sich nicht
    /// lesen ließ (Lesefehler oder Seite ganz hinter dem Dateiende): Linux schickt dann SIGBUS statt SIGSEGV.
    /// </summary>
    public bool HandlePageFault(ulong address, ulong error, out bool busError) => HandlePageFault(address, error, out busError, out _);

    /// <summary>
    /// Wie <see cref="HandlePageFault(ulong, ulong, out bool)"/>; <paramref name="outOfMemory"/> (V43) meldet bei false, dass für eine
    /// erlaubte Seite kein Frame mehr kam (Prozessgrenze oder Kernel-Reserve). Linux beendet den Prozess dann mit SIGKILL (OOM-Killer).
    /// </summary>
    public bool HandlePageFault(ulong address, ulong error, out bool busError, out bool outOfMemory)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.HandlePageFault(address, error, out busError, out outOfMemory);
        }

        // V47: Fehlt ein Frame, wird einmal gesammelt und der Fehler neu behandelt, aber nach dem Loslassen von _memGate (und damit auch von
        // _gate und StorageLock, die darunter liegen): Ein Voll-GC unter Sperre hielte jeden anderen Thread des Prozesses fest.
        // Heißpfad ohne Closure: der erste Versuch läuft direkt; nur bei echtem Frame-Mangel (nicht bei Prozessgrenze) kommt der Retry.
        bool handled = AttemptFault(address, error, out bool bus, out bool oom, out bool shortFrames);
        if (!handled && oom && shortFrames)
        {
            bool b2 = false;
            bool o2 = false;
            bool h2 = false;
            FrameReclaim.Retry(() =>
            {
                h2 = AttemptFault(address, error, out b2, out o2, out bool s2);
                return !(o2 && s2);
            }, static () => GC.Collect(), static () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency);
            handled = h2;
            bus = b2;
            oom = o2;
        }

        busError = bus;
        outOfMemory = oom;
        return handled;
    }

    /// <summary>Ein Versuch der Seitenfehlerbehandlung unter <c>_memGate</c>; <paramref name="shortFrames"/>: der Kernel hatte keinen Frame (GC kann helfen), nicht nur die Prozessgrenze.</summary>
    private bool AttemptFault(ulong address, ulong error, out bool bus, out bool oom, out bool shortFrames)
    {
        lock (_memGate)
        {
            _noFrame = false;
            _frameShort = false;
            bool handled = HandlePageFaultLocked(address, error, out bus);
            oom = !handled && _noFrame;
            shortFrames = oom && _frameShort;
            return handled;
        }
    }

    /// <summary>
    /// V24: Liegt <paramref name="address"/> in einem Bereich der Karte, auch PROT_NONE? Für den si_code eines Seitenfehlers: SEGV_ACCERR,
    /// wenn ja (Zugriff verboten), sonst SEGV_MAPERR.
    /// </summary>
    public bool HasMapping(ulong address)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.HasMapping(address);
        }

        lock (_memGate)
        {
            return address < ElfLoader.UserEnd && _map.TryFind(address & ~(Page - 1), out _);
        }
    }

    private bool HandlePageFaultLocked(ulong address, ulong error, out bool busError)
    {
        busError = false;
        if (address >= ElfLoader.UserEnd)
        {
            return false;
        }

        ulong page = address & ~(Page - 1);
        if ((error & FaultPresent) != 0)
        {
            return IsStaleWriteFault(page, error);
        }

        if (!_map.TryFind(page, out MapAccess access) || access == MapAccess.None)
        {
            return false;
        }

        if (((error & FaultWrite) != 0 && access != MapAccess.ReadWrite) || ((error & FaultFetch) != 0 && access != MapAccess.ReadExecute))
        {
            return false;
        }

        // Frame da, Seite nicht abgebildet: Protect konnte sie nach dem Aufheben nicht neu abbilden (V3-Review P3 b).
        // Jetzt mit dem Zugriff aus der Karte nachholen; Inhalt und Frame bleiben.
        // V38: Oder zwei Threads liefen zugleich in dieselbe neue Seite (WebContent: SIGSEGV beim Befehlsabruf, Fehlercode 0x14, RIP =
        // Adresse im Programmtext): Der erste hat sie geholt und abgebildet, während dieser auf _memGate wartete. Die Tabelle lehnt das
        // zweite Abbilden ab (Cosmos TryMap: schon vorhanden); ist sie mit demselben Frame da, ist der Fehler erledigt.
        if (_frames.TryGetValue(page, out ulong held))
        {
            return _pages.Map(page, held, access) || (_pages is IUserPagesQuery query && query.MapsTo(page, held, write: false));
        }

        return Populate(page, access, out busError) != 0;
    }

    /// <summary>
    /// V38b: Schreibfehler auf einer vorhandenen Seite, die beim Fehler nur lesbar abgebildet war. Lief zwischen Ausnahme und
    /// <c>_memGate</c> ein mprotect(PROT_WRITE) eines anderen Threads (oder sah diese CPU noch den alten TLB-Eintrag), ist die Seite jetzt
    /// schreibbar: wie bei Linux den Befehl wiederholen statt SIGSEGV. true nur, wenn Karte <em>und</em> Tabelle das Schreiben schon
    /// erlauben und derselbe Frame abgebildet ist; sonst liefe der wiederholte Befehl endlos in denselben Fehler. Ein Seitenfehler
    /// invalidiert auf x86 den TLB-Eintrag der Adresse, die Wiederholung liest also die neue Tabelle. Ausführfehler (NX) und Lesefehler auf
    /// vorhandenen Seiten bleiben Schutzverletzungen: Die Tabelle meldet kein NX-Bit, und PROT_NONE hebt die Abbildung ganz auf.
    /// </summary>
    private bool IsStaleWriteFault(ulong page, ulong error) =>
        (error & FaultWrite) != 0 && (error & FaultFetch) == 0
        && _map.TryFind(page, out MapAccess access) && access == MapAccess.ReadWrite
        && _frames.TryGetValue(page, out ulong held)
        && _pages is IUserPagesQuery query && query.MapsTo(page, held, write: true);

    public bool TrySetBrk(ulong newBrk)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.TrySetBrk(newBrk);
        }

        lock (_memGate)
        {
            return TrySetBrkLocked(newBrk);
        }
    }

    private bool TrySetBrkLocked(ulong newBrk)
    {
        if (newBrk < _brkStart || newBrk > ElfLoader.UserEnd)
        {
            return false;
        }

        ulong oldEnd = PageUp(_brk);
        ulong newEnd = PageUp(newBrk);
        if (newEnd > oldEnd)
        {
            if (!_map.Reserve(oldEnd, newEnd - oldEnd, MapAccess.ReadWrite, Placement.FixedNoReplace).IsOK())
            {
                return false;
            }
        }
        else if (newEnd < oldEnd)
        {
            Release(newEnd, oldEnd);
        }

        _brk = newBrk;
        return true;
    }

    public ulong MapAnonymous(ulong len, int prot)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.MapAnonymous(len, prot);
        }

        lock (_memGate)
        {
            return MapAnonymousLocked(len, prot);
        }
    }

    private ulong MapAnonymousLocked(ulong len, int prot)
    {
        if (len == 0 || (len & (Page - 1)) != 0 || !TryAccess(prot, out MapAccess access))
        {
            return 0;
        }

        ResultObject<ulong> placed = _map.Reserve(MmapBase, len, access, Placement.Hint);
        return placed.IsOK() ? placed.Value : 0;
    }

    // MapFixed und Discard (V2c) laufen wie Unmap unter _memGate: Threads (V10) teilen Karte und Frames.
    public bool MapFixed(ulong addr, ulong len, int prot)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.MapFixed(addr, len, prot);
        }

        lock (_memGate)
        {
            return MapFixedLocked(addr, len, prot);
        }
    }

    private bool MapFixedLocked(ulong addr, ulong len, int prot)
    {
        if (len == 0 || (len & (Page - 1)) != 0 || (addr & (Page - 1)) != 0 || addr < ElfLoader.MinAddress
            || !InUserRange(addr, len) || !TryAccess(prot, out MapAccess access))
        {
            return false;
        }

        // Erst die Frames des Altbestands freigeben (sonst blieben sie mit altem Inhalt an den Seiten), dann neu belegen.
        Release(addr, addr + len);
        return _map.Reserve(addr, len, access, Placement.FixedNoReplace).IsOK();
    }

    public bool Discard(ulong addr, ulong len)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.Discard(addr, len);
        }

        lock (_memGate)
        {
            return DiscardLocked(addr, len);
        }
    }

    private bool DiscardLocked(ulong addr, ulong len)
    {
        if (!InUserRange(addr, len))
        {
            return false;
        }

        // Frame weg, Bereich bleibt: der nächste Zugriff füllt die Seite über den Seitenfehler mit Nullen.
        return UnmapAndDrop(PagesWithFrames(addr, addr + len), addr, addr + len, stuck: null);
    }

    /// <summary>
    /// Hebt die Seiten (mit Frame, in [start, end)) auf und gibt danach ihre Frames zurück (<see cref="DropFrame"/>); mit
    /// <see cref="IUserPagesRange"/> ein Abschuss je Folge zusammenhängender Seiten. Eine Seite, die abgebildet bleibt, behält ihren
    /// Frame und landet in <paramref name="stuck"/>; false, wenn es eine gab. Unter _memGate.
    /// </summary>
    private bool UnmapAndDrop(List<ulong> pages, ulong start, ulong end, List<ulong>? stuck)
    {
        bool ok = true;
        if (_pages is IUserPagesRange range)
        {
            Span<bool> gone = stackalloc bool[RangeChunk];
            foreach ((ulong first, int count) in FrameRuns(pages, start, end))
            {
                range.UnmapRange(first, count, gone);

                // Erst nach dem Abschuss des ganzen Stücks frei: Vorher könnte eine andere CPU den Frame noch über ihren TLB erreichen.
                for (int i = 0; i < count; i++)
                {
                    ok &= DropOrKeep(first + (ulong)i * Page, gone[i], stuck);
                }
            }

            return ok;
        }

        foreach (ulong page in pages)
        {
            ok &= DropOrKeep(page, _pages.Unmap(page), stuck);
        }

        return ok;
    }

    private bool DropOrKeep(ulong page, bool gone, List<ulong>? stuck)
    {
        if (gone)
        {
            DropFrame(page); // V17: geteilte Seiten geben nur ihren Verweis zurück, der Inhalt bleibt in der memfd
            return true;
        }

        _refusedUnmaps++;
        stuck?.Add(page);
        return false;
    }

    /// <summary>
    /// N4-S2: Die Seiten mit Frame als Folgen zusammenhängender Seiten in [start, end), je höchstens <see cref="RangeChunk"/> lang. Ohne
    /// Sortieren: Eine Folge beginnt dort, wo die Seite davor keinen Frame hat oder vor <paramref name="start"/> liegt.
    /// </summary>
    private List<(ulong First, int Count)> FrameRuns(List<ulong> pages, ulong start, ulong end)
    {
        var runs = new List<(ulong First, int Count)>();
        foreach (ulong page in pages)
        {
            if (page > start && _frames.ContainsKey(page - Page))
            {
                continue;
            }

            ulong first = page;
            int count = 1;
            for (ulong next = page + Page; next < end && _frames.ContainsKey(next); next += Page)
            {
                if (count == RangeChunk)
                {
                    runs.Add((first, count));
                    first = next;
                    count = 0;
                }

                count++;
            }

            runs.Add((first, count));
        }

        return runs;
    }

    public bool Unmap(ulong addr, ulong len)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.Unmap(addr, len);
        }

        lock (_memGate)
        {
            return UnmapLocked(addr, len);
        }
    }

    private bool UnmapLocked(ulong addr, ulong len)
    {
        // Erst prüfen, dann Frames freigeben: scheiterte _map.Release erst danach, blieben Bereiche ohne Frames stehen und
        // füllten sich beim nächsten Zugriff genullt (V3-Review P3 d).
        if (len == 0 || ((addr | len) & (Page - 1)) != 0 || !InUserRange(addr, len))
        {
            return false;
        }

        // Unter MinAddress liegt nie ein Bereich; dort ist munmap wie bei Linux erfolgreich und ohne Wirkung.
        ulong start = Math.Max(addr, ElfLoader.MinAddress);
        return start >= addr + len || Release(start, addr + len);
    }

    public bool Protect(ulong addr, ulong len, int prot)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.Protect(addr, len, prot);
        }

        lock (_memGate)
        {
            return ProtectLocked(addr, len, prot);
        }
    }

    private bool ProtectLocked(ulong addr, ulong len, int prot)
    {
        // V17: ein nach F_SEAL_WRITE geteilt abgebildeter Bereich wird nie schreibbar (Fehlernummer liefert ProtectShared).
        if (!InUserRange(addr, len) || !TryAccess(prot, out MapAccess access) || ((prot & ProtWrite) != 0 && DeniesWrite(addr, addr + len)))
        {
            return false;
        }

        // Der alte Zugriff je Seite mit Frame, damit eine Seite, die die Tabelle nicht freigibt, in der Karte so bleibt, wie die
        // Tabelle sie abbildet (V3-Review P3 b). Sonst erlaubte die Karte dem Kernel, was die Tabelle dem Programm verwehrt, oder umgekehrt.
        List<ulong> pages = PagesWithFrames(addr, addr + len);
        var before = new Dictionary<ulong, MapAccess>(pages.Count);
        foreach (ulong page in pages)
        {
            _map.TryFind(page, out MapAccess old);
            before[page] = old;
        }

        if (!_map.Protect(addr, len, access).IsOK())
        {
            return false;
        }

        // N4-S2: mit IUserPagesRange ein Abschuss je Folge zusammenhängender Seiten statt Aufheben und neu Abbilden je Seite; die
        // Seite bleibt dabei abgebildet, ein anderer Thread bekommt keinen unechten Seitenfehler. PROT_NONE hebt weiter auf (die
        // Tabelle kennt keinen Eintrag ohne Zugriff), der Frame bleibt beim Prozess. Lehnt die Tabelle ein Stück ab (eine Seite nicht
        // abgebildet, etwa nach gescheitertem Abbilden unten), geht dieses Stück Seite für Seite den alten Weg.
        bool ok = true;
        if (_pages is IUserPagesRange range)
        {
            Span<bool> gone = stackalloc bool[RangeChunk];
            foreach ((ulong first, int count) in FrameRuns(pages, addr, addr + len))
            {
                if (access == MapAccess.None)
                {
                    range.UnmapRange(first, count, gone);
                    for (int i = 0; i < count; i++)
                    {
                        ulong page = first + (ulong)i * Page;
                        if (!gone[i])
                        {
                            _map.Protect(page, Page, before[page], enforceLimit: false); // wie ProtectPage: Karte folgt der Tabelle
                            ok = false;
                        }
                    }

                    continue;
                }

                bool changed = range.ProtectRange(first, count, access);
                for (int i = 0; i < count; i++)
                {
                    ulong page = first + (ulong)i * Page;
                    ok &= changed ? Protected(page, access) : ProtectPage(page, before[page], access);
                }
            }

            return ok;
        }

        foreach (ulong page in pages)
        {
            ok &= ProtectPage(page, before[page], access);
        }

        return ok;
    }

    /// <summary>Eine Seite mit Frame auf den neuen Zugriff, durch Aufheben und neu Abbilden; unter _memGate, Karte schon geändert.</summary>
    private bool ProtectPage(ulong page, MapAccess before, MapAccess access)
    {
        // Neu abbilden statt umschreiben: IUserPages kennt nur Map und Unmap, der Frame und sein Inhalt bleiben.
        if (!_pages.Unmap(page))
        {
            _map.Protect(page, Page, before, enforceLimit: false); // Zurücksetzen darf am Limit nicht scheitern (V48-Review)
            return false;
        }

        // Scheitert das neue Abbilden, bleibt der Frame beim Prozess und die Seite unabgebildet: Karte und Tabelle widersprechen
        // sich nicht, der nächste Zugriff des Programms bildet über HandlePageFault nach, der Kernel liest über den Frame.
        if (access != MapAccess.None && !_pages.Map(page, _frames[page], access))
        {
            return false;
        }

        return Protected(page, access);
    }

    private bool Protected(ulong page, MapAccess access)
    {
        if (access == MapAccess.ReadWrite && _shared.TryGetValue(page, out LinuxSharedFrame? sf))
        {
            sf.Owner.MarkDirty(sf); // V32-P2: ab jetzt ohne Seitenfehler beschreibbar
        }

        return true;
    }

    public bool TryRead(ulong addr, Span<byte> dst) => Copy(addr, dst, default, write: false);

    public bool TryWrite(ulong addr, ReadOnlySpan<byte> src) => Copy(addr, default, src, write: true);

    public bool CanWrite(ulong addr, ulong length)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.CanWrite(addr, length);
        }

        lock (_memGate)
        {
            return length == 0 || (InUserRange(addr, length) && PagesAllowLocked(addr, length, write: true));
        }
    }

    public bool TryReadCString(ulong addr, int max, out string s)
    {
        s = string.Empty;
        if (max < 0)
        {
            return false;
        }

        var bytes = new List<byte>(Math.Min(max, 256));
        Span<byte> one = stackalloc byte[1];
        for (ulong i = 0; i <= (ulong)max; i++)
        {
            if (addr > ulong.MaxValue - i || !TryRead(addr + i, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                s = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                return true;
            }

            if (i == (ulong)max)
            {
                return false;
            }

            bytes.Add(one[0]);
        }

        return false;
    }

    /// <summary>Hebt alle Abbildungen auf, gibt die Tabelle und danach alle Frames frei. Danach ist der Prozess unbrauchbar.</summary>
    public void Destroy()
    {
        try
        {
            // Dateien zuerst: Nach Destroy ruft niemand mehr den Prozess. Scheitert ein Schließen, gibt finally den Speicher trotzdem frei.
            CloseAllFiles();
        }
        finally
        {
            DestroyMemory();
            FreeIds();
        }
    }

    private bool _memoryDestroyed;

    // N4-S2: Seiten, deren Aufheben die Tabelle abgelehnt hat (Frame bleibt beim Prozess, Seite gesperrt). Sollte mit dem
    // Cosmos-Abschuss nicht mehr vorkommen; der Lauf meldet die Zahl am Ende. Unter _memGate.
    private int _refusedUnmaps;

    /// <summary>N4-S2: Zahl der Seiten, deren Aufheben die Tabelle abgelehnt hat.</summary>
    public int RefusedUnmaps
    {
        get
        {
            lock (_memGate)
            {
                return _refusedUnmaps;
            }
        }
    }

    /// <summary>N4-S2: Höchstzahl Seiten je Bereichsaufruf; begrenzt den Stack für die Ergebnisse (Cosmos braucht 8 Bytes je Seite).</summary>
    private const int RangeChunk = 64;

    /// <summary>
    /// V50: Dateien schließen und Speicher freigeben, <b>bevor</b> das Ende dem Elternprozess gemeldet wird. Linux gibt den Adressraum in
    /// <c>do_exit</c> (exit_mm) vor dem Zombie frei; meldete ZehOS erst danach, sähe ein Elternprozess nach wait4 noch den ganzen Speicher
    /// des Kindes belegt (gemessen: nach einem Kind mit 380 MiB frei 64 statt 426 MiB) und liefe beim nächsten Seitenfehler in den OOM-Kill.
    /// Doppelter Aufruf und das spätere <see cref="Destroy"/> sind wirkungslos.
    /// </summary>
    public void ReleaseMemoryBeforeReport()
    {
        try
        {
            CloseAllFiles();
        }
        finally
        {
            DestroyMemory();
        }
    }

    /// <summary>Gibt Tabelle und Frames frei; ein vfork-Kind besitzt keinen eigenen Speicher (er gehört dem Elternprozess).</summary>
    private void DestroyMemory()
    {
        if (_vforkOf != null)
        {
            return;
        }

        // V50 P3: unter _memGate wie jeder andere Zugriff auf Tabelle und Frames; ein /proc-Leser auf einer anderen CPU
        // (Copy, Seitenfehler) sieht sonst halb freigegebene Tabellen. Die Sperre ist wiederholbar, DropFrame darf sie erneut nehmen.
        lock (_memGate)
        {
            if (_memoryDestroyed)
            {
                return;
            }

            _memoryDestroyed = true;
            // Erst die Tabelle: Solange sie besteht, könnte eine CPU über sie noch einen Frame erreichen.
            _pages.DestroyTable();
            foreach (ulong page in new List<ulong>(_frames.Keys))
            {
                DropFrame(page); // V17: geteilte Frames leben weiter, solange Datei oder ein anderer Prozess sie hält
            }

            DropAllRegions();
            _fileExtents.Clear();
        }
    }

    private bool Copy(ulong addr, Span<byte> dst, ReadOnlySpan<byte> src, bool write)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.Copy(addr, dst, src, write);
        }

        lock (_memGate)
        {
            return CopyLocked(addr, dst, src, write);
        }
    }

    private bool CopyLocked(ulong addr, Span<byte> dst, ReadOnlySpan<byte> src, bool write)
    {
        ulong length = (ulong)(write ? src.Length : dst.Length);
        if (length == 0)
        {
            return true;
        }

        if (!InUserRange(addr, length))
        {
            return false;
        }

        // Erst alle Seiten prüfen, dann kopieren: ein Fehler mitten im Bereich lässt nichts halb geschrieben.
        if (!PagesAllowLocked(addr, length, write))
        {
            return false;
        }

        ulong done = 0;
        while (done < length)
        {
            ulong at = addr + done;
            ulong page = at & ~(Page - 1);
            int offset = (int)(at - page);
            int chunk = (int)Math.Min(Page - (ulong)offset, length - done);
            if (!_frames.TryGetValue(page, out ulong frame))
            {
                _map.TryFind(page, out MapAccess access);
                frame = Populate(page, access, out _); // Kernelzugriff: Lesefehler einer Dateiseite ist -EFAULT wie bei Linux
                if (frame == 0)
                {
                    return false;
                }
            }

            Span<byte> memory = _pages.Frame(frame).Slice(offset, chunk);
            if (write)
            {
                src.Slice((int)done, chunk).CopyTo(memory);
            }
            else
            {
                memory.CopyTo(dst.Slice((int)done, chunk));
            }

            done += (ulong)chunk;
        }

        return true;
    }

    /// <summary>Alle Seiten von [addr, addr+length) sind abgebildet und erlauben den Zugriff; Bereich schon geprüft, _memGate gehalten.</summary>
    private bool PagesAllowLocked(ulong addr, ulong length, bool write)
    {
        ulong end = addr + length;
        for (ulong page = addr & ~(Page - 1); page < end; page += Page)
        {
            if (!_map.TryFind(page, out MapAccess access) || access == MapAccess.None || (write && access != MapAccess.ReadWrite))
            {
                return false;
            }
        }

        return true;
    }

    // busError (V30-P3): 0, weil sich eine Dateiseite nicht lesen ließ (Linux: SIGBUS); sonst false.
    private ulong Populate(ulong page, MapAccess access, out bool busError)
    {
        ulong shared = PopulateShared(page, access, out bool isShared, out busError);
        if (isShared)
        {
            return shared;
        }

        ulong frame = NewFrame();
        if (frame == 0)
        {
            return 0;
        }

        // V30: Seiten aus einer Datei werden jetzt gelesen, vor dem Abbilden: Kein anderer Thread sieht die Seite halb gefüllt. Gelesen
        // wird unter _memGate ohne FileGate (ILinuxPageSource); die Platte nimmt darin StorageLock. Lesefehler und eine Seite ganz hinter
        // dem Dateiende (auch nach ftruncate) enden wie bei Linux mit SIGBUS.
        if (_fileExtents.Covers(page) && !_fileExtents.Fill(page, _pages.Frame(frame)))
        {
            _pages.FreeFrame(frame);
            busError = true;
            return 0;
        }

        if (!_pages.Map(page, frame, access))
        {
            _pages.FreeFrame(frame);
            return 0;
        }

        _frames[page] = frame;
        SyncBudget();
        return frame;
    }

    /// <summary>V43b: Seiten, die gegen diesen Prozess zählen, ohne die Frames seiner memfds; unter <c>_memGate</c>.</summary>
    private long PrivateFrames => _frames.Count - _budgetedShared;

    /// <summary>V43b: meldet <see cref="PrivateFrames"/> an die Grenze, nach jeder Änderung von <c>_frames</c>; unter <c>_memGate</c>.</summary>
    private void SyncBudget() => _budget.SetPrivate(PrivateFrames);

    private ulong NewFrame()
    {
        if (!_budget.AllowsPrivate(PrivateFrames))
        {
            _noFrame = true; // Prozessgrenze: SIGKILL, aber kein GC-Retry
            return 0;
        }

        ulong frame = _pages.AllocateFrame();
        if (frame == 0)
        {
            _noFrame = true;
            _frameShort = true;
        }

        return frame;
    }

    /// <summary>Gibt [start, end) frei; start und end ausgerichtet und im Adressraum der Karte.</summary>
    private bool Release(ulong start, ulong end)
    {
        // Bleibt eine Seite abgebildet (die Tabelle lehnt ab), darf der Frame nicht frei werden: er bleibt beim Prozess,
        // bis Destroy die Tabelle aufgelöst hat.
        List<ulong> stuck = new();
        _ = UnmapAndDrop(PagesWithFrames(start, end), start, end, stuck);

        if (!_map.Release(start, end - start).IsOK())
        {
            return false;
        }

        // V17: Geteilte Bereiche enden mit der Karte; eine gesperrte Seite (unten) behält ihren Verweis in _shared bis Destroy.
        TrimRegions(start, end);
        _fileExtents.Cut(start, end); // V30: Ein neuer Bereich an derselben Stelle liest nicht mehr aus der alten Datei

        // Eine solche Seite bleibt als gesperrter Bereich (None) belegt (V3-Review P3 a): Sonst legte ein späteres mmap oder brk
        // dort einen neuen Bereich an, und Programm wie Kernel sähen den alten, nicht genullten Frame. Die Sperre gilt nur für die
        // Karte (V3-Review P3 c): Der Kernel greift über IUserMemory nicht mehr zu, und ein Seitenfehler dort bricht ab. Die Tabelle
        // bildet die Seite aber weiter mit den alten Rechten ab; das Programm greift ohne Fehler weiter auf den alten Inhalt zu, bis
        // ein späteres munmap das Aufheben erneut versucht oder Destroy die Tabelle auflöst.
        bool reserved = true;
        foreach (ulong page in stuck)
        {
            // Der Bereich ist eben frei geworden und nur dieser Thread ändert die Karte; scheitert es doch, meldet munmap den
            // Fehler, statt die Seite unbemerkt zur Wiedervergabe freizugeben. Der Frame bleibt in jedem Fall beim Prozess.
            if (!_map.Reserve(page, Page, MapAccess.None, Placement.FixedNoReplace).IsOK())
            {
                reserved = false;
            }
        }

        return reserved;
    }

    /// <summary>Seiten mit Frame in [start, end): über die Frames, nicht über den Bereich, der Terabytes groß sein kann.</summary>
    private List<ulong> PagesWithFrames(ulong start, ulong end)
    {
        var pages = new List<ulong>();
        foreach (ulong page in _frames.Keys)
        {
            if (page >= start && page < end)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    private static bool InUserRange(ulong addr, ulong length) => UserRange.Contains(0, ElfLoader.UserEnd, addr, length);

    private static ulong PageUp(ulong value) => (value + Page - 1) & ~(Page - 1);

    /// <summary>PROT_* auf den Zugriff. Schreiben und Ausführen zugleich lehnt ZehOS ab (W^X, wie der Lader).</summary>
    private static bool TryAccess(int prot, out MapAccess access)
    {
        bool write = (prot & ProtWrite) != 0, exec = (prot & ProtExec) != 0;
        access = exec ? MapAccess.ReadExecute : write ? MapAccess.ReadWrite : (prot & ProtRead) != 0 ? MapAccess.Read : MapAccess.None;
        return !(write && exec);
    }
}
