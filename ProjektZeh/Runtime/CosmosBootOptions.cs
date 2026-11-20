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

    public static string ConfigureWorkerCpus(CpuTopology topology)
    {
        bool[] selected = CpuSelection.Workers(topology, Current.Cpus, Current.SmtOff);
        var mask = CpuSet.Empty;
        for (uint cpu = 0; cpu < selected.Length; cpu++)
            if (selected[cpu]) mask = mask.With(cpu);

        mask = mask.Intersect(KernelCpus.Online);
        WorkerCpus = mask.IsEmpty ? CpuSet.Single(0) : mask;

        if (!topology.Measured) return "Hyperthreading: unbekannt";
        if (topology.CpuCount == topology.CoreCount) return "Hyperthreading: nicht vorhanden";
        return Current.SmtOff ? "Hyperthreading: abgeschaltet (zehos.smt=off)" : "Hyperthreading: genutzt";
    }

    /// <summary>
    /// Zuerst im Hauptthread aufrufen, vor jedem Threadstart. Der Hauptthread und neue Threads bleiben bis
    /// <see cref="MoveMainToWorkers"/> auf CPU 0. Explizit platzierte Worker verwenden später <see cref="WorkerCpus"/>.
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

    /// <summary>
    /// Nach <see cref="ConfigureWorkerCpus"/> im Hauptthread aufrufen (E7-A): Hauptschleife und Standardaffinität wechseln
    /// auf <see cref="WorkerCpus"/>, CPU 0 bleibt den Interrupts und den Cosmos-Dienstthreads. Mit <c>zehos.cpus=1</c> und
    /// auf einer Maschine mit einer CPU ist <see cref="WorkerCpus"/> CPU 0, dann ändert sich nichts. Recovery ruft das nicht auf.
    /// Gibt eine Protokollzeile zurück.
    /// </summary>
    public static string MoveMainToWorkers()
    {
        var workers = WorkerCpus;
        try
        {
            // Environment.ProcessorCount bleibt bei dem Wert, den CoreLib beim ersten Lesen nach Apply gemerkt hat (1);
            // ZehOS startet seine Threads selbst und nutzt den Threadpool nicht für Last.
            KernelCpus.DefaultAffinity = workers;
            // Der Wechsel wartet auf die Umschaltung; danach läuft die Hauptschleife nicht mehr dort, wo die Tastatur-,
            // Maus- und Tablet-Interrupts ankommen. Die Eingabepuffer sind dafür seit E4-D mit echten Sperren geschützt.
            ThreadAffinity.Current = workers;
            return $"CPUs online={KernelCpus.Online.Count}, Oberfläche und Worker auf {Describe(workers)}";
        }
        catch (InvalidOperationException exception)
        {
            // Ohne laufenden Scheduler bleibt alles auf CPU 0.
            return $"CPUs online={KernelCpus.Online.Count}, Oberfläche und Worker auf CPU 0 ({exception.Message})";
        }
    }

    private static string Describe(CpuSet set)
    {
        var text = new System.Text.StringBuilder();
        int count = KernelCpus.Count;
        for (uint cpu = 0; cpu < count; cpu++)
        {
            if (!set.Contains(cpu)) continue;
            text.Append(text.Length == 0 ? "CPU " : ",");
            text.Append(cpu);
        }
        return text.Length == 0 ? "-" : text.ToString();
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
