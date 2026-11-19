using System;
using System.Runtime.Intrinsics.X86;
using Cosmos.Kernel.Core.X64.Cpu;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Threading;

namespace ProjektZeh.Scheduling;

internal static class CosmosCpuTopologyReader
{
    public static CpuTopology Read(Action<string> log)
    {
        int count = KernelCpus.Count;
        try
        {
            if (!SchedulerDiagnostics.IsRunning) return Fallback("Scheduler läuft nicht.");
            var online = KernelCpus.Online;
            var apicIds = new uint[count];
            for (uint cpu = 0; cpu < count; cpu++)
            {
                if (!online.Contains(cpu)) return Fallback($"CPU {cpu} ist offline.");

                uint target = cpu;
                uint? apicId = null;
                string? error = null;
                var started = ThreadStarter.TryStart(() =>
                {
                    try
                    {
                        ThreadAffinity.Current = CpuSet.Single(target);
                        if (KernelCpus.Current == target) apicId = ReadApicId();
                        else error = "Affinität nicht wirksam.";
                    }
                    catch (Exception exception)
                    {
                        error = exception.Message;
                    }
                }, background: true);
                if (!started.IsOK()) return Fallback($"CPU {cpu}: {started.Status.Message}");
                if (!started.Value!.Join(2000)) return Fallback($"CPU {cpu}: Zeitlimit für die Messung erreicht.");
                if (apicId is not { } id) return Fallback($"CPU {cpu}: {error ?? "Keine APIC-ID gelesen."}");
                apicIds[cpu] = id;
            }

            var (smtShift, packageShift) = CpuTopologyDecoder.ReadShifts(X86Base.CpuId);
            if (count > 1 && packageShift == CpuTopologyDecoder.SinglePackage)
                return Fallback("CPUID meldet keine Kern- und Verbunddaten.");
            return CpuTopology.FromApicIds(apicIds, smtShift, packageShift);
        }
        catch (Exception exception)
        {
            return Fallback(exception.Message);
        }

        CpuTopology Fallback(string reason)
        {
            log($"CPU-Topologie konnte nicht ermittelt werden: {reason}");
            return CpuTopology.Flat(count);
        }
    }

    private static uint ReadApicId()
    {
        int maximumLeaf = X86Base.CpuId(0, 0).Eax;
        // Dieselben erweiterten Blätter wie beim Dekodieren der Kern- und Verbundbits verwenden.
        if (maximumLeaf >= 0x1F)
        {
            var topology = X86Base.CpuId(0x1F, 0);
            if ((topology.Ebx & 0xFFFF) != 0) return (uint)topology.Edx;
        }

        if (maximumLeaf >= 0x0B)
        {
            var topology = X86Base.CpuId(0x0B, 0);
            if ((topology.Ebx & 0xFFFF) != 0) return (uint)topology.Edx;
        }

        return LocalApic.GetId();
    }
}
