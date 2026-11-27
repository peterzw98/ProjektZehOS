#if ZEHOS_LINUX_RUN
using System;
using System.IO;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.Core.Scheduler;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // UserMode und UserPageTable gelten als Vorschau.

/// <summary>
/// Ladybird V3 (nur Testbau, <c>-p:ZehOSLinuxRun=true</c>): startet ein statisches Linux-Programm (x86-64, musl) im Ring 3 auf
/// einem eigenen Kernelthread mit eigener <see cref="UserPageTable"/>. Die Brücke leitet <c>syscall</c> an
/// <see cref="LinuxSyscalls.Dispatch"/> (V2) und Seitenfehler an <see cref="LinuxUserProcess.HandlePageFault"/>; jeder andere
/// Fehler im Ring 3 bricht das Programm ab, nie den Kernel. Ein Programm zur Zeit.
/// </summary>
internal static unsafe class CosmosLinuxRun
{
    /// <summary>Speicher je Programm (Frames), danach endet ein Seitenfehler mit Abbruch.</summary>
    private const ulong MaxBytes = 512UL << 20;

    /// <summary>
    /// V43: Seiten, die Nutzer-Frames dem Kernel lassen (<see cref="UserPageTable.ReservedPages"/>): ein Achtel des Speichers,
    /// mindestens 64 MiB, höchstens 128 MiB. Ohne Reserve nahmen die Frames aller Programme (Ladybird: mehrere Prozesse je bis
    /// <see cref="MaxBytes"/>) dem Kernel-Heap die letzte Seite, und der Kernel hielt mit OutOfMemoryException an; mit ihr
    /// scheitert der Seitenfehler oder mmap des Programms.
    /// </summary>
    private const ulong ReserveMinPages = (64UL << 20) / AddressSpaceMap.PageSize;

    /// <summary>Obergrenze der Reserve (<see cref="ReserveMinPages"/>).</summary>
    private const ulong ReserveMaxPages = (128UL << 20) / AddressSpaceMap.PageSize;

    /// <summary>Größter ladbarer Teil eines Programms (V28: ohne Debugabschnitte, <see cref="FileService.ReadExecutable"/>).</summary>
    public const int MaxFileSize = 128 << 20;

    /// <summary>Rückgabe von Enter nach einem Abbruch durch den Kernel (Zeitlimit, Ausnahme in der Brücke).</summary>
    private const ulong Killed = ulong.MaxValue - 16;

    /// <summary>Rückgabe von Enter nach Strg+C oder Esc im Terminal (Status <c>signal 2</c> wie SIGINT).</summary>
    private const ulong Interrupted = ulong.MaxValue - 17;

    /// <summary>
    /// So lange wartet <see cref="Run"/> nach dem Zeitlimit noch auf das Programm. Es verlässt den Ring 3 mit dem nächsten
    /// Interrupt dort oder bei der Rückkehr aus dem laufenden Aufruf; Schlafen im Kernel prüft den Abbruch je <see cref="SleepSlice"/>.
    /// </summary>
    private const int LeaveGraceMs = 5000;

    /// <summary>Längster Schlaf am Stück (poll mit Frist): danach prüft der Thread, ob das Programm abgebrochen wird (V3-Review P2).</summary>
    private const int SleepSlice = 50;

    private const ulong PageFault = 14;
    private const int Pid = 1;

    /// <summary>
    /// Umgebung wie <c>experiments/LinuxAbi/run-linux.py</c>, damit die Ausgaben mit <c>expected/</c> vergleichbar sind. V28b: HOME ist die
    /// Prozesswurzel; Ladybird und fontconfig legen Profil und Cache darunter an (<c>$HOME/.local</c>, <c>$HOME/.cache</c>) und warnen sonst.
    /// V37: dazu TMPDIR, XDG_RUNTIME_DIR und XDG_CACHE_HOME (<see cref="LinuxRunEnvironment.Defaults"/>), vor dem Start angelegt.
    /// </summary>
    public static readonly string[] Environment = LinuxRunEnvironment.Defaults;

    private static int busy;
    private static bool initialized;
    private static ulong hhdm;

    // Das laufende Programm; die Handler finden es über den Thread, auf dem sie laufen.
    private static Running? current;

    /// <summary>
    /// V36: Kernelthreads aller Prozesse eines Laufs zusammen (Linux-Threads, fork-, vfork- und spawn-Kinder). Vorher galt hier die
    /// Grenze eines Prozesses (<see cref="LinuxUserProcess.MaxThreads"/>, 64); Ladybird headless braucht über alle Prozesse mehr
    /// (Host-strace V31: Hub 16, Compositor 11, RequestServer 6+4, SystemProbe 1, je WebContent 19-21), und der 65. scheiterte mit
    /// <c>-EAGAIN</c> in <c>pthread_create</c>. Je Platz läuft ein Kernelthread mit eigenem Kernelstack; 256 lässt Ladybird mit drei
    /// WebContent Luft und begrenzt einen Lauf weiterhin.
    /// </summary>
    public const int MaxRunThreads = 256;

    private sealed class Running
    {
        public readonly TerminalDescriptor Terminal;

        /// <summary>Prozesswurzel auf der Datenplatte (Kernelpfad) oder null für ein leeres Dateisystem im Arbeitsspeicher (V8).</summary>
        public string? Root;

        /// <summary>V37: Umgebung des Programms; ihre Standardordner legt <see cref="Execute"/> vor dem Start an.</summary>
        public string[] Envp = [];

        /// <summary>Anfängliches Arbeitsverzeichnis im Prozess (wird angelegt) oder null für "/"; nur die Gastprobe setzt es.</summary>
        public string? StartCwd;

        /// <summary>Programmpfad im Prozess für /proc/self/exe (V12) oder null, wenn das Programm außerhalb der Wurzel liegt.</summary>
        public string? ExePath;

        /// <summary>V26: argv[0] als absoluter Pfad im Prozess; execve darauf startet <see cref="ExePath"/> (siehe <see cref="LinuxUserProcess.StartPath"/>).</summary>
        public string? StartPath;

        /// <summary>Gesetzt bei einem Vordergrundprogramm der Shell; dann meldet der Programmthread das Ende selbst.</summary>
        public LinuxTerminalSession? Session;
        public LinuxUserProcess? Process;
        public SchedulerThread? Thread;
        public ulong Result = UserMode.EnterRefused;
        public bool Started;
        public string? Error;
        public ulong FaultVector, FaultError, FaultAddress, FaultRip;
        public bool Fault;

        /// <summary>V30-P3: Signal des Fehlers (SIGBUS bei einer unlesbaren Dateiseite, sonst nach dem Vektor).</summary>
        public int FaultSignal;

        /// <summary>Gesetzt beim Zeitlimit: Schlafen im Kernel endet, der Aufruf kehrt zurück, die Rückkehr verlässt den Ring 3.</summary>
        public volatile bool Cancelled;

        /// <summary>
        /// Wer zuletzt fertig ist (<see cref="Run"/> oder der Programmthread), gibt <see cref="busy"/> frei. Endet das Programm auch
        /// nach <see cref="LeaveGraceMs"/> nicht, kehrt die Shell zurück, und ein neues run geht erst nach seinem Ende.
        /// </summary>
        public int Owners = 2;

        /// <summary>
        /// V10: die Kernelthreads der Linux-Threads, Platz 0 der Hauptthread. Geschrieben unter der Sperre auf dem Feld, gelesen
        /// ohne Sperre in den Handlern (Referenzen, volatil gelesen); ein Platz wird erst frei, wenn sein Thread den Ring 3 verlassen hat.
        /// </summary>
        public readonly Bridge?[] Threads = new Bridge?[MaxRunThreads];

        /// <summary>
        /// V36: höchster je belegter Platz + 1 (wächst nur, unter der Sperre geschrieben, nach dem Platz). <see cref="CurrentBridge"/> läuft
        /// bei jedem Aufruf und durchsucht nur bis hierhin, damit die größere Tabelle kleine Läufe nicht verlangsamt.
        /// </summary>
        public int ThreadsUsed;

        /// <summary>Kernelthreads des Programms, die noch laufen; der Hauptthread räumt erst auf, wenn er der letzte ist.</summary>
        public int KernelThreads;

        public Running(TerminalDescriptor terminal) => Terminal = terminal;
    }

    /// <summary>Ein Linux-Thread im Kernel (V10): sein Kernelthread und, bis zu seinem ersten Aufruf, die Register aus clone.</summary>
    private sealed class Bridge
    {
        public required LinuxThread Linux;

        /// <summary>Prozess des Threads; im vfork-Kind (V15) bis zu execve oder exit das Kind, danach wieder der Elternprozess.</summary>
        public required LinuxUserProcess Process;
        public SchedulerThread? Sched;
        public int Slot;

