using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace ProjektZeh.Security;

internal static class CosmosSaltSource
{
    public static void Fill(byte[] bytes)
    {
        if (!X86Base.IsSupported) throw new CryptographicException("Keine gepruefte Hardwarezufallsquelle.");
        bool seed = (X86Base.CpuId(7, 0).Ebx & 0x40000) != 0;
        bool random = (X86Base.CpuId(1, 0).Ecx & 0x40000000) != 0;
        if (!seed && !random) throw new CryptographicException("RDSEED/RDRAND fehlt; Kennwortanlage gesperrt.");
        ulong previous = 0;
        for (int offset = 0; offset < bytes.Length; offset += 8)
        {
            ulong word = 0;
            bool success = false;
            for (int retry = 0; retry < 64 && !success; retry++)
            {
                int result = seed ? ReadSeed(out word) : ReadRandom(out word);
                success = result != 0 && word != 0 && word != ulong.MaxValue && word != previous;
            }
            if (!success) throw new CryptographicException("Hardwarezufallsquelle nicht bereit; Kennwortanlage gesperrt.");
            previous = word;
            for (int index = 0; index < 8 && offset + index < bytes.Length; index++) bytes[offset + index] = (byte)(word >> (index * 8));
        }
    }

    // Die geprueften Cosmos-3.0.94-Nativfunktionen liefern Carry-Status und genau ein 64-Bit-Wort.
    [DllImport("*", EntryPoint = "_native_cpu_rdseed64", ExactSpelling = true)]
    [SuppressGCTransition]
    private static extern int ReadSeed(out ulong value);

    [DllImport("*", EntryPoint = "_native_cpu_rdrand64", ExactSpelling = true)]
    [SuppressGCTransition]
    private static extern int ReadRandom(out ulong value);
}
