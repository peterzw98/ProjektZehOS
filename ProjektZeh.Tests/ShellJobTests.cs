using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class ShellJobTests
{
    [TestMethod]
    public void ResultIsDeliveredOnce()
    {
        var tasks = new BackgroundTaskService();
        var job = new ShellJob(tasks, "net", "Netzwerkauftrag");
        Assert.IsTrue(job.Start("net info", new Output(), _ => "Adresse").IsOK());
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual("Adresse", job.TakeResult());
        Assert.IsNull(job.TakeResult());
    }

    [TestMethod]
    public void SecondStartIsRejectedWhileRunningAndCancelReportsCancelled()
    {
        var tasks = new BackgroundTaskService();
        var job = new ShellJob(tasks, "net", "Netzwerkauftrag");
        using var started = new ManualResetEventSlim();
        var output = new Output();
        Assert.IsTrue(job.Start("net dhcp", output, token =>
        {
            started.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return "";
        }).IsOK());
        Assert.IsTrue(started.Wait(2000));
        Assert.IsFalse(job.Start("net info", output, _ => "falsch").IsOK());
        Assert.IsTrue(job.Cancel(output).IsOK());
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual("Netzwerkauftrag abgebrochen.", job.TakeResult());
    }

    [TestMethod]
    public void FailureIsReportedAndLogged()
    {
        var tasks = new BackgroundTaskService();
        string? logged = null;
        var job = new ShellJob(tasks, "pkg", "Paketauftrag", text => logged = text);
        Assert.IsTrue(job.Start("pkg", new Output(), _ => throw new InvalidOperationException("kaputt")).IsOK());
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual("Paketauftrag fehlgeschlagen: kaputt", job.TakeResult());
        Assert.AreEqual("Paketauftrag fehlgeschlagen: kaputt", logged);
    }

    [TestMethod]
    public void PollWritesFinishedResultAndClosedSessionDropsResult()
    {
        string? pending = "fertig";
        var shell = new ShellService { TakeJobResult = () => { var text = pending; pending = null; return text; } };
        var output = new Output();
        Assert.IsTrue(shell.Poll(output));
        Assert.AreEqual("fertig", output.Last);
        Assert.IsFalse(shell.Poll(output));
        shell.Close();
        pending = "geschlossen";
        Assert.IsFalse(shell.Poll(output));
        Assert.AreEqual("fertig", output.Last);
    }

    [TestMethod]
    public void CloseCancelsWorkAndDropsItsResult()
    {
        var tasks = new BackgroundTaskService();
        var job = new ShellJob(tasks, "net", "Netzwerkauftrag");
        Assert.IsTrue(job.Start("net dhcp", new Output(), token =>
        { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return ""; }).IsOK());
        job.Close();
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.IsNull(job.TakeResult());
        Assert.IsFalse(job.Start("net info", new Output(), _ => "falsch").IsOK());
    }

    [TestMethod]
    public void ResultFromPreviousIdentityIsDiscarded()
    {
        int generation = 1;
        var tasks = new BackgroundTaskService();
        var job = new ShellJob(tasks, "net", "Netzwerkauftrag", getSessionGeneration: () => generation);
        using var release = new ManualResetEventSlim();
        Assert.IsTrue(job.Start("net fetch", new Output(), _ => { release.Wait(CancellationToken.None); return "privater Inhalt"; }).IsOK());
        generation = 2;
        release.Set();
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.IsNull(job.TakeResult());
        Assert.IsTrue(job.Start("net info", new Output(), _ => "neue Sitzung").IsOK());
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual("neue Sitzung", job.TakeResult());
    }

    private sealed class Output : ITextOutput
    {
        public string? Last { get; private set; }
        public void WriteLine(string text) => Last = text;
        public void Clear() { }
    }
}
