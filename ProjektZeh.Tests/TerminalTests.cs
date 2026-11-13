using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class TerminalTests
{
    [TestMethod]
    public void Buffer_WrapsMultilineAndBlankLines_AndRetainsOnlyLatestRows()
    {
        var buffer = new TerminalBuffer(4, 3);
        buffer.WriteLine("abcdEF\r\n\nZ");
        Assert.AreEqual(3, buffer.Count);
        Assert.AreEqual("EF", buffer.GetLine(0));
        Assert.AreEqual("", buffer.GetLine(1));
        Assert.AreEqual("Z", buffer.GetLine(2));
        buffer.WriteLine("123456789");
        Assert.AreEqual("1234", buffer.GetLine(0));
        Assert.AreEqual("5678", buffer.GetLine(1));
        Assert.AreEqual("9", buffer.GetLine(2));
        buffer.Clear();
        Assert.AreEqual(0, buffer.Count);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => buffer.GetLine(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TerminalBuffer(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TerminalBuffer(4, 0));
    }

    [TestMethod]
    public void Terminal_ExecutesAndReportsErrors_AndKeepsDraftAndHistoryIndependent()
    {
        var first = CreateTerminal();
        var second = CreateTerminal();
        first.Start();
        second.Start();
        Type(first, "echo first");
        first.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual("first", first.Buffer.GetLine(first.Buffer.Count - 1));
        Type(second, "echo draft");
        first.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Up));
        Assert.AreEqual("echo first", first.Editor.Text);
        Assert.AreEqual("echo draft", second.Editor.Text);
        Type(first, "x");
        first.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.IsTrue(first.Buffer.GetLine(first.Buffer.Count - 1).Contains("firstx", StringComparison.Ordinal));
        var invalid = CreateTerminal();
        Type(invalid, "invalid");
        invalid.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.IsTrue(string.Join("", Lines(invalid.Buffer)).Contains("UnknownCommand", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Terminal_ScrollTypingClearAndClose_BoundOffsetAndReleaseBuffer()
    {
        var terminal = CreateTerminal();
        for (int i = 0; i < 300; i++) terminal.Buffer.WriteLine($"line {i}");
        terminal.Draw(new PixelSurface(400, 300, new int[120000]), 0, 0, 400, 300, true);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: 73));
        Assert.IsTrue(terminal.ScrollOffset > 0);
        for (int i = 0; i < 500; i++) terminal.Handle(new(InputEventType.KeyDown, KeyCode: 73));
        Assert.IsTrue(terminal.ScrollOffset < terminal.Buffer.Count);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: 81));
        Assert.IsTrue(terminal.ScrollOffset >= 0);
        Type(terminal, "clear");
        Assert.AreEqual(0, terminal.ScrollOffset);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(0, terminal.Buffer.Count);
        terminal.Close();
        Assert.IsTrue(terminal.IsClosed);
        Type(terminal, "ignored");
        Assert.AreEqual("", terminal.Editor.Text);
        Assert.AreEqual(0, terminal.Buffer.Count);
    }

    [TestMethod]
    public void Terminal_SeparateShells_KeepWorkingDirectoriesIndependent()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(root, "child"));
        try
        {
            var files1 = new FileService(root);
            var files2 = new FileService(root);
            var first = CreateTerminal(new ShellService(new FileShellCommands(files1).Commands));
            var second = CreateTerminal(new ShellService(new FileShellCommands(files2).Commands));
            Type(first, "cd child");
            first.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
            Type(second, "pwd");
            second.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
            Assert.AreEqual(Path.Combine(root, "child"), files1.CurrentDirectory);
            Assert.AreEqual(root, files2.CurrentDirectory);
        }
        finally { Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(73, '9')]
    [DataRow(81, '3')]
    public void Terminal_NumericKeypad_DoesNotScrollInsteadOfTyping(int key, char character)
    {
        var terminal = CreateTerminal();
        for (int i = 0; i < 20; i++) terminal.Buffer.WriteLine("row");
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: 73));
        int previousOffset = terminal.ScrollOffset;
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)key, IsTextKey: true));
        Assert.AreEqual(previousOffset, terminal.ScrollOffset);
        terminal.Handle(new(InputEventType.Text, Character: character));
        Assert.AreEqual(character.ToString(), terminal.Editor.Text);
        Assert.AreEqual(0, terminal.ScrollOffset);
    }

    [TestMethod]
    public void Terminal_ScrolledOutput_DoesNotOverwriteTextWithScrollIndicator()
    {
        var font = new byte[4096];
        Array.Fill(font, (byte)0xff);
        var terminal = new TerminalApp(new ShellService(), font, 8, 16, 48);
        var pixels = new int[400 * 300];
        var surface = new PixelSurface(400, 300, pixels);
        for (int i = 0; i < 20; i++) terminal.Buffer.WriteLine(new string('A', 48));
        terminal.Draw(surface, 0, 0, 400, 300, true);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.PageUp));
        terminal.Draw(surface, 0, 0, 400, 300, true);
        Assert.IsTrue(terminal.ScrollOffset > 0);
        Assert.AreEqual(unchecked((int)0xffedf4fc), pixels[8 * 400 + 344]);
    }

    private static TerminalApp CreateTerminal(ShellService? shell = null) => new(shell ?? new ShellService(), new byte[4096], 8, 16, 48);
    internal static void Type(TerminalApp terminal, string text)
    {
        foreach (char character in text) terminal.Handle(new(InputEventType.Text, Character: character));
    }
    private static string[] Lines(TerminalBuffer buffer)
    {
        var lines = new string[buffer.Count];
        for (int i = 0; i < lines.Length; i++) lines[i] = buffer.GetLine(i);
        return lines;
    }
}
