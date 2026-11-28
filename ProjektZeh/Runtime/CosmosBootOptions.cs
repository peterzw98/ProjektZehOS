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
        if (Current.TlbTimeoutMs is int tlbTimeout)
        {
            // Vor dem ersten Linux-Lauf, also vor jedem Abschuss; der Wert ist ein einzelnes int (Volatile in Cosmos).
#pragma warning disable COSMOS0001 // UserPageTable gilt als Vorschau.
            Cosmos.Kernel.Core.Memory.UserPageTable.ShootdownTimeoutMs = tlbTimeout;
#pragma warning restore COSMOS0001
        }
        try
        {
            // Die Standardaffinität zuerst, vor den Threadstarts. Environment.ProcessorCount beeinflusst das nicht mehr: CoreLib hat den
            // Wert beim ersten Lesen gemerkt, und das geschieht vor jedem ZehOS-Code (Probe, Issue #662).
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
            // Environment.ProcessorCount ändert sich hier nicht: CoreLib hat den Wert schon vor Apply gemerkt (online-CPUs minus CPU 0,
            // auf 4 CPUs 3, auf 1 CPU 1). ZehOS startet seine Threads selbst und nutzt den Threadpool nicht für Last (nicht geprüft).
            KernelCpus.DefaultAffinity = workers;
            // Der Wechsel wartet auf die Umschaltung; danach läuft die Hauptschleife nicht mehr dort, wo die Tastatur-,
            // Maus- und Tablet-Interrupts ankommen. Die Eingabepuffer sind dafür seit E4-D mit echten Sperren geschützt.
            ThreadAffinity.Current = workers;
            return $"CPUs online={KernelCpus.Online.Count}, Oberfläche und Worker auf {Describe(workers)}";
        }
        catch (InvalidOperationException exception)
        {
            // Ohne laufenden Scheduler bleibt alles auf CPU 0. Scheitert erst der Wechsel der Hauptschleife, wäre die
            // Standardaffinität schon umgestellt und neue Threads lägen anders als die Bootzeile meldet; deshalb zurück auf CPU 0.
            try
            {
                KernelCpus.DefaultAffinity = CpuSet.Single(0);
            }
            catch (InvalidOperationException)
            {
                // Ohne Scheduler gibt es keine Standardaffinität zum Zurücksetzen.
            }
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
