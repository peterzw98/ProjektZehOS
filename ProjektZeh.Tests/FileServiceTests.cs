using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FileServiceTests
{
    private string directory = null!;
    private FileService files = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        files = new FileService(directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public void WriteText_NewService_ReadsPreviouslySavedContent()
    {
        var written = files.WriteText("hallo.txt", "Hallo ZehOS! äöü");
        var reopened = new FileService(directory).ReadText("hallo.txt");

        Assert.IsTrue(written.IsOK(), written.ToString());
        Assert.IsTrue(reopened.IsOK(), reopened.ToString());
        Assert.AreEqual("Hallo ZehOS! äöü", reopened.Value, "Gespeicherter Inhalt muss unveraendert lesbar sein.");
    }

    [TestMethod]
    public void WriteText_ExistingFile_ReplacesContentWithoutTrailingBytes()
    {
        files.WriteText("test.txt", "Langer bisheriger Inhalt");

        var result = files.WriteText("test.txt", "kurz");

        Assert.IsTrue(result.IsOK(), result.ToString());
        Assert.AreEqual("kurz", File.ReadAllText(Path.Combine(directory, "test.txt")),
            "Write muss die Datei ersetzen und den Stream schliessen.");
    }

    [TestMethod]
    public void ChangeDirectory_ParentAndRelativePaths_ResolveFromShellDirectory()
    {
        Assert.IsTrue(files.CreateDirectory("texte").IsOK(), "Der Unterordner muss angelegt werden.");
        Assert.IsTrue(files.ChangeDirectory("texte").IsOK(), "Cd muss in den Unterordner wechseln.");
        Assert.IsTrue(files.WriteText("brief.txt", "Inhalt").IsOK(), "Relative Pfade muessen schreibbar sein.");
        Assert.IsTrue(files.ChangeDirectory("..").IsOK(), "Cd muss den Elternordner erreichen.");

        Assert.AreEqual(directory, files.CurrentDirectory, "Das Arbeitsverzeichnis muss wieder der Ausgangsordner sein.");
        Assert.AreEqual("Inhalt", files.ReadText(Path.Combine("texte", "brief.txt")).Value,
            "Relative Dateipfade muessen vom Arbeitsverzeichnis ausgehen.");
    }

    [TestMethod]
    public void ChangeDirectory_MissingDirectory_PreservesWorkingDirectory()
    {
        var result = files.ChangeDirectory("fehlt");

        Assert.IsFalse(result.IsOK(), "Ein nicht vorhandenes Ziel muss einen Fehler liefern.");
        Assert.AreEqual(directory, files.CurrentDirectory, "Ein fehlgeschlagener Wechsel darf das Arbeitsverzeichnis nicht veraendern.");
        Assert.IsNotNull(result.ExceptionDetails, "Die tatsaechliche IO-Ursache muss erhalten bleiben.");
    }

    [TestMethod]
    public void ChangeDirectory_File_RejectsTarget()
    {
        files.WriteText("datei.txt", "Inhalt");

        var result = files.ChangeDirectory("datei.txt");

        Assert.IsFalse(result.IsOK(), "Cd darf keine Datei als Verzeichnis akzeptieren.");
        Assert.AreEqual(directory, files.CurrentDirectory, "Der vorherige Pfad muss erhalten bleiben.");
    }

    [TestMethod]
    public void ListEntries_FilesAndDirectories_ReturnsBothKinds()
    {
        files.CreateDirectory("ordner");
        files.WriteText("datei.txt", "Inhalt");

        var result = files.ListEntries(".");

        Assert.IsTrue(result.IsOK(), result.ToString());
        CollectionAssert.AreEquivalent(new[] { Path.Combine(directory, "ordner"), Path.Combine(directory, "datei.txt") },
            result.Value, "Ls muss Ordner und Dateien anzeigen.");
    }

    [TestMethod]
    public void ReadText_MissingFile_ReturnsActualFailureAndRemainsUsable()
    {
        var missing = files.ReadText("fehlt.txt");
        var next = files.WriteText("weiter.txt", "weiter");

        Assert.IsFalse(missing.IsOK(), "Eine fehlende Datei darf keinen Erfolg liefern.");
        Assert.IsNotNull(missing.Status.ExceptionDetails, "Die IO-Diagnose muss weitergegeben werden.");
        Assert.IsTrue(next.IsOK(), "Ein Lesefehler darf nachfolgende Operationen nicht blockieren.");
    }

    [TestMethod]
    public void ReadText_TooLargeFile_ReturnsFailureAndClosesStream()
    {
        string path = Path.Combine(directory, "gross.txt");
        File.WriteAllText(path, new string('x', FileService.MaximumTextLength + 1));

        var result = files.ReadText("gross.txt");

        Assert.IsFalse(result.IsOK(), "Cat darf keine beliebig grosse Datei in den Speicher lesen.");
        StringAssert.Contains(result.Status.Message, "TextTooLarge", "Die Groessenbegrenzung muss erkennbar sein.");
        File.Delete(path);
        Assert.IsFalse(File.Exists(path), "Auch im Fehlerfall muss der Dateihandle geschlossen werden.");
    }

    [TestMethod]
    public void WriteText_TooLargeContent_PreservesExistingFile()
    {
        files.WriteText("test.txt", "vorher");

        var result = files.WriteText("test.txt", new string('x', FileService.MaximumTextLength + 1));

        Assert.IsFalse(result.IsOK(), "Zu grosse Texte muessen vor dem Oeffnen abgelehnt werden.");
        Assert.AreEqual("vorher", files.ReadText("test.txt").Value, "Eine abgelehnte Eingabe darf die Datei nicht leeren.");
    }

    [TestMethod]
    public void WriteText_MaximumLength_RoundTripsWithoutTruncation()
    {
        string text = new('x', FileService.MaximumTextLength);

        var written = files.WriteText("grenze.txt", text);
        var read = files.ReadText("grenze.txt");

        Assert.IsTrue(written.IsOK(), written.ToString());
        Assert.IsTrue(read.IsOK(), read.ToString());
        Assert.AreEqual(text, read.Value, "Der Grenzwert muss erlaubt sein und vollstaendig gelesen werden.");
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void WriteText_MissingPath_ReturnsFailure(string? path)
    {
        var result = files.WriteText(path, "Inhalt");

        Assert.IsFalse(result.IsOK(), "Ein leerer Pfad darf keine Datei erzeugen.");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(directory).Length, "Ungueltige Eingaben duerfen nichts schreiben.");
    }
}
