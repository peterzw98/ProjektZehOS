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
using ProjektZeh.SystemServices;

namespace ProjektZeh.Network;

internal sealed class CosmosNetworkService : INetworkService
{
    internal static readonly object Gate = new();
    private const int TimeoutMilliseconds = 5000;
    private const int DnsRetryMilliseconds = 2000;
    private const int MaximumBody = 16384;

    public NetworkStatus Status => ReadStatus();

    private static NetworkStatus ReadStatus()
    {
        if (!NetworkManager.IsEnabled || !NetworkManager.IsInitialized || NetworkManager.DeviceCount == 0)
            return new(false, false, null);
        var adapter = NetworkManager.Primary;
        var address = adapter.IPConfig?.Address;
        return new(adapter.IsValid, adapter.LinkUp,
            address is null || address.Equals(Address4.Zero) ? null : address.ToString());
    }

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
        if (operation == "auto-dhcp")
        {
            if (ReadStatus().Address is string configured) return "IPv4 bereits konfiguriert: " + configured;
            operation = "dhcp";
        }
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
        if (operation == "time")
        {
            if (arguments.Length is < 1 or > 2) throw new ArgumentException("Verwendung: net time [Server] [Port]");
            int port = Sntp.Port;
            if (arguments.Length == 2 && (!int.TryParse(arguments[1], out port) || port is < 1 or > 65535)) throw new ArgumentException("Ungueltiger Port.");
            return SyncTime(arguments[0], port, cancellation);
        }
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

