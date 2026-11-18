using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[DoNotParallelize]
[TestClass]
public sealed class TerminalCompletionTests
{
    private string directory = "";

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), "zehos-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(directory, "alpine.txt"), "b");
    }

    [TestCleanup]
    public void Remove() => Directory.Delete(directory, true);

    [TestMethod]
    public void Complete_ExtendsCommonPrefix_ThenListsAlternatives()
    {
        var shell = new ShellService(files: new FileService(directory));
        var alternatives = new List<string>();
        Assert.AreEqual("cat alp", shell.Complete("cat al", 6, alternatives));
        Assert.AreEqual(0, alternatives.Count, "Solange ergaenzt werden kann, wird nichts aufgelistet.");
        Assert.AreEqual("cat alp", shell.Complete("cat alp", 7, alternatives));
        CollectionAssert.AreEqual(new[] { "alpha.txt", "alpine.txt" }, alternatives);
    }

    [TestMethod]
    public void TerminalTab_PrintsAlternativesBelowInput()
    {
        var terminal = new TerminalApp(new ShellService(files: new FileService(directory)), new byte[4096], 8, 16, 80);
        terminal.Start();
        foreach (char c in "cat alp") terminal.Handle(new(InputEventType.Text, Character: c));
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        Assert.AreEqual("cat alp", terminal.Editor.Text);
        Assert.AreEqual("zehos> cat alp", terminal.Buffer.GetLine(terminal.Buffer.Count - 2));
        Assert.AreEqual("alpha.txt   alpine.txt", terminal.Buffer.GetLine(terminal.Buffer.Count - 1));
    }

    [TestMethod]
    public void FormatAlternatives_UsesColumnsAndLimitsLongLists()
    {
        var names = new List<string>();
        for (int index = 0; index < 100; index++) names.Add("n" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        var rows = ShellService.FormatAlternatives(names, 20);
        Assert.AreEqual("n00  n01  n02  n03", rows[0]);
        Assert.AreEqual("... und 4 weitere", rows[^1]);
    }

    [TestMethod]
    public void Suggestion_FromHistory_AcceptedWithRightArrowAtLineEnd()
    {
        var editor = new LineEditor { Suggest = true };
        foreach (char c in "ls /data") editor.Handle(new(InputEventType.Text, Character: c), out _);
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter), out _);
        editor.Handle(new(InputEventType.Text, Character: 'l'), out _);
        Assert.AreEqual("ls /data", editor.Suggestion);
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Right), out _);
        Assert.AreEqual("ls /data", editor.Text);
        Assert.IsNull(editor.Suggestion);
        editor.IsSecret = true;
        editor.SetText("l");
        Assert.IsNull(editor.Suggestion, "Kennworteingaben zeigen keine Vorschlaege.");
        Assert.IsNull(new LineEditor().Suggestion, "Ohne Suggest bleibt die Textshell unveraendert.");
    }
}
