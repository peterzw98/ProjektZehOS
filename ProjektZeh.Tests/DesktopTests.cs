using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopTests
{
    [TestMethod]
    public void Desktop_BootsTwoTerminals_AndStarterRespectsLimit()
    {
        int shells = 0;
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => { shells++; return new ShellService(); });
        Assert.AreEqual(2, desktop.Windows.Count);
        Assert.AreEqual(2, shells);
        desktop.Handle(new(InputEventType.PointerDown, X: 40, Y: 740, Button: PointerButtons.Right));
        Assert.AreEqual(2, desktop.Windows.Count);
        desktop.Handle(new(InputEventType.PointerDown, X: 40, Y: 740, Button: PointerButtons.Left));
        Assert.AreEqual(3, desktop.Windows.Count);
        while (desktop.OpenTerminal()) { }
        Assert.AreEqual(Desktop.MaximumWindows, desktop.Windows.Count);
        Assert.AreEqual(Desktop.MaximumWindows, shells);
    }

    [TestMethod]
    public void Desktop_RedrawAfterMoveAndClose_RestoresBackgroundAndCanReopen()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService());
        var pixels = new int[1024 * 768];
        var surface = new PixelSurface(1024, 768, pixels);
        var active = desktop.Windows.ActiveWindow!;
        int oldX = active.X;
        int oldY = active.Y;
        desktop.Handle(new(InputEventType.PointerDown, X: oldX + 20, Y: oldY + 10, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.PointerMove, X: 20, Y: 10));
        desktop.Handle(new(InputEventType.PointerUp, X: 20, Y: 10, Button: PointerButtons.Left));
        desktop.Render(surface, 1000, 1000, 700, PointerButtons.None);
        Assert.AreEqual(unchecked((int)0xff142236), pixels[(oldY + active.Height - 2) * 1024 + oldX + active.Width - 2]);
        while (desktop.Windows.ActiveWindow is { } window)
            desktop.Handle(new(InputEventType.PointerDown, X: window.X + window.Width - 16, Y: window.Y + 16, Button: PointerButtons.Left));
        desktop.Render(surface, 2000, 1000, 700, PointerButtons.None);
        Assert.AreEqual(0, desktop.Windows.Count);
        Assert.AreEqual(unchecked((int)0xff142236), pixels[400 * 1024 + 500]);
        Assert.IsTrue(desktop.OpenTerminal());
        Assert.IsInstanceOfType<TerminalApp>(desktop.Windows.ActiveWindow!.Application);
    }
}
