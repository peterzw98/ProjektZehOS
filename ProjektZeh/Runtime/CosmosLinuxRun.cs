#if ZEHOS_LINUX_RUN
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.Core.Scheduler;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
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

    /// <summary>Größte ladbare Datei; js aus Ladybird liegt deutlich darunter.</summary>
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

    /// <summary>Umgebung wie <c>experiments/LinuxAbi/run-linux.py</c>, damit die Ausgaben mit <c>expected/</c> vergleichbar sind.</summary>
    public static readonly string[] Environment = ["ZEHOS_ABI=1", "LANG=C", "PATH=/usr/bin:/bin"];

    private static int busy;
    private static bool initialized;
    private static ulong hhdm;

    // Das laufende Programm; die Handler finden es über den Thread, auf dem sie laufen.
    private static Running? current;

    private sealed class Running
    {
        public readonly TerminalDescriptor Terminal;

        /// <summary>Gesetzt bei einem Vordergrundprogramm der Shell; dann meldet der Programmthread das Ende selbst.</summary>
        public LinuxTerminalSession? Session;
        public LinuxUserProcess? Process;
        public SchedulerThread? Thread;
        public ulong Result = UserMode.EnterRefused;
        public bool Started;
        public string? Error;
        public ulong FaultVector, FaultError, FaultAddress, FaultRip;
        public bool Fault;

        /// <summary>Gesetzt beim Zeitlimit: Schlafen im Kernel endet, der Aufruf kehrt zurück, die Rückkehr verlässt den Ring 3.</summary>
        public volatile bool Cancelled;

        /// <summary>
        /// Wer zuletzt fertig ist (<see cref="Run"/> oder der Programmthread), gibt <see cref="busy"/> frei. Endet das Programm auch
        /// nach <see cref="LeaveGraceMs"/> nicht, kehrt die Shell zurück, und ein neues run geht erst nach seinem Ende.
        /// </summary>
        public int Owners = 2;

        public Running(TerminalDescriptor terminal) => Terminal = terminal;
    }

    /// <summary>Ergebnis eines Laufs: Status wie bei run-linux.py (<c>exit n</c>, <c>signal n</c>) oder ein Fehlertext.</summary>
    internal readonly record struct Outcome(bool Ran, string Status, string Detail);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "VirtualToPhysical")]
    private static extern ulong ToPhysical([UnsafeAccessorType("Cosmos.Kernel.Core.Memory.PageAllocator, Cosmos.Kernel.Core")] object? owner, ulong address);

    /// <summary>
    /// Shell: <c>run &lt;datei&gt; [argumente...]</c>. argv[0] ist der Pfad wie angegeben. Mit <paramref name="shell"/> läuft das
    /// Programm neben der Ereignisschleife (V3 stdin): Der Befehl kehrt sofort zurück, die Shell holt die Ausgabe in
    /// <see cref="ShellService.Poll"/>, Enter-Zeilen gehen an fd 0, Strg+C oder Esc bricht ab; ohne Zeitlimit.
    /// </summary>
    public static ResultErr RunFile(FileService files, string[] arguments, ITextOutput output, ShellService? shell)
    {
        if (arguments.Length == 0)
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: run <datei> [argumente...]");
        }

        ResultObject<byte[]> file = files.ReadBytes(arguments[0], MaxFileSize);
        if (!file.IsOK())
        {
            return file.Status;
        }

        if (shell != null)
        {
            return StartForeground(file.Value!, arguments, shell);
        }

        // Spiegel ins serielle Protokoll für scripts/test-linux-abi.py (V5): rohe Bytes als Hex, Status wie run-linux.py.
        Serial("LINUXRUN BEGIN " + arguments[0]);
        Outcome outcome = Run(file.Value!, arguments, Environment, output, 120, data => Serial("LINUXRUN OUT " + Convert.ToHexString(data)));
        Serial("LINUXRUN END " + (outcome.Ran ? outcome.Status : "notrun " + outcome.Detail));
        if (!outcome.Ran)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "run: " + outcome.Detail);
        }

        output.WriteLine("[" + outcome.Status + (outcome.Detail.Length > 0 ? ", " + outcome.Detail : string.Empty) + "]");
        return ResultErr.CreateSuccess();
    }

    /// <summary>
    /// Startet das Programm als Vordergrundprogramm der Shell und kehrt sofort zurück. Das Ende meldet der Programmthread selbst
    /// (Statuszeile in der Sitzung, LINUXRUN END im seriellen Protokoll).
    /// </summary>
    private static ResultErr StartForeground(byte[] elf, string[] arguments, ShellService shell)
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
            if (!Prepare(elf, arguments, Environment, out UserImage? image, out string why))
            {
                return ResultErr.CreateFailure(ResultCode.Failure, "run: " + why);
            }

            var session = new LinuxTerminalSession(Thread.Sleep);
            string name = arguments[0];
            run = new Running(new TerminalDescriptor(session) { Mirror = data => Serial("LINUXRUN OUT " + Convert.ToHexString(data)) })
            {
                Session = session,

                // Nur der Programmthread gibt busy frei; die Shell wartet nicht auf ihn.
                Owners = 1,
            };
            Running target = run;
            session.OnInterrupt = () => Abort(target, Interrupted);
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
    private static bool Prepare(byte[] elf, string[] argv, string[] envp, out UserImage? image, out string why)
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

    private static void Serial(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString(text + "\n");

    /// <summary>Lädt und startet das Programm und wartet auf sein Ende, höchstens <paramref name="timeoutSeconds"/> Sekunden.</summary>
    public static Outcome Run(byte[] elf, string[] argv, string[] envp, ITextOutput output, int timeoutSeconds, ByteSink? mirror = null)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return new Outcome(false, string.Empty, "es läuft schon ein Programm");
        }

        Running? run = null;
        try
        {
            if (!Prepare(elf, argv, envp, out UserImage? image, out string why))
            {
                return new Outcome(false, string.Empty, why);
            }

            var started = new Running(new TerminalDescriptor(output) { Mirror = mirror });
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

        if (run.Process?.ExitCode is int code)
        {
            return new Outcome(true, "exit " + code, string.Empty);
        }

        if (run.Fault)
        {
            return new Outcome(true, "signal " + SignalOf(run.FaultVector), "Ausnahme " + run.FaultVector + " Fehlercode 0x" + run.FaultError.ToString("X")
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

    /// <summary>Signal, das Linux für die Ausnahme schickte (Status wie bei run-linux.py).</summary>
    private static int SignalOf(ulong vector) => vector switch
    {
        0 or 16 or 19 => 8,   // #DE, #MF, #XM: SIGFPE
        6 => 4,               // #UD: SIGILL
        3 or 1 => 5,          // #BP, #DB: SIGTRAP
        17 => 7,              // #AC: SIGBUS
        _ => 11,              // #GP, #PF und der Rest: SIGSEGV
    };

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
        UserPageTable? table = UserPageTable.TryCreate();
        if (table == null)
        {
            run.Error = "keine Seitentabelle (kein Speicher)";
            return;
        }

        var process = new LinuxUserProcess(new CosmosUserPages(table), run.Terminal, Pid, MaxBytes, Host)
        {
            OnExit = code => UserMode.LeaveCurrent((ulong)code),
            OnFsBase = UserMode.SetFsBase,
        };
        run.Process = process;
        bool bound = false;
        try
        {
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
            Volatile.Write(ref current, null);
            if (bound)
            {
                UserPageTable.UnbindCurrentThread();
            }

            process.Destroy();
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
        return run != null && ReferenceEquals(run.Thread, FaultIsolation.CurrentSchedulerThread()) ? run : null;
    }

    // ---- Handler: auf dem Kernelstack des Programmthreads, Interrupts an, keine Ausnahme darf hinaus ----

    [UnmanagedCallersOnly]
    private static void OnSyscall(UserFrame* frame)
    {
        Running? run = Current();
        if (run?.Process == null)
        {
            frame->Rax = unchecked((ulong)-LinuxSyscalls.ENOSYS);
            return;
        }

        try
        {
            var args = new SyscallArgs
            {
                Number = unchecked((long)frame->Rax),
                A0 = frame->Rdi,
                A1 = frame->Rsi,
                A2 = frame->Rdx,
                A3 = frame->R10,
                A4 = frame->R8,
                A5 = frame->R9,
            };
            frame->Rax = unchecked((ulong)LinuxSyscalls.Dispatch(ref args, run.Process));
        }
        catch (Exception exception)
        {
            // Ein Fehler der Brücke beendet das Programm, nicht den Kernel.
            run.Error = "Aufruf " + frame->Rax + ": " + exception.GetType().Name + ": " + exception.Message;
            UserMode.LeaveCurrent(Killed);
        }
    }

    [UnmanagedCallersOnly]
    private static int OnFault(UserFrame* frame, ulong vector, ulong error, ulong address)
    {
        Running? run = Current();
        if (run?.Process == null)
        {
            return 0;
        }

        try
        {
            if (vector == PageFault && run.Process.HandlePageFault(address, error))
            {
                return 1;
            }
        }
        catch (Exception exception)
        {
            run.Error = exception.GetType().Name + ": " + exception.Message;
        }

        run.Fault = true;
        run.FaultVector = vector;
        run.FaultError = error;
        run.FaultAddress = address;
        run.FaultRip = frame->Rip;
        return 0;
    }

    private static readonly CosmosLinuxHost Host = new();

    /// <summary>Zeit, Zufall und Schlafen für die Aufrufe aus V2b (clock_gettime, getrandom, poll mit Frist).</summary>
    private sealed class CosmosLinuxHost : ILinuxHost
    {
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
                if (run == null || run.Cancelled)
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
    private sealed class CosmosUserPages : IUserPages
    {
        private readonly UserPageTable _table;

        public CosmosUserPages(UserPageTable table) => _table = table;

        public ulong AllocateFrame() => UserPageTable.AllocateFrame();

        public void FreeFrame(ulong frame) => UserPageTable.FreeFrame(frame);

        public Span<byte> Frame(ulong frame) => new((void*)(frame + hhdm), (int)AddressSpaceMap.PageSize);

        public bool Map(ulong page, ulong frame, MapAccess access) =>
            access != MapAccess.None && _table.TryMap(page, frame, access == MapAccess.ReadWrite, access == MapAccess.ReadExecute);

        public bool Unmap(ulong page) => _table.Unmap(page) != 0 || !_table.TryTranslate(page, false, out _);

        public void DestroyTable() => _table.Dispose();
    }
}
#endif
