using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.TextEditor;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

/// <summary>Issue #201: Nicht-UTF-8-Dateien duerfen weder Editor noch >> stillschweigend umkodieren (U+FFFD).</summary>
[TestClass]
public sealed class NonUtf8FileTests
{
    // "Grüße\n" in Latin-1: das Byte 0xFC ist kein gueltiges UTF-8.
    private static readonly byte[] Latin1 = [0x47, 0x72, 0xFC, 0xDF, 0x65, 0x0A];
    private readonly UiPainter painter = new(new byte[4096], 8, 16);
    private string directory = "";
    private string file = "";

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "latin1.txt");
        File.WriteAllBytes(file, Latin1);
    }

    [TestCleanup]
    public void Remove() => Directory.Delete(directory, true);

    [TestMethod]
    public void ReadTextRejectsInvalidUtf8WithAClearCause()
    {
        var read = new FileService(directory).ReadText(file);
        Assert.IsFalse(read.IsOK());
        StringAssert.Contains(read.Status.Message, "NotUtf8");
        CollectionAssert.AreEqual(Latin1, File.ReadAllBytes(file));
    }

    [TestMethod]
    public void AppendRedirectLeavesTheFileUntouched()
    {
        var files = new FileService(directory);
        var shell = new ShellService(files: files);
        Assert.IsFalse(shell.ExecuteLine("echo x >> latin1.txt", new Output()).IsOK());
        CollectionAssert.AreEqual(Latin1, File.ReadAllBytes(file));
    }

    [TestMethod]
    public void EditorDoesNotOverwriteAFileItCouldNotLoad()
    {
        var editor = new TextEditorApp(new FileService(directory), painter, file);
        editor.Start();
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        CollectionAssert.AreEqual(Latin1, File.ReadAllBytes(file), "Speichern nach fehlgeschlagenem Laden darf die Datei nicht leeren.");
    }

    [TestMethod]
    public void EditorAlsoProtectsFilesThatAreTooLargeToLoad()
    {
        // Gleiches Muster wie bei Nicht-UTF-8: Laden scheitert, die Datei existiert, Speichern darf sie nicht leeren.
        File.WriteAllText(file, new string('a', FileService.MaximumTextLength + 1));
        var editor = new TextEditorApp(new FileService(directory), painter, file);
        editor.Start();
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        Assert.AreEqual(FileService.MaximumTextLength + 1, new FileInfo(file).Length);
    }

    [TestMethod]
    public void Utf8WithBomKeepsItsBytesThroughReadAndWrite()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, 0x47, 0x72, 0xC3, 0xBC, 0xC3, 0x9F, 0x65, 0x0A];
        File.WriteAllBytes(file, withBom);
        var files = new FileService(directory);
        var read = files.ReadText(file);
        Assert.IsTrue(read.IsOK(), read.ToString());
        Assert.IsTrue(files.WriteText(file, read.Value).IsOK());
        CollectionAssert.AreEqual(withBom, File.ReadAllBytes(file));
    }

    [TestMethod]
    public void Utf16ByteOrderMarkIsNotSilentlyConvertedToUtf8()
    {
        File.WriteAllBytes(file, [0xFF, 0xFE, 0x41, 0x00]);
        Assert.IsFalse(new FileService(directory).ReadText(file).IsOK());
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
