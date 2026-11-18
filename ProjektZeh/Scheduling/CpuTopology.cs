using System;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Unveränderliche Zuordnung CPU-Index -> Kern und Verbund. Kerne und Verbünde sind dicht ab 0 nummeriert,
/// in der Reihenfolge ihres ersten Auftretens über aufsteigende CPU-Indizes.
/// </summary>
internal sealed class CpuTopology
{
    private readonly int[] core;
    private readonly int[] package;
    private readonly int[][] siblings;

    private CpuTopology(int[] core, int[] package, int coreCount)
    {
        this.core = core;
        this.package = package;
        CoreCount = coreCount;
        siblings = new int[core.Length][];
        for (int cpu = 0; cpu < core.Length; cpu++)
        {
            int count = 0;
            for (int other = 0; other < core.Length; other++) if (other != cpu && core[other] == core[cpu]) count++;
            siblings[cpu] = new int[count];
            count = 0;
            for (int other = 0; other < core.Length; other++) if (other != cpu && core[other] == core[cpu]) siblings[cpu][count++] = other;
        }
    }

    public int CpuCount => core.Length;
    public int CoreCount { get; }
    public int CoreOf(int cpu) => core[cpu];
    public int PackageOf(int cpu) => package[cpu];
    public ReadOnlySpan<int> SiblingsOf(int cpu) => siblings[cpu];

    /// <summary>Jede CPU ein eigener Kern, ein Verbund.</summary>
    public static CpuTopology Flat(int cpuCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuCount, 1);
        var ids = new uint[cpuCount];
        for (int cpu = 0; cpu < cpuCount; cpu++) ids[cpu] = (uint)cpu;
        return FromApicIds(ids, 0, CpuTopologyDecoder.SinglePackage);
    }

    public static CpuTopology FromApicIds(ReadOnlySpan<uint> apicIdByCpu, int smtShift, int packageShift)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(apicIdByCpu.Length, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(smtShift);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(smtShift, packageShift);
        var core = new int[apicIdByCpu.Length];
        var package = new int[apicIdByCpu.Length];
        var coreKeys = new uint[apicIdByCpu.Length];
        var packageKeys = new uint[apicIdByCpu.Length];
        int cores = 0, packages = 0;
        for (int cpu = 0; cpu < apicIdByCpu.Length; cpu++)
        {
            core[cpu] = DenseIndex(coreKeys, ref cores, Shift(apicIdByCpu[cpu], smtShift));
            package[cpu] = DenseIndex(packageKeys, ref packages, Shift(apicIdByCpu[cpu], packageShift));
        }
        return new CpuTopology(core, package, cores);
    }

    // Eine Verschiebung um 32 oder mehr wirkt in C# modulo 32; hier heißt sie "alle gleich".
    private static uint Shift(uint value, int shift) => shift >= 32 ? 0 : value >> shift;

    private static int DenseIndex(uint[] keys, ref int count, uint key)
    {
        for (int i = 0; i < count; i++) if (keys[i] == key) return i;
        keys[count] = key;
        return count++;
    }
}
