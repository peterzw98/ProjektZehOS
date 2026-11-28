#if ZEHOS_LINUX_RUN_PROBE
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.FileSystem;
using ProjektZeh.Storage;
using ProjektZeh.Shell;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe Ladybird V3 (<c>-p:ZehOSLinuxRunProbe=true -p:ZehOSLinuxRunBundle=&lt;datei&gt;</c>): startet beim Boot jedes Programm
/// eines eingebetteten Bündels (<c>scripts/test-linux-run.py bundle</c>) über <see cref="CosmosLinuxRun"/> mit den Argumenten und der
/// Umgebung von <c>experiments/LinuxAbi/run-linux.py</c> und schreibt Ausgabe und Status seriell (<c>LINUX_RUN</c>-Zeilen). Danach hält
/// der Kernel an. Nur Testbau: Das Bündel liegt im Kernelabbild.
/// <para>
/// Dateien (V8): Wie <c>run-linux.py</c> ein frisches Verzeichnis je Programm nimmt, bekommt hier jedes Programm die Wurzel
/// <see cref="RootBase"/>/&lt;name&gt; auf der Datenplatte, wenn <c>/data</c> eingehängt ist (das Gastskript startet mit einem frischen
/// Image), sonst ein leeres Dateisystem im Arbeitsspeicher. Die Zeile <c>LINUX_RUN root</c> nennt, welches. Arbeitsverzeichnis ist
/// <see cref="ProbeCwd"/> darin.
/// </para>
/// <para>
/// V25: Einträge, deren Name mit "/" beginnt (<c>bundle --file /lib/ld-musl-x86_64.so.1=…</c>), sind keine Programme, sondern Dateien,
/// die vor jedem Programm an diesen Pfad seiner Wurzel kommen (Interpreter und Bibliotheken dynamisch gelinkter Programme).
/// </para>
/// </summary>
internal static unsafe partial class CosmosLinuxRunProbe
{
    private const uint Magic = 0x3152_4C5A; // "ZLR1"
    // 60 s: oom_loop braucht unter TCG (~2 s je Runde, 12 Runden) mehr als 20 s; das Limit fängt nur Hänger ab.
    private const int TimeoutSeconds = 60;
    private const string RootBase = "/data/linuxrun";

    /// <summary>
    /// Arbeitsverzeichnis jedes Programms: <c>run-linux.py</c> startet in einem frischen Unterordner, die erwarteten Ausgaben
    /// (<c>dirs</c>: leeres Listing von ".", getcwd endet auf "/d/e" nach mindestens einem Ordner davor) setzen das voraus.
    /// </summary>
    private const string ProbeCwd = "/w";

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

#pragma warning disable COSMOS0001 // UserPageTable gilt als Vorschau.
    private static long ShootdownCount() => Cosmos.Kernel.Core.Memory.UserPageTable.Shootdowns;

    private static long LongestShootdownMicroseconds() => Cosmos.Kernel.Core.Memory.UserPageTable.LongestShootdownMicroseconds;
#pragma warning restore COSMOS0001

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
            var files = new System.Collections.Generic.List<(string Path, byte[] Content)>();
            var programs = new System.Collections.Generic.List<(string Name, byte[] Elf)>();
            int at = 8;
            for (uint i = 0; i < entries; i++)
            {
                // Eintrag: Namenslänge (u32), Name (UTF-8), Größe (u32), Inhalt.
                int nameLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bundle.Slice(at)));
                string entryName = Encoding.UTF8.GetString(bundle.Slice(at + 4, nameLength));
                at += 4 + nameLength;
                int entrySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bundle.Slice(at)));
                byte[] content = bundle.Slice(at + 4, entrySize).ToArray();
                at += 4 + entrySize;
                if (entryName.StartsWith('/'))
                {
                    files.Add((entryName, content));
                }
                else
                {
                    programs.Add((entryName, content));
                }
            }

            foreach ((string name, byte[] elf) in programs)
            {
                int size = elf.Length;
                Write("begin " + name + " size=" + size);
                string? root = StorageLock.Run(() => VfsManager.TryGetMount("/data", out _)) ? RootBase + "/" + name : null;
                Write("root " + name + " " + (root ?? "memory"));

                // V19: Das Programm liegt auch als Datei in seiner Wurzel (/bin/<name>, neben dem Arbeitsverzeichnis), damit /proc/self/exe
                // auf es zeigt und es sich per posix_spawn oder execve selbst starten kann wie unter run-linux.py.
                string? exePath = null;
                if (root != null)
                {
                    string dir = root + "/bin";
                    StorageLock.Run(() =>
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(dir + "/" + name, elf);
                        return true;
                    });
                    exePath = "/bin/" + name;
                    foreach ((string path, byte[] content) in files)
                    {
                        string target = root + path;
                        StorageLock.Run(() =>
                        {
                            Directory.CreateDirectory(target.Substring(0, target.LastIndexOf('/')));
                            File.WriteAllBytes(target, content);
                            return true;
                        });
                    }
                }

                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                long shootdowns = ShootdownCount();
                CosmosLinuxRun.Outcome outcome = CosmosLinuxRun.Run(elf, [name, "a1", "b 2"], CosmosLinuxRun.Environment, new SerialOutput(), TimeoutSeconds,
                    root: root, cwd: ProbeCwd, exePath: exePath);
                Write(outcome.Ran
                    ? "status " + name + " " + outcome.Status + (outcome.Detail.Length > 0 ? " (" + outcome.Detail + ")" : string.Empty)
                    : "status " + name + " notrun (" + outcome.Detail + ")");
                // N4-S5: Laufzeit und TLB-Abschüsse je Programm (Entwurf, Risiken: längste Wartezeit je Lauf ausgeben). Die Zeile gehört
                // nicht zur verglichenen Ausgabe; test-linux-run.py zeigt sie im Protokoll. longest_us gilt seit dem Boot.
                long elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                Write("time " + name + " ms=" + elapsedMs + " shootdowns=" + (ShootdownCount() - shootdowns)
                    + " longest_us=" + LongestShootdownMicroseconds());
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
