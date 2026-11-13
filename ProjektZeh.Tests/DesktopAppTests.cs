using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Apps.Pong;
using ProjektZeh.Apps.TextEditor;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopAppTests
{
    private string directory = null!;
    private readonly UiPainter painter = new(new byte[4096], 8, 16);

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }
    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, true);

    [TestMethod]
    public void DesktopApps_UseCentralPermissionsAndLoseWriteAccessAfterLogout()
    {
        var access = new FileAccessControl(directory);
        access.Initialize();
        access.Persist = ResultErr.CreateSuccess;
        var identity = new UserSession("alice", "alice", false, directory);
        access.Assign(directory, "alice");
        string foreign = Path.Combine(directory, "bob.txt");
        File.WriteAllText(foreign, "vertraulich");
        access.Assign(foreign, "bob");
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(),
            () => new FileService(directory, access: access, session: identity));
        Assert.IsTrue(desktop.OpenFiles());
        var manager = (FileManagerApp)desktop.Windows.ActiveWindow!.Application;
        string? opened = null;
        var browser = new FileManagerApp(new FileService(directory, access: access, session: identity), painter,
            path => { opened = path; return true; });
        browser.Start(); Click(browser, 600, 80);
        Assert.IsNull(opened, "Fremde Dateien duerfen nicht in der Auswahl erscheinen.");
        Assert.IsTrue(desktop.OpenEditor());
        var editor = (TextEditorApp)desktop.Windows.ActiveWindow!.Application;
        Click(editor, 20, 130); Type(editor, "eigene Datei");
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        string target = Path.Combine(directory, "neu.txt");
        Assert.AreEqual("eigene Datei", File.ReadAllText(target));
        identity.Logout();
        Type(editor, "gesperrt");
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        Assert.IsTrue(editor.IsDirty);
        Assert.AreEqual("eigene Datei", File.ReadAllText(target));
        Assert.AreEqual("vertraulich", File.ReadAllText(foreign));
        manager.Close();
    }

    [TestMethod]
    public void Editor_NewMultilineFile_SaveReopenAndPreserveExistingLineEndings()
    {
        string path = Path.Combine(directory, "text.txt");
        var editor = new TextEditorApp(new FileService(directory), painter, path);
        editor.Start();
        Click(editor, 20, 130);
        Type(editor, "erste");
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Type(editor, "zweite");
        Assert.IsTrue(editor.IsDirty);
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        Assert.AreEqual("erste\nzweite", File.ReadAllText(path));
        Assert.IsFalse(editor.IsDirty);
        var reopened = new TextEditorApp(new FileService(directory), painter, path);
        reopened.Start();
        Type(reopened, "neu ");
        reopened.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        Assert.AreEqual("neu erste\nzweite", File.ReadAllText(path));
        File.WriteAllText(path, "a\r\nb");
        var windowsText = new TextEditorApp(new FileService(directory), painter, path);
        windowsText.Start(); Type(windowsText, "x");
        windowsText.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        Assert.AreEqual("xa\r\nb", File.ReadAllText(path));
    }

    [TestMethod]
    public void Editor_DirtyClose_CancelKeepsTextAndDiscardRemovesWindow()
    {
        var editor = new TextEditorApp(new FileService(directory), painter);
        var manager = new WindowManager(1024, 712);
        var window = new Window("Editor", 20, 20, 800, 560, editor);
        manager.Add(window);
        Click(editor, 20, 130); Type(editor, "Entwurf");
        Close(manager, window);
        Assert.AreEqual(1, manager.Count);
        Assert.IsTrue(editor.CloseConfirmationVisible);
        Click(editor, 30, 150);
        Assert.IsFalse(editor.CloseConfirmationVisible);
        Assert.IsTrue(editor.IsDirty);
        Close(manager, window);
        Click(editor, 300, 150);
        manager.Update(100);
        Assert.AreEqual(0, manager.Count);
    }

    [TestMethod]
    public void Editor_SaveAndClose_WritesDraftBeforeRemovingWindow()
    {
        string path = Path.Combine(directory, "entwurf.txt");
        var editor = new TextEditorApp(new FileService(directory), painter, path);
        var manager = new WindowManager(1024, 712);
        var window = new Window("Editor", 20, 20, 800, 560, editor);
        manager.Add(window);
        Click(editor, 20, 130); Type(editor, "bleibt erhalten"); Close(manager, window);
        Click(editor, 620, 150); manager.Update(100);
        Assert.AreEqual("bleibt erhalten", File.ReadAllText(path));
        Assert.AreEqual(0, manager.Count);
    }

    [TestMethod]
    public void Editor_SaveFailureOnClose_PreservesOriginalAndKeepsConfirmation()
    {
        string path = Path.Combine(directory, "original.txt");
        File.WriteAllText(path, "vorher");
        var editor = new TextEditorApp(new FileService(directory,
            (_, _, _) => throw new IOException("Schreibfehler")), painter, path);
        var manager = new WindowManager(1024, 712);
        var window = new Window("Editor", 20, 20, 800, 560, editor);
        manager.Add(window);
        Type(editor, "neu"); Close(manager, window);
        Click(editor, 620, 150); manager.Update(100);
        Assert.AreEqual(1, manager.Count);
        Assert.IsTrue(editor.CloseConfirmationVisible);
        Assert.IsTrue(editor.IsDirty);
        Assert.AreEqual("vorher", File.ReadAllText(path));
    }

    [TestMethod]
    public void Editor_LoadWhileDirty_DoesNotDiscardDraft()
    {
        string path = Path.Combine(directory, "text.txt"); File.WriteAllText(path, "alt");
        var editor = new TextEditorApp(new FileService(directory), painter, path);
        editor.Start(); Type(editor, "neu");
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F3));
        editor.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.F2));
        Assert.AreEqual("neualt", File.ReadAllText(path));
    }

    [TestMethod]
    public void FileManager_DirectoryMetadataAndEditorLaunch_UseSelectedAbsolutePath()
    {
        string path = Path.Combine(directory, "datei mit leerzeichen.txt");
        File.WriteAllText(path, "Vorschau");
        string? opened = null;
        var app = new FileManagerApp(new FileService(directory), painter, name => { opened = name; return true; });
        app.Start();
        Click(app, 600, 80);
        Assert.AreEqual(path, opened);
        Directory.CreateDirectory(Path.Combine(directory, "ordner"));
        var entries = new FileService(directory).ListDirectory(".");
        Assert.IsTrue(entries.IsOK());
        Assert.IsTrue(entries.Value![0].IsDirectory);
        Assert.AreEqual(Path.Combine(directory, "ordner"), entries.Value[0].Path);
        app.Close();
    }

    [TestMethod]
    public void Pong_ElapsedStepsAndInputReset_AreIndependentFromDrawRate()
    {
        var game = new PongGame(); var divided = new PongGame();
        game.Step(100, -1);
        for (int index = 0; index < 10; index++) divided.Step(10, -1);
        Assert.AreEqual(game.BallX, divided.BallX, 0.0001);
        Assert.AreEqual(game.PlayerY, divided.PlayerY, 0.0001);
        var app = new PongApp(painter);
        var surface = new PixelSurface(800, 560, new int[800 * 560]);
        app.Draw(surface, 0, 0, 800, 560, true);
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Down));
        app.Update(0); app.Update(100);
        double moved = app.Game.PlayerY;
        Assert.IsTrue(moved > (PongGame.Height - PongGame.PaddleHeight) / 2);
        app.Handle(new(InputEventType.Reset));
        app.Update(1000);
        Assert.AreEqual(moved, app.Game.PlayerY);
        app.Draw(surface, 0, 0, 800, 560, true);
        app.Update(1010); app.Update(1020);
        Assert.AreEqual(moved, app.Game.PlayerY);
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Space));
        double ball = app.Game.BallX;
        app.Update(1100);
        Assert.AreEqual(ball, app.Game.BallX);
    }

    [TestMethod]
    public void Pong_LongRun_BoundsAndScoringRemainValid()
    {
        var game = new PongGame();
        for (int frame = 0; frame < 30000; frame++)
        {
            game.Step(16, 0);
            Assert.IsTrue(game.BallY >= 0 && game.BallY <= PongGame.Height - PongGame.BallSize);
            Assert.IsTrue(game.ComputerY >= 0 && game.ComputerY <= PongGame.Height - PongGame.PaddleHeight);
        }
        Assert.IsTrue(game.ComputerScore + game.PlayerScore > 0);
        game.Step(int.MaxValue, -1);
        Assert.IsTrue(game.PlayerY >= 0);
    }

    [TestMethod]
    public void Apps_SteadyDrawing_ReusesBuffersWithoutPerFrameAllocations()
    {
        var editor = new TextEditorApp(new FileService(directory), painter);
        var pong = new PongApp(painter);
        var surface = new PixelSurface(800, 560, new int[800 * 560]);
        editor.Start();
        for (int index = 0; index < 20; index++)
        { editor.Draw(surface, 0, 0, 796, 526, true); pong.Draw(surface, 0, 0, 796, 526, true); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
        { editor.Draw(surface, 0, 0, 796, 526, true); pong.Draw(surface, 0, 0, 796, 526, true); }
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before,
            "Unveraenderte Editor-/Pong-Bilder benoetigen keine neuen Puffer oder Strings.");
    }

    [TestMethod]
    public void Window_Close_ReleasesApplicationReferences()
    {
        var manager = new WindowManager(1024, 712);
        WeakReference reference = OpenAndClose(manager, new FileService(directory), painter);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsFalse(reference.IsAlive, "Die weiterhin lebende Fensterverwaltung darf die geschlossene Anwendung nicht halten.");
        GC.KeepAlive(manager);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClose(WindowManager manager, FileService files, UiPainter painter)
    {
        var editor = new TextEditorApp(files, painter);
        var window = new Window("Editor", 20, 20, 800, 560, editor);
        manager.Add(window);
        var reference = new WeakReference(editor);
        Close(manager, window);
        return reference;
    }

    private static void Type(TextEditorApp app, string text)
    { foreach (char character in text) app.Handle(new(InputEventType.Text, Character: character)); }
    private static void Click(ProjektZeh.Apps.IApplication app, int x, int y)
    {
        app.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        app.Handle(new(InputEventType.PointerUp, X: x, Y: y, Button: PointerButtons.Left));
    }
    private static void Close(WindowManager manager, Window window) => manager.Handle(new(InputEventType.PointerDown,
        X: window.X + window.Width - 16, Y: window.Y + 16, Button: PointerButtons.Left));
}
