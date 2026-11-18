using System;

namespace ProjektZeh.Scheduling;

/// <summary>Wahl der CPU für einen bereiten Thread und Reihenfolge des Stehlens; reine Rechnung ohne Zustand.</summary>
internal static class ClassPlacement
{
    /// <summary>
    /// running[cpu]: Klasse des dort laufenden Threads, null = CPU frei. queued[cpu]: wartende Threads dort.
    /// Die erste zutreffende Regel gilt, bei mehreren Treffern die kleinste CPU.
    /// </summary>
    public static int SelectCpu(CpuTopology topology, ThreadClass threadClass, int lastCpu,
        ReadOnlySpan<bool> allowed, ReadOnlySpan<ThreadClass?> running, ReadOnlySpan<int> queued)
    {
        int count = topology.CpuCount;
        if (allowed.Length < count || running.Length < count || queued.Length < count)
            throw new ArgumentException("allowed, running und queued brauchen je einen Eintrag pro CPU.");
        if (lastCpu >= 0 && lastCpu < count && allowed[lastCpu] && Free(running, queued, lastCpu) && !SiblingRunsHigher(topology, running, threadClass, lastCpu)) return lastCpu;
        for (int cpu = 0; cpu < count; cpu++) if (allowed[cpu] && Free(running, queued, cpu) && SiblingsFree(topology, running, queued, cpu)) return cpu;
        for (int cpu = 0; cpu < count; cpu++) if (allowed[cpu] && Free(running, queued, cpu) && !SiblingRunsHigher(topology, running, threadClass, cpu)) return cpu;
        for (int cpu = 0; cpu < count; cpu++) if (allowed[cpu] && Free(running, queued, cpu)) return cpu;
        int best = -1, bestLoad = int.MaxValue;
        for (int cpu = 0; cpu < count; cpu++)
        {
            if (!allowed[cpu]) continue;
            int load = queued[cpu] + (running[cpu] != null ? 1 : 0);
            if (load < bestLoad) { best = cpu; bestLoad = load; }
        }
        return best >= 0 ? best : throw new ArgumentException("Keine CPU ist erlaubt.", nameof(allowed));
    }

    private static bool Free(ReadOnlySpan<ThreadClass?> running, ReadOnlySpan<int> queued, int cpu) => running[cpu] == null && queued[cpu] == 0;

    private static bool SiblingRunsHigher(CpuTopology topology, ReadOnlySpan<ThreadClass?> running, ThreadClass threadClass, int cpu)
    {
        foreach (int sibling in topology.SiblingsOf(cpu))
            if (running[sibling] is { } other && other < threadClass) return true;
        return false;
    }

    private static bool SiblingsFree(CpuTopology topology, ReadOnlySpan<ThreadClass?> running, ReadOnlySpan<int> queued, int cpu)
    {
        foreach (int sibling in topology.SiblingsOf(cpu)) if (!Free(running, queued, sibling)) return false;
        return true;
    }

    /// <summary>Füllt order mit allen anderen CPUs: erst Geschwister, dann derselbe Verbund, dann der Rest, je aufsteigend. Liefert die Anzahl.</summary>
    public static int StealOrder(CpuTopology topology, int cpu, Span<int> order)
    {
        int count = topology.CpuCount;
        if (order.Length < count - 1) throw new ArgumentException("order braucht einen Eintrag pro anderer CPU.", nameof(order));
        int length = 0;
        foreach (int sibling in topology.SiblingsOf(cpu)) order[length++] = sibling;
        for (int other = 0; other < count; other++)
            if (other != cpu && topology.CoreOf(other) != topology.CoreOf(cpu) && topology.PackageOf(other) == topology.PackageOf(cpu)) order[length++] = other;
        for (int other = 0; other < count; other++)
            if (other != cpu && topology.PackageOf(other) != topology.PackageOf(cpu)) order[length++] = other;
        return length;
    }
}
