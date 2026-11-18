#if ZEHOS_WASM
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.X509;
using ProjektZeh.Graphics.Raster;
using ProjektZeh.Network;
using ProjektZeh.Scheduling;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>
/// Fuehrt Wasm-Module ueber den statisch gebundenen wasm3-Interpreter aus, mit .aot-Bytes und ohne Gasbudget
/// optional nativ ueber WAMR-AOT (-p:ZehOSWasmAot=true). Alle Laeufe teilen sich einen langlebigen Worker mit festem
/// Stack, weil Cosmos 3.0.94 Stacks beendeter Threads nicht freigibt und die nativen Stackgrenzen von wasm3
/// (d_m3MaxNativeStack, siehe ProjektZeh.csproj) und WAMR (zehos_host_wamr.c) auf diesen Stack abgestimmt sind.
/// </summary>
internal static unsafe partial class WasmRuntime
{
    public const int ThreadStackBytes = 8 * 1024 * 1024;
    private const uint WasmStackBytes = 1024 * 1024;
    private const ulong MemoryLimitBytes = 256ul * 1024 * 1024;

    private static readonly object gate = new();
    private static readonly WasmJobQueue jobs = new();
    private static Thread? worker;
    private static Action<WasmStatus, int, string>? running;
    private static int isolatedRuns;
    // Jede Isolation laesst Thread und 8-MiB-Stack liegen, und ein neuer Stack scheitert nach Dateilast leicht (E4.0 1G).
    private const int MaximumIsolatedRuns = 3;
    private static bool workerRetired;
    private static bool cleaningUp;
    private static WasmNetworkSession? network;
    private static IList<X509Certificate>? tlsAnchors;
    private static WasmSaveStore? save;

    /// <summary>Waechter fuer Symlinks und Mountgrenzen des App-Speichers; der Kernel setzt ihn beim Start.</summary>
    public static Func<string, bool, ResultErr>? SavePathGuard { get; set; }
    /// <summary>Freier Platz des Datentraegers fuer die ENOSPC-Vorpruefung.</summary>
    public static Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? SaveDiskSpace { get; set; }
    /// <summary>Migrations- (4.5) bzw. Energiesperre (7b): solange true, melden schreibende Aufrufe EBUSY.</summary>
    public static Func<bool> SaveWritesBlocked { get; set; } = () => false;
    /// <summary>Pfadvergleich des Profils (Legacy-FAT ohne, ext2-Root mit Gross-/Kleinschreibung).</summary>
    public static StringComparison SaveComparison { get; set; } = StringComparison.OrdinalIgnoreCase;

    /// <summary>Summe der Wartezeit (sleep_ms) des laufenden bzw. letzten Laufs in Stopwatch-Ticks; trennt Rechen- von Wartezeit.</summary>
    public static long WaitedTicks { get; private set; }

    // Aus der Tick-Summe statt je Aufruf abgerundet: viele kurze Schlafaufrufe gingen sonst verloren.
    public static long WaitedMilliseconds => WaitedTicks * 1000 / Stopwatch.Frequency;

    /// <summary>true, solange ein Lauf aussteht oder laeuft; der gemeinsame Worker fuehrt nur einen zur Zeit aus.</summary>
    public static bool IsBusy { get { lock (gate) return pending > 0; } }
    private static int pending;

    /// <summary>
    /// Stellt einen Lauf in die Warteschlange; completed laeuft danach auf dem Worker. Mit aot (Bytes eines .aot zum
    /// selben Modul) laeuft der Einstieg nativ unter WAMR, sofern WasmEngineSelection das zulaesst; sonst das .wasm.
    /// </summary>
    public static void Enqueue(byte[] module, string entry, ulong gas, IWasmSession session,
        Action<WasmStatus, int, string> completed, byte[]? aot = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        EnqueueLoaded(() => (module, aot, null), entry, gas, session, completed, null);
    }

