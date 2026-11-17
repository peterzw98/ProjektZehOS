using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Shell;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class InputScrollTests
{
    [TestMethod]
    public void Workspace_GermanControlPlusMinus_ZoomsWithoutTyping()
    {
        var app = new TerminalWorkspaceApp(() => new ShellService(), new byte[4096], 8, 16);
        app.Start();
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        app.Handle(new(InputEventType.KeyDown, KeyCode: 27));
        Assert.AreEqual(2, app.ActiveTerminal.Zoom);
        app.Handle(new(InputEventType.KeyDown, KeyCode: 53));
        Assert.AreEqual(1, app.ActiveTerminal.Zoom);
        Assert.AreEqual("", app.ActiveTerminal.Editor.Text);
    }

    [TestMethod]
    public void Workspace_ShortMenuWheel_ReachesLastAction()
    {
        var app = new TerminalWorkspaceApp(() => new ShellService(), new byte[4096], 8, 16);
        app.Start(); app.Resize(640, 200);
        app.ActiveTerminal.SetZoom(2);
        app.Handle(new(InputEventType.PointerDown, X: 100, Y: 100, Button: PointerButtons.Right));
        app.Handle(new(InputEventType.PointerScroll, X: 110, Y: 100, ScrollDelta: -20));
        app.Handle(new(InputEventType.PointerDown, X: 110, Y: 185, Button: PointerButtons.Left));
        Assert.AreEqual(1, app.ActiveTerminal.Zoom);
    }

    [TestMethod]
    public void Workspace_WheelTargetsHoveredSplitWithoutChangingActiveTerminal()
    {
        var app = new TerminalWorkspaceApp(() => new ShellService(), new byte[4096], 8, 16);
        app.Start(); app.Resize(800, 500);
        var first = app.ActiveTerminal;
        for (int i = 0; i < 100; i++) first.Buffer.WriteLine($"row {i}");
        Assert.IsTrue(app.Split(true));
        var second = app.ActiveTerminal;
        app.Handle(new(InputEventType.PointerScroll, X: 20, Y: 150, ScrollDelta: 1));
        Assert.AreEqual(3, first.ScrollOffset);
        Assert.AreEqual(0, second.ScrollOffset);
        Assert.AreSame(second, app.ActiveTerminal);
    }

    [DataTestMethod]
    [DataRow(43, false, false, false, '\0', '#')]
    [DataRow(43, true, false, false, '\0', '\'')]
    [DataRow(86, false, false, false, '\0', '<')]
    [DataRow(86, true, false, false, '\0', '>')]
    [DataRow(86, false, false, true, '\0', '|')]
    [DataRow(21, false, true, false, 'z', 'Z')]
    [DataRow(18, true, true, false, 'E', 'e')]
    [DataRow(26, false, true, false, 'ü', 'Ü')]
    [DataRow(16, false, false, true, '@', '@')]
    [DataRow(50, false, true, true, 'µ', 'µ')]
    public void GermanKeys_CorrectCosmosIsoKeysAndCapsLock(int code, bool shift, bool caps, bool altGr, char translated, char expected)
    {
        Assert.AreEqual(expected, GermanKeyboard.Translate((byte)code, translated, shift, caps, false, altGr));
    }

    [TestMethod]
    public void GermanKeys_ControlDoesNotInsertIsoPunctuation()
    {
        Assert.AreEqual('\0', GermanKeyboard.Translate(43, '\0', false, false, true, false));
        Assert.AreEqual('\0', GermanKeyboard.Translate(86, '\0', false, false, true, false));
    }

    [TestMethod]
    public void Pointer_StationaryWheel_ReportsPositionOnceAndResetsOnOverflow()
    {
        var input = new InputService(100, 80);
        input.ReportPointer(50, 40, PointerButtons.None, -2);
        Assert.IsTrue(input.TryRead(out var wheel));
        Assert.AreEqual(InputEventType.PointerScroll, wheel.Type);
        Assert.AreEqual(-2, wheel.ScrollDelta);
        Assert.AreEqual(50, wheel.X);
        Assert.AreEqual(40, wheel.Y);
        input.ReportPointer(50, 40, PointerButtons.None);
        Assert.IsFalse(input.TryRead(out _));
        for (int i = 0; i <= InputService.QueueCapacity; i++) input.ReportPointer(50, 40, PointerButtons.None, 1);
        Assert.IsTrue(input.TryRead(out var reset));
        Assert.AreEqual(InputEventType.Reset, reset.Type);
        Assert.IsFalse(input.TryRead(out _));
    }

    [TestMethod]
    public void Terminal_Wheel_ClampsPreservesDraftAndReturnsToLiveOnText()
    {
        var terminal = new TerminalApp(new ShellService(), new byte[4096], 8, 16, 48);
        terminal.Resize(400, 300);
        for (int i = 0; i < 100; i++) terminal.Buffer.WriteLine($"row {i}");
        TerminalTests.Type(terminal, "draft");
        terminal.Handle(new(InputEventType.PointerScroll, ScrollDelta: 1));
        Assert.AreEqual(3, terminal.ScrollOffset);
        Assert.AreEqual("draft", terminal.Editor.Text);
        terminal.Handle(new(InputEventType.PointerScroll, ScrollDelta: int.MaxValue));
        Assert.IsTrue(terminal.ScrollOffset > 3);
        terminal.Handle(new(InputEventType.PointerScroll, ScrollDelta: int.MinValue));
        Assert.AreEqual(0, terminal.ScrollOffset);
        terminal.Handle(new(InputEventType.PointerScroll, ScrollDelta: 1));
        TerminalTests.Type(terminal, "x");
        Assert.AreEqual(0, terminal.ScrollOffset);
    }

    [TestMethod]
    public void Terminal_PageUpScrollsBackAndPageDownForward()
    {
        var terminal = new TerminalApp(new ShellService(), new byte[4096], 8, 16, 48);
        terminal.Resize(400, 300);
        for (int i = 0; i < 100; i++) terminal.Buffer.WriteLine($"row {i}");
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageUp));
        int page = terminal.ScrollOffset;
        Assert.IsTrue(page > 0);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageUp));
        Assert.AreEqual(2 * page, terminal.ScrollOffset);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageDown));
        Assert.AreEqual(page, terminal.ScrollOffset);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageDown));
        Assert.AreEqual(0, terminal.ScrollOffset);
    }

    [TestMethod]
    public void Panel_WheelTargetsHoveredTextViewWithoutChangingFocusOrCursor()
    {
        var panel = new UiPanel();
        var first = new UiTextBox(1000, true) { Bounds = new(0, 0, 100, 60) };
        var second = new UiTextBox(1000, true) { Bounds = new(100, 0, 100, 60) };
        panel.Add(first); panel.Add(second);
        second.SetText(string.Join('\n', new string[30]));
        second.Draw(new PixelSurface(200, 100, new int[20000]), new UiPainter(new byte[4096], 8, 16), 0, 0, true);
        panel.Handle(new(InputEventType.PointerScroll, X: 110, Y: 20, ScrollDelta: -1));
        Assert.AreEqual(3, second.FirstRow);
        Assert.AreEqual(0, second.Cursor);
        Assert.AreSame(first, panel.Focused);
        panel.Handle(new(InputEventType.PointerScroll, X: 110, Y: 20, ScrollDelta: int.MinValue));
        Assert.AreEqual(27, second.FirstRow);
        panel.Handle(new(InputEventType.PointerScroll, X: 110, Y: 20, ScrollDelta: int.MaxValue));
        Assert.AreEqual(0, second.FirstRow);
    }

    [TestMethod]
    public void List_WheelScrollSurvivesDrawWithoutChangingSelection()
    {
        var list = new UiList { Bounds = new(0, 0, 100, 68) };
        list.SetItems(new string[20]);
        var surface = new PixelSurface(100, 100, new int[10000]);
        var painter = new UiPainter(new byte[4096], 8, 16);
        list.Draw(surface, painter, 0, 0, true);
        list.Handle(new(InputEventType.PointerScroll, X: 20, Y: 20, ScrollDelta: -1));
        list.Draw(surface, painter, 0, 0, true);
        Assert.AreEqual(0, list.SelectedIndex);
        list.Handle(new(InputEventType.PointerDown, X: 20, Y: 5, Button: PointerButtons.Left));
        Assert.AreEqual(3, list.SelectedIndex);
    }

    [TestMethod]
    public void TextShell_HistoryClampsClearAndKeypadDoesNotScroll()
    {
        var output = new ConsoleTextOutput();
        output.StartInteractive(40, 5);
        for (int i = 0; i < 300; i++) output.WriteLine($"row {i}");
        Assert.AreEqual(256, output.Buffer.Count);
        Assert.AreEqual("row 44", output.Buffer.GetLine(0));
        Assert.IsTrue(output.HandleScroll(new(InputEventType.PointerScroll, ScrollDelta: 1)));
        Assert.AreEqual(3, output.ScrollOffset);
        Assert.IsFalse(output.HandleScroll(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageDown, IsTextKey: true)));
        Assert.AreEqual(3, output.ScrollOffset);
        output.HandleScroll(new(InputEventType.PointerScroll, ScrollDelta: int.MaxValue));
        Assert.AreEqual(251, output.ScrollOffset);
        output.HandleScroll(new(InputEventType.PointerScroll, ScrollDelta: int.MinValue));
        Assert.AreEqual(0, output.ScrollOffset);
        output.HandleScroll(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageUp));
        Assert.AreEqual(5, output.ScrollOffset);
        output.Clear();
        Assert.AreEqual(0, output.Buffer.Count);
        Assert.AreEqual(0, output.ScrollOffset);
    }

    [TestMethod]
    public void TextShell_ResolutionChangeReflowsHistoryAndUpdatesScrollLimit()
    {
        var output = new ConsoleTextOutput();
        output.StartInteractive(40, 5);
        for (int i = 0; i < 10; i++) output.WriteLine("12345678901234567890");
        output.HandleScroll(new(InputEventType.PointerScroll, ScrollDelta: int.MaxValue));
        Assert.AreEqual(5, output.ScrollOffset);
        output.StartInteractive(10, 3);
        Assert.AreEqual(20, output.Buffer.Count);
        output.HandleScroll(new(InputEventType.PointerScroll, ScrollDelta: int.MaxValue));
        Assert.AreEqual(17, output.ScrollOffset);
        output.FollowOutput();
        Assert.AreEqual(0, output.ScrollOffset);
    }
}
