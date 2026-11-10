using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.Shell;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DiagnosticCommandTests
{
    [TestMethod]
    public void ExecuteLine_Diagnostics_ReadsProvidedVersionClockAndBoundedLog()
    {
        long elapsed = 123;
        var log = new SystemLog(1, () => elapsed, null);
        log.Information("Boot", "verworfen");
        log.Error("Display", "Grafikfehler");
        var shell = new ShellService(log, () => elapsed, "ZehOS Testversion; Cosmos Testversion");
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        output.Setup(target => target.WriteLine(It.IsAny<string>()));

        Assert.IsTrue(shell.ExecuteLine("version", output.Object).IsOK(), "Version muss laufen.");
        Assert.IsTrue(shell.ExecuteLine("uptime", output.Object).IsOK(), "Uptime muss laufen.");
        elapsed = 456;
        Assert.IsTrue(shell.ExecuteLine("uptime", output.Object).IsOK(), "Die Uhr muss erneut gelesen werden.");
        Assert.IsTrue(shell.ExecuteLine("log", output.Object).IsOK(), "Das Log muss lesbar sein.");
        Assert.IsTrue(shell.ExecuteLine("echo weiter", output.Object).IsOK(), "Diagnosen muessen die Shell bedienbar lassen.");

        output.Verify(target => target.WriteLine("ZehOS Testversion; Cosmos Testversion"), Times.Once);
        output.Verify(target => target.WriteLine("Laufzeit seit ZehOS-Start: 123 ms"), Times.Once);
        output.Verify(target => target.WriteLine("Laufzeit seit ZehOS-Start: 456 ms"), Times.Once);
        output.Verify(target => target.WriteLine("Systemprotokoll: 1/1 Eintraege (aelteste zuerst)."), Times.Once);
        output.Verify(target => target.WriteLine("[123 ms] Error Display: Grafikfehler"), Times.Once);
        output.Verify(target => target.WriteLine("weiter"), Times.Once);
        output.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow("version", "Versionsinformationen sind nicht verfuegbar.")]
    [DataRow("uptime", "Laufzeit ist nicht verfuegbar: keine initialisierte monotone Uhr.")]
    [DataRow("log", "Systemprotokoll ist nicht verfuegbar.")]
    public void ExecuteLine_UnavailableDiagnostics_DoesNotInventValues(string command, string expected)
    {
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        output.Setup(target => target.WriteLine(expected));
        var shell = new ShellService();
        Assert.IsTrue(shell.ExecuteLine(command, output.Object).IsOK(), "Fehlende Daten muessen erklaert werden.");
        output.Verify(target => target.WriteLine(expected), Times.Once);
        output.VerifyNoOtherCalls();
    }
}