    internal static Address Resolve(string host, CancellationToken token, Stopwatch? deadline = null)
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
            // Ein verlorenes UDP-Paket kann sonst den ganzen Abruf abbrechen (pkg install im Gast scheiterte mit DNS-Zeitlimit);
            // die Frage geht deshalb ohne Antwort einmal erneut hinaus, innerhalb derselben Frist.
            var asked = Stopwatch.StartNew(); bool repeated = false;
            while (address is null && clock.ElapsedMilliseconds < limit)
            {
                token.ThrowIfCancellationRequested();
                address = client.Receive(0);
                if (address is null && !repeated && asked.ElapsedMilliseconds >= DnsRetryMilliseconds) { client.SendQuery(host); repeated = true; }
                if (address is null) token.WaitHandle.WaitOne(10);
            }
            if (address is null) throw new IOException("DNS-Zeitlimit erreicht oder Name nicht gefunden.");
        }
        catch (Exception exception) { failure = exception; }
        try { client.Close(); } catch (Exception exception) { failure = CleanupFailure(failure, "DNS", exception); }
        if (failure != null) throw failure;
        return address!;
    }

    private const int NtpRetryMilliseconds = 2000;

    /// <summary>
    /// Etappe 12.2: einmal SNTP (Frist 2 s, eine Wiederholung) und den Versatz nach <see cref="ClockCorrection"/> uebernehmen.
    /// Nicht authentifiziert; die Grenzen stehen in der Spec 2026-11-17-tls-12-2-audit.md §2.3.
    /// </summary>
    private static string SyncTime(string host, int port, CancellationToken token)
    {
        Address server = Resolve(host, token);
        byte[] nonce = new byte[8];
        CosmosSaltSource.Fill(nonce);
        byte[] request = Sntp.CreateRequest(nonce);
        // Der parameterlose Cosmos-Konstruktor bindet Port 0, Antworten kaemen dann nie an (3.0.97, wie DnsClient mit Port 53).
        var client = new Cosmos.Kernel.System.Network.UdpClient(Sntp.Port); Exception? failure = null; DateTime network = default, rtc = default, current = default; bool answered = false;
        try
        {
            client.Connect(server, port);
            client.Send(request);
            var clock = Stopwatch.StartNew(); bool repeated = false;
            while (!answered && clock.ElapsedMilliseconds < 2 * NtpRetryMilliseconds)
            {
                token.ThrowIfCancellationRequested();
                var sender = new Cosmos.Kernel.System.Network.EndPoint(Address4.Zero, 0);
                byte[]? data = client.Receive(ref sender, 50);
                // Fremde oder kaputte Pakete werden verworfen; es zaehlt nur eine Antwort auf diese Anfrage.
                if (data != null && Sntp.ParseResponse(data, nonce) is { } parsed && parsed.IsOK())
                {
                    network = parsed.Value;
                    rtc = DateTime.UtcNow;
                    current = SystemClock.UtcNow;
                    answered = true;
                }
                if (!answered && !repeated && clock.ElapsedMilliseconds >= NtpRetryMilliseconds) { client.Send(request); repeated = true; }
            }
            if (!answered) throw new IOException("Keine gueltige NTP-Antwort von " + host + ".");
        }
        catch (Exception exception) { failure = exception; }
        try { client.Close(); } catch (Exception exception) { failure = CleanupFailure(failure, "NTP", exception); }
        if (failure != null) throw failure;
        var offset = ClockCorrection.Offset(rtc, current, network, TlsRoots.MinimumTime);
        if (!offset.IsOK()) throw new IOException("Zeitabgleich nicht uebernommen: " + offset.Status.Message);
        SystemClock.Apply(offset.Value);
        // Eine unplausible Uhr wird ohne Rueckschrittgrenze korrigiert; das soll im Protokoll sichtbar sein.
        string warning = ClockCorrection.IsPlausible(current, TlsRoots.MinimumTime) ? "" : " Warnung: Die Uhr war unplausibel und wurde ungeprueft uebernommen (SNTP ist nicht authentifiziert).";
        return "Uhr abgeglichen mit " + host + ": " + network.ToString("u") + " (Versatz zur RTC " + offset.Value.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s)." + warning;
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

    // Frist je Abruf, gemessen ab TlsTransport.StartDeadline: DNS, Verbindung, TLS-Handshake und Antwort zusammen.
    // Mit idleDeadline (Downloads) beginnt sie nach jedem empfangenen Block neu. Das einmalige Parsen der TLS-Wurzeln
    // liegt vor dem Start der Uhr.
    private const int FetchTimeoutMilliseconds = 10000;

    internal static string Fetch(string text, CancellationToken token, byte[]? rootsOverride = null) =>
        Encoding.UTF8.GetString(Exchange(text, rootsOverride ?? TlsRoots.Load(), false, stream => HttpUpdateResponse.Read(stream, MaximumBody), token));

    /// <summary>
    /// Fuehrt einen einzelnen GET aus und uebergibt den Antwortstrom an <paramref name="read"/>. Fuer Paketdownloads gilt das
    /// Zeitlimit je Datenpaket statt fuer den gesamten Auftrag, damit grosse Dateien nicht am festen 10-s-Limit scheitern.
    /// </summary>
    internal static T Download<T>(string text, byte[] roots, Func<Stream, T> read, CancellationToken token)
    {
        while (!Monitor.TryEnter(Gate, 50)) token.ThrowIfCancellationRequested();
        try
        {
            EnsureAddress();
            return Exchange(text, roots, true, read, token);
        }
        finally { Monitor.Exit(Gate); }
    }

    internal static void EnsureAddress()
    {
        if (!NetworkManager.IsEnabled || !NetworkManager.IsInitialized) throw new IOException("Netzwerkdienst nicht initialisiert.");
        if (NetworkManager.DeviceCount == 0 || !NetworkManager.LinkUp) throw new IOException("Kein betriebsbereiter Netzwerkadapter.");
        if (NetworkManager.Primary.IPConfig == null || NetworkManager.Primary.IPConfig.Address.Equals(Address4.Zero))
            throw new IOException("IPv4 ist nicht konfiguriert: net static oder net dhcp verwenden.");
    }

    private static T Exchange<T>(string text, byte[] roots, bool idleDeadline, Func<Stream, T> read, CancellationToken token)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Nur HTTP- und HTTPS-URLs ohne Zugangsdaten oder Fragment werden unterstuetzt.");
        if (uri.Host.Length > 253 || uri.PathAndQuery.Length > 2048) throw new ArgumentException("URL ist zu lang.");
        // Erst nach der Pruefung: eine ungueltige URL soll nicht vorher die Wurzeln parsen.
        var clock = TlsTransport.StartDeadline(uri, roots);
        Address address = Resolve(uri.Host, token, clock);
        CheckDeadline(clock, token);
        CosmosHttpUpdateSource.RandomizeTcpStart();
        var socket = new Socket(System.Net.Sockets.AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Exception? failure = null; T result = default!;
        try
        {
            socket.Connect(IPAddress.Parse(address.ToString()!), uri.Port);
            CheckDeadline(clock, token);
            var transport = new PollingStream(socket, clock, idleDeadline, token);
            Stream stream = transport;
            if (uri.Scheme == "https")
            {
                stream = TlsTransport.Open(transport, uri.Host, CosmosSaltSource.Fill, roots, SystemClock.UtcNow);
                CheckDeadline(clock, token);
            }
            byte[] request = Encoding.ASCII.GetBytes($"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n");
            stream.Write(request, 0, request.Length);
            stream.Flush();
            result = read(stream);
            CheckDeadline(clock, token);
        }
        catch (Exception exception) { failure = exception; }
        // Kein close_notify: Eine gelesene Antwort ist laengengeprueft (HttpUpdateResponse), nach einem Fehler wird die
        // Verbindung verworfen. Hat der Server schon geschlossen, meldet Cosmos den Socket nie als schreibbar;
        // PollingStream.Write wartete bis zum 10-s-Zeitlimit, und Fetch scheiterte danach mit internal_error(80).
        try { socket.Close(0); } catch (Exception exception) { failure = CleanupFailure(failure, "TCP", exception); }
        if (failure != null) throw failure;
        return result;
    }

    private static void CheckDeadline(Stopwatch clock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (clock.ElapsedMilliseconds >= FetchTimeoutMilliseconds) throw new IOException("Netzwerk-Zeitlimit erreicht.");
    }

    private sealed class PollingStream(Socket socket, Stopwatch clock, bool idleDeadline, CancellationToken token) : Stream
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
            if (idleDeadline && received > 0) clock.Restart();
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
