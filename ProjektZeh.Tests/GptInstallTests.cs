using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
using ProjektZeh.Installer;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage.Ext2;
using ProjektZeh.Storage.Partitioning;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

/// <summary>GPT-Installer (Spec 7a-S4, Abschnitt 5, Hosttests 1-8).</summary>
[TestClass]
public sealed class GptInstallTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private static readonly byte[] Efi = [1, 2, 3];
    private static readonly byte[] Kernel = new byte[150 * 1024];

    // Test 1: jede Vorbedingung scheitert vor dem ersten Schreibzugriff.
    [DataTestMethod]
    [DataRow("klein", "32 bis 128 GiB")]
    [DataRow("gross", "32 bis 128 GiB")]
    [DataRow("sektor4k", "512-Byte-Sektoren")]
    [DataRow("cd0", "Installationsmedium")]
    [DataRow("fehlt", "nicht gefunden")]
    public void BeginGpt_DiskNotSuitable_FailsWithoutOpeningOrWriting(string disk, string message)
    {
        var platform = new Platform();
        var result = Service(platform).BeginGpt(disk, true);

        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, message);
        Assert.AreEqual(0, platform.Opens);
    }

    [TestMethod]
    public void BeginGpt_SizeLimits_AreInclusive()
    {
        var platform = new Platform();
        Assert.IsTrue(Service(platform).BeginGpt("min", true).IsOK(), "Genau 32 GiB ist erlaubt.");
        Assert.IsTrue(Service(platform).BeginGpt("max", true).IsOK(), "Genau 128 GiB ist erlaubt.");
    }

    [TestMethod]
    public void BeginGpt_WithoutErase_Fails()
    {
        var platform = new Platform();
        var result = Service(platform).BeginGpt("min", false);

        StringAssert.Contains(result.Status.Message, "--erase erforderlich");
        Assert.AreEqual(0, platform.Opens);
    }

    [TestMethod]
    public void BeginGpt_RandomUnavailable_FailsWithoutOpening()
    {
        var platform = new Platform();
        var service = new InstallService(platform, Efi, Kernel, "0.5.0", gptEnabled: true,
            random: _ => throw new HardwareRandomUnavailableException());

        var result = service.BeginGpt("min", true);

        StringAssert.Contains(result.Status.Message, "Zufallsquelle");
        Assert.AreEqual(0, platform.Opens);
    }

    [TestMethod]
    public void BeginGpt_FilesMissingOrTooLargeForEsp_Fails()
    {
        var platform = new Platform();
        Assert.IsFalse(new InstallService(platform, [], Kernel, "0.5.0", gptEnabled: true, random: Fill).BeginGpt("min", true).IsOK());
        // Kernel zweimal auf der ESP: 250 MiB passen nicht in 512 MiB minus 16 MiB Reserve.
        var tooLarge = new InstallService(platform, Efi, new byte[250 * 1024 * 1024], "0.5.0", gptEnabled: true, random: Fill).BeginGpt("min", true);
        StringAssert.Contains(tooLarge.Status.Message, "EFI-Partition");
        Assert.AreEqual(0, platform.Opens);
    }

    [TestMethod]
    public void BeginGpt_PlatformRefusesTarget_ReturnsThatFailure()
    {
        // Eingebundenes Ziel, ausgeschlossene Datenplatte und belegte Mountpunkte prueft die Plattform beim Oeffnen.
        var platform = new Platform { OpenFailure = "Installations-Mountpunkt belegt" };
        var result = Service(platform).BeginGpt("min", true);

        Assert.AreEqual("Installations-Mountpunkt belegt", result.Status.Message);
        Assert.AreEqual(0, platform.Target.Calls.Count);
    }

    [TestMethod]
    public void BeginGpt_WithoutFeatureFlag_Fails()
    {
        var platform = new Platform();
        var result = new InstallService(platform, Efi, Kernel, "0.5.0", random: Fill).BeginGpt("min", true);

        StringAssert.Contains(result.Status.Message, "nicht freigeschaltet");
        Assert.AreEqual(0, platform.Opens);
    }

    [TestMethod]
    public void BeginGpt_GuidsComeFromBegin_RandomNeverCalledAgain()
    {
        var platform = new Platform();
        int calls = 0;
        var service = new InstallService(platform, Efi, Kernel, "0.5.0", gptEnabled: true, random: bytes => { calls++; Fill(bytes); });

        var job = service.BeginGpt("min", true);
        Run(job.Value!);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, platform.Target.Tables.Count);
    }

    // Test 2: der Legacy-Weg bleibt unveraendert.
    [TestMethod]
    public void Begin_Legacy_StillCreatesLegacyJob()
    {
        var job = Service(new Platform()).Begin("min", true);
        Assert.IsInstanceOfType<InstallJob>(job.Value);
    }

    // Test 3 und 8: Shell mit und ohne Freischaltung.
    [DataTestMethod]
    [DataRow("setup install min --layout gpt --erase", false)]
    [DataRow("setup install min --layout mbr --erase", true)]
    [DataRow("setup install min --layout", true)]
    [DataRow("setup install min --layout gpt --layout gpt --erase", true)]
    public void Shell_UnknownLayoutOrNotEnabled_ReturnsUsage(string line, bool enabled)
    {
        var installer = new RecordingService();
        var shell = new ShellService(new SetupShellCommands(installer, () => true, enabled).Commands);

        var result = shell.ExecuteLine(line, new Output());

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code);
        Assert.AreEqual(0, installer.Calls.Count);
    }

    [TestMethod]
    public void Shell_GptWithoutErase_FailsWithHint()
    {
        var installer = new RecordingService();
        var shell = new ShellService(new SetupShellCommands(installer, () => true, true).Commands);

        var result = shell.ExecuteLine("setup install min --layout gpt", new Output());

        StringAssert.Contains(result.Message, "--erase");
        Assert.AreEqual(0, installer.Calls.Count);
    }

    [DataTestMethod]
    [DataRow("setup install min --layout gpt --erase")]
    [DataRow("setup install min --erase --layout gpt")]
    public void Shell_GptWithErase_CallsBeginGpt(string line)
    {
        var installer = new RecordingService();
        var shell = new ShellService(new SetupShellCommands(installer, () => true, true).Commands);

        Assert.IsTrue(shell.ExecuteLine(line, new Output()).IsOK());

        CollectionAssert.AreEqual(new[] { "gpt min" }, installer.Calls);
    }

    [TestMethod]
    public void Shell_LegacyInstall_StillCallsBegin()
    {
        var installer = new RecordingService();
        var shell = new ShellService(new SetupShellCommands(installer, () => true, true).Commands);

        Assert.IsTrue(shell.ExecuteLine("setup install min --erase", new Output()).IsOK());

        CollectionAssert.AreEqual(new[] { "legacy min True" }, installer.Calls);
    }

    [TestMethod]
    public void Shell_List_ShowsGptColumnOnlyWhenEnabled()
    {
        var installer = new RecordingService
        {
            Disks = [new("a", 32 * GiB, 0, true), new("b", 31 * GiB, 0, true), new("c", 64 * GiB, 0, true, 4096)],
        };
        var enabled = new Output();
        new ShellService(new SetupShellCommands(installer, () => true, true).Commands).ExecuteLine("setup list", enabled);
        var disabled = new Output();
        new ShellService(new SetupShellCommands(installer, () => true).Commands).ExecuteLine("setup list", disabled);

        StringAssert.EndsWith(enabled.Lines[0], ", gpt: ja");
        StringAssert.Contains(enabled.Lines[1], "gpt: nein (GPT-Layout nur fuer 32 bis 128 GiB)");
        StringAssert.Contains(enabled.Lines[2], "gpt: nein (GPT-Layout nur mit 512-Byte-Sektoren)");
        Assert.IsFalse(disabled.Lines.Any(line => line.Contains("gpt", StringComparison.Ordinal)));
    }

    // Test 4: Stufenfolge und Schranken.
    [TestMethod]
    public void Job_RunsStagesInSpecOrder_MarkerIsLastWriteBetweenFlushes()
    {
        var platform = new Platform();
        var job = Service(platform).BeginGpt("min", true).Value!;
        var percents = new List<int>();

        Run(job, percents);

        var calls = platform.Target.Calls;
        Assert.AreEqual(1, calls.Count(call => call == "layout"));
        Assert.IsTrue(calls.IndexOf("format-esp") < calls.IndexOf("format-root"));
        Assert.IsTrue(calls.IndexOf("prepare-root") < calls.FindIndex(call => call.StartsWith("file ", StringComparison.Ordinal)));
        int marker = calls.IndexOf("write-marker");
        Assert.AreEqual("flush-esp", calls[marker - 1]);
        Assert.AreEqual("flush-esp", calls[marker + 1]);
        Assert.AreEqual(marker, calls.FindLastIndex(IsWrite), "Der ESP-Marker ist der letzte Schreibzugriff.");
        // Zweites Superblock-Lesen nach dem Aushaengen des Root.
        Assert.AreEqual("superblock", calls[calls.IndexOf("prepare-root") + 1]);
        Assert.AreEqual("complete", calls[^1]);
        Assert.IsTrue(job.IsCompleted);
        CollectionAssert.AreEqual(percents.OrderBy(value => value).ToList(), percents, "Prozent steigen monoton.");
        Assert.AreEqual(100, job.Percent);
    }

    [TestMethod]
    public void Job_WritesMarkerMatchingTableAndRootMarkerWithDiskGuid()
    {
        var platform = new Platform();
        Run(Service(platform).BeginGpt("min", true).Value!);

        GptTable table = platform.Target.Tables[0];
        var marker = InstallMarker.Parse(platform.Target.Marker!).Value!;
        GptEntry root = table.Entries[1];
        Assert.AreEqual(table.DiskGuid, marker.DiskGuid);
        Assert.AreEqual(root.Unique, marker.RootGuid);
        Assert.AreEqual(root.FirstLba, marker.RootFirstLba);
        Assert.AreEqual(root.LastLba - root.FirstLba + 1, marker.RootSectors);
        Assert.AreEqual(table.DiskGuid, platform.Target.RootMarkerDisk);
    }

    // Test 5: Abbruch in jeder Stufe.
    [DataTestMethod]
    [DataRow("layout", "Partitioning")]
    [DataRow("format-esp", "FormattingEsp")]
    [DataRow("format-root", "FormattingRoot")]
    [DataRow("prepare-root", "PreparingRoot")]
    [DataRow("file /boot/ProjektZeh.elf", "CopyingFiles")]
    [DataRow("file /boot/limine/limine.conf", "WritingBootConfig")]
    [DataRow("write-marker", "WritingMarker")]
    public void Job_FailureInStage_AbortsWithoutMarkerAndNamesState(string failingCall, string stage)
    {
        var platform = new Platform();
        platform.Target.FailOn = failingCall;
        var job = Service(platform).BeginGpt("min", true).Value!;

        var result = RunUntilFailure(job);

        Assert.AreEqual(stage, job.Stage.ToString());
        Assert.AreEqual("abort", platform.Target.Calls[^1]);
        Assert.IsNull(platform.Target.Marker, "Ohne Abschluss steht nie ein gueltiger ESP-Marker.");
        StringAssert.Contains(result.Message, "setup install min --layout gpt --erase");
        StringAssert.Contains(result.Message, "unklar");
    }

    [TestMethod]
    public void Job_CompleteFails_ReportsErrorAfterMarker()
    {
        var platform = new Platform();
        platform.Target.FailOn = "complete";
        var job = Service(platform).BeginGpt("min", true).Value!;

        var result = RunUntilFailure(job);

        Assert.IsNotNull(platform.Target.Marker);
        StringAssert.Contains(result.Message, "Aushaengen fehlgeschlagen");
        Assert.IsFalse(job.IsCompleted);
    }

    [TestMethod]
    public void Job_AbortAlsoFails_NamesBothErrors()
    {
        var platform = new Platform();
        platform.Target.FailOn = "format-root";
        platform.Target.AbortFailure = "ESP nicht aushaengbar";
        var result = RunUntilFailure(Service(platform).BeginGpt("min", true).Value!);

        StringAssert.Contains(result.Message, "format-root fehlgeschlagen");
        StringAssert.Contains(result.Message, "ESP nicht aushaengbar");
    }

    // Test 6: Root-Pruefungen.
    [TestMethod]
    public void Job_RootCheckAfterFormatNotOk_AbortsBeforePreparingRoot()
    {
        var platform = new Platform();
        platform.Target.Superblocks.Enqueue(Superblock(state: 2));
        var job = Service(platform).BeginGpt("min", true).Value!;

        RunUntilFailure(job);

        Assert.AreEqual(InstallStage.VerifyingRoot, job.Stage);
        CollectionAssert.DoesNotContain(platform.Target.Calls, "prepare-root");
    }

    [TestMethod]
    public void Job_RootWithoutSparseSuper_AbortsAtVerifyingRoot()
    {
        var platform = new Platform();
        byte[] superblock = Superblock(state: 1);
        BitConverter.TryWriteBytes(superblock.AsSpan(100), 0u);
        platform.Target.Superblocks.Enqueue(superblock);
        var job = Service(platform).BeginGpt("min", true).Value!;

        var result = RunUntilFailure(job);

        Assert.AreEqual(InstallStage.VerifyingRoot, job.Stage);
        StringAssert.Contains(result.Message, "sparse_super");
    }

    [TestMethod]
    public void Job_SecondReadNotClean_AbortsBeforeCopying()
    {
        var platform = new Platform();
        platform.Target.Superblocks.Enqueue(Superblock(state: 1));
        platform.Target.Superblocks.Enqueue(Superblock(state: 0));
        var job = Service(platform).BeginGpt("min", true).Value!;

        var result = RunUntilFailure(job);

        Assert.AreEqual(InstallStage.PreparingRoot, job.Stage);
        StringAssert.Contains(result.Message, "s_state");
        Assert.IsFalse(platform.Target.Calls.Any(call => call.StartsWith("file ", StringComparison.Ordinal)));
    }

    // Test 7: Ruecklesen des Markers.
    [TestMethod]
    public void Job_MarkerReadBackDiffers_Aborts()
    {
        var platform = new Platform();
        platform.Target.CorruptMarkerReadBack = true;
        var job = Service(platform).BeginGpt("min", true).Value!;

        var result = RunUntilFailure(job);

        Assert.AreEqual(InstallStage.WritingMarker, job.Stage);
        StringAssert.Contains(result.Message, "Installationsmarker nach dem Schreiben nicht gleich");
        StringAssert.Contains(result.Message, "Installationsmarker fehlt");
    }

    // Test 6a und Abschnitt 4, erste Zeile: Identitaet nach Abbruch.
    [TestMethod]
    public void Identity_ReinstallOverHealthyAbortedAfterPartitioning_IsNotHealthy()
    {
        ulong blocks = 32 * GiB / 512;
        var old = GptLayoutWriter.Plan(512, blocks, Fill).Value!;
        var fresh = GptLayoutWriter.Plan(512, blocks, Fill).Value!;
        GptEntry oldRoot = old.Entries[1];
        string oldMarker = new InstallMarker(old.DiskGuid, oldRoot.Unique, oldRoot.FirstLba, oldRoot.LastLba - oldRoot.FirstLba + 1).Serialize();

        var result = InstalledIdentity.Evaluate(oldMarker, ResultObjectFactory.CreateSuccess(fresh), _ => RootCheck.Ok);

        Assert.AreEqual(InstalledState.Unclear, result.State);
    }

    [TestMethod]
    public void Identity_WriterAbortedBeforePrimaryHeader_IsUnclearOrNotGpt()
    {
        // Schritte 1 und 2 des Writers stehen auf der Platte (Backup gueltig, Primaerkopf genullt), dann bricht er ab.
        ulong blocks = 32 * GiB / 512;
        var disk = new Dictionary<ulong, byte[]>();
        int writes = 0;
        Assert.ThrowsException<InvalidOperationException>(() => GptLayoutWriter.Write(512, blocks, Fill, (block, data) =>
        {
            if (++writes > 4) throw new InvalidOperationException("Abbruch nach Schritt 2");
            for (ulong sector = 0; sector < (ulong)data.Length / 512; sector++)
                disk[block + sector] = data.Slice((int)sector * 512, 512).ToArray();
        }, () => { }));

        var table = GptLayout.Verify(512, blocks, (block, count, buffer) =>
        {
            for (ulong sector = 0; sector < count; sector++)
                (disk.TryGetValue(block + sector, out var data) ? data : new byte[512]).CopyTo(buffer.Slice((int)sector * 512, 512));
        });
        var result = InstalledIdentity.Evaluate(null, table, _ => RootCheck.Ok);

        Assert.IsFalse(table.IsOK(), "Ohne Primaerkopf ist die GPT ungueltig.");
        Assert.AreEqual(InstalledState.Unclear, result.State, "Ungueltige GPT ohne Marker sperrt, nie Legacy.");
    }

    // Test 8, Assistent: GPT-Zeile nur mit Freischaltung, bei ungeeigneter Platte ohne Kaestchen.
    [TestMethod]
    public void Wizard_GptChecked_RequiresEraseAndCallsBeginGpt()
    {
        var service = new RecordingService { Disks = [new("min", 32 * GiB, 0, true)] };
        var wizard = ReachConfirm(service, gptEnabled: true);

        Assert.AreEqual("Alle Daten auf min loeschen", (wizard.FocusedControl as SetupCheckBox)?.Label);
        Press(wizard, PhysicalKey.Tab);
        Assert.AreEqual("GPT mit ext2-Root (32 bis 128 GiB)", (wizard.FocusedControl as SetupCheckBox)?.Label);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Confirm, wizard.Page, "Ohne Loeschbestaetigung startet der GPT-Weg nicht.");
        Assert.AreEqual(0, service.Calls.Count);

        Press(wizard, PhysicalKey.Tab);
        while (wizard.FocusedControl is not SetupCheckBox { Label: "Alle Daten auf min loeschen" }) Press(wizard, PhysicalKey.Tab);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);

        CollectionAssert.AreEqual(new[] { "gpt min" }, service.Calls);
    }

    [TestMethod]
    public void Wizard_GptUnchecked_UsesLegacyBegin()
    {
        var service = new RecordingService { Disks = [new("min", 32 * GiB, 0, true)] };
        var wizard = ReachConfirm(service, gptEnabled: true);

        Press(wizard, PhysicalKey.Enter);

        CollectionAssert.AreEqual(new[] { "legacy min False" }, service.Calls);
    }

    [DataTestMethod]
    [DataRow(false, 32UL)]
    [DataRow(true, 31UL)]
    public void Wizard_NotEnabledOrDiskUnsuitable_OffersNoGptCheckbox(bool enabled, ulong sizeGiB)
    {
        var wizard = ReachConfirm(new RecordingService { Disks = [new("disk", sizeGiB * GiB, 0, true)] }, enabled);

        for (int index = 0; index < 8; index++)
        {
            Assert.IsFalse(wizard.FocusedControl is SetupCheckBox { Label: "GPT mit ext2-Root (32 bis 128 GiB)" });
            Press(wizard, PhysicalKey.Tab);
        }
    }

    [TestMethod]
    public void Wizard_StageRow_LegacyKeepsOrderAndGptStagesFollowPhases()
    {
        InstallStage[] legacy = [InstallStage.Partitioning, InstallStage.Formatting, InstallStage.CopyingFiles, InstallStage.WritingBootConfig, InstallStage.Completed];
        InstallStage[] gpt = [InstallStage.Partitioning, InstallStage.FormattingEsp, InstallStage.FormattingRoot, InstallStage.VerifyingRoot,
            InstallStage.PreparingRoot, InstallStage.CopyingFiles, InstallStage.WritingBootConfig, InstallStage.WritingMarker, InstallStage.Completed];

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, legacy.Select(SetupWizard.StageRow).ToArray());
        var rows = gpt.Select(SetupWizard.StageRow).ToList();
        CollectionAssert.AreEqual(rows.OrderBy(row => row).ToList(), rows);
        Assert.AreEqual(4, rows[^1]);
    }

    private static SetupWizard ReachConfirm(RecordingService service, bool gptEnabled)
    {
        var wizard = new SetupWizard(service, new UiPainter(new byte[256 * 64], 16, 32), 1024, 768, gptEnabled);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Space);
        Press(wizard, PhysicalKey.Enter);
        Press(wizard, PhysicalKey.Enter);
        Assert.AreEqual(SetupPage.Confirm, wizard.Page);
        return wizard;
    }

    private static void Press(SetupWizard wizard, PhysicalKey key)
    {
        wizard.Handle(new(InputEventType.KeyDown, KeyCode: (byte)key));
        wizard.Handle(new(InputEventType.KeyUp, KeyCode: (byte)key));
    }

    private static InstallService Service(Platform platform) =>
        new(platform, Efi, Kernel, "0.5.0", gptEnabled: true, random: Fill);

    private static void Fill(byte[] bytes) => RandomNumberGenerator.Fill(bytes);

    private static bool IsWrite(string call) =>
        call is "layout" or "format-esp" or "format-root" or "prepare-root" or "write-marker" || call.StartsWith("file ", StringComparison.Ordinal);

    private static void Run(IInstallJob job, List<int>? percents = null)
    {
        for (int guard = 0; !job.IsCompleted && guard < 1000; guard++)
        {
            percents?.Add(job.Percent);
            Assert.IsTrue(job.Step().IsOK(), "Schritt " + job.Stage);
        }
        percents?.Add(job.Percent);
    }

    private static ResultErr RunUntilFailure(IInstallJob job)
    {
        for (int guard = 0; guard < 1000; guard++)
        {
            var result = job.Step();
            if (!result.IsOK()) return result;
        }
        throw new AssertFailedException("Kein Fehler.");
    }

    /// <summary>Superblock, den Ext2FeatureGate als 4-KiB-ext2 mit sparse_super annimmt, mit waehlbarem s_state.</summary>
    internal static byte[] Superblock(ushort state)
    {
        // Gleiche Geometrie wie Ext2FeatureGateTests.Superblock: eine Gruppe, 128 Inodes, 2048 Bloecke.
        var block = new byte[1024];
        BitConverter.TryWriteBytes(block.AsSpan(0), 128u);
        BitConverter.TryWriteBytes(block.AsSpan(4), 2048u);
        BitConverter.TryWriteBytes(block.AsSpan(32), 32768u);
        BitConverter.TryWriteBytes(block.AsSpan(40), 128u);
        BitConverter.TryWriteBytes(block.AsSpan(96), 2u);                 // s_feature_incompat: filetype
        BitConverter.TryWriteBytes(block.AsSpan(24), 2u);                 // s_log_block_size: 4096
        BitConverter.TryWriteBytes(block.AsSpan(56), (ushort)0xEF53);     // s_magic
        BitConverter.TryWriteBytes(block.AsSpan(Ext2FeatureGate.StateOffset), state);
        BitConverter.TryWriteBytes(block.AsSpan(76), 1u);                 // s_rev_level: dynamic
        BitConverter.TryWriteBytes(block.AsSpan(88), (ushort)128);        // s_inode_size
        BitConverter.TryWriteBytes(block.AsSpan(100), 1u);                // s_feature_ro_compat: sparse_super
        return block;
    }

    private sealed class Platform : IInstallPlatform
    {
        public int Opens { get; private set; }
        public string? OpenFailure { get; init; }
        public RecordingTarget Target { get; } = new();

        public ResultObject<InstallCandidate[]> GetCandidates() => ResultObjectFactory.CreateSuccess<InstallCandidate[]>(
        [
            new(new InstallDisk("cd0", 64 * GiB, 1, false), true),
            new(new InstallDisk("klein", 32 * GiB - 512, 0, true), false),
            new(new InstallDisk("min", 32 * GiB, 1, false), false),
            new(new InstallDisk("max", 128 * GiB, 0, true), false),
            new(new InstallDisk("gross", 128 * GiB + 512, 0, true), false),
            new(new InstallDisk("sektor4k", 64 * GiB, 0, true, 4096), false),
        ]);

        public ResultObject<IInstallTarget> OpenTarget(string diskName) =>
            ResultObjectFactory.CreateSuccess<IInstallTarget>(new LegacyTarget());

        public ResultObject<IGptInstallTarget> OpenGptTarget(string diskName)
        {
            Opens++;
            return OpenFailure == null ? ResultObjectFactory.CreateSuccess<IGptInstallTarget>(Target)
                : ResultObjectFactory.CreateFailure<IGptInstallTarget>(ResultCode.Failure, OpenFailure);
        }

        public ResultErr Restart() => ResultErr.CreateSuccess();
    }

    private sealed class LegacyTarget : IInstallTarget
    {
        public ResultErr Partition() => ResultErr.CreateSuccess();
        public ResultErr Format() => ResultErr.CreateSuccess();
        public ResultErr Complete() => ResultErr.CreateSuccess();
        public ResultErr Abort() => ResultErr.CreateSuccess();
        public ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last) => ResultErr.CreateSuccess();
    }

    /// <summary>Zaehl-Fake: protokolliert jeden Aufruf in Reihenfolge und scheitert auf Wunsch bei genau einem.</summary>
    internal sealed class RecordingTarget : IGptInstallTarget
    {
        public List<string> Calls { get; } = [];
        public List<GptTable> Tables { get; } = [];
        public Queue<byte[]> Superblocks { get; } = new();
        public string? FailOn { get; set; }
        public string? AbortFailure { get; set; }
        public bool CorruptMarkerReadBack { get; set; }
        public string? Marker { get; private set; }
        public Guid? RootMarkerDisk { get; private set; }
        private string? pendingMarker;

        private ResultErr Record(string call)
        {
            Calls.Add(call);
            return call == FailOn ? ResultErr.CreateFailure(ResultCode.Failure, call + " fehlgeschlagen") : ResultErr.CreateSuccess();
        }

        public ResultErr WriteLayout(GptTable table) { Tables.Add(table); return Record("layout"); }
        public ResultErr FormatEsp() => Record("format-esp");
        public ResultErr FormatRoot() => Record("format-root");

        public ResultObject<byte[]> ReadRootSuperblock()
        {
            Calls.Add("superblock");
            return ResultObjectFactory.CreateSuccess(Superblocks.Count > 0 ? Superblocks.Dequeue() : Superblock(1));
        }

        public ResultErr PrepareRoot(Guid diskGuid)
        {
            RootMarkerDisk = diskGuid;
            return Record("prepare-root");
        }

        public ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last) =>
            last ? Record("file " + path) : ResultErr.CreateSuccess();

        public ResultErr FlushEsp() => Record("flush-esp");

        public ResultErr WriteMarker(string text)
        {
            var result = Record("write-marker");
            if (result.IsOK()) pendingMarker = text;
            return result;
        }

        public ResultObject<string> ReadMarker()
        {
            Calls.Add("read-marker");
            // Erst ein erfolgreich zurueckgelesener Marker gilt fuer den Test als auf der Platte stehend.
            string text = CorruptMarkerReadBack ? pendingMarker + "x" : pendingMarker!;
            if (!CorruptMarkerReadBack) Marker = text;
            return ResultObjectFactory.CreateSuccess(text);
        }

        public ResultErr Complete() => Record("complete");

        public ResultErr Abort()
        {
            Calls.Add("abort");
            return AbortFailure == null ? ResultErr.CreateSuccess() : ResultErr.CreateFailure(ResultCode.Failure, AbortFailure);
        }
    }

    private sealed class RecordingService : IInstallService
    {
        public List<string> Calls { get; } = [];
        public InstallDisk[] Disks { get; init; } = [];
        public string ProductVersion => "0.5.0";
        public ResultObject<InstallDisk[]> GetDisks() => ResultObjectFactory.CreateSuccess(Disks);

        public ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting)
        {
            Calls.Add($"legacy {diskName} {eraseExisting}");
            return ResultObjectFactory.CreateSuccess<IInstallJob>(new DoneJob());
        }

        public ResultObject<IInstallJob> BeginGpt(string diskName, bool eraseExisting)
        {
            Calls.Add("gpt " + diskName);
            return ResultObjectFactory.CreateSuccess<IInstallJob>(new DoneJob());
        }

        public ResultErr Restart() => ResultErr.CreateSuccess();
    }

    private sealed class DoneJob : IInstallJob
    {
        public InstallStage Stage => InstallStage.Completed;
        public int Percent => 100;
        public bool IsCompleted => true;
        public ResultErr Step() => ResultErr.CreateSuccess();
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
