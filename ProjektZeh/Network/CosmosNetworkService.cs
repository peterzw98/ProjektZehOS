using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cosmos.Kernel.System.Network;
using ProjektZeh.Shared;
using ProjektZeh.Update;
using ProjektZeh.Security;

namespace ProjektZeh.Network;

internal sealed class CosmosNetworkService : INetworkService
{
    internal static readonly object Gate = new();
    private const int TimeoutMilliseconds = 5000;
    private const int MaximumBody = 16384;

    public ResultObject<string> Execute(string operation, string[] arguments, CancellationToken cancellation)
    {
        while (!Monitor.TryEnter(Gate, 50)) cancellation.ThrowIfCancellationRequested();
        ResultObject<string> result;
        try { cancellation.ThrowIfCancellationRequested(); result = ResultObjectFactory.CreateSuccess(Perform(operation, arguments, cancellation)); }
        catch (Exception exception) { result = ResultObjectFactory.CreateFromException<string>(exception); }
        Monitor.Exit(Gate);
        return result;
    }

    private static string Perform(string operation, string[] arguments, CancellationToken cancellation)
    {
        if (!NetworkManager.IsEnabled || !NetworkManager.IsInitialized) throw new IOException("Netzwerkdienst nicht initialisiert.");
        if (operation == "info")
        {
            if (arguments.Length != 0) throw new ArgumentException("Verwendung: netinfo");
            var info = new StringBuilder($"Adapter: {NetworkManager.DeviceCount}");
            for (int i = 0; i < NetworkManager.DeviceCount; i++)
            {
                var adapter = NetworkManager.GetAdapter(i);
                if (adapter.IsValid) info.Append($"\n{adapter.Name}: MAC {adapter.MacAddress}, Link {adapter.LinkUp}, IPv4 {adapter.IPConfig?.Address.ToString() ?? "keine"}, Maske {adapter.IPConfig?.SubnetMask.ToString() ?? "-"}, Gateway {adapter.IPConfig?.DefaultGateway.ToString() ?? "-"}");
            }
            foreach (var server in DnsConfig.Nameservers) info.Append("\nDNS: " + server);
            return info.ToString();
        }
        if (NetworkManager.DeviceCount == 0 || !NetworkManager.LinkUp) throw new IOException("Kein betriebsbereiter Netzwerkadapter.");
        if (operation == "static")
        {
            if (arguments.Length != 4) throw new ArgumentException("Verwendung: net static <IP> <Maske> <Gateway> <DNS>");
            Address ip = ParseAddress(arguments[0]), mask = ParseAddress(arguments[1]), gateway = ParseAddress(arguments[2]), dns = ParseAddress(arguments[3]);
            Ipv4Configuration.Validate(arguments);
            IPConfig.Enable(ip, mask, gateway);
            while (DnsConfig.Nameservers.Count > 0) DnsConfig.Remove(DnsConfig.Nameservers[0]);
            DnsConfig.Add(dns);
            return "Statische IPv4-Konfiguration gesetzt: " + ip;
        }
        if (operation == "dhcp")
        {
            if (arguments.Length != 0) throw new ArgumentException("Verwendung: net dhcp");
            var client = new DhcpClient();
            Exception? failure = null;
            try { client.SendDiscoverPacket(); cancellation.ThrowIfCancellationRequested(); }
            catch (Exception exception) { failure = exception; }
            try { client.Close(); } catch (Exception exception) { failure = CleanupFailure(failure, "DHCP", exception); }
            if (failure != null) throw failure;
            if (NetworkManager.Primary.IPConfig == null || NetworkManager.Primary.IPConfig.Address.Equals(Address4.Zero)) throw new IOException("DHCP hat keine IPv4-Adresse geliefert.");
            return "DHCP IPv4: " + NetworkManager.Primary.IPConfig.Address;
        }
        if (NetworkManager.Primary.IPConfig == null || NetworkManager.Primary.IPConfig.Address.Equals(Address4.Zero)) throw new IOException("IPv4 ist nicht konfiguriert: net static oder net dhcp verwenden.");
        if (arguments.Length != 1) throw new ArgumentException("Genau ein Ziel angeben.");
        return operation switch
        {
            "dns" => "DNS: " + Resolve(arguments[0], cancellation),
            "ping" => Ping(ParseAddress(arguments[0]), cancellation),
            "fetch" => Fetch(arguments[0], cancellation),
            _ => throw new ArgumentException("Unbekannte Netzoperation.")
        };
    }

