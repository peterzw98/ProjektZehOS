using System;
using System.Security.Cryptography;

namespace ProjektZeh.Security;

/// <summary>Die CPU bietet weder RDSEED noch RDRAND; ohne andere gepruefte Quelle sind Salts und TLS gesperrt.</summary>
internal sealed class HardwareRandomUnavailableException : CryptographicException
{
    public HardwareRandomUnavailableException() : base("RDSEED/RDRAND fehlt.") { }
}

/// <summary>Plattformunabhaengige Auswahl und Pruefung der CPU-Zufallsbefehle; CosmosSaltSource liefert CPUID und Befehle.</summary>
internal static class HardwareRandom
{
    internal delegate int ReadWord(out ulong value);

    private const int Retries = 64;

    /// <summary>Liest Blatt 7 (RDSEED) nur, wenn CPUID es anbietet; sonst liefert die CPU Werte eines anderen Blatts.</summary>
    internal static (bool Seed, bool Random) Detect(Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> cpuId)
    {
        int maximumLeaf = cpuId(0, 0).Eax;
        bool random = maximumLeaf >= 1 && (cpuId(1, 0).Ecx & 0x40000000) != 0;
        bool seed = maximumLeaf >= 7 && (cpuId(7, 0).Ebx & 0x40000) != 0;
        return (seed, random);
    }

    /// <summary>Fuellt bytes wortweise; versagt RDSEED trotz Wiederholungen, uebernimmt RDRAND den Rest des Aufrufs.</summary>
    internal static void Fill(byte[] bytes, bool seed, bool random, ReadWord readSeed, ReadWord readRandom)
    {
        if (!seed && !random) throw new HardwareRandomUnavailableException();
        ulong previous = 0;
        for (int offset = 0; offset < bytes.Length; offset += 8)
        {
            ulong word = 0;
            bool success = seed && TryRead(readSeed, previous, out word);
            // Ein erschoepftes RDSEED nicht fuer jedes weitere Wort erneut 64-mal versuchen (VM-Exits unter KVM).
            if (!success) seed = false;
            if (!success && random) success = TryRead(readRandom, previous, out word);
            if (!success) throw new CryptographicException("Hardwarezufallsquelle nicht bereit.");
            previous = word;
            for (int index = 0; index < 8 && offset + index < bytes.Length; index++) bytes[offset + index] = (byte)(word >> (index * 8));
        }
    }

    // Carry-Fehler sowie 0, alle Bits gesetzt und Wiederholungen gelten als defekte Werte bekannter Firmwarefehler.
    private static bool TryRead(ReadWord read, ulong previous, out ulong word)
    {
        word = 0;
        for (int retry = 0; retry < Retries; retry++)
        {
            if (read(out word) != 0 && word != 0 && word != ulong.MaxValue && word != previous) return true;
        }
        return false;
    }
}
