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
        public LinuxUserProcess? Process;
        public SchedulerThread? Thread;
        public ulong Result = UserMode.EnterRefused;
        public bool Started;
        public string? Error;
        public ulong FaultVector, FaultError, FaultAddress, FaultRip;
        public bool Fault;

        public Running(TerminalDescriptor terminal) => Terminal = terminal;
    }

    /// <summary>Ergebnis eines Laufs: Status wie bei run-linux.py (<c>exit n</c>, <c>signal n</c>) oder ein Fehlertext.</summary>
    internal readonly record struct Outcome(bool Ran, string Status, string Detail);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "VirtualToPhysical")]
    private static extern ulong ToPhysical([UnsafeAccessorType("Cosmos.Kernel.Core.Memory.PageAllocator, Cosmos.Kernel.Core")] object? owner, ulong address);

    /// <summary>Shell: <c>run &lt;datei&gt; [argumente...]</c>. argv[0] ist der Pfad wie angegeben.</summary>
    public static ResultErr RunFile(FileService files, string[] arguments, ITextOutput output)
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

    private static void Serial(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString(text + "\n");

    /// <summary>Lädt und startet das Programm und wartet auf sein Ende, höchstens <paramref name="timeoutSeconds"/> Sekunden.</summary>
    public static Outcome Run(byte[] elf, string[] argv, string[] envp, ITextOutput output, int timeoutSeconds, ByteSink? mirror = null)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return new Outcome(false, string.Empty, "es läuft schon ein Programm");
        }

        try
        {
            if (!Initialize(out string why))
            {
                return new Outcome(false, string.Empty, why);
            }

            UserImage image;
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
            }
            catch (ElfLoadException exception)
            {
                return new Outcome(false, string.Empty, exception.Message);
            }
            catch (HardwareRandomUnavailableException exception)
            {
                return new Outcome(false, string.Empty, "kein Hardwarezufall für AT_RANDOM: " + exception.Message);
            }

            var run = new Running(new TerminalDescriptor(output) { Mirror = mirror });
            var thread = new Thread(() => Body(run, image, elf));
            thread.Start();
            bool timedOut = false;
            if (!thread.Join(timeoutSeconds * 1000))
            {
                timedOut = true;
                SchedulerThread? target = Volatile.Read(ref run.Thread);
                if (target != null)
                {
                    UserMode.RequestLeave(target, Killed);
                }

                thread.Join();
            }

            run.Terminal.Flush();
            return Describe(run, timedOut);
        }
        finally
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

        public void SleepNanos(long ns) => Thread.Sleep((int)Math.Clamp(ns / 1_000_000, 1, int.MaxValue));
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
