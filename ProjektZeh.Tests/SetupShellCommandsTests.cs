using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Installer;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SetupShellCommandsTests
{
    private readonly Service installer = new();
    private readonly Output output = new();
    private ShellService shell = null!;

    [TestInitialize]
    public void Create() => shell = new ShellService(new SetupShellCommands(installer, () => true).Commands);

    [TestMethod]
    public void List_PrintsEveryDiskWithSizeAndContent()
    {
        installer.Disks = [new("sata0", 1000, 2, IsBlank: false), new("sata1", 2000, 0, IsBlank: true)];

        Assert.IsTrue(shell.ExecuteLine("setup list", output).IsOK());

        CollectionAssert.AreEqual(new[]
        {
            $"sata0: 1000 Bytes, {installer.Disks[0].ContentText}",
            $"sata1: 2000 Bytes, {installer.Disks[1].ContentText}"
        }, output.Lines);
    }

    [TestMethod]
    public void List_DiskQueryFails_ReturnsThatFailure()
    {
        installer.DisksFailure = ResultErr.CreateFailure(ResultCode.Failure, "Kein Controller");

        var result = shell.ExecuteLine("setup list", output);

        Assert.AreEqual("Kein Controller", result.Message);
        Assert.AreEqual(0, output.Lines.Count);
    }

    [DataTestMethod]
    [DataRow("setup install sata0", false)]
    [DataRow("setup install sata0 --erase", true)]
    public void Install_RunsJobToEndAndPassesEraseOnlyWhenConfirmed(string line, bool erase)
    {
        Assert.IsTrue(shell.ExecuteLine(line, output).IsOK());

        CollectionAssert.AreEqual(new[] { ("sata0", erase) }, installer.BeginCalls);
        Assert.AreEqual(3, installer.Job.Steps);
        // Jede Stufe erscheint einmal, auch wenn sie mehrere Schritte dauert.
        CollectionAssert.AreEqual(new[]
        {
            "Installation: Partitioning (0%)",
            "Installation: CopyingFiles (66%)",
            "ZehOS 0.2.0 installiert. Installationsmedium entfernen und neu starten."
        }, output.Lines);
    }

    [TestMethod]
    public void Install_StepFails_StopsAndDoesNotReportSuccess()
    {
        installer.Job = new Job(3) { FailAt = 2 };

        var result = shell.ExecuteLine("setup install sata0", output);

        Assert.AreEqual("Schreibfehler", result.Message);
        Assert.AreEqual(2, installer.Job.Steps, "Nach dem Fehler darf kein weiterer Schritt laufen.");
        Assert.IsFalse(output.Lines.Exists(text => text.Contains("installiert", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Install_BeginFails_ReturnsThatFailure()
    {
        installer.BeginFailure = ResultErr.CreateFailure(ResultCode.NotFound, "Laufwerk fehlt");

        var result = shell.ExecuteLine("setup install sata9", output);

        Assert.AreEqual(ResultCode.NotFound, result.Code);
        Assert.AreEqual(0, installer.Job.Steps);
    }

    [DataTestMethod]
    [DataRow("setup")]
    [DataRow("setup list all")]
    [DataRow("setup install")]
    [DataRow("setup install sata0 --force")]
    [DataRow("setup install sata0 --erase now")]
    public void InvalidArguments_ReturnUsageWithoutTouchingDisks(string line)
    {
        var result = shell.ExecuteLine(line, output);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code);
        StringAssert.StartsWith(result.Message, "Verwendung: setup", StringComparison.Ordinal);
        Assert.AreEqual(0, installer.BeginCalls.Count);
        Assert.AreEqual(0, installer.DiskQueries);
    }

    private sealed class Service : IInstallService
    {
        public InstallDisk[] Disks { get; set; } = [];
        public ResultErr? DisksFailure { get; set; }
        public ResultErr? BeginFailure { get; set; }
        public Job Job { get; set; } = new(3);
        public List<(string, bool)> BeginCalls { get; } = [];
        public int DiskQueries { get; private set; }
        public string ProductVersion => "0.2.0";

        public ResultObject<InstallDisk[]> GetDisks()
        {
            DiskQueries++;
            return DisksFailure == null ? ResultObjectFactory.CreateSuccess(Disks)
                : ResultObjectFactory.CreateFailure<InstallDisk[]>(DisksFailure.Code, DisksFailure.Message);
        }

        public ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting)
        {
            BeginCalls.Add((diskName, eraseExisting));
            return BeginFailure == null ? ResultObjectFactory.CreateSuccess<IInstallJob>(Job)
                : ResultObjectFactory.CreateFailure<IInstallJob>(BeginFailure.Code, BeginFailure.Message);
        }

        public ResultErr Restart() => throw new AssertFailedException("setup startet nicht neu.");
    }

    private sealed class Job(int total) : IInstallJob
    {
        public int Steps { get; private set; }
        public int FailAt { get; init; } = -1;
        // Ab dem zweiten Schritt die naechste Stufe, damit der Stufenwechsel in der Ausgabe pruefbar ist.
        public InstallStage Stage => Steps < 2 ? InstallStage.Partitioning : InstallStage.CopyingFiles;
        public int Percent => Steps * 100 / total;
        public bool IsCompleted => Steps >= total;

        public ResultErr Step()
        {
            Steps++;
            return Steps == FailAt ? ResultErr.CreateFailure(ResultCode.Failure, "Schreibfehler") : ResultErr.CreateSuccess();
        }
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
