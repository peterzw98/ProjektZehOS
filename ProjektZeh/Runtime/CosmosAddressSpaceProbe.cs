#if ZEHOS_USER_MODE_PROBE && !ZEHOS_USER_MODE_PROBE_NEGATIVE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Network;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Threading;
using ProjektZeh.Processes;
using ProjektZeh.Storage;
using Probe = ProjektZeh.Runtime.CosmosUserModeProbe;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // UserMode, UserPageTable und die Scheduler-Typen gelten als Vorschau.

/// <summary>
/// Gastprobe Ladybird N1-B4: Programme aus <c>Native/UserProbe/probe.S</c> auf eigenen <see cref="UserPageTable"/>s
/// (Cosmos-Patch user-cr3). Laeuft am Ende der Probe von N1-A6 und meldet ueber dieselben <c>USER_PROBE</c>-Zeilen.
/// Jeder Adressraum hat dasselbe Layout: Abbild ab <see cref="CodeBase"/> (lesen, ausfuehren), Datenseiten ab
/// <see cref="DataBase"/> (lesen, schreiben, nicht ausfuehren), Stack in Seite 1. Das Abbild liegt einmal in eigenen Frames
/// und ist in jeder Tabelle abgebildet; Datenframes gehoeren dem Adressraum.
/// </summary>
internal static unsafe class CosmosAddressSpaceProbe
{
    private const ulong PageSize = 4096;
    private const ulong CodeBase = 0x0000_0000_0040_0000;
    private const ulong DataBase = 0x0000_0000_1000_0000;
    private const int DataPages = 4;             // 0 Daten (isolate), 1 Stack, 2-3 Adressliste (sparse)
    private const ulong StackTop = DataBase + PageSize + 2048;
    private const ulong TopPage = 0x0000_7FFF_FFFF_F000; // gesperrt: erste Adresse hinter dem abbildbaren Bereich
    private const ulong SysCount = 0x7A01, SysExit = 0x7A02, SysCheck = 0x7A03, SysDevice = 0x7A04, SysSample = 0x7A05, SysYield = 0x7A06;
    private const int MaxSpaces = 8;

    private static ulong hhdm;
    private static ulong[] codeFrames = Array.Empty<ulong>();

    // Gebundene Adressraeume, damit die Handler den des laufenden Threads finden (Handler laufen im Thread, Interrupts an).
    private static readonly Space?[] bound = new Space?[MaxSpaces];

    // N1-B5: Messwerte der Zeitfaelle (Ticks aus rdtsc im Ring 3), je Gruppe; ausgewertet wird erst nach dem Messfenster.
    private const int SampleMax = 20000;
    private static readonly uint[][] samples = [new uint[SampleMax], new uint[SampleMax]];
    private static readonly int[] sampleCount = new int[2];

    private static long calls;
    private static int faults, faultsMapped;
    private static ulong faultVector, faultError, faultAddress;
    private static long checks;
    private static int checkFailures;
    private static int collectInCheck;
    private static int collectionsInCheck;

    /// <summary>Ein Benutzeradressraum: Tabelle, eigene Datenframes, bei Bedarf eine Belegungskarte (sparse).</summary>
    private sealed class Space
    {
        public readonly UserPageTable Table;
        public readonly ulong[] Data = new ulong[DataPages];
        public readonly List<ulong> Frames = new(16);
        public AddressSpaceMap? Map;
        public SchedulerThread? Thread;

        private Space(UserPageTable table) => Table = table;

        /// <param name="sharedData">Gegenprobe isolate: Datenseite 0 auf diesen fremden Frame (gehoert dem anderen Raum, hier nicht freigeben).</param>
        public static Space? Create(bool code = true, ulong sharedData = 0)
        {
            UserPageTable? table = UserPageTable.TryCreate();
            if (table == null) return null;
            var space = new Space(table);
            bool ok = true;
            if (code)
                for (int i = 0; i < codeFrames.Length; i++)
                    ok &= table.TryMap(CodeBase + (ulong)i * PageSize, codeFrames[i], writable: false, executable: true);
            for (int i = 0; i < DataPages && ok; i++)
            {
                ulong frame = i == 0 && sharedData != 0 ? sharedData : space.NewFrame();
                space.Data[i] = frame;
                ok = frame != 0 && table.TryMap(DataBase + (ulong)i * PageSize, frame, writable: true, executable: false);
            }
            if (!ok) { space.Dispose(); return null; }
            return space;
        }

        public ulong NewFrame()
        {
            ulong frame = UserPageTable.AllocateFrame();
            if (frame != 0) Frames.Add(frame);
            return frame;
        }

        public ulong* At(int page, ulong offset = 0) => (ulong*)(Data[page] + hhdm + offset);

        /// <summary>Argumente fuer das Programm bei [rsp], [rsp + 8], [rsp + 16].</summary>
        public void Args(ulong a, ulong b = 0, ulong c = 0)
        {
            ulong* stack = At(1, 2048);
            stack[0] = a;
            stack[1] = b;
            stack[2] = c;
        }

        /// <summary>Erst die Tabelle (wartet auf CPUs, die sie noch halten), dann die Frames: kein Unmap, das eine fremde CPU ablehnt.</summary>
        public void Dispose()
        {
            Table.Dispose();
            foreach (ulong frame in Frames) UserPageTable.FreeFrame(frame);
            Frames.Clear();
        }

        public void Bind()
        {
            Table.BindToCurrentThread();
            Thread = FaultIsolation.CurrentSchedulerThread();
            for (int i = 0; i < MaxSpaces; i++)
                if (Interlocked.CompareExchange(ref bound[i], this, null) == null) return;
            throw new InvalidOperationException("too many bound spaces");
        }

        public void Unbind()
        {
            for (int i = 0; i < MaxSpaces; i++)
                if (ReferenceEquals(Volatile.Read(ref bound[i]), this)) Volatile.Write(ref bound[i], null);
            UserPageTable.UnbindCurrentThread();
            Thread = null;
        }
    }

