#if ZEHOS_USER_MODE_PROBE
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Threading;
using CpuSet = Cosmos.Kernel.System.Threading.CpuSet;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // UserMode und die Scheduler-Typen gelten als Vorschau.

/// <summary>
/// Gastprobe Ladybird N1-A6 (<c>-p:ZehOSUserModeProbe=true</c>, nur Probe): die Faelle von <c>Native/UserProbe/probe.S</c> im
/// Ring 3. Auswertung mit <c>scripts/test-user-mode-probe.py</c>. <c>-p:ZehOSUserModeProbeNegative=true</c> loest statt der
/// Faelle einen #PF im Kernelthread aus; das System muss anhalten. Ergebniszeilen schreibt nur der Hauptthread.
/// </summary>
internal static unsafe partial class CosmosUserModeProbe
{
    private const int PageSize = 4096;
    private const int FirstDataPage = 64;
    private const int DataPages = 8;
    private const int StackOffset = 2048;
    private const int FpuAreaSize = 512; // ThreadContext.FpuAreaSize; danach folgt der Rahmen wie UserFrame
    private const byte NmiVector = 2;
    private const ulong Present = 1, Writable = 2, User = 4, NoExecute = 1UL << 63;
    private const ulong AddressMask = 0x000F_FFFF_FFFF_F000;
    private const ulong UserEnd = 0x0000_8000_0000_0000;
    private const ulong UserCs = 0x4B;

    internal static ulong userBase; // N1-B4 kernelread: Adresse, die nur die Kerneltabellen der unteren Haelfte abbilden
    internal static readonly ulong[] data = new ulong[DataPages];
    private static int failed;

    private static long calls;
    private static ulong exitAux;
    private static int badReturn;
    private static int faults;
    private static ulong faultVector, faultError, faultAddress;
    private static int checkFailures;
    private static long checks;
    private static readonly SchedulerThread?[] switchThreads = new SchedulerThread?[DataPages];
    private static int nmis, nmisInRing3;

    private delegate void NmiHandler(ref UserFrame frame);

