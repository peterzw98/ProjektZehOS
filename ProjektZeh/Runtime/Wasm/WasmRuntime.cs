#if ZEHOS_WASM
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ProjektZeh.Security;

namespace ProjektZeh.Runtime.Wasm;

internal enum WasmStatus { Ok = 0, NoMemory = 1, InvalidModule = 2, LoadFailed = 3, EntryMissing = 4, Trap = 5, OutOfGas = 6, LimitFailed = 7 }

/// <summary>Dienste, die eine laufende Wasm-Anwendung vom Kernel erhaelt.</summary>
internal interface IWasmSession
{
    /// <summary>Ordner, auf den die Anwendung lesend als "/" zugreift; null sperrt Dateizugriffe.</summary>
    string? Root { get; }
    void Write(int fd, ReadOnlySpan<byte> data);
    void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test);
    int PollKey();
    bool TryReadEvent(out WasmInputEvent value);
    bool ShouldExit { get; }
    /// <summary>Uhr der Anwendung in Millisekunden; steht still, solange die Sitzung pausiert.</summary>
    uint TicksMilliseconds { get; }
}

/// <summary>
/// Fuehrt Wasm-Module ueber den statisch gebundenen wasm3-Interpreter aus. Alle Laeufe teilen sich einen
/// langlebigen Worker mit festem Stack, weil Cosmos 3.0.94 Stacks beendeter Threads nicht freigibt und das
/// native wasm3-Budget (d_m3MaxNativeStack, siehe ProjektZeh.csproj) auf diesen Stack abgestimmt ist.
/// </summary>
internal static unsafe class WasmRuntime
{
    public const int ThreadStackBytes = 8 * 1024 * 1024;
    private const uint WasmStackBytes = 1024 * 1024;
    private const ulong MemoryLimitBytes = 256ul * 1024 * 1024;
    private const int MaximumOpenFiles = 16;

    private static readonly object gate = new();
    private static readonly Queue<Action> jobs = new();
    private static readonly Dictionary<int, FileStream> files = [];
    private static Thread? worker;
    private static IWasmSession? current;
    private static int nextHandle;
    private static uint[] indexedFrame = [];

    /// <summary>Summe der Wartezeit des letzten Laufs in Millisekunden; trennt Rechen- von Wartezeit bei Messungen.</summary>
    public static long WaitedMilliseconds { get; private set; }

    /// <summary>true, solange ein Lauf aussteht oder laeuft; der gemeinsame Worker fuehrt nur einen zur Zeit aus.</summary>
    public static bool IsBusy { get { lock (gate) return pending > 0; } }
    private static int pending;

    /// <summary>Stellt einen Lauf in die Warteschlange; completed laeuft danach auf dem Worker.</summary>
    public static void Enqueue(byte[] module, string entry, ulong gas, IWasmSession session,
        Action<WasmStatus, int, string> completed)
    {
        ArgumentNullException.ThrowIfNull(module);
        Enqueue(() => (module, null), entry, gas, session, completed);
    }