        /// <summary>Frame des laufenden Aufrufs (nur auf dem eigenen Kernelstack gültig, nur dort gelesen).</summary>
        public UserFrame* Frame;

        /// <summary>Register des Elternthreads beim clone, mit rax 0 und dem neuen Stack.</summary>
        public UserFrame Start;

        /// <summary>
        /// Gesetzt, bis der Kindthread seinen ersten Aufruf macht. Er betritt den Ring 3 auf dem <c>syscall</c> des Elternthreads
        /// (<see cref="EntryRip"/>) mit genullten Registern; der Aufruf kommt sofort und übernimmt <see cref="Start"/> statt
        /// verteilt zu werden. Der Rückweg aus einem Aufruf stellt jedes Register aus dem Frame her (UserMode.s), so braucht
        /// clone keinen Cosmos-Patch.
        /// </summary>
        public bool Pending;
        public ulong EntryRip;

        /// <summary>
        /// V21 fork: fxsave-Abbild des Elternthreads beim clone; der erste Aufruf (<see cref="Pending"/>) legt es unter den Frame, fxrstor64
        /// lädt es beim Rücksprung. null bei Threads (die beginnen mit frischem FPU-Zustand).
        /// </summary>
        public byte[]? StartFpu;

        /// <summary>V15 vfork: Elternprozess, Elternthread und dessen Register beim clone; gesetzt, solange das Kind auf diesem Thread läuft.</summary>
        public LinuxUserProcess? VforkParent;
        public LinuxThread? VforkThread;
        public UserFrame VforkFrame;

        /// <summary>V15: Das vfork-Kind hat execve oder exit gerufen; nach dem Aufruf läuft der Thread wieder als Elternthread weiter.</summary>
        public bool Resume;

        /// <summary>Ausnahme im Ring 3 auf diesem Thread (V15: ein Kindprozess meldet sie selbst, nicht über <see cref="Running"/>).</summary>
        public bool Fault;
        public ulong FaultVector;
        public int FaultSignal; // V30-P3: wie Running.FaultSignal

        /// <summary>V38: Fehlercode, Adresse (CR2) und RIP der Ausnahme; im Handler nur gespeichert, gemeldet nach dem Verlassen des Ring 3.</summary>
        public ulong FaultError, FaultAddress, FaultRip;

        /// <summary>
        /// V38: Ein Signalhandler des Programms hat die Ausnahme übernommen (etwa der Absturzbericht von Ladybird, der danach mit 128 + Signal
        /// endet). Die Felder oben tragen sie; der nächste Aufruf dieses Threads meldet sie im Threadkontext (<see cref="ReportFault"/>).
        /// </summary>
        public bool FaultReportPending;
    }

