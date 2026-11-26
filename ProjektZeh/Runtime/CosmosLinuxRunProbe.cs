#if ZEHOS_LINUX_RUN_PROBE
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Cosmos.Kernel.System.Diagnostics;
using ProjektZeh.Shell;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe Ladybird V3 (<c>-p:ZehOSLinuxRunProbe=true -p:ZehOSLinuxRunBundle=&lt;datei&gt;</c>): startet beim Boot jedes Programm
/// eines eingebetteten Bündels (<c>scripts/test-linux-run.py bundle</c>) über <see cref="CosmosLinuxRun"/> mit den Argumenten und der
/// Umgebung von <c>experiments/LinuxAbi/run-linux.py</c> und schreibt Ausgabe und Status seriell (<c>LINUX_RUN</c>-Zeilen). Danach hält
/// der Kernel an. Nur Testbau: Das Bündel liegt im Kernelabbild.
/// </summary>
internal static unsafe partial class CosmosLinuxRunProbe
{
    private const uint Magic = 0x3152_4C5A; // "ZLR1"
    private const int TimeoutSeconds = 20;

    [LibraryImport("*", EntryPoint = "zehos_linux_run_bundle_address"), SuppressGCTransition] private static partial ulong BundleAddress();
    [LibraryImport("*", EntryPoint = "zehos_linux_run_bundle_size"), SuppressGCTransition] private static partial ulong BundleSize();

    /// <summary>Gibt Zeilen des Programms seriell aus; das Terminal braucht der Testlauf nicht.</summary>
    private sealed class SerialOutput : ITextOutput
    {
        public void WriteLine(string text) => Write("out " + text);

        public void Clear()
        {
        }
    }

    private static void Write(string text) => Log.WriteString("LINUX_RUN " + text + "\n");

    /// <summary>Immer false: nach der Probe hält der Kernel an.</summary>
    internal static bool Run()
    {
        int count = 0;
        try
        {
            var bundle = new ReadOnlySpan<byte>((void*)BundleAddress(), checked((int)BundleSize()));
            if (bundle.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(bundle) != Magic)
            {
                Write("error bundle");
                return false;
            }

            uint entries = BinaryPrimitives.ReadUInt32LittleEndian(bundle.Slice(4));
            int at = 8;
            for (uint i = 0; i < entries; i++)
            {
                // Eintrag: Namenslänge (u32), Name (UTF-8), Größe (u32), Inhalt.
                int nameLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bundle.Slice(at)));
                string name = Encoding.UTF8.GetString(bundle.Slice(at + 4, nameLength));
                at += 4 + nameLength;
                int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bundle.Slice(at)));
                byte[] elf = bundle.Slice(at + 4, size).ToArray();
                at += 4 + size;

                Write("begin " + name + " size=" + size);
                CosmosLinuxRun.Outcome outcome = CosmosLinuxRun.Run(elf, [name, "a1", "b 2"], CosmosLinuxRun.Environment, new SerialOutput(), TimeoutSeconds);
                Write(outcome.Ran
                    ? "status " + name + " " + outcome.Status + (outcome.Detail.Length > 0 ? " (" + outcome.Detail + ")" : string.Empty)
                    : "status " + name + " notrun (" + outcome.Detail + ")");
                count++;
            }
        }
        catch (Exception exception)
        {
            Write("error " + exception.GetType().Name + ": " + exception.Message);
        }

        Write("done count=" + count);
        return false;
    }
}
#endif