    private static Space? Current()
    {
        SchedulerThread? thread = FaultIsolation.CurrentSchedulerThread();
        for (int i = 0; i < MaxSpaces; i++)
        {
            Space? space = Volatile.Read(ref bound[i]);
            if (space != null && ReferenceEquals(space.Thread, thread)) return space;
        }
        return null;
    }

    private static ulong Entry(string name) => CodeBase + Probe.Offset(name);

    internal static void Run(delegate* unmanaged<UserFrame*, void> previousSyscall, delegate* unmanaged<UserFrame*, ulong, ulong, ulong, int> previousFault)
    {
        bool shared = UserPageTable.ShareKernelHalf();
        hhdm = Probe.hhdm; // AddressSpace ist intern; N1-A6 hat den Versatz aus einer Heapseite bestimmt
        if (!shared || !CopyImage())
        {
            Probe.Report("b4_setup", false, "shared=" + (shared ? 1 : 0) + " (ShareKernelHalf braucht EFER.NXE) or no frames");
            return;
        }
        Probe.Write("info b4 code_pages=" + codeFrames.Length + " hhdm=0x" + hhdm.ToString("X"));
        // Eigene Handler fuer die B4-Faelle; sie bedienen auch Zaehlen und Ende, die Case nach jedem Fall braucht.
        UserMode.SyscallHandler = &OnSyscall;
        UserMode.FaultHandler = &OnFault;
        try
        {
            Probe.Case("isolate", Isolate);
            Probe.Case("kernelread", KernelRead);
            Probe.Case("nx", NoExecute);
            Probe.Case("edge", Edge);
            Probe.Case("sparse", Sparse);
            Probe.Case("copy", Copy);
            Probe.Case("churn", Churn);
            Probe.Case("gc2", Collect);
            Probe.Case("device", Device);
            Probe.Case("aswitch", AddressSwitch);
            Probe.Case("pfault", PageFault);
        }
        finally
        {
            UserMode.SyscallHandler = previousSyscall;
            UserMode.FaultHandler = previousFault;
        }
    }

    private static bool CopyImage()
    {
        ulong size = Probe.ImageSize();
        int pages = (int)((size + PageSize - 1) / PageSize);
        codeFrames = new ulong[pages];
        for (int i = 0; i < pages; i++)
        {
            codeFrames[i] = UserPageTable.AllocateFrame();
            if (codeFrames[i] == 0) return false;
            ulong chunk = Math.Min(PageSize, size - (ulong)i * PageSize);
            Buffer.MemoryCopy((void*)(Probe.ImageAddress() + (ulong)i * PageSize), (void*)(codeFrames[i] + hhdm), PageSize, chunk);
        }
        return true;
    }

    // ---- Handler ----

    [UnmanagedCallersOnly]
    private static void OnSyscall(UserFrame* frame)
    {
        switch (frame->Rax)
        {
            case SysCount:
                Interlocked.Increment(ref calls);
                frame->Rax = 0;
                break;
            case SysExit:
                UserMode.LeaveCurrent(frame->Rdi);
                break;
            case SysCheck:
                ulong result = Check(frame->Rdi);
                if (result != 0) Interlocked.Increment(ref checkFailures);
                long count = Interlocked.Increment(ref checks);
                // gc2: der Thread laeuft im Handler auf der Tabelle des Programms.
                if (Volatile.Read(ref collectInCheck) != 0 && count % 8 == 0)
                {
                    GC.Collect();
                    Interlocked.Increment(ref collectionsInCheck);
                }
                frame->Rax = result;
                break;
            case SysDevice:
                frame->Rax = DeviceStep();
                break;
            case SysSample:
                // Mehr als SampleMax je Gruppe werden verworfen; der Zaehler laeuft weiter, damit der Fall das Ende sieht.
                ulong group = frame->Rsi & 1;
                int index = Interlocked.Increment(ref sampleCount[group]) - 1;
                if (index < SampleMax) samples[group][index] = (uint)frame->Rdi;
                frame->Rax = 0;
                break;
            case SysYield:
                Thread.Yield();
                frame->Rax = 0;
                break;
            default:
                frame->Rax = ulong.MaxValue;
                break;
        }
    }

    /// <summary>Der Kernel liest die Datenseite des Adressraums des laufenden Threads ueber dessen Tabelle und den HHDM.</summary>
    private static ulong Check(ulong id)
    {
        Space? space = Current();
        if (space == null) return 1;
        if (!space.Table.TryTranslate(DataBase, write: true, out ulong physical)) return 2;
        if (*(ulong*)(physical + hhdm) != id || physical != space.Data[0]) return 3;
        return *space.At(1, 2048) == id ? 0UL : 4;
    }

    [UnmanagedCallersOnly]
    private static int OnFault(UserFrame* frame, ulong vector, ulong error, ulong address)
    {
        // sparse: nicht vorhandene Seite in einem reservierten Bereich wird hier belegt (Handler im Thread, Interrupts an).
        if (vector == 14 && (error & 1) == 0)
        {
            Space? space = Current();
            ulong page = address & ~(PageSize - 1);
            if (space?.Map != null && space.Map.TryFind(page, out MapAccess access) && access != MapAccess.None)
            {
                ulong frameAddress = space.NewFrame();
                if (frameAddress != 0 && space.Table.TryMap(page, frameAddress, access == MapAccess.ReadWrite, access == MapAccess.ReadExecute))
                {
                    faultsMapped++;
                    return 1;
                }
            }
        }
        faults++;
        faultVector = vector;
        faultError = error;
        faultAddress = address;
        return 0;
    }

    // ---- Threads ----

    /// <summary>Ein Thread, gebunden an einen eigenen Adressraum, der ein Programm immer wieder betritt, bis er angehalten wird.</summary>
    private sealed class Runner
    {
        private readonly Thread thread;
        private readonly Space space;
        private int stop, finished;
        public SchedulerThread? Scheduler;
        public long Exits;
        public ulong Bad;
        public string Failure = "";

