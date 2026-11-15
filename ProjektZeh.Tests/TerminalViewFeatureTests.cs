using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class TerminalViewFeatureTests
{
    [TestMethod]
    public void SelectionCopiesAcrossViewsAndPasteNeverExecutesNewlines()
    {
        var first = Create();
        var second = Create();
        first.Buffer.WriteLine("echo one\necho two");
        first.SelectAll();
        Assert.AreEqual("echo one\necho two", first.CopySelection());
        second.PasteClipboard();
        Assert.AreEqual("echo one echo two", second.Editor.Text);
        Assert.AreEqual(0, second.Buffer.Count);
    }

    [TestMethod]
    public void FindCyclesMatchesBothDirectionsAndScrollsToResult()
    {
        var terminal = Create();
        terminal.Resize(400, 100);
        terminal.Buffer.WriteLine("needle\nother\nneedle\nlast\nlast\nlast");
        Assert.IsTrue(terminal.Find("needle"));
        Assert.AreEqual("needle", terminal.CopySelection());
        int first = terminal.ScrollOffset;
        Assert.IsTrue(terminal.Find("needle"));
        Assert.IsTrue(terminal.ScrollOffset < first);
        Assert.IsTrue(terminal.Find("needle", true));
        Assert.AreEqual(first, terminal.ScrollOffset);
        Assert.IsFalse(terminal.Find("absent"));
    }

    [TestMethod]
    public void MouseSelectionUsesContentLocalCoordinatesAndResetStopsDrag()
    {
        var terminal = Create();
        terminal.Buffer.WriteLine("abcdef");
        terminal.Draw(new PixelSurface(500, 300, new int[150000]), 40, 30, 400, 200, true);
        terminal.Handle(new(InputEventType.PointerDown, X: 8, Y: 8, Button: PointerButtons.Left));
        terminal.Handle(new(InputEventType.PointerMove, X: 32, Y: 8));
        terminal.Handle(new(InputEventType.PointerUp, X: 32, Y: 8, Button: PointerButtons.Left));
        Assert.AreEqual("abc", terminal.CopySelection());
        terminal.Handle(new(InputEventType.Reset));
        terminal.Handle(new(InputEventType.PointerMove, X: 48, Y: 8));
        Assert.AreEqual("", terminal.CopySelection());
        Assert.AreEqual("abcdef", terminal.Buffer.GetLine(0));
    }

    [TestMethod]
    public void ZoomReflowsAndDrawingHonorsPaneClipAndColors()
    {
        var font = new byte[4096];
        font['A' * 16] = 0x80;
        var terminal = new TerminalApp(new ShellService(), font, 8, 16, 48);
        terminal.Resize(160, 100);
        terminal.Buffer.WriteLine(new string('A', 18));
        terminal.SetZoom(2);
        Assert.AreEqual(2, terminal.Zoom);
        Assert.AreEqual(2, terminal.Buffer.Count);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => terminal.SetZoom(4));
        terminal.SetColors(123, 456, 789);
        var pixels = new int[30000];
        terminal.Draw(new PixelSurface(200, 150, pixels), 20, 20, 160, 100, true);
        Assert.AreEqual(0, pixels[19 * 200 + 20]);
        Assert.AreEqual(123, pixels[20 * 200 + 20]);
        Assert.AreEqual(456, pixels[28 * 200 + 28]);
        Assert.AreEqual(456, pixels[29 * 200 + 29]);
        Assert.AreEqual(123, pixels[28 * 200 + 30]);
        Assert.AreEqual(0, pixels[120 * 200 + 20]);
    }

    [TestMethod]
    public void ScrollbarPagesAndDragsAndResetReleasesModifiers()
    {
        var terminal = Create();
        terminal.Resize(400, 100);
        for (int i = 0; i < 40; i++) terminal.Buffer.WriteLine("row " + i);
        terminal.Handle(new(InputEventType.PointerDown, X: 396, Y: 8, Button: PointerButtons.Left));
        Assert.IsTrue(terminal.ScrollOffset > 0);
        terminal.Handle(new(InputEventType.PointerDown, X: 396, Y: 60, Button: PointerButtons.Left));
        terminal.Handle(new(InputEventType.PointerMove, X: 396, Y: 8));
        Assert.AreEqual(36, terminal.ScrollOffset);
        terminal.Handle(new(InputEventType.PointerUp, X: 396, Y: 8, Button: PointerButtons.Left));
        terminal.Handle(new(InputEventType.PointerMove, X: 396, Y: 80));
        Assert.AreEqual(36, terminal.ScrollOffset);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        terminal.Handle(new(InputEventType.Reset));
        terminal.Handle(new(InputEventType.Text, Character: 'l'));
        Assert.AreEqual("l", terminal.Editor.Text);
        Assert.AreEqual(40, terminal.Buffer.Count);
    }

    [TestMethod]
    public void SecretInputRejectsClipboardAndEvictedSelectionCannotCopyDifferentText()
    {
        var terminal = Create();
        terminal.Buffer.WriteLine("public");
        terminal.SelectAll();
        TerminalClipboard.Text = "safe";
        terminal.Shell.BeginSecretInput(_ => () => () => ProjektZeh.Shared.ResultErr.CreateSuccess());
        Assert.AreEqual("", terminal.CopySelection());
        terminal.PasteClipboard();
        Assert.AreEqual("", terminal.Editor.Text);
        Assert.AreEqual("safe", TerminalClipboard.Text);
        terminal.Shell.CancelSecretInput();
        for (int i = 0; i < 260; i++) terminal.Buffer.WriteLine("different");
        Assert.AreEqual("", terminal.CopySelection());
        Assert.AreEqual("safe", TerminalClipboard.Text);
    }

    [TestMethod]
    public void CopySelectionPreservesLogicalLinesAcrossDisplayWrapping()
    {
        var terminal = Create();
        terminal.Resize(80, 200);
        terminal.Buffer.WriteLine("abcdefghijk\nnext");
        terminal.SelectAll();
        Assert.AreEqual("abcdefghijk\nnext", terminal.CopySelection());
    }

    [TestMethod]
    public void PagerKeepsFirstAndLastOfMoreThanScrollbackCapacityAndDismissRestoresDraft()
    {
        var terminal = Create();
        terminal.Editor.SetText("draft");
        var lines = new string[300];
        for (int i = 0; i < lines.Length; i++) lines[i] = "page" + i;
        terminal.Shell.ExecuteLine("echo \"" + string.Join("\n", lines) + "\" | less", terminal.Buffer);
        terminal.Update(0);

        Assert.IsTrue(terminal.Find("page0"));
        Assert.IsTrue(terminal.Find("page299"));
        terminal.Handle(new(InputEventType.Text, Character: 'q'));
        Assert.IsNull(terminal.Shell.PagerText);
        Assert.AreEqual("draft", terminal.Editor.Text);
    }

    [TestMethod]
    public void IdentityChangeAndCloseReleasePreviousDraftHistoryOutputAndClipboard()
    {
        int generation = 0;
        var terminal = new TerminalApp(new ShellService(getSessionGeneration: () => generation), new byte[4096], 8, 16, 48);
        TerminalTests.Type(terminal, "echo previous");
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        TerminalTests.Type(terminal, "private draft");
        TerminalClipboard.Text = "previous user";
        generation++;
        terminal.Update(0);
        Assert.AreEqual("", terminal.Editor.Text);
        Assert.AreEqual(0, terminal.Buffer.Count);
        Assert.AreEqual("", TerminalClipboard.Text);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Up));
        Assert.AreEqual("", terminal.Editor.Text);
        TerminalTests.Type(terminal, "closing draft");
        terminal.Close();
        Assert.AreEqual("", terminal.Editor.Text);
    }

    [TestMethod]
    public void FindMatchesAcrossSoftWrapAndCopiesOnlyMatchedText()
    {
        var terminal = new TerminalApp(new ShellService(), new byte[4096], 8, 16, 4);
        terminal.Buffer.WriteLine("abcdefgh");
        Assert.IsTrue(terminal.Find("defg"));
        Assert.AreEqual("defg", terminal.CopySelection());
        Assert.IsTrue(terminal.Find("abcd"));
        Assert.AreEqual("abcd", terminal.CopySelection());
        Assert.IsTrue(terminal.Find("cdef", true));
        Assert.AreEqual("cdef", terminal.CopySelection());
    }

    [TestMethod]
    public void CloseReleasesShellOnlyOnce()
    {
        var terminal = Create();
        int closed = 0;
        terminal.Shell.OnClose = () => closed++;
        terminal.Close();
        terminal.Close();
        Assert.AreEqual(1, closed);
    }

    [TestMethod]
    public void AliasToAccountCommandCannotEnterEditorHistory()
    {
        var terminal = Create();
        terminal.Shell.ExecuteLine("alias hidden=user", terminal.Buffer);
        TerminalTests.Type(terminal, "hidden add alice admin accidental-secret");
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Up));
        Assert.AreEqual("", terminal.Editor.Text);
    }

    [TestMethod]
    public void PagerSpaceFromInputServicePagesOncePerTextPair()
    {
        var terminal = Create();
        terminal.Resize(400, 100);
        var lines = new string[20];
        for (int index = 0; index < lines.Length; index++) lines[index] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(terminal.Shell.ExecuteLine("echo \"" + string.Join("\n", lines) + "\" | less", terminal.Buffer).IsOK());
        terminal.Update(0);
        var input = new InputService(400, 100);
        input.ReportKey((byte)PhysicalKey.Space, false, ' ');
        while (input.TryRead(out var value)) terminal.Handle(value);
        terminal.Handle(new(InputEventType.PointerDown, X: 8, Y: 8, Button: PointerButtons.Left));
        terminal.Handle(new(InputEventType.PointerUp, X: 24, Y: 8, Button: PointerButtons.Left));
        Assert.AreEqual("4", terminal.CopySelection());
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Space));
        terminal.Handle(new(InputEventType.Text, Character: ' '));
        terminal.Handle(new(InputEventType.PointerDown, X: 8, Y: 8, Button: PointerButtons.Left));
        terminal.Handle(new(InputEventType.PointerUp, X: 24, Y: 8, Button: PointerButtons.Left));
        Assert.AreEqual("8", terminal.CopySelection());
        terminal.Handle(new(InputEventType.Text, Character: 'q'));
        Assert.IsNull(terminal.Shell.PagerText);
    }

    private static TerminalApp Create() => new(new ShellService(), new byte[4096], 8, 16, 48);
}
