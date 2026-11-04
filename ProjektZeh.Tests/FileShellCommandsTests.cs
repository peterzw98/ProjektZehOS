using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FileShellCommandsTests
{
    private string directory = null!;
    private ShellService shell = null!;
    private Mock<ITextOutput> output = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        shell = new ShellService(new FileShellCommands(new FileService(directory)).Commands);
        output = new Mock<ITextOutput>(MockBehavior.Strict);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public void ExecuteLine_QuotedFileName_PreservesTextAndEmptyContent()
    {
        var written = shell.ExecuteLine("write \"meine datei.txt\" \"Hallo  ZehOS\"", output.Object);
        output.Setup(target => target.WriteLine("Hallo  ZehOS"));
        var read = shell.ExecuteLine("cat \"meine datei.txt\"", output.Object);
        var emptied = shell.ExecuteLine("write \"meine datei.txt\" \"\"", output.Object);

        Assert.IsTrue(written.IsOK(), written.ToString());
        Assert.IsTrue(read.IsOK(), read.ToString());
        Assert.IsTrue(emptied.IsOK(), emptied.ToString());
        Assert.AreEqual(string.Empty, File.ReadAllText(Path.Combine(directory, "meine datei.txt")),
            "Ein ausdruecklich leerer Inhalt muss die Datei leeren.");
        output.Verify(target => target.WriteLine("Hallo  ZehOS"), Times.Once, "Cat muss den Inhalt genau einmal ausgeben.");
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_DirectoryCommands_UseSameWorkingDirectory()
    {
        Assert.IsTrue(shell.ExecuteLine("mkdir texte", output.Object).IsOK(), "Mkdir muss einen Ordner erstellen.");
        Assert.IsTrue(shell.ExecuteLine("cd texte", output.Object).IsOK(), "Cd muss den neuen Ordner erreichen.");
        string expectedDirectory = Path.Combine(directory, "texte");
        output.Setup(target => target.WriteLine(expectedDirectory));
        Assert.IsTrue(shell.ExecuteLine("pwd", output.Object).IsOK(), "Pwd muss verfuegbar sein.");
        Assert.IsTrue(shell.ExecuteLine("write brief.txt mehrere Worte", output.Object).IsOK(), "Write muss Textargumente verbinden.");
        string expectedFile = Path.Combine(expectedDirectory, "brief.txt");
        output.Setup(target => target.WriteLine(expectedFile));

        var listed = shell.ExecuteLine("ls", output.Object);

        Assert.IsTrue(listed.IsOK(), listed.ToString());
        Assert.AreEqual("mehrere Worte", File.ReadAllText(expectedFile), "Write muss im Arbeitsverzeichnis speichern.");
        output.Verify(target => target.WriteLine(expectedDirectory), Times.Once, "Pwd muss den gewechselten Pfad ausgeben.");
        output.Verify(target => target.WriteLine(expectedFile), Times.Once, "Ls muss den relativen Arbeitsordner verwenden.");
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_IoFailure_ReportsOnceThroughResultAndAllowsNextCommand()
    {
        var failed = shell.ExecuteLine("cat fehlt.txt", output.Object);

        Assert.IsFalse(failed.IsOK(), "Ein Dateifehler muss beim Kernel ankommen.");
        Assert.IsNotNull(failed.ExceptionDetails, "Die urspruengliche Diagnose darf nicht verloren gehen.");
        output.VerifyNoOtherCalls();
        Assert.IsTrue(shell.ExecuteLine("write weiter.txt weiter", output.Object).IsOK(),
            "Nach dem Fehler muss die Shell weiterarbeiten.");
    }

    [DataTestMethod]
    [DataRow("cat")]
    [DataRow("cat a b")]
    [DataRow("write datei.txt")]
    [DataRow("mkdir")]
    [DataRow("cd")]
    [DataRow("cd a b")]
    [DataRow("ls a b")]
    [DataRow("pwd extra")]
    public void ExecuteLine_InvalidArguments_DoesNotChangeFileSystem(string input)
    {
        var result = shell.ExecuteLine(input, output.Object);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code, "Falsche Argumente muessen erkennbar sein.");
        StringAssert.Contains(result.Message, "Verwendung:", "Die richtige Verwendung muss genannt werden.");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(directory).Length, "Fehlerhafte Befehle duerfen nichts anlegen.");
        output.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ExecuteLine_Help_IncludesFileCommands()
    {
        output.Setup(target => target.WriteLine(It.IsAny<string>()));

        var result = shell.ExecuteLine("help", output.Object);

        Assert.IsTrue(result.IsOK(), result.ToString());
        foreach (string command in new[] { "pwd", "cd", "ls", "cat", "mkdir", "write" })
        {
            output.Verify(target => target.WriteLine(It.Is<string>(text => text.StartsWith(command, StringComparison.Ordinal))),
                Times.Once, $"Die Hilfe muss {command} erklaeren.");
        }
    }
}
