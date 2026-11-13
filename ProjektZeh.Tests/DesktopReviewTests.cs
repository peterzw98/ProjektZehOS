using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Settings;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopReviewTests
{
    private string root = "";
    [TestInitialize]
    public void Create() { root = Path.Combine(Path.GetTempPath(), "zehos-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup]
    public void Remove() { Directory.Delete(root, true); UiPainter.SetTheme(true, 0); }

    [TestMethod]
    public void PersonalizationDoesNotInventASavedDisplayChoice()
    {
        var preferences = new DesktopPreferences(new FileService(root));
        preferences.Apply(preferences.Settings with { Dark = false });
        string saved = File.ReadAllText(Path.Combine(root, "zehos-desktop.conf"));
        Assert.IsFalse(saved.Contains("width=", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("height=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailedRevertStopsRetryingAndReportsActualDisplay()
    {
        var start = new DisplayMode(1024, 768);
        var requested = new DisplayMode(1920, 1080);
        var actual = start;
        int attempts = 0;
        var display = new DisplaySettings(start, [start, requested], mode =>
        {
            attempts++;
            if (mode == start) { actual = new(1280, 720); return ResultErr.CreateFailure(ResultCode.Failure, "Treiberfehler"); }
            actual = mode; return ResultErr.CreateSuccess();
        }, () => actual);
        display.Apply(requested, 500);
        display.Update(15500);
        display.Update(16000);
        display.Update(17000);
        Assert.IsFalse(display.Pending);
        Assert.AreEqual(actual, display.Current);
        Assert.AreEqual(2, attempts);
        StringAssert.Contains(display.Status, "fehlgeschlagen");
    }

    [TestMethod]
    public void SavedDisplayIsAppliedWithVisibleConfirmationAndTimesOut()
    {
        File.WriteAllText(Path.Combine(root, "zehos-desktop.conf"), "version=2\ndisplayConfirmed=True\nwidth=1920\nheight=1080\n");
        var boot = new DisplayMode(1024, 768);
        var actual = boot;
        var display = new DisplaySettings(boot, [boot, new(1920, 1080)], mode => { actual = mode; return ResultErr.CreateSuccess(); });
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService(root), display);
        desktop.Update(60000);
        Assert.IsTrue(display.Pending);
        Assert.IsInstanceOfType<SettingsApp>(desktop.Windows.ActiveWindow!.Application);
        display.Update(74999);
        Assert.AreEqual(new DisplayMode(1920, 1080), actual);
        display.Update(75000);
        Assert.AreEqual(boot, actual);
    }

    [TestMethod]
    public void OldImplicitResolutionDoesNotOverrideBootMode()
    {
        File.WriteAllText(Path.Combine(root, "zehos-desktop.conf"), "version=1\ndark=False\nwidth=1920\nheight=1080\n");
        int switches = 0;
        var boot = new DisplayMode(1024, 768);
        var display = new DisplaySettings(boot, [boot, new(1920, 1080)], _ => { switches++; return ResultErr.CreateSuccess(); });
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService(root), display);
        desktop.Update(60000);
        Assert.AreEqual(0, switches);
        Assert.IsFalse(desktop.Preferences.Settings.Dark);
    }

    [TestMethod]
    public void LeavingDesktopRevertsUnconfirmedResolutionImmediately()
    {
        var boot = new DisplayMode(1024, 768);
        var actual = boot;
        var display = new DisplaySettings(boot, [boot, new(1920, 1080)], mode => { actual = mode; return ResultErr.CreateSuccess(); });
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService(root), display);
        display.Apply(new(1920, 1080), 1000);
        desktop.Suspend();
        Assert.IsFalse(display.Pending);
        Assert.AreEqual(boot, actual);
    }

    [TestMethod]
    public void SettingsSelectActiveModeInsteadOfFirstMode()
    {
        var boot = new DisplayMode(1024, 768);
        int switches = 0;
        var display = new DisplaySettings(boot, [new(1920, 1080), boot], _ => { switches++; return ResultErr.CreateSuccess(); });
        var app = new SettingsApp(new UiPainter(new byte[4096], 8, 16), new DesktopPreferences(new FileService(root)), display, () => 0);
        // Seitenleiste -> Aufloesungsliste -> Uebernehmen.
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(0, switches);
    }

    [DataTestMethod]
    [DataRow(40, 3)]
    [DataRow(108, 3)]
    [DataRow(124, 3)]
    [DataRow(40, 6)]
    public void BmpBitfieldsReadsCommonBgraAndRejectsInvalidMasks(int header, int compression)
    {
        int start = header == 40 ? 14 + header + (compression == 6 ? 16 : 12) : 14 + header;
        var bmp = new byte[start + 4];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        void Put(int offset, int value) => BitConverter.GetBytes(value).CopyTo(bmp, offset);
        Put(2, bmp.Length); Put(10, start); Put(14, header); Put(18, 1); Put(22, -1);
        bmp[26] = 1; bmp[28] = 32; Put(30, compression);
        Put(54, 0xff0000); Put(58, 0xff00); Put(62, 0xff);
        if (compression == 6 || header >= 108) Put(66, unchecked((int)0xff000000));
        Put(start, unchecked((int)0x00123456));
        Assert.AreEqual(unchecked((int)0xff123456), WallpaperImage.Decode(bmp).Pixels[0]);
        Put(58, 0xff0000);
        Assert.ThrowsException<ArgumentException>(() => WallpaperImage.Decode(bmp));
    }

    [TestMethod]
    public void ReadBytesHonorsPrivateFileRights()
    {
        string file = Path.Combine(root, "private.bmp");
        File.WriteAllBytes(file, [1, 2, 3]);
        var access = new FileAccessControl(root);
        access.Initialize(); access.Assign(root, "1"); access.Grant(root, "*", FilePermission.Read); access.Assign(file, "1");
        var other = new FileService(root, access: access, session: new UserSession("2", "bob", false, root));
        Assert.IsFalse(other.ReadBytes("private.bmp", 100).IsOK());
    }

    [TestMethod]
    public void TaskManagerDoesNotRetargetSelectionAfterWindowReplacement()
    {
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(), () => new FileService(root));
        desktop.OpenTerminal(); var selected = desktop.Windows.ActiveWindow!;
        desktop.OpenTaskManager(); var app = (TaskManagerApp)desktop.Windows.ActiveWindow!.Application;
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Down));
        desktop.Windows.RequestClose(selected);
        desktop.OpenTerminal(); var replacement = desktop.Windows.ActiveWindow!;
        app.Update(1000);
        // Tab waehlt die vorher sichtbare Anwendungsliste; Enter darf nicht ihr altes Zeilenziel ersetzen.
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Tab));
        app.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreSame(replacement, desktop.Windows.ActiveWindow);
    }
}
