using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class NetworkAutoConfigurationTests
{
    [TestMethod]
    public void RequestsDhcpWhenLinkHasNoAddressAndStopsOnCancellation()
    {
        var network = new FakeNetwork(new(true, true, null));
        using var cancellation = new CancellationTokenSource();
        NetworkAutoConfiguration.Run(network, new(_ => { }, cancellation.Token), _ => cancellation.Cancel(), 1);
        Assert.AreEqual(1, network.Calls);
        Assert.AreEqual("auto-dhcp", network.Operation);
        Assert.AreEqual(0, network.Arguments!.Length);
    }

    [TestMethod]
    [DataRow(false, false, null)]
    [DataRow(true, false, null)]
    [DataRow(true, true, "10.0.2.15")]
    public void DoesNothingWithoutLinkOrWhenAddressExists(bool adapter, bool link, string? address)
    {
        var network = new FakeNetwork(new(adapter, link, address));
        var tasks = new BackgroundTaskService();
        using var read = new ManualResetEventSlim();
        network.Read = () => read.Set();
        Assert.IsTrue(tasks.Start("auto", context => NetworkAutoConfiguration.Run(network, context, _ => { }, 1)).IsOK());
        Assert.IsTrue(read.Wait(2000));
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual(0, network.Calls);
    }

    [TestMethod]
    public void LogsRepeatedFailureOnce()
    {
        var network = new FakeNetwork(new(true, true, null));
        var tasks = new BackgroundTaskService();
        var logs = new List<string>();
        Assert.IsTrue(tasks.Start("auto", context => NetworkAutoConfiguration.Run(network, context, text => logs.Add(text), 1)).IsOK());
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref network.Calls) >= 3, 2000));
        Assert.IsTrue(tasks.StopAll(2000));
        CollectionAssert.AreEqual(new[] { "kein DHCP" }, logs);
    }

    [TestMethod]
    [DataRow(false, false, null, "Kein Netzwerkadapter")]
    [DataRow(true, false, null, "Keine Verbindung")]
    [DataRow(true, true, null, "Suche nach Netzwerk ...")]
    [DataRow(true, true, "10.0.2.15", "Verbunden")]
    public void StatusDescribesConnection(bool adapter, bool link, string? address, string text)
        => Assert.AreEqual(text, new NetworkStatus(adapter, link, address).Text);

    [TestMethod]
    public void RestartAfterFailedPowerActionWaitsForPreviousDhcp()
    {
        var network = new FakeNetwork(new(true, true, null));
        var tasks = new BackgroundTaskService();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int active = 0, maximum = 0;
        network.ExecuteWork = token =>
        {
            int count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            entered.Set(); release.Wait(CancellationToken.None);
            Interlocked.Decrement(ref active);
            return ResultObjectFactory.CreateFailure<string>(ResultCode.Failure, "kein DHCP");
        };
        var automatic = new NetworkAutoConfiguration(network, tasks, _ => { });
        Assert.IsTrue(automatic.Start().IsOK());
        Assert.IsTrue(entered.Wait(2000));
        automatic.Stop();
        Assert.IsFalse(tasks.StopAll(0));
        Assert.IsTrue(automatic.Start().IsOK());
        Assert.AreEqual(1, Volatile.Read(ref network.Calls));
        release.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref network.Calls) >= 2, 2000));
        automatic.Stop();
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual(1, maximum);
    }

    private sealed class FakeNetwork(NetworkStatus status) : INetworkService
    {
        public int Calls;
        public string? Operation;
        public string[]? Arguments;
        public Action? Read;
        public Func<CancellationToken, ResultObject<string>>? ExecuteWork;
        public NetworkStatus Status { get { Read?.Invoke(); return status; } }
        public ResultObject<string> Execute(string operation, string[] arguments, CancellationToken cancellation)
        {
            Operation = operation; Arguments = arguments; Interlocked.Increment(ref Calls);
            if (ExecuteWork != null) return ExecuteWork(cancellation);
            return ResultObjectFactory.CreateFailure<string>(ResultCode.Failure, "kein DHCP");
        }
    }
}
