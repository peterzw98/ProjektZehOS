using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class TaskShellCommandsTests
{
    private readonly BackgroundTaskService tasks = new();
    private readonly Output output = new();
    private ShellService shell = null!;

    [TestInitialize]
    public void Create() => shell = new ShellService(new TaskShellCommands(tasks, () => ["rr quantum=1"]).Commands);

    // Gestartete Aufgaben laufen als echte Threads; ohne Abbruch wuerden sie den Testlauf ueberdauern.
    [TestCleanup]
    public void Stop() => Assert.IsTrue(tasks.StopAll(2000), "Hintergrundaufgaben enden nicht.");

    [TestMethod]
    public void List_WithoutArgumentsAndWithList_PrintSameTask()
    {
        Assert.IsTrue(shell.ExecuteLine("tasks start wait", output).IsOK());
        output.Lines.Clear();

        Assert.IsTrue(shell.ExecuteLine("tasks", output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("tasks list", output).IsOK());

        Assert.AreEqual(2, output.Lines.Count, "Beide Formen zeigen genau die eine gestartete Aufgabe.");
        foreach (string line in output.Lines) StringAssert.StartsWith(line, "1 wait ", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Scheduler_PrintsEntriesFromCallback()
    {
        Assert.IsTrue(shell.ExecuteLine("tasks scheduler", output).IsOK());

        CollectionAssert.AreEqual(new[] { "rr quantum=1" }, output.Lines);
    }

    [TestMethod]
    public void StartBusy_ReportsIdAndCancelStopsThatTask()
    {
        Assert.IsTrue(shell.ExecuteLine("tasks start busy", output).IsOK());
        CollectionAssert.AreEqual(new[] { "Aufgabe 1 gestartet." }, output.Lines);
        Assert.AreEqual("busy", tasks.Snapshot()[0].Name);

        Assert.IsTrue(shell.ExecuteLine("tasks cancel 1", output).IsOK());

        Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot()[0].State is BackgroundTaskState.Cancelled or BackgroundTaskState.Completed, 2000),
            "Die abgebrochene Aufgabe laeuft weiter: " + tasks.Snapshot()[0].State);
    }

    [TestMethod]
    public void Cancel_UnknownId_ReportsNotFound()
    {
        var result = shell.ExecuteLine("tasks cancel 999", output);

        Assert.AreEqual(ResultCode.NotFound, result.Code);
        Assert.AreEqual(0, output.Lines.Count);
    }

    [DataTestMethod]
    [DataRow("tasks cancel x")]
    [DataRow("tasks cancel 1 2")]
    [DataRow("tasks start other")]
    [DataRow("tasks start")]
    [DataRow("tasks bogus")]
    public void InvalidArguments_ReturnUsageAndStartNothing(string line)
    {
        var result = shell.ExecuteLine(line, output);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code);
        StringAssert.StartsWith(result.Message, "Verwendung: tasks", StringComparison.Ordinal);
        Assert.AreEqual(0, tasks.Snapshot().Length, "Eine abgelehnte Eingabe darf keine Aufgabe starten.");
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
