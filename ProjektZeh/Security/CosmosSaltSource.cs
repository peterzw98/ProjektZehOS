using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace ProjektZeh.Security;

internal static class CosmosSaltSource
{
    public static void Fill(byte[] bytes)
    {
        if (!X86Base.IsSupported) throw new HardwareRandomUnavailableException();
        var (seed, random) = HardwareRandom.Detect(X86Base.CpuId);
        HardwareRandom.Fill(bytes, seed, random, ReadSeed, ReadRandom);
    }

    // Die geprueften Cosmos-3.0.94-Nativfunktionen liefern Carry-Status und genau ein 64-Bit-Wort.
    [DllImport("*", EntryPoint = "_native_cpu_rdseed64", ExactSpelling = true)]
    [SuppressGCTransition]
    private static extern int ReadSeed(out ulong value);

    [DllImport("*", EntryPoint = "_native_cpu_rdrand64", ExactSpelling = true)]
    [SuppressGCTransition]
    private static extern int ReadRandom(out ulong value);
}
