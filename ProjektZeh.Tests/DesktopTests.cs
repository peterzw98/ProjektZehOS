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
    public void KeyboardMenu_SpaceLaunch_DoesNotTypeSpaceIntoNewTerminal()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService());
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: 59));
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Space));
        desktop.Handle(new(InputEventType.Text, Character: ' '));
        Assert.AreEqual(1, desktop.Windows.Count);
        var terminal = (TerminalApp)desktop.Windows.ActiveWindow!.Application;
        Assert.AreEqual("", terminal.Editor.Text);
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Space));
        desktop.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.AreEqual("x", terminal.Editor.Text);
    }

    [TestMethod]
    public void Taskbar_MinimizeRestore_AndMenuEscape_PreserveDraft()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService());
        desktop.OpenTerminal();
        var window = desktop.Windows.ActiveWindow!;
        var terminal = (TerminalApp)window.Application;
        desktop.Handle(new(InputEventType.Text, Character: 'x'));
        desktop.Handle(new(InputEventType.PointerDown, X: 320, Y: 740, Button: PointerButtons.Left));
        Assert.IsNull(desktop.Windows.ActiveWindow);
        Assert.IsTrue(window.IsMinimized);
        desktop.Handle(new(InputEventType.PointerDown, X: 320, Y: 740, Button: PointerButtons.Left));
        Assert.AreSame(window, desktop.Windows.ActiveWindow);
        desktop.Handle(new(InputEventType.PointerDown, X: 40, Y: 740, Button: PointerButtons.Left));
        Assert.IsTrue(desktop.CloseMenu());
        Assert.IsFalse(desktop.CloseMenu());
        Assert.AreEqual("x", terminal.Editor.Text);
    }

    [TestMethod]
    public void AltTab_ThreeWindows_CyclesWithoutEditingTerminal()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService());
        desktop.OpenTerminal();
        var first = desktop.Windows.ActiveWindow!;
        desktop.OpenTerminal();
        var second = desktop.Windows.ActiveWindow!;
        desktop.OpenTerminal();
        var third = desktop.Windows.ActiveWindow!;
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: 15));
        Assert.AreSame(first, desktop.Windows.ActiveWindow);
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: 15));
        Assert.AreSame(second, desktop.Windows.ActiveWindow);
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: 15));
        Assert.AreSame(third, desktop.Windows.ActiveWindow);
        desktop.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.AreEqual("", ((TerminalApp)third.Application).Editor.Text);
        desktop.Handle(new(InputEventType.Reset));
        desktop.Handle(new(InputEventType.Text, Character: 'y'));
        Assert.AreEqual("y", ((TerminalApp)third.Application).Editor.Text);
    }

    [TestMethod]
    public void MenuSearch_FiltersAppsWithoutLeakingTextIntoTerminal()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService());
        desktop.OpenTerminal();
        var terminal = (TerminalApp)desktop.Windows.ActiveWindow!.Application;
        desktop.Handle(new(InputEventType.PointerDown, X: 40, Y: 740, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.Text, Character: 'z'));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(1, desktop.Windows.Count);
        Assert.AreEqual("", terminal.Editor.Text);
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Backspace));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(2, desktop.Windows.Count);
    }

    [TestMethod]
    public void Desktop_BootsEmpty_AndMenuStarterRespectsLimit()
    {
        int shells = 0;
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => { shells++; return new ShellService(); });
        Assert.AreEqual(0, desktop.Windows.Count);
        Assert.AreEqual(0, shells);
        desktop.Handle(new(InputEventType.PointerDown, X: 40, Y: 740, Button: PointerButtons.Right));
        Assert.AreEqual(0, desktop.Windows.Count);
        desktop.Handle(new(InputEventType.PointerDown, X: 40, Y: 740, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(1, desktop.Windows.Count);
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
        desktop.OpenTerminal();
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
