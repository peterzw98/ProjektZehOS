using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class LineEditorTests
{
    [TestMethod]
    public void Editor_InsertMoveDeleteAndSubmit_ProducesEditedCommand()
    {
        var editor = new LineEditor();
        Type(editor, "echo ac");
        Key(editor, PhysicalKey.Left);
        Type(editor, "b");
        Key(editor, PhysicalKey.Home);
        Key(editor, PhysicalKey.Delete);
        Key(editor, PhysicalKey.End);
        Key(editor, PhysicalKey.Backspace);
        editor.Handle(new(InputEventType.KeyDown, (byte)PhysicalKey.Enter), out var command);
        Assert.AreEqual("cho ab", command);
        Assert.AreEqual("", editor.Text);
        Assert.AreEqual(0, editor.Cursor);
    }

    [TestMethod]
    public void Editor_History_RestoresUnsubmittedDraftAndCursor()
    {
        var editor = new LineEditor();
        Type(editor, "echo one");
        Key(editor, PhysicalKey.Enter);
        Type(editor, "draft");
        Key(editor, PhysicalKey.Left);
        Key(editor, PhysicalKey.Up);
        Assert.AreEqual("echo one", editor.Text);
        Key(editor, PhysicalKey.Down);
        Assert.AreEqual("draft", editor.Text);
        Assert.AreEqual(4, editor.Cursor);
    }

    [TestMethod]
    public void Editor_HistoryLimit_KeepsMostRecentCommands()
    {
        var editor = new LineEditor();
        for (var i = 0; i < LineEditor.HistoryCapacity + 2; i++)
        {
            Type(editor, $"echo {i}");
            Key(editor, PhysicalKey.Enter);
        }
        for (var i = 0; i < LineEditor.HistoryCapacity + 3; i++) Key(editor, PhysicalKey.Up);
        Assert.AreEqual("echo 2", editor.Text);
    }

    [TestMethod]
    public void Editor_KeyReleaseAndControlText_DoNotChangeLine()
    {
        var editor = new LineEditor();
        Type(editor, "abc");
        editor.Handle(new(InputEventType.KeyUp, (byte)PhysicalKey.Backspace), out _);
        Type(editor, "\n\t\0");
        Assert.AreEqual("abc", editor.Text);
        Key(editor, PhysicalKey.Home);
        Key(editor, PhysicalKey.Left);
        Key(editor, PhysicalKey.Backspace);
        Assert.AreEqual(0, editor.Cursor);
    }

    [TestMethod]
    public void Editor_LongInput_IsBoundedAndCanStillSubmit()
    {
        var editor = new LineEditor();
        for (var i = 0; i < LineEditor.MaximumLength + 1; i++) Type(editor, "a");
        editor.Handle(new(InputEventType.KeyDown, (byte)PhysicalKey.Enter), out var command);
        Assert.AreEqual(LineEditor.MaximumLength, command!.Length);
    }

    [DataTestMethod]
    [DataRow((byte)72, '8', false, "draft8")]
    [DataRow((byte)83, '.', true, ".draft")]
    public void Editor_PrintableKeypad_DoesNotAlsoNavigateOrDelete(byte code, char character, bool home, string expected)
    {
        var editor = new LineEditor();
        Type(editor, "echo old");
        Key(editor, PhysicalKey.Enter);
        Type(editor, "draft");
        if (home) Key(editor, PhysicalKey.Home);
        var input = new InputService(100, 80);
        input.ReportKey(code, false, character);
        while (input.TryRead(out var value)) editor.Handle(value, out _);
        Assert.AreEqual(expected, editor.Text);
    }

    private static void Type(LineEditor editor, string text)
    {
        foreach (char character in text) editor.Handle(new(InputEventType.Text, Character: character), out _);
    }

    private static void Key(LineEditor editor, PhysicalKey key) =>
        editor.Handle(new(InputEventType.KeyDown, (byte)key), out _);
}
