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
            CheckClock(service, probeCa);
            Check(CosmosNetworkService.Fetch("https://10.0.2.2:18443/ok", CancellationToken.None, probeCa) == "ZehOS network probe\n", "TLS valid");
            // Server schliesst sofort nach dem Body; der Abruf darf weder scheitern noch auf das 10-s-Zeitlimit warten.
            var finClock = Stopwatch.StartNew();
            Check(CosmosNetworkService.Fetch("https://10.0.2.2:18443/fin", CancellationToken.None, probeCa) == "ZehOS network probe\n"
                && finClock.ElapsedMilliseconds < 5000, "TLS close after server FIN");
            for (int port = 18444; port <= 18447; port++)
            {
                bool rejected = false;
                try { CosmosNetworkService.Fetch("https://10.0.2.2:" + port + "/ok", CancellationToken.None, probeCa); }
                catch (Exception exception) { rejected = IsBadCertificate(exception); }
                Check(rejected, "TLS certificate rejection " + port);
            }
            Check(CosmosNetworkService.Fetch("https://10.0.2.2:18443/ok", CancellationToken.None, probeCa) == "ZehOS network probe\n", "TLS recovery");
#if ZEHOS_WASM
            CheckWasmTls(probeCa);
#endif
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
#if ZEHOS_NETWORK_TLS_PROBE
    /// <summary>
    /// Etappe 12.2 PR C: SNTP-Fixtures des Helfers auf 10.0.2.2 (18123 richtige Zeit, 18124 fremde Antwort, 18125 eine
    /// Stunde zurueck). Mit falscher RTC (--rtc-base) muss TLS erst gesperrt sein und nach dem Abgleich gehen.
    /// </summary>
    private static void CheckClock(CosmosNetworkService service, byte[] probeCa)
    {
        bool rtcPlausible = ProjektZeh.SystemServices.ClockCorrection.IsPlausible(DateTime.UtcNow, TlsRoots.MinimumTime);
        Write("time RTC " + DateTime.UtcNow.ToString("u") + (rtcPlausible ? " plausible" : " implausible"));
        if (!rtcPlausible)
        {
            bool blocked = false;
            try { CosmosNetworkService.Fetch("https://10.0.2.2:18443/ok", CancellationToken.None, probeCa); }
            catch (Exception exception)
            {
                for (Exception? current = exception; current != null; current = current.InnerException)
                    blocked |= current.Message.Contains("UTC-Uhr", StringComparison.Ordinal);
            }
            Check(blocked, "TLS blocked by implausible RTC");
        }
        Check(!service.Execute("time", ["10.0.2.2", "18124"], CancellationToken.None).IsOK(), "NTP foreign answer ignored");
        if (rtcPlausible)
        {
            var backward = service.Execute("time", ["10.0.2.2", "18125"], CancellationToken.None);
            Check(!backward.IsOK() && backward.Status.Message.Contains("BackwardStepRefused", StringComparison.Ordinal), "NTP backward step: " + backward.Status.Message);
        }
        var synced = service.Execute("time", ["10.0.2.2", "18123"], CancellationToken.None);
        Check(synced.IsOK() && ProjektZeh.SystemServices.SystemClock.IsSynchronized, "NTP sync: " + synced.Status.Message);
        Check(ProjektZeh.SystemServices.ClockCorrection.IsPlausible(ProjektZeh.SystemServices.SystemClock.UtcNow, TlsRoots.MinimumTime), "clock plausible after sync");
        Write("time PASS " + synced.Value);
    }
