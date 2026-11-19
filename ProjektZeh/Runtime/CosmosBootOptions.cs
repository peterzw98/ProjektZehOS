using System;
using Cosmos.Kernel.System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>Liest die Kernel-Kommandozeile und begrenzt die CPUs, auf denen Threads laufen.</summary>
internal static unsafe class CosmosBootOptions
{
    /// <summary>Die geparsten Optionen; vor <see cref="Apply"/> alle leer.</summary>
    public static BootOptions Current { get; private set; }

    /// <summary>
    /// Zuerst im Hauptthread aufrufen, vor jedem Threadstart. Etappe 5: setzt immer die Affinität des Hauptthreads und die
    /// Standardaffinität neuer Threads auf CPU 0, unabhängig von den Optionen (die Optionen werden nur gelesen).
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
            return $"CPUs online={KernelCpus.Count}, genutzt=1 (zehos.cpus={(Current.Cpus?.ToString() ?? "-")}, Etappe 5: immer CPU 0)";
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
