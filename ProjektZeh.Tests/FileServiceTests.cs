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
    public void WriteText_CosmosBackupNameExists_ReplacesOnlyTarget()
    {
        File.WriteAllText(Path.Combine(directory, "test.txt"), "vorher");
        File.WriteAllText(Path.Combine(directory, "test.txt.~replace"), "Nachbardatei");
        files = new FileService(directory, MoveLikeCosmos);

        var result = files.WriteText("test.txt", "nachher");

        Assert.IsTrue(result.IsOK(), result.ToString());
        Assert.AreEqual("nachher", files.ReadText("test.txt").Value);
        Assert.AreEqual("Nachbardatei", files.ReadText("test.txt.~replace").Value);
        Assert.AreEqual(2, Directory.GetFileSystemEntries(directory).Length,
            "Ein erfolgreicher Austausch darf keine Hilfsdateien hinterlassen.");
    }

    [DataTestMethod]
    [DataRow(247)]
    [DataRow(255)]
    public void WriteText_LongFatName_ReplacesExistingFile(int nameLength)
    {
        string name = new('a', nameLength);
        File.WriteAllText(Path.Combine(directory, name), "vorher");
        files = new FileService(directory, MoveLikeCosmos);

        var result = files.WriteText(name, "nachher");

        Assert.IsTrue(result.IsOK(), result.ToString());
        Assert.AreEqual("nachher", files.ReadText(name).Value);
        Assert.AreEqual(1, Directory.GetFileSystemEntries(directory).Length);
    }

    [TestMethod]
    public void WriteText_FinalRenameFails_RestoresOriginalAndRemovesTemporaryFiles()
    {
        string target = Path.Combine(directory, "test.txt");
        File.WriteAllText(target, "vorher");
        bool originalMoved = false;
        files = new FileService(directory, (source, destination, overwrite) =>
        {
            if (source.EndsWith(".tmp", StringComparison.Ordinal))
            {
                throw new IOException("Austausch fehlgeschlagen");
            }

            MoveLikeCosmos(source, destination, overwrite);
            if (source == target)
            {
                originalMoved = true;
            }
        });

        var result = files.WriteText("test.txt", "nachher");

        Assert.IsFalse(result.IsOK());
        Assert.IsTrue(originalMoved, "Der Fehler muss nach dem Sichern der Originaldatei auftreten.");
        Assert.AreEqual("vorher", File.ReadAllText(target));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(directory).Length);
    }

    [TestMethod]
    public void WriteText_RestoreFails_KeepsOriginalBackupAndReportsItsPath()
    {
        string target = Path.Combine(directory, "test.txt");
        File.WriteAllText(target, "vorher");
        string? backupPath = null;
        files = new FileService(directory, (source, destination, overwrite) =>
        {
            if (source == target)
            {
                MoveLikeCosmos(source, destination, overwrite);
                backupPath = destination;
                return;
            }

            throw new IOException("Umbenennung fehlgeschlagen");
        });

        var result = files.WriteText("test.txt", "nachher");

        Assert.IsFalse(result.IsOK());
        Assert.IsNotNull(backupPath, "Vor dem Austausch muss die Originaldatei gesichert werden.");
        Assert.AreEqual("vorher", File.ReadAllText(backupPath));
        StringAssert.Contains(result.Message, backupPath,
            "Der Fehler muss den Pfad zur erhaltenen Originaldatei nennen.");
        Assert.AreEqual(1, Directory.GetFileSystemEntries(directory).Length,
            "Nur die Sicherung darf zur manuellen Wiederherstellung bleiben.");
    }

    private static void MoveLikeCosmos(string source, string destination, bool overwrite)
    {
        if (overwrite && File.Exists(destination))
        {
            string backupPath = destination + ".~replace";
            if (Path.GetFileName(backupPath).Length > 255 || File.Exists(backupPath))
            {
                throw new IOException("Cosmos-Sicherungsname nicht verfuegbar");
            }
        }

        File.Move(source, destination, overwrite);
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
    public void WriteText_EncodingFailure_PreservesExistingFileAndRemovesTemporaryFile()
    {
        files.WriteText("test.txt", "vorher");

        var result = files.WriteText("test.txt", "\uD800");

        Assert.IsFalse(result.IsOK(), "Ein einzelnes UTF-16-Surrogat muss einen Schreibfehler liefern.");
        Assert.AreEqual("vorher", files.ReadText("test.txt").Value,
            "Ein Schreibfehler darf den bisherigen Dateiinhalt nicht veraendern.");
        Assert.AreEqual(1, Directory.GetFileSystemEntries(directory).Length,
            "Eine fehlgeschlagene temporaere Datei muss entfernt werden.");
    }

    [TestMethod]
    public void WriteText_TargetIsDirectory_PreservesDirectoryAndRemovesTemporaryFile()
    {
        files.CreateDirectory("ordner");

        var result = files.WriteText("ordner", "Inhalt");

        Assert.IsFalse(result.IsOK(), "Ein Verzeichnis darf nicht durch eine Datei ersetzt werden.");
        Assert.IsTrue(Directory.Exists(Path.Combine(directory, "ordner")), "Das Zielverzeichnis muss erhalten bleiben.");
        Assert.AreEqual(1, Directory.GetFileSystemEntries(directory).Length,
            "Bei einem fehlgeschlagenen Ersetzen darf keine temporaere Datei verbleiben.");
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
