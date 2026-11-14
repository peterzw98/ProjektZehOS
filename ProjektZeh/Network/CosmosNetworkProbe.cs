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
