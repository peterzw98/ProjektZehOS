using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopSettingsTests
{
    [TestMethod]
    public void Settings_RoundtripAndInvalidValues_UseValidatedDefaults()
    {
        var settings = DesktopSettings.Parse("version=2\ndisplayConfirmed=True\ndark=false\naccent=2\nwallpaper=1\nwidth=1920\nheight=1080\nimage=/data/wall.bmp");
        Assert.IsFalse(settings.Dark);
        Assert.AreEqual(2, settings.Accent);
        Assert.AreEqual(settings, DesktopSettings.Parse(settings.Serialize()));
        var invalid = DesktopSettings.Parse("dark=nonsense\naccent=999\nwallpaper=-9\nwidth=1\nheight=999999");
        Assert.IsTrue(invalid.Dark);
        Assert.AreEqual(0, invalid.Accent);
        Assert.AreEqual(0, invalid.Width);
        Assert.AreEqual(0, invalid.Height);
    }

    [TestMethod]
    public void Display_TimeoutRestoresActualMode_ConfirmKeepsMode()
    {
        var hd = new DisplayMode(1280, 720);
        var fhd = new DisplayMode(1920, 1080);
        DisplayMode actual = fhd;
        var display = new DisplaySettings(fhd, [hd, fhd], value => { actual = value; return ResultErr.CreateSuccess(); });
        Assert.IsTrue(display.Apply(hd, 100).IsOK());
        Assert.IsTrue(display.Pending);
        display.Update(15099);
        Assert.AreEqual(hd, actual);
        display.Update(15100);
        Assert.AreEqual(fhd, actual);
        Assert.IsFalse(display.Pending);
        display.Apply(hd, 20000);
        display.Confirm();
        display.Update(99999);
        Assert.AreEqual(hd, actual);
        Assert.IsFalse(display.Apply(new(3000, 2000), 100000).IsOK());
    }

    [TestMethod]
    public void Display_FailedSwitchDoesNotChangeSelection()
    {
        var fhd = new DisplayMode(1920, 1080);
        var display = new DisplaySettings(fhd, [fhd, new(1024, 768)], _ => ResultErr.CreateFailure(ResultCode.Failure, "Treiberfehler"));
        Assert.IsFalse(display.Apply(new(1024, 768), 0).IsOK());
        Assert.AreEqual(fhd, display.Current);
        Assert.IsFalse(display.Pending);
    }

    [TestMethod]
    public void Wallpaper_BmpRejectsTruncationAndOversize_AndReadsBottomUp()
    {
        byte[] bmp = new byte[70];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(70).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(2).CopyTo(bmp, 18);
        BitConverter.GetBytes(2).CopyTo(bmp, 22);
        bmp[26] = 1; bmp[28] = 24;
        bmp[54] = 255; bmp[62 + 2] = 255;
        var image = WallpaperImage.Decode(bmp);
        Assert.AreEqual(unchecked((int)0xffff0000), image.Pixels[0]);
        Assert.AreEqual(unchecked((int)0xff0000ff), image.Pixels[2]);
        Assert.ThrowsException<ArgumentException>(() => WallpaperImage.Decode(bmp[..60]));
        BitConverter.GetBytes(int.MaxValue).CopyTo(bmp, 18);
        Assert.ThrowsException<ArgumentException>(() => WallpaperImage.Decode(bmp));
    }

    [TestMethod]
    public void Input_ResizeClampsPointerAndWindowBounds_WithoutLosingApplication()
    {
        var input = new InputService(1920, 1080);
        input.ReportPointer(1800, 1000, PointerButtons.Left);
        input.Resize(1024, 768);
        Assert.AreEqual(1023, input.PointerX);
        Assert.AreEqual(767, input.PointerY);
        Assert.AreEqual(PointerButtons.None, input.Buttons);
        var desktop = new Desktop(1920, 1080, new byte[4096], 8, 16, () => new ShellService(), () => new FileService("/data"));
        desktop.OpenTerminal();
        var window = desktop.Windows.ActiveWindow!;
        var app = window.Application;
        desktop.Resize(1024, 768);
        Assert.AreSame(app, desktop.Windows.ActiveWindow!.Application);
        Assert.IsTrue(window.X + window.Width <= 1024);
        Assert.IsTrue(window.Y + window.Height <= 712);
    }

    [TestMethod]
    public void Menu_DoubleClickStartsOnce_AndResetCancelsSequence()
    {
        long clock = 0;
        var list = new UiList { Bounds = new(0, 0, 240, 100), DoubleClick = true, GetMilliseconds = () => clock };
        list.SetItems(["Terminal", "Dateien"]);
        list.Draw(new PixelSurface(300, 150, new int[45000]), new UiPainter(new byte[4096], 8, 16), 0, 0, true);
        int launches = 0;
        list.Activated = () => launches++;
        void Click(int y) => list.Handle(new(InputEventType.PointerDown, X: 10, Y: y, Button: PointerButtons.Left));
        Click(10); Assert.AreEqual(0, launches);
        clock = 200; Click(10); Assert.AreEqual(1, launches);
        clock = 250; Click(10); Assert.AreEqual(1, launches);
        clock = 300; Click(30); Assert.AreEqual(1, launches);
        list.Handle(new(InputEventType.Reset));
        clock = 350; Click(30); Assert.AreEqual(1, launches);
        clock = 900; Click(30); Assert.AreEqual(1, launches);
        list.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        Assert.AreEqual(2, launches);
    }

    [TestMethod]
    public void Preferences_SaveFailureRemainsVisible_AndRoundtripUsesFileService()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zehos-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preferences = new DesktopPreferences(new FileService(directory));
            Assert.IsTrue(preferences.Apply(preferences.Settings with { Dark = false, Accent = 1 }).IsOK());
            Assert.IsFalse(preferences.StatusIsError);
            var loaded = new DesktopPreferences(new FileService(directory));
            Assert.IsFalse(loaded.Settings.Dark);
            Assert.AreEqual(1, loaded.Settings.Accent);
            var missing = new DesktopPreferences(new FileService(Path.Combine(directory, "absent")));
            Assert.IsFalse(missing.Save().IsOK());
            Assert.IsTrue(missing.Status.Length > 0);
        }
        finally { Directory.Delete(directory, true); }
    }
}