    [LibraryImport("*", EntryPoint = "zehos_user_probe_address"), SuppressGCTransition] internal static partial ulong ImageAddress();
    [LibraryImport("*", EntryPoint = "zehos_user_probe_size"), SuppressGCTransition] internal static partial ulong ImageSize();
    [LibraryImport("*", EntryPoint = "zehos_user_probe_cr3"), SuppressGCTransition] internal static partial ulong ReadCr3();
    [LibraryImport("*", EntryPoint = "zehos_user_probe_cr4"), SuppressGCTransition] private static partial ulong ReadCr4();
    [LibraryImport("*", EntryPoint = "zehos_user_probe_mask"), SuppressGCTransition] private static partial ulong Mask();
    [LibraryImport("*", EntryPoint = "zehos_user_probe_restore"), SuppressGCTransition] private static partial void Restore(ulong flags);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "VirtualToPhysical")]
    private static extern ulong ToPhysical([UnsafeAccessorType("Cosmos.Kernel.Core.Memory.PageAllocator, Cosmos.Kernel.Core")] object? owner, ulong address);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SendNmi")]
    private static extern void SendNmi([UnsafeAccessorType("Cosmos.Kernel.Core.X64.Cpu.LocalApic, Cosmos.Kernel.Core.X64")] object? owner, ulong apicId);

    private const string IrqDelegate = "Cosmos.Kernel.Core.CPU.InterruptManager+IrqDelegate, Cosmos.Kernel.Core";

    // IRQContext ist intern und hat dieselbe Anordnung wie UserFrame. SetHandler prueft den Delegattyp, der Aufruf haengt nur
    // von den Feldern des Delegaten ab: Der eigene Delegat bekommt deshalb den Typ von Cosmos (gleich gross, nur diese Probe).
    private static object CosmosHandler(NmiHandler source)
    {
        object target = source;
        ref nint type = ref Unsafe.Subtract(ref Unsafe.As<byte, nint>(ref Unsafe.As<Raw>(target).First), 1);
        type = Type.GetType(IrqDelegate, throwOnError: true)!.TypeHandle.Value;
        return target;
    }

    private sealed class Raw { public byte First; }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetHandler")]
    private static extern void SetHandler([UnsafeAccessorType("Cosmos.Kernel.Core.CPU.InterruptManager, Cosmos.Kernel.Core")] object? owner, byte vector,
        [UnsafeAccessorType(IrqDelegate)] object? handler);

    /// <summary>Immer false: nach der Probe haelt der Kernel an.</summary>
    internal static bool Run()
    {
        try
        {
            if (!SetUp()) { Report("setup", false, "pages or UserMode.Initialize"); return Done(); }
#if ZEHOS_USER_MODE_PROBE_NEGATIVE
            bool ran = UserMode.Enter(Entry("leave"), Stack(0)) == 0;
            Write("gegenprobe pf in kernel thread ring3=" + (ran ? 1 : 0));
            sink = *(int*)0;
            Write("gegenprobe NOT HALTED");
            return Done();
#else
            // Der wichtigste Fall zuerst: Er wertet jeden Rueckweg ueber einen vorgemerkten Wechsel.
            Case("switch", Switch);
            Case("loop", Loop);
            Case("spin", Spin);
            Case("fpu", Fpu);
            Case("gc", Collect);
            Case("fault_de", () => Fault("fault_de", 0, 0, 0));
            Case("fault_ud", () => Fault("fault_ud", 6, 0, 0));
            Case("fault_gp_cli", () => Fault("fault_gp_cli", 13, 0, 0));
            Case("fault_gp_hlt", () => Fault("fault_gp_hlt", 13, 0, 0));
            Case("fault_gp_in", () => Fault("fault_gp_in", 13, 0, 0));
            Case("fault_pf_read", () => Fault("fault_pf_read", 14, 4, 0xFFFF_8000_0000_0000));
            Case("fault_pf_write", () => Fault("fault_pf_write", 14, 6, 0xFFFF_8000_0000_0000));
            Case("fault_pf_exec", () => Fault("fault_pf_exec", 14, 4, 0xFFFF_8000_0000_0000));
            Case("fault_avx", Avx);
            Case("badret", BadReturn);
            Case("killspin", KillSpin);
            Case("nmi", Nmi);
            Case("leave", Leave);
            // N1-B4: Faelle mit eigenen Seitentabellen, danach muss das Programm der Kerneltabellen weiter laufen (Case).
            CosmosAddressSpaceProbe.Run(&OnSyscall, &OnFault);
            return Done();
#endif
        }
        catch (Exception exception)
        {
            Report("exception", false, exception.GetType().Name + ": " + exception.Message);
            return Done();
        }
    }

#if ZEHOS_USER_MODE_PROBE_NEGATIVE
    private static volatile int sink;
