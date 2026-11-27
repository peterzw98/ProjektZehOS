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
            ulong heapStart = brkStart & ~(Page - 1);
            ulong heapEnd = (brk + Page - 1) & ~(Page - 1);
            foreach ((ulong start, ulong end, MapAccess access) in raw)
            {
                string name = string.Empty;
                bool shared = false;
                foreach (SharedRegion r in mem._regions)
                {
                    if (r.Start <= start && start < r.End)
                    {
                        name = r.Mem.Path;
                        shared = true;
                        break;
                    }
                }

                if (!shared && heapEnd > heapStart && start >= heapStart && start < heapEnd)
                {
                    name = "[heap]";
                }
                else if (!shared && stackTop != 0 && start < stackTop && stackTop <= end)
                {
                    name = "[stack]";
                }

                regions.Add(new LinuxProcRegion(start, end, access, shared, name));
            }
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
