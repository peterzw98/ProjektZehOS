using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Installer;
using ProjektZeh.Shared;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SetupWizardTests
{
    private static readonly UiPainter Painter = new(new byte[256 * 64], 16, 32);

    [TestMethod]
    public void Wizard_KeyboardOnlyFlow_InstallsEmptyDiskWithoutErase()
    {
        var service = new FakeInstallService(new InstallDisk("disk0", 20UL << 30, 0, IsBlank: true)) { Job = new FakeJob(3) };
        var wizard = new SetupWizard(service, Painter, 1024, 768);
        Assert.AreEqual(SetupPage.Welcome, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.License, wizard.Page);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Disk, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Confirm, wizard.Page);
        Assert.AreEqual("Installieren", Label(wizard));
        Press(wizard, PhysicalKey.Enter);
        CollectionAssert.AreEqual(new[] { ("disk0", false) }, service.BeginCalls);
        Assert.AreEqual(SetupPage.Installing, wizard.Page);
        for (int i = 0; i < 10 && wizard.Page == SetupPage.Installing; i++) wizard.Update();
        Assert.AreEqual(3, service.Job.Steps);
        Assert.AreEqual(SetupPage.Done, wizard.Page);
        Assert.AreEqual(0, service.RestartCount);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(1, service.RestartCount);
    }

    [TestMethod]
    public void License_WithoutAcceptance_BlocksNextAndKeepsChoiceOnBack()
    {
        var wizard = new SetupWizard(new FakeInstallService(new InstallDisk("disk0", 1UL << 30, 0, IsBlank: true)), Painter, 1024, 768);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.License, wizard.Page);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.License, wizard.Page);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("< Zurueck", Label(wizard));
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Welcome, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Disk, wizard.Page);
    }

    [TestMethod]
    public void Confirm_PartitionedDisk_RequiresFreshEraseConfirmation()
    {
        var service = new FakeInstallService(new InstallDisk("disk1", 64UL << 30, 2, IsBlank: false)) { Job = new FakeJob(1) };
        var wizard = ReachConfirm(service);
        Assert.IsInstanceOfType<SetupCheckBox>(wizard.FocusedControl);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Tab);
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("Abbrechen", Label(wizard), "Installieren bleibt ohne Bestaetigung uebersprungen.");
        Assert.AreEqual(0, service.BeginCalls.Count);
        Press(wizard, PhysicalKey.Tab);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("< Zurueck", Label(wizard));
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Disk, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Confirm, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(0, service.BeginCalls.Count, "Rueckkehr setzt die Loeschbestaetigung zurueck.");
        Click(wizard, wizard.FocusedControl!);
        Press(wizard, PhysicalKey.Enter);
        CollectionAssert.AreEqual(new[] { ("disk1", true) }, service.BeginCalls);
        Assert.AreEqual(SetupPage.Installing, wizard.Page);
    }

    [TestMethod]
    public void Confirm_UnpartitionedDiskWithData_RequiresEraseConfirmation()
    {
        var service = new FakeInstallService(new InstallDisk("disk2", 8UL << 30, 0, IsBlank: false)) { Job = new FakeJob(1) };
        var wizard = ReachConfirm(service);
        Assert.IsInstanceOfType<SetupCheckBox>(wizard.FocusedControl);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        CollectionAssert.AreEqual(new[] { ("disk2", true) }, service.BeginCalls);
    }

    [TestMethod]
    public void DiskList_ArrowsAndClick_SelectDiskAndKeepSelectionOnRefresh()
    {
        var service = new FakeInstallService(new InstallDisk("a", 1UL << 30, 0, IsBlank: true), new InstallDisk("b", 1UL << 30, 1, IsBlank: false),
            new InstallDisk("c", 1UL << 30, 0, IsBlank: true)) { Job = new FakeJob(1) };
        var wizard = ReachDisk(service);
        var list = (SetupDiskList)wizard.FocusedControl!;
        Press(wizard, PhysicalKey.Down);
        Press(wizard, PhysicalKey.Down);
        Press(wizard, PhysicalKey.Down);
        Assert.AreEqual("c", list.Selected!.Name);
        Press(wizard, PhysicalKey.Up);
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("Aktualisieren", Label(wizard));
        service.Disks = [new InstallDisk("b", 1UL << 30, 0, IsBlank: true), new InstallDisk("a", 1UL << 30, 0, IsBlank: true)];
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual("b", list.Selected!.Name);
        Assert.AreEqual(SetupPage.Disk, wizard.Page);
        wizard.Render(new PixelSurface(1024, 768, new int[1024 * 768]), 0, 0, PointerButtons.None);
        // Kopfzeile und erste Zeile sind je eine Glyphenhoehe plus Abstand hoch.
        wizard.Handle(new(InputEventType.PointerDown, X: list.Bounds.X + 50, Y: list.Bounds.Y + 1 + 40 + 44 + 10, Button: PointerButtons.Left));
        wizard.Handle(new(InputEventType.PointerUp, X: list.Bounds.X + 50, Y: list.Bounds.Y + 1 + 40 + 44 + 10, Button: PointerButtons.Left));
        Assert.AreEqual("a", list.Selected!.Name);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Enter);
        CollectionAssert.AreEqual(new[] { ("a", false) }, service.BeginCalls);
    }

    [TestMethod]
    public void DiskPage_NoDisks_DisablesNext()
    {
        var wizard = ReachDisk(new FakeInstallService());
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Disk, wizard.Page);
        Press(wizard, PhysicalKey.Tab);
        Press(wizard, PhysicalKey.Tab);
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("Abbrechen", Label(wizard));
    }

    [TestMethod]
    public void Disk_FocusTraversal_FollowsTabAndShiftTab()
    {
        var wizard = ReachDisk(new FakeInstallService(new InstallDisk("disk0", 1UL << 30, 0, IsBlank: true)));
        Assert.IsInstanceOfType<SetupDiskList>(wizard.FocusedControl);
        string[] expected = ["Aktualisieren", "< Zurueck", "Weiter >", "Abbrechen"];
        foreach (string label in expected)
        {
            Press(wizard, PhysicalKey.Tab);
            Assert.AreEqual(label, Label(wizard));
        }
        Press(wizard, PhysicalKey.Tab);
        Assert.IsInstanceOfType<SetupDiskList>(wizard.FocusedControl);
        wizard.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("Abbrechen", Label(wizard));
        wizard.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.LeftShift));
    }

    [TestMethod]
    public void Begin_Failure_ShowsErrorAndBackReturnsToConfirm()
    {
        var service = new FakeInstallService(new InstallDisk("disk0", 1UL << 30, 0, IsBlank: true))
        {
            BeginResult = ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.Failure, "Platte belegt")
        };
        var wizard = ReachConfirm(service);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Error, wizard.Page);
        Assert.AreEqual("< Zurueck", Label(wizard));
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Confirm, wizard.Page);
    }

    [TestMethod]
    public void Step_Failure_ShowsErrorAndBackRefreshesDisks()
    {
        var job = new FakeJob(5) { FailAt = 2 };
        var service = new FakeInstallService(new InstallDisk("disk0", 1UL << 30, 0, IsBlank: true)) { Job = job };
        var wizard = ReachConfirm(service);
        Press(wizard, PhysicalKey.Enter);
        for (int i = 0; i < 5; i++) wizard.Update();
        Assert.AreEqual(SetupPage.Error, wizard.Page);
        Assert.AreEqual(2, job.Steps, "Nach dem Fehler wird nicht weiter installiert.");
        service.Disks = [new InstallDisk("disk0", 1UL << 30, 1, IsBlank: false)];
        int reads = service.GetDisksCount;
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Disk, wizard.Page);
        Assert.AreEqual(reads + 1, service.GetDisksCount);
    }

    [TestMethod]
    public void Fail_FromException_ShowsErrorPage()
    {
        var wizard = ReachConfirm(new FakeInstallService(new InstallDisk("disk0", 1UL << 30, 0, IsBlank: true)) { Job = new FakeJob(4) });
        Press(wizard, PhysicalKey.Enter);
        wizard.Fail(ResultErr.CreateFromException(new IOException("Lesefehler")));
        Assert.AreEqual(SetupPage.Error, wizard.Page);
        wizard.Update();
        Assert.AreEqual(SetupPage.Error, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Disk, wizard.Page);
    }

    [TestMethod]
    public void GetDisks_Failure_ShowsErrorAndBackReturnsToLicense()
    {
        var service = new FakeInstallService { DisksResult = ResultObjectFactory.CreateFailure<InstallDisk[]>(ResultCode.NotFound, "Kein Controller") };
        var wizard = ReachDisk(service);
        Assert.AreEqual(SetupPage.Error, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.License, wizard.Page);
    }

    [TestMethod]
    public void Installing_EscapeTabAndClick_CannotCancel()
    {
        var service = new FakeInstallService(new InstallDisk("disk0", 1UL << 30, 0, IsBlank: true)) { Job = new FakeJob(3) };
        var wizard = ReachConfirm(service);
        var cancel = FindButton(wizard, "Abbrechen");
        FindButton(wizard, "Installieren");
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Installing, wizard.Page);
        Press(wizard, PhysicalKey.Escape);
        Assert.IsFalse(wizard.IsCancelPromptOpen);
        Press(wizard, PhysicalKey.Tab);
        Assert.IsNull(wizard.FocusedControl);
        Click(wizard, cancel);
        Assert.IsFalse(wizard.IsCancelPromptOpen);
        Assert.AreEqual(0, service.RestartCount);
    }

    [TestMethod]
    public void CancelPrompt_EscapeClosesAndYesRestarts()
    {
        var service = new FakeInstallService();
        var wizard = new SetupWizard(service, Painter, 1024, 768);
        Press(wizard, PhysicalKey.Escape);
        Assert.IsTrue(wizard.IsCancelPromptOpen);
        Assert.AreEqual("Nein", Label(wizard));
        Press(wizard, PhysicalKey.Enter);
        Assert.IsFalse(wizard.IsCancelPromptOpen);
        Assert.AreEqual(0, service.RestartCount);
        Press(wizard, PhysicalKey.Escape);
        Press(wizard, PhysicalKey.Escape);
        Assert.IsFalse(wizard.IsCancelPromptOpen);
        Press(wizard, PhysicalKey.Escape);
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("Ja", Label(wizard));
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(1, service.RestartCount);
        service.RestartResult = ResultErr.CreateFailure(ResultCode.Failure, "Kein Neustart");
        Press(wizard, PhysicalKey.Escape);
        Click(wizard, wizard.FocusedControl!);
        Assert.IsFalse(wizard.IsCancelPromptOpen, "Klick auf Nein schliesst die Rueckfrage.");
        Press(wizard, PhysicalKey.Escape);
        Press(wizard, PhysicalKey.Tab);
        Press(wizard, PhysicalKey.Space);
        Assert.AreEqual(SetupPage.Error, wizard.Page);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Welcome, wizard.Page);
    }

    [TestMethod]
    public void Render_AllPagesAtFullHd1024And800_StaysInsideAndClearsDirty()
    {
        foreach (var painter in new[] { Painter, new UiPainter(new byte[4096], 8, 16) })
        {
            foreach (var (width, height) in new[] { (1920, 1080), (1024, 768), (800, 600) })
            {
                foreach (var (name, wizard) in AllPages(painter, width, height))
                {
                    var surface = new PixelSurface(width, height, new int[width * height]);
                    Assert.IsTrue(wizard.IsDirty, name);
                    wizard.Render(surface, width - 1, height - 1, PointerButtons.None);
                    Assert.IsFalse(wizard.IsDirty, name);
                    wizard.Handle(new(InputEventType.PointerMove, X: 5, Y: 5));
                    Assert.IsTrue(wizard.IsDirty, name);
                }
            }
        }
    }

    [TestMethod]
    public void Layout_FullHd_UsesLargerCenteredDialogWithDesktopBackground()
    {
        var wizard = new SetupWizard(new FakeInstallService(), Painter, 1920, 1080);
        var start = (UiButton)wizard.FocusedControl!;
        Assert.AreEqual(960, start.Bounds.X + start.Bounds.Width / 2, "Startschaltflaeche steht mittig.");
        var cancel = FindButton(wizard, "Abbrechen");
        int right = cancel.Bounds.X + cancel.Bounds.Width;
        // Der Dialog waechst ueber die 880 Pixel von 1024x768 hinaus, bleibt aber deutlich schmaler als der Bildschirm.
        Assert.IsTrue(right > 960 + 880 / 2 && right < 1920 - 200, $"Rechter Rand {right}");
        var pixels = new int[1920 * 1080];
        wizard.Render(new PixelSurface(1920, 1080, pixels), 0, 0, PointerButtons.None);
        var wallpaper = new int[1920 * 1080];
        var defaults = new DesktopSettings();
        WallpaperImage.Create(wallpaper, 1920, 1080, defaults.Wallpaper, defaults.Dark, UiPainter.Accent);
        Assert.AreEqual(wallpaper[1079 * 1920 + 1919], pixels[1079 * 1920 + 1919], "Hintergrund entspricht dem Desktopstandard.");
        Assert.AreEqual(UiPainter.Background, pixels[540 * 1920 + 400], "Dialogflaeche verwendet die Themenfarbe.");
    }

    [TestMethod]
    public void Paragraph_LongWordsAndBlankLines_WrapsWithinColumns()
    {
        Assert.AreEqual(5, SetupWizard.Paragraph(null, Painter, "abc def ghi\n\nxxxxxxxx", 0, 0, 7 * 16, 0));
        Assert.AreEqual(1, SetupWizard.Paragraph(null, Painter, "", 0, 0, 100, 0));
        Assert.AreEqual("20,0 GB", SetupDiskList.FormatSize(20UL << 30));
        Assert.AreEqual("512 MB", SetupDiskList.FormatSize(512UL << 20));
    }

    /// <summary>Schreibt alle Seiten als PNG nach ZEHOS_SCREENSHOT_DIR; ohne Variable wird nichts erzeugt.</summary>
    [TestMethod]
    public void Screenshots_WhenRequested_WritePngPerPage()
    {
        string? directory = Environment.GetEnvironmentVariable("ZEHOS_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        var painter = ScreenshotSupport.LoadCosmosFont() ?? Painter;
        Directory.CreateDirectory(directory);
        foreach (var (width, height) in new[] { (1920, 1080), (1024, 768), (800, 600) })
        {
            foreach (var (name, wizard) in AllPages(painter, width, height))
            {
                var pixels = new int[width * height];
                wizard.Render(new PixelSurface(width, height, pixels), width * 2 / 3, height / 3, PointerButtons.None);
                ScreenshotSupport.WritePng(Path.Combine(directory, $"{width}x{height}-{name}.png"), width, height, pixels);
            }
        }
    }

    private static IEnumerable<(string Name, SetupWizard Wizard)> AllPages(UiPainter painter, int width, int height)
    {
        FakeInstallService Service() => new(new InstallDisk("QEMU HARDDISK 0", 32UL << 30, 2, IsBlank: false), new InstallDisk("VBOX SSD 1", 8UL << 30, 0, IsBlank: true),
            new InstallDisk("Testplatte", 512UL << 20, 0, IsBlank: true)) { Job = new FakeJob(10) { Percent = 42, Stage = InstallStage.CopyingFiles } };
        var wizard = new SetupWizard(Service(), painter, width, height);
        yield return ("1-welcome", wizard);
        wizard = new SetupWizard(Service(), painter, width, height);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Down);
        Press(wizard, PhysicalKey.Down);
        Press(wizard, PhysicalKey.Tab);
        yield return ("2-license", wizard);
        yield return ("3-disk", ReachDisk(Service(), painter, width, height));
        yield return ("4-confirm", ReachConfirm(Service(), painter, width, height));
        wizard = ReachConfirm(Service(), painter, width, height);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        yield return ("5-installing", wizard);
        var finish = Service();
        finish.Job = new FakeJob(1);
        wizard = ReachConfirm(finish, painter, width, height);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        wizard.Update();
        yield return ("6-done", wizard);
        var failing = Service();
        failing.BeginResult = ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.Failure,
            "Die Partitionstabelle konnte nicht geschrieben werden: Schreibfehler an Sektor 0.");
        wizard = ReachConfirm(failing, painter, width, height);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        yield return ("7-error", wizard);
        wizard = new SetupWizard(Service(), painter, width, height);
        Press(wizard, PhysicalKey.Escape);
        yield return ("8-cancel", wizard);
    }

    private static SetupWizard ReachDisk(FakeInstallService service, UiPainter? painter = null, int width = 1024, int height = 768)
    {
        var wizard = new SetupWizard(service, painter ?? Painter, width, height);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        return wizard;
    }

    private static SetupWizard ReachConfirm(FakeInstallService service, UiPainter? painter = null, int width = 1024, int height = 768)
    {
        var wizard = ReachDisk(service, painter, width, height);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Confirm, wizard.Page);
        return wizard;
    }

    private static UiButton FindButton(SetupWizard wizard, string label)
    {
        for (int i = 0; i < 8; i++)
        {
            if (wizard.FocusedControl is UiButton button && button.Label == label) return button;
            Press(wizard, PhysicalKey.Tab);
        }
        throw new AssertFailedException($"Schaltflaeche {label} fehlt.");
    }

    private static string? Label(SetupWizard wizard) => (wizard.FocusedControl as UiButton)?.Label;

    private static void Press(SetupWizard wizard, PhysicalKey key)
    {
        wizard.Handle(new(InputEventType.KeyDown, KeyCode: (byte)key));
        wizard.Handle(new(InputEventType.KeyUp, KeyCode: (byte)key));
    }

    private static void Click(SetupWizard wizard, UiControl control)
    {
        int x = control.Bounds.X + control.Bounds.Width / 2;
        int y = control.Bounds.Y + control.Bounds.Height / 2;
        wizard.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        wizard.Handle(new(InputEventType.PointerUp, X: x, Y: y, Button: PointerButtons.Left));
    }

    private sealed class FakeInstallService(params InstallDisk[] disks) : IInstallService
    {
        public InstallDisk[] Disks { get; set; } = disks;
        public ResultObject<InstallDisk[]>? DisksResult { get; set; }
        public ResultObject<IInstallJob>? BeginResult { get; set; }
        public ResultErr RestartResult { get; set; } = ResultErr.CreateSuccess();
        public FakeJob Job { get; set; } = new(1);
        public List<(string, bool)> BeginCalls { get; } = [];
        public int GetDisksCount { get; private set; }
        public int RestartCount { get; private set; }
        public string ProductVersion => "0.2.0";

        public ResultObject<InstallDisk[]> GetDisks()
        {
            GetDisksCount++;
            return DisksResult ?? ResultObjectFactory.CreateSuccess(Disks);
        }

        public ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting)
        {
            BeginCalls.Add((diskName, eraseExisting));
            return BeginResult ?? ResultObjectFactory.CreateSuccess<IInstallJob>(Job);
        }

        public ResultErr Restart()
        {
            RestartCount++;
            return RestartResult;
        }
    }

    private sealed class FakeJob(int total) : IInstallJob
    {
        public int Steps { get; private set; }
        public int FailAt { get; init; } = -1;
        public InstallStage Stage { get; set; } = InstallStage.Partitioning;
        public int Percent { get; set; }
        public bool IsCompleted => Steps >= total;

        public ResultErr Step()
        {
            Steps++;
            if (Steps == FailAt) return ResultErr.CreateFailure(ResultCode.Failure, "Schreibfehler");
            Percent = Steps * 100 / total;
            if (IsCompleted) Stage = InstallStage.Completed;
            return ResultErr.CreateSuccess();
        }
    }
}