    /// <summary>
    /// Wie oben, laedt Modul und optionales .aot aber erst auf dem Worker, etwa nach einer Integritaetspruefung grosser
    /// Paketdateien. Liefert load kein Modul, endet der Lauf mit LoadFailed und der Fehlermeldung. Mit name meldet der Lauf
    /// seriell die gewaehlte Laufzeit und einen Rueckfall auf wasm3 (Gasttests fragen beides ab).
    /// </summary>
    public static void Enqueue(Func<(byte[]? Module, byte[]? Aot, string? Error)> load, string entry, ulong gas, IWasmSession session,
        Action<WasmStatus, int, string> completed, string? name = null) =>
        EnqueueLoaded(load, entry, gas, session, completed, name);

    private static void EnqueueLoaded(Func<(byte[]? Module, byte[]? Aot, string? Error)> load, string entry, ulong gas,
        IWasmSession session, Action<WasmStatus, int, string> completed, string? name)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(completed);
        bool retired;
        string? notStarted = null;
        lock (gate)
        {
            retired = workerRetired;
            if (!retired)
            {
                // Ohne Worker bliebe der Lauf ewig in der Warteschlange (Run wartet dann unbegrenzt).
                var started = StartWorkerLocked();
                if (!started.IsOK()) notStarted = "Wasm-Worker nicht gestartet: " + started.Message;
                else EnqueueLockedJob(load, entry, gas, session, completed, name);
            }
        }
        if (retired) completed(WasmStatus.CpuFault, 0, RetiredMessage);
        else if (notStarted != null) completed(WasmStatus.LoadFailed, 0, notStarted);
    }

    // Unter gate gerufen.
    private static void EnqueueLockedJob(Func<(byte[]? Module, byte[]? Aot, string? Error)> load, string entry, ulong gas,
        IWasmSession session, Action<WasmStatus, int, string> completed, string? name)
    {
        pending++;
        jobs.Enqueue(() =>
        {
            // Fuer PollFaultIsolation: endet der Worker in einer CPU-Ausnahme, meldet die Hauptschleife das Ende.
            lock (gate) running = completed;
            WasmStatus status = WasmStatus.LoadFailed;
            int result = 0;
            string message;
            (byte[]? Module, byte[]? Aot, string? Error) loaded;
            // Ein Ladefehler darf den gemeinsamen Worker nicht beenden.
            try { loaded = load(); }
            catch (Exception exception) { loaded = (null, null, exception.Message); }
            if (loaded.Module != null) status = Execute(loaded.Module, loaded.Aot, entry, gas, session, name, out result, out message);
            else message = loaded.Error ?? "Modul nicht geladen.";
            // Vor completed freigeben: das Fenster darf danach sofort eine neue App starten (IsBusy).
            lock (gate)
            {
                pending--;
                running = null;
            }
            completed(status, result, message);
        }, completed);
        Monitor.Pulse(gate);
    }

    /// <summary>
    /// Legt den Worker beim Start an, solange der Heap noch frei von Netzmuell ist: Der 8-MiB-Stack kommt aus dem
    /// nativen Heap, dessen Fehlschlag keine Collection ausloest; Cosmos 3.0.97 benutzt dann einen Nullzeiger.
    /// Erst nach dem Scheduler aufrufen, sonst laeuft der Thread nie. Scheitert die Stackprobe (ThreadStarter), bleibt der
    /// Worker aus; der naechste Lauf versucht es erneut und endet sonst mit LoadFailed statt zu haengen.
    /// </summary>
    public static ResultErr StartWorker()
    {
        lock (gate) return StartWorkerLocked();
    }

    /// <summary>
    /// Gibt die Ressourcen eines Laufs frei: Sitzung, App-Speicher, Netz und offene Paketdateien. Nach einer isolierten
    /// CPU-Ausnahme ruft sie die Hauptschleife, weil der aufgegebene Worker sein Ende nie erreicht. Liefert false, wenn
    /// ein Teil nicht sauber schloss.
    /// </summary>
    private static bool ReleaseRunResources()
    {
        bool clean = true;
        current = null;
        presentFailureReported = false;
        // Offene Dateien im App-Speicher werden geschrieben und ihre Puffer freigegeben, auch nach einem Trap.
        try { save?.CloseAll(); }
        catch (Exception) { clean = false; }
        save = null;
        if (network != null && network.CloseAll() != WasmNetworkError.Success) clean = false;
        network = null;
        foreach (var stream in files.Values)
        {
            try { StorageLock.Run(stream.Dispose); }
            catch (Exception) { clean = false; }
        }
        files.Clear();
        return clean;
    }

    /// <summary>Zahl der Laeufe, die seit dem Start durch eine isolierte CPU-Ausnahme endeten.</summary>
    public static int IsolatedRuns => Volatile.Read(ref isolatedRuns);

    /// <summary>
    /// Hauptschleife (Roadmap 12.4-I1): Hat eine CPU-Ausnahme den Worker aufgegeben, beendet sie den Lauf von aussen,
    /// gibt seine Ressourcen frei und startet einen neuen Worker fuer die Warteschlange. Thread, 8-MiB-Stack und der
    /// native Speicher der Instanz bleiben bis zum Neustart belegt.
    /// </summary>
    public static void PollFaultIsolation()
    {
        int thread = ProjektZeh.Runtime.FaultIsolation.TakeIsolated();
        if (thread < 0) return;
        Action<WasmStatus, int, string>? completed;
        string? restart = null;
        bool retiredNow = false;
        List<Action<WasmStatus, int, string>>? dropped = null;
        lock (gate)
        {
            completed = running;
            running = null;
            if (completed != null) pending--;
            worker = null;
            // Bis zum Ende des Aufraeumens startet niemand einen Ersatzworker (auch nicht EnqueueLoaded): ein neuer Lauf
            // setzte sonst current/save/network/files, und ReleaseRunResources schloesse dessen Ressourcen (Review pink P2-2).
            cleaningUp = true;
            if (Volatile.Read(ref isolatedRuns) + 1 >= MaximumIsolatedRuns)
            {
                workerRetired = true;
                retiredNow = true;
                restart = $"nach {MaximumIsolatedRuns} CPU-Fehlern kein weiterer Worker";
            }
        }
        // Raeumt die Strukturen des aufgegebenen Workers (save, files, network) von hier aus ab. Sicher, weil er nie wieder
        // laeuft: Cosmos reiht ihn nur ueber IScheduler.OnThreadReady/OnThreadYield neu ein, und
        // CosmosRoundRobinScheduler.OnThreadReady verwirft Threads in FaultIsolation.Quarantine.
        // catch statt finally: Auch wenn das Aufraeumen unerwartet wirft, erhalten Lauf und Warteschlange ihr Ende und
        // die Isolation wird gezaehlt (Review senior).
        bool clean;
        try { clean = ReleaseRunResources(); }
        catch (Exception) { clean = false; }
        lock (gate)
        {
            cleaningUp = false;
            if (!retiredNow)
            {
                // Ein gescheiterter Ersatzstart (Stackprobe) ist voruebergehend: kein Ruhestand, die naechste App
                // startet den Worker ueber EnqueueLoaded erneut.
                var started = StartWorkerLocked();
                if (!started.IsOK()) restart = started.Message;
            }
            // Ohne Worker laeuft die Warteschlange nie an; ihre Fenster erhalten ein Ende statt "beschaeftigt".
            if (worker == null)
            {
                dropped = jobs.Drain();
                pending -= dropped.Count;
            }
        }
        Interlocked.Increment(ref isolatedRuns);
        Cosmos.Kernel.System.Diagnostics.Log.WriteString(
            $"WASM CPU-Ausnahme im App-Code, Worker-Thread {thread} aufgegeben (Ressourcen {(clean ? "frei" : "nicht ganz frei")})" +
            (restart == null ? "; neuer Worker gestartet. Neustart empfohlen.\n"
                : retiredNow ? $"; kein neuer Worker ({restart}). Paket-Apps erst nach einem Neustart des Systems.\n"
                : $"; kein neuer Worker ({restart}). Die naechste App versucht den Start erneut.\n"));
        if (completed != null) NotifyEnded(completed, WasmStatus.CpuFault, CpuFaultMessage);
        if (dropped == null) return;
        foreach (var waiting in dropped)
        {
            if (retiredNow) NotifyEnded(waiting, WasmStatus.CpuFault, RetiredMessage);
            else NotifyEnded(waiting, WasmStatus.LoadFailed, "Wasm-Worker nicht gestartet: " + restart);
        }
    }

    // Nach einer Isolation laufen die Rueckrufe in der Hauptschleife statt im Worker; eine Ausnahme daraus darf weder die
    // Sitzung beenden noch die uebrigen Fenster ohne Ende lassen (Review pink P3-4).
    private static void NotifyEnded(Action<WasmStatus, int, string> completed, WasmStatus status, string message)
    {
        try { completed(status, 0, message); }
        catch (Exception exception)
        {
            Cosmos.Kernel.System.Diagnostics.Log.WriteString($"WASM Rueckruf nach CPU-Ausnahme fehlgeschlagen: {exception.GetType().Name}\n");
        }
    }

    private const string CpuFaultMessage = "CPU-Fehler im App-Code; die App wurde beendet. Neustart empfohlen.";
    private const string RetiredMessage = "Nach CPU-Fehlern starten Paket-Apps erst nach einem Neustart des Systems wieder.";

