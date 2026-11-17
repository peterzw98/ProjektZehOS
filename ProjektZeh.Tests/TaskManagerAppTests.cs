using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class TaskManagerAppTests
{
    private string root = "";
    [TestInitialize]
    public void Create() { root = Path.Combine(Path.GetTempPath(), "zehos-taskmanager-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup]
    public void Remove() { Directory.Delete(root, true); UiPainter.SetTheme(true, 0); }

    private Desktop CreateDesktop(TaskManagerSection[]? sections = null) =>
        new(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService(root), systemSections: sections);

    private static void Press(TaskManagerApp app, PhysicalKey key) => app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)key));

    private static TaskManagerApp OpenTaskManager(Desktop desktop)
    {
        desktop.OpenTaskManager();
        return (TaskManagerApp)desktop.Windows.ActiveWindow!.Application;
    }

    // Seitenleiste auf "Anwendungen", dann Fokus in die Liste; die erste Zeile ist das zuerst geoeffnete Fenster.
    private static void ShowApplicationList(TaskManagerApp app)
    {
        Press(app, PhysicalKey.Down);
        Press(app, PhysicalKey.Tab);
    }

    [TestMethod]
    public void ActivatingSelectedWindowBringsItToFront()
    {
        var desktop = CreateDesktop();
        desktop.OpenTerminal(); var first = desktop.Windows.ActiveWindow!;
        desktop.OpenTerminal();
        var app = OpenTaskManager(desktop);
        ShowApplicationList(app);
        Press(app, PhysicalKey.Enter);
        Assert.AreSame(first, desktop.Windows.ActiveWindow);
        Assert.IsFalse(first.IsMinimized);
    }

    [TestMethod]
    public void ActivatingAlreadyActiveWindowDoesNotMinimizeIt()
    {
        var desktop = CreateDesktop();
        desktop.OpenTerminal();
        var app = OpenTaskManager(desktop);
        var own = desktop.Windows.ActiveWindow!;
        ShowApplicationList(app);
        Press(app, PhysicalKey.End);
        Press(app, PhysicalKey.Enter);
        Assert.AreSame(own, desktop.Windows.ActiveWindow);
        Assert.IsFalse(own.IsMinimized);
    }

    [TestMethod]
    public void CloseButtonClosesSelectedWindow()
    {
        var desktop = CreateDesktop();
        desktop.OpenTerminal(); var first = desktop.Windows.ActiveWindow!;
        desktop.OpenTerminal(); var second = desktop.Windows.ActiveWindow!;
        var app = OpenTaskManager(desktop);
        ShowApplicationList(app);
        // Liste -> Aktivieren -> Schliessen.
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Enter);
        Assert.AreEqual(2, desktop.Windows.Count);
        Assert.AreSame(second, desktop.Windows.GetWindow(0));
        Assert.AreNotSame(first, desktop.Windows.GetWindow(1));
        Assert.IsInstanceOfType<TaskManagerApp>(desktop.Windows.GetWindow(1).Application, "Nur das gewaehlte Fenster darf fehlen.");
    }

    [TestMethod]
    public void CloseButtonWithoutSelectionClosesNothing()
    {
        var desktop = CreateDesktop();
        desktop.OpenTerminal(); var first = desktop.Windows.ActiveWindow!;
        desktop.OpenTerminal();
        var app = OpenTaskManager(desktop);
        ShowApplicationList(app);
        // Das gewaehlte Fenster verschwindet von aussen; danach besteht keine Auswahl mehr.
        desktop.Windows.RequestClose(first);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Enter);
        Assert.AreEqual(2, desktop.Windows.Count);
    }

    [TestMethod]
    public void SectionIsReadAgainOnlyAfterRefreshInterval()
    {
        int reads = 0;
        var desktop = CreateDesktop([new("Zaehler", () => { reads++; return [reads.ToString(System.Globalization.CultureInfo.InvariantCulture)]; })]);
        var app = OpenTaskManager(desktop);
        Press(app, PhysicalKey.Down);
        Press(app, PhysicalKey.Down);
        Assert.AreEqual(1, reads);
        app.Update(5000);
        Assert.AreEqual(2, reads);
        app.Update(5999);
        Assert.AreEqual(2, reads);
        app.Update(6000);
        Assert.AreEqual(3, reads);
        Assert.AreEqual("3", app.Lines[0]);
    }
}
