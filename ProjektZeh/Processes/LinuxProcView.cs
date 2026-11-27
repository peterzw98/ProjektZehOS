using System.Collections.Generic;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes;

/// <summary>
/// V29: Sicht eines Prozesses auf die Prozesse seines Laufs für /proc (LinuxProcFiles.cs). Gesucht wird wie bei kill im Baum ab dem ersten
/// Prozess (<see cref="Find"/>); den Zustand des Ziels liest der Schnappschuss nacheinander unter Threadsperre (Threads, Signale) und
/// Speichersperre (Karte, Frames), nie unter beiden zugleich. Der Verteiler ruft unter der Dateisperre; die steht in der Reihenfolge vor
/// beiden, die Kinderlisten nimmt <see cref="Children"/> nur kurz.
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxProcView
{
    public int ProcessCount
    {
        get
        {
            var all = new List<LinuxUserProcess>();
            Collect(RunRoot(), all);
            return all.Count;
        }
    }

    public LinuxProcSnapshot? ProcSnapshot(int pid)
    {
        if (!TryFindInRun(pid, out LinuxUserProcess? target, out bool zombie))
        {
            return null;
        }

        return target!.Describe(zombie, running: ReferenceEquals(target, this));
    }

    public string? ExePathOf(int pid) => TryFindInRun(pid, out LinuxUserProcess? target, out _) ? target!.ExePath : null;

    private LinuxUserProcess RunRoot()
    {
        LinuxUserProcess root = this;
        while (root.Parent != null)
        {
            root = root.Parent;
        }

        return root;
    }

    private bool TryFindInRun(int pid, out LinuxUserProcess? target, out bool zombie)
    {
        zombie = false;
        target = this;
        if (pid != Pid && !Find(RunRoot(), pid, out target, out zombie))
        {
            return false;
        }

        // Nach execve gilt das neue Abbild (dieselbe PID), wie bei kill.
        while (true)
        {
            LinuxUserProcess? next;
            lock (target!._threadGate)
            {
                next = target._execSuccessor;
            }

            if (next is null)
            {
                return true;
            }

            target = next;
        }
    }

    private LinuxProcSnapshot Describe(bool zombie, bool running)
    {
        LinuxProcFiles.SignalSets(Signals, out ulong ignored, out ulong caught);
        int threads = zombie ? 1 : LiveThreads;
        ulong pending = PendingSignals;
        LinuxUserProcess mem = _vforkOf ?? this; // vfork-Kind: Adressraum des Elternprozesses
        var regions = new List<LinuxProcRegion>();
        long anon, shmem;
        ulong brkStart, brk, stackTop, stackSize;
        lock (mem._memGate)
        {
            var raw = new List<(ulong Start, ulong End, MapAccess Access)>();
            mem._map.CopyRegionsTo(raw);
            brkStart = mem._brkStart;
            brk = mem._brk;
            stackTop = mem._stackTop;
            stackSize = mem._stackSize;
            shmem = mem._shared.Count;
            anon = mem._frames.Count - shmem; // geteilte Seiten stehen auch in _frames (LinuxUserProcessShared.cs)
            var shared = new List<(ulong Start, ulong End, string Name)>(mem._regions.Count);
            foreach (SharedRegion r in mem._regions)
            {
                shared.Add((r.Start, r.End, r.Mem.Path));
            }

            // AddressSpaceMap verschmilzt Nachbarn gleicher Rechte; NameRegions trennt sie wieder an Heap-, Stack- und memfd-Grenzen.
            ulong stackLow = stackTop > stackSize ? stackTop - stackSize : 0;
            LinuxProcFiles.NameRegions(raw, shared, brkStart & ~(Page - 1), (brk + Page - 1) & ~(Page - 1), stackLow, stackTop, regions);
        }

        return new LinuxProcSnapshot
        {
            Pid = Pid,
            ParentPid = ParentPid,
            GroupPid = GroupPid,
            Name = LinuxProcFiles.CommOf(ExePath),
            State = zombie ? 'Z' : running ? 'R' : 'S',
            Uid = Uid,
            Gid = Gid,
            Threads = threads,
            SigPending = pending,
            SigBlocked = Signals.Blocked,
            SigIgnored = ignored,
            SigCaught = caught,
            RssAnonPages = zombie ? 0 : anon,
            RssShmemPages = zombie ? 0 : shmem,
            BrkStart = brkStart,
            Brk = brk,
            StackTop = stackTop,
            StackSize = stackSize,
            Regions = zombie ? new List<LinuxProcRegion>() : regions,
        };
    }
}
