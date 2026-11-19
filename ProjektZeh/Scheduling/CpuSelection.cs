using System;

namespace ProjektZeh.Scheduling;

internal static class CpuSelection
{
    public static bool[] Workers(CpuTopology topology, int? cpuLimit, bool smtOff)
    {
        var workers = new bool[topology.CpuCount];
        int limit = Math.Min(cpuLimit ?? workers.Length, workers.Length);
        bool any = false;
        for (int cpu = 1; cpu < limit; cpu++)
        {
            if (smtOff && HasEarlierSibling(topology, cpu)) continue;
            workers[cpu] = true;
            any = true;
        }

        workers[0] = !any;
        return workers;
    }

    private static bool HasEarlierSibling(CpuTopology topology, int cpu)
    {
        foreach (int sibling in topology.SiblingsOf(cpu))
            if (sibling < cpu) return true;
        return false;
    }
}