    /// <summary>
    /// Wie oben, laedt das Modul aber erst auf dem Worker, etwa nach einer Integritaetspruefung grosser Paketdateien.
    /// Liefert load kein Modul, endet der Lauf mit LoadFailed und der Fehlermeldung.
    /// </summary>
    public static void Enqueue(Func<(byte[]? Module, string? Error)> load, string entry, ulong gas, IWasmSession session,
        Action<WasmStatus, int, string> completed)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(completed);
        lock (gate)
        {
            pending++;
            jobs.Enqueue(() =>
            {
                WasmStatus status = WasmStatus.LoadFailed;
                int result = 0;
                string message;
                (byte[]? Module, string? Error) loaded;
                // Ein Ladefehler darf den gemeinsamen Worker nicht beenden.
                try { loaded = load(); }
                catch (Exception exception) { loaded = (null, exception.Message); }
                if (loaded.Module != null) status = Execute(loaded.Module, entry, gas, session, out result, out message);
                else message = loaded.Error ?? "Modul nicht geladen.";
                // Vor completed freigeben: das Fenster darf danach sofort eine neue App starten (IsBusy).
                lock (gate) pending--;
                completed(status, result, message);
            });
            if (worker == null)
            {
                worker = new Thread(Work, ThreadStackBytes);
                worker.Start();
            }
            Monitor.Pulse(gate);
        }
    }

    /// <summary>Fuehrt einen Lauf aus und wartet auf das Ergebnis; nur ausserhalb des Workers aufrufen.</summary>
    public static WasmStatus Run(byte[] module, string entry, ulong gas, IWasmSession session, out int result, out string message)
    {
        using var done = new ManualResetEvent(false);
        WasmStatus status = WasmStatus.Trap;
        int value = 0;
        string text = string.Empty;
        Enqueue(module, entry, gas, session, (s, r, m) => { status = s; value = r; text = m; done.Set(); });
        done.WaitOne();
        result = value;
        message = text;
        return status;
    }

    private static void Work()
    {
        while (true)
        {
            Action job;
            lock (gate)
            {
                while (jobs.Count == 0) Monitor.Wait(gate);
                job = jobs.Dequeue();
            }
            job();
            // Apps wie Doom lesen zweistellige MiB ueber FileStream; ohne Sammeln blieb dieser Muell im Desktop
            // ueber mehrere Starts liegen (gemessen: rund 30 MiB je Doom-Lauf), obwohl er freigebbar ist.
            GC.Collect();
            // Eine Diagnosezeile darf den gemeinsamen Worker nicht beenden; danach haengen sonst alle weiteren Laeufe.
            try { ReportMemory(); }
            catch (Exception) { /* Nur die Speicherzeile fehlt; Gasttests melden dann zu wenige Werte. */ }
        }
    }

    // Seriell nach dem Sammeln: erst dann ist der Lauf vollstaendig aufgeraeumt. Gasttests vergleichen damit den
    // Speicher ueber viele Laeufe; eine Messung direkt nach "APP <id> beendet" enthielt noch ungesammelten Muell.
    private static void ReportMemory()
    {
        var physical = CosmosSystemInfo.PhysicalMemory();
        Cosmos.Kernel.System.Diagnostics.Log.WriteString(
            $"WASM Speicher frei={physical.Free / 1024} KiB heap={GC.GetGCMemoryInfo().HeapSizeBytes / 1024} KiB\n");
    }

    private static WasmStatus Execute(byte[] module, string entry, ulong gas, IWasmSession session, out int result, out string message)
    {
        byte[] entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
        byte* text = stackalloc byte[256];
        int value = 0;
        int code;
        current = session;
        WaitedMilliseconds = 0;
        try
        {
            fixed (byte* bytes = module)
            fixed (byte* name = entryBytes)
            {
                code = NativeRun(bytes, (uint)module.Length, name, gas, WasmStackBytes, MemoryLimitBytes, &value, text, 256);
            }
        }
        finally
        {
            current = null;
            // Die C-Schicht schliesst offene Dateien selbst; dies faengt nur Reste nach Fehlern ab.
            foreach (var stream in files.Values) stream.Dispose();
            files.Clear();
        }
        result = value;
        message = Marshal.PtrToStringAnsi((IntPtr)text) ?? string.Empty;
        return (WasmStatus)code;
    }

    [DllImport("*", EntryPoint = "zehos_wasm_run", ExactSpelling = true)]
    private static extern int NativeRun(byte* bytes, uint length, byte* entry, ulong gas, uint stackBytes, ulong memoryBytes,
        int* result, byte* message, uint messageLength);

    // Hostrueckrufe der C-Schicht. Ausnahmen duerfen die native Grenze nicht ueberqueren.

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_write")]
    private static void HostWrite(int fd, byte* data, uint length)
    {
        try { current?.Write(fd, new ReadOnlySpan<byte>(data, (int)Math.Min(length, 65536u))); }
        catch (Exception) { /* Ausgabefehler beenden die App nicht; die Daten gehen verloren. */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_time_ns")]
    private static ulong HostTime(int clockId)
    {
        // WASI-Uhr 0 ist Realzeit (UTC vom Hypervisor), alle anderen messen monoton ab Boot.
        if (clockId == 0) return (ulong)(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        return (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_random")]
    private static void HostRandom(byte* buffer, uint length)
    {
        // In kleinen Bloecken: eine Anfrage ueber die ganze lineare Memory darf keine riesige Allokation (und damit eine
        // Ausnahme ueber die native Grenze) ausloesen.
        var bytes = new byte[Math.Min(length, 4096u)];
        for (uint offset = 0; offset < length; offset += (uint)bytes.Length)
        {
            int count = (int)Math.Min((uint)bytes.Length, length - offset);
            try { CosmosSaltSource.Fill(bytes); }
            catch (Exception) { Array.Clear(bytes); /* Ohne Hardwarezufall bleibt der Puffer null; Apps duerfen das nicht fuer Geheimnisse nutzen. */ }
            bytes.AsSpan(0, count).CopyTo(new Span<byte>(buffer + offset, count));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_open")]
    private static int HostOpen(byte* path, uint length)
    {
        if (files.Count >= MaximumOpenFiles) return -1;
        try
        {
            string? full = ResolvePackagePath(path, length);
            if (full == null) return -1;
            var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            int handle = ++nextHandle;
            files[handle] = stream;
            return handle;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }

    // C prueft die relativen Komponenten; dieser gemeinsame Schritt sichert den Bezug zum Paketordner.
    private static string? ResolvePackagePath(byte* path, uint length)
    {
        string? root = current?.Root;
        if (root == null) return null;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(Path.Combine(root, Encoding.UTF8.GetString(path, (int)length)));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Pfad ausserhalb des Pakets.");
        return full;
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_stat")]
    private static int HostStat(byte* path, uint length, ulong* size, int* type)
    {
        try
        {
            string? full = ResolvePackagePath(path, length);
            if (full == null) return 2; // WASI EACCES
            FileAttributes attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return 2;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            *size = directory ? 0 : (ulong)new FileInfo(full).Length;
            *type = directory ? 3 : 4;
            return 0;
        }
        catch (FileNotFoundException) { return 44; } // WASI ENOENT
        catch (DirectoryNotFoundException) { return 44; }
        catch (UnauthorizedAccessException) { return 2; }
        catch (Exception) { return 29; } // WASI EIO; keine Ausnahme ueber die native Grenze.
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_file_size")]
    private static long HostFileSize(int handle)
    {
        try { return files.TryGetValue(handle, out var stream) ? stream.Length : -1; }
        catch (IOException) { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_file_read")]
    private static int HostFileRead(int handle, ulong offset, byte* buffer, uint length)
    {
        if (!files.TryGetValue(handle, out var stream)) return -1;
        try
        {
            stream.Position = (long)offset;
            return stream.Read(new Span<byte>(buffer, (int)length));
        }
        catch (IOException) { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_close")]
    private static void HostClose(int handle)
    {
        if (files.Remove(handle, out var stream)) stream.Dispose();
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_present")]
    private static void HostPresent(uint* pixels, int width, int height, int tic, int test) =>
        current?.Present(new ReadOnlySpan<uint>(pixels, width * height), width, height, tic, test != 0);

    // Palettenbilder werden nativ in einen wiederverwendeten Puffer umgerechnet; im Interpreter kostete das
    // auf dem Host fast die Haelfte der Bildzeit von Doom.
    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_present_indexed")]
    private static void HostPresentIndexed(byte* indices, uint* palette, int width, int height, int tic)
    {
        int count = width * height;
        if (indexedFrame.Length < count) indexedFrame = new uint[count];
        for (int index = 0; index < count; index++) indexedFrame[index] = palette[indices[index]] & 0x00FFFFFF;
        current?.Present(indexedFrame.AsSpan(0, count), width, height, tic, false);
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_ticks_ms")]
    private static uint HostTicks() => current?.TicksMilliseconds ?? (uint)(Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_sleep_ms")]
    private static void HostSleep(uint milliseconds)
    {
        long start = Stopwatch.GetTimestamp();
        Thread.Sleep((int)milliseconds);
        WaitedMilliseconds += (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_poll_key")]
    private static int HostPollKey() => current?.PollKey() ?? -1;

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_poll_event")]
    private static int HostPollEvent(int* fields)
    {
        try
        {
            if (current == null || !current.TryReadEvent(out var value)) return 0;
            fields[0] = (int)value.Type;
            fields[1] = value.Code;
            fields[2] = value.X;
            fields[3] = value.Y;
            return 1;
        }
        catch (Exception) { return -1; }
    }

    // Ohne Sitzung gibt es niemanden mehr, fuer den die App laufen koennte.
    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_should_exit")]
    private static int HostShouldExit() => current == null || current.ShouldExit ? 1 : 0;
}
#endif
