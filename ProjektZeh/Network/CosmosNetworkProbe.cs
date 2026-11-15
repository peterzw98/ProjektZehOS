#if ZEHOS_NETWORK_PROBE
using System;
using System.Diagnostics;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;
using Org.BouncyCastle.Tls;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Network;

internal static class CosmosNetworkProbe
{
    public static bool Run(BackgroundTaskService tasks)
    {
        try
        {
            var service = new CosmosNetworkService();
            Check(service.Execute("static", ["10.0.2.15", "255.255.255.0", "10.0.2.2", "10.0.2.3"], CancellationToken.None).IsOK(), "static");
            Write("static PASS");
            Check(service.Execute("ping", ["10.0.2.2"], CancellationToken.None).IsOK(), "ping gateway");
            Write("ping PASS");
            Check(service.Execute("dns", ["example.com"], CancellationToken.None).IsOK(), "DNS example.com");
            Check(!service.Execute("dns", ["does-not-exist.invalid"], CancellationToken.None).IsOK(), "DNS failure");
            Write("dns PASS");
            var ok = service.Execute("fetch", ["http://10.0.2.2:18080/ok"], CancellationToken.None);
            Check(ok.IsOK() && ok.Value == "ZehOS network probe\n", "fragmented HTTP: " + ok.Status.Message);
            Write("fetch PASS");
#if ZEHOS_WASM
            CheckWasmNetwork();
#endif
#if ZEHOS_NETWORK_TLS_PROBE
            byte[] probeCa = TlsRoots.Read("ZehOS.NetworkProbeCa");
            Check(CosmosNetworkService.Fetch("https://10.0.2.2:18443/ok", CancellationToken.None, probeCa) == "ZehOS network probe\n", "TLS valid");
            for (int port = 18444; port <= 18447; port++)
            {
                bool rejected = false;
                try { CosmosNetworkService.Fetch("https://10.0.2.2:" + port + "/ok", CancellationToken.None, probeCa); }
                catch (Exception exception) { rejected = IsBadCertificate(exception); }
                Check(rejected, "TLS certificate rejection " + port);
            }
            Check(CosmosNetworkService.Fetch("https://10.0.2.2:18443/ok", CancellationToken.None, probeCa) == "ZehOS network probe\n", "TLS recovery");
            foreach (string path in new[] { "partial", "oversize", "slow" })
            {
                bool rejected = false;
                try { CosmosNetworkService.Fetch("https://10.0.2.2:18443/" + path, CancellationToken.None, probeCa); }
                catch (System.IO.IOException) { rejected = true; }
                Check(rejected, "TLS body limit " + path);
            }
            var tlsClock = Stopwatch.StartNew();
            bool timedOut = false;
            try { CosmosNetworkService.Fetch("https://10.0.2.2:18448/", CancellationToken.None, probeCa); }
            catch (System.IO.IOException) { timedOut = true; }
            Check(timedOut && tlsClock.ElapsedMilliseconds < 15000, "TLS handshake deadline");
            int tlsFinished = 0; bool tlsCancelled = false;
            var tlsTask = tasks.Start("TLS probe", context =>
            {
                try { CosmosNetworkService.Fetch("https://10.0.2.2:18448/", context.Token, probeCa); }
                catch (Exception exception) { tlsCancelled = IsCancellation(exception); }
                Volatile.Write(ref tlsFinished, 1);
            });
            Check(tlsTask.IsOK(), "TLS worker start");
            Thread.Sleep(300);
            Check(tasks.Cancel(tlsTask.Value).IsOK(), "TLS cancel");
            tlsClock.Restart();
            while (Volatile.Read(ref tlsFinished) == 0 && tlsClock.ElapsedMilliseconds < 15000) Thread.Sleep(20);
            Check(Volatile.Read(ref tlsFinished) == 1 && tlsCancelled && tlsClock.ElapsedMilliseconds < 15000, "TLS cancel completion");
            Write("tls PASS");
#endif
            Check(!service.Execute("fetch", ["http://10.0.2.2:18080/partial"], CancellationToken.None).IsOK(), "partial");
            Check(!service.Execute("fetch", ["http://10.0.2.2:18080/oversize"], CancellationToken.None).IsOK(), "oversize");
            Check(!service.Execute("fetch", ["http://10.0.2.2:18081/"], CancellationToken.None).IsOK(), "connection refusal");
            Check(!service.Execute("fetch", ["http://10.0.2.2:18080/stall"], CancellationToken.None).IsOK(), "stalled timeout");
            Check(service.Execute("fetch", ["http://10.0.2.2:18080/ok"], CancellationToken.None).IsOK(), "recovery after errors");
            Write("limits PASS");
            ResultObject<string>? cancelled = null;
            var started = tasks.Start("network probe", context => Volatile.Write(ref cancelled, service.Execute("fetch", ["http://10.0.2.2:18080/stall"], context.Token)));
            Check(started.IsOK(), "worker start");
            Thread.Sleep(300);
            Check(tasks.Cancel(started.Value).IsOK(), "cancel");
            var clock = Stopwatch.StartNew(); int heartbeat = 0;
            while (Volatile.Read(ref cancelled) == null && clock.ElapsedMilliseconds < 15000) { heartbeat++; Thread.Sleep(20); }
            Check(Volatile.Read(ref cancelled) is { } response && !response.IsOK() && heartbeat > 0, "worker cancel/heartbeat");
            Write("cancel heartbeat PASS");
            Check(service.Execute("dhcp", [], CancellationToken.None).IsOK(), "DHCP");
            Write("dhcp PASS");
            Write("PASS");
            return true;
        }
        catch (Exception exception) { Write("FAIL " + exception.Message); return false; }
    }
#if ZEHOS_WASM
    private static void CheckWasmNetwork()
    {
        // Minimalmodul ruft net_open fuer den lokalen Testserver auf und liefert den WASI-Status.
        byte[] module = Convert.FromHexString("0061736d01000000010e0260057f7f7f7f7f017f6000017f021501087a65686f735f7631086e65745f6f70656e00000302010105030100010707010372756e00010a14011200418001410841a08d01410041900110000b0b0f01004180010b0831302e302e322e32");
        var deniedSession = new NetworkProbeSession(false);
        var allowedSession = new NetworkProbeSession(true);
        Check(ProjektZeh.Runtime.Wasm.WasmRuntime.Run(module, "run", 100000, deniedSession, out int deniedStatus, out _) == ProjektZeh.Runtime.Wasm.WasmStatus.Ok && deniedStatus == 2, "Wasm import denies network");
        Check(ProjektZeh.Runtime.Wasm.WasmRuntime.Run(module, "run", 100000, allowedSession, out int allowedStatus, out _) == ProjektZeh.Runtime.Wasm.WasmStatus.Ok && allowedStatus == 0, "Wasm import allows network");
        var denied = new ProjektZeh.Runtime.Wasm.WasmNetworkSession(new ProjektZeh.Runtime.Wasm.CosmosWasmNetworkConnector(), false);
        Check(denied.Open("10.0.2.2", 18080, 0, out _) == 2, "Wasm network capability");
        var network = new ProjektZeh.Runtime.Wasm.WasmNetworkSession(new ProjektZeh.Runtime.Wasm.CosmosWasmNetworkConnector(), true);
        Check(network.Open("10.0.2.2", 18080, 0, out int handle) == 0, "Wasm net open");
        var clock = Stopwatch.StartNew();
        int status;
        while ((status = network.Status(handle)) == 6 && clock.ElapsedMilliseconds < 15000) Thread.Sleep(10);
        Check(status == 0, "Wasm net connect " + status);
        byte[] request = System.Text.Encoding.ASCII.GetBytes("GET /ok HTTP/1.0\r\nHost: 10.0.2.2\r\nConnection: close\r\n\r\n");
        Check(network.Send(handle, request, out int written) == 0 && written == request.Length, "Wasm net send");
        byte[] data = new byte[4096];
        int total = 0;
        bool eof = false;
        clock.Restart();
        while (!eof && clock.ElapsedMilliseconds < 15000)
        {
            int result = network.Receive(handle, data.AsSpan(total), out int read);
            Check(result is 0 or 6, "Wasm net recv " + result);
            if (result == 0 && read == 0) eof = true;
            total += read;
            Check(total < data.Length, "Wasm net response limit");
            if (!eof) Thread.Sleep(10);
        }
        string response = System.Text.Encoding.ASCII.GetString(data, 0, total);
        Check(eof && response.Contains("ZehOS network probe", StringComparison.Ordinal), "Wasm net response");
        Check(network.Close(handle) == 0 && network.CloseAll() == 0, "Wasm net close");
        var pending = new ProjektZeh.Runtime.Wasm.WasmNetworkSession(new ProjektZeh.Runtime.Wasm.CosmosWasmNetworkConnector(), true);
        Check(pending.Open("10.0.2.200", 18080, 0, out int blocked) == 0, "Wasm blocked open");
        Thread.Sleep(300);
        Check(pending.Status(blocked) == 6, "Wasm connect did not remain pending");
        clock.Restart();
        Check(pending.Close(blocked) == 0 && clock.ElapsedMilliseconds < 1000, "Wasm close during connect");
        Check(pending.Open("10.0.2.2", 18080, 0, out int after) == 0, "Wasm open after close");
        clock.Restart();
        while ((status = pending.Status(after)) == 6 && clock.ElapsedMilliseconds < 60000) Thread.Sleep(20);
        Check(status == 0 || status == 73, "Wasm pending completion " + status);
        Write($"wasm-connect pending status={status} waitMs={clock.ElapsedMilliseconds}");
        Check(pending.Close(after) == 0 && pending.CloseAll() == 0, "Wasm pending cleanup");
        Write("wasm-network PASS");
    }
    private sealed class NetworkProbeSession(bool allowed) : ProjektZeh.Runtime.Wasm.IWasmSession
    {
        public string? Root => null;
        public bool AllowsNetwork => allowed;
        public bool ShouldExit => false;
        public uint TicksMilliseconds => 0;
        public void Write(int fd, ReadOnlySpan<byte> data) { }
        public void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test) { }
        public int PollKey() => -1;
        public bool TryReadEvent(out ProjektZeh.Runtime.Wasm.WasmInputEvent value) { value = default; return false; }
    }
#endif
#if ZEHOS_NETWORK_TLS_PROBE
    private static bool IsCancellation(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is OperationCanceledException) return true;
        return false;
    }

    private static bool IsBadCertificate(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is TlsFatalAlert alert && alert.AlertDescription == AlertDescription.bad_certificate) return true;
        return false;
    }
#endif
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Write(string message) => Log.WriteString("NETWORK_PROBE " + message + "\n");
}
#endif
