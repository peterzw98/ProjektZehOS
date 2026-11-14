using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Settings;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Apps.TextEditor;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopRedesignTests
{
    private string directory = "";

    [TestInitialize]
    public void Create()
    {
        directory = Path.Combine(Path.GetTempPath(), "zehos-redesign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Remove()
    {
        Directory.Delete(directory, true);
        UiPainter.SetTheme(true, 0);
    }

    private Desktop CreateDesktop(int width = 1920, int height = 1080, DisplaySettings? display = null, TaskManagerSection[]? sections = null) =>
        new(width, height, new byte[4096], 8, 16, () => new ShellService(), () => new FileService(directory), display, sections);

    private static void Click(Desktop desktop, int x, int y)
    {
        desktop.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        desktop.Handle(new(InputEventType.PointerUp, X: x, Y: y, Button: PointerButtons.Left));
    }

    [TestMethod]
    public void MenuDoubleClick_StartsSelectedOnce_AndPinnedStarterOpensSettingsOnlyOnce()
    {
        var desktop = CreateDesktop();
        desktop.Update(1000);
        Click(desktop, 40, 1060);
        var surface = new PixelSurface(1920, 1080, new int[1920 * 1080]);
        desktop.Render(surface, 1000, 0, 0, PointerButtons.None);
        // Zweiter Eintrag (Dateien): erst markieren, dann innerhalb der Doppelklickfrist erneut klicken.
        int rowY = 1080 - Desktop.BarHeight - 8 - 620 + 16 + 44 + 12 + 4 + 28 + 14;
        Click(desktop, 100, rowY);
        Assert.AreEqual(0, desktop.Windows.Count);
        desktop.Update(1200);
        Click(desktop, 100, rowY);
        Assert.AreEqual(1, desktop.Windows.Count);
        Assert.AreEqual("Dateien", desktop.Windows.ActiveWindow!.Title);

        Click(desktop, 152 + 2 * 48 + 20, 1060);
        Click(desktop, 152 + 2 * 48 + 20, 1060);
        Assert.AreEqual(2, desktop.Windows.Count);
        Assert.IsInstanceOfType<SettingsApp>(desktop.Windows.ActiveWindow!.Application);
    }

    [TestMethod]
    public void Settings_ThemeAndWallpaper_ApplyImmediatelyAndPersist()
    {
        var desktop = CreateDesktop();
        Assert.IsTrue(desktop.OpenSettings());
        var settings = (SettingsApp)desktop.Windows.ActiveWindow!.Application;
        settings.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Down));
        Assert.AreEqual(SettingsApp.PersonalizationPage, settings.Page);
        var surface = new PixelSurface(1920, 1080, new int[1920 * 1080]);
        desktop.Render(surface, 0, 0, 0, PointerButtons.None);
        int before = UiPainter.Background;
        // Schaltflaeche "Hell" liegt rechts der Seitenleiste unter Titel und Abschnittsueberschrift.
        var window = desktop.Windows.ActiveWindow!;
        int lightX = window.ContentX + 320 + 24 + 20, lightY = window.ContentY + 20 + 16 + 24 + 24 + 10;
        Click(desktop, lightX, lightY);
        Assert.IsFalse(desktop.Preferences.Settings.Dark);
        Assert.AreNotEqual(before, UiPainter.Background);
        Assert.IsFalse(desktop.Preferences.StatusIsError, desktop.Preferences.Status);
        Assert.IsFalse(new DesktopPreferences(new FileService(directory)).Settings.Dark);
    }

    [TestMethod]
    public void PointerOnlyMove_RestoresSceneUnderOldPointer_WithoutRedrawingScene()
    {
        var desktop = CreateDesktop(1024, 768);
        var pixels = new int[1024 * 768];
        var surface = new PixelSurface(1024, 768, pixels);
        Assert.IsNull(desktop.Render(surface, 0, 2000, 2000, PointerButtons.None), "Das erste Bild ist immer vollstaendig.");
        var clean = (int[])pixels.Clone();
        desktop.Handle(new(InputEventType.PointerMove, X: 400, Y: 300));
        Assert.AreEqual((2000, 2000), desktop.Render(surface, 10, 400, 300, PointerButtons.None));
        Assert.AreEqual(-1, pixels[300 * 1024 + 400]);
        // Fremde Pixel ausserhalb des Zeigers bleiben unberuehrt, weil die Szene nicht neu gezeichnet wird.
        pixels[10 * 1024 + 10] = 12345;
        desktop.Handle(new(InputEventType.PointerMove, X: 600, Y: 500));
        Assert.AreEqual((400, 300), desktop.Render(surface, 20, 600, 500, PointerButtons.None));
        Assert.AreEqual(12345, pixels[10 * 1024 + 10]);
        for (int row = 300; row < 300 + Desktop.PointerHeight; row++)
            for (int column = 400; column < 400 + Desktop.PointerWidth; column++)
                Assert.AreEqual(clean[row * 1024 + column], pixels[row * 1024 + column]);
        // Nur alte und neue Zeigerflaeche duerfen sich gegenueber der Szene unterscheiden; nur diese werden ausgegeben.
        for (int index = 0; index < pixels.Length; index++)
        {
            int column = index % 1024, row = index / 1024;
            bool inPointer = column >= 600 && column < 600 + Desktop.PointerWidth && row >= 500 && row < 500 + Desktop.PointerHeight;
            if (!inPointer && index != 10 * 1024 + 10) Assert.AreEqual(clean[index], pixels[index]);
        }
        desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        Assert.IsNull(desktop.Render(surface, 30, 600, 500, PointerButtons.None));
        Assert.AreEqual(clean[10 * 1024 + 10], pixels[10 * 1024 + 10]);
        Assert.IsNull(desktop.Render(new PixelSurface(1024, 768, new int[1024 * 768]), 40, 600, 500, PointerButtons.None),
            "Eine ersetzte Zeichenflaeche erfordert ein vollstaendiges Bild.");
    }

    [TestMethod]
    public void Settings_InvalidImage_KeepsPreviousWallpaperAndReportsError()
    {
        File.WriteAllText(Path.Combine(directory, "kaputt.bmp"), "kein Bild");
        var preferences = new DesktopPreferences(new FileService(directory));
        preferences.Apply(preferences.Settings with { Wallpaper = 2 });
        int revision = preferences.Revision;
        Assert.IsFalse(preferences.UseImage("kaputt.bmp").IsOK());
        Assert.AreEqual(2, preferences.Settings.Wallpaper);
        Assert.AreEqual(revision, preferences.Revision);
        Assert.IsTrue(preferences.StatusIsError);
        Assert.IsFalse(preferences.UseImage("fehlt.bmp").IsOK());
        Assert.AreEqual(2, preferences.Settings.Wallpaper);
    }

    [TestMethod]
    public void Settings_SingleDriverMode_OffersNoRuntimeSwitch()
    {
        var mode = new DisplayMode(1920, 1080);
        var display = new DisplaySettings(mode, [mode], _ => ResultErr.CreateSuccess());
        Assert.IsFalse(display.CanSwitch);
        Assert.IsTrue(display.Apply(mode, 0).IsOK());
        Assert.IsFalse(display.Pending);
        var multi = new DisplaySettings(mode, [mode, new(1280, 720)], _ => ResultErr.CreateSuccess());
        Assert.IsTrue(multi.CanSwitch);
        multi.Apply(new(1280, 720), 1000);
        Assert.AreEqual(15L, multi.SecondsLeft(1000));
        Assert.AreEqual(1L, multi.SecondsLeft(15999));
    }

    [TestMethod]
    public void TaskManager_ShowsAdditionalSections_AndCloseKeepsEditorQuestion()
    {
        int reads = 0;
        var desktop = CreateDesktop(sections: [new("Netzwerk", () => { reads++; return ["Adapter: test0"]; })]);
        Assert.IsTrue(desktop.OpenEditor());
        var editorWindow = desktop.Windows.ActiveWindow!;
        var editor = (TextEditorApp)editorWindow.Application;
        for (int tab = 0; tab < 4; tab++) desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        desktop.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.IsTrue(editor.IsDirty);
        Assert.IsTrue(desktop.OpenTaskManager());
        var manager = (TaskManagerApp)desktop.Windows.ActiveWindow!.Application;
        manager.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Down));
        manager.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Down));
        Assert.AreEqual(2, manager.Section);
        Assert.AreEqual("Adapter: test0", manager.Lines[0]);
        Assert.AreEqual(1, reads);
        Assert.IsTrue(desktop.OpenTaskManager());
        Assert.AreEqual(2, desktop.Windows.Count);

        desktop.Windows.RequestClose(editorWindow);
        Assert.AreEqual(2, desktop.Windows.Count);
        Assert.IsTrue(editor.CloseConfirmationVisible);
    }

    [TestMethod]
    public void Wallpaper_BuiltInsFillWholeBuffer_AndResizeKeepsWindows()
    {
        var pixels = new int[320 * 180];
        for (int variant = 0; variant < WallpaperImage.BuiltInNames.Length; variant++)
        {
            Array.Clear(pixels);
            WallpaperImage.Create(pixels, 320, 180, variant, variant % 2 == 0, UiPainter.Accent);
            Assert.IsFalse(Array.Exists(pixels, value => value == 0), $"Motiv {variant} laesst Pixel leer.");
        }
        Assert.ThrowsException<ArgumentException>(() => WallpaperImage.Create(new int[10], 320, 180, 0, true, 0));
        var desktop = CreateDesktop();
        desktop.OpenSettings();
        desktop.Resize(1024, 768);
        var window = desktop.Windows.ActiveWindow!;
        Assert.IsTrue(window.X + window.Width <= 1024 && window.Y + window.Height <= 768 - Desktop.BarHeight);
        var surface = new PixelSurface(1024, 768, new int[1024 * 768]);
        desktop.Render(surface, 0, 0, 0, PointerButtons.None);
    }
}
