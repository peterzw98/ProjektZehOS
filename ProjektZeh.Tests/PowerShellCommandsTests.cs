using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PowerShellCommandsTests
{
    [DataTestMethod]
    [DataRow("reboot", "reboot")]
    [DataRow("ReBoOt", "reboot")]
    [DataRow("shutdown", "shutdown")]
    [DataRow("SHUTDOWN", "shutdown")]
    public void ExecuteLine_PowerCommand_PreparesBeforeRequestedAction(string input, string expectedAction)
    {
        var calls = new List<string>();
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        output.Setup(target => target.WriteLine(It.IsAny<string>())).Callback(() => calls.Add("output"));
        var commands = new PowerShellCommands(() =>
        {
            calls.Add("prepare");
            return ResultErr.CreateSuccess();
        }, () => calls.Add("reboot"), () => calls.Add("shutdown"));
        var shell = new ShellService(commands.Commands);

        var result = shell.ExecuteLine(input, output.Object);

        CollectionAssert.AreEqual(new[] { "prepare", "output", expectedAction }, calls,
            "Nur die angeforderte Energieaktion darf nach der Vorbereitung ausgefuehrt werden.");
        Assert.AreEqual(ResultCode.Failure, result.Code,
            "Eine zurueckkehrende Energieaktion muss als fehlgeschlagen gemeldet werden.");
    }

    [DataTestMethod]
    [DataRow("reboot")]
    [DataRow("shutdown")]
    public void ExecuteLine_PreparationFails_DoesNotInvokePowerAction(string input)
    {
        var expected = ResultErr.CreateFailure(ResultCode.Failure, "UnmountRefused");
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        int powerCalls = 0;
        var shell = new ShellService(new PowerShellCommands(() => expected,
            () => powerCalls++, () => powerCalls++).Commands);

        var result = shell.ExecuteLine(input, output.Object);

        Assert.AreSame(expected, result, "Ein Fehler beim Aushaengen muss zur Shell zurueckkommen.");
        Assert.AreEqual(0, powerCalls, "Bei fehlgeschlagener Vorbereitung darf keine Energieaktion erfolgen.");
        output.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow("reboot now")]
    [DataRow("shutdown extra")]
    public void ExecuteLine_ExtraArguments_DoesNotPrepareOrInvokePowerAction(string input)
    {
        int calls = 0;
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        var shell = new ShellService(new PowerShellCommands(() =>
        {
            calls++;
            return ResultErr.CreateSuccess();
        }, () => calls++, () => calls++).Commands);

        var result = shell.ExecuteLine(input, output.Object);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code, "Energieaktionen akzeptieren keine Argumente.");
        Assert.AreEqual(0, calls, "Ungueltige Befehle duerfen keine Nebenwirkungen haben.");
        output.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ExecuteLine_Exception_ReturnsFailure(bool failPreparation)
    {
        int powerCalls = 0;
        var output = new Mock<ITextOutput>();
        var shell = new ShellService(new PowerShellCommands(() =>
        {
            if (failPreparation)
            {
                throw new InvalidOperationException("Preparation failed");
            }
            return ResultErr.CreateSuccess();
        }, () =>
        {
            powerCalls++;
            throw new InvalidOperationException("Power failed");
        }, () => Assert.Fail("Shutdown wurde nicht angefordert.")).Commands);

        var result = shell.ExecuteLine("reboot", output.Object);

        Assert.AreEqual(ResultCode.Exception, result.Code, "Eine Exception muss als Shellfehler zurueckkommen.");
        Assert.AreEqual(failPreparation ? 0 : 1, powerCalls, "Fehlgeschlagene Vorbereitung muss den Neustart verhindern.");
    }

    [TestMethod]
    public void ExecuteLine_Help_ListsPowerCommands()
    {
        var output = new Mock<ITextOutput>();
        var shell = new ShellService(new PowerShellCommands(ResultErr.CreateSuccess,
            () => Assert.Fail("Help darf keinen Neustart ausloesen."),
            () => Assert.Fail("Help darf nicht ausschalten.")).Commands);

        Assert.IsTrue(shell.ExecuteLine("help", output.Object).IsOK(), "Help muss verfuegbar bleiben.");

        output.Verify(target => target.WriteLine(It.Is<string>(line => line.StartsWith("reboot -", StringComparison.Ordinal))), Times.Once);
        output.Verify(target => target.WriteLine(It.Is<string>(line => line.StartsWith("shutdown -", StringComparison.Ordinal))), Times.Once);
    }
}
