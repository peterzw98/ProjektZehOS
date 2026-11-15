using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Input;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FileManagerTests
{
    // Standardlayout 796 x 526 mit 8 x 16-Schrift: Werkzeugleiste y 8..52, Aktionen y 60..104, erste Zeile ab y 140.
    private const int ActionY = 80;
    private const int FirstRowY = 148;
    private readonly UiPainter painter = new(new byte[4096], 8, 16);
    private string directory = "";

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), "zehos-explorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Remove() => Directory.Delete(directory, true);

    private static void Click(FileManagerApp app, int x, int y)
    {
        app.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        app.Handle(new(InputEventType.PointerUp, X: x, Y: y, Button: PointerButtons.Left));
    }

    private static void Type(FileManagerApp app, string text)
    {
        foreach (char c in text) app.Handle(new(InputEventType.Text, Character: c));
    }

    private static void Key(FileManagerApp app, PhysicalKey key) => app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)key));

    private FileManagerApp Start(FileService files)
    {
        var app = new FileManagerApp(files, painter, _ => true);
        app.Start();
        return app;
    }

    [TestMethod]
    public void History_BackAndForward_FollowOpenedFolder()
    {
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        var files = new FileService(directory);
        var app = Start(files);
        app.Update(1000);
        Click(app, 300, FirstRowY);
        app.Update(1100);
        Click(app, 300, FirstRowY);
        Assert.AreEqual(Path.Combine(directory, "sub"), files.CurrentDirectory);
        Click(app, 30, 30);
        Assert.AreEqual(directory, files.CurrentDirectory);
        Click(app, 80, 30);
        Assert.AreEqual(Path.Combine(directory, "sub"), files.CurrentDirectory);
        Click(app, 130, 30);
        Assert.AreEqual(directory, files.CurrentDirectory, "Hoch fuehrt zum uebergeordneten Ordner.");
    }

    [TestMethod]
    public void NewFolder_AndRename_UseInlineNameField()
    {
        File.WriteAllText(Path.Combine(directory, "x.txt"), "inhalt");
        var app = Start(new FileService(directory));
        Click(app, 400, ActionY);
        Type(app, "projekte");
        Key(app, PhysicalKey.Enter);
        Assert.IsTrue(Directory.Exists(Path.Combine(directory, "projekte")));

        Directory.Delete(Path.Combine(directory, "projekte"));
        Key(app, PhysicalKey.F5);
        Click(app, 300, FirstRowY);
        Key(app, PhysicalKey.F2);
        Type(app, "y.txt");
        Key(app, PhysicalKey.Enter);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "y.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "x.txt")));
    }

    [TestMethod]
    public void Delete_NeedsSecondConfirmation_AndEscapeCancelsNaming()
    {
        string target = Path.Combine(directory, "weg.txt");
        File.WriteAllText(target, "x");
        var app = Start(new FileService(directory));
        Click(app, 300, FirstRowY);
        Key(app, PhysicalKey.Delete);
        Assert.IsTrue(File.Exists(target), "Der erste Druck fragt nur nach.");
        Key(app, PhysicalKey.Delete);
        Assert.IsFalse(File.Exists(target));

        Click(app, 400, ActionY);
        Key(app, PhysicalKey.Escape);
        Type(app, "nichts");
        Key(app, PhysicalKey.Enter);
        Assert.AreEqual(0, Directory.GetDirectories(directory).Length);
    }

    [TestMethod]
    public void Naming_IsCancelledBySelectionChangeOrSecondClick_AndKeepsRenameTarget()
    {
        File.WriteAllText(Path.Combine(directory, "a.txt"), "a");
        File.WriteAllText(Path.Combine(directory, "b.txt"), "b");
        var app = Start(new FileService(directory));
        Click(app, 300, FirstRowY);
        Key(app, PhysicalKey.F2);
        // Andere Zeile waehlen bricht ab; Esc erreicht die App im Desktop nicht.
        Click(app, 300, FirstRowY + 24);
        Type(app, "x.txt");
        Key(app, PhysicalKey.Enter);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, Array.ConvertAll(Directory.GetFiles(directory), Path.GetFileName));

        Click(app, 400, ActionY);
        Click(app, 400, ActionY);
        Type(app, "kein");
        Key(app, PhysicalKey.Enter);
        Assert.AreEqual(0, Directory.GetDirectories(directory).Length, "Zweiter Klick auf Ordner neu bricht ab.");
    }

    [TestMethod]
    public void Navigation_EndsOpenRename_SoFileIsNotMovedIntoOtherFolder()
    {
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        var files = new FileService(directory);
        var app = Start(files);
        Click(app, 300, FirstRowY);
        Click(app, 400, ActionY);
        // Ordner wechseln, waehrend die Eingabe offen ist; der Index der Auswahl bleibt dabei 0.
        Click(app, 300, FirstRowY);
        app.Update(100);
        Click(app, 300, FirstRowY);
        Assert.AreEqual(Path.Combine(directory, "sub"), files.CurrentDirectory);
        Type(app, "verirrt");
        Key(app, PhysicalKey.Enter);
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "sub", "verirrt")));
    }

    [TestMethod]
    public void Rename_UsesFileSelectedWhenStarted()
    {
        File.WriteAllText(Path.Combine(directory, "a.txt"), "a");
        var app = Start(new FileService(directory));
        Click(app, 300, FirstRowY);
        Key(app, PhysicalKey.F2);
        Type(app, "c.txt");
        Key(app, PhysicalKey.Enter);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "c.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "a.txt")));
    }

    [TestMethod]
    public void Row_FormatsNameSizeAndType()
    {
        Assert.AreEqual("1.5 KB", FileManagerApp.FormatSize(1536));
        Assert.AreEqual("", FileManagerApp.FormatSize(-1));
        string row = FileManagerApp.Row(new FileEntry("/data/bericht.txt", false), 12, 40);
        Assert.AreEqual(40, row.Length);
        StringAssert.StartsWith(row, "bericht.txt");
        StringAssert.EndsWith(row, "12 B TXT   ");
        StringAssert.Contains(FileManagerApp.Row(new FileEntry("/data/ordner", true), -1, 40), "Ordner");
    }
}
