using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class StorageShellCommandsTests
{
    [DataTestMethod]
    [DataRow("disk")]
    [DataRow("disk init")]
    [DataRow("disk init sata0 extra")]
    [DataRow("disk mount")]
    [DataRow("disk unmount sata0")]
    [DataRow("disk format sata0")]
    public void ExecuteLine_InvalidArguments_DoesNotTouchStorage(string input)
    {
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        var result = shell.ExecuteLine(input, output.Object);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code, "Ein unvollstaendiger Disk-Befehl darf nicht ausgefuehrt werden.");
        storage.VerifyNoOtherCalls();
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_Initialize_IsExplicitAndPassesFailureToCaller()
    {
        var expected = ResultErr.CreateFailure(ResultCode.Failure, "DiskNotEmpty");
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        storage.Setup(target => target.InitializeTestDisk("sata0", false, false)).Returns(expected);
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        var result = shell.ExecuteLine("disk init sata0", output.Object);

        Assert.AreSame(expected, result, "Der konkrete Speicherfehler muss unveraendert zurueckkommen.");
        storage.Verify(target => target.InitializeTestDisk("sata0", false, false), Times.Once, "Nur der ausdrueckliche Aufruf darf initialisieren.");
        storage.VerifyNoOtherCalls();
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_InitWithYesLarge_PassesConfirmation()
    {
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        storage.Setup(target => target.InitializeTestDisk("sata1", true, false)).Returns(ResultErr.CreateSuccess());
        var output = new Mock<ITextOutput>();
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        Assert.IsTrue(shell.ExecuteLine("disk init sata1 --yes-large", output.Object).IsOK());
        Assert.IsFalse(shell.ExecuteLine("disk init sata1 --yes", output.Object).IsOK(), "Nur die ausgeschriebene Bestaetigung zaehlt.");

        storage.Verify(target => target.InitializeTestDisk("sata1", true, false), Times.Once);
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_InitFlags_AnyOrderEachOnce()
    {
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        storage.Setup(target => target.InitializeTestDisk("sata1", true, true)).Returns(ResultErr.CreateSuccess());
        storage.Setup(target => target.InitializeTestDisk("sata1", false, true)).Returns(ResultErr.CreateSuccess());
        var output = new Mock<ITextOutput>();
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        Assert.IsTrue(shell.ExecuteLine("disk init sata1 --yes-large --yes-overwrite", output.Object).IsOK());
        Assert.IsTrue(shell.ExecuteLine("disk init sata1 --yes-overwrite --yes-large", output.Object).IsOK());
        Assert.IsTrue(shell.ExecuteLine("disk init sata1 --yes-overwrite", output.Object).IsOK());
        Assert.IsFalse(shell.ExecuteLine("disk init sata1 --yes-overwrite --yes-overwrite", output.Object).IsOK(), "doppeltes Flag");
        Assert.IsFalse(shell.ExecuteLine("disk init sata1 --overwrite", output.Object).IsOK(), "unbekanntes Flag");

        storage.Verify(target => target.InitializeTestDisk("sata1", true, true), Times.Exactly(2));
        storage.Verify(target => target.InitializeTestDisk("sata1", false, true), Times.Once);
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_Use_NeedsExplicitYes()
    {
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        storage.Setup(target => target.UseDataDisk("sata1")).Returns(ResultErr.CreateSuccess());
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        Assert.IsFalse(shell.ExecuteLine("disk use sata1", output.Object).IsOK(), "ohne Bestaetigung nichts");
        Assert.IsFalse(shell.ExecuteLine("disk use sata1 --ja", output.Object).IsOK());
        Assert.IsTrue(shell.ExecuteLine("disk use sata1 --yes", output.Object).IsOK());

        storage.Verify(target => target.UseDataDisk("sata1"), Times.Once);
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_InitYesLarge_SuccessNamesTheResidualRisk_OnlyThen()
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(target => target.InitializeTestDisk("sata1", true, false)).Returns(ResultErr.CreateSuccess());
        storage.Setup(target => target.InitializeTestDisk("sata2", false, false)).Returns(ResultErr.CreateSuccess());
        storage.Setup(target => target.InitializeTestDisk("sata3", true, false)).Returns(ResultErr.CreateFailure(ResultCode.Failure, "x"));
        var lines = new System.Collections.Generic.List<string>();
        var output = new Mock<ITextOutput>();
        output.Setup(target => target.WriteLine(It.IsAny<string>())).Callback<string>(lines.Add);
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        shell.ExecuteLine("disk init sata2", output.Object);
        shell.ExecuteLine("disk init sata3 --yes-large", output.Object);
        Assert.AreEqual(0, lines.Count, "ohne --yes-large oder bei Fehler kein Hinweis");
        shell.ExecuteLine("disk init sata1 --yes-large", output.Object);

        Assert.AreEqual(1, lines.Count);
        StringAssert.Contains(lines[0], "ZWISCHEN den Stichproben");
    }

    [TestMethod]
    public void ExecuteLine_Mount_OnlySata0WithoutConfirmation()
    {
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        storage.Setup(target => target.Mount("sata0")).Returns(ResultErr.CreateSuccess());
        storage.Setup(target => target.Mount("sata1")).Returns(ResultErr.CreateSuccess());
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        Assert.IsTrue(shell.ExecuteLine("disk mount sata0", output.Object).IsOK());
        Assert.IsFalse(shell.ExecuteLine("disk mount sata1", output.Object).IsOK(), "fremde Platte ohne Bestaetigung");
        Assert.IsFalse(shell.ExecuteLine("disk mount sata1 --ja", output.Object).IsOK());
        Assert.IsTrue(shell.ExecuteLine("disk mount sata1 --yes", output.Object).IsOK());

        storage.Verify(target => target.Mount("sata0"), Times.Once);
        storage.Verify(target => target.Mount("sata1"), Times.Once);
        storage.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_MountAndUnmount_DoNotInitializeDisk()
    {
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        storage.Setup(target => target.Mount("sata0")).Returns(ResultErr.CreateSuccess());
        storage.Setup(target => target.Unmount()).Returns(ResultErr.CreateSuccess());
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        var shell = new ShellService(new StorageShellCommands(storage.Object).Commands);

        Assert.IsTrue(shell.ExecuteLine("disk mount sata0", output.Object).IsOK(), "Mount muss erreichbar sein.");
        Assert.IsTrue(shell.ExecuteLine("disk unmount", output.Object).IsOK(), "Unmount muss erreichbar sein.");

        storage.Verify(target => target.Mount("sata0"), Times.Once, "Das benannte Laufwerk muss eingebunden werden.");
        storage.Verify(target => target.Unmount(), Times.Once, "Das Laufwerk muss ausgehaengt werden.");
        storage.VerifyNoOtherCalls();
        output.VerifyNoOtherCalls();
    }
}