    private static Address4 ParseAddress(string text)
    {
        byte[] bytes = Ipv4Configuration.ParseAddress(text);
        return new Address4(bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    private static IOException CleanupFailure(Exception? original, string operation, Exception cleanup) =>
        new IOException((original == null ? "" : original.Message + " ") + operation + "-Bereinigung fehlgeschlagen: " + cleanup.Message, cleanup);

    private static Address Resolve(string host, CancellationToken token, Stopwatch? deadline = null)
    {
        NetworkTarget.ValidateHost(host);
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return ParseAddress(host);
        if (host.Length is < 1 or > 253 || DnsConfig.Nameservers.Count == 0) throw new IOException("Kein DNS-Server konfiguriert oder Name ungueltig.");
        var client = new DnsClient(); Address? address = null; Exception? failure = null;
        try
        {
            client.Connect(DnsConfig.Nameservers[0]); client.SendQuery(host);
            var clock = deadline ?? Stopwatch.StartNew();
            long limit = deadline is null ? TimeoutMilliseconds : FetchTimeoutMilliseconds;
            while (address is null && clock.ElapsedMilliseconds < limit)
            { token.ThrowIfCancellationRequested(); address = client.Receive(0); if (address is null) token.WaitHandle.WaitOne(10); }
            if (address is null) throw new IOException("DNS-Zeitlimit erreicht oder Name nicht gefunden.");
        }
        catch (Exception exception) { failure = exception; }
        try { client.Close(); } catch (Exception exception) { failure = CleanupFailure(failure, "DNS", exception); }
        if (failure != null) throw failure;
        return address!;
    }

    private static string Ping(Address address, CancellationToken token)
    {
        var client = new IcmpClient(); Exception? failure = null; bool received = false;
        var clock = Stopwatch.StartNew();
        try
        {
            client.Connect(address); client.SendEcho(); var source = new Cosmos.Kernel.System.Network.EndPoint(Address4.Zero, 0);
            while (!received && clock.ElapsedMilliseconds < TimeoutMilliseconds)
            { token.ThrowIfCancellationRequested(); received = client.Receive(ref source, 0) >= 0; if (!received) token.WaitHandle.WaitOne(10); }
            if (!received) throw new IOException("Ping-Zeitlimit erreicht.");
        }
        catch (Exception exception) { failure = exception; }
        try { client.Close(); } catch (Exception exception) { failure = CleanupFailure(failure, "ICMP", exception); }
        if (failure != null) throw failure;
        return $"Antwort von {address} nach {clock.ElapsedMilliseconds} ms.";
    }

    private const int FetchTimeoutMilliseconds = 10000;

    internal static string Fetch(string text, CancellationToken token, byte[]? rootsOverride = null)
    {
        var clock = Stopwatch.StartNew();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Nur HTTP- und HTTPS-URLs ohne Zugangsdaten oder Fragment werden unterstuetzt.");
        if (uri.Host.Length > 253 || uri.PathAndQuery.Length > 2048) throw new ArgumentException("URL ist zu lang.");
        Address address = Resolve(uri.Host, token, clock);
        CheckDeadline(clock, token);
        CosmosHttpUpdateSource.RandomizeTcpStart();
        var socket = new Socket(System.Net.Sockets.AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Exception? failure = null; byte[]? body = null; Stream? secure = null;
        try
        {
            socket.Connect(IPAddress.Parse(address.ToString()!), uri.Port);
            CheckDeadline(clock, token);
            var transport = new PollingStream(socket, clock, token);
            Stream stream = transport;
            if (uri.Scheme == "https")
            {
                secure = TlsTransport.Open(transport, uri.Host, CosmosSaltSource.Fill, rootsOverride ?? TlsRoots.Load(), DateTime.UtcNow);
                CheckDeadline(clock, token);
                stream = secure;
            }
            byte[] request = Encoding.ASCII.GetBytes($"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n");
            stream.Write(request, 0, request.Length);
            stream.Flush();
            body = HttpUpdateResponse.Read(stream, MaximumBody);
            CheckDeadline(clock, token);
        }
        catch (Exception exception) { failure = exception; }
        try { secure?.Dispose(); } catch (Exception exception) { failure = CleanupFailure(failure, "TLS", exception); }
        try { socket.Close(0); } catch (Exception exception) { failure = CleanupFailure(failure, "TCP", exception); }
        if (failure != null) throw failure;
        return Encoding.UTF8.GetString(body!);
    }

    private static void CheckDeadline(Stopwatch clock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (clock.ElapsedMilliseconds >= FetchTimeoutMilliseconds) throw new IOException("Netzwerk-Zeitlimit erreicht.");
    }

    private sealed class PollingStream(Socket socket, Stopwatch clock, CancellationToken token) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            CheckDeadline(clock, token);
            while (!socket.Poll(0, SelectMode.SelectRead))
            {
                CheckDeadline(clock, token);
                token.WaitHandle.WaitOne(10);
            }
            CheckDeadline(clock, token);
            int received = socket.Receive(buffer, offset, count, SocketFlags.None);
            CheckDeadline(clock, token);
            return received;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                CheckDeadline(clock, token);
                while (!socket.Poll(0, SelectMode.SelectWrite))
                {
                    CheckDeadline(clock, token);
                    token.WaitHandle.WaitOne(10);
                }
                int sent = socket.Send(buffer, offset, count, SocketFlags.None);
                CheckDeadline(clock, token);
                if (sent <= 0) throw new IOException("TCP-Senden fehlgeschlagen.");
                offset += sent; count -= sent;
            }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => CheckDeadline(clock, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
