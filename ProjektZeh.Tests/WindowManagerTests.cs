using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WindowManagerTests
{
    [TestMethod]
    public void Maximize_Twice_RestoresGeometryAndKeepsTaskbarSpace()
    {
        var manager = new WindowManager(400, 300);
        var window = new Window("Test", 20, 30, 180, 150, new ProbeApp());
        manager.Add(window);
        manager.Handle(new(InputEventType.PointerDown, X: 152, Y: 46, Button: PointerButtons.Left));
        Assert.AreEqual(400, window.Width);
        Assert.AreEqual(300, window.Height);
        manager.Handle(new(InputEventType.PointerDown, X: 352, Y: 16, Button: PointerButtons.Left));
        Assert.AreEqual(20, window.X);
        Assert.AreEqual(30, window.Y);
        Assert.AreEqual(180, window.Width);
        Assert.AreEqual(150, window.Height);
    }

    [TestMethod]
    public void Minimize_RemovesKeyboardFocusWithoutClosingApplication()
    {
        var manager = new WindowManager(400, 300);
        var app = new ProbeApp();
        manager.Add(new Window("Test", 20, 30, 180, 150, app));
        manager.Handle(new(InputEventType.PointerDown, X: 120, Y: 46, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.IsNull(manager.ActiveWindow);
        Assert.AreEqual(1, manager.Count);
        Assert.AreEqual(0, app.Closes);
        Assert.IsFalse(app.Events.Exists(e => e.Type == InputEventType.Text));
    }

    [TestMethod]
    public void Resize_BottomRight_ClampsToWorkspaceAndResetStopsCapture()
    {
        var manager = new WindowManager(400, 300);
        var window = new Window("Test", 20, 30, 180, 150, new ProbeApp());
        manager.Add(window);
        manager.Handle(new(InputEventType.PointerDown, X: 199, Y: 179, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.PointerMove, X: 900, Y: 900));
        Assert.AreEqual(380, window.Width);
        Assert.AreEqual(270, window.Height);
        manager.Reset();
        manager.Handle(new(InputEventType.PointerMove, X: 50, Y: 50));
        Assert.AreEqual(380, window.Width);
    }

    [TestMethod]
    public void Focus_OverlappingWindows_OnlyTopmostReceivesKeysAndText()
    {
        var manager = new WindowManager(400, 300);
        var back = new ProbeApp();
        var front = new ProbeApp();
        var first = new Window("First", 10, 10, 180, 150, back);
        var second = new Window("Second", 80, 80, 180, 150, front);
        manager.Add(first);
        manager.Add(second);
        manager.Handle(new(InputEventType.PointerDown, X: 100, Y: 130, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.Text, Character: 'a'));
        Assert.AreSame(second, manager.ActiveWindow);
        Assert.AreEqual(1, front.Events.FindAll(e => e.Type == InputEventType.Text).Count);
        Assert.AreEqual(0, back.Events.FindAll(e => e.Type == InputEventType.Text).Count);

        manager.Handle(new(InputEventType.PointerUp, X: 100, Y: 130, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.PointerDown, X: 20, Y: 60, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.KeyDown, KeyCode: 30));
        Assert.AreSame(first, manager.ActiveWindow);
        Assert.AreEqual(InputEventType.Reset, front.Events[^1].Type);
        Assert.AreEqual(InputEventType.KeyDown, back.Events[^1].Type);
        Assert.AreEqual(1, back.Starts);
        Assert.AreEqual(1, front.Starts);
    }

    [TestMethod]
    public void Drag_OutsideTitleAndOtherButtonRelease_KeepsCaptureUntilLeftRelease()
    {
        var manager = new WindowManager(400, 300);
        var window = new Window("Drag", 10, 10, 180, 150, new ProbeApp());
        manager.Add(window);
        manager.Handle(new(InputEventType.PointerDown, X: 30, Y: 20, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.PointerMove, X: 300, Y: 260));
        Assert.AreEqual(220, window.X);
        Assert.AreEqual(150, window.Y);
        manager.Handle(new(InputEventType.PointerUp, X: 300, Y: 260, Button: PointerButtons.Right));
        manager.Handle(new(InputEventType.PointerMove, X: 90, Y: 80));
        Assert.AreEqual(70, window.X);
        Assert.AreEqual(70, window.Y);
        manager.Handle(new(InputEventType.PointerUp, X: 90, Y: 80, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.PointerMove, X: 0, Y: 0));
        Assert.AreEqual(70, window.X);
    }

    [TestMethod]
    public void Reset_DuringDrag_CancelsCaptureAndResetsApplication()
    {
        var app = new ProbeApp();
        var manager = new WindowManager(400, 300);
        var window = new Window("Reset", 10, 10, 180, 150, app);
        manager.Add(window);
        manager.Handle(new(InputEventType.PointerDown, X: 30, Y: 20, Button: PointerButtons.Left));
        manager.Handle(new(InputEventType.Reset));
        manager.Handle(new(InputEventType.PointerMove, X: 100, Y: 100));
        Assert.AreEqual(10, window.X);
        Assert.IsTrue(app.Events.Exists(e => e.Type == InputEventType.Reset));
    }

    [TestMethod]
    public void Close_ActiveAndLastWindow_ReleasesAppAndAllowsEmptyDesktop()
    {
        var manager = new WindowManager(400, 300);
        var app = new ProbeApp();
        var first = new Window("Back", 10, 10, 180, 150, new ProbeApp());
        manager.Add(first);
        manager.Add(new Window("Close", 80, 80, 180, 150, app));
        manager.Handle(new(InputEventType.PointerDown, X: 245, Y: 92, Button: PointerButtons.Left));
        Assert.AreEqual(1, manager.Count);
        Assert.AreEqual(1, app.Closes);
        Assert.AreSame(first, manager.ActiveWindow);
        manager.Handle(new(InputEventType.PointerDown, X: 175, Y: 22, Button: PointerButtons.Left));
        Assert.AreEqual(0, manager.Count);
        Assert.IsNull(manager.ActiveWindow);
        manager.Handle(new(InputEventType.Text, Character: 'a'));
        manager.Reset();
    }

    [TestMethod]
    public void Render_ClipsContentsAndDrawsWindowsBackToFront()
    {
        var pixels = new int[400 * 300];
        var surface = new PixelSurface(400, 300, pixels);
        var manager = new WindowManager(400, 300);
        var first = new Window("Back", 10, 10, 180, 150, new ProbeApp { Color = 7 });
        var second = new Window("Front", 80, 80, 180, 150, new ProbeApp { Color = 9 });
        manager.Add(first);
        manager.Add(second);
        manager.Render(surface, new byte[256], 8, 1);
        Assert.AreEqual(0, pixels[0]);
        Assert.AreEqual(7, pixels[60 * 400 + 20]);
        Assert.AreEqual(9, pixels[130 * 400 + 100]);
        Assert.AreNotEqual(7, pixels[10 * 400 + 10]);
        Assert.AreNotEqual(9, pixels[80 * 400 + 80]);
        manager.Handle(new(InputEventType.PointerDown, X: 20, Y: 60, Button: PointerButtons.Left));
        manager.Render(surface, new byte[256], 8, 1);
        Assert.AreEqual(7, pixels[130 * 400 + 100]);
    }

    [TestMethod]
    public void Add_ExtremePosition_ClampsAndRejectsInvalidSizesAndDuplicates()
    {
        var manager = new WindowManager(400, 300);
        var window = new Window("Edges", int.MaxValue, int.MinValue, 180, 150, new ProbeApp());
        manager.Add(window);
        Assert.AreEqual(220, window.X);
        Assert.AreEqual(0, window.Y);
        Assert.ThrowsException<ArgumentException>(() => manager.Add(window));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            manager.Add(new Window("Too large", 0, 0, 401, 150, new ProbeApp())));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Window("Too small", 0, 0, 1, 1, new ProbeApp()));
    }

    private sealed class ProbeApp : IApplication
    {
        public readonly List<InputEvent> Events = [];
        public int Starts { get; private set; }
        public int Closes { get; private set; }
        public int Color { get; init; }
        public void Start() => Starts++;
        public void Handle(InputEvent value) => Events.Add(value);
        public void Update(long milliseconds) { }
        public void Resize(int width, int height) { }
        public void Close() => Closes++;
        public void Draw(PixelSurface surface, int x, int y, int width, int height, bool active) =>
            surface.FillRectangle(int.MinValue / 2, int.MinValue / 2, int.MaxValue, int.MaxValue, Color);
    }
}
