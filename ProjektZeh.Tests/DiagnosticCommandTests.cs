using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Tests;

[DoNotParallelize]
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

    [TestMethod]
    public void ExecuteLine_Frametimes_SwitchesSharedProfileAndRejectsUnknownArguments()
    {
        var shell = new ShellService((SystemLog?)null, null, null);
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        output.Setup(target => target.WriteLine(It.IsAny<string>()));
        try
        {
            Assert.IsTrue(shell.ExecuteLine("frametimes on", output.Object).IsOK());
            Assert.IsTrue(Runtime.Wasm.WasmFrameProfile.Shared.Enabled);
            Assert.AreEqual(ResultCode.InvalidArgument, shell.ExecuteLine("frametimes laut", output.Object).Code);
            Assert.IsTrue(Runtime.Wasm.WasmFrameProfile.Shared.Enabled, "Ein falsches Argument darf nichts umschalten.");
            Assert.IsTrue(shell.ExecuteLine("frametimes off", output.Object).IsOK());
            Assert.IsFalse(Runtime.Wasm.WasmFrameProfile.Shared.Enabled);
        }
        finally { Runtime.Wasm.WasmFrameProfile.Shared.SetEnabled(false); }
        output.Verify(target => target.WriteLine(It.Is<string>(text => text.StartsWith("Bildzeitmessung an. Anteile", System.StringComparison.Ordinal))), Times.Once);
        output.Verify(target => target.WriteLine(It.Is<string>(text => text.StartsWith("Bildzeitmessung aus. Anteile", System.StringComparison.Ordinal))), Times.Once);
    }

    [TestMethod]
    public void ExecuteLine_WasmAot_IsOffByDefaultAndSwitchesForTheNextStart()
    {
        var shell = new ShellService((SystemLog?)null, null, null);
        var output = new Mock<ITextOutput>(MockBehavior.Strict);
        output.Setup(target => target.WriteLine(It.IsAny<string>()));
        try
        {
            // Peters Entscheidung: AOT bleibt standardmaessig aus, bis Etappe F abgeschlossen ist.
            Assert.IsFalse(Runtime.Wasm.WasmEngineSelection.AotEnabled);
            Assert.IsTrue(shell.ExecuteLine("wasmaot", output.Object).IsOK());
            Assert.IsTrue(shell.ExecuteLine("wasmaot on", output.Object).IsOK());
            Assert.IsTrue(Runtime.Wasm.WasmEngineSelection.AotEnabled);
            Assert.AreEqual(ResultCode.InvalidArgument, shell.ExecuteLine("wasmaot schnell", output.Object).Code);
            Assert.IsTrue(Runtime.Wasm.WasmEngineSelection.AotEnabled, "Ein falsches Argument darf nichts umschalten.");
            Assert.IsTrue(shell.ExecuteLine("wasmaot off", output.Object).IsOK());
            Assert.IsFalse(Runtime.Wasm.WasmEngineSelection.AotEnabled);
        }
        finally { Runtime.Wasm.WasmEngineSelection.AotEnabled = false; }
        output.Verify(target => target.WriteLine("WAMR-AOT aus (Kennung wamr245-aot5-x64-bc-nrz)."), Times.Exactly(2));
        output.Verify(target => target.WriteLine("WAMR-AOT an (Kennung wamr245-aot5-x64-bc-nrz)."), Times.Once);
        // Der Hostbuild enthaelt kein WAMR; die Shell sagt das bei jeder Abfrage.
        output.Verify(target => target.WriteLine("WAMR-AOT ist in diesem Kernel nicht verfuegbar; Apps laufen unter wasm3."), Times.Exactly(3));
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