#if ZEHOS_ISOLATION_PROBE
    internal static void ProbeEnqueue(Action job)
    {
        lock (gate)
        {
            jobs.Enqueue(job, null);
            StartWorkerLocked();
            Monitor.Pulse(gate);
        }
    }

#endif
    private static ResultErr StartWorkerLocked()
    {
        if (worker != null) return ResultErr.CreateSuccess();
        // Waehrend PollFaultIsolation aufraeumt, bleibt der Lauf eingereiht; der Ersatzworker startet direkt danach.
        if (cleaningUp) return ResultErr.CreateSuccess();
        if (workerRetired) return ResultErr.CreateFailure(ResultCode.Failure, RetiredMessage);
        var started = ThreadStarter.TryStart(Work, ThreadStackBytes);
        if (!started.IsOK()) return started.Status;
        worker = started.Value;
        return ResultErr.CreateSuccess();
    }

    /// <summary>Fuehrt einen Lauf aus und wartet auf das Ergebnis; nur ausserhalb des Workers aufrufen.</summary>
    public static WasmStatus Run(byte[] module, string entry, ulong gas, IWasmSession session, out int result, out string message,
        byte[]? aot = null)
    {
        using var done = new ManualResetEvent(false);
        WasmStatus status = WasmStatus.Trap;
        int value = 0;
        string text = string.Empty;
        Enqueue(module, entry, gas, session, (s, r, m) => { status = s; value = r; text = m; done.Set(); }, aot);
        done.WaitOne();
        result = value;
        message = text;
        return status;
    }

    private static void Work()
    {
        ProjektZeh.Runtime.FaultIsolation.RegisterWorker();
        while (true)
        {
            Action job;
            lock (gate)
            {
                while (jobs.Count == 0) Monitor.Wait(gate);
                job = jobs.Dequeue();
            }
            // Auch vorher sammeln: Cosmos 3.0.97 sammelt erst, wenn der Seitenallokator erschoepft ist, und eine grosse
            // zusammenhaengende Allokation liefert bei vollem Heap NULL statt zu sammeln. Nach einer Online-Installation
            // (TLS, Katalog, Paketdateien) lagen rund 2 Mio. tote Objekte im Heap; ClassiCube scheiterte dann am
            // Wachstum des Wasm-Speichers ("Out of VRAM", Exitcode 1).
            long collectStart = Stopwatch.GetTimestamp();
            GC.Collect();
            // Wie bei ReportMemory: eine Diagnosezeile darf den gemeinsamen Worker nicht beenden.
            try
            {
                Cosmos.Kernel.System.Diagnostics.Log.WriteString(
                    $"WASM GC vor Lauf {(long)Stopwatch.GetElapsedTime(collectStart).TotalMilliseconds} ms\n");
            }
            catch (Exception) { /* Nur die Dauerzeile fehlt. */ }
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

    private static WasmStatus Execute(byte[] module, byte[]? aot, string entry, ulong gas, IWasmSession session,
        string? name, out int result, out string message)
    {
        byte[] entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
        byte* text = stackalloc byte[256];
        int value = 0;
        int code;
        current = session;
        network = new WasmNetworkSession(new CosmosWasmNetworkConnector(CreateTlsClient), session.AllowsNetwork);
        save = session.SaveRoot is { } saveRoot ? new WasmSaveStore(saveRoot, () => AppStorageGate.IsBlocked || SaveWritesBlocked(), SavePathGuard,
            SaveDiskSpace, disk: operation => AppStorageGate.Run(operation, WasiErrno.Busy), comparison: SaveComparison) : null;
        WaitedTicks = 0;
        try
        {
            fixed (byte* entryName = entryBytes)
            {
                bool native = WasmEngineSelection.Select(WasmEngineSelection.AotBuiltIn, aot, gas) == WasmEngine.Aot;
                ReportEngine(name, native ? "aot " + WasmEngineSelection.AotAbi : "wasm3");
                int instantiated = 0;
                code = native ? RunAot(aot!, entryName, &value, text, &instantiated) : RunWasm3(module, entryName, gas, &value, text);
                // Scheitert das .aot vor dem Instanziieren (Format, Laden, Speicherobergrenze), laeuft dieselbe App unter
                // wasm3. Ohne .wasm (AOT-Probe) bleibt der AOT-Status stehen.
                if (native && WasmEngineSelection.FallsBackToWasm3(module.Length > 0, code != (int)WasmStatus.Ok, instantiated != 0))
                {
                    ReportEngine(name, $"wasm3 AOT-Rueckfall: {(WasmStatus)code} {Marshal.PtrToStringAnsi((IntPtr)text)}");
                    value = 0;
                    code = RunWasm3(module, entryName, gas, &value, text);
                }
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
        if (!ReleaseRunResources()) code = (int)WasmStatus.Trap;
        result = value;
        message = Marshal.PtrToStringAnsi((IntPtr)text) ?? string.Empty;
        return (WasmStatus)code;
    }

    private static int RunWasm3(byte[] module, byte* entry, ulong gas, int* value, byte* text)
    {
        fixed (byte* bytes = module)
        {
            ProjektZeh.Runtime.FaultIsolation.EnterNativeRun();
            try { return NativeRun(bytes, (uint)module.Length, entry, gas, WasmStackBytes, MemoryLimitBytes, value, text, 256); }
            finally { ProjektZeh.Runtime.FaultIsolation.LeaveNativeRun(); }
        }
    }

    [DllImport("*", EntryPoint = "zehos_wasm_run", ExactSpelling = true)]
    private static extern int NativeRun(byte* bytes, uint length, byte* entry, ulong gas, uint stackBytes, ulong memoryBytes,
        int* result, byte* message, uint messageLength);

    // Eigene WASM-Zeile statt "APP <id> ...": Gasttests werten jede andere APP-Zeile beim Warten auf "erstes Bild" als
    // Fehlschlag. Format: "WASM Start <name> Laufzeit=aot <AotAbi>" bzw. "Laufzeit=wasm3[ AOT-Rueckfall: <Status> <Text>]".
    private static void ReportEngine(string? name, string engine)
    {
        if (name != null) Cosmos.Kernel.System.Diagnostics.Log.WriteString($"WASM Start {name} Laufzeit={engine}\n");
    }

    // WAMR ist nur mit -p:ZehOSWasmAot=true im Kernel; ohne ihn waehlt WasmEngineSelection immer wasm3.
#if ZEHOS_WASM_AOT

    // Dieselben Grenzen wie wasm3 (Speicher, Stack der Ausfuehrungsumgebung), aber kein Gasbudget.
    private static int RunAot(byte[] aot, byte* entry, int* value, byte* text, int* instantiated)
    {
        fixed (byte* bytes = aot)
        {
            ProjektZeh.Runtime.FaultIsolation.EnterNativeRun();
            try { return NativeRunAot(bytes, (uint)aot.Length, entry, WasmStackBytes, MemoryLimitBytes, value, text, 256, instantiated); }
            finally { ProjektZeh.Runtime.FaultIsolation.LeaveNativeRun(); }
        }
    }

    [DllImport("*", EntryPoint = "zehos_wasm_run_aot", ExactSpelling = true)]
    private static extern int NativeRunAot(byte* bytes, uint length, byte* entry, uint stackBytes, ulong memoryBytes,
        int* result, byte* message, uint messageLength, int* instantiated);
#else
    private static int RunAot(byte[] aot, byte* entry, int* value, byte* text, int* instantiated) =>
        throw new InvalidOperationException("WAMR-AOT ist in diesem Kernel nicht enthalten.");
#endif

    // Das Bundle wird nur einmal je Kernellauf geparst. Es laeuft hoechstens eine Paket-App, daher greift nur ein
    // Thread auf den Zwischenspeicher zu.
    private static TlsClient CreateTlsClient(string host) => CosmosWasmNetworkConnector.CreateAppClient(host, CosmosSaltSource.Fill,
        () => tlsAnchors ??= TlsTransport.LoadAnchors(TlsRoots.Load()), ProjektZeh.SystemServices.SystemClock.UtcNow);

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

    // Rasterizer: Die C-Bruecke uebergibt den linearen Speicher; WasmRasterizer prueft jeden Bereich. Eine unerwartete
    // Ausnahme beendet nur die App (Trap), statt die native Grenze zu ueberqueren; sie wird seriell protokolliert,
    // damit ein Fehler im Rasterizer nicht als Fehler der App erscheint.
    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_raster_draw")]
    private static int HostRasterDraw(byte* memory, uint length, uint call)
    {
        try { return length > int.MaxValue ? WasmRasterizer.OutOfBounds : WasmRasterizer.Draw(new Span<byte>(memory, (int)length), call); }
        catch (Exception exception) { return ReportRasterFailure(exception); }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_raster_fill")]
    private static int HostRasterFill(byte* memory, uint length, uint offset, uint count, uint value)
    {
        try { return length > int.MaxValue ? WasmRasterizer.OutOfBounds : WasmRasterizer.Fill(new Span<byte>(memory, (int)length), offset, count, value); }
        catch (Exception exception) { return ReportRasterFailure(exception); }
    }

    private static int ReportRasterFailure(Exception exception)
    {
        try { Cosmos.Kernel.System.Diagnostics.Log.WriteString("WASM Rasterizer-Fehler: " + exception.GetType().Name + "\n"); }
        catch (Exception) { /* Protokoll nicht verfuegbar; der Trap meldet den Abbruch trotzdem. */ }
        return WasmRasterizer.OutOfBounds;
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
        if (clockId == 0) return (ulong)(ProjektZeh.SystemServices.SystemClock.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        return (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_random")]
    private static int HostRandom(byte* buffer, uint length)
    {
        // C hat buffer/length gegen die lineare Memory (hoechstens MemoryLimitBytes) geprueft; length passt in int.
        return WasmRandom.Fill(new Span<byte>(buffer, (int)length), CosmosSaltSource.Fill);
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

    // Etappe 13.4: App-Speicher /save. Jede Rueckgabe ist eine WASI-Fehlernummer; Ausnahmen werden zu EIO, damit
    // keine die native Grenze ueberquert.

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_available")]
    private static int HostSaveAvailable() => save != null ? 1 : 0;

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_open")]
    private static int HostSaveOpen(byte* path, uint length, uint flags, int* handle)
    {
        try
        {
            if (save == null) return WasiErrno.BadFile;
            int status = save.Open(Encoding.UTF8.GetString(path, (int)length), (flags & 1) != 0, (flags & 4) != 0, (flags & 8) != 0,
                (flags & 2) != 0, (flags & 16) != 0, out int opened);
            *handle = opened;
            return status;
        }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_read")]
    private static int HostSaveRead(int handle, ulong offset, byte* buffer, uint length, uint* read)
    {
        try
        {
            if (save == null || offset > long.MaxValue) return WasiErrno.BadFile;
            int status = save.Read(handle, (long)offset, new Span<byte>(buffer, (int)length), out int count);
            *read = (uint)count;
            return status;
        }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_write")]
    private static int HostSaveWrite(int handle, ulong offset, byte* data, uint length)
    {
        try
        {
            if (save == null) return WasiErrno.BadFile;
            if (offset > int.MaxValue) return WasiErrno.FileTooLarge;
            return save.Write(handle, (long)offset, new ReadOnlySpan<byte>(data, (int)length), out _);
        }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_size")]
    private static long HostSaveSize(int handle)
    {
        try { return save?.Size(handle) ?? -1; }
        catch (Exception) { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_set_size")]
    private static int HostSaveSetSize(int handle, ulong size)
    {
        try { return save == null ? WasiErrno.BadFile : size > int.MaxValue ? WasiErrno.FileTooLarge : save.SetSize(handle, (long)size); }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_sync")]
    private static int HostSaveSync(int handle)
    {
        try { return save?.Sync(handle) ?? WasiErrno.BadFile; }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_close")]
    private static int HostSaveClose(int handle)
    {
        try { return save?.Close(handle) ?? WasiErrno.BadFile; }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_stat")]
    private static int HostSaveStat(byte* path, uint length, ulong* size, int* type)
    {
        try
        {
            if (save == null) return WasiErrno.BadFile;
            int status = save.Stat(Encoding.UTF8.GetString(path, (int)length), out long bytes, out bool directory);
            *size = (ulong)bytes;
            *type = directory ? 3 : 4;
            return status;
        }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_path")]
    private static int HostSavePath(int operation, byte* path, uint length)
    {
        try
        {
            if (save == null) return WasiErrno.BadFile;
            string name = Encoding.UTF8.GetString(path, (int)length);
            return operation switch
            {
                1 => save.CreateDirectory(name),
                2 => save.RemoveDirectory(name),
                3 => save.Unlink(name),
                _ => WasiErrno.Invalid,
            };
        }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_rename")]
    private static int HostSaveRename(byte* from, uint fromLength, byte* to, uint toLength)
    {
        try
        {
            return save?.Rename(Encoding.UTF8.GetString(from, (int)fromLength), Encoding.UTF8.GetString(to, (int)toLength)) ?? WasiErrno.BadFile;
        }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_save_readdir")]
    private static int HostSaveReaddir(int handle, byte* buffer, uint length, ulong cookie, uint* used)
    {
        try
        {
            if (save == null) return WasiErrno.BadFile;
            int status = save.List(handle, out var entries);
            if (status != WasiErrno.Success) return status;
            *used = (uint)WasiDirectoryEntries.Write(entries, cookie, new Span<byte>(buffer, (int)length));
            return WasiErrno.Success;
        }
        catch (Exception) { return WasiErrno.Io; }
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_ticks_ms")]
    private static uint HostTicks() => current?.TicksMilliseconds ?? (uint)(Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_sleep_ms")]
    private static void HostSleep(uint milliseconds)
    {
        long start = Stopwatch.GetTimestamp();
        Thread.Sleep((int)milliseconds);
        WaitedTicks += Stopwatch.GetTimestamp() - start;
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
