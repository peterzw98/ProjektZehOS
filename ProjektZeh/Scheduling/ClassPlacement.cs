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

    /// <summary>
    /// #674 V2: Ziel für den freiwilligen Umzug des Threads, der auf cpu läuft, oder -1. Umgezogen wird nur, wenn ein Geschwister
    /// von cpu einen Thread rechnet und ein erlaubter anderer Kern ganz frei ist (alle seine CPUs ohne Laufenden und ohne Wartende);
    /// dann die kleinste freie CPU dort. Gegen Pendeln: Der Thread muss mindestens minOnCpu auf cpu gelaufen und seit dem letzten
    /// Umzug minSinceMove vergangen sein (Einheit beliebig, gleich für alle vier Werte). Nach einem Umzug hat der alte Kern ein freies
    /// Geschwister; zurück ginge es erst, wenn der neue Kern wieder voll wird, und auch dann höchstens einmal je minSinceMove.
    /// </summary>
    public static int SelectMoveTarget(CpuTopology topology, int cpu, ReadOnlySpan<bool> allowed, ReadOnlySpan<ThreadClass?> running,
        ReadOnlySpan<int> queued, long onCpu, long sinceMove, long minOnCpu, long minSinceMove)
    {
        int count = topology.CpuCount;
        if (allowed.Length < count || running.Length < count || queued.Length < count)
            throw new ArgumentException("allowed, running und queued brauchen je einen Eintrag pro CPU.");
        if (cpu < 0 || cpu >= count || onCpu < minOnCpu || sinceMove < minSinceMove) return -1;
        bool siblingBusy = false;
        foreach (int sibling in topology.SiblingsOf(cpu)) siblingBusy |= running[sibling] != null;
        if (!siblingBusy) return -1;
        int ownCore = topology.CoreOf(cpu);
        for (int other = 0; other < count; other++)
            if (allowed[other] && topology.CoreOf(other) != ownCore && Free(running, queued, other) && SiblingsFree(topology, running, queued, other))
                return other;
        return -1;
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
