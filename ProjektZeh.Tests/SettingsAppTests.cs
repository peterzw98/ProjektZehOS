using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Settings;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[DoNotParallelize]
[TestClass]
public sealed class SettingsAppTests
{
    private static readonly DisplayMode Boot = new(1024, 768);
    private static readonly DisplayMode FullHd = new(1920, 1080);
    private string root = "";
    private DisplayMode actual;
    private int switches;

    [TestInitialize]
    public void Create() { root = Path.Combine(Path.GetTempPath(), "zehos-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup]
    public void Remove() { Directory.Delete(root, true); UiPainter.SetTheme(true, 0); }

    private string SettingsFile => Path.Combine(root, "zehos-desktop.conf");

    private (SettingsApp App, DisplaySettings Display) CreateApp()
    {
        actual = Boot;
        var display = new DisplaySettings(Boot, [Boot, FullHd], mode => { switches++; actual = mode; return ResultErr.CreateSuccess(); });
        var app = new SettingsApp(new UiPainter(new byte[4096], 8, 16), new DesktopPreferences(new FileService(root)), display, () => 1000);
        return (app, display);
    }

    private static void Press(SettingsApp app, PhysicalKey key) => app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)key));

    private static void PressShiftTab(SettingsApp app)
    {
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        Press(app, PhysicalKey.Tab);
        app.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.LeftShift));
    }

    // Seitenleiste -> Aufloesungsliste, Full HD waehlen -> Uebernehmen; der Fokus bleibt auf "Uebernehmen".
    private static void SwitchToFullHd(SettingsApp app)
    {
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Down);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Enter);
    }

    [TestMethod]
    public void KeepConfirmsPendingModeAndSavesResolution()
    {
        var (app, display) = CreateApp();
        SwitchToFullHd(app);
        Assert.IsTrue(display.Pending);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Enter);
        Assert.IsFalse(display.Pending);
        Assert.AreEqual(FullHd, display.Current);
        string saved = File.ReadAllText(SettingsFile);
        StringAssert.Contains(saved, "width=1920");
        StringAssert.Contains(saved, "height=1080");
        StringAssert.Contains(saved, "displayConfirmed=True");
    }

    [TestMethod]
    public void RevertRestoresPreviousModeWhileSwitchIsPending()
    {
        var (app, display) = CreateApp();
        SwitchToFullHd(app);
        // Uebernehmen -> Beibehalten -> Zuruecknehmen.
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Enter);
        Assert.IsFalse(display.Pending);
        Assert.AreEqual(Boot, display.Current);
        Assert.AreEqual(Boot, actual);
        Assert.IsFalse(File.Exists(SettingsFile) && File.ReadAllText(SettingsFile).Contains("width=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void KeepAndRevertAreSkippedWithoutPendingSwitch()
    {
        var (app, _) = CreateApp();
        // Ohne Wartezeit fuehrt Tab von "Uebernehmen" zurueck zur Seitenleiste, nicht zu "Beibehalten".
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Down);
        Assert.AreEqual(SettingsApp.PersonalizationPage, app.Page);
    }

    [TestMethod]
    public void ModeListIgnoresInputWhileSwitchAwaitsConfirmation()
    {
        var (app, display) = CreateApp();
        SwitchToFullHd(app);
        // Rueckwaerts ueberspringt der Fokus die gesperrte Liste; Pfeil hoch trifft die Seitenleiste auf ihrer ersten Seite.
        PressShiftTab(app);
        Press(app, PhysicalKey.Up);
        Press(app, PhysicalKey.Tab);
        Press(app, PhysicalKey.Enter);
        Assert.IsFalse(display.Pending);
        // Erneutes Uebernehmen nutzt die unveraenderte Auswahl und loest keinen zweiten Wechsel aus.
        PressShiftTab(app);
        Press(app, PhysicalKey.Enter);
        Assert.AreEqual(FullHd, display.Current);
        Assert.IsFalse(display.Pending);
        Assert.AreEqual(1, switches);
    }
}
