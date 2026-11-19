using System;
using Cosmos.Kernel.System.Threading;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Runtime;

/// <summary>Liest die Kernel-Kommandozeile und begrenzt die CPUs, auf denen Threads laufen.</summary>
internal static unsafe class CosmosBootOptions
{
    /// <summary>Die geparsten Optionen; vor <see cref="Apply"/> alle leer.</summary>
    public static BootOptions Current { get; private set; }

    public static CpuSet WorkerCpus { get; private set; } = CpuSet.Single(0);

    public static void ConfigureWorkerCpus(CpuTopology topology)
    {
        // zehos.smt=off wird erst mit E7-D aktiviert.
        bool[] selected = CpuSelection.Workers(topology, Current.Cpus, smtOff: false);
        var mask = CpuSet.Empty;
        for (uint cpu = 0; cpu < selected.Length; cpu++)
            if (selected[cpu]) mask = mask.With(cpu);

        mask = mask.Intersect(KernelCpus.Online);
        WorkerCpus = mask.IsEmpty ? CpuSet.Single(0) : mask;
    }

    /// <summary>
    /// Zuerst im Hauptthread aufrufen, vor jedem Threadstart. Der Hauptthread und neue Threads bleiben standardmäßig
    /// auf CPU 0. Explizit platzierte Worker verwenden später <see cref="WorkerCpus"/>.
    /// Gibt eine Protokollzeile zurück, weil das Systemprotokoll hier noch nicht bekannt sein muss.
    /// </summary>
    public static string Apply()
    {
        Current = BootOptions.Parse(ReadCommandLine());
        try
        {
            // Die Standardaffinität zuerst: CoreLib merkt sich Environment.ProcessorCount beim ersten Lesen.
            KernelCpus.DefaultAffinity = CpuSet.Single(0);
            ThreadAffinity.Current = CpuSet.Single(0);
            return $"CPUs online={KernelCpus.Online.Count}, Hauptthread und Standardaffinität: CPU 0 (zehos.cpus={Current.Cpus?.ToString() ?? "-"})";
        }
        catch (InvalidOperationException exception)
        {
            // Ohne laufenden Scheduler (Recovery-Fälle) bleibt es bei der Protokollzeile.
            return $"CPUs: keine Begrenzung ohne Scheduler ({exception.Message})";
        }
    }

    private static string? ReadCommandLine()
    {
        byte* cmdline = Cosmos.Kernel.Boot.Limine.Limine.Cmdline;
        if (cmdline == null)
            return null;
        // Nullterminiert, ASCII; die Länge ist begrenzt, falls die Terminierung fehlt.
        var chars = new char[512];
        int length = 0;
        while (length < chars.Length && cmdline[length] != 0)
        {
            chars[length] = (char)cmdline[length];
            length++;
        }
        return new string(chars, 0, length);
    }
}
