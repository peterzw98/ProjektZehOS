#if ZEHOS_WASM
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.X509;
using ProjektZeh.Network;
using ProjektZeh.Security;

namespace ProjektZeh.Runtime.Wasm;

internal enum WasmStatus { Ok = 0, NoMemory = 1, InvalidModule = 2, LoadFailed = 3, EntryMissing = 4, Trap = 5, OutOfGas = 6, LimitFailed = 7 }

/// <summary>Dienste, die eine laufende Wasm-Anwendung vom Kernel erhaelt.</summary>
internal interface IWasmSession
{
    /// <summary>Ordner, auf den die Anwendung lesend als "/" zugreift; null sperrt Dateizugriffe.</summary>
    string? Root { get; }
    bool AllowsNetwork { get; }
    void Write(int fd, ReadOnlySpan<byte> data);
    void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test);
    int PollKey();
    bool TryReadEvent(out WasmInputEvent value);
    /// <summary>Faengt den Zeiger ein oder gibt ihn frei; false, wenn die Sitzung das nicht kann.</summary>
    bool SetPointerCapture(bool enabled);
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
    private static WasmNetworkSession? network;
    private static IList<X509Certificate>? tlsAnchors;
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
            StartWorkerLocked();
            Monitor.Pulse(gate);
        }
    }

    /// <summary>
    /// Legt den Worker beim Start an, solange der Heap noch frei von Netzmuell ist: Der 8-MiB-Stack kommt aus dem
    /// nativen Heap, dessen Fehlschlag keine Collection ausloest; Cosmos 3.0.97 benutzt dann einen Nullzeiger.
    /// Erst nach dem Scheduler aufrufen, sonst laeuft der Thread nie.
    /// </summary>
    public static void StartWorker()
    {
        lock (gate) StartWorkerLocked();
    }

    private static void StartWorkerLocked()
    {
        if (worker != null) return;
        worker = new Thread(Work, ThreadStackBytes);
        worker.Start();
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
        network = new WasmNetworkSession(new CosmosWasmNetworkConnector(CreateTlsClient), session.AllowsNetwork);
        WaitedMilliseconds = 0;
        try
        {
            fixed (byte* bytes = module)
            fixed (byte* name = entryBytes)
            {
                code = NativeRun(bytes, (uint)module.Length, name, gas, WasmStackBytes, MemoryLimitBytes, &value, text, 256);
            }
        }
        catch (Exception exception)
        {
            code = (int)WasmStatus.Trap;
            value = 0;
            byte[] failure = Encoding.ASCII.GetBytes(exception.Message);
            int count = Math.Min(failure.Length, 255);
            for (int i = 0; i < count; i++) text[i] = failure[i];
            text[count] = 0;
        }
        current = null;
        int networkCleanup = network.CloseAll();
        network = null;
        foreach (var stream in files.Values)
        {
            try { stream.Dispose(); }
            catch (Exception) { code = (int)WasmStatus.Trap; }
        }
        files.Clear();
        if (networkCleanup != WasmNetworkError.Success) code = (int)WasmStatus.Trap;
        result = value;
        message = Marshal.PtrToStringAnsi((IntPtr)text) ?? string.Empty;
        return (WasmStatus)code;
    }

    [DllImport("*", EntryPoint = "zehos_wasm_run", ExactSpelling = true)]
    private static extern int NativeRun(byte* bytes, uint length, byte* entry, ulong gas, uint stackBytes, ulong memoryBytes,
        int* result, byte* message, uint messageLength);

    // Das Bundle wird nur einmal je Kernellauf geparst. Es laeuft hoechstens eine Paket-App, daher greift nur ein
    // Thread auf den Zwischenspeicher zu.
    private static TlsClient CreateTlsClient(string host) => CosmosWasmNetworkConnector.CreateAppClient(host, CosmosSaltSource.Fill,
        () => tlsAnchors ??= TlsTransport.LoadAnchors(TlsRoots.Load()), DateTime.UtcNow);

    // Hostrueckrufe der C-Schicht. Ausnahmen duerfen die native Grenze nicht ueberqueren.

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_net_open")]
    private static int HostNetOpen(byte* host, uint length, int port, int flags, int* handle)
    {
        try
        {
            if (length > 253) return WasmNetworkError.Invalid;
            var bytes = new ReadOnlySpan<byte>(host, (int)length);
            foreach (byte value in bytes) if (value > 127) return WasmNetworkError.Invalid;
            int status = network?.Open(Encoding.ASCII.GetString(bytes), port, flags, out *handle) ?? WasmNetworkError.Access;
            return status;
        }
        catch (Exception) { return WasmNetworkError.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_net_status")]
    private static int HostNetStatus(int handle)
    {
        try { return network?.Status(handle) ?? WasmNetworkError.BadHandle; }
        catch (Exception) { return WasmNetworkError.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_net_send")]
    private static int HostNetSend(int handle, byte* data, uint length, int* count)
    {
        try
        {
            if (length > WasmNetworkSession.MaximumTransferBytes) return WasmNetworkError.Invalid;
            return network?.Send(handle, new ReadOnlySpan<byte>(data, (int)length), out *count) ?? WasmNetworkError.BadHandle;
        }
        catch (Exception) { return WasmNetworkError.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_net_recv")]
    private static int HostNetReceive(int handle, byte* data, uint length, int* count)
    {
        try
        {
            if (length > WasmNetworkSession.MaximumTransferBytes) return WasmNetworkError.Invalid;
            return network?.Receive(handle, new Span<byte>(data, (int)length), out *count) ?? WasmNetworkError.BadHandle;
        }
        catch (Exception) { return WasmNetworkError.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_net_close")]
    private static int HostNetClose(int handle)
    {
        try { return network?.Close(handle) ?? WasmNetworkError.BadHandle; }
        catch (Exception) { return WasmNetworkError.Io; }
    }

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
    private static int HostRandom(byte* buffer, uint length)
    {
        // C hat buffer/length gegen die lineare Memory (hoechstens MemoryLimitBytes) geprueft; length passt in int.
        return WasmRandom.Fill(new Span<byte>(buffer, (int)length), CosmosSaltSource.Fill);
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

    private static string? ResolvePackagePath(byte* path, uint length)
    {
        string? root = current?.Root;
        return root == null ? null : WasmPackagePaths.Resolve(root, Encoding.UTF8.GetString(path, (int)length));
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_stat")]
    private static int HostStat(byte* path, uint length, ulong* size, int* type)
    {
        try
        {
            int result = WasmPackagePaths.Stat(current?.Root, Encoding.UTF8.GetString(path, (int)length), out ulong bytes, out int kind);
            if (result == WasmPackagePaths.Success)
            {
                *size = bytes;
                *type = kind;
            }
            return result;
        }
        catch (Exception) { return WasmPackagePaths.Io; } // keine Ausnahme ueber die native Grenze.
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

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_pointer_capture")]
    private static int HostPointerCapture(int enabled)
    {
        try { return current != null && current.SetPointerCapture(enabled != 0) ? 0 : -1; }
        catch (Exception) { return -1; }
    }

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
