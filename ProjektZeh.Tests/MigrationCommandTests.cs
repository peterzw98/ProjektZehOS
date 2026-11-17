using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MigrationCommandTests
{
    [TestMethod]
    public void OnlyTheExplicitConfirmedSyntaxStartsWork()
    {
        var service = new FakeMigration();
        var tasks = new BackgroundTaskService();
        var commands = new MigrationShellCommands(service, tasks, () => true, () => true);
        foreach (string[] arguments in new[] { Array.Empty<string>(), ["migrate", "sata1"],
            ["migrate", "sata1", "--erase"], ["migrate", "sata1", "--yes", "extra"], ["migrate", "", "--yes"] })
            Assert.AreEqual(ResultCode.InvalidArgument, commands.Commands[0].Execute(arguments, new Output()).Code);
        Assert.AreEqual(0, service.Calls);
        Assert.IsTrue(commands.Commands[0].Execute(["migrate", "sata1", "--yes"], new Output()).IsOK());
        Assert.IsTrue(service.Started.Wait(2000));
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual(1, service.Calls);
        Assert.AreEqual("sata1", service.Disk);
    }

    [TestMethod]
    public void AdministratorAndLegacyAreRequiredBeforeCallingTheService()
    {
        var service = new FakeMigration();
        var tasks = new BackgroundTaskService();
        foreach (var state in new[] { (Admin: false, Legacy: true), (Admin: true, Legacy: false) })
        {
            var commands = new MigrationShellCommands(service, tasks, () => state.Admin, () => state.Legacy);
            Assert.IsFalse(commands.Commands[0].Execute(["migrate", "sata1", "--yes"], new Output()).IsOK());
        }
        Assert.AreEqual(0, service.Calls);
    }

    [TestMethod]
    public void CancellationBelongsToTheStartingShell()
    {
        var service = new FakeMigration { WaitForCancellation = true };
        var tasks = new BackgroundTaskService();
        var first = new MigrationShellCommands(service, tasks, () => true, () => true);
        var other = new MigrationShellCommands(service, tasks, () => true, () => true);
        var output = new Output();
        Assert.IsTrue(first.Commands[0].Execute(["migrate", "sata1", "--yes"], output).IsOK());
        Assert.IsTrue(service.Started.Wait(2000));
        Assert.IsFalse(first.Commands[0].Execute(["migrate", "sata2", "--yes"], output).IsOK());
        Assert.IsFalse(other.Commands[0].Execute(["cancel"], output).IsOK());
        Assert.IsTrue(first.Interrupt(output));
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.IsTrue(SpinWait.SpinUntil(() => first.TakeResult()?.Contains("MigrationCancelled", StringComparison.Ordinal) == true, 2000));
        Assert.IsNull(other.TakeResult());
    }

    [TestMethod]
    public void ChangedIdentityReceivesNeitherOldLogNorResult()
    {
        int generation = 0;
        var service = new FakeMigration { WaitForCancellation = true };
        var tasks = new BackgroundTaskService();
        var commands = new MigrationShellCommands(service, tasks, () => true, () => true, () => generation);
        Assert.IsTrue(commands.Commands[0].Execute(["migrate", "sata1", "--yes"], new Output()).IsOK());
        Assert.IsTrue(service.Started.Wait(2000));
        generation++;
        Assert.IsNull(commands.TakeResult());
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.IsNull(commands.TakeResult());
    }

    private sealed class FakeMigration : IMigrationService
    {
        public int Calls;
        public string? Disk;
        public bool WaitForCancellation;
        public ManualResetEventSlim Started { get; } = new();
        public ResultObject<MigrationReport> Run(string diskName, Action<string> log, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            Disk = diskName;
            log("Inventar");
            Started.Set();
            if (WaitForCancellation)
            {
                cancellation.WaitHandle.WaitOne(2000);
                log("Spaetes Protokoll");
                return ResultObjectFactory.CreateFailure<MigrationReport>(ResultCode.Failure, "cause='MigrationCancelled'");
            }
            return ResultObjectFactory.CreateSuccess(new MigrationReport(0, "hash"));
        }
    }

    private sealed class Output : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }
}