        public Runner(Space space, string program, ulong id, uint cpu)
        {
            this.space = space;
            ulong entry = Entry(program);
            space.Args(id, 0, DataBase);
            thread = new Thread(() =>
            {
                Probe.PinTo(cpu);
                try { space.Bind(); }
                catch (Exception exception) { Failure = "bind " + exception.Message; Volatile.Write(ref finished, 1); return; }
                Volatile.Write(ref Scheduler, space.Thread);
                while (Volatile.Read(ref stop) == 0)
                {
                    ulong result = UserMode.Enter(entry, StackTop);
                    if (result != 0 && Bad == 0) Bad = result;
                    Interlocked.Increment(ref Exits);
                }
                space.Unbind();
                Volatile.Write(ref finished, 1);
            });
            thread.Start();
        }

        public ulong Progress => Volatile.Read(ref *space.At(1, 2048 + 8));

        public bool WaitProgress()
        {
            ulong before = Progress;
            return Probe.WaitFor(() => Progress != before || Volatile.Read(ref finished) != 0, 60) && Volatile.Read(ref finished) == 0;
        }

        public bool Stop()
        {
            Volatile.Write(ref stop, 1);
            Probe.WaitFor(() => Volatile.Read(ref Scheduler) != null || Volatile.Read(ref finished) != 0, 60);
            SchedulerThread? target = Volatile.Read(ref Scheduler);
            if (target != null) UserMode.RequestLeave(target, 0);
            return Probe.Join(thread);
        }
    }

    // ---- Faelle ----

    /// <summary>Zwei Programme an denselben Adressen in zwei Tabellen, je 10 s auf derselben CPU und (ab 2 CPUs) auf verschiedenen.</summary>
    private static (bool, string) Isolate()
    {
        // Gegenprobe zuerst: Teilen beide Tabellen die Datenseite, muss der Fall das sehen, sonst beweist er auch ohne CR3-Wechsel nichts.
        uint controlCpu = KernelCpus.Count == 1 ? Probe.ProgramCpu : 0;
        string control = IsolatePair(controlCpu, Probe.ProgramCpu, true, out bool controlOk);
        string same = IsolatePair(Probe.ProgramCpu, Probe.ProgramCpu, false, out bool sameOk);
        string text = "control(shared page): " + control + " same: " + same;
        if (KernelCpus.Count == 1) return (controlOk && sameOk, text + " other: skipped(1 cpu)");
        string other = IsolatePair(0, Probe.ProgramCpu, false, out bool otherOk);
        return (controlOk && sameOk && otherOk, text + " other: " + other);
    }

    /// <param name="shared">Gegenprobe: b bildet die Datenseite von a ab; ok heisst dann, die Ueberschneidung wurde gesehen.</param>
    private static string IsolatePair(uint firstCpu, uint secondCpu, bool shared, out bool ok)
    {
        checks = 0;
        checkFailures = 0;
        Space? a = Space.Create();
        Space? b = a == null ? null : Space.Create(sharedData: shared ? a.Data[0] : 0);
        if (a == null || b == null) { b?.Dispose(); a?.Dispose(); ok = false; return "no space"; }
        var first = new Runner(a, "isolate", 0x1111, firstCpu);
        var second = new Runner(b, "isolate", 0x2222, secondCpu);
        int seconds = 0, wrong = 0;
        bool progress = true;
        for (; seconds < 10; seconds++)
        {
            if (shared)
            {
                // Bad liest der Runner nach jedem Austritt; ein erster Befund reicht, die Gegenprobe endet dann frueh.
                if (first.Bad != 0 || second.Bad != 0 || Volatile.Read(ref checkFailures) != 0) break;
                Thread.Sleep(1000);
                continue;
            }
            progress &= first.WaitProgress() && second.WaitProgress();
            // Gegenlesung aus dem Kernel: jeder Frame haelt nur die eigene Kennung.
            if (*a.At(0) != 0x1111 || *b.At(0) != 0x2222) wrong++;
            Thread.Sleep(1000);
        }
        bool joined = first.Stop() & second.Stop();
        ulong p1 = first.Progress, p2 = second.Progress;
        // b zuerst: sein Eintrag zeigt bei der Gegenprobe auf den Frame von a, den erst a.Dispose freigibt.
        b.Dispose();
        a.Dispose();
        bool failures = first.Failure == "" && second.Failure == "";
        if (shared)
            ok = joined && failures && (first.Bad == 1 || second.Bad == 1 || checkFailures > 0);
        else
            ok = joined && progress && wrong == 0 && first.Bad == 0 && second.Bad == 0 && checks > 0 && checkFailures == 0 && failures;
        return "cpus=" + firstCpu + "/" + secondCpu + " seconds=" + seconds + " progress=" + p1 + "/" + p2 + " checks=" + checks
            + " check_failures=" + checkFailures + " wrong=" + wrong + " bad=0x" + first.Bad.ToString("X") + "/0x" + second.Bad.ToString("X")
            + " " + first.Failure + second.Failure;
    }

    /// <summary>Faellt mit eigenem Adressraum auf dem aktuellen Thread aus, mit dem erwarteten #PF.</summary>
    private static (ulong Result, ulong Vector, ulong Error, ulong Address) Fault(Space space, string program, ulong argument, ulong second = 0, ulong rip = 0)
    {
        faults = 0;
        faultVector = faultError = faultAddress = 0;
        space.Args(argument, second);
        space.Bind();
        ulong result;
        try { result = UserMode.Enter(rip != 0 ? rip : Entry(program), StackTop); }
        finally { space.Unbind(); }
        return (result, faults == 1 ? faultVector : 0xFF, faultError, faultAddress);
    }

    // ---- N1-B5: Zeitfaelle ----

    /// <summary>Min, p10, Median, p90, p99, Max der ersten <paramref name="count"/> Ticks (sortiert in place).</summary>
    private static string Stats(uint[] values, int count)
    {
        if (count == 0) return "n=0";
        Array.Sort(values, 0, count);
        return "n=" + count + " min=" + values[0] + " p10=" + values[count / 10] + " med=" + values[count / 2]
            + " p90=" + values[count * 9 / 10] + " p99=" + values[count * 99 / 100] + " max=" + values[count - 1];
    }

