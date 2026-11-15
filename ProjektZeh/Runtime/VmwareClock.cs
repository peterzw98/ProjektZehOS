namespace ProjektZeh.Runtime;

internal static class VmwareClock
{
    public static bool TryDecode(uint maximumLeaf, uint vendorB, uint vendorC, uint vendorD,
        uint tscKhz, uint busKhz, out long tscHz, out uint lapicTicksPerMs)
    {
        tscHz = 0;
        lapicTicksPerMs = 0;
        if (maximumLeaf < 0x40000010 || vendorB != 0x61774d56 || vendorC != 0x4d566572 || vendorD != 0x65726177)
            return false;
        if (tscKhz == 0 || busKhz < 16) return false;

        tscHz = (long)tscKhz * 1000;
        // Cosmos programmiert den LAPIC mit Teiler 16; kHz sind bereits Ticks pro Millisekunde.
        lapicTicksPerMs = busKhz / 16;
        return true;
    }
}
