using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.Shell;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class ShellServiceTests
{
    private ShellService shell = null!;
    private Mock<ITextOutput> output = null!;

    [TestInitialize]
    public void Initialize()
    {
        shell = new ShellService();
        output = new Mock<ITextOutput>(MockBehavior.Strict);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t ")]
    public void ExecuteLine_EmptyInput_DoesNotWriteAnything(string? input)
    {
        var result = shell.ExecuteLine(input, output.Object);

        Assert.IsTrue(result.IsOK(), "Leere Eingabe muss erfolgreich ignoriert werden.");
        output.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow("echo Hallo ZehOS", "Hallo ZehOS")]
    [DataRow(" ECHO \"Hallo  ZehOS\" ", "Hallo  ZehOS")]
    [DataRow("echo", "")]
    [DataRow("echo \"\" Ende", " Ende")]
    public void ExecuteLine_Echo_WritesArguments(string input, string expected)
    {
        output.Setup(target => target.WriteLine(expected));

        var result = shell.ExecuteLine(input, output.Object);

        Assert.IsTrue(result.IsOK(), "Echo muss Argumente ausgeben koennen.");
        output.Verify(target => target.WriteLine(expected), Times.Once, "Echo muss genau eine Zeile schreiben.");
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_Help_ListsAllBuiltInCommands()
    {
        output.Setup(target => target.WriteLine(It.IsAny<string>()));

        var result = shell.ExecuteLine("help", output.Object);

        Assert.IsTrue(result.IsOK(), "Die Hilfe muss aufrufbar sein.");
        foreach (string command in new[] { "help", "echo", "clear", "about" })
        {
            output.Verify(target => target.WriteLine(It.Is<string>(text => text.StartsWith(command))),
                Times.Once, $"Die Hilfe muss {command} genau einmal beschreiben.");
        }
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_Clear_ClearsTheOutput()
    {
        output.Setup(target => target.Clear());

        var result = shell.ExecuteLine("clear", output.Object);

        Assert.IsTrue(result.IsOK(), "Clear muss erfolgreich sein.");
        output.Verify(target => target.Clear(), Times.Once, "Clear muss die Ausgabe genau einmal leeren.");
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_About_DescribesZehOS()
    {
        output.Setup(target => target.WriteLine(It.IsAny<string>()));

        var result = shell.ExecuteLine("about", output.Object);

        Assert.IsTrue(result.IsOK(), "About muss aufrufbar sein.");
        output.Verify(target => target.WriteLine(It.Is<string>(text => text.Contains("ZehOS"))),
            Times.Once, "About muss das Betriebssystem benennen.");
        output.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow("help extra")]
    [DataRow("clear extra")]
    [DataRow("about extra")]
    public void ExecuteLine_UnexpectedArguments_ReportsUsageWithoutSideEffects(string input)
    {
        var result = shell.ExecuteLine(input, output.Object);

        Assert.IsFalse(result.IsOK(), "Unerwartete Argumente duerfen nicht stillschweigend ignoriert werden.");
        Assert.AreEqual(ResultCode.InvalidArgument, result.Code, "Die Fehlerart muss falsche Argumente benennen.");
        StringAssert.Contains(result.Message, "Verwendung:", "Die Meldung muss die richtige Verwendung zeigen.");
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_UnknownCommand_ReturnsAnErrorAndAcceptsTheNextCommand()
    {
        var result = shell.ExecuteLine("does-not-exist", output.Object);

        Assert.AreEqual(ResultCode.NotFound, result.Code, "Unbekannte Befehle muessen erkennbar sein.");
        Assert.IsFalse(result.IsOK(), "Ein unbekannter Befehl darf keinen Erfolg melden.");
        StringAssert.Contains(result.Message, "does-not-exist", "Die Meldung muss den unbekannten Namen nennen.");
        output.VerifyNoOtherCalls();
        output.Setup(target => target.WriteLine("weiter"));

        var nextResult = shell.ExecuteLine("echo weiter", output.Object);

        Assert.IsTrue(nextResult.IsOK(), "Ein Eingabefehler darf die Shell nicht beenden.");
        output.Verify(target => target.WriteLine("weiter"), Times.Once, "Der naechste Befehl muss laufen.");
    }

    [TestMethod]
    public void ExecuteLine_UnclosedQuotation_DoesNotExecuteAndAcceptsTheNextCommand()
    {
        var result = shell.ExecuteLine("clear \"", output.Object);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code, "Der Parserfehler muss erhalten bleiben.");
        output.VerifyNoOtherCalls();
        output.Setup(target => target.WriteLine("weiter"));

        var nextResult = shell.ExecuteLine("echo weiter", output.Object);

        Assert.IsTrue(nextResult.IsOK(), "Der naechste Befehl muss unabhaengig vom Parserfehler laufen.");
        output.Verify(target => target.WriteLine("weiter"), Times.Once, "Die Shell muss weiterarbeiten.");
    }

    [TestMethod]
    public void ExecuteLine_MissingOutput_ReturnsAnInputError()
    {
        var result = shell.ExecuteLine("echo test", null);

        Assert.AreEqual(ResultCode.MissingArgument, result.Code, "Eine fehlende Ausgabe muss als Fehler zurueckkommen.");
        Assert.IsFalse(result.IsOK(), "Ohne Ausgabe darf die Shell keinen Erfolg melden.");
    }
}
