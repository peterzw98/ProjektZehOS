using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;
using TlsFatalAlert = Org.BouncyCastle.Tls.TlsFatalAlert;
using AlertDescription = Org.BouncyCastle.Tls.AlertDescription;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class TlsTransportTests
{
    [TestMethod]
    public void Read_MissingResource_ThrowsWithResourceName()
    {
        var error = Assert.ThrowsException<InvalidOperationException>(() => TlsRoots.Read("fehlt"));

        StringAssert.Contains(error.Message, "fehlt", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Load_ShippedRootBundle_IsCachedAndYieldsTrustAnchors()
    {
        byte[] bundle = TlsRoots.Load();

        Assert.AreSame(bundle, TlsRoots.Load(), "Das Bundle wird je Start nur einmal gelesen.");
        // cacert.pem enthaelt ueber hundert Wurzeln; deutlich weniger hiesse, dass das Einlesen abbricht.
        Assert.IsTrue(TlsTransport.LoadAnchors(bundle).Count > 100);
        Assert.AreSame(TlsTransport.CachedAnchors(bundle), TlsTransport.CachedAnchors(bundle));
    }

    [TestMethod]
    public void StartDeadline_Https_ParsesRootsBeforeTheClockStarts()
    {
        // Eigene Kopie: so ist der Cache vor dem Aufruf sicher leer fuer genau dieses Array.
        byte[] bundle = (byte[])TlsRoots.Load().Clone();
        var parse = System.Diagnostics.Stopwatch.StartNew();
        var deadline = TlsTransport.StartDeadline(new Uri("https://packages.example/catalog"), bundle);
        long total = parse.ElapsedTicks;

        // Die Frist-Uhr laeuft erst nach dem Parsen an: sie hat hoechstens den Bruchteil seit der Rueckkehr gesehen.
        Assert.IsTrue(deadline.ElapsedTicks < total / 2, $"Die Uhr lief schon waehrend des Wurzelparsens ({deadline.ElapsedTicks} von {total} Ticks).");
        var probe = System.Diagnostics.Stopwatch.StartNew();
        TlsTransport.CachedAnchors(bundle);
        Assert.IsTrue(probe.ElapsedMilliseconds < 50, "Nach StartDeadline muss der Abruf den Cache treffen.");
    }

    [TestMethod]
    public void StartDeadline_Http_DoesNotParseRoots()
    {
        byte[] invalid = [1, 2, 3];
        // HTTP braucht keine Wurzeln; ungueltige Bytes duerfen deshalb nicht geparst werden.
        Assert.IsTrue(TlsTransport.StartDeadline(new Uri("http://10.0.2.2/"), invalid).IsRunning);
    }

    [TestMethod]
    public async Task TlsChecksChainNameAndDates()
    {
        using RSA caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=ZehOS Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using X509Certificate2 ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(30));
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using X509Certificate2 unsigned = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), [1, 2, 3]);
        using X509Certificate2 withKey = unsigned.CopyWithPrivateKey(key);
        // SChannel unter Windows lehnt fluechtige Schluessel als Serverzertifikat ab ("platform does not support
        // ephemeral keys"); ueber PFX geladen ist der Schluessel gespeichert und der Test laeuft auf allen Plattformen.
        using X509Certificate2 serverCert = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        byte[] roots = Encoding.ASCII.GetBytes(ca.ExportCertificatePem());
        Assert.ThrowsException<InvalidOperationException>(() => TlsTransport.Open(new MemoryStream(), "localhost", bytes => RandomNumberGenerator.Fill(bytes), roots, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.ThrowsException<CryptographicException>(() => TlsTransport.Open(new MemoryStream(), "localhost", _ => throw new CryptographicException("Keine Entropie"), roots, DateTime.UtcNow));
        await Check(serverCert, roots, "localhost", DateTime.UtcNow, true);
        await Check(serverCert, roots, "localhost", DateTime.UtcNow, true, SslProtocols.Tls12);
        await Check(serverCert, roots, "wrong.example", DateTime.UtcNow, false);
        await Check(serverCert, roots, "localhost", DateTime.UtcNow.AddDays(2), false);
        await Check(serverCert, roots, "localhost", DateTime.UtcNow.AddDays(-2), false);
        using RSA otherKey = RSA.Create(2048);
        var otherRequest = new CertificateRequest("CN=Other CA", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        otherRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 other = otherRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(30));
        await Check(serverCert, Encoding.ASCII.GetBytes(other.ExportCertificatePem()), "localhost", DateTime.UtcNow, false);
    }

    private static async Task Check(X509Certificate2 certificate, byte[] roots, string host, DateTime now, bool expected, SslProtocols protocols = SslProtocols.Tls13)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(accepted.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = protocols });
                await ssl.WriteAsync(new byte[] { 42 });
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException) { }
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        bool succeeded = false;
        Exception? failure = null;
        try
        {
            Stream tls = TlsTransport.Open(client.GetStream(), host, bytes => RandomNumberGenerator.Fill(bytes), roots, now);
            succeeded = tls.ReadByte() == 42;
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException) { failure = exception; }
        listener.Stop();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(expected, succeeded);
        if (!expected)
        {
            bool badCertificate = false;
            for (Exception? current = failure; current != null; current = current.InnerException)
                if (current is TlsFatalAlert alert && alert.AlertDescription == AlertDescription.bad_certificate) badCertificate = true;
            Assert.IsTrue(badCertificate, failure?.ToString());
        }
    }
}
