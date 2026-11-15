using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmNetworkTests
{
    private sealed class Connector : IWasmNetworkConnector
    {
        public Connection Last { get; private set; } = null!;
        public IWasmNetworkConnection Open(string host, int port) => Last = new Connection();
    }

    private sealed class Connection : IWasmNetworkConnection
    {
        public bool Closed { get; private set; }
        public int State { get; set; } = WasmNetworkError.Again;
        public int Status() => State;
        public int Send(ReadOnlySpan<byte> data, out int written) { written = Math.Min(data.Length, 2); return State; }
        public int Receive(Span<byte> data, out int read) { read = 0; return State; }
        public void Close() => Closed = true;
    }

    [TestMethod]
    public void NetworkAccessRequiresExplicitCapability()
    {
        var connector = new Connector();
        var session = new WasmNetworkSession(connector, false);
        Assert.AreEqual(WasmNetworkError.Access, session.Open("example.com", 80, 0, out int handle));
        Assert.AreEqual(0, handle);
        Assert.IsNull(connector.Last);
    }

    [TestMethod]
    public void RejectsInvalidEndpointsAndFlags()
    {
        var session = new WasmNetworkSession(new Connector(), true);
        Assert.AreEqual(WasmNetworkError.Invalid, session.Open("bad host", 80, 0, out _));
        Assert.AreEqual(WasmNetworkError.Invalid, session.Open("example.com", 0, 0, out _));
        Assert.AreEqual(WasmNetworkError.NotSupported, session.Open("example.com", 80, 1, out _));
    }

    [TestMethod]
    public void PendingPartialSendEofAndClosureUseSessionHandles()
    {
        var connector = new Connector();
        var session = new WasmNetworkSession(connector, true);
        Assert.AreEqual(WasmNetworkError.Success, session.Open("127.0.0.1", 80, 0, out int handle));
        Assert.AreEqual(WasmNetworkError.Again, session.Status(handle));
        connector.Last.State = WasmNetworkError.Success;
        Assert.AreEqual(WasmNetworkError.Success, session.Send(handle, [1, 2, 3], out int written));
        Assert.AreEqual(2, written);
        Assert.AreEqual(WasmNetworkError.Success, session.Receive(handle, new byte[8], out int read));
        Assert.AreEqual(0, read);
        Assert.AreEqual(WasmNetworkError.Success, session.Close(handle));
        Assert.IsTrue(connector.Last.Closed);
        Assert.AreEqual(WasmNetworkError.BadHandle, session.Status(handle));
    }

    [TestMethod]
    public void EnforcesLimitAndClosesPendingConnectionAtSessionEnd()
    {
        var connector = new Connector();
        var session = new WasmNetworkSession(connector, true);
        for (int i = 0; i < WasmNetworkSession.MaximumConnections; i++)
            Assert.AreEqual(WasmNetworkError.Success, session.Open("10.0.2.2", 80, 0, out _));
        var last = connector.Last;
        Assert.AreEqual(WasmNetworkError.TooManyFiles, session.Open("10.0.2.2", 80, 0, out _));
        session.CloseAll();
        Assert.IsTrue(last.Closed);
        Assert.AreEqual(WasmNetworkError.Access, session.Open("10.0.2.2", 80, 0, out _));
    }
}
