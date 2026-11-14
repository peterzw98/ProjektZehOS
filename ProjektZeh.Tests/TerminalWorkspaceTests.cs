using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class TerminalWorkspaceTests
{
    [TestMethod]
    public void TabsAndSplits_KeepDraftsAndShellsIndependent_AndReleaseClosedViews()
    {
        int released = 0;
        var app = Create(() => { var shell = new ShellService(); shell.OnClose = () => released++; return shell; });
        app.Start(); app.Resize(800, 500);
        var first = app.ActiveTerminal;
        Type(app, "echo first");
        Assert.IsTrue(app.Split(true));
        Assert.AreEqual(2, app.ViewCount);
        Assert.AreEqual("", app.ActiveTerminal.Editor.Text);
        Type(app, "echo second");
        app.FocusNextPane();
        Assert.AreSame(first, app.ActiveTerminal);
        Assert.AreEqual("echo first", first.Editor.Text);
        Assert.IsTrue(app.NewTab());
        Assert.AreEqual(2, app.TabCount);
        Assert.AreEqual(1, app.ActiveTabIndex);
        app.CloseActiveView();
        Assert.AreEqual(1, released);
        Assert.AreEqual(1, app.TabCount);
        app.Close();
        Assert.AreEqual(3, released);
        Assert.IsTrue(app.IsCloseRequested);
    }

    [TestMethod]
    public void ResourceLimits_RejectExtraTabsAndTooSmallSplits_WithoutCreatingShells()
    {
        int created = 0;
        var app = Create(() => { created++; return new ShellService(); });
        app.Start(); app.Resize(640, 400);
        while (app.NewTab()) { }
        Assert.AreEqual(TerminalWorkspaceApp.MaximumTabs, app.TabCount);
        Assert.AreEqual(app.TabCount, created);
        app.Resize(320, 159);
        Assert.IsFalse(app.Split(false));
        Assert.AreEqual(app.TabCount, created);
        app.Close();
    }

    [TestMethod]
    public void LocalPointerCoordinates_FocusTheClickedPane_AfterDrawingAtWindowOffset()
    {
        var app = Create(); app.Start(); app.Resize(800, 500);
        var left = app.ActiveTerminal;
        app.Split(true);
        var right = app.ActiveTerminal;
        var surface = new PixelSurface(1024, 768, new int[1024 * 768]);
        app.Draw(surface, 72, 112, 800, 500, true);
        app.Handle(new(InputEventType.PointerDown, X: 50, Y: 100, Button: PointerButtons.Left));
        app.Handle(new(InputEventType.PointerUp, X: 50, Y: 100, Button: PointerButtons.Left));
        Assert.AreSame(left, app.ActiveTerminal);
        app.Handle(new(InputEventType.PointerDown, X: 600, Y: 100, Button: PointerButtons.Left));
        app.Handle(new(InputEventType.PointerUp, X: 600, Y: 100, Button: PointerButtons.Left));
        Assert.AreSame(right, app.ActiveTerminal);
        app.Close();
    }

    [TestMethod]
    public void KeyboardShortcutsAndReset_DoNotTypeShortcutLetters_AndPreserveDraft()
    {
        var app = Create(); app.Start(); app.Resize(800, 500);
        Type(app, "draft");
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        app.Handle(new(InputEventType.KeyDown, KeyCode: 20));
        app.Handle(new(InputEventType.Text, Character: 'T'));
        Assert.AreEqual(2, app.TabCount);
        Assert.AreEqual("", app.ActiveTerminal.Editor.Text);
        app.Handle(new(InputEventType.Reset));
        Type(app, "x");
        app.SelectTab(0);
        Assert.AreEqual("draft", app.ActiveTerminal.Editor.Text);
        app.SelectTab(1);
        Assert.AreEqual("x", app.ActiveTerminal.Editor.Text);
        app.Close();
    }

    [TestMethod]
    public void SearchProfileAndZoom_OperateOnFocusedView_AndRenderWithinClip()
    {
        var app = Create(); app.Start(); app.Resize(640, 400);
        app.ActiveTerminal.Buffer.WriteLine("needle");
        Assert.IsTrue(app.ActiveTerminal.Find("needle"));
        app.SetProfile(1);
        app.ActiveTerminal.SetZoom(2);
        Assert.AreEqual(1, app.Profile);
        Assert.AreEqual(2, app.ActiveTerminal.Zoom);
        int[] pixels = new int[800 * 600];
        var surface = new PixelSurface(800, 600, pixels);
        surface.SetClip(40, 40, 640, 400);
        app.Draw(surface, 40, 40, 640, 400, true);
        Assert.AreEqual(0, pixels[0]);
        Assert.AreEqual(0, pixels[599 * 800 + 799]);
        app.Close();
    }

    [TestMethod]
    public void GuestFontSize_ExpandsToolbarHitAreaAlongWithText()
    {
        var app = new TerminalWorkspaceApp(() => new ShellService(), new byte[16384], 16, 32);
        app.Start(); app.Resize(960, 600);
        app.Handle(new(InputEventType.PointerDown, X: 10, Y: 60, Button: PointerButtons.Left));
        Assert.AreEqual(2, app.TabCount);
        app.Close();
    }
    [TestMethod]
    public void GuestFontSize_RendersCloseGlyphsAndMatchesSearchCloseHitTarget()
    {
        var font = new byte[16384]; Array.Fill(font, (byte)0xff);
        var app = new TerminalWorkspaceApp(() => new ShellService(), font, 16, 32);
        app.Start(); app.Resize(960, 600);
        var pixels = new int[960 * 600];
        var surface = new PixelSurface(960, 600, pixels);
        app.Draw(surface, 0, 0, 960, 600, true);
        Assert.AreEqual(ProjektZeh.UI.UiPainter.Secondary, pixels[10 * 960 + 160]);
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        app.Handle(new(InputEventType.KeyDown, KeyCode: 33));
        app.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Control));
        app.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.LeftShift));
        app.Draw(surface, 0, 0, 960, 600, true);
        Assert.AreEqual(ProjektZeh.UI.UiPainter.Accent, pixels[99 * 960 + 940]);
        app.Handle(new(InputEventType.PointerDown, X: 940, Y: 100, Button: PointerButtons.Left));
        app.Handle(new(InputEventType.Text, Character: 'z'));
        Assert.AreEqual("z", app.ActiveTerminal.Editor.Text);
        app.Close();
    }
    private static TerminalWorkspaceApp Create(Func<ShellService>? factory = null) =>
        new(factory ?? (() => new ShellService()), new byte[4096], 8, 16);
    private static void Type(TerminalWorkspaceApp app, string text)
    {
        foreach (char value in text) app.Handle(new(InputEventType.Text, Character: value));
    }
}
