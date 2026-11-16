using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Apps.Pong;
using ProjektZeh.Apps.TextEditor;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Apps;
using ProjektZeh.Runtime.Wasm;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopTests
{
    [TestMethod]
    public void Escape_WithAppCaptureReachesAppBeforeRecovery()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        var app = new CaptureProbeApp();
        app.Input.SetCapture(true);
        desktop.Windows.Add(new Window("Probe", 20, 30, 300, 200, app));
        var escape = new InputEvent(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Escape);
        Assert.IsTrue(desktop.HandleEscape(escape));
        Assert.IsFalse(app.Input.IsCaptured);
        Assert.IsTrue(app.Input.TryRead(out var received));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.KeyDown, (byte)PhysicalKey.Escape), received);
        Assert.IsTrue(desktop.HandleEscape(escape with { IsRepeat = true }));
        Assert.IsFalse(desktop.HandleEscape(escape));
    }

    [TestMethod]
    public void Escape_WithMenuOpenedDuringAppCaptureClosesMenuOnly()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        var app = new CaptureProbeApp();
        app.Input.SetCapture(true);
        desktop.Windows.Add(new Window("Probe", 20, 30, 300, 200, app));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F1));
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Alt));
        Assert.IsFalse(app.Input.IsCaptured);
        while (app.Input.TryRead(out _)) { }
        var escape = new InputEvent(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Escape);
        Assert.IsTrue(desktop.HandleEscape(escape));
        // CloseMenu setzt die Fenster zurueck; die App sieht nur dieses Reset, kein Esc.
        while (app.Input.TryRead(out var received)) Assert.AreNotEqual(WasmInputEventType.KeyDown, received.Type);
        Assert.IsFalse(desktop.CloseMenu());
        Assert.IsFalse(desktop.HandleEscape(escape));
    }

    [TestMethod]
    public void Escape_DuringWindowDragDoesNotGoToApp()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenTerminal();
        var window = desktop.Windows.ActiveWindow!;
        desktop.Handle(new(InputEventType.PointerDown, X: window.X + 20, Y: window.Y + 10, Button: PointerButtons.Left));
        Assert.IsTrue(desktop.Windows.HasPointerCapture);
        Assert.IsFalse(desktop.HandleEscape(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Escape)));
    }

    private sealed class CaptureProbeApp : IApplication
    {
        public WasmInputBuffer Input { get; } = new();
        public bool CapturesPointer => Input.IsCaptured;
        public void Start() { }
        public void Handle(InputEvent value) => Input.Handle(value);
        public void Update(long milliseconds) { }
        public void Draw(PixelSurface surface, int x, int y, int width, int height, bool active) { }
        public void Close() { }
    }

    [TestMethod]
    public void ContentCapture_SecondButtonOverTaskbarDoesNotOpenMenu()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenPong();
        var window = desktop.Windows.ActiveWindow!;
        desktop.Handle(new(InputEventType.PointerDown, X: window.ContentX + 20, Y: window.ContentY + 20, Button: PointerButtons.Right));
        desktop.Handle(new(InputEventType.PointerDown, X: 40, Y: 740, Button: PointerButtons.Left));
        Assert.IsFalse(desktop.CloseMenu());
        desktop.Handle(new(InputEventType.PointerUp, X: 40, Y: 740, Button: PointerButtons.Right));
        Assert.IsTrue(desktop.Windows.HasPointerCapture);
        desktop.Handle(new(InputEventType.PointerUp, X: 40, Y: 740, Button: PointerButtons.Left));
        Assert.IsFalse(desktop.Windows.HasPointerCapture);
    }

    [TestMethod]
    public void AltDuringDrag_MouseReleaseStillEndsCapture()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenTerminal();
        var window = desktop.Windows.ActiveWindow!;
        desktop.Handle(new(InputEventType.PointerDown, X: window.X + 20, Y: window.Y + 10, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.PointerUp, X: 200, Y: 200, Button: PointerButtons.Left));
        Assert.IsFalse(desktop.Windows.HasPointerCapture);
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Alt));
        int x = window.X;
        desktop.Handle(new(InputEventType.PointerMove, X: 400, Y: 300));
        Assert.AreEqual(x, window.X);
    }

    [TestMethod]
    public void WorkspaceSwitchDuringDrag_EndsCaptureAndLeavesWindowInPlace()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenTerminal();
        var window = desktop.Windows.ActiveWindow!;
        desktop.Handle(new(InputEventType.PointerDown, X: window.X + 20, Y: window.Y + 10, Button: PointerButtons.Left));
        Assert.IsTrue(desktop.Windows.HasPointerCapture);
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Right));
        Assert.AreEqual(1, desktop.Windows.CurrentWorkspace);
        Assert.IsFalse(desktop.Windows.HasPointerCapture);
        int x = window.X, y = window.Y;
        desktop.Handle(new(InputEventType.PointerMove, X: 400, Y: 300));
        desktop.Handle(new(InputEventType.PointerUp, X: 400, Y: 300, Button: PointerButtons.Left));
        Assert.AreEqual((x, y, 0), (window.X, window.Y, window.Workspace));
    }

    [TestMethod]
    public void OpenMenu_PausesPongUntilWindowRegainsFocus()
    {
        var desktop = new Desktop(1024, 768, new byte[16384], 16, 32, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenPong();
        var game = ((PongApp)desktop.Windows.ActiveWindow!.Application).Game;
        var surface = new PixelSurface(1024, 768, new int[1024 * 768]);
        desktop.Render(surface, 0, 1000, 700, PointerButtons.None);
        desktop.Update(0); desktop.Update(100);
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F1));
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Render(surface, 200, 1000, 700, PointerButtons.None);
        double position = game.BallX;
        desktop.Update(200); desktop.Update(300);
        Assert.AreEqual(position, game.BallX);
        Assert.IsFalse(desktop.NeedsAnimation);
        desktop.CloseMenu();
        desktop.Render(surface, 400, 1000, 700, PointerButtons.None);
        desktop.Update(400); desktop.Update(500);
        Assert.AreNotEqual(position, game.BallX);
    }

    [DataTestMethod]
    [DataRow("Dateien")]
    [DataRow("Texteditor")]
    [DataRow("Pong")]
    public void Menu_SearchAndLaunch_PreservesMasterApplications(string name)
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F1));
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Alt));
        foreach (char character in name) desktop.Handle(new(InputEventType.Text, Character: character));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(1, desktop.Windows.Count);
        var window = desktop.Windows.ActiveWindow!;
        Assert.AreEqual(name, window.Title);
        Assert.IsTrue(window.Application is FileManagerApp or TextEditorApp or PongApp);
        desktop.Handle(new(InputEventType.PointerDown, X: window.X + window.Width - 48, Y: window.Y + 16, Button: PointerButtons.Left));
        Assert.IsTrue(window.IsMaximized);
        Assert.AreEqual(712, window.Height);
        desktop.Handle(new(InputEventType.PointerDown, X: window.X + window.Width - 80, Y: window.Y + 16, Button: PointerButtons.Left));
        Assert.IsTrue(window.IsMinimized);
        Assert.IsFalse(desktop.NeedsAnimation);
    }

    [TestMethod]
    public void KeyboardMenu_SpaceLaunch_DoesNotTypeSpaceIntoNewTerminal()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: 59));
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Alt));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Space));
        desktop.Handle(new(InputEventType.Text, Character: ' '));
        Assert.AreEqual(1, desktop.Windows.Count);
        var terminal = ((TerminalWorkspaceApp)desktop.Windows.ActiveWindow!.Application).ActiveTerminal;
        Assert.AreEqual("", terminal.Editor.Text);
        desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Space));
        desktop.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.AreEqual("x", terminal.Editor.Text);
    }

    [TestMethod]
    public void Taskbar_MinimizeRestore_AndMenuEscape_PreserveDraft()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenTerminal();
        var window = desktop.Windows.ActiveWindow!;
        var terminal = ((TerminalWorkspaceApp)window.Application).ActiveTerminal;
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
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
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
        Assert.AreEqual("", ((TerminalWorkspaceApp)third.Application).ActiveTerminal.Editor.Text);
        desktop.Handle(new(InputEventType.Reset));
        desktop.Handle(new(InputEventType.Text, Character: 'y'));
        Assert.AreEqual("y", ((TerminalWorkspaceApp)third.Application).ActiveTerminal.Editor.Text);
    }

    [TestMethod]
    public void MenuSearch_FiltersAppsWithoutLeakingTextIntoTerminal()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenTerminal();
        var terminal = ((TerminalWorkspaceApp)desktop.Windows.ActiveWindow!.Application).ActiveTerminal;
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
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => { shells++; return new ShellService(); }, () => new FileService("/data"));
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
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        var pixels = new int[1024 * 768];
        var surface = new PixelSurface(1024, 768, pixels);
        desktop.Render(surface, 0, 1000, 700, PointerButtons.None);
        var background = (int[])pixels.Clone();
        desktop.OpenTerminal();
        var active = desktop.Windows.ActiveWindow!;
        int oldX = active.X;
        int oldY = active.Y;
        desktop.Handle(new(InputEventType.PointerDown, X: oldX + 20, Y: oldY + 10, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.PointerMove, X: 20, Y: 10));
        desktop.Handle(new(InputEventType.PointerUp, X: 20, Y: 10, Button: PointerButtons.Left));
        desktop.Render(surface, 1000, 1000, 700, PointerButtons.None);
        int corner = (oldY + active.Height - 2) * 1024 + oldX + active.Width - 2;
        Assert.AreEqual(background[corner], pixels[corner]);
        while (desktop.Windows.ActiveWindow is { } window)
            desktop.Handle(new(InputEventType.PointerDown, X: window.X + window.Width - 16, Y: window.Y + 16, Button: PointerButtons.Left));
        desktop.Render(surface, 2000, 1000, 700, PointerButtons.None);
        Assert.AreEqual(0, desktop.Windows.Count);
        Assert.AreEqual(background[400 * 1024 + 500], pixels[400 * 1024 + 500]);
        Assert.IsTrue(desktop.OpenTerminal());
        Assert.IsInstanceOfType<TerminalWorkspaceApp>(desktop.Windows.ActiveWindow!.Application);
    }
}
