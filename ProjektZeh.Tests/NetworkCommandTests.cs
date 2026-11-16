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
        var firstSession = new NetworkShellCommands(network, tasks, () => true);
        var first = firstSession.Commands;
        var second = new NetworkShellCommands(network, tasks, () => true).Commands;
        var output = new Output();
        Assert.IsTrue(first[4].Execute(["http://example.test/"], output).IsOK());
        Assert.IsTrue(network.Started.WaitOne(2000));
        Assert.IsFalse(first[2].Execute(["1.2.3.4"], output).IsOK());
        Assert.IsFalse(second[1].Execute(["cancel"], output).IsOK());
        Assert.IsTrue(first[1].Execute(["cancel"], output).IsOK());
        Assert.IsTrue(SpinWait.SpinUntil(() => firstSession.TakeResult() == "Netzwerkauftrag abgebrochen.", 2000));
        Assert.IsTrue(tasks.StopAll(2000));
    }

    [TestMethod]
    public void ResultIsLoggedWhenTaskEnds()
    {
        using var logged = new ManualResetEventSlim();
        string? text = null;
        var tasks = new BackgroundTaskService();
        var commands = new NetworkShellCommands(new FakeNetwork(), tasks, () => true, value => { text = value; logged.Set(); }).Commands;
        Assert.IsTrue(commands[1].Execute(["dhcp"], new Output()).IsOK());
        Assert.IsTrue(commands[1].Execute(["cancel"], new Output()).IsOK());
        Assert.IsTrue(logged.Wait(2000));
        Assert.AreEqual("Netzwerkauftrag abgebrochen.", text);
        Assert.IsTrue(tasks.StopAll(2000));
    }

    [TestMethod]
    public void StatusIsNoLongerACommand()
    {
        var commands = new NetworkShellCommands(new FakeNetwork(), new BackgroundTaskService(), () => true).Commands;
        Assert.AreEqual(ResultCode.InvalidArgument, commands[1].Execute(["status"], new Output()).Code);
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