    private static int Filled(int group) => Math.Min(Volatile.Read(ref sampleCount[group]), SampleMax);

    /// <summary>
    /// Kosten eines Adressraumwechsels: ein Yield-Aufruf im Ring 3, allein (kein anderer Thread, kein Wechsel) und mit einem zweiten
    /// Programm in einem anderen Adressraum auf derselben CPU (zwei Wechsel mit CR3). Gemessen wird "Wechsel inkl. Yield": Die Differenz
    /// der Mediane enthaelt Scheduler (Thread.Yield), Syscall-Ein/Austritt und CR3, nicht CR3 allein. Die Gegenstelle ruft nur Yield
    /// (kein SYS_SAMPLE), damit beide Arme dieselbe Arbeit je Runde tun. Gegenprobe: zwei Kernelthreads auf derselben CPU ohne Ring 3,
    /// ebenfalls in TSC-Ticks (gleiche Einheit wie alone/pair).
    /// </summary>
    private static (bool, string) AddressSwitch()
    {
        uint cpu = Probe.ProgramCpu;
        Space? a = Space.Create();
        Space? b = Space.Create();
        if (a == null || b == null) { a?.Dispose(); b?.Dispose(); return (false, "no space"); }
        sampleCount[0] = sampleCount[1] = 0;
        var alone = new Runner(a, "aswitch", 0, cpu);
        bool aloneOk = Probe.WaitFor(() => Volatile.Read(ref sampleCount[0]) >= SampleMax, 60);
        bool stopped = alone.Stop();
        bool failed = alone.Bad != 0 || alone.Failure != "";
        var first = new Runner(a, "aswitch", 1, cpu);
        var second = new Runner(b, "aswitch", 0xFF, cpu); // Gegenstelle: nur Yield, ohne rdtsc/SYS_SAMPLE (gleiche Arbeit wie im Arm "alone")
        bool pairOk = Probe.WaitFor(() => Volatile.Read(ref sampleCount[1]) >= SampleMax, 60);
        stopped &= first.Stop();
        stopped &= second.Stop();
        failed |= first.Bad != 0 || second.Bad != 0 || first.Failure != "" || second.Failure != "";
        int aloneN = Filled(0), pairN = Filled(1);
        string aloneText = Stats(samples[0], aloneN), pairText = Stats(samples[1], pairN);
        long net = aloneN == 0 || pairN == 0 ? -1 : (long)samples[1][pairN / 2] - samples[0][aloneN / 2];
        a.Dispose();
        b.Dispose();
        // Gegenprobe ohne Ring 3: zwei Kernelthreads wechseln per Yield auf derselben CPU.
        string kernelText = KernelSwitch(cpu, out bool kernelOk);
        return (aloneOk && pairOk && stopped && !failed && kernelOk,
            "alone: " + aloneText + " pair: " + pairText + " net_med=" + net + " kernel_pair(tsc_ticks): " + kernelText);
    }

    private static string KernelSwitch(uint cpu, out bool ok)
    {
        const int rounds = 20000;
        var ticks = new uint[rounds];
        int count = 0, stop = 0;
        var other = new Thread(() =>
        {
            Probe.PinTo(cpu);
            while (Volatile.Read(ref stop) == 0) Thread.Yield();
        });
        var measure = new Thread(() =>
        {
            Probe.PinTo(cpu);
            for (; count < rounds; count++)
            {
                ulong start = Probe.ReadTsc();
                Thread.Yield();
                ticks[count] = (uint)(Probe.ReadTsc() - start);
            }
        });
        other.Start();
        measure.Start();
        bool joined = Probe.Join(measure, 60);
        Volatile.Write(ref stop, 1);
        joined &= Probe.Join(other, 60);
        ok = joined && count == rounds;
        return Stats(ticks, count);
    }

    /// <summary>
    /// Kosten eines Seitenfehlers mit Belegung: Erstzugriff (Store, der FaultHandler holt einen Frame und bildet ihn ab) gegen denselben
    /// Store auf einer schon abgebildeten Seite. Die Seiten liegen hintereinander in einem Bereich (eine Seitentabelle fasst 512).
    /// </summary>
    private static (bool, string) PageFault()
    {
        const int pages = 4000;
        Space? space = Space.Create();
        if (space == null) return (false, "no space");
        var map = new AddressSpaceMap(PageSize, TopPage);
        map.Reserve(CodeBase, (ulong)codeFrames.Length * PageSize, MapAccess.ReadExecute, Placement.FixedNoReplace);
        map.Reserve(DataBase, DataPages * PageSize, MapAccess.ReadWrite, Placement.FixedNoReplace);
        var range = map.Reserve(0x1_0000_0000, pages * PageSize, MapAccess.ReadWrite, Placement.Hint);
        if (!range.IsOK()) { space.Dispose(); return (false, "reserve failed"); }
        space.Map = map;
        space.Frames.Capacity = pages + 64;
        sampleCount[0] = sampleCount[1] = 0;
        faultsMapped = 0;
        var (result, vector, _, _) = Fault(space, "pfault", range.Value, pages);
        int mapped = faultsMapped;
        int framesUsed = space.Frames.Count;
        space.Dispose();
        int first = Filled(0), again = Filled(1);
        string firstText = Stats(samples[0], first), againText = Stats(samples[1], again);
        long net = first == 0 || again == 0 ? -1 : (long)samples[0][first / 2] - samples[1][again / 2];
        return (result == 0 && mapped == pages && first == pages && again == pages,
            "pages=" + pages + " mapped=" + mapped + " frames=" + framesUsed + " first_touch: " + firstText + " mapped_touch: " + againText + " net_med=" + net
            + " result=" + Hex(result) + " vector=" + vector);
    }

    private static string Hex(ulong value) => "0x" + value.ToString("X");

