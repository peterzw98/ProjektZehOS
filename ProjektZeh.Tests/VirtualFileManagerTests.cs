using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Input;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

/// <summary>Dateimanager an /proc, /sys, /dev (Spec E5b/E6b §3a): eigener Ort, Hoch an der Wurzel verlaesst den Baum, nur lesbar.</summary>
[TestClass]
public sealed class VirtualFileManagerTests
{
    // Wie FileManagerTests: Standardlayout 796 x 526 mit 8 x 16-Schrift.
    private const int ActionY = 80;
    private readonly UiPainter painter = new(new byte[4096], 8, 16);
    private string directory = "";
    private FileService files = null!;

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), "zehos-virtual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        files = new FileService(directory, session: new UserSession("2", "bob", false, directory),
            virtualSource: _ => ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetBytes("inhalt\n")));
    }

    [TestCleanup]
    public void Remove() => Directory.Delete(directory, true);

    private FileManagerApp Start()
    {
        var app = new FileManagerApp(files, painter, _ => true);
        app.Start();
        return app;
    }

    private static void Click(FileManagerApp app, int x, int y)
    {
        app.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        app.Handle(new(InputEventType.PointerUp, X: x, Y: y, Button: PointerButtons.Left));
    }

    [TestMethod]
    public void VirtualTreeIsAPlaceOutsideTheWorkingDirectory()
    {
        var app = Start();
        Assert.IsTrue(app.LoadDirectory("/sys", true));
        Assert.AreEqual("/sys", app.Location);
        Assert.AreEqual(directory, files.CurrentDirectory, "Das Arbeitsverzeichnis bleibt der letzte normale Ort.");
        Assert.IsTrue(app.LoadDirectory("kernel", true));
        Assert.AreEqual("/sys/kernel", app.Location);
    }

    [TestMethod]
    public void UpAtTheTreeRootReturnsToThePreviousNormalPlaceAndBackReturns()
    {
        var app = Start();
        app.LoadDirectory("/sys", true);
        app.LoadDirectory("kernel", true);
        Click(app, 130, 30);
        Assert.AreEqual("/sys", app.Location);
        Click(app, 130, 30);
        Assert.AreEqual(directory, app.Location);
        Click(app, 30, 30);
        Assert.AreEqual("/sys", app.Location, "Zurueck fuehrt wieder in den Baum.");
    }

    [TestMethod]
    public void WriteActionsAreRefusedInsideTheTree()
    {
        var app = Start();
        app.LoadDirectory("/proc", true);
        Click(app, 400, ActionY);
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Delete));
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Delete));
        Assert.AreEqual("/proc", app.Location);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(directory).Length, "Nichts darf auf der Platte entstehen.");
    }

    [TestMethod]
    public void UnknownVirtualPathKeepsThePlaceAndReportsIt()
    {
        var app = Start();
        Assert.IsFalse(app.LoadDirectory("/proc/version", true), "Eine Datei ist kein Ort.");
        Assert.AreEqual(directory, app.Location);
        Assert.AreNotEqual(string.Empty, app.Status);
    }
}