#endif

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
#if ZEHOS_NETWORK_TLS_PROBE
    // Nicht blockierender TLS-Pfad der Wasm-Apps mit denselben Fixtures wie die Shell: Erfolg, Ablehnungen, Frist.
    private static void CheckWasmTls(byte[] probeCa)
    {
        var anchors = TlsTransport.LoadAnchors(probeCa);
        var network = new ProjektZeh.Runtime.Wasm.WasmNetworkSession(new ProjektZeh.Runtime.Wasm.CosmosWasmNetworkConnector(host =>
            TlsTransport.CreateClient(host, ProjektZeh.Security.CosmosSaltSource.Fill, anchors, ProjektZeh.SystemServices.SystemClock.UtcNow, true)), true);
        int tls = ProjektZeh.Runtime.Wasm.WasmNetworkSession.FlagTls;
        int status;
        // Der eine Netzwerkworker kann noch im blockierenden Cosmos-Connect zu 10.0.2.200 aus CheckWasmNetwork haengen;
        // erst wenn eine TCP-Verbindung wieder durchgeht, misst die TLS-Frist den Handshake und nicht diese Wartezeit.
        var clock = Stopwatch.StartNew();
        do
        {
            Check(network.Open("10.0.2.2", 18080, 0, out int probe) == 0, "Wasm TLS worker probe");
            while ((status = network.Status(probe)) == 6) Thread.Sleep(20);
            Check(network.Close(probe) == 0, "Wasm TLS worker probe close");
        }
        while (status != 0 && clock.ElapsedMilliseconds < 180000);
        Check(status == 0, "Wasm TLS worker free " + status);
        Write($"wasm-tls workerFreeMs={clock.ElapsedMilliseconds}");
        clock.Restart();
        Check(network.Open("10.0.2.2", 18443, tls, out int handle) == 0, "Wasm TLS open");
        while ((status = network.Status(handle)) == 6 && clock.ElapsedMilliseconds < 15000) Thread.Sleep(10);
        Check(status == 0, "Wasm TLS handshake " + status);
        Write($"wasm-tls handshakeMs={clock.ElapsedMilliseconds}");
        byte[] request = System.Text.Encoding.ASCII.GetBytes("GET /ok HTTP/1.0\r\nHost: 10.0.2.2\r\nConnection: close\r\n\r\n");
        Check(network.Send(handle, request, out int written) == 0 && written == request.Length, "Wasm TLS send");
        byte[] data = new byte[4096];
        int total = 0, result = 6;
        clock.Restart();
        // Der Fixture-Server schliesst ohne close_notify; nach dem Rumpf ist daher 0 oder ECONNRESET zulaessig.
        while (clock.ElapsedMilliseconds < 15000)
        {
            result = network.Receive(handle, data.AsSpan(total), out int read);
            total += read;
            Check(total < data.Length, "Wasm TLS response limit");
            if (result is not (0 or 6) || (result == 0 && read == 0)) break;
            if (read == 0) Thread.Sleep(10);
        }
        string response = System.Text.Encoding.ASCII.GetString(data, 0, total);
        Check(result is 0 or 15 && response.Contains("ZehOS network probe", StringComparison.Ordinal), "Wasm TLS response " + result);
        Check(network.Close(handle) == 0, "Wasm TLS close");
        for (int port = 18444; port <= 18447; port++)
        {
            Check(network.Open("10.0.2.2", port, tls, out int rejected) == 0, "Wasm TLS open " + port);
            clock.Restart();
            while ((status = network.Status(rejected)) == 6 && clock.ElapsedMilliseconds < 15000) Thread.Sleep(10);
            Check(status == 63, "Wasm TLS certificate rejection " + port + " status=" + status);
            Check(network.Close(rejected) == 0, "Wasm TLS close " + port);
        }
        Check(network.Open("10.0.2.2", 18448, tls, out int silent) == 0, "Wasm TLS open silent");
        clock.Restart();
        while ((status = network.Status(silent)) == 6 && clock.ElapsedMilliseconds < 20000) Thread.Sleep(20);
        Check(status == 73 && clock.ElapsedMilliseconds < 15000, "Wasm TLS handshake deadline " + status);
        Check(network.Close(silent) == 0 && network.CloseAll() == 0, "Wasm TLS cleanup");
        Write("wasm-tls PASS");
    }
#endif

    private sealed class NetworkProbeSession(bool allowed) : ProjektZeh.Runtime.Wasm.IWasmSession
    {
        public string? Root => null;
        public string? SaveRoot => null;
        public bool AllowsNetwork => allowed;
        public bool ShouldExit => false;
        public uint TicksMilliseconds => 0;
        public void Write(int fd, ReadOnlySpan<byte> data) { }
        public void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test) { }
        public int PollKey() => -1;
        public bool TryReadEvent(out ProjektZeh.Runtime.Wasm.WasmInputEvent value) { value = default; return false; }
        public bool SetPointerCapture(bool enabled) => false;
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
