using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            return IPAddress.Parse(host);
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
        public void CloseDoesNotBlockOnConnectAndOrphanIsClosedAfterReturn()
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
                CosmosWasmNetworkConnector.BeforeConnect = null;
            }
        }

        [TestMethod]
        public void TimedOutPendingConnectionDoesNotStartLater()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var session = new WasmNetworkSession(new CosmosWasmNetworkConnector(), true);
            lock (ProjektZeh.Network.CosmosNetworkService.Gate)
            {
                Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", port, 0, out int handle));
                var clock = Stopwatch.StartNew();
                while (session.Status(handle) == WasmNetworkError.Again && clock.ElapsedMilliseconds < 12000) Thread.Sleep(50);
                Assert.AreEqual(WasmNetworkError.TimedOut, session.Status(handle));
                Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
            }
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
            Assert.AreEqual(14, session.Status(handle));
            Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
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
