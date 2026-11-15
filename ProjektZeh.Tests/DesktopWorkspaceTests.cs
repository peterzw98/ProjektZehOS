using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Pong;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopWorkspaceTests
{
    private string directory = "";

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), "zehos-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Remove()
    {
        Directory.Delete(directory, true);
        UiPainter.SetTheme(true, 0);
    }

    private Desktop CreateDesktop() =>
        new(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService(directory));

    private static void Click(Desktop desktop, int x, int y)
    {
        desktop.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.PointerUp, X: x, Y: y, Button: PointerButtons.Left));
    }

    private static void Key(Desktop desktop, PhysicalKey key, bool down = true) =>
        desktop.Handle(new(down ? InputEventType.KeyDown : InputEventType.KeyUp, KeyCode: (byte)key));

    [TestMethod]
    public void SwitchWorkspace_HidesWindowsAndKeepsThemOpen()
    {
        var manager = new WindowManager(400, 300);
        var first = new Window("A", 20, 30, 180, 150, new WindowManagerTests.ProbeApp());
        manager.Add(first);
        manager.SwitchWorkspace(1);
        Assert.IsNull(manager.ActiveWindow);
        Assert.IsNull(manager.HitTest(60, 60));
        var second = new Window("B", 20, 30, 180, 150, new WindowManagerTests.ProbeApp());
        manager.Add(second);
        Assert.AreEqual(1, second.Workspace);
        manager.Cycle();
        Assert.AreSame(second, manager.ActiveWindow, "Alt+Tab bleibt auf dem aktuellen Desktop.");
        manager.ToggleTask(first);
        Assert.AreEqual(0, manager.CurrentWorkspace, "Aktivieren eines Fensters wechselt auf dessen Desktop.");
        Assert.AreSame(first, manager.ActiveWindow);
        Assert.AreEqual(2, manager.Count);
    }

    [TestMethod]
    public void MoveActiveToWorkspace_FollowsWindowAndIgnoresInvalidTarget()
    {
        var manager = new WindowManager(400, 300);
        var window = new Window("A", 20, 30, 180, 150, new WindowManagerTests.ProbeApp());
        manager.Add(window);
        manager.MoveActiveToWorkspace(WindowManager.WorkspaceCount);
        Assert.AreEqual(0, window.Workspace);
        manager.MoveActiveToWorkspace(2);
        Assert.AreEqual(2, window.Workspace);
        Assert.AreEqual(2, manager.CurrentWorkspace);
        Assert.AreSame(window, manager.ActiveWindow);
        Assert.AreEqual(0, manager.CountOn(0));
        Assert.AreEqual(1, manager.CountOn(2));
    }

    [TestMethod]
    public void RequestClose_OnOtherWorkspace_ShowsPendingConfirmation()
    {
        var manager = new WindowManager(400, 300);
        var app = new Moq.Mock<ProjektZeh.Apps.IApplication>();
        app.Setup(application => application.RequestClose()).Returns(false);
        var window = new Window("Editor", 20, 30, 180, 150, app.Object);
        manager.Add(window);
        manager.SwitchWorkspace(1);
        manager.RequestClose(window);
        Assert.AreEqual(0, manager.CurrentWorkspace, "Die Schliessrueckfrage muss sichtbar sein.");
        Assert.AreSame(window, manager.ActiveWindow);
        Assert.AreSame(window, manager.HitTest(60, 60));
        Assert.AreEqual(1, manager.Count);
        app.Verify(application => application.RequestClose(), Moq.Times.Once);
    }

    [TestMethod]
    public void CloseAll_ReturnsToFirstWorkspace()
    {
        var manager = new WindowManager(400, 300);
        manager.SwitchWorkspace(3);
        manager.Add(new Window("A", 20, 30, 180, 150, new WindowManagerTests.ProbeApp()));
        manager.CloseAll();
        Assert.AreEqual(0, manager.CurrentWorkspace, "Ein Sitzungswechsel beginnt auf Desktop 1.");
    }

    [TestMethod]
    public void CtrlAltArrows_SwitchAndMoveWithoutTypingIntoTerminal()
    {
        var desktop = CreateDesktop();
        desktop.OpenTerminal();
        var terminal = desktop.Windows.ActiveWindow!;
        Key(desktop, PhysicalKey.Control);
        Key(desktop, PhysicalKey.Alt);
        Key(desktop, PhysicalKey.Right);
        Assert.AreEqual(1, desktop.Windows.CurrentWorkspace);
        Assert.IsNull(desktop.Windows.ActiveWindow);
        Key(desktop, PhysicalKey.Left);
        Assert.AreSame(terminal, desktop.Windows.ActiveWindow);
        Key(desktop, PhysicalKey.LeftShift);
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: 4));
        Assert.AreEqual(2, terminal.Workspace, "Strg+Alt+Umschalt+3 verschiebt das Fenster auf Desktop 3.");
        Assert.AreEqual(2, desktop.Windows.CurrentWorkspace);
        desktop.Handle(new(InputEventType.Reset));
        desktop.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.AreEqual("x", ((Apps.Terminal.TerminalWorkspaceApp)terminal.Application).ActiveTerminal.Editor.Text);
    }

    [TestMethod]
    public void Pager_ClickSwitchesDesktop_AndTaskbarListsOnlyCurrentWindows()
    {
        var desktop = CreateDesktop();
        desktop.OpenTerminal();
        // Pager: Uhrbreite 5*8+32, vier Felder zu 36 px, 8 px Abstand.
        Click(desktop, 1024 - 72 - 3 * 36 - 8 + 10, 740);
        Assert.AreEqual(1, desktop.Windows.CurrentWorkspace);
        Click(desktop, 320, 740);
        Assert.AreEqual(1, desktop.Windows.CurrentWorkspace, "Auf dem leeren Desktop gibt es keinen Taskleisteneintrag.");
        Assert.IsNull(desktop.Windows.ActiveWindow);
    }

    [TestMethod]
    public void DesktopShortcuts_DoubleClickStarts_AndPinnedAppPersists()
    {
        var desktop = CreateDesktop();
        desktop.Update(1000);
        Click(desktop, 60, 58);
        Assert.AreEqual(0, desktop.Windows.Count, "Ein Klick waehlt nur aus.");
        desktop.Update(1200);
        Click(desktop, 60, 58);
        Assert.AreEqual(1, desktop.Windows.Count);
        Assert.AreEqual("Terminal 1", desktop.Windows.ActiveWindow!.Title);

        Click(desktop, 40, 740);
        foreach (char c in "pong") desktop.Handle(new(InputEventType.Text, Character: c));
        Click(desktop, 400, 600);
        var restored = CreateDesktop();
        restored.Update(3000);
        Click(restored, 60, 8 + 4 + 4 * 92 + 46);
        restored.Update(3100);
        Click(restored, 60, 8 + 4 + 4 * 92 + 46);
        Assert.IsInstanceOfType<PongApp>(restored.Windows.ActiveWindow!.Application);
    }

    [TestMethod]
    public void Settings_ShortcutsRoundTrip_AndDefaultStaysImplicit()
    {
        Assert.IsNull(DesktopSettings.Parse(new DesktopSettings().Serialize()).Shortcuts);
        var parsed = DesktopSettings.Parse(new DesktopSettings { Shortcuts = ["Pong", "Dateien"] }.Serialize());
        CollectionAssert.AreEqual(new[] { "Pong", "Dateien" }, parsed.Shortcuts);
        Assert.AreEqual(0, DesktopSettings.Parse("shortcuts=\n").Shortcuts!.Length);
    }

    [TestMethod]
    public void TileWrap_SplitsLongNamesIntoTwoLines()
    {
        Assert.AreEqual(("Terminal", ""), UiTileGrid.Wrap("Terminal", 9));
        Assert.AreEqual(("Einstellu", "ngen"), UiTileGrid.Wrap("Einstellungen", 9));
        Assert.AreEqual(("Freedoom", "Phase 1"), UiTileGrid.Wrap("Freedoom Phase 1", 9));
    }
}
