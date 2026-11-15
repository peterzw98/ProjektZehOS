// Der PIT-/TSC-Rueckfall stammt aus Cosmos 3.0.94 (5d0b953); BSD-3-Clause, siehe ThirdParty/CosmosTiming/.
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.Core.X64.Cpu;
using Serial = Cosmos.Kernel.System.Diagnostics.Log;

namespace ProjektZeh.Runtime;

// shortcut: Cosmos 3.0.94 kalibriert ueber PIT/MMIO; nach einer korrigierten Cosmos-Version entfernen.
internal static class CosmosVmwareClock
{
    public static bool TryRead(out long tscHz, out uint lapicTicksPerMs)
    {
        var vendor = X86Base.CpuId(0x40000000, 0);
        // Unbekannte Hypervisor-Leaves duerfen nicht als VMware-Frequenzen interpretiert werden.
        if ((uint)vendor.Eax < 0x40000010 || (uint)vendor.Ebx != 0x61774d56 ||
            (uint)vendor.Ecx != 0x4d566572 || (uint)vendor.Edx != 0x65726177)
        {
            tscHz = 0;
            lapicTicksPerMs = 0;
            return false;
        }
        var frequency = X86Base.CpuId(0x40000010, 0);
        return VmwareClock.TryDecode((uint)vendor.Eax, (uint)vendor.Ebx, (uint)vendor.Ecx, (uint)vendor.Edx,
            (uint)frequency.Eax, (uint)frequency.Ebx, out tscHz, out lapicTicksPerMs);
    }
}

#pragma warning disable COSMOS0001, NAOT0007 // Versionsgebundene Korrektur vor Initialisierung von Scheduler, Uhr und Geraeten.
[Plug(typeof(LocalApic))]
public static class CosmosVmwareLapicPlug
{
    private const string Apic = "Cosmos.Kernel.Core.X64.Cpu.LocalApic, Cosmos.Kernel.Core.X64";
    [PlugMember]
    public static void CalibrateTimer()
    {
        if (!LocalApic.IsInitialized)
        {
            Serial.WriteString("[LocalAPIC] Cannot calibrate timer - APIC not initialized\n");
            return;
        }

        uint ticks;
        if (CosmosVmwareClock.TryRead(out _, out ticks))
            Serial.WriteString("[LocalAPIC] VMware CPUID timer frequency\n");
        else
        {
            // Ausserhalb VMware bleibt die bisherige Cosmos-3.0.94-Kalibrierung erhalten.
            WriteRegister(null, 0x3e0, 3);
            WritePort(null, 0x43, 0x30);
            WritePort(null, 0x40, 0x9c);
            WritePort(null, 0x40, 0x2e);
            WriteRegister(null, 0x320, 0x10000);
            WriteRegister(null, 0x380, uint.MaxValue);
            ushort lastCount = 11932;
            while (true)
            {
                WritePort(null, 0x43, 0);
                byte lo = ReadPort(null, 0x40);
                byte hi = ReadPort(null, 0x40);
                ushort count = (ushort)(lo | (hi << 8));
                if (count > lastCount || count == 0) break;
                lastCount = count;
            }
            ticks = (uint.MaxValue - ReadRegister(null, 0x390)) / 10;
            WriteRegister(null, 0x380, 0);
        }
        TicksPerMs(null) = ticks;
        TimerCalibrated(null) = true;
        Serial.WriteString("[LocalAPIC] Timer calibrated: ");
        Serial.WriteNumber(ticks);
        Serial.WriteString(" ticks/ms\n");
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_ticksPerMs")]
    private static extern ref uint TicksPerMs([UnsafeAccessorType(Apic)] object? owner);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_timerCalibrated")]
    private static extern ref bool TimerCalibrated([UnsafeAccessorType(Apic)] object? owner);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Read")]
    private static extern uint ReadRegister([UnsafeAccessorType(Apic)] object? owner, uint offset);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Write")]
    private static extern void WriteRegister([UnsafeAccessorType(Apic)] object? owner, uint offset, uint value);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Read8")]
    private static extern byte ReadPort([UnsafeAccessorType("Cosmos.Kernel.Core.Native+IO, Cosmos.Kernel.Core")] object? owner, ushort port);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Write8")]
    private static extern void WritePort([UnsafeAccessorType("Cosmos.Kernel.Core.Native+IO, Cosmos.Kernel.Core")] object? owner, ushort port, byte value);
}

[Plug(TargetName = "Cosmos.Kernel.Core.X64.Cpu.X64CpuOps")]
public static class CosmosVmwareTscPlug
{
    private const string CpuOps = "Cosmos.Kernel.Core.X64.Cpu.X64CpuOps, Cosmos.Kernel.Core.X64";

    [PlugMember]
    public static void CalibrateTsc()
    {
        if (!LocalApic.IsTimerCalibrated)
        {
            Serial.WriteString("[TSC] LAPIC timer not calibrated\n");
            return;
        }
        if (!CosmosVmwareClock.TryRead(out long frequency, out _))
        {
            ulong start = ReadTsc(null);
            LocalApic.Wait(10);
            frequency = (long)((ReadTsc(null) - start) / 10 * 1000);
        }
        else Serial.WriteString("[TSC] VMware CPUID frequency\n");
        Frequency(null) = frequency;
        Calibrated(null) = true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ReadTSC")]
    private static extern ulong ReadTsc([UnsafeAccessorType(CpuOps)] object? owner);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<TscFrequency>k__BackingField")]
    private static extern ref long Frequency([UnsafeAccessorType(CpuOps)] object? owner);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<IsTscCalibrated>k__BackingField")]
    private static extern ref bool Calibrated([UnsafeAccessorType(CpuOps)] object? owner);
}
#pragma warning restore COSMOS0001, NAOT0007
