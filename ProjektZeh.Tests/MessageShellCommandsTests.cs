using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Messaging;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MessageShellCommandsTests
{
    private readonly BackgroundTaskService tasks = new();
    private readonly Output output = new();
    private MessageDemoService demo = null!;
    private ShellService shell = null!;

    [TestInitialize]
    public void Create()
    {
        demo = new MessageDemoService(tasks);
        shell = new ShellService(new MessageShellCommands(demo).Commands);
    }

    // Die Vorfuehrung laeuft in echten Threads; ohne Abbruch wuerde sie den Testlauf ueberdauern.
    [TestCleanup]
    public void Stop()
    {
        demo.Stop();
        Assert.IsTrue(tasks.StopAll(2000), "Hintergrundaufgaben enden nicht.");
    }

    [DataTestMethod]
    [DataRow("messages start slow-producer")]
    [DataRow("messages start slow-consumer")]
    public void Start_KnownMode_DescribesDemoAndStartsBothTasks(string line)
    {
        Assert.IsTrue(shell.ExecuteLine(line, output).IsOK());

        Assert.AreEqual(1, output.Lines.Count);
        StringAssert.StartsWith(output.Lines[0], "24 nummerierte Nachrichten, Puffer 3.", StringComparison.Ordinal);
        var names = Array.ConvertAll(tasks.Snapshot(), task => task.Name);
        CollectionAssert.AreEquivalent(new[] { "messages-producer", "messages-consumer" }, names);
    }

    [TestMethod]
    public void Start_WhileRunning_FailsWithoutSecondDescription()
    {
        Assert.IsTrue(shell.ExecuteLine("messages start slow-producer", output).IsOK());

        var second = shell.ExecuteLine("messages start slow-consumer", output);

        Assert.AreEqual(ResultCode.Failure, second.Code);
        Assert.AreEqual(1, output.Lines.Count, "Die Beschreibung erscheint nur fuer den gelungenen Start.");
        Assert.AreEqual(2, tasks.Snapshot().Length);
    }

    [TestMethod]
    public void Status_PrintsCountersOfTheDemo()
    {
        Assert.IsTrue(shell.ExecuteLine("messages status", output).IsOK());

        CollectionAssert.AreEqual(new[] { demo.Status() }, output.Lines);
        StringAssert.StartsWith(output.Lines[0], "messages: active=0 produced=0 ", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Stop_RunningDemo_EndsBothTasks()
    {
        Assert.IsTrue(shell.ExecuteLine("messages start slow-consumer", output).IsOK());
        output.Lines.Clear();

        Assert.IsTrue(shell.ExecuteLine("messages stop", output).IsOK());

        CollectionAssert.AreEqual(new[] { "Abbruch angefordert." }, output.Lines);
        Assert.IsTrue(SpinWait.SpinUntil(() => demo.Status().Contains("active=0", StringComparison.Ordinal), 2000), demo.Status());
    }

    [TestMethod]
    public void Close_RunningDemo_LetsConsumerFinishBufferedMessages()
    {
        Assert.IsTrue(shell.ExecuteLine("messages start slow-consumer", output).IsOK());
        output.Lines.Clear();

        Assert.IsTrue(shell.ExecuteLine("messages close", output).IsOK());

        CollectionAssert.AreEqual(new[] { "Puffer geschlossen; vorhandene Nachrichten werden noch verarbeitet." }, output.Lines);
        Assert.IsTrue(SpinWait.SpinUntil(() => demo.Status().Contains("active=0", StringComparison.Ordinal), 5000), demo.Status());
        StringAssert.Contains(demo.Status(), "buffered=0/3", StringComparison.Ordinal);
    }

    [DataTestMethod]
    [DataRow("messages")]
    [DataRow("messages start")]
    [DataRow("messages start fast")]
    [DataRow("messages status now")]
    [DataRow("messages bogus")]
    public void InvalidArguments_ReturnUsageAndStartNothing(string line)
    {
        var result = shell.ExecuteLine(line, output);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code);
        StringAssert.StartsWith(result.Message, "Verwendung: messages", StringComparison.Ordinal);
        Assert.AreEqual(0, output.Lines.Count);
        Assert.AreEqual(0, tasks.Snapshot().Length, "Eine abgelehnte Eingabe darf keine Aufgabe starten.");
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