    /// <summary>Ergebnis eines Laufs: Status wie bei run-linux.py (<c>exit n</c>, <c>signal n</c>) oder ein Fehlertext.</summary>
    internal readonly record struct Outcome(bool Ran, string Status, string Detail);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "VirtualToPhysical")]
    private static extern ulong ToPhysical([UnsafeAccessorType("Cosmos.Kernel.Core.Memory.PageAllocator, Cosmos.Kernel.Core")] object? owner, ulong address);

    /// <summary>
    /// Shell: <c>run [NAME=WERT...] &lt;datei&gt; [argumente...]</c>; die Zuweisungen ergänzen oder ersetzen <see cref="Environment"/> (V37). argv[0] ist der Pfad wie angegeben. Mit <paramref name="shell"/> läuft das
    /// Programm neben der Ereignisschleife (V3 stdin): Der Befehl kehrt sofort zurück, die Shell holt die Ausgabe in
    /// <see cref="ShellService.Poll"/>, Enter-Zeilen gehen an fd 0, Strg+C oder Esc bricht ab; ohne Zeitlimit.
    /// Dateien (V8): Das aktuelle Verzeichnis der Shell ist die Prozesswurzel "/" und das Arbeitsverzeichnis des Programms
    /// (etwa <c>cd /data/s1</c>, dann <c>run files</c>); in "/" oder direkt auf einem Laufwerk bekommt es ein leeres Dateisystem im Arbeitsspeicher.
    /// Eine Wurzel auf der Platte braucht Schreib- und Verwaltungsrecht des angemeldeten Nutzers (<see cref="FileService.AuthorizeProcessRoot"/>).
    /// </summary>
    public static ResultErr RunFile(FileService files, string[] words, ITextOutput output, ShellService? shell)
    {
        if (words.Length == 0)
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: run [NAME=WERT...] <datei> [argumente...]");
        }

        // V37: führende NAME=WERT-Wörter setzen die Umgebung wie env; der Rest ist der Programmaufruf.
        if (!LinuxRunEnvironment.Split(words, out string[] envp, out string[] arguments, out string envError))
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "run: " + envError);
        }

        ResultObject<byte[]> file = files.ReadExecutable(arguments[0], MaxFileSize);
        if (!file.IsOK())
        {
            return file.Status;
        }

        string? root = RootFor(files.CurrentDirectory);
        if (root != null)
        {
            // Review V8 (P1): Das Programm schreibt im ganzen Unterbaum ohne eigene Rechteprüfung; cd prüft nur Lesen.
            ResultErr allowed = files.AuthorizeProcessRoot(root);
            if (!allowed.IsOK())
            {
                return ResultErr.CreateFailure(allowed.Code, "run: " + allowed.Message);
            }
        }

        // V12: Ladybird sucht seine ICU-Daten relativ zu /proc/self/exe. ReadBytes hat denselben Pfad eben gelesen.
        string? exe = LinuxPath.InsideRoot(System.IO.Path.GetFullPath(arguments[0], files.CurrentDirectory), root);
        if (shell != null)
        {
            return StartForeground(file.Value!, arguments, envp, shell, root, exe);
        }

        // Spiegel ins serielle Protokoll für scripts/test-linux-abi.py (V5): rohe Bytes als Hex, Status wie run-linux.py.
        Serial("LINUXRUN BEGIN " + arguments[0]);
        Outcome outcome = Run(file.Value!, arguments, envp, output, 120, data => Serial("LINUXRUN OUT " + Convert.ToHexString(data)), root,
            exePath: exe);
        Serial("LINUXRUN END " + (outcome.Ran ? outcome.Status : "notrun " + outcome.Detail));
        if (!outcome.Ran)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "run: " + outcome.Detail);
        }

        output.WriteLine("[" + outcome.Status + (outcome.Detail.Length > 0 ? ", " + outcome.Detail : string.Empty) + "]");
        return ResultErr.CreateSuccess();
    }

    /// <summary>
    /// Prozesswurzel für <c>run</c> aus dem Verzeichnis der Shell; null (Arbeitsspeicher) für "/" und für einen Einhängepunkt selbst.
    /// "/" nimmt <see cref="DirectoryFileStore"/> nicht an, und ein ganzes Laufwerk (etwa <c>/data</c>) gäbe dem Programm die
    /// Systemdateien darauf (Konten, Pakete); erst ein Unterordner wie <c>/data/s1</c> wird Wurzel.
    /// </summary>
    private static string? RootFor(string directory)
    {
        string root = directory.TrimEnd('/');
        if (root.Length == 0)
        {
            return null;
        }

        bool mountPoint = StorageLock.Run(() =>
        {
            foreach (Cosmos.Kernel.System.FileSystem.VfsMount mount in Cosmos.Kernel.System.FileSystem.VfsManager.Mounts)
            {
                if (string.Equals(mount.MountPoint.TrimEnd('/'), root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        });
        return mountPoint ? null : root;
    }

    /// <summary>
    /// Startet das Programm als Vordergrundprogramm der Shell und kehrt sofort zurück. Das Ende meldet der Programmthread selbst
    /// (Statuszeile in der Sitzung, LINUXRUN END im seriellen Protokoll).
    /// </summary>
    /// <summary>V26: argv[0] als Pfad im Prozess (relativ zum Start-Arbeitsverzeichnis); null ohne Programmpfad in der Wurzel.</summary>
    private static string? StartPathOf(string[] argv, string? cwd, string? exePath)
    {
        if (exePath == null || argv.Length == 0 || argv[0].Length == 0)
        {
            return null;
        }

        return LinuxPath.Normalize(argv[0][0] == '/' ? argv[0] : (cwd ?? "/") + "/" + argv[0]);
    }

    private static ResultErr StartForeground(byte[] elf, string[] arguments, string[] envp, ShellService shell, string? root, string? exePath)
    {
        if (shell.HasForeground)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "run: in dieser Sitzung läuft schon ein Programm");
        }

        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "run: es läuft schon ein Programm");
        }

        Running? run = null;
        bool started = false;
        try
        {
            // Auch der Shell-Weg (run, Ladybird-Demo) braucht die Reserve, nicht nur Run().
            ReserveKernelPages();
            if (!Prepare(elf, arguments, envp, root, out UserImage? image, out string why))
            {
                return ResultErr.CreateFailure(ResultCode.Failure, "run: " + why);
            }

            var session = new LinuxTerminalSession(Thread.Sleep);
            string name = arguments[0];
            run = new Running(new TerminalDescriptor(session) { Mirror = data => Serial("LINUXRUN OUT " + Convert.ToHexString(data)) })
            {
                Session = session,
                Root = root,
                Envp = envp,
                ExePath = exePath,
                StartPath = StartPathOf(arguments, null, exePath),

                // Nur der Programmthread gibt busy frei; die Shell wartet nicht auf ihn.
                Owners = 1,
            };
            Running target = run;
            session.OnInterrupt = () => Abort(target, Interrupted);
            session.SizeSource = () => shell.WindowSize;
            ResultErr attached = shell.AttachForeground(session);
            if (!attached.IsOK())
            {
                return attached;
            }

            Serial("LINUXRUN BEGIN " + name);
            var thread = new Thread(() => Body(target, image!, elf));
            thread.Start();
            started = true;
            return ResultErr.CreateSuccess();
        }
        finally
        {
            // Ohne Programmthread gibt es niemanden, der busy später freigibt.
            if (!started)
            {
                Volatile.Write(ref busy, 0);
            }
        }
    }

    /// <summary>Lässt das Programm den Ring 3 verlassen (Zeitlimit oder Strg+C); Schlafen und Lesen im Kernel enden mit der nächsten Scheibe.</summary>
    private static void Abort(Running run, ulong result)
    {
        run.Cancelled = true;
        Interlocked.MemoryBarrier();
        SchedulerThread? target = Volatile.Read(ref run.Thread);
        if (target != null)
        {
            UserMode.RequestLeave(target, result);
        }

        LeaveAll(run, null, result);
    }

    /// <summary>
    /// Alle Kernelthreads des Programms außer <paramref name="except"/> verlassen den Ring 3 (exit_group, Absturz eines Threads,
    /// Abbruch). Wartende in futex kehren zurück, ein Lesen auf fd 0 bekommt Dateiende; ein Thread, der noch nicht im Ring 3
    /// ist, sieht <see cref="Running.Cancelled"/> und betritt ihn nicht.
    /// </summary>
    private static void LeaveAll(Running run, Bridge? except, ulong result)
    {
        run.Cancelled = true;
        Interlocked.MemoryBarrier();
        foreach (Bridge? b in run.Threads)
        {
            SchedulerThread? sched = b == null || b == except ? null : Volatile.Read(ref b.Sched);
            if (sched != null)
            {
                UserMode.RequestLeave(sched, result);
            }
        }

        // Alle Prozesse des Laufs (V15): Kinder enden mit dem ersten Prozess.
        run.Process?.Futexes.Abort();
        for (int i = 0; i < run.Threads.Length; i++)
        {
            Volatile.Read(ref run.Threads[i])?.Process.Futexes.Abort();
        }

        if (Volatile.Read(ref run.KernelThreads) > 1)
        {
            run.Session?.EndInput();
        }
    }

    /// <summary>
    /// V15: Nur die Threads von <paramref name="process"/> verlassen den Ring 3 (exit_group oder Absturz in einem Kindprozess); für den
    /// ersten Prozess des Laufs wie <see cref="LeaveAll"/>.
    /// </summary>
    private static void LeaveProcess(Running run, LinuxUserProcess process, Bridge? except, ulong result)
    {
        if (ReferenceEquals(process, run.Process))
        {
            LeaveAll(run, except, result);
            return;
        }

        foreach (Bridge? b in run.Threads)
        {
            SchedulerThread? sched = b == null || b == except || !ReferenceEquals(b.Process, process) ? null : Volatile.Read(ref b.Sched);
            if (sched != null)
            {
                UserMode.RequestLeave(sched, result);
            }
        }

        process.Futexes.Abort();
    }

    /// <summary>Ende eines Vordergrundprogramms: letzte Zeile, Statuszeile für die Shell, Spiegel ins serielle Protokoll.</summary>
    private static void Report(Running run)
    {
        run.Terminal.Flush();
        Outcome outcome = Describe(run, false);
        Serial("LINUXRUN END " + (outcome.Ran ? outcome.Status : "notrun " + outcome.Detail));
        run.Session!.Finish(outcome.Ran
            ? "[" + outcome.Status + (outcome.Detail.Length > 0 ? ", " + outcome.Detail : string.Empty) + "]"
            : "run: " + outcome.Detail);
    }

    /// <summary>Initialisiert den Ring 3 und lädt das Abbild (Kernelseite, noch ohne Programmthread).</summary>
    private static bool Prepare(byte[] elf, string[] argv, string[] envp, string? root, out UserImage? image, out string why)
    {
        image = null;
        if (!Initialize(out why))
        {
            return false;
        }

        try
        {
            // AT_RANDOM ist die Stack-Kanarie von musl: nur aus der Hardware, ohne Ersatz (V1-Review P3 (2)).
            byte[] random = new byte[16];
            CosmosSaltSource.Fill(random);
            image = ElfLoader.Load(elf, argv, envp, new ElfLoadOptions
            {
                Random = random,
                HwCap = (uint)X86Base.CpuId(1, 0).Edx,
                ReadInterpreter = root == null ? null : (string path, out byte[] content) => ReadFromRoot(root, path, out content),
            });
            return true;
        }
        catch (ElfLoadException exception)
        {
            why = exception.Message;
        }
        catch (HardwareRandomUnavailableException exception)
        {
            why = "kein Hardwarezufall für AT_RANDOM: " + exception.Message;
        }

        return false;
    }

    /// <summary>
    /// V25: liest den Interpreter des ersten Programms aus der Prozesswurzel (Kernelpfad). Das Dateisystem des Prozesses gibt es zu
    /// diesem Zeitpunkt noch nicht; deshalb direkt über die Datenplatte. Grenze: Verweise der Prozesswurzel (CosmosLinuxDiskLinks) folgt
    /// das nicht; Alpines ld-musl-x86_64.so.1 ist eine gewöhnliche Datei. Spätere execve lesen über das Dateisystem des Prozesses.
    /// </summary>
    private static long ReadFromRoot(string root, string path, out byte[] content)
    {
        content = [];
        const long ENOENT = 2, EACCES = 13, ENOMEM = 12;
        // Je Komponente: ".." aus der Wurzel heraus ist nie erlaubt, auch nicht mitten im Pfad (/lib/../../x) oder hinter "//".
        foreach (string part in path.Split('/'))
        {
            if (part == "..")
            {
                return -EACCES;
            }
        }

        string full = root + path;
        bool tooLarge = false;
        byte[]? bytes = StorageLock.Run(() =>
        {
            if (!File.Exists(full))
            {
                return null;
            }

            if (new FileInfo(full).Length > LinuxUserProcess.MaxExecFileSize)
            {
                tooLarge = true;
                return [];
            }

            return File.ReadAllBytes(full);
        });
        if (bytes == null)
        {
            return -ENOENT;
        }

        if (tooLarge)
        {
            return -ENOMEM; // wie ReadExecutable bei Größe über MaxExecFileSize
        }

        // Eine leere Datei ist kein ELF-Abbild: der Lader meldet ENOEXEC (wie Linux), nicht ENOMEM.
        content = bytes;
        return 0;
    }

    /// <summary>Setzt die Kernel-Reserve (<see cref="ReserveMinPages"/>) beim ersten Programm; Threadkontext, vor jedem Prozess.</summary>
    private static void ReserveKernelPages()
    {
        if (UserPageTable.ReservedPages != 0)
        {
            return;
        }

        ulong pages = Math.Clamp(UserPageTable.TotalPages / 8, ReserveMinPages, ReserveMaxPages);
        UserPageTable.ReservedPages = pages;
        Serial("[linux-run] Kernel-Reserve " + (pages * AddressSpaceMap.PageSize >> 20).ToString() + " MiB, frei "
            + (UserPageTable.FreePages * AddressSpaceMap.PageSize >> 20).ToString() + " von "
            + (UserPageTable.TotalPages * AddressSpaceMap.PageSize >> 20).ToString() + " MiB");
    }

    private static void Serial(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString(text + "\n");

    /// <summary>Lädt und startet das Programm und wartet auf sein Ende, höchstens <paramref name="timeoutSeconds"/> Sekunden.</summary>
    /// <param name="root">Prozesswurzel (Kernelpfad, wird bei Bedarf angelegt) oder null für ein leeres Dateisystem im Arbeitsspeicher.</param>
    /// <param name="cwd">Arbeitsverzeichnis im Prozess (absolut, wird angelegt) oder null für "/".</param>
    public static Outcome Run(byte[] elf, string[] argv, string[] envp, ITextOutput output, int timeoutSeconds, ByteSink? mirror = null,
        string? root = null, string? cwd = null, string? exePath = null)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return new Outcome(false, string.Empty, "es läuft schon ein Programm");
        }

        Running? run = null;
        try
        {
            ReserveKernelPages();
            if (!Prepare(elf, argv, envp, root, out UserImage? image, out string why))
            {
                return new Outcome(false, string.Empty, why);
            }

            var started = new Running(new TerminalDescriptor(output) { Mirror = mirror }) { Root = root, Envp = envp, StartCwd = cwd, ExePath = exePath, StartPath = StartPathOf(argv, cwd, exePath) };
            var thread = new Thread(() => Body(started, image!, elf));
            thread.Start();
            run = started;
            bool timedOut = false;
            if (!thread.Join(timeoutSeconds * 1000))
            {
                timedOut = true;
                Abort(run, Killed);

                // Mit Frist (V3-Review P2): hängt das Programm im Kernel an einer Stelle ohne Abbruchprüfung, kehrt wenigstens die
                // Shell zurück. Das Terminal nimmt danach nichts mehr an, der Thread räumt beim Ende selbst auf.
                if (!thread.Join(LeaveGraceMs))
                {
                    run.Terminal.Close();
                    return new Outcome(true, "signal 9", "Zeitlimit; das Programm endet nicht, run bleibt bis zu seinem Ende gesperrt");
                }
            }

            run.Terminal.Flush();
            return Describe(run, timedOut);
        }
        finally
        {
            Release(run);
        }
    }

    /// <summary>Gibt <see cref="busy"/> frei, wenn der andere Beteiligte schon fertig ist (oder es keinen Programmthread gab).</summary>
    private static void Release(Running? run)
    {
        if (run == null || Interlocked.Decrement(ref run.Owners) == 0)
        {
            Volatile.Write(ref busy, 0);
        }
    }

    private static Outcome Describe(Running run, bool timedOut)
    {
        if (!run.Started)
        {
            return new Outcome(false, string.Empty, run.Error ?? "nicht gestartet");
        }

        if (run.Process?.TerminatedBySignal is int signal)
        {
            return new Outcome(true, "signal " + signal, "tgkill");
        }

        if (run.Process?.ExitCode is int code)
        {
            return new Outcome(true, "exit " + code, string.Empty);
        }

        if (run.Fault)
        {
            return new Outcome(true, "signal " + run.FaultSignal, "Ausnahme " + run.FaultVector + " Fehlercode 0x" + run.FaultError.ToString("X")
                + " Adresse 0x" + run.FaultAddress.ToString("X") + " RIP 0x" + run.FaultRip.ToString("X")
                + (run.Error != null ? " " + run.Error : string.Empty));
        }

        if (run.Result == Interrupted)
        {
            return new Outcome(true, "signal 2", "abgebrochen (Strg+C)");
        }

        if (timedOut || run.Result == Killed)
        {
            return new Outcome(true, "signal 9", timedOut ? "Zeitlimit" : "abgebrochen: " + run.Error);
        }

        string reason = run.Result == UserMode.InvalidReturn ? "ungültige Register für die Rückkehr in den Ring 3"
            : run.Result == UserMode.EnterRefused ? "Ring 3 abgelehnt" : "Ende ohne exit (0x" + run.Result.ToString("X") + ")";
        return new Outcome(true, "signal 11", reason);
    }

    /// <summary>Signal, das Linux für die Ausnahme schickte (Status wie bei run-linux.py); Tabelle seit V24 in <see cref="LinuxFault"/>.</summary>
    private static int SignalOf(ulong vector) => LinuxFault.SignalOf(vector);

    private static void Body(Running run, UserImage image, byte[] elf)
    {
        try
        {
            Execute(run, image, elf);
        }
        finally
        {
            if (run.Session != null)
            {
                try
                {
                    Report(run);
                }
                catch (Exception exception)
                {
                    // Ohne Statuszeile bliebe das Terminal beim Programm; dann wenigstens den Fehler melden.
                    run.Session.Finish("run: " + exception.GetType().Name + ": " + exception.Message);
                }
            }

            // Zuletzt: danach darf ein neues run beginnen.
            Release(run);
        }
    }

    private static void Execute(Running run, UserImage image, byte[] elf)
    {
        Volatile.Write(ref run.Thread, FaultIsolation.CurrentSchedulerThread());
        ILinuxFileSystem? fileSystem = null;
        if (run.Root != null)
        {
            // Datenplatte FAT: Namen ohne Groß-/Kleinschreibung. Fehlt die Wurzel oder lässt sie sich nicht anlegen, startet das
            // Programm nicht, statt still ohne Dateien zu laufen.
            ResultObject<DirectoryFileStore> store = DirectoryFileStore.Open(run.Root, new CosmosLinuxDiskLinks(run.Root), StringComparison.OrdinalIgnoreCase);
            if (!store.IsOK())
            {
                run.Error = "Prozesswurzel " + run.Root + ": " + store.Status.Message;
                return;
            }

            // V34: Eigentümer der Dateien = Benutzer des Prozesses (AT_UID/getuid); sonst meldet stat 0, und Ladybird lehnt sein Crash-Verzeichnis ab.
            fileSystem = new LinuxFileSystem(store.Value!, run.Terminal, uid: (uint)image.Uid, gid: (uint)image.Gid);
        }

        UserPageTable? table = UserPageTable.TryCreate();
        if (table == null)
        {
            run.Error = "keine Seitentabelle (kein Speicher)";
            return;
        }

        var process = new LinuxUserProcess(new CosmosUserPages(table), run.Terminal, Pid, MaxBytes, Host, fileSystem)
        {
            ExePath = run.ExePath,
            StartPath = run.StartPath,
            HwCap = (uint)X86Base.CpuId(1, 0).Edx,
        };
        Wire(run, table, process);

        // V10: exit_group (oder das Ende des letzten Threads) beendet alle Threads; exit nur den aufrufenden.
        process.OnExit = code =>
        {
            UserMode.LeaveCurrent((ulong)code);
            LeaveAll(run, CurrentBridge(run), (ulong)code);
        };

        // V15 kill aus einem Kindprozess: Der aufrufende Thread gehört nicht zu diesem Prozess und bleibt im Ring 3, bis der Lauf ihn beendet.
        process.OnKilled = code => LeaveAll(run, CurrentBridge(run), (ulong)code);
        var main = new Bridge { Linux = process.MainThread, Process = process, Sched = FaultIsolation.CurrentSchedulerThread() };
        BindCpuTime(main);
        run.Threads[0] = main;
        run.ThreadsUsed = 1;
        run.KernelThreads = 1;
        run.Process = process;

        // V37: Standardordner der Umgebung (TMPDIR, XDG_*) beschreibbar anlegen; ein vorhandener Ordner bleibt, wie er ist.
        foreach ((string path, uint mode) in LinuxRunEnvironment.DirectoriesFor(run.Envp))
        {
            long made = process.FileSystem.Mkdir(path, mode);
            if (made < 0 && made != -LinuxErrno.EEXIST)
            {
                // Nur Warnung: Ein Programm kommt oft ohne /tmp oder /.cache aus (etwa schreibgeschützte Wurzel); ein Abbruch wäre schlimmer.
                byte[] warning = Encoding.UTF8.GetBytes("run: Warnung: Ordner " + path + " nicht angelegt (errno " + (-made) + ")\n");
                run.Terminal.Write(warning);
            }
        }

        if (run.StartCwd != null)
        {
            // Wie run-linux.py, das in einem Unterordner startet: "/" zeigte sonst /dev und getcwd hätte keinen Ordnernamen.
            long made = process.FileSystem.Mkdir(run.StartCwd, 0x1ED);
            if (made < 0 && made != -LinuxErrno.EEXIST)
            {
                run.Error = "Arbeitsverzeichnis " + run.StartCwd + ": errno " + (-made);
                return;
            }

            process.Cwd = run.StartCwd;
        }

        bool bound = false;
        try
        {
            // Alle Threads eines Programms auf einer CPU: UserPageTable kennt noch keinen TLB-Shootdown (N4) und lehnt Unmap und
            // Protect ab, solange eine andere CPU auf der Tabelle läuft. Kindthreads erben die Menge ihres Erzeugers.
            var cpus = Cosmos.Kernel.System.Threading.ThreadAffinity.Current;
            if (cpus.Count > 1)
            {
                Cosmos.Kernel.System.Threading.ThreadAffinity.Current = Cosmos.Kernel.System.Threading.CpuSet.Single(cpus.First);
            }

            ResultErr loaded = process.Load(image, elf);
            if (!loaded.IsOK())
            {
                run.Error = loaded.Message;
                return;
            }

            table.BindToCurrentThread();
            bound = true;
            Volatile.Write(ref current, run);

            // Gegenstück zur Sperre in Run: Thread schreiben, dann Cancelled lesen. Sah Run den Thread noch nicht, sieht dieser
            // Thread hier das Zeitlimit und betritt den Ring 3 nicht.
            Interlocked.MemoryBarrier();
            if (run.Cancelled)
            {
                run.Error = "abgebrochen vor dem Start";
                return;
            }

            run.Started = true;
            run.Result = UserMode.Enter(image.Entry, image.StackPointer);
        }
        catch (Exception exception)
        {
            run.Error ??= exception.GetType().Name + ": " + exception.Message;
        }
        finally
        {
            // V19: execve ohne vfork hat den Prozess ersetzt (run.Process und der Hauptthread zeigen auf das neue Abbild).
            process = main.Process;

            // Erst wenn kein anderer Thread mehr im Ring 3 oder im Kernel auf dem Adressraum arbeitet, darf er weg.
            WaitForThreads(run, process, main);
            Volatile.Write(ref current, null);
            if (bound)
            {
                UserPageTable.UnbindCurrentThread();
            }

            process.Destroy();
        }
    }

    /// <summary>
    /// Der Hauptthread hat den Ring 3 verlassen. Mit exit (pthread_exit in main) laufen die anderen weiter, bis der letzte endet;
    /// sonst (exit_group, Absturz, Abbruch, Fehler) beendet er sie. Gewartet wird in Scheiben, der Abbruch wird je Scheibe erneuert.
    /// </summary>
    private static void WaitForThreads(Running run, LinuxUserProcess process, Bridge main)
    {
        while (Volatile.Read(ref run.KernelThreads) > 1)
        {
            if (process.ExitCode.HasValue || run.Cancelled || !main.Linux.Exited)
            {
                LeaveAll(run, main, Killed);
            }

            Thread.Sleep(SleepSlice);
        }

        process.ThreadGone(main.Linux);
    }

    /// <summary>clone im Kernel: ein Kernelthread für den neuen Linux-Thread, mit den Registern des aufrufenden Threads.</summary>
    private static bool StartChild(Running run, UserPageTable table, LinuxUserProcess process, LinuxThread child)
    {
        Bridge? parent = CurrentBridge(run);
        if (parent == null || parent.Frame == null || parent.Frame->Rip < 2)
        {
            return false;
        }

        var b = new Bridge { Linux = child, Process = process, Start = *parent.Frame, Pending = true, EntryRip = parent.Frame->Rip - 2 };
        b.Start.Rax = 0;
        if (child.StackPointer != 0)
        {
            b.Start.Rsp = child.StackPointer;
        }

        if (!TakeSlot(run, b))
        {
            return false;
        }

        child.Host = b;
        try
        {
            new Thread(() => ChildBody(run, table, process, b)).Start();
            return true;
        }
        catch (Exception)
        {
            ReleaseSlot(run, b);
            return false;
        }
    }

    /// <summary>Trägt den Thread in <see cref="Running.Threads"/> ein (alle Prozesse des Laufs zusammen); false, wenn voll oder abgebrochen.</summary>
    private static bool TakeSlot(Running run, Bridge b)
    {
        lock (run.Threads)
        {
            int slot = Array.IndexOf(run.Threads, null);
            if (slot < 0 || run.Cancelled)
            {
                return false;
            }

            b.Slot = slot;
            Volatile.Write(ref run.Threads[slot], b);
            if (slot >= run.ThreadsUsed)
            {
                Volatile.Write(ref run.ThreadsUsed, slot + 1);
            }
            Interlocked.Increment(ref run.KernelThreads);
            return true;
        }
    }

    private static void ReleaseSlot(Running run, Bridge b)
    {
        lock (run.Threads)
        {
            if (run.Threads[b.Slot] == b)
            {
                Volatile.Write(ref run.Threads[b.Slot], null);
                Interlocked.Decrement(ref run.KernelThreads);
            }
        }
    }

    /// <summary>
    /// V29c: CPU-Zeit des Linux-Threads aus <c>SchedulerThread.TotalRuntime</c> des Kernelthreads, gezählt ab dem Zeitpunkt der Zuordnung
    /// (der Kernelthread gehörte zuvor womöglich einer anderen Aufgabe). Die Laufzeit zählt der Zeitgeber je Tick (OnTick), nicht je Befehl.
    /// </summary>
    private static void BindCpuTime(Bridge b)
    {
        SchedulerThread? sched = Volatile.Read(ref b.Sched);
        if (sched == null)
        {
            return;
        }

        ulong start = sched.TotalRuntime;
        b.Linux.CpuNanosSource = () => (long)(sched.TotalRuntime - start);
    }

    private static void ChildBody(Running run, UserPageTable table, LinuxUserProcess process, Bridge b)
    {
        bool bound = false;
        try
        {
            Volatile.Write(ref b.Sched, FaultIsolation.CurrentSchedulerThread());
            BindCpuTime(b);
            table.BindToCurrentThread();
            bound = true;
            UserMode.SetFsBase(b.Linux.FsBase);

            // Wie im Hauptthread: Sched schreiben, dann Cancelled lesen; LeaveAll schreibt Cancelled, dann liest es Sched.
            Interlocked.MemoryBarrier();
            if (run.Cancelled || process.ExitCode.HasValue)
            {
                return;
            }

            UserMode.Enter(b.EntryRip, b.Start.Rsp);
            if (b.Fault && !ReferenceEquals(process, run.Process))
            {
                ReportFault(process, b);
                process.RecordFault(b.FaultSignal);
            }


            // Weder exit noch exit_group: Absturz oder ungültige Register. Linux beendet dann den ganzen Prozess.
            if (!b.Linux.Exited && !process.ExitCode.HasValue)
            {
                LeaveProcess(run, process, b, Killed);
            }
        }
        catch (Exception exception)
        {
            run.Error ??= exception.GetType().Name + ": " + exception.Message;
            LeaveProcess(run, process, b, Killed);
        }
        finally
        {
            if (bound)
            {
                UserPageTable.UnbindCurrentThread();
            }

            process.ThreadGone(b.Linux);
            ReleaseSlot(run, b);
        }
    }

    private static bool Initialize(out string why)
    {
        why = string.Empty;
        if (initialized)
        {
            return true;
        }

        if (!UserPageTable.ShareKernelHalf())
        {
            why = "keine Benutzerseitentabellen (ShareKernelHalf braucht EFER.NXE)";
            return false;
        }

        // AddressSpace.HhdmOffset ist intern; der Heap liegt im HHDM (wie N1-A6 den Versatz bestimmt).
        void* probe = NativeMemory.Alloc(64);
        hhdm = (ulong)probe - ToPhysical(null, (ulong)probe);
        NativeMemory.Free(probe);
        UserMode.SyscallHandler = &OnSyscall;
        UserMode.FaultHandler = &OnFault;
        if (!UserMode.Initialize())
        {
            why = "Ring 3 auf dieser CPU nicht verfügbar";
            return false;
        }

        initialized = true;
        return true;
    }

    private static Running? Current()
    {
        Running? run = Volatile.Read(ref current);
        return run != null && CurrentBridge(run) != null ? run : null;
    }

    /// <summary>Der Linux-Thread des aufrufenden Kernelthreads; ohne Allokation (Handler).</summary>
    private static Bridge? CurrentBridge(Running run)
    {
        SchedulerThread? me = FaultIsolation.CurrentSchedulerThread();
        if (me == null)
        {
            return null;
        }

        Bridge?[] threads = run.Threads;
        int used = Math.Min(Volatile.Read(ref run.ThreadsUsed), threads.Length);
        for (int i = 0; i < used; i++)
        {
            Bridge? b = Volatile.Read(ref threads[i]);
            if (b != null && ReferenceEquals(Volatile.Read(ref b.Sched), me))
            {
                return b;
            }
        }

        return null;
    }

    // ---- Handler: auf dem Kernelstack des Programmthreads, Interrupts an, keine Ausnahme darf hinaus ----

    [UnmanagedCallersOnly]
    private static void OnSyscall(UserFrame* frame)
    {
        Running? run = Volatile.Read(ref current);
        Bridge? me = run == null ? null : CurrentBridge(run);
        if (run == null || me == null)
        {
            frame->Rax = unchecked((ulong)-LinuxSyscalls.ENOSYS);
            return;
        }

        // Erster Aufruf eines Kindthreads: Register aus clone übernehmen, nichts verteilen (siehe Bridge.Pending).
        if (me.Pending)
        {
            me.Pending = false;
            *frame = me.Start;
            if (me.StartFpu != null)
            {
                me.StartFpu.CopyTo(new Span<byte>((byte*)frame - LinuxSignalFrame.FpuSize, LinuxSignalFrame.FpuSize));
                me.StartFpu = null;
            }

            return;
        }

        me.Frame = frame;
        try
        {
            if (me.FaultReportPending)
            {
                me.FaultReportPending = false;
                ReportFault(me.Process, me, handled: true);
            }

            // V18 rt_sigreturn: Register aus dem Signalrahmen statt eines Ergebnisses; danach können weitere Signale warten.
            if (frame->Rax == LinuxSignalFrame.RtSigreturn)
            {
                SignalReturn(me, frame);
                return;
            }

            // Dieselbe Registerzuordnung wie in den Hosttests (LinuxUserRegs.ToSyscallArgs); AfterSyscall liest daraus die Flags
            // für SIGPIPE und den Neustart. Kopie, weil Dispatch die Argumente ändern darf.
            SyscallArgs call = ToRegs(frame).ToSyscallArgs();
            var args = call;
            long takenBefore = me.Process.SignalsTaken;
            long result = LinuxSyscalls.Dispatch(ref args, me.Process);
            if (me.Resume)
            {
                ResumeParent(me, frame);
                call.Number = -1; // der Aufruf gehörte dem vfork-Kind; der Elternthread hat nichts neu zu starten
            }
            else
            {
                frame->Rax = unchecked((ulong)result);
            }

            DeliverSignals(me, frame, call, result, takenBefore);
        }
        catch (Exception exception)
        {
            // Ein Fehler der Brücke beendet das Programm, nicht den Kernel.
            run.Error = "Aufruf " + frame->Rax + ": " + exception.GetType().Name + ": " + exception.Message;
            UserMode.LeaveCurrent(Killed);
            LeaveAll(run, me, Killed);
        }
        finally
        {
            me.Frame = null;
        }
    }

    /// <summary>
    /// V18: Vor dem Rücksprung die wartenden Signale mit Handler anspringen (Rahmen auf dem Benutzerstapel, LinuxSignals.cs). Das
    /// fxsave-Abbild des Threads liegt direkt unter dem Rahmen (UserMode.s: <c>sub rsp, FPU_SIZE; fxsave64 [rsp]</c>, frame = rsp davor),
    /// fxrstor64 lädt es beim Rücksprung. Endet der Prozess dabei (SIGSEGV), verlässt der Thread den Ring 3 über OnExit.
    /// </summary>
    private static void DeliverSignals(Bridge me, UserFrame* frame, in SyscallArgs call, long result, long takenBefore)
    {
        LinuxUserProcess process = me.Process;
        if (!process.SignalWaiting && result != -LinuxSignalFrame.EPIPE && result != -LinuxSyscalls.EINTR)
        {
            return; // der häufige Fall ohne Kopien
        }

        LinuxUserRegs regs = ToRegs(frame);
        LinuxSignalFrame.AfterSyscall(process, ref regs, new Span<byte>((byte*)frame - LinuxSignalFrame.FpuSize, LinuxSignalFrame.FpuSize),
            call, result, takenBefore);
        FromRegs(frame, regs);
    }

    private static void SignalReturn(Bridge me, UserFrame* frame)
    {
        LinuxUserRegs regs = ToRegs(frame);
        var fpu = new Span<byte>((byte*)frame - LinuxSignalFrame.FpuSize, LinuxSignalFrame.FpuSize);
        if (!LinuxSignalFrame.Return(me.Process, ref regs, fpu))
        {
            me.Process.ForceSegv();
            return;
        }

        if (LinuxSignalFrame.NonCanonicalStack(regs))
        {
            me.Process.ForceBus();
            return;
        }

        FromRegs(frame, regs);
        DeliverSignals(me, frame, new SyscallArgs { Number = -1 }, 0, 0);
    }

    private static LinuxUserRegs ToRegs(UserFrame* f) => new()
    {
        R8 = f->R8, R9 = f->R9, R10 = f->R10, R11 = f->R11, R12 = f->R12, R13 = f->R13, R14 = f->R14, R15 = f->R15,
        Rdi = f->Rdi, Rsi = f->Rsi, Rbp = f->Rbp, Rbx = f->Rbx, Rdx = f->Rdx, Rax = f->Rax, Rcx = f->Rcx,
        Rsp = f->Rsp, Rip = f->Rip, Rflags = f->Rflags,
    };

    // CS und SS bleiben die des Kernels; UserMode.Prepare prüft RIP, RSP und RFLAGS vor dem Rücksprung (ungültig: der Thread endet).
    private static void FromRegs(UserFrame* f, in LinuxUserRegs r)
    {
        f->R8 = r.R8;
        f->R9 = r.R9;
        f->R10 = r.R10;
        f->R11 = r.R11;
        f->R12 = r.R12;
        f->R13 = r.R13;
        f->R14 = r.R14;
        f->R15 = r.R15;
        f->Rdi = r.Rdi;
        f->Rsi = r.Rsi;
        f->Rbp = r.Rbp;
        f->Rbx = r.Rbx;
        f->Rdx = r.Rdx;
        f->Rax = r.Rax;
        f->Rcx = r.Rcx;
        f->Rsp = r.Rsp;
        f->Rip = r.Rip;
        f->Rflags = r.Rflags;
    }

    /// <summary>
    /// V15: Ende der vfork-Phase (execve oder exit des Kinds). Der Thread läuft als Elternthread weiter: dessen Register aus clone, rax
    /// die PID des Kinds, seine FS-Basis. Das vfork-Objekt gibt nur seine Tabelle ab, Speicher und offene Dateien gehören dem Elternprozess.
    /// </summary>
    private static void ResumeParent(Bridge me, UserFrame* frame)
    {
        LinuxUserProcess child = me.Process;
        me.Resume = false;
        *frame = me.VforkFrame;
        frame->Rax = (ulong)child.Pid;
        me.Process = me.VforkParent!;
        me.Linux = me.VforkThread!;
        me.VforkParent = null;
        me.VforkThread = null;
        UserMode.SetFsBase(me.Linux.FsBase);
        child.Release();
    }

    /// <summary>Hooks eines Prozesses im Kernel (erster Prozess und jedes Abbild nach execve).</summary>
    private static void Wire(Running run, UserPageTable table, LinuxUserProcess process)
    {
        process.OnFsBase = UserMode.SetFsBase;
        process.CurrentThreadSource = () => CurrentBridge(run)?.Linux ?? process.MainThread;
        process.StartThread = child => StartChild(run, table, process, child);
        process.OnThreadExit = (_, code) => UserMode.LeaveCurrent((ulong)code);
        process.StartVfork = (child, stack) => StartVfork(run, child, stack);
        process.CreatePages = () =>
        {
            UserPageTable? created = UserPageTable.TryCreate();
            return created == null ? null : new CosmosUserPages(created);
        };
        process.StartExec = (next, image) => StartExec(run, next, image);
        process.ReplaceImage = (next, image) => ReplaceImage(run, next, image);
        process.StartFork = (child, stack) => StartFork(run, child, stack);
    }

    /// <summary>
    /// V21 fork: <paramref name="child"/> (Adressraum schon kopiert) auf eigenem Kernelthread mit eigener Seitentabelle. Er betritt den Ring 3
    /// wie ein Thread aus clone am <c>syscall</c> des Elternthreads und übernimmt beim ersten Aufruf dessen Register (rax 0, Stack aus clone)
    /// und FPU-Zustand (<see cref="Bridge.Pending"/>); der Befehl liegt im Kind an derselben Adresse, weil der Code mitkopiert ist.
    /// </summary>
    private static bool StartFork(Running run, LinuxUserProcess child, ulong stack)
    {
        Bridge? me = CurrentBridge(run);
        if (me == null || me.Frame == null || me.VforkParent != null || me.Frame->Rip < 2)
        {
            return false;
        }

        var table = ((CosmosUserPages)child.Pages).Table;
        Wire(run, table, child);
        child.OnExit = code =>
        {
            UserMode.LeaveCurrent((ulong)code);
            LeaveProcess(run, child, CurrentBridge(run), (ulong)code);
        };
        child.OnKilled = code => LeaveProcess(run, child, CurrentBridge(run), (ulong)code);
        var b = new Bridge
        {
            Linux = child.MainThread, Process = child, Start = *me.Frame, Pending = true, EntryRip = me.Frame->Rip - 2,
            StartFpu = new byte[LinuxSignalFrame.FpuSize],
        };
        b.Start.Rax = 0;
        if (stack != 0)
        {
            b.Start.Rsp = stack;
        }

        new ReadOnlySpan<byte>((byte*)me.Frame - LinuxSignalFrame.FpuSize, LinuxSignalFrame.FpuSize).CopyTo(b.StartFpu);
        if (!TakeSlot(run, b))
        {
            return false;
        }

        child.MainThread.Host = b;
        try
        {
            new Thread(() => ProcessBody(run, table, child, b, b.EntryRip, b.Start.Rsp, child.MainThread.FsBase)).Start();
            return true;
        }
        catch (Exception)
        {
            ReleaseSlot(run, b);
            return false;
        }
    }

    /// <summary>
    /// V19 execve ohne vfork: Der aufrufende Thread (der einzige des Prozesses) läuft ab dem Rücksprung als <paramref name="next"/> weiter, auf
    /// dessen Seitentabelle am Einsprung des Abbilds: Register null außer RIP, RSP und RFLAGS (IF), FS-Basis 0, FPU im Grundzustand. Ist der
    /// alte Prozess der erste des Laufs, wird <paramref name="next"/> es (Ende, Status, Abbruch des Laufs). Die alte Tabelle ist danach an
    /// keinen Thread mehr gebunden, ihr Prozess gibt sie frei.
    /// </summary>
    private static bool ReplaceImage(Running run, LinuxUserProcess next, UserImage image)
    {
        Bridge? me = CurrentBridge(run);
        if (me == null || me.Frame == null || me.VforkParent != null)
        {
            return false;
        }

        LinuxUserProcess old = me.Process;
        var table = ((CosmosUserPages)next.Pages).Table;
        Wire(run, table, next);
        if (ReferenceEquals(old, run.Process))
        {
            next.OnExit = code =>
            {
                UserMode.LeaveCurrent((ulong)code);
                LeaveAll(run, CurrentBridge(run), (ulong)code);
            };
            next.OnKilled = code => LeaveAll(run, CurrentBridge(run), (ulong)code);
            Volatile.Write(ref run.Process, next);
        }
        else
        {
            next.OnExit = code =>
            {
                UserMode.LeaveCurrent((ulong)code);
                LeaveProcess(run, next, CurrentBridge(run), (ulong)code);
            };
            next.OnKilled = code => LeaveProcess(run, next, CurrentBridge(run), (ulong)code);
        }

        // Erst die neue Bindung, dann der Wechsel der Brücke: Ein Seitenfehler danach geht an den Prozess, dessen Tabelle geladen ist.
        UserPageTable.UnbindCurrentThread();
        table.BindToCurrentThread();
        me.Process = next;
        me.Linux = next.MainThread;
        UserMode.SetFsBase(0);

        // Nur die Register des Programms; CS, SS und die Felder des Einsprungs bleiben (UserMode.Prepare prüft sie beim Rücksprung).
        UserFrame* f = me.Frame;
        FromRegs(f, new LinuxUserRegs { Rip = image.Entry, Rsp = image.StackPointer, Rflags = 0x202 });

        // fxsave-Abbild unter dem Frame (UserMode.s): FCW 0x37F, MXCSR 0x1F80 wie nach FNINIT/Linux fpu__clear; MXCSR_MASK bleibt.
        var fpu = new Span<byte>((byte*)f - LinuxSignalFrame.FpuSize, LinuxSignalFrame.FpuSize);
        fpu[..24].Clear();
        fpu[32..].Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fpu, 0x37F);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fpu[24..], 0x1F80);
        return true;
    }

    /// <summary>
    /// V15 vfork: Der aufrufende Thread läuft ab dem Rücksprung als <paramref name="child"/> weiter (rax 0, Stack aus clone). Der
    /// Elternthread ist damit blockiert, wie Linux es für CLONE_VFORK verlangt, ohne einen zweiten Kernelthread auf demselben Speicher.
    /// </summary>
    private static bool StartVfork(Running run, LinuxUserProcess child, ulong stack)
    {
        Bridge? me = CurrentBridge(run);
        if (me == null || me.Frame == null || me.VforkParent != null)
        {
            return false;
        }

        me.VforkFrame = *me.Frame;
        if (stack != 0)
        {
            me.Frame->Rsp = stack;
        }

        child.OnFsBase = UserMode.SetFsBase;
        child.CurrentThreadSource = () => CurrentBridge(run)?.Linux ?? child.MainThread;
        child.CreatePages = () =>
        {
            UserPageTable? created = UserPageTable.TryCreate();
            return created == null ? null : new CosmosUserPages(created);
        };
        child.StartExec = (next, image) => StartExec(run, next, image);

        // exit vor execve: kein Verlassen des Ring 3, der Thread kehrt nach dem Aufruf als Elternthread zurück.
        child.OnExit = _ =>
        {
            child.ReportEnd(child.WaitStatusOf(9));
            me.Resume = true;
        };
        me.VforkParent = me.Process;
        me.VforkThread = me.Linux;
        me.Process = child;
        me.Linux = child.MainThread;
        return true;
    }

    /// <summary>
    /// V15 execve im vfork-Kind: das neue Abbild auf eigenem Kernelthread mit eigener Seitentabelle; der aufrufende Thread kehrt nach dem
    /// Aufruf als Elternthread zurück (auch wenn kein Thread startet, dann endet das Kind mit SIGKILL).
    /// </summary>
    private static bool StartExec(Running run, LinuxUserProcess next, UserImage image)
    {
        Bridge? me = CurrentBridge(run);
        if (me == null || me.VforkParent == null)
        {
            return false;
        }

        me.Resume = true;
        var table = ((CosmosUserPages)next.Pages).Table;
        Wire(run, table, next);
        next.OnExit = code =>
        {
            UserMode.LeaveCurrent((ulong)code);
            LeaveProcess(run, next, CurrentBridge(run), (ulong)code);
        };
        next.OnKilled = code => LeaveProcess(run, next, CurrentBridge(run), (ulong)code); // kill aus einem anderen Prozess
        var b = new Bridge { Linux = next.MainThread, Process = next };
        if (!TakeSlot(run, b))
        {
            return false;
        }

        try
        {
            new Thread(() => ProcessBody(run, table, next, b, image.Entry, image.StackPointer, 0)).Start();
            return true;
        }
        catch (Exception)
        {
            ReleaseSlot(run, b);
            return false;
        }
    }

    /// <summary>
    /// Kernelthread eines Prozesses nach execve (am Einsprung des Abbilds) oder fork (V21: am <c>syscall</c> des Elternthreads, siehe
    /// <see cref="StartFork"/>): Ring 3, dann auf die eigenen Threads warten, Ende melden, aufräumen.
    /// </summary>
    private static void ProcessBody(Running run, UserPageTable table, LinuxUserProcess process, Bridge b, ulong entry, ulong stack, ulong fsBase)
    {
        bool bound = false;
        try
        {
            Volatile.Write(ref b.Sched, FaultIsolation.CurrentSchedulerThread());
            BindCpuTime(b);
            table.BindToCurrentThread();
            bound = true;
            UserMode.SetFsBase(fsBase);
            Interlocked.MemoryBarrier();

            // kill vor dem Start (etwa während vfork angekommen): ExitCode steht schon, der Ring 3 bleibt zu.
            if (!run.Cancelled && process.ExitCode == null)
            {
                UserMode.Enter(entry, stack);
                if (b.Fault)
                {
                    ReportFault(b.Process, b);
                    b.Process.RecordFault(b.FaultSignal);
                }
            }
        }
        catch (Exception exception)
        {
            run.Error ??= exception.GetType().Name + ": " + exception.Message;
        }
        finally
        {
            process = b.Process; // V19: execve ohne vfork ersetzt den Prozess auf diesem Thread

            // Wie WaitForThreads, nur für diesen Prozess: erst wenn kein eigener Thread mehr läuft, darf der Adressraum weg.
            while (HasOtherThreads(run, process, b))
            {
                LeaveProcess(run, process, b, Killed);
                Thread.Sleep(SleepSlice);
            }

            process.ThreadGone(b.Linux);
            if (bound)
            {
                UserPageTable.UnbindCurrentThread();
            }

            process.ReportEnd(process.WaitStatusOf(9));
            process.Destroy();
            ReleaseSlot(run, b);
        }
    }

    private static bool HasOtherThreads(Running run, LinuxUserProcess process, Bridge self)
    {
        for (int i = 0; i < run.Threads.Length; i++)
        {
            Bridge? other = Volatile.Read(ref run.Threads[i]);
            if (other != null && other != self && ReferenceEquals(other.Process, process))
            {
                return true;
            }
        }

        return false;
    }

    [UnmanagedCallersOnly]
    private static int OnFault(UserFrame* frame, ulong vector, ulong error, ulong address)
    {
        Running? run = Volatile.Read(ref current);
        Bridge? me = run == null ? null : CurrentBridge(run);
        if (run == null || me == null)
        {
            return 0;
        }

        bool busError = false; // V30-P3: Dateiseite nicht lesbar, Linux schickt SIGBUS statt SIGSEGV
        try
        {
            // Im vfork-Kind leitet der Prozess an den Speicher des Elternprozesses weiter (V15).
            if (vector == PageFault && me.Process.HandlePageFault(address, error, out busError))
            {
                return 1;
            }
        }
        catch (Exception exception)
        {
            run.Error = exception.GetType().Name + ": " + exception.Message;
        }

        // Abbruch (Zeitlimit, Strg+C) während eines Fehlers: kein Handler mehr und keine Ausnahme melden, der Lauf endet als Abbruch (sonst
        // meldete Describe eine Ausnahme, etwa bei einem Handler, der den Befehl endlos wiederholt).
        if (run.Cancelled)
        {
            return 0;
        }

        // V24: Hat der Prozess einen Handler für das Signal, läuft er statt des Prozessendes (Linux force_sig_fault). Nicht im vfork-Kind:
        // Es läuft auf dem Kernelthread und im Speicher des Elternprozesses, ein Handler mit siglongjmp darin bliebe ungeprüft.
        ulong faultRip = frame->Rip; // DeliverFault setzt den Rahmen auf den Handler
        try
        {
            if (!me.Process.InVfork && DeliverFault(me, frame, vector, error, address, busError))
            {
                // Nur merken (kein Protokoll im Ausnahmehandler); Fault bleibt false, der Thread läuft im Handler weiter.
                me.FaultVector = vector;
                me.FaultSignal = busError ? LinuxFault.SigBus : SignalOf(vector);
                me.FaultError = error;
                me.FaultAddress = address;
                me.FaultRip = faultRip;
                me.FaultReportPending = true;
                return 1;
            }
        }
        catch (Exception exception)
        {
            run.Error ??= exception.GetType().Name + ": " + exception.Message;
        }

        // V15: Eine Ausnahme in einem Kindprozess (nach execve) beendet nur ihn; sein Thread meldet sie nach dem Verlassen.
        // Im vfork-Kind (vor execve) bricht sie den ganzen Lauf ab: Der Thread trägt die Register des blockierten Elternthreads.
        int signal = busError ? LinuxFault.SigBus : SignalOf(vector);
        me.Fault = true;
        me.FaultVector = vector;
        me.FaultSignal = signal;
        me.FaultError = error;
        me.FaultAddress = address;
        me.FaultRip = frame->Rip;
        if (!ReferenceEquals(me.Process, run.Process) && !me.Process.InVfork)
        {
            return 0;
        }

        run.Fault = true;
        run.FaultVector = vector;
        run.FaultSignal = signal;
        run.FaultError = error;
        run.FaultAddress = address;
        run.FaultRip = frame->Rip;
        return 0;
    }

    /// <summary>
    /// V24: Signalrahmen für eine Ausnahme. Das fxsave-Abbild liegt wie beim Aufruf direkt unter dem Frame: Der Interrupt-Stub von Cosmos
    /// sichert es dort (ThreadContext.Fpu vor R15, Interrupts.s fxsave64/fxrstor64), der Frame ist der IRQContext ab R15. Was hier hineinkommt,
    /// lädt der Rücksprung des Stubs, ebenso die geänderten Register (UserModeX64.PrepareReturn prüft sie vorher).
    /// </summary>
    private static bool DeliverFault(Bridge me, UserFrame* frame, ulong vector, ulong error, ulong address, bool busError)
    {
        LinuxUserProcess process = me.Process;
        var fpu = new Span<byte>((byte*)frame - LinuxSignalFrame.FpuSize, LinuxSignalFrame.FpuSize);
        bool mapped = vector == PageFault && process.HasMapping(address);
        LinuxFault fault = LinuxFault.Of(vector, error, address, frame->Rip, mapped, fpu, busError);
        LinuxUserRegs regs = ToRegs(frame);
        if (!LinuxSignalFrame.DeliverFault(process, ref regs, fpu, fault))
        {
            return false;
        }

        FromRegs(frame, regs);
        return true;
    }

    /// <summary>
    /// V38: Eine Ausnahme ohne Handler beendet einen Kindprozess sonst still (nur der Wartestatus des Elternprozesses trägt das Signal).
    /// Eine Zeile ins serielle Protokoll mit Prozess, Thread, Vektor, Fehlercode, Adresse und RIP samt Ort (Bibliothek+Versatz). Läuft im
    /// Threadkontext nach dem Verlassen des Ring 3 (OnFault darf weder allokieren noch protokollieren); der Adressraum lebt noch, denn er
    /// wird erst nach dem letzten Thread des Prozesses freigegeben. Der Hauptprozess des Laufs meldet seine Ausnahme schon über Describe.
    /// </summary>
    private static void ReportFault(LinuxUserProcess process, Bridge b, bool handled = false)
    {
        try
        {
            Serial((handled ? "LINUXRUN FAULT HANDLER pid " : "LINUXRUN FAULT pid ") + process.Pid + " tid " + b.Linux.Tid + " " + process.ExePath + " signal " + b.FaultSignal
                + " Ausnahme " + b.FaultVector + " Fehlercode 0x" + b.FaultError.ToString("x") + " Adresse 0x" + b.FaultAddress.ToString("x")
                + " (" + process.DescribeAddress(b.FaultAddress) + ") RIP 0x" + b.FaultRip.ToString("x") + " (" + process.DescribeAddress(b.FaultRip) + ")");
        }
        catch (Exception)
        {
            // Nur Diagnose: Ein Fehler beim Beschreiben darf das Prozessende nicht verhindern.
        }
    }

    private static readonly CosmosLinuxHost Host = new();

    /// <summary>Zeit, Zufall, Schlafen und Speicher für die Aufrufe aus V2b (clock_gettime, getrandom, poll mit Frist) und V13 (sysinfo).</summary>
    private sealed class CosmosLinuxHost : ILinuxHost, ILinuxSystemInfo
    {
        /// <summary>sysinfo (V13): dieselbe Quelle wie <c>CosmosSystemInfo.MemoryText</c> und die Speicherzeilen der Proben.</summary>
        public bool TryGetMemory(out ulong totalBytes, out ulong freeBytes)
        {
            var memory = CosmosSystemInfo.PhysicalMemory();
            totalBytes = memory.Total;
            freeBytes = memory.Free;
            return true;
        }

        public long RealtimeNanos => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;

        public long MonotonicNanos
        {
            get
            {
                // Ohne 128-Bit-Arithmetik: ganze Sekunden und Rest getrennt (der Rest mal 10^9 bleibt bei Frequenzen unter 9 GHz in long).
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
                long frequency = System.Diagnostics.Stopwatch.Frequency;
                return (ticks / frequency * 1_000_000_000) + (ticks % frequency * 1_000_000_000 / frequency);
            }
        }

        public bool TryFillRandom(Span<byte> dst)
        {
            try
            {
                byte[] bytes = new byte[dst.Length];
                CosmosSaltSource.Fill(bytes);
                bytes.CopyTo(dst);
                return true;
            }
            catch (HardwareRandomUnavailableException)
            {
                return false;
            }
        }

        /// <summary>In Scheiben von <see cref="SleepSlice"/>: ein Abbruch (Zeitlimit) beendet den Schlaf (V3-Review P2).</summary>
        public void SleepNanos(long ns)
        {
            long ms = Math.Max(1, ns / 1_000_000);
            while (ms > 0)
            {
                Running? run = Current();
                if (run == null || run.Cancelled || CurrentBridge(run)?.Process.IsAborted == true)
                {
                    return;
                }

                int step = (int)Math.Min(ms, SleepSlice);
                Thread.Sleep(step);
                ms -= step;
            }
        }
    }

    /// <summary>Seiten über eine <see cref="UserPageTable"/>; Frames liest und schreibt der Kernel über das HHDM.</summary>
    private sealed class CosmosUserPages : IUserPages, IUserPagesQuery
    {
        private readonly UserPageTable _table;

        public CosmosUserPages(UserPageTable table) => _table = table;

        public UserPageTable Table => _table;

        public ulong AllocateFrame() => UserPageTable.AllocateFrame();

        public void FreeFrame(ulong frame) => UserPageTable.FreeFrame(frame);

        public Span<byte> Frame(ulong frame) => new((void*)(frame + hhdm), (int)AddressSpaceMap.PageSize);

        public bool Map(ulong page, ulong frame, MapAccess access) =>
            access != MapAccess.None && _table.TryMap(page, frame, access == MapAccess.ReadWrite, access == MapAccess.ReadExecute);

        public bool Unmap(ulong page) => _table.Unmap(page) != 0 || !_table.TryTranslate(page, false, out _);

        public bool MapsTo(ulong page, ulong frame) =>
            _table.TryTranslate(page, false, out ulong physical) && (physical & ~(AddressSpaceMap.PageSize - 1)) == frame;

        public void DestroyTable() => _table.Dispose();
    }
}
#endif
