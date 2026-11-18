using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Network
{
    internal static class CosmosNetworkService
    {
        internal static readonly object Gate = new();
        internal static ManualResetEventSlim? ResolveEntered;
        internal static ManualResetEventSlim? ResolveContinue;
        internal static void EnsureAddress() { }
        internal static IPAddress Resolve(string host, CancellationToken token)
        {
            ResolveEntered?.Set();
            ResolveContinue?.Wait(5000, token);
            return host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(host);
        }
    }
}

namespace ProjektZeh.Update
{
    internal static class CosmosHttpUpdateSource
    {
        internal static void RandomizeTcpStart() { }
    }
}

namespace ProjektZeh.Tests
{
    [TestClass]
    public sealed class WasmNetworkHostTests
    {
        private static void WaitFor(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (!ready() && clock.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            Assert.IsTrue(ready(), "Netzwerkzustand wurde nicht rechtzeitig erreicht.");
        }

        [TestMethod]
        public void ClosedPendingConnectionDoesNotConnect()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            lock (ProjektZeh.Network.CosmosNetworkService.Gate)
            {
                Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
                Assert.AreEqual(WasmNetworkError.Again, session.Status(handle));
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            }
            Thread.Sleep(100);
            Assert.IsFalse(listener.Pending(), "Eine geschlossene ausstehende Verbindung darf nicht starten.");
        }

        [TestMethod]
        public void CloseReturnsWhileResolutionIsBlockedAndPreventsConnect()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            ProjektZeh.Network.CosmosNetworkService.ResolveEntered = entered;
            ProjektZeh.Network.CosmosNetworkService.ResolveContinue = proceed;
            try
            {
                var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
                Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
                Assert.IsTrue(entered.Wait(5000));
                var clock = Stopwatch.StartNew();
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
                Assert.IsTrue(clock.ElapsedMilliseconds < 500, "Close darf nicht auf DNS warten.");
                proceed.Set();
                Thread.Sleep(100);
                Assert.IsFalse(listener.Pending(), "Nach Abbruch waehrend DNS darf kein Connect starten.");
            }
            finally
            {
                proceed.Set();
                ProjektZeh.Network.CosmosNetworkService.ResolveEntered = null;
                ProjektZeh.Network.CosmosNetworkService.ResolveContinue = null;
            }
        }

