using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UiTests
{
    [TestMethod]
    public void Panel_TabShiftTabAndDisabledControls_KeepOneFocus()
    {
        var panel = new UiPanel();
        var first = new UiTextBox(20);
        var disabled = new UiButton("Aus", () => Assert.Fail()) { Enabled = false };
        var last = new UiTextBox(20);
        panel.Add(first); panel.Add(disabled); panel.Add(last);
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        Assert.AreSame(last, panel.Focused);
        panel.Handle(new(InputEventType.Text, Character: 'z'));
        Assert.AreEqual("z", last.Text);
        Assert.AreEqual("", first.Text);
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        Assert.AreSame(first, panel.Focused);
        Assert.IsFalse(last.Focused);
        panel.Handle(new(InputEventType.Reset));
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        Assert.AreSame(last, panel.Focused);
    }

    [TestMethod]
    public void Button_CaptureReleaseOutsideAndReset_DoNotActivate()
    {
        int activated = 0;
        var button = new UiButton("Los", () => activated++) { Bounds = new(10, 10, 80, 40) };
        var panel = new UiPanel(); panel.Add(button);
        panel.Handle(new(InputEventType.PointerDown, X: 20, Y: 20, Button: PointerButtons.Left));
        panel.Handle(new(InputEventType.PointerUp, X: 200, Y: 20, Button: PointerButtons.Left));
        Assert.AreEqual(0, activated);
        panel.Handle(new(InputEventType.PointerDown, X: 20, Y: 20, Button: PointerButtons.Left));
        panel.Handle(new(InputEventType.Reset));
        panel.Handle(new(InputEventType.PointerUp, X: 20, Y: 20, Button: PointerButtons.Left));
        Assert.AreEqual(0, activated);
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter, IsRepeat: true));
        Assert.AreEqual(1, activated);
    }

    [TestMethod]
    public void TextBuffer_MultilineEditingAndLimit_PreserveCursorAndBoundaries()
    {
        var buffer = new TextBuffer(8, true);
        buffer.SetText("ab\nxyz");
        buffer.MoveTo(2);
        buffer.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Down));
        Assert.AreEqual(5, buffer.Cursor);
        buffer.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Up));
        Assert.AreEqual(2, buffer.Cursor);
        buffer.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        buffer.Handle(new(InputEventType.Text, Character: 'c'));
        Assert.AreEqual("ab\nc\nxyz", buffer.Text);
        Assert.IsFalse(buffer.Handle(new(InputEventType.Text, Character: 'd')));
        buffer.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Backspace));
        Assert.AreEqual("ab\n\nxyz", buffer.Text);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => buffer.SetText("123456789"));
        Assert.AreEqual("ab\n\nxyz", buffer.Text);
    }

    [TestMethod]
    public void TextBox_SelectAllReplaceAndNumLockText_DoNotApplyNavigationTwice()
    {
        var panel = new UiPanel(); var box = new UiTextBox(20); panel.Add(box);
        box.SetText("alt");
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        panel.Handle(new(InputEventType.KeyDown, KeyCode: 30));
        panel.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Control));
        panel.Handle(new(InputEventType.Text, Character: '9'));
        panel.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Home, IsTextKey: true));
        panel.Handle(new(InputEventType.Text, Character: '7'));
        Assert.AreEqual("97", box.Text);
        Assert.AreEqual(2, box.Cursor);
    }

    [TestMethod]
    public void TextView_ReflowScrollAndReadOnly_PreserveTextAndClip()
    {
        var box = new UiTextBox(1000, true, true) { Bounds = new(4, 4, 60, 52) };
        box.SetText(string.Join('\n', new string[40]).Replace("\n", "abcdefgh\n", StringComparison.Ordinal));
        var painter = new UiPainter(new byte[4096], 8, 16);
        int[] pixels = new int[100 * 100];
        var surface = new PixelSurface(100, 100, pixels);
        string before = box.Text;
        box.Draw(surface, painter, 0, 0, true);
        box.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageDown));
        box.Handle(new(InputEventType.Text, Character: 'x'));
        box.Draw(surface, painter, 0, 0, true);
        Assert.IsTrue(box.FirstRow > 0);
        Assert.AreEqual(before, box.Text);
        Assert.AreEqual(0, pixels[3 * 100 + 4]);
        box.SetText("kurz");
        box.Handle(new(InputEventType.PointerDown, X: 10, Y: 10, Button: PointerButtons.Left));
        box.Draw(surface, painter, 0, 0, true);
        Assert.AreEqual(0, box.FirstRow);
    }

    [TestMethod]
    public void TextBox_SelectionNavigationAndNestedClip_PreserveTextAndParentClip()
    {
        var box = new UiTextBox(20) { Bounds = new(0, 0, 4, 4) };
        box.SetText("alt"); box.SelectAll();
        box.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Left));
        box.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.AreEqual("xalt", box.Text);
        int[] pixels = new int[100];
        var surface = new PixelSurface(10, 10, pixels);
        surface.SetClip(2, 2, 4, 4);
        box.Draw(surface, new UiPainter(new byte[4096], 8, 16), 0, 0, true);
        Assert.AreEqual((2, 2, 4, 4), surface.Clip);
        Assert.AreEqual(0, pixels[1 * 10 + 2]);
        Assert.AreEqual(0, pixels[4 * 10 + 2]);
    }

    [TestMethod]
    public void TextBox_VerticalNavigation_FollowsVisibleWrappedRows()
    {
        var box = new UiTextBox(40, true) { Bounds = new(0, 0, 60, 80) };
        box.SetText("abcdefghij");
        box.Draw(new PixelSurface(60, 80, new int[4800]), new UiPainter(new byte[4096], 8, 16), 0, 0, true);
        for (int index = 0; index < 7; index++) box.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Right));
        box.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Up));
        Assert.AreEqual(1, box.Cursor);
        box.Handle(new(InputEventType.Text, Character: 'X'));
        box.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Down));
        box.Handle(new(InputEventType.Text, Character: 'Y'));
        Assert.AreEqual("aXbcdefgYhij", box.Text);
    }

    [TestMethod]
    public void List_EmptyAndScrolledSelection_ActivateExactlyOneEntry()
    {
        var list = new UiList { Bounds = new(0, 0, 120, 60) };
        int activated = 0; list.Activated = () => activated++;
        list.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(0, activated);
        list.SetItems(["a", "b", "c", "d"]);
        list.Draw(new PixelSurface(120, 60, new int[7200]), new UiPainter(new byte[4096], 8, 16), 0, 0, true);
        list.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.End));
        list.Draw(new PixelSurface(120, 60, new int[7200]), new UiPainter(new byte[4096], 8, 16), 0, 0, true);
        list.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(3, list.SelectedIndex);
        Assert.AreEqual(1, activated);
    }
}
