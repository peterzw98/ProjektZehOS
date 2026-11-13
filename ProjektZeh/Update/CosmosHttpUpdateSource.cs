using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cosmos.Kernel.System.Network;
using ProjektZeh.Shared;

namespace ProjektZeh.Update;

internal sealed class CosmosHttpUpdateSource(UpdateEndpoint endpoint) : IUpdateSource
{
    internal const int MaximumKernelBytes = 64 * 1024 * 1024;

    public ResultObject<byte[]> Read(string name)
    {
        // Dateinamen stammen aus dem streng geprueften Manifest oder aus diesen beiden festen Namen.
        int limit = name == "update-manifest.txt" ? 4096 : name == "update-manifest.sig" ? 64 : MaximumKernelBytes;
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

    private static ResultObject<byte[]> CleanupFailure(ResultObject<byte[]> result, Exception exception) =>
        ResultObjectFactory.CreateFromException<byte[]>(new IOException(
            result.IsOK() ? "HTTP-Verbindung konnte nicht geschlossen werden." : result.Status + " HTTP-Bereinigung fehlgeschlagen.", exception));
}