    /// <summary>
    /// Kernelhaelfte (Heap im HHDM, Kernelabbild) ist ohne User-Bit: #PF mit P=1. Die untere Haelfte der Kerneltabellen (das
    /// Programmfenster von N1-A6, dort mit User-Bit, und die alte Identitaetsabbildung) fehlt in der eigenen Tabelle: #PF mit P=0.
    /// Gegenprobe: dasselbe A6-Fenster ist mit den Kerneltabellen lesbar.
    /// </summary>
    private static (bool, string) KernelRead()
    {
        Space? space = Space.Create();
        if (space == null) return (false, "no space");
        ulong heap = Probe.data[0], image = Probe.ImageAddress(), window = Probe.userBase, identity = 0x10_0000;
        ulong kernelRoot = Probe.ReadCr3() & 0x000F_FFFF_FFFF_F000;
        // Ohne Abbildung in den Kerneltabellen faende der Fall identity auch ohne eigene Tabelle #PF mit P=0: dann ist er rot.
        bool identityInKernel = KernelMaps(kernelRoot, identity);
        var cases = new (string Name, ulong Address, ulong Error)[]
        {
            ("heap", heap, 5), ("image", image, 5), ("a6window", window, 4), ("identity", identity, 4),
        };
        bool ok = true;
        string text = "";
        foreach (var (name, address, error) in cases)
        {
            var (result, vector, got, at) = Fault(space, "readat", address);
            bool one = result == UserMode.Faulted && vector == 14 && (got & ~0x10UL) == error && at == address;
            ok &= one;
            text += name + "=" + (one ? "pf" : "BAD") + "(" + Hex(got) + ") ";
        }
        space.Dispose();
        // Gegenprobe mit den Kerneltabellen (Programm von N1-A6): readat endet mit 0x10, wenn das Lesen gelingt.
        *(ulong*)(Probe.data[0] + 2048) = window;
        ulong control = UserMode.Enter(Probe.userBase + Probe.Offset("readat"), Probe.Stack(0));
        ok &= control == 0x10 && identityInKernel;
        return (ok, text + "kernel_tables_read=" + Hex(control) + " identity_in_kernel=" + (identityInKernel ? 1 : 0));
    }

    /// <summary>Geht die Kerneltabellen ab <paramref name="root"/> fuer <paramref name="address"/> durch (4 Ebenen, grosse Seiten).</summary>
    private static bool KernelMaps(ulong root, ulong address)
    {
        ulong table = root;
        for (int shift = 39; shift >= 12; shift -= 9)
        {
            ulong entry = ((ulong*)(table + hhdm))[(address >> shift) & 0x1FF];
            if ((entry & 1) == 0) return false;
            if (shift == 12 || (shift <= 30 && (entry & 0x80) != 0)) return true;
            table = entry & 0x000F_FFFF_FFFF_F000;
        }
        return false;
    }

    /// <summary>Ausfuehren einer Datenseite: #PF mit I/D-Bit; Gegenprobe: Sprung in den Code laeuft.</summary>
    private static (bool, string) NoExecute()
    {
        Space? space = Space.Create();
        if (space == null) return (false, "no space");
        var (result, vector, error, address) = Fault(space, "execat", DataBase);
        var (control, _, _, _) = Fault(space, "execat", Entry("leave"));
        space.Dispose();
        bool ok = result == UserMode.Faulted && vector == 14 && (error & 0x15) == 0x15 && address == DataBase && control == 0;
        return (ok, "vector=" + vector + " error=" + Hex(error) + " address=" + Hex(address) + " control=" + Hex(control));
    }

    /// <summary>
    /// <c>syscall</c> als letzte Anweisung der Seite unter <see cref="TopPage"/>: Der Rueckweg kehrt nach TopPage zurueck (kanonisch,
    /// nicht abbildbar) und endet dort mit #PF im Ring 3, nicht im Kernel. Die Seite selbst lehnt TryMap ab.
    /// </summary>
    private static (bool, string) Edge()
    {
        Space? space = Space.Create();
        if (space == null) return (false, "no space");
        ulong frame = space.NewFrame();
        bool topRefused = !space.Table.TryMap(TopPage, frame, writable: false, executable: true);
        bool mapped = space.Table.TryMap(TopPage - PageSize, frame, writable: false, executable: true);
        // mov eax, 0x7A01 (B8 01 7A 00 00); syscall (0F 05): endet genau an TopPage.
        byte* tail = (byte*)(frame + hhdm + PageSize - 7);
        byte* code = stackalloc byte[] { 0xB8, 0x01, 0x7A, 0x00, 0x00, 0x0F, 0x05 };
        for (int i = 0; i < 7; i++) tail[i] = code[i];
        long before = Interlocked.Read(ref calls);
        var (result, vector, error, address) = Fault(space, "", 0, 0, TopPage - 7);
        long made = Interlocked.Read(ref calls) - before;
        space.Dispose();
        bool ok = topRefused && mapped && result == UserMode.Faulted && vector == 14 && (error & ~0x10UL) == 4 && address == TopPage && made == 1;
        return (ok, "top_refused=" + (topRefused ? 1 : 0) + " calls=" + made + " result=" + Hex(result) + " vector=" + vector
            + " error=" + Hex(error) + " address=" + Hex(address));
    }

