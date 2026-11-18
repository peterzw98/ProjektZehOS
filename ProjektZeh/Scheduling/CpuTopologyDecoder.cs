using System;
using System.Numerics;

namespace ProjektZeh.Scheduling;

/// <summary>Liest aus CPUID, wie viele Bits der APIC-ID auf Hardware-Threads eines Kerns und auf Kerne eines Verbunds entfallen.</summary>
internal static class CpuTopologyDecoder
{
    /// <summary>packageShift ab diesem Wert: alle CPUs bilden einen Verbund.</summary>
    public const int SinglePackage = 32;
    private const int AmdEbx = 0x68747541, AmdEdx = 0x69746E65, AmdEcx = 0x444D4163; // "AuthenticAMD"

    /// <summary>cpuId(leaf, subleaf) wie in Security/HardwareRandom.cs. Die erste anwendbare Quelle gilt, sonst (0, SinglePackage).</summary>
    public static (int SmtShift, int PackageShift) ReadShifts(Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> cpuId)
    {
        int maximumLeaf = cpuId(0, 0).Eax;
        // Erweiterte Topologieblätter: 0x1F (mit Modulebene) vor 0x0B. Ebx = 0 meldet ältere virtuelle CPUs ohne Daten.
        foreach (int leaf in (ReadOnlySpan<int>)[0x1F, 0x0B])
        {
            if (maximumLeaf < leaf || (cpuId(leaf, 0).Ebx & 0xFFFF) == 0) continue;
            int smt = 0, package = 0;
            for (int subleaf = 0; subleaf < 8; subleaf++)
            {
                var level = cpuId(leaf, subleaf);
                int type = (level.Ecx >> 8) & 0xFF;
                if (type == 0) break;
                int shift = level.Eax & 0x1F;
                if (type == 1) smt = shift;
                package = shift;
            }
            return (smt, package);
        }
        var vendor = cpuId(0, 0);
        if (vendor.Ebx == AmdEbx && vendor.Edx == AmdEdx && vendor.Ecx == AmdEcx
            && (uint)cpuId(unchecked((int)0x80000000), 0).Eax >= 0x8000001E)
        {
            int threadsPerCore = ((cpuId(unchecked((int)0x8000001E), 0).Ebx >> 8) & 0xFF) + 1;
            int size = cpuId(unchecked((int)0x80000008), 0).Ecx;
            int idSize = (size >> 12) & 0xF;
            return (CeilLog2((uint)threadsPerCore), idSize != 0 ? idSize : CeilLog2((uint)((size & 0xFF) + 1)));
        }
        if (maximumLeaf >= 4 && (cpuId(1, 0).Edx & (1 << 28)) != 0)
        {
            int logical = (cpuId(1, 0).Ebx >> 16) & 0xFF;
            int cores = ((cpuId(4, 0).Eax >> 26) & 0x3F) + 1;
            int package = CeilLog2((uint)logical);
            return (Math.Max(0, package - CeilLog2((uint)cores)), package);
        }
        return (0, SinglePackage);
    }

    private static int CeilLog2(uint value) => value <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount(value - 1);
}
