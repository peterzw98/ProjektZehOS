using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Cosmos.Kernel.System.Network;
using ProjektZeh.Security;
using ProjektZeh.Shared;

namespace ProjektZeh.Update;

internal sealed class CosmosHttpUpdateSource(UpdateEndpoint endpoint) : IUpdateSource
{
    private const string TcpConnectionType = "Cosmos.Kernel.System.Network.Protocols.Tcp.TcpConnection, Cosmos.Kernel.System";
    private static bool tcpStartRandomized;

    public ResultObject<byte[]> Read(string name, int maxBytes)
    {
        // Dateinamen stammen aus dem streng geprueften Manifest oder aus festen Namen; die Grenze setzt der Aufrufer.
        int limit = maxBytes;
        Socket? socket = null;
        NetworkStream? stream = null;
        DhcpClient? dhcp = null;
        DnsClient? dns = null;
        ResultObject<byte[]> result;
        try
        {
            if (!NetworkManager.IsEnabled || !NetworkManager.IsInitialized || NetworkManager.DeviceCount == 0 || !NetworkManager.LinkUp)
                return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, "Kein betriebsbereiter Netzwerkadapter.");
            if (NetworkManager.Primary.IPConfig == null || NetworkManager.Primary.IPConfig.Address.Equals(Address4.Zero))
            {
                dhcp = new DhcpClient();
                dhcp.SendDiscoverPacket();
                dhcp.Close();
                dhcp = null;
                if (NetworkManager.Primary.IPConfig == null || NetworkManager.Primary.IPConfig.Address.Equals(Address4.Zero))
                    throw new IOException("DHCP hat keine IPv4-Adresse geliefert.");
            }
            Address address;
            if (IPAddress.TryParse(endpoint.Host, out var numeric)) address = Address.Parse(numeric.ToString()) ?? throw new IOException("Ungueltige IPv4-Adresse.");
            else
            {
                if (DnsConfig.Nameservers.Count == 0) return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, "Kein DNS-Server konfiguriert.");
                dns = new DnsClient();
                dns.Connect(DnsConfig.Nameservers[0]);
                dns.SendQuery(endpoint.Host);
                address = dns.Receive(5000) ?? throw new IOException("DNS-Aufloesung fehlgeschlagen.");
                dns.Close();
                dns = null;
            }
            RandomizeTcpStart();
            socket = new Socket(System.Net.Sockets.AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = 10000,
                SendTimeout = 10000
            };
            socket.Connect(IPAddress.Parse(address.ToString()!), endpoint.Port);
            byte[] request = Encoding.ASCII.GetBytes($"GET /{name} HTTP/1.0\r\nHost: {endpoint}\r\nConnection: close\r\n\r\n");
            int sent = 0;
            while (sent < request.Length)
            {
                int count = socket.Send(request, sent, request.Length - sent, SocketFlags.None);
                if (count <= 0) throw new IOException("HTTP-Anfrage konnte nicht gesendet werden.");
                sent += count;
            }
            stream = new NetworkStream(socket, false);
            result = ResultObjectFactory.CreateSuccess(HttpUpdateResponse.Read(stream, limit));
        }
        catch (Exception exception) { result = ResultObjectFactory.CreateFromException<byte[]>(exception); }
        // Cosmos fuehrt verschachtelte finally-Bloecke nicht auf jedem Fehlerpfad aus.
        try { dhcp?.Close(); dns?.Close(); stream?.Dispose(); }
        catch (Exception exception) { result = CleanupFailure(result, exception); }
        try { socket?.Close(1000); }
        catch (Exception exception) { result = CleanupFailure(result, exception); }
        return result;
    }

    /// <summary>
    /// Cosmos 3.0.94 beginnt nach jedem Start mit lokalem Port 49152. Nach einem Warmstart kennen NAT und Gegenstelle
    /// dieses Verbindungstupel noch und verwerfen den neuen SYN; der Gast haengt dann im Verbindungsaufbau.
    /// </summary>
    private static void RandomizeTcpStart()
    {
        if (tcpStartRandomized) return;
        byte[] random = new byte[2];
        try { CosmosSaltSource.Fill(random); }
        catch (CryptographicException) { BitConverter.TryWriteBytes(random, Stopwatch.GetTimestamp()); }
        NextPort(null) = (ushort)(49152 + BitConverter.ToUInt16(random, 0) % 16000);
        tcpStartRandomized = true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_nextPort")]
    private static extern ref ushort NextPort([UnsafeAccessorType(TcpConnectionType)] object? connection);

    private static ResultObject<byte[]> CleanupFailure(ResultObject<byte[]> result, Exception exception) =>
        ResultObjectFactory.CreateFromException<byte[]>(new IOException(
            result.IsOK() ? "HTTP-Verbindung konnte nicht geschlossen werden." : result.Status + " HTTP-Bereinigung fehlgeschlagen.", exception));
}
