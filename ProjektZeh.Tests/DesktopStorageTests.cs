using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopStorageTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, true);

    [DataTestMethod]
    [DataRow("cat fehlt.txt")]
    [DataRow("cd fehlt")]
    [DataRow("cat \"\"")]
    [DataRow("write fehlt/datei.txt Inhalt")]
    public void Desktop_FileError_ShowsDiagnosticAndAcceptsNextCommand(string command)
    {
        var desktop = CreateDesktop(directory);
        var terminal = (TerminalApp)desktop.Windows.ActiveWindow!.Application;
        int before = terminal.Buffer.Count;

        Execute(desktop, command);

        Assert.IsTrue(terminal.Buffer.Count > before + 1, "Der Fehler muss nach dem Befehl sichtbar sein.");
        Assert.AreEqual("", terminal.Editor.Text, "Die fehlgeschlagene Zeile muss abgeschlossen sein.");
        Execute(desktop, "echo weiter");
        Assert.AreEqual("weiter", terminal.Buffer.GetLine(terminal.Buffer.Count - 1));
        Execute(desktop, "write weiter.txt Inhalt");
        Assert.AreEqual("Inhalt", File.ReadAllText(Path.Combine(directory, "weiter.txt")));
        Assert.AreEqual(2, desktop.Windows.Count, "Beide Terminalfenster muessen erhalten bleiben.");
    }

    [TestMethod]
    public void Desktop_MissingStorage_BootsAndOtherTerminalKeepsDraft()
    {
        var desktop = CreateDesktop(Path.Combine(directory, "nicht-eingebunden"));
        var firstWindow = desktop.Windows.ActiveWindow!;
        var first = (TerminalApp)firstWindow.Application;
        Type(desktop, "echo Entwurf");
        Assert.IsTrue(desktop.OpenTerminal());
        var second = (TerminalApp)desktop.Windows.ActiveWindow!.Application;

        Execute(desktop, "ls");
        Execute(desktop, "echo ohne Laufwerk");

        Assert.AreEqual("ohne Laufwerk", second.Buffer.GetLine(second.Buffer.Count - 1));
        Assert.AreEqual("echo Entwurf", first.Editor.Text);
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "nicht-eingebunden")),
            "Der Desktop darf einen fehlenden Datenpfad nicht automatisch einrichten.");
        desktop.Handle(new(InputEventType.PointerDown, X: firstWindow.X + 10,
            Y: firstWindow.Y + 10, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.Reset));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual("Entwurf", first.Buffer.GetLine(first.Buffer.Count - 1));
    }

    [TestMethod]
    public void Desktop_ReplaceFailure_PreservesFileCleansTemporaryAndAllowsRetry()
    {
        string target = Path.Combine(directory, "original.txt");
        File.WriteAllText(target, "vorher");
        bool fail = true;
        var desktop = CreateDesktop(directory, (source, destination, overwrite) =>
        {
            // Ein Fehler nach der Sicherung prueft die Rueckkehr durch den gesamten Desktoppfad.
            if (fail && destination == target && source.EndsWith(".tmp", StringComparison.Ordinal))
                throw new IOException("Test: Datentraeger voll beim Ersetzen");
            File.Move(source, destination, overwrite);
        });
        var terminal = (TerminalApp)desktop.Windows.ActiveWindow!.Application;

        Execute(desktop, "write original.txt nachher");

        StringAssert.Contains(Output(terminal), "Test: Datentraeger voll beim Ersetzen");
        Assert.AreEqual("vorher", File.ReadAllText(target));
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFileSystemEntries(directory),
            "Sicherung und temporaere Datei muessen nach erfolgreicher Wiederherstellung verschwinden.");
        Execute(desktop, "cat original.txt");
        Assert.AreEqual("vorher", terminal.Buffer.GetLine(terminal.Buffer.Count - 1));
        fail = false;
        Execute(desktop, "write original.txt nachher");
        Execute(desktop, "cat original.txt");
        Assert.AreEqual("nachher", terminal.Buffer.GetLine(terminal.Buffer.Count - 1));
        Assert.AreEqual("nachher", File.ReadAllText(target));
    }

    private static Desktop CreateDesktop(string path, Action<string, string, bool>? moveFile = null)
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16,
            () => new ShellService(new FileShellCommands(new FileService(path, moveFile)).Commands));
        desktop.OpenTerminal();
        desktop.OpenTerminal();
        return desktop;
    }

    private static void Execute(Desktop desktop, string command)
    {
        Type(desktop, command);
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
    }

    private static void Type(Desktop desktop, string text)
    {
        foreach (char character in text) desktop.Handle(new(InputEventType.Text, Character: character));
    }

    private static string Output(TerminalApp terminal) =>
        string.Join("", Enumerable.Range(0, terminal.Buffer.Count).Select(terminal.Buffer.GetLine));
}