        [TestMethod]
        public void CloseBeforeConnectPreventsConnection()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            CosmosWasmNetworkConnector.BeforeConnect = () => { entered.Set(); proceed.Wait(5000); };
            try
            {
                var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
                Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
                Assert.IsTrue(entered.Wait(5000));
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
                proceed.Set();
                Thread.Sleep(100);
                Assert.IsFalse(listener.Pending());
            }
            finally
            {
                proceed.Set();
                CosmosWasmNetworkConnector.BeforeConnect = null;
            }
        }

        [TestMethod]
        public void CloseDoesNotBlockOnConnectAndOrphanIsClosedAfterReturn()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            CosmosWasmNetworkConnector.AfterConnect = () => { entered.Set(); proceed.Wait(5000); };
            try
            {
                var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
                Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
                Assert.IsTrue(entered.Wait(5000));
                var clock = Stopwatch.StartNew();
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
                Assert.IsTrue(clock.ElapsedMilliseconds < 500, "Close darf nicht auf Connect warten.");
                proceed.Set();
                WaitFor(listener.Pending);
                using Socket peer = listener.AcceptSocket();
                peer.ReceiveTimeout = 5000;
                try { Assert.AreEqual(0, peer.Receive(new byte[1]), "Verwaister Socket muss geschlossen werden."); }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset) { }
            }
            finally
            {
                proceed.Set();
                CosmosWasmNetworkConnector.AfterConnect = null;
            }
        }

        [TestMethod]
        public void TimedOutPendingConnectionDoesNotStartLater()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            // Kurze Frist statt der 10 s des Kernels; geprueft wird das Verhalten nach Ablauf, nicht die Dauer.
            CosmosWasmNetworkConnector.TimeoutMs = 1000;
            try
            {
                lock (ProjektZeh.Network.CosmosNetworkService.Gate)
                {
                    Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
                    var clock = Stopwatch.StartNew();
                    while (session.Status(handle) == WasmNetworkError.Again && clock.ElapsedMilliseconds < 5000) Thread.Sleep(50);
                    Assert.AreEqual(WasmNetworkError.TimedOut, session.Status(handle));
                    Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
                }
            }
            finally { CosmosWasmNetworkConnector.TimeoutMs = 10000; }
            Thread.Sleep(100);
            Assert.IsFalse(listener.Pending(), "Ein abgelaufener Verbindungsversuch darf nicht spaeter starten.");
        }

        [TestMethod]
        public void WorkerReportsDnsFailureSeparately()
        {
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            Assert.AreEqual(WasmNetworkError.Success, session.Open("missing.invalid", 80, 0, out int handle));
            WaitFor(() => session.Status(handle) != WasmNetworkError.Again);
            Assert.AreEqual(WasmNetworkError.NameNotFound, session.Status(handle));
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
        }

        [TestMethod]
        public void WorkerReportsRefusedConnection()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
            WaitFor(() => session.Status(handle) != WasmNetworkError.Again);
            Assert.AreEqual(WasmNetworkError.ConnectionRefused, session.Status(handle));
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
        }

        [TestMethod]
        public void WorkerReportsResetConnection()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
            using (Socket peer = listener.AcceptSocket())
            {
                // Linger mit Frist 0 schliesst mit RST statt FIN.
                peer.LingerState = new LingerOption(true, 0);
            }
            WaitFor(() => session.Receive(handle, new byte[1], out _) is not (WasmNetworkError.Again or WasmNetworkError.Success));
            Assert.AreEqual(WasmNetworkError.ConnectionReset, session.Receive(handle, new byte[1], out int count));
            Assert.AreEqual(0, count);
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
        }

        private sealed class TestAuthority : IDisposable
        {
            private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            internal X509Certificate2 Root { get; }

            internal TestAuthority(string name)
            {
                var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(30));
            }

            internal byte[] Pem => Encoding.ASCII.GetBytes(Root.ExportCertificatePem());

            internal X509Certificate2 Issue(string address, DateTimeOffset notBefore, DateTimeOffset notAfter)
            {
                using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest("CN=server", leafKey, HashAlgorithmName.SHA256);
                var names = new SubjectAlternativeNameBuilder();
                if (IPAddress.TryParse(address, out IPAddress? ip)) names.AddIpAddress(ip);
                else names.AddDnsName(address);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
                byte[] serial = RandomNumberGenerator.GetBytes(8);
                using X509Certificate2 leaf = request.Create(Root, notBefore, notAfter, serial);
                // SslStream braucht unter Linux einen exportierbaren Schluessel; PFX-Umweg wie in den .NET-Beispielen.
                return X509CertificateLoader.LoadPkcs12(leaf.CopyWithPrivateKey(leafKey).Export(X509ContentType.Pfx), null);
            }

            internal X509Certificate2 Issue(string address) =>
                Issue(address, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

            public void Dispose() { Root.Dispose(); key.Dispose(); }
        }

        private static WasmNetworkSession TlsSession(byte[] trustedPem)
        {
            var anchors = TlsTransport.LoadAnchors(trustedPem);
            return new(new CosmosWasmNetworkConnector(host => CosmosWasmNetworkConnector.CreateAppClient(host,
                bytes => RandomNumberGenerator.Fill(bytes), () => anchors, DateTime.UtcNow)), true);
        }

        // Antwortet nach der Anfrage mit response und beendet mit oder ohne close_notify.
        private static (int Port, Task Done) TlsServer(TcpListener listener, X509Certificate2 certificate, byte[] response, bool closeNotify)
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task done = Task.Run(async () =>
            {
                using Socket peer = listener.AcceptSocket();
                using var stream = new SslStream(new NetworkStream(peer, false));
                try
                {
                    await stream.AuthenticateAsServerAsync(certificate);
                    var request = new byte[4];
                    await stream.ReadExactlyAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    await stream.WriteAsync(response);
                    if (closeNotify)
                    {
                        await stream.ShutdownAsync();
                        // Den Peer erst nach seinem Ende schliessen: SChannel verwirft sonst noch ausstehende Daten.
                        while (await stream.ReadAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) != 0) { }
                    }
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
            });
            return (port, done);
        }

        private static byte[] ReadAll(WasmNetworkSession session, int handle, out int result)
        {
            var data = new System.Collections.Generic.List<byte>();
            var buffer = new byte[WasmNetworkSession.MaximumTransferBytes];
            int last = WasmNetworkError.Again;
            WaitFor(() =>
            {
                last = session.Receive(handle, buffer, out int count);
                for (int i = 0; i < count; i++) data.Add(buffer[i]);
                return last != WasmNetworkError.Again && (last != WasmNetworkError.Success || count == 0);
            });
            result = last;
            return data.ToArray();
        }

        private static int Handshake(WasmNetworkSession session, int port, out int handle, string host = "127.0.0.1")
        {
            Assert.AreEqual(WasmNetworkError.Success, session.Open(host, port, WasmNetworkSession.FlagTls, out handle));
            int id = handle;
            WaitFor(() => session.Status(id) != WasmNetworkError.Again);
            return session.Status(handle);
        }

        [TestMethod]
        public void TlsTransfersLargeResponseAndEndsWithCloseNotify()
        {
            using var authority = new TestAuthority("ZehOS Test-CA");
            using X509Certificate2 certificate = authority.Issue("127.0.0.1");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            byte[] response = new byte[3 * WasmNetworkSession.MaximumTransferBytes + 17];
            for (int i = 0; i < response.Length; i++) response[i] = (byte)i;
            var (port, done) = TlsServer(listener, certificate, response, true);
            var session = TlsSession(authority.Pem);
            Assert.AreEqual(WasmNetworkError.Success, Handshake(session, port, out int handle));
            Assert.AreEqual(WasmNetworkError.Success, session.Send(handle, "ping"u8, out int written));
            Assert.AreEqual(4, written);
            CollectionAssert.AreEqual(response, ReadAll(session, handle, out int result));
            Assert.AreEqual(WasmNetworkError.Success, result, "close_notify ist ein regulaeres Ende.");
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            Assert.IsTrue(done.Wait(5000));
        }

        [TestMethod]
        public void TlsEndWithoutCloseNotifyReportsResetAfterData()
        {
            using var authority = new TestAuthority("ZehOS Test-CA");
            using X509Certificate2 certificate = authority.Issue("127.0.0.1");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var (port, done) = TlsServer(listener, certificate, "pong"u8.ToArray(), false);
            var session = TlsSession(authority.Pem);
            Assert.AreEqual(WasmNetworkError.Success, Handshake(session, port, out int handle));
            Assert.AreEqual(WasmNetworkError.Success, session.Send(handle, "ping"u8, out _));
            CollectionAssert.AreEqual("pong"u8.ToArray(), ReadAll(session, handle, out int result));
            Assert.AreEqual(WasmNetworkError.ConnectionReset, result);
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            Assert.IsTrue(done.Wait(5000));
        }

        [TestMethod]
        public void TlsRejectsWrongNameForeignAuthorityAndExpiredCertificate()
        {
            using var authority = new TestAuthority("ZehOS Test-CA");
            using var foreign = new TestAuthority("Fremde CA");
            using X509Certificate2 wrongName = authority.Issue("10.9.9.9");
            using X509Certificate2 foreignCertificate = foreign.Issue("127.0.0.1");
            using X509Certificate2 expired = authority.Issue("127.0.0.1", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            foreach (X509Certificate2 certificate in new[] { wrongName, foreignCertificate, expired })
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var (port, done) = TlsServer(listener, certificate, [], true);
                var session = TlsSession(authority.Pem);
                Assert.AreEqual(WasmNetworkError.NotPermitted, Handshake(session, port, out int handle), certificate.Subject);
                Assert.AreEqual(WasmNetworkError.NotPermitted, session.Send(handle, "ping"u8, out int written));
                Assert.AreEqual(0, written);
                Assert.IsTrue(done.Wait(5000));
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            }
        }

        [TestMethod]
        public void TlsPeerWithoutTlsAndAbortedHandshakeAreReported()
        {
            static void ReadClientHello(Socket peer)
            {
                // Ungelesene TCP-Daten fuehren unter Windows beim Schliessen zu RST statt zum beabsichtigten FIN.
                using var transport = new NetworkStream(peer, false);
                var header = new byte[5];
                transport.ReadExactly(header);
                transport.ReadExactly(new byte[(header[3] << 8) | header[4]]);
            }

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var authority = new TestAuthority("ZehOS Test-CA");
            Task plain = Task.Run(() =>
            {
                using Socket peer = listener.AcceptSocket();
                ReadClientHello(peer);
                peer.Send("HTTP/1.0 400 Bad Request\r\n\r\n"u8.ToArray());
                peer.Shutdown(SocketShutdown.Both);
            });
            var session = TlsSession(authority.Pem);
            Assert.AreEqual(WasmNetworkError.Protocol, Handshake(session, port, out int handle));
            Assert.IsTrue(plain.Wait(5000));
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            Task aborted = Task.Run(() =>
            {
                using Socket peer = listener.AcceptSocket();
                ReadClientHello(peer);
                peer.Shutdown(SocketShutdown.Both);
            });
            Assert.AreEqual(WasmNetworkError.ConnectionReset, Handshake(session, port, out handle));
            Assert.IsTrue(aborted.Wait(5000));
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
        }

        [TestMethod]
        public void TlsHandshakeDeadlineHoldsWithoutStatusCalls()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var authority = new TestAuthority("ZehOS Test-CA");
            // Der Server nimmt die Verbindung an und schweigt laenger als die (fuer den Test verkuerzte) Frist; die App
            // fragt nur Send ab, nie Status.
            CosmosWasmNetworkConnector.TimeoutMs = 1000;
            try
            {
                Task silent = Task.Run(() =>
                {
                    using Socket peer = listener.AcceptSocket();
                    Thread.Sleep(2500);
                });
                var session = TlsSession(authority.Pem);
                Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, WasmNetworkSession.FlagTls, out int handle));
                var clock = Stopwatch.StartNew();
                int result = WasmNetworkError.Again;
                while (result == WasmNetworkError.Again && clock.ElapsedMilliseconds < 5000)
                {
                    result = session.Send(handle, "ping"u8, out _);
                    Thread.Sleep(50);
                }
                Assert.AreEqual(WasmNetworkError.TimedOut, result);
                Assert.IsTrue(silent.Wait(5000));
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            }
            finally { CosmosWasmNetworkConnector.TimeoutMs = 10000; }
        }

        [TestMethod]
        public void TlsSendsHostAsSniAndChecksDnsName()
        {
            using var authority = new TestAuthority("ZehOS Test-CA");
            foreach (var (name, expected) in new[] { ("localhost", WasmNetworkError.Success), ("other.test", WasmNetworkError.NotPermitted) })
            {
                using X509Certificate2 certificate = authority.Issue(name);
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string? sni = null;
                Task server = Task.Run(async () =>
                {
                    using Socket peer = listener.AcceptSocket();
                    using var stream = new SslStream(new NetworkStream(peer, false));
                    var options = new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, requested) => { sni = requested; return certificate; },
                    };
                    try { await stream.AuthenticateAsServerAsync(options); await stream.ShutdownAsync(); }
                    catch (AuthenticationException) { }
                    catch (IOException) { }
                });
                var session = TlsSession(authority.Pem);
                Assert.AreEqual(expected, Handshake(session, port, out int handle, "localhost"), name);
                Assert.IsTrue(server.Wait(5000));
                Assert.AreEqual("localhost", sni, "Der Hostname aus net_open ist zugleich SNI.");
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            }
        }

        [TestMethod]
        public void TlsServerAlertIsProtocolError()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var authority = new TestAuthority("ZehOS Test-CA");
            Task server = Task.Run(() =>
            {
                using Socket peer = listener.AcceptSocket();
                peer.Receive(new byte[4096]);
                // Fataler Alert handshake_failure(40) statt ServerHello.
                peer.Send(new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 });
                Thread.Sleep(200);
                peer.Shutdown(SocketShutdown.Both);
            });
            var session = TlsSession(authority.Pem);
            Assert.AreEqual(WasmNetworkError.Protocol, Handshake(session, port, out int handle));
            Assert.IsTrue(server.Wait(5000));
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
        }

        [TestMethod]
        public void AppClientReportsMissingClockRandomOrAnchorsAsNoSystem()
        {
            using var authority = new TestAuthority("ZehOS Test-CA");
            var anchors = TlsTransport.LoadAnchors(authority.Pem);
            Action<byte[]> random = bytes => RandomNumberGenerator.Fill(bytes);
            var cases = new Func<string, Org.BouncyCastle.Tls.TlsClient>[]
            {
                host => CosmosWasmNetworkConnector.CreateAppClient(host, random, () => anchors, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
                host => CosmosWasmNetworkConnector.CreateAppClient(host, _ => throw new InvalidOperationException("kein RDRAND"), () => anchors, DateTime.UtcNow),
                host => CosmosWasmNetworkConnector.CreateAppClient(host, random, () => throw new InvalidOperationException("Bundle fehlt"), DateTime.UtcNow),
            };
            foreach (var factory in cases)
            {
                var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(factory), true);
                Assert.AreEqual(WasmNetworkError.NoSystem, session.Open("127.0.0.1", 443, WasmNetworkSession.FlagTls, out int handle));
                Assert.AreEqual(0, handle);
            }
            var ready = new WasmNetworkSession(new CosmosWasmNetworkConnector(host =>
                CosmosWasmNetworkConnector.CreateAppClient(host, random, () => anchors, DateTime.UtcNow)), true);
            Assert.AreEqual(WasmNetworkError.Success, ready.Open("127.0.0.1", 9, WasmNetworkSession.FlagTls, out _));
            ready.CloseAll();
        }

        [TestMethod]
        public void TlsWithoutFactoryReportsNoSystem()
        {
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            Assert.AreEqual(WasmNetworkError.NoSystem, session.Open("127.0.0.1", 443, WasmNetworkSession.FlagTls, out int handle));
            Assert.AreEqual(0, handle);
        }

        [TestMethod]
        public void WorkerConnectsTransfersFragmentedDataAndCloses()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<byte[]> server = Task.Run(() =>
            {
                using Socket peer = listener.AcceptSocket();
                peer.ReceiveTimeout = 4000;
                var request = new byte[3];
                int count = 0;
                while (count < request.Length) count += peer.Receive(request, count, request.Length - count, SocketFlags.None);
                peer.Send(new byte[] { 4, 5 });
                peer.Send(new byte[] { 6 });
                peer.Shutdown(SocketShutdown.Send);
                return request;
            });
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
            WaitFor(() => session.Status(handle) != WasmNetworkError.Again);
            Assert.AreEqual(WasmNetworkError.Success, session.Status(handle));
            Assert.AreEqual(WasmNetworkError.Success, session.Send(handle, new byte[] { 1, 2, 3 }, out int written));
            Assert.AreEqual(3, written);
            var response = new byte[3];
            int total = 0;
            WaitFor(() =>
            {
                int result = session.Receive(handle, response.AsSpan(total), out int count);
                total += count;
                return result == WasmNetworkError.Success && total == response.Length;
            });
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, response);
            WaitFor(() => session.Receive(handle, new byte[1], out int count) == WasmNetworkError.Success && count == 0);
            Assert.IsTrue(server.Wait(5000));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, server.Result);
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
        }
    }
}