    /// <summary>32 GiB und 4 TiB reserviert (AddressSpaceMap), 1000 verstreute Seiten, belegt erst im FaultHandler.</summary>
    private static (bool, string) Sparse()
    {
        const int pages = 1000;
        Space? space = Space.Create();
        if (space == null) return (false, "no space");
        var map = new AddressSpaceMap(PageSize, TopPage);
        // Code und Daten sind fest belegt, damit Hint nicht darauf faellt.
        map.Reserve(CodeBase, (ulong)codeFrames.Length * PageSize, MapAccess.ReadExecute, Placement.FixedNoReplace);
        map.Reserve(DataBase, DataPages * PageSize, MapAccess.ReadWrite, Placement.FixedNoReplace);
        var small = map.Reserve(0x1_0000_0000, 32UL << 30, MapAccess.ReadWrite, Placement.Hint);
        var large = map.Reserve(0x100_0000_0000, 4UL << 40, MapAccess.ReadWrite, Placement.Hint);
        if (!small.IsOK() || !large.IsOK()) { space.Dispose(); return (false, "reserve failed"); }
        space.Map = map;
        space.Frames.Capacity = pages + 64;
        ulong seed = 0x5EED_0B41;
        var pageList = new ulong[pages];
        for (int i = 0; i < pages; i++)
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            bool inLarge = (i & 1) != 0;
            ulong length = inLarge ? 4UL << 40 : 32UL << 30;
            ulong start = inLarge ? large.Value : small.Value;
            // Die Liste liegt in zwei Datenseiten, deren Frames nicht nebeneinander liegen muessen.
            ulong address = start + (seed % (length / PageSize)) * PageSize + ((seed >> 40) & 0x1FF) * 8;
            *space.At(2 + i / 512, (ulong)(i % 512) * 8) = address;
            pageList[i] = address & ~(PageSize - 1);
        }
        // Der Handler belegt jede Seite genau einmal: erwartet ist die Zahl verschiedener Seiten, nicht nur hoechstens 1000.
        // Quadratisch statt Array.Sort: 10^6 Vergleiche, keine im Kernel ungepruefte API.
        int distinct = 0;
        for (int i = 0; i < pages; i++)
        {
            int j = 0;
            while (j < i && pageList[j] != pageList[i]) j++;
            if (j == i) distinct++;
        }
        int framesBefore = space.Frames.Count;
        faultsMapped = 0;
        var (result, vector, _, _) = Fault(space, "sparse", DataBase + 2 * PageSize, pages);
        int mapped = faultsMapped;
        // Ausserhalb der Reservierung belegt der Handler nichts: #PF beendet den Lauf.
        var (outside, outVector, _, outAddress) = Fault(space, "readat", 0x8000_0000);
        int framesUsed = space.Frames.Count - framesBefore;
        space.Dispose();
        bool ok = result == 0 && mapped == distinct && framesUsed == mapped
            && outside == UserMode.Faulted && outVector == 14 && outAddress == 0x8000_0000;
        return (ok, "small=" + Hex(small.Value) + " large=" + Hex(large.Value) + " mapped=" + mapped + " distinct=" + distinct + " result=" + Hex(result)
            + " vector=" + vector + " outside=" + Hex(outside));
    }

    /// <summary>
    /// Kopierweg wie ein Systemaufruf ihn braucht: Seite fuer Seite TryTranslate, dann ueber den HHDM; alles oder nichts. Der Pin
    /// (user-cr3) hat keinen eigenen Kopierweg in den Kernel; geprueft wird daher dieser probeneigene Weg auf UserPageTable.TryTranslate.
    /// </summary>
    private static bool CopyUser(Space space, ulong user, byte* kernel, ulong length, bool toUser)
    {
        if (length == 0) return true;
        if (user > ulong.MaxValue - length) return false;
        ulong end = user + length;
        // Erst pruefen, dann kopieren: ein Fehler mitten im Bereich laesst den Speicher unveraendert.
        for (ulong page = user & ~(PageSize - 1); page < end; page += PageSize)
            if (!space.Table.TryTranslate(page, toUser, out _)) return false;
        ulong done = 0;
        while (done < length)
        {
            ulong at = user + done;
            ulong chunk = Math.Min(length - done, PageSize - (at & (PageSize - 1)));
            if (!space.Table.TryTranslate(at, toUser, out ulong physical)) return false;
            byte* mapped = (byte*)(physical + hhdm);
            if (toUser) Buffer.MemoryCopy(kernel + done, mapped, chunk, chunk);
            else Buffer.MemoryCopy(mapped, kernel + done, chunk, chunk);
            done += chunk;
        }
        return true;
    }

    private static (bool, string) Copy()
    {
        const int pages = 16, rounds = 10000, maxLength = 9000;
        const ulong copyBase = 0x2000_0000;
        Space? space = Space.Create();
        if (space == null) return (false, "no space");
        var shadow = new byte[pages * (int)PageSize];
        var frames = new ulong[pages];
        bool setup = true;
        for (int i = 0; i < pages; i++)
        {
            if (i % 4 == 3) continue; // Luecke
            frames[i] = space.NewFrame();
            setup &= frames[i] != 0 && space.Table.TryMap(copyBase + (ulong)i * PageSize, frames[i], writable: i % 2 == 0, executable: false);
            for (int j = 0; j < (int)PageSize; j++) shadow[i * (int)PageSize + j] = ((byte*)(frames[i] + hhdm))[j] = (byte)(i * 31 + j);
        }
        byte[] buffer = new byte[maxLength];
        ulong seed = 0xC0FF_EE00_1234_5678;
        int succeeded = 0, refused = 0, wrong = 0;
        fixed (byte* kernel = buffer)
            for (int round = 0; round < rounds; round++)
            {
                seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
                ulong kind = seed % 10, length = (seed >> 8) % maxLength;
                if ((seed >> 20) % 16 == 0) length = 0;
                ulong user = kind switch
                {
                    < 5 => copyBase - 2 * PageSize + (seed >> 24) % ((pages + 4) * PageSize),
                    5 or 6 => 0xFFFF_8000_0000_0000 + (seed >> 16),        // Kernelhaelfte
                    7 => 0x0000_8000_0000_0000 + (seed >> 24),              // nicht kanonisch
                    8 => ulong.MaxValue - (seed >> 50),                     // Ueberlauf von user + length
                    _ => (seed >> 24) % 0x10_0000,                          // unten, auch Seite 0
                };
                bool toUser = (seed >> 60 & 1) != 0;
                if (toUser) for (ulong k = 0; k < length; k++) kernel[k] = (byte)(seed >> (int)(k % 56));
                bool expected = Expected(user, length, toUser, copyBase, pages);
                bool got = CopyUser(space, user, kernel, length, toUser);
                if (got != expected) { wrong++; continue; }
                if (!got) { refused++; continue; }
                succeeded++;
                if (length == 0) continue;
                int offset = (int)(user - copyBase);
                for (int k = 0; k < (int)length; k++)
                {
                    if (toUser) shadow[offset + k] = kernel[k];
                    else if (kernel[k] != shadow[offset + k]) { wrong++; break; }
                }
            }
        // Am Ende: jede abgebildete Seite gleicht dem Modell.
        int differs = 0;
        for (int i = 0; i < pages; i++)
            if (frames[i] != 0)
                for (int j = 0; j < (int)PageSize; j++)
                    if (((byte*)(frames[i] + hhdm))[j] != shadow[i * (int)PageSize + j]) { differs++; break; }
        space.Dispose();
        return (setup && wrong == 0 && differs == 0 && succeeded > 100 && refused > 100,
            "path=probe_copyuser_on_trytranslate rounds=" + rounds + " ok=" + succeeded + " refused=" + refused + " wrong=" + wrong + " differs=" + differs);
    }

    private static bool Expected(ulong user, ulong length, bool write, ulong copyBase, int pages)
    {
        if (length == 0) return true;
        if (user > ulong.MaxValue - length || user < copyBase || user + length > copyBase + (ulong)pages * PageSize) return false;
        for (ulong page = (user - copyBase) / PageSize; page <= (user + length - 1 - copyBase) / PageSize; page++)
            if (page % 4 == 3 || (write && page % 2 != 0)) return false;
        return true;
    }

    /// <summary>1000 Adressraeume anlegen, binden, betreten, loesen, freigeben; freier Speicher danach +-1 MiB.</summary>
    private static (bool, string) Churn()
    {
        for (int i = 0; i < 50; i++) if (!ChurnOnce()) return (false, "warmup failed at " + i);
        GC.Collect();
        long before = (long)CosmosSystemInfo.PhysicalMemory().Free;
        int failedRounds = 0;
        for (int i = 0; i < 1000; i++) if (!ChurnOnce()) failedRounds++;
        GC.Collect();
        long change = (long)CosmosSystemInfo.PhysicalMemory().Free - before;
        return (failedRounds == 0 && Math.Abs(change) <= 1024 * 1024, "spaces=1000 failed=" + failedRounds + " free_change=" + change);
    }

    private static bool ChurnOnce()
    {
        Space? space = Space.Create();
        if (space == null) return false;
        space.Bind();
        ulong result;
        try { result = UserMode.Enter(Entry("leave"), StackTop); }
        finally { space.Unbind(); }
        space.Dispose();
        return result == 0;
    }

    /// <summary>
    /// GC mit eigenem CR3: zwei isolate-Programme in eigenen Tabellen; ihre Threads sammeln im Handler (auf der Tabelle des
    /// Programms), dazu ein dritter, an eine eigene Tabelle gebundener Thread 1000-mal GC.Collect().
    /// </summary>
    private static (bool, string) Collect()
    {
        checks = 0;
        checkFailures = 0;
        collectionsInCheck = 0;
        Space? a = Space.Create(), b = Space.Create(), c = Space.Create(code: false);
        if (a == null || b == null || c == null) { a?.Dispose(); b?.Dispose(); c?.Dispose(); return (false, "no space"); }
        Space own = c;
        uint cpu = Probe.ProgramCpu;
        var first = new Runner(a, "isolate", 0x3333, cpu);
        var second = new Runner(b, "isolate", 0x4444, cpu);
        bool progress = first.WaitProgress() && second.WaitProgress();
        Volatile.Write(ref collectInCheck, 1);
        int collections = 0;
        string failure = "";
        var collector = new Thread(() =>
        {
            try
            {
                own.Bind();
                for (; collections < 1000; collections++) { _ = new byte[256]; GC.Collect(); }
                own.Unbind();
            }
            catch (Exception exception) { failure = exception.GetType().Name; }
        });
        collector.Start();
        bool joined = Probe.Join(collector, 900);
        int overlapped = Volatile.Read(ref collectionsInCheck);
        // Sammeln im Handler haengt nur am Fortschritt der Programme (jeder achte SYS_CHECK), nicht an der Dauer des Sammlers.
        bool inHandler = Probe.WaitFor(() => Volatile.Read(ref collectionsInCheck) >= 4, 120);
        progress &= first.WaitProgress() && second.WaitProgress();
        Volatile.Write(ref collectInCheck, 0);
        long exits = first.Exits + second.Exits;
        joined &= first.Stop() & second.Stop();
        a.Dispose(); b.Dispose(); c.Dispose();
        return (joined && progress && failure == "" && collections == 1000 && exits == 0 && checkFailures == 0 && inHandler,
            "collections=" + collections + " in_handler=" + collectionsInCheck + " overlapped=" + overlapped + " exits=" + exits + " check_failures=" + checkFailures + " " + failure);
    }

    // ---- device ----

    private static IBlockDevice? disk;
    private static byte[] diskReference = Array.Empty<byte>(), diskBuffer = Array.Empty<byte>();
    private static Canvas? canvas;
    private static int deviceRound;
    private static int diskReads, frames, pings, pingLosses;
    private static string deviceFailure = "";
    private const int DeviceRounds = 30, DiskBlocks = 8;

    /// <summary>Ein Schritt im Handler, auf der Tabelle des Programms: AHCI lesen, Bild zeigen, ICMP an das QEMU-Netz.</summary>
    private static ulong DeviceStep()
    {
        int round = deviceRound++;
        if (round >= DeviceRounds) return 1;
        try
        {
            switch (round % 3)
            {
                case 0:
                    IBlockDevice? device = disk;
                    if (device == null) return 0x100;
                    StorageLock.Run(() => device.ReadBlock(0, DiskBlocks, diskBuffer));
                    for (int i = 0; i < diskBuffer.Length; i++) if (diskBuffer[i] != diskReference[i]) return 0x101;
                    diskReads++;
                    break;
                case 1:
                    if (canvas == null) return 0x200;
                    canvas.Clear(unchecked((int)0xFF004080) | (round * 8 & 0xFF) << 16);
                    canvas.Display();
                    frames++;
                    break;
                default:
                    if (!Ping()) return 0x300;
                    pings++;
                    break;
            }
        }
        catch (Exception exception)
        {
            deviceFailure = exception.GetType().Name;
            return 0x400;
        }
        return 0;
    }

    private static bool Ping()
    {
        var client = new IcmpClient();
        try
        {
            client.Connect(new Address4(10, 0, 2, 2));
            var source = new EndPoint(Address4.Zero, 0);
            // Beim ersten Echo ist die ARP-Antwort oft noch nicht da (IPv4OutgoingBuffer: "ARP timeout", die Antwort kommt danach),
            // das Echo geht verloren. Deshalb bis zu fuenf Versuche je eine Sekunde. Jeder Versuch nach dem ersten zaehlt als
            // Verlust: Nur der Referenz-Ping darf welche haben (ARP), danach verlangt Device() 0, sonst verdeckte die
            // Wiederholung verlorene Echos auf der Tabelle des Programms.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) pingLosses++;
                client.SendEcho();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 1000)
                {
                    if (client.Receive(ref source, 0) >= 0) return true;
                    Thread.Sleep(10);
                }
            }
            return false;
        }
        finally { client.Close(); }
    }

    /// <summary>
    /// Netz, AHCI und Framebuffer, waehrend Programme mit eigenen Tabellen laufen: einmal aus dem Handler eines gebundenen
    /// Threads (Kernelcode auf der Tabelle des Programms, Geraeteinterrupts treffen diese Tabelle), einmal aus einem
    /// Kernelthread, waehrend ein isolate-Programm auf der Programm-CPU rechnet.
    /// </summary>
    private static (bool, string) Device()
    {
        deviceFailure = "";
        IBlockDevice? first = null;
        foreach (IBlockDevice device in StorageManager.Devices) { first = device; break; }
        disk = first;
        if (first != null)
        {
            diskReference = new byte[DiskBlocks * (int)first.BlockSize];
            diskBuffer = new byte[diskReference.Length];
            StorageLock.Run(() => first.ReadBlock(0, DiskBlocks, diskReference));
        }
        try { canvas = Canvas.GetFullScreen(); } catch (Exception exception) { deviceFailure = "canvas " + exception.GetType().Name; }
        bool network = NetworkManager.IsEnabled && NetworkManager.IsInitialized && NetworkManager.DeviceCount > 0;
        if (network && (NetworkManager.Primary.IPConfig == null || NetworkManager.Primary.IPConfig.Address.Equals(Address4.Zero)))
            IPConfig.Enable(new Address4(10, 0, 2, 15), new Address4(255, 255, 255, 0), new Address4(10, 0, 2, 2)); // QEMU user-Netz
        pingLosses = 0;
        bool reference = network && Ping();
        int referenceLosses = pingLosses;
        if (disk == null || canvas == null || !reference)
            return (false, "disk=" + (disk != null ? 1 : 0) + " canvas=" + (canvas != null ? 1 : 0) + " net=" + (network ? 1 : 0)
                + " ping_kernel_tables=" + (reference ? 1 : 0) + " ref_losses=" + referenceLosses + " " + deviceFailure);

        // 1) im Handler eines gebundenen Threads
        Space? created = Space.Create();
        if (created == null) return (false, "no space");
        Space space = created;
        deviceRound = diskReads = frames = pings = pingLosses = 0;
        ulong result = 0xDEAD;
        var worker = new Thread(() =>
        {
            Probe.PinTo(Probe.ProgramCpu);
            space.Bind();
            try { result = UserMode.Enter(Entry("device"), StackTop); }
            finally { space.Unbind(); }
        });
        worker.Start();
        bool joined = Probe.Join(worker, 300);
        space.Dispose();
        int handlerLosses = pingLosses;
        string inHandler = "ref_losses=" + referenceLosses + " | handler: result=" + Hex(result) + " joined=" + (joined ? 1 : 0) + " disk=" + diskReads
            + " frames=" + frames + " pings=" + pings + " ping_losses=" + handlerLosses + " " + deviceFailure;

        // 2) aus einem Kernelthread, waehrend ein Programm mit eigener Tabelle auf der Programm-CPU laeuft
        checks = 0;
        checkFailures = 0;
        Space? running = Space.Create();
        if (running == null) return (false, inHandler + " no space");
        var runner = new Runner(running, "isolate", 0x5555, Probe.ProgramCpu);
        bool progress = runner.WaitProgress();
        diskReads = frames = pings = pingLosses = 0;
        ulong step = 1;
        int rounds = 0;
        // Geraeterunden wiederholen, bis das Programm mindestens einmal geprueft hat (hoechstens 10 s): Unter TCG mit einer CPU
        // kam das Programm in den 30 Schritten einer Runde manchmal nicht zum Zug (program_checks=0, #633-Serie TCG smp1 1/3).
        var deadline = Stopwatch.StartNew();
        do
        {
            deviceRound = 0;
            step = 0;
            while (step == 0) step = DeviceStep();
            rounds++;
            if (step != 1) break;
            if (Volatile.Read(ref checks) == 0) Thread.Sleep(10); // dem Programm auf derselben CPU Zeit geben
        }
        while (Volatile.Read(ref checks) == 0 && deadline.ElapsedMilliseconds < 10000);
        bool kernelProgress = runner.WaitProgress();
        bool kernelJoined = runner.Stop();
        running.Dispose();
        long programChecks = Volatile.Read(ref checks);
        string inKernel = "kernel: step=" + Hex(step) + " rounds=" + rounds + " ms=" + deadline.ElapsedMilliseconds + " disk=" + diskReads
            + " frames=" + frames + " pings=" + pings + " ping_losses=" + pingLosses + " program_checks=" + programChecks
            + " check_failures=" + checkFailures + " bad=0x" + runner.Bad.ToString("X")
            + " progress=" + (progress ? 1 : 0) + "/" + (kernelProgress ? 1 : 0) + " joined=" + (kernelJoined ? 1 : 0);
        bool ok = joined && kernelJoined && result == 0 && step == 1 && progress && kernelProgress && checkFailures == 0 && runner.Bad == 0
            && programChecks > 0 && handlerLosses == 0 && pingLosses == 0;
        return (ok, inHandler + " | " + inKernel);
    }
}
#endif