#endif

    internal static void Write(string text) => Log.WriteString("USER_PROBE " + text + "\n");

    internal static void Report(string name, bool ok, string text)
    {
        if (!ok) failed++;
        Write(name + " ok=" + (ok ? "1 " : "0 ") + text);
    }

    private static bool Done()
    {
        Write("done failed=" + failed);
        return false;
    }

    internal static void Case(string name, Func<(bool Ok, string Text)> body)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var (ok, text) = body();
            // Jeder Fall beendet nur den Probelauf: danach muss ein Programm wie vorher laufen.
            bool after = UserMode.Enter(Entry("leave"), Stack(0)) == 0;
            Report(name, ok && after, text + " after=" + (after ? 1 : 0) + " ms=" + clock.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            Report(name, false, exception.GetType().Name + ": " + exception.Message);
        }
    }

    // ---- Seiten ----

    internal static ulong hhdm;

    // Der Heap liegt im HHDM und ist dort physisch zusammenhaengend; die Seiten bleiben bis zum Ende der Probe belegt.
    private static ulong NewPages(int count)
    {
        ulong raw = (ulong)NativeMemory.AllocZeroed((nuint)((count + 1) * PageSize));
        return (raw + PageSize - 1) & ~(ulong)(PageSize - 1);
    }

    private static ulong Physical(ulong page) => ToPhysical(null, page);

    private static ulong* Table(ulong entry) => (ulong*)((entry & AddressMask) + hhdm);

    private static bool SetUp()
    {
        ulong code = NewPages(FirstDataPage);
        hhdm = code - Physical(code);
        ulong* pml4 = Table(ReadCr3());
        ulong index = 1;
        while (index < 256 && pml4[index] != 0) index++;
        ulong size = ImageSize();
        if (index == 256 || size > FirstDataPage * PageSize || *(uint*)ImageAddress() != 0x3150_555A) return false;
        userBase = index << 39;
        Buffer.MemoryCopy((void*)ImageAddress(), (void*)code, FirstDataPage * PageSize, size);
        ulong tables = NewPages(3);
        ulong* pdpt = (ulong*)tables, pd = pdpt + 512, pt = pd + 512;
        for (int i = 0; i < FirstDataPage; i++) pt[i] = Physical(code) + (ulong)(i * PageSize) | Present | User;
        for (int i = 0; i < DataPages; i++)
        {
            data[i] = NewPages(1);
            pt[FirstDataPage + i] = Physical(data[i]) | Present | Writable | User | NoExecute;
        }
        pd[0] = Physical((ulong)pt) | Present | Writable | User;
        pdpt[0] = Physical((ulong)pd) | Present | Writable | User;
        Volatile.Write(ref pml4[index], Physical((ulong)pdpt) | Present | Writable | User);
        UserMode.SyscallHandler = &OnSyscall;
        UserMode.FaultHandler = &OnFault;
        bool ready = UserMode.Initialize();
        Write("info cpus=" + KernelCpus.Count + " base=0x" + userBase.ToString("X") + " cr4=0x" + ReadCr4().ToString("X")
            + " avx=" + (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx >> 28 & 1) + " tsc_hz=" + Stopwatch.Frequency);
        return ready;
    }

    private static ulong Entry(string name) => userBase + Offset(name);

    /// <summary>Versatz eines Einstiegs im Abbild, aus der Kopftabelle (N1-B4 bildet das Abbild an eine eigene Basis ab).</summary>
    internal static uint Offset(string name)
    {
        byte* image = (byte*)ImageAddress();
        uint count = *(uint*)(image + 4), size = *(uint*)(image + 8);
        for (uint i = 0; i < count; i++)
        {
            byte* entry = image + 16 + i * size;
            int length = 0;
            while (length < name.Length && entry[length] == name[length]) length++;
            if (length == name.Length && entry[length] == 0) return *(uint*)(entry + 24);
        }
        throw new InvalidOperationException("no entry " + name);
    }

    private static ulong DataUser(int page) => userBase + (ulong)(FirstDataPage + page) * PageSize;
    internal static ulong Stack(int page) => DataUser(page) + StackOffset;
    private static ref ulong Counter(int page) => ref *(ulong*)(data[page] + StackOffset + 8);

    // ---- Handler (auf dem Kernelstack des Threads, Interrupts an) ----

    [UnmanagedCallersOnly]
    private static void OnSyscall(UserFrame* frame)
    {
        switch (frame->Rax)
        {
            case 0x7A01:
                calls++;
                frame->Rax = 0;
                if (badReturn == 1) frame->Rip = UserEnd;
                else if (badReturn == 2) frame->Rip = data[0];
                break;
            case 0x7A02:
                exitAux = frame->Rsi;
                UserMode.LeaveCurrent(frame->Rdi);
                break;
            case 0x7A03:
                ulong result = Check(frame);
                if (result != 0) Interlocked.Increment(ref checkFailures);
                Interlocked.Increment(ref checks);
                frame->Rax = result;
                break;
            default:
                frame->Rax = ulong.MaxValue;
                break;
        }
    }

    private static ulong Check(UserFrame* frame)
    {
        ulong id = frame->Rdi;
        if (id == 0 || id > DataPages) return 1;
        SchedulerThread? thread = FaultIsolation.CurrentSchedulerThread();
        if (thread == null || !ReferenceEquals(thread, Volatile.Read(ref switchThreads[id - 1]))) return 2;
        if (!OnOwnStack(thread, (ulong)frame)) return 3;
        return frame->Rsp == Stack((int)id - 1) ? 0UL : 4;
    }

    private static bool OnOwnStack(SchedulerThread thread, ulong address) =>
        address >= thread.StackBase && address < thread.StackBase + thread.StackSize;

    [UnmanagedCallersOnly]
    private static int OnFault(UserFrame* frame, ulong vector, ulong error, ulong address)
    {
        faults++;
        faultVector = vector;
        faultError = error;
        faultAddress = address;
        return 0;
    }

    private static void OnNmi(ref UserFrame frame)
    {
        Interlocked.Increment(ref nmis);
        if ((frame.Cs & 3) != 0) Interlocked.Increment(ref nmisInRing3);
    }

    // ---- Threads ----

    internal static uint ProgramCpu => (uint)KernelCpus.Count - 1;

    internal static void PinTo(uint cpu)
    {
        ThreadAffinity.Current = CpuSet.Single(cpu);
        while (KernelCpus.Current != cpu) Thread.Sleep(1);
    }

    internal static bool Join(Thread thread, int seconds = 240) => thread.Join(seconds * 1000);

    internal static bool WaitFor(Func<bool> condition, int seconds = 240)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > seconds * 1000L) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>Ein Thread, der ein Programm immer wieder betritt, bis er angehalten wird.</summary>
    private sealed class Runner
    {
        private readonly Thread thread;
        private readonly int page;
        private int stop, finished;
        public SchedulerThread? Scheduler;
        public long Entered, Exits;
        public ulong Last;
        public ulong Bad; // erstes Ergebnis ungleich 0 bei Programmen, die mit 0 enden

        public Runner(string program, int page, uint cpu, bool once = false)
        {
            this.page = page;
            ulong entry = Entry(program);
            thread = new Thread(() =>
            {
                PinTo(cpu);
                *(ulong*)data[page] = (ulong)page + 1; // Ziel der FS-Basis
                *(ulong*)(data[page] + StackOffset) = (ulong)page + 1;
                UserMode.SetFsBase(DataUser(page));
                SchedulerThread? self = FaultIsolation.CurrentSchedulerThread();
                Volatile.Write(ref switchThreads[page], self);
                Volatile.Write(ref Scheduler, self);
                while (Volatile.Read(ref stop) == 0)
                {
                    Interlocked.Increment(ref Entered);
                    ulong result = UserMode.Enter(entry, Stack(page));
                    Volatile.Write(ref Last, result);
                    if (result != 0 && Bad == 0) Bad = result;
                    Interlocked.Increment(ref Exits);
                    if (once) break;
                }
                Volatile.Write(ref finished, 1);
            });
            thread.Start();
        }

        public bool Finished => Volatile.Read(ref finished) != 0;

        /// <summary>Laesst das laufende Programm zu Ende gehen oder bricht es ab.</summary>
        public bool Stop(bool leave)
        {
            Volatile.Write(ref stop, 1);
            if (leave && WaitFor(() => Volatile.Read(ref Scheduler) != null)) UserMode.RequestLeave(Scheduler!, 0);
            return Join(thread);
        }

        public bool WaitProgress()
        {
            ulong before = Volatile.Read(ref Counter(page));
            return WaitFor(() => Volatile.Read(ref Counter(page)) != before);
        }
    }

    // ---- Faelle ----

    private const int SwitchThreads = 3;
    private const int SwitchRounds = 200;

    /// <summary>
    /// Drei Ring-3-Threads und ein Beobachter auf einer CPU. Maskiert auf dieser CPU laeuft keiner der drei; liegt der gesicherte
    /// Rahmen eines Threads im Ring 3, kommt er nur ueber einen vorgemerkten Wechsel zurueck. Genau dann verlangt der Beobachter
    /// seinen Austritt: Nimmt der Rueckweg den falschen Thread, endet ein anderer, oder Rahmen, FS-Basis oder Stack passen nicht.
    /// </summary>
    private static (bool, string) Switch()
    {
        uint cpu = ProgramCpu;
        checkFailures = 0;
        checks = 0;
        var runners = new Runner[SwitchThreads];
        for (int i = 0; i < SwitchThreads; i++) runners[i] = new Runner("switch", i, cpu);
        int rounds = 0, badContext = 0, wrong = 0, others = 0;
        string failure = "";
        var observer = new Thread(() =>
        {
            PinTo(cpu);
            for (int i = 0; i < SwitchThreads; i++)
                if (!runners[i].WaitProgress()) { failure = "no progress"; return; }
            var exits = new long[SwitchThreads];
            while (rounds < SwitchRounds)
            {
                int target = rounds % SwitchThreads;
                Runner runner = runners[target];
                SchedulerThread thread = runner.Scheduler!;
                ulong value = 0x5000_0000UL + (ulong)rounds;
                for (int i = 0; i < SwitchThreads; i++) exits[i] = Interlocked.Read(ref runners[i].Exits);
                bool asked = false;
                var clock = Stopwatch.StartNew();
                while (!asked)
                {
                    if (clock.ElapsedMilliseconds > 60000) { failure = "never preempted in ring 3"; return; }
                    ulong flags = Mask();
                    ulong sp = (ulong)thread.StackPointer;
                    if (thread.State == SchedulerThreadState.Ready && OnOwnStack(thread, sp) && OnOwnStack(thread, sp + FpuAreaSize + (ulong)sizeof(UserFrame) - 1))
                    {
                        UserFrame* frame = (UserFrame*)(sp + FpuAreaSize);
                        if (frame->Cs == UserCs)
                        {
                            if (frame->Rip - userBase >= FirstDataPage * PageSize || frame->Rsp != Stack(target)) badContext++;
                            UserMode.RequestLeave(thread, value);
                            asked = true;
                        }
                    }
                    Restore(flags);
                    if (!asked) Thread.Sleep(1);
                }
                long before = exits[target];
                if (!WaitFor(() => Interlocked.Read(ref runner.Exits) != before, 60)) { failure = "request without exit"; return; }
                if (Volatile.Read(ref runner.Last) != value) wrong++;
                for (int i = 0; i < SwitchThreads; i++)
                    if (i != target && Interlocked.Read(ref runners[i].Exits) != exits[i]) others++;
                for (int i = 0; i < SwitchThreads; i++)
                    if (!runners[i].WaitProgress()) { failure = "stopped thread " + i; return; }
                rounds++;
            }
        });
        observer.Start();
        bool joined = Join(observer, 900);
        foreach (Runner runner in runners) joined &= runner.Stop(leave: true);
        return (joined && failure == "" && rounds == SwitchRounds && badContext == 0 && wrong == 0 && others == 0 && checkFailures == 0 && checks > 0,
            "staged_leaves=" + rounds + " bad_context=" + badContext + " wrong=" + wrong + " others=" + others + " checks=" + checks
            + " check_failures=" + checkFailures + " " + failure);
    }

    private static (bool, string) Loop()
    {
        calls = 0;
        ulong result = UserMode.Enter(Entry("loop"), Stack(0));
        return (result == 0 && calls == 1000000, "calls=" + calls + " result=0x" + result.ToString("X") + " tsc_ticks=" + exitAux);
    }

    private static (bool, string) Spin()
    {
        const int seconds = 10;
        uint cpu = ProgramCpu;
        var runner = new Runner("spin", 0, cpu, once: true);
        long counter = 0;
        int stop = 0;
        // Auf derselben CPU: nur der Tick kann dem Programm die CPU nehmen.
        var other = new Thread(() =>
        {
            PinTo(cpu);
            while (Volatile.Read(ref stop) == 0) Interlocked.Increment(ref counter);
        });
        other.Start();
        bool entered = WaitFor(() => Interlocked.Read(ref runner.Entered) != 0);
        int moved = 0;
        for (int i = 0; i < seconds; i++)
        {
            long before = Interlocked.Read(ref counter);
            Thread.Sleep(1000);
            if (Interlocked.Read(ref counter) != before) moved++;
        }
        bool running = !runner.Finished;
        UserMode.RequestLeave(runner.Scheduler!, 0x77);
        bool left = WaitFor(() => runner.Finished);
        Volatile.Write(ref stop, 1);
        bool joined = Join(other) && left && runner.Stop(leave: false);
        return (entered && running && joined && moved == seconds && runner.Last == 0x77,
            "seconds=" + moved + " running=" + (running ? 1 : 0) + " result=0x" + runner.Last.ToString("X"));
    }

    private static (bool, string) Fpu()
    {
        uint cpu = ProgramCpu;
        var first = new Runner("fpu", 0, cpu);
        var second = new Runner("fpu2", 1, cpu);
        double value = 1;
        int stop = 0;
        var kernel = new Thread(() =>
        {
            PinTo(cpu);
            while (Volatile.Read(ref stop) == 0) value = Math.Sqrt(value * 1.5 + 0.25);
        });
        kernel.Start();
        Thread.Sleep(10000);
        bool joined = WaitFor(() => Interlocked.Read(ref first.Exits) != 0 && Interlocked.Read(ref second.Exits) != 0);
        joined &= first.Stop(leave: false) & second.Stop(leave: false);
        Volatile.Write(ref stop, 1);
        joined &= Join(kernel);
        return (joined && first.Bad == 0 && second.Bad == 0,
            "runs=" + first.Exits + "/" + second.Exits + " bad=0x" + first.Bad.ToString("X") + "/0x" + second.Bad.ToString("X"));
    }

    private static (bool, string) Collect()
    {
        checkFailures = 0;
        uint cpu = ProgramCpu;
        var first = new Runner("switch", 0, cpu);
        var second = new Runner("switch", 1, cpu);
        bool progress = first.WaitProgress() && second.WaitProgress();
        int collections = 0;
        var collector = new Thread(() => { for (; collections < 1000; collections++) { _ = new byte[256]; GC.Collect(); } });
        collector.Start();
        bool joined = Join(collector, 900);
        progress &= first.WaitProgress() && second.WaitProgress();
        long exits = first.Exits + second.Exits; // vor dem Anhalten darf keiner geendet haben
        joined &= first.Stop(leave: true) & second.Stop(leave: true);
        return (joined && progress && collections == 1000 && exits == 0 && checkFailures == 0,
            "collections=" + collections + " exits=" + exits + " check_failures=" + checkFailures);
    }

    private static (bool, string) Fault(string program, ulong vector, ulong error, ulong address)
    {
        faults = 0;
        ulong result = UserMode.Enter(Entry(program), Stack(0));
        // Bit 0 (Seite vorhanden) und Bit 4 (Befehl) haengen vom Lader und von NX ab; der Wert steht im Log.
        bool ok = result == UserMode.Faulted && faults == 1 && faultVector == vector && (faultError & ~0x11UL) == error && faultAddress == address;
        return (ok, "vector=" + faultVector + " error=0x" + faultError.ToString("X") + " address=0x" + faultAddress.ToString("X") + " faults=" + faults);
    }

    private static (bool, string) Avx()
    {
        bool osxsave = (ReadCr4() & 1UL << 18) != 0;
        var (ok, text) = Fault("fault_avx", 6, 0, 0);
        return (ok && !osxsave, text + " osxsave=" + (osxsave ? 1 : 0));
    }

    private static (bool, string) BadReturn()
    {
        badReturn = 1;
        ulong nonCanonical = UserMode.Enter(Entry("badret"), Stack(0));
        badReturn = 2;
        ulong kernel = UserMode.Enter(Entry("badret"), Stack(0));
        badReturn = 0;
        ulong good = UserMode.Enter(Entry("badret"), Stack(0));
        return (nonCanonical == UserMode.InvalidReturn && kernel == UserMode.InvalidReturn && good == 0,
            "noncanonical=0x" + nonCanonical.ToString("X") + " kernel=0x" + kernel.ToString("X") + " good=0x" + good.ToString("X"));
    }

    private static (bool, string) KillSpin()
    {
        const int rounds = 1000;
        uint cpu = ProgramCpu;
        uint from = 0;
        var runner = new Runner("spin", 0, cpu);
        int done = 0, wrong = 0, otherCpu = 0;
        string failure = "";
        var killer = new Thread(() =>
        {
            PinTo(from);
            for (; done < rounds; done++)
            {
                // Der naechste Eintritt ist gezaehlt; nach dem Schlaf dreht das Programm im Ring 3.
                if (!WaitFor(() => Interlocked.Read(ref runner.Entered) > done, 60)) { failure = "no entry"; return; }
                Thread.Sleep(2);
                long before = Interlocked.Read(ref runner.Exits);
                if (before != done) { failure = "left early"; return; }
                ulong value = 0x6000_0000UL + (ulong)done;
                if (KernelCpus.Current != cpu) otherCpu++;
                UserMode.RequestLeave(runner.Scheduler!, value);
                if (!WaitFor(() => Interlocked.Read(ref runner.Exits) != before, 60)) { failure = "no exit"; return; }
                if (Volatile.Read(ref runner.Last) != value) wrong++;
            }
        });
        killer.Start();
        bool joined = Join(killer, 900) & runner.Stop(leave: true);
        bool cross = KernelCpus.Count == 1 || otherCpu == rounds;
        return (joined && failure == "" && done == rounds && wrong == 0 && cross,
            "rounds=" + done + " wrong=" + wrong + " other_cpu=" + otherCpu + " " + failure);
    }

    /// <summary>NMI waehrend <c>loop</c>: mit mehreren CPUs per IPI von CPU 0 (APIC-ID = CPU-Nummer wie in QEMU), sonst schickt das Skript sie.</summary>
    private static (bool, string) Nmi()
    {
        uint cpu = ProgramCpu;
        NmiHandler handler = OnNmi;
        object installed = CosmosHandler(handler);
        SetHandler(null, NmiVector, installed);
        calls = 0;
        nmis = 0;
        nmisInRing3 = 0;
        var runner = new Runner("loop", 0, cpu);
        bool joined = WaitFor(() => Interlocked.Read(ref runner.Entered) != 0);
        if (KernelCpus.Count > 1)
        {
            var sender = new Thread(() =>
            {
                PinTo(0);
                for (int i = 0; i < 2000; i++)
                {
                    ulong flags = Mask();
                    SendNmi(null, cpu);
                    Restore(flags);
                    if (i % 20 == 0) Thread.Sleep(1);
                }
            });
            sender.Start();
            joined &= Join(sender);
        }
        else
        {
            Write("nmi window open");
            joined &= WaitFor(() => Volatile.Read(ref nmisInRing3) >= 5, 60);
            Write("nmi window closed");
            // Ein noch zugestellter NMI braucht den Handler.
            Thread.Sleep(3000);
        }
        joined &= WaitFor(() => Interlocked.Read(ref runner.Exits) != 0) & runner.Stop(leave: false);
        SetHandler(null, NmiVector, null);
        GC.KeepAlive(installed);
        GC.KeepAlive(handler);
        return (joined && runner.Bad == 0 && calls == runner.Exits * 1000000 && nmisInRing3 > 0,
            "nmis=" + nmis + " ring3=" + nmisInRing3 + " runs=" + runner.Exits + " calls=" + calls + " bad=0x" + runner.Bad.ToString("X"));
    }

    private static (bool, string) Leave()
    {
        ulong entry = Entry("leave");
        for (int i = 0; i < 100; i++) UserMode.Enter(entry, Stack(0));
        GC.Collect();
        long before = (long)CosmosSystemInfo.PhysicalMemory().Free;
        int wrong = 0;
        for (int i = 0; i < 1000; i++)
            if (UserMode.Enter(entry, Stack(0)) != 0) wrong++;
        GC.Collect();
        long change = (long)CosmosSystemInfo.PhysicalMemory().Free - before;
        return (wrong == 0 && Math.Abs(change) <= 1024 * 1024, "wrong=" + wrong + " free_change=" + change);
    }
}
#endif
