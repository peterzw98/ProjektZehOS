using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class NetworkCommandTests
{
    [TestMethod]
    public void ConfigurationRequiresAdministrator()
    {
        var network = new FakeNetwork();
        var commands = new NetworkShellCommands(network, new BackgroundTaskService(), () => false).Commands;
        Assert.IsFalse(commands[1].Execute(["dhcp"], new Output()).IsOK());
        Assert.IsFalse(commands[1].Execute(["static", "1", "2", "3", "4"], new Output()).IsOK());
        Assert.AreEqual(0, network.Calls);
    }

    [TestMethod]
    public void WorkAndCancellationStayWithinSession()
    {
        var network = new FakeNetwork();
        var tasks = new BackgroundTaskService();
        var first = new NetworkShellCommands(network, tasks, () => true).Commands;
        var second = new NetworkShellCommands(network, tasks, () => true).Commands;
        var output = new Output();
        Assert.IsTrue(first[4].Execute(["http://example.test/"], output).IsOK());
        Assert.IsTrue(network.Started.WaitOne(2000));
        Assert.IsFalse(first[2].Execute(["1.2.3.4"], output).IsOK());
        Assert.IsFalse(second[1].Execute(["cancel"], output).IsOK());
        Assert.IsTrue(first[1].Execute(["cancel"], output).IsOK());
        Assert.IsTrue(SpinWait.SpinUntil(() => { first[1].Execute(["status"], output); return output.Last == "Netzwerkauftrag abgebrochen."; }, 2000));
        Assert.IsTrue(tasks.StopAll(2000));
    }

    private sealed class FakeNetwork : INetworkService
    {
        public int Calls;
        public ManualResetEvent Started { get; } = new(false);
        public ResultObject<string> Execute(string operation, string[] arguments, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls); Started.Set(); cancellation.WaitHandle.WaitOne(2000);
            return ResultObjectFactory.CreateSuccess("erledigt");
        }
    }
    private sealed class Output : ITextOutput
    {
        public string Last { get; private set; } = "";
        public void WriteLine(string text) => Last = text;
        public void Clear() { }
    }
}
