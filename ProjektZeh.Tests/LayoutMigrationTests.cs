using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

/// <summary>Etappe 4.4 (Spec 2026-11-17-storage-e4-4-migration.md §5); Nummern in den Namen folgen der Testliste dort.</summary>
[TestClass]
public sealed class LayoutMigrationTests
{
    private const string Anna = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Guid Disk = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private string source = "";
    private readonly List<string> log = [];
    private readonly List<IDisposable> cleanup = [];

    [TestInitialize]
    public void Setup()
    {
        source = Path.Combine(Path.GetTempPath(), "zehos-migration-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var item in cleanup) item.Dispose();
        if (Directory.Exists(source)) Directory.Delete(source, true);
    }

    // ---------------------------------------------------------------- Hilfen

    private static string Accounts() =>
        AccountService.Header + $"{Anna}|anna|admin|{AccountService.UnknownAccountHash}\n{Bob}|bob|user|{AccountService.UnknownAccountHash}\n";

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Acl(params string[] lines) => "ZEHOS-SECURITY-1\n" + string.Concat(lines.Select(line => line + "\n"));
    private static string P(string path, string owner) => $"P|{Encode(path)}|{owner}";
    private static string G(string path, string user, int permission) => $"G|{Encode(path)}|{user}|{permission}";

    private static string StandardSecurity() => Accounts() + AccountService.Separator + Acl(
        P("/data", Anna), G("/data", "*", 1),
        P("/data/home", Anna), G("/data/home", "*", 1),
        P("/data/home/anna", Anna),
        P("/data/Home/Anna/Bild.BMP", Anna), G("/data/Home/Anna/Bild.BMP", Bob, 1),
        P("/data/home/bob", Bob),
        P("/data/notizen.txt", Anna),
        P("/data/verschwunden.txt", Anna));

    private void Write(string relative, string content)
    {
        string path = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Typischer Legacy-Bestand mit jeder Zeile der Zuordnungstabelle.</summary>
    private void StandardSource()
    {
        Write(".zehos-security.db", StandardSecurity());
        Write(".zehos-commands.db", "ZEHOS-COMMANDS-1\nuser|admin\n");
        Write(".zehos-security-recovery-0123.db", "alt");
        Write(".zehos-update.conf", "source=https://example.invalid/\n");
        Write(".zehos-packages/catalog.signed", "katalog");
        Write(".zehos-packages/apps/doom/1.0.0/doom.wasm", "wasm");
        Write(".zehos-apps/doom/" + Anna + "/save.dat", "spielstand");
        Write("home/anna/zehos-desktop.conf", "version=2\ndark=True\nwallpaper=4\nimage=/data/home/anna/bild.bmp\n");
        Write("home/anna/bild.bmp", "BM");
        Write("home/bob/brief.txt", "hallo");
        Write("notizen.txt", "notiz");
        Write("boot/zehos.elf", "kernel");
        Write(".zehos-security.db.zehos-old.bak", "x");
        Write("dokumente/.zehos-replace.tmp", "x");
    }

    [TestMethod]
    public void SourceDirectoryOverTheParserLimitConflictsBeforeAnyTargetWrite()
    {
        StandardSource();
        string bulk = Path.Combine(source, "bulk");
        Directory.CreateDirectory(bulk);
        for (int i = 0; i < 8191; i++) File.Create(Path.Combine(bulk, $"f{i:D4}.txt")).Dispose();
        var target = Target();
        var result = Run(target);
        StringAssert.Contains(result.Status.Message, "PlanConflict");
        Assert.IsFalse(Writes(target), string.Join(", ", target.Operations));
        Assert.AreEqual(LayoutState.Fresh, target.Marker().State);
    }

    private MigrationPlan Plan(params string[] accounts)
    {
        var inventory = MigrationInventory.Read(source);
        Assert.IsTrue(inventory.IsOK(), inventory.Status.Message);
        return MigrationPlan.Create(inventory.Value!, accounts.Length == 0 ? ["anna", "bob"] : accounts, SystemPaths.Legacy, SystemPaths.Root);
    }

    private static Dictionary<string, string> Targets(MigrationPlan plan) => plan.Files.ToDictionary(file => file.Source, file => file.Target);

    private FakeMigrationTarget Target()
    {
        var target = new FakeMigrationTarget(Disk);
        cleanup.Add(target);
        return target;
    }

    private sealed class Lock : IDisposable
    {
        public int Taken { get; private set; }
        public int Released { get; private set; }
        public Lock Take() { Taken++; return this; }
        public void Dispose() => Released++;
    }

    [TestMethod]
    public void CancellationBeforeReadyLeavesTheTargetUnreadyAndReleasesTheSource()
    {
        StandardSource();
        foreach (MigrationStep step in Enum.GetValues<MigrationStep>())
        {
            var target = Target();
            var sourceLock = new Lock();
            using var cancellation = new CancellationTokenSource();
            var result = LayoutMigration.Run(source, target, () => sourceLock.Take(), log.Add,
                current => { if (current == step) cancellation.Cancel(); }, cancellation.Token);
            Assert.AreEqual(1, sourceLock.Released, step.ToString());
            if (step == MigrationStep.Ready)
                Assert.IsTrue(result.IsOK(), "Ein bereits abgeschlossener Commit wird nicht rueckwirkend abgebrochen.");
            else
            {
                Assert.IsFalse(result.IsOK(), step.ToString());
                StringAssert.Contains(result.Status.Message, "cause='MigrationCancelled'");
                Assert.AreNotEqual(LayoutState.Ready, target.Marker().State, step.ToString());
            }
        }
    }

    [TestMethod]
    public void AlreadyCancelledMigrationDoesNotWriteAndReleasesTheSource()
    {
        StandardSource();
        var target = Target();
        var sourceLock = new Lock();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = LayoutMigration.Run(source, target, () => sourceLock.Take(), log.Add, cancellation: cancellation.Token);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "cause='MigrationCancelled'");
        Assert.IsFalse(Writes(target));
        Assert.AreEqual(1, sourceLock.Released);
    }

    [TestMethod]
    public void RealFileTargetRepeatsAnInterruptedMigrationWithOnlyKnownBackups()
    {
        StandardSource();
        string before = SourceHash();
        string targetRoot = Path.Combine(Path.GetTempPath(), "zehos-real-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetRoot);
        try
        {
            foreach (string path in LayoutMigration.InstallerEntries)
                if (path != LayoutMarker.Path) Directory.CreateDirectory(Path.Combine(targetRoot, path[1..].Replace('/', Path.DirectorySeparatorChar)));
            string markerPath = Path.Combine(targetRoot, "etc", "zehos", ".zehos-layout");
            File.WriteAllText(markerPath, new LayoutMarker(LayoutState.Fresh, Disk).Serialize());
            var target = new FileMigrationTarget(targetRoot, () => ResultObjectFactory.CreateSuccess(1L << 30),
                () => ResultObjectFactory.CreateSuccess(100_000L), ResultErr.CreateSuccess, (_, _) => ResultErr.CreateSuccess());
            var sourceLock = new Lock();
            using var cancellation = new CancellationTokenSource();
            var first = LayoutMigration.Run(source, target, () => sourceLock.Take(), log.Add,
                step => { if (step == MigrationStep.Copied) cancellation.Cancel(); }, cancellation.Token);
            Assert.IsFalse(first.IsOK());
            using (var reader = new StreamReader(target.ReadBack(LayoutMarker.Path).Value!))
                Assert.AreEqual(LayoutState.Migrating, LayoutMarker.Parse(reader.ReadToEnd()).Value!.State);

            string note = Path.Combine(targetRoot, "srv", "legacy-data", "notizen.txt");
            File.Move(note, note + IMigrationTarget.BackupSuffix);
            File.Move(markerPath, markerPath + IMigrationTarget.BackupSuffix);
            var second = LayoutMigration.Run(source, target, () => sourceLock.Take(), log.Add);
            Assert.IsTrue(second.IsOK(), second.Status.Message + "\n" + string.Join("\n", log));
            using (var reader = new StreamReader(target.ReadBack(LayoutMarker.Path).Value!))
                Assert.AreEqual(LayoutState.Ready, LayoutMarker.Parse(reader.ReadToEnd()).Value!.State);
            Assert.IsFalse(Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories).Any(path =>
                path.EndsWith(IMigrationTarget.BackupSuffix, StringComparison.Ordinal) || path.EndsWith(IMigrationTarget.TemporarySuffix, StringComparison.Ordinal)));
            Assert.AreEqual(before, SourceHash());
            Assert.AreEqual(2, sourceLock.Released);
        }
        finally { Directory.Delete(targetRoot, true); }
    }

    [TestMethod]
    public void SourceFileCollidingWithTheBackupSuffixConflictsBeforeWriting()
    {
        StandardSource();
        File.WriteAllText(Path.Combine(source, "note" + IMigrationTarget.BackupSuffix), "user content");
        var target = Target();
        var result = Run(target);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "PlanConflict");
        Assert.IsFalse(Writes(target));
    }

    private ResultObject<MigrationReport> Run(FakeMigrationTarget target, Lock? sourceLock = null, Action<MigrationStep>? progress = null)
    {
        sourceLock ??= new Lock();
        return LayoutMigration.Run(source, target, sourceLock.Take, log.Add, progress);
    }

    private static bool Writes(FakeMigrationTarget target) => target.Operations.Any(operation => !operation.StartsWith("sync", StringComparison.Ordinal));

    private string SourceHash()
    {
        var text = new StringBuilder();
        foreach (string file in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).OrderBy(name => name, StringComparer.Ordinal))
            text.Append(Path.GetRelativePath(source, file)).Append(' ')
                .Append(File.Exists(file) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) : "dir").Append('\n');
        return text.ToString();
    }

    // ---------------------------------------------------------------- Plan

    [TestMethod]
    public void T01_EveryTableRowMapsToItsTarget()
    {
        StandardSource();
        var plan = Plan();
        Assert.AreEqual(0, plan.Conflicts.Count, string.Join("; ", plan.Conflicts));
        var targets = Targets(plan);
        Assert.AreEqual("/etc/zehos/.zehos-security.db", targets[".zehos-security.db"]);
        Assert.AreEqual("/etc/zehos/.zehos-commands.db", targets[".zehos-commands.db"]);
        Assert.AreEqual("/etc/zehos/.zehos-security-recovery-0123.db", targets[".zehos-security-recovery-0123.db"]);
        Assert.AreEqual("/etc/zehos/update.conf", targets[".zehos-update.conf"]);
        Assert.AreEqual("/var/lib/zehos/packages/catalog.signed", targets[".zehos-packages/catalog.signed"]);
        Assert.AreEqual("/var/lib/zehos/packages/apps/doom/1.0.0/doom.wasm", targets[".zehos-packages/apps/doom/1.0.0/doom.wasm"]);
        Assert.AreEqual($"/var/lib/zehos/apps/doom/{Anna}/save.dat", targets[$".zehos-apps/doom/{Anna}/save.dat"]);
        Assert.AreEqual("/home/anna/.config/zehos/desktop.conf", targets["home/anna/zehos-desktop.conf"]);
        Assert.AreEqual("/home/anna/bild.bmp", targets["home/anna/bild.bmp"]);
        Assert.AreEqual("/home/bob/brief.txt", targets["home/bob/brief.txt"]);
        Assert.AreEqual("/srv/legacy-data/notizen.txt", targets["notizen.txt"]);
        Assert.AreEqual(PlanContent.SecurityDatabase, plan.Files.Single(file => file.Source == ".zehos-security.db").Content);
        Assert.AreEqual(PlanContent.DesktopSettings, plan.Files.Single(file => file.Source == "home/anna/zehos-desktop.conf").Content);
        CollectionAssert.IsSubsetOf(new[] { "/home/anna/.config", "/home/anna/.config/zehos", "/srv", "/srv/legacy-data" }, plan.Directories.ToArray());
        Assert.IsTrue(plan.IsCreatedDirectory("/home/anna/.config"));
        Assert.IsFalse(plan.IsCreatedDirectory("/home/anna"), "home/anna stammt aus der Quelle");
    }

    [TestMethod]
    public void T01_HomeTakesTheAccountNameAndSourceNamesTheirListedSpelling()
    {
        Write(".zehos-security.db", StandardSecurity());
        Write("Home/Anna/Bild.BMP", "BM");
        var plan = Plan();
        Assert.AreEqual(0, plan.Conflicts.Count, string.Join("; ", plan.Conflicts));
        Assert.AreEqual("/home/anna/Bild.BMP", Targets(plan)["Home/Anna/Bild.BMP"], "Home-Ordner nach Konto, Dateiname wie aufgelistet");
    }

    [TestMethod]
    public void T02_BootTemporaryAndBackupFilesAreExcludedAndLogged()
    {
        StandardSource();
        var plan = Plan();
        var sources = plan.Files.Select(file => file.Source).ToArray();
        CollectionAssert.DoesNotContain(sources, "boot/zehos.elf");
        CollectionAssert.DoesNotContain(sources, ".zehos-security.db.zehos-old.bak");
        CollectionAssert.DoesNotContain(sources, "dokumente/.zehos-replace.tmp");
        Assert.IsTrue(plan.Log.Any(line => line.Contains("boot", StringComparison.Ordinal)));
        Assert.IsTrue(plan.Log.Any(line => line.Contains(".zehos-replace.tmp", StringComparison.Ordinal)));
        Assert.IsFalse(plan.Directories.Any(directory => directory.Contains("boot", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void T03_UnknownSystemFileIsAConflictAndNothingIsWritten()
    {
        StandardSource();
        Write(".zehos-foo", "?");
        Write("dokumente/.zehos-bar", "?");
        var plan = Plan();
        Assert.AreEqual(2, plan.Conflicts.Count, string.Join("; ", plan.Conflicts));

        var target = Target();
        var result = Run(target);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "PlanConflict");
        Assert.IsFalse(Writes(target), string.Join(", ", target.Operations));
        Assert.AreEqual(LayoutState.Fresh, target.Marker().State);
    }

    [TestMethod]
    public void T04_HomeWithoutAccountGoesToLegacyData()
    {
        StandardSource();
        Write("home/carl/datei.txt", "c");
        var plan = Plan();
        Assert.AreEqual(0, plan.Conflicts.Count);
        Assert.AreEqual("/srv/legacy-data/home/carl/datei.txt", Targets(plan)["home/carl/datei.txt"]);
        Assert.IsTrue(plan.Log.Any(line => line.Contains("home/carl", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void T05_TwoSourcesOnOneTargetAreAConflict()
    {
        StandardSource();
        Write("home/anna/.config/zehos/desktop.conf", "version=2\n");
        var plan = Plan();
        Assert.AreEqual(1, plan.Conflicts.Count, string.Join("; ", plan.Conflicts));
        StringAssert.Contains(plan.Conflicts[0], "/home/anna/.config/zehos/desktop.conf");
    }

    [TestMethod]
    public void T06_SpaceCountsWholeBlocksInodesAndReserve()
    {
        Write(".zehos-security.db", StandardSecurity());
        // Genug Dateien, dass 10 % ueber den Mindestreserven liegen.
        for (int i = 0; i < 3000; i++) Write($"viele/{i:D4}.txt", "x");
        var plan = Plan();
        // Verzeichnisse: /etc, /etc/zehos, /srv, /srv/legacy-data, /srv/legacy-data/viele.
        Assert.AreEqual(5, plan.Directories.Count, string.Join(", ", plan.Directories));
        long bytes = (5 + 3001) * 4096L, inodes = 5 + 3001;
        Assert.AreEqual(bytes + bytes / 10, plan.RequiredBytes, "1-Byte-Dateien belegen je einen 4-KiB-Block");
        Assert.AreEqual(inodes + inodes / 10, plan.RequiredInodes);

        var target = Target();
        target.Bytes = plan.RequiredBytes - 1;
        var result = Run(target);
        StringAssert.Contains(result.Status.Message, "NoSpace");
        Assert.IsFalse(Writes(target));

        target = Target();
        target.Inodes = plan.RequiredInodes - 1;
        result = Run(target);
        StringAssert.Contains(result.Status.Message, "NoInodes");
        Assert.IsFalse(Writes(target));

        target = Target();
        target.Bytes = plan.RequiredBytes;
        target.Inodes = plan.RequiredInodes;
        Assert.IsTrue(Run(target).IsOK(), "genau ausreichend");
    }

    [TestMethod]
    public void T06_SmallSourceReservesAtLeastOneMebibyte()
    {
        Write(".zehos-security.db", StandardSecurity());
        var plan = Plan();
        long bytes = (plan.Directories.Count + 1) * 4096L;
        Assert.AreEqual(bytes + 1024 * 1024, plan.RequiredBytes);
        Assert.AreEqual(plan.Directories.Count + 1 + 64, plan.RequiredInodes);
    }

    [TestMethod]
    public void T07_AccountNameThatIsNoPathComponentIsAConflict()
    {
        Write(".zehos-security.db", StandardSecurity());
        var plan = Plan("anna", "bob.");
        Assert.AreEqual(1, plan.Conflicts.Count, string.Join("; ", plan.Conflicts));
        StringAssert.Contains(plan.Conflicts[0], "bob.");
    }

    [TestMethod]
    public void T07_TargetComponentWithForbiddenCharacterIsAConflict()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Windows erlaubt '|' nicht in Dateinamen; der Fall entsteht dort nicht.");
        Write(".zehos-security.db", StandardSecurity());
        Write("a|b.txt", "x");
        var plan = Plan();
        Assert.AreEqual(1, plan.Conflicts.Count, string.Join("; ", plan.Conflicts));
        StringAssert.Contains(plan.Conflicts[0], "a|b.txt");
    }

    [TestMethod]
    public void T07_NameLongerThanExt2AllowsIsAConflict()
    {
        Write(".zehos-security.db", StandardSecurity());
        // FAT erlaubt 255 UTF-16-Zeichen; Linux-Hosts lehnen > 255 Byte schon beim Anlegen ab, daher die Regel auch direkt pruefen.
        // Dateien brauchen Platz fuer die Temp-Endung (10 Byte): hoechstens 245 Byte; Verzeichnisse 255 Byte.
        Write(new string('\u00e4', 120) + ".txt", "x");
        Assert.AreEqual(0, Plan().Conflicts.Count, "244 Byte passen");
        Write(new string('\u00e4', 121) + ".txt", "x");
        Assert.AreEqual(1, Plan().Conflicts.Count, "246 Byte: Temp-Name waere zu lang");
        Assert.AreEqual(245, MigrationPlan.MaximumFileNameBytes);
        Assert.IsTrue(MigrationPlan.ValidTarget("/srv/" + new string('a', 245), SystemPaths.Root, isFile: true));
        Assert.IsFalse(MigrationPlan.ValidTarget("/srv/" + new string('a', 246), SystemPaths.Root, isFile: true));
        Assert.IsFalse(MigrationPlan.ValidTarget("/srv/brief.txt.zehos-tmp", SystemPaths.Root, isFile: true), "kollidiert mit dem Temp-Namen von brief.txt");
        Assert.IsTrue(MigrationPlan.ValidTarget("/srv/ordner.zehos-tmp", SystemPaths.Root, isFile: false));
        Assert.IsTrue(MigrationPlan.ValidTarget("/srv/" + new string('a', 255), SystemPaths.Root, isFile: false));
        Assert.IsFalse(MigrationPlan.ValidTarget("/srv/" + new string('a', 256), SystemPaths.Root, isFile: false));
        Assert.IsTrue(MigrationPlan.ValidTarget("/srv/" + new string('a', 255) + "/x", SystemPaths.Root, isFile: true), "Verzeichnisteil darf 255 Byte haben");
    }

    [TestMethod]
    public void T07_FileOfTwoGibibytesIsAConflict()
    {
        Write(".zehos-security.db", StandardSecurity());
        string path = Path.Combine(source, "gross.img");
        using (var file = File.Create(path)) file.SetLength(MigrationPlan.MaximumFileBytes);
        Assert.AreEqual(0, Plan().Conflicts.Count, "2 GiB - 1 passt");
        using (var file = File.OpenWrite(path)) file.SetLength(MigrationPlan.MaximumFileBytes + 1);
        var plan = Plan();
        Assert.AreEqual(1, plan.Conflicts.Count);
        StringAssert.Contains(plan.Conflicts[0], "gross.img");
    }

    // ---------------------------------------------------------------- ACL

    private (string Text, MigrationPlan Plan) Rewrite()
    {
        StandardSource();
        var plan = Plan();
        var result = MigrationAcl.Rewrite(StandardSecurity(), plan, log.Add);
        Assert.IsTrue(result.IsOK(), result.Status.Message);
        return (result.Value!, plan);
    }

    private static Dictionary<string, (string Owner, List<string> Grants)> Entries(string text)
    {
        var entries = new Dictionary<string, (string, List<string>)>(StringComparer.Ordinal);
        string acl = text[(text.IndexOf(AccountService.Separator, StringComparison.Ordinal) + AccountService.Separator.Length)..];
        foreach (string line in acl.Split('\n').Skip(1).Where(line => line.Length != 0))
        {
            string[] parts = line.Split('|');
            string path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            if (parts[0] == "P") entries.Add(path, (parts[2], []));
            else entries[path].Item2.Add(parts[2] + ":" + parts[3]);
        }
        return entries;
    }

    [TestMethod]
    public void T08_AccountLinesStayByteIdentical()
    {
        var (text, _) = Rewrite();
        StringAssert.StartsWith(text, Accounts() + AccountService.Separator + "ZEHOS-SECURITY-1\n");
        Assert.AreEqual(MigrationAcl.ReadAccountNames(StandardSecurity()).Value!.Length, 2);
    }

    [TestMethod]
    public void T09_AclPathIsMappedToTheListedName()
    {
        Write(".zehos-security.db", StandardSecurity());
        Write("home/anna/bild.bmp", "BM");
        var plan = Plan();
        var text = MigrationAcl.Rewrite(StandardSecurity(), plan, log.Add).Value!;
        var entries = Entries(text);
        Assert.IsTrue(entries.ContainsKey("/home/anna/bild.bmp"), string.Join(", ", entries.Keys));
        Assert.AreEqual(Anna, entries["/home/anna/bild.bmp"].Owner);
        CollectionAssert.AreEqual(new[] { Bob + ":1" }, entries["/home/anna/bild.bmp"].Grants);
    }

    [TestMethod]
    public void T10_EntryWithoutFileIsLoggedAndDropped()
    {
        var (text, _) = Rewrite();
        Assert.IsFalse(Entries(text).Keys.Any(path => path.Contains("verschwunden", StringComparison.Ordinal)));
        Assert.IsTrue(log.Any(line => line.Contains("/data/verschwunden.txt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void T11_RootRightsAndCreatedDirectoriesFollowTheRules()
    {
        var (text, _) = Rewrite();
        var entries = Entries(text);
        Assert.AreEqual(Anna, entries["/"].Owner);
        CollectionAssert.AreEqual(new[] { "*:1" }, entries["/"].Grants);
        CollectionAssert.AreEqual(new[] { "*:1" }, entries["/home"].Grants);
        foreach (string path in new[] { "/srv", "/srv/legacy-data" })
        {
            Assert.AreEqual(Anna, entries[path].Owner, path);
            CollectionAssert.AreEqual(new[] { "*:1" }, entries[path].Grants, path);
        }
        Assert.IsFalse(entries.Keys.Any(path => path.StartsWith("/etc", StringComparison.Ordinal) || path.StartsWith("/var", StringComparison.Ordinal)));
        Assert.AreEqual(Anna, entries["/home/anna/.config"].Owner);
        Assert.AreEqual(Anna, entries["/home/anna/.config/zehos"].Owner);
        Assert.AreEqual(Bob, entries["/home/bob"].Owner);
        var paths = entries.Keys.ToList();
        CollectionAssert.AreEqual(paths.OrderBy(path => path, StringComparer.Ordinal).ToList(), paths, "Pfade Ordinal sortiert");
    }

    [TestMethod]
    public void T11_SourceDirectoryWithoutAclEntryInheritsNothing()
    {
        StandardSource();
        Write("dokumente/brief.txt", "b");
        var plan = Plan();
        var entries = Entries(MigrationAcl.Rewrite(StandardSecurity(), plan, log.Add).Value!);
        Assert.IsFalse(entries.ContainsKey("/srv/legacy-data/dokumente"), "Quellverzeichnis ohne Eintrag bleibt ohne Eintrag (5a.3)");
    }

    [TestMethod]
    public void T12_DamagedSecurityTextIsRejected()
    {
        StandardSource();
        var plan = Plan();
        string head = Accounts() + AccountService.Separator;
        foreach (string damaged in new[]
        {
            head + Acl(P("/data", Anna), "X|abc|def"),
            head + Acl(P("/data", Anna), "P|%%%|" + Anna),
            head + Acl(G("/data/notizen.txt", Bob, 1), P("/data", Anna)),
            head + Acl(P("/data", Anna), G("/data", Bob, 7)),
            head + "KAPUTT\n",
            Accounts() + Acl(P("/data", Anna)),
            head + Acl(P("/data/notizen.txt", Anna)),
        })
        {
            var result = MigrationAcl.Rewrite(damaged, plan, log.Add);
            Assert.IsFalse(result.IsOK(), damaged);
        }
    }

    [TestMethod]
    public void T12_ResultLoadsIntoFileAccessControlOnLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("FileAccessControl nutzt Path.GetFullPath; die Rueckleseprobe gilt nur auf Linux-Hosts (Spec §3.6).");
        var (text, _) = Rewrite();
        string acl = text[(text.IndexOf(AccountService.Separator, StringComparison.Ordinal) + AccountService.Separator.Length)..];
        var access = new FileAccessControl("/", StringComparison.Ordinal);
        var restored = access.Restore(acl);
        Assert.IsTrue(restored.IsOK(), restored.Message);
        Assert.IsTrue(access.Contains("/home/anna/.config/zehos"));
    }

    [TestMethod]
    public void T12a_DesktopBackgroundIsMappedOrCleared()
    {
        StandardSource();
        var plan = Plan();
        string mapped = MigrationAcl.RewriteDesktop("version=2\nimage=/data/home/anna/bild.bmp\ndark=False\n", plan, log.Add);
        Assert.AreEqual("version=2\nimage=/home/anna/bild.bmp\ndark=False\n", mapped);
        string cleared = MigrationAcl.RewriteDesktop("image=/data/gibtsnicht.bmp\n", plan, log.Add);
        Assert.AreEqual("image=\n", cleared);
        Assert.IsTrue(log.Any(line => line.Contains("/data/gibtsnicht.bmp", StringComparison.Ordinal)));
        Assert.AreEqual("image=bild.bmp\n", MigrationAcl.RewriteDesktop("image=bild.bmp\n", plan, log.Add), "relativ bleibt");
        Assert.AreEqual("image=\n", MigrationAcl.RewriteDesktop("image=/data/.zehos-packages/catalog.signed\n", plan, log.Add), "Systemdaten");
    }

    // ---------------------------------------------------------------- Journal und Marker

    private static readonly string SourceSha = new('c', 64);
    private static readonly string FileSha = new('d', 64);

    private static string JournalText() =>
        MigrationJournal.HeaderLine(SourceSha) + MigrationJournal.DirectoryLine("/srv") + MigrationJournal.DirectoryLine("/srv/legacy-data") +
        MigrationJournal.WriteLine("/srv/legacy-data/a") + MigrationJournal.DoneLine("/srv/legacy-data/a", FileSha) + MigrationJournal.WriteLine("/srv/legacy-data/b");

    [TestMethod]
    public void T13_JournalRoundTripsAndToleratesADamagedTail()
    {
        var journal = MigrationJournal.Parse(JournalText());
        Assert.IsTrue(journal.IsOK(), journal.Status.Message);
        Assert.AreEqual(SourceSha, journal.Value!.SourceHash);
        CollectionAssert.AreEqual(new[] { "/srv", "/srv/legacy-data" }, journal.Value.Directories.ToArray());
        CollectionAssert.AreEqual(new[] { "/srv/legacy-data/a", "/srv/legacy-data/b" }, journal.Value.Files.ToArray());

        string text = JournalText();
        foreach (string tail in new[] { "W abc", "\0\0\0\0", "\0\0\n\0\0\n", "F " + "!!!!\n", "W " + Convert.ToBase64String([0xff, 0xfe]) + "\n" })
        {
            var parsed = MigrationJournal.Parse(text + tail);
            Assert.IsTrue(parsed.IsOK(), $"Schwanz {tail.Replace("\0", "\\0", StringComparison.Ordinal)}: {parsed.Status.Message}");
            Assert.AreEqual(2, parsed.Value!.Files.Count);
        }
        string truncated = text[..^5];
        Assert.AreEqual(1, MigrationJournal.Parse(truncated).Value!.Files.Count, "abgeschnittene letzte Zeile zaehlt nicht");
    }

    [TestMethod]
    public void T13_DamagedMiddleLineOrHeaderInvalidatesTheJournal()
    {
        string[] lines = JournalText().Split('\n');
        lines[2] = "X kaputt";
        Assert.IsFalse(MigrationJournal.Parse(string.Join('\n', lines)).IsOK());
        Assert.IsFalse(MigrationJournal.Parse("ZEHOS-MIGRATION-2 " + SourceSha + "\n" + MigrationJournal.WriteLine("/a")).IsOK(), "falscher Kopf vor gueltiger Zeile");
        foreach (string unfinished in new[] { "", "ZEHOS-MIG", "ZEHOS-MIGRATION-2 " + SourceSha + "\n", "\0\0\n\0" })
        {
            var empty = MigrationJournal.Parse(unfinished);
            Assert.IsTrue(empty.IsOK(), "unvollstaendiger Kopf ohne Folgezeilen = noch nichts begonnen");
            Assert.IsNull(empty.Value!.SourceHash);
            Assert.AreEqual(0, empty.Value.Files.Count + empty.Value.Directories.Count);
        }
        Assert.IsFalse(MigrationJournal.Parse(MigrationJournal.HeaderLine(SourceSha) + "D " + Encode("relativ") + "\n" + MigrationJournal.WriteLine("/a")).IsOK());
    }

    [TestMethod]
    public void T14_MarkerRoundTripsEveryState()
    {
        foreach (var marker in new[]
        {
            new LayoutMarker(LayoutState.Fresh, Disk),
            new LayoutMarker(LayoutState.Migrating, Disk),
            new LayoutMarker(LayoutState.Ready, Disk),
            new LayoutMarker(LayoutState.Ready, Disk, SourceSha, 42),
        })
        {
            var parsed = LayoutMarker.Parse(marker.Serialize());
            Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
            Assert.AreEqual(marker, parsed.Value);
        }
        StringAssert.StartsWith(new LayoutMarker(LayoutState.Fresh, Disk).Serialize(), "format=1\nstate=fresh\ndisk=" + Disk.ToString("D") + "\ncrc32=");
    }

    [TestMethod]
    public void T14_InvalidMarkersAreRejected()
    {
        string good = new LayoutMarker(LayoutState.Ready, Disk, SourceSha, 1).Serialize();
        Assert.IsFalse(LayoutMarker.Parse(good.Replace("files=1", "files=2", StringComparison.Ordinal)).IsOK(), "CRC falsch");
        Assert.IsFalse(LayoutMarker.Parse(Seal("format=1\nstate=fresh\ndisk=" + Disk + "\n" + new string('x', 500) + "\n")).IsOK(), "zu gross");
        Assert.IsFalse(LayoutMarker.Parse(Seal("format=1\nstate=fresh\ndisk=" + Disk + "\nextra=1\n")).IsOK(), "unbekanntes Feld");
        Assert.IsFalse(LayoutMarker.Parse(Seal("format=1\nstate=busy\ndisk=" + Disk + "\n")).IsOK(), "unbekannter Zustand");
        Assert.IsFalse(LayoutMarker.Parse(Seal("format=1\nstate=migrating\ndisk=" + Disk + $"\nsource={SourceSha}\nfiles=1\n")).IsOK(), "Quelle nur bei ready");
        Assert.IsFalse(LayoutMarker.Parse(Seal("format=2\nstate=fresh\ndisk=" + Disk + "\n")).IsOK(), "Format");
        Assert.IsFalse(LayoutMarker.Parse(Seal("format=1\nstate=fresh\ndisk=" + Guid.Empty + "\n")).IsOK(), "leere GUID");
        Assert.IsFalse(LayoutMarker.Parse(good[..^3]).IsOK(), "abgeschnitten");
    }

    private static string Seal(string body) => ProjektZeh.Storage.Partitioning.ChecksummedText.Seal(body);

    // ---------------------------------------------------------------- Ablauf

    [TestMethod]
    public void T15_SuccessCopiesEverythingAndMarksReadyLast()
    {
        StandardSource();
        var target = Target();
        var sourceLock = new Lock();
        var result = Run(target, sourceLock);
        Assert.IsTrue(result.IsOK(), result.Status.Message);

        var plan = Plan();
        foreach (var file in plan.Files.Where(file => file.Content == PlanContent.Copy))
            Assert.AreEqual(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(target.Host(file.Target)))), file.Target);
        StringAssert.Contains(File.ReadAllText(target.Host("/home/anna/.config/zehos/desktop.conf")), "image=/home/anna/bild.bmp");
        StringAssert.StartsWith(File.ReadAllText(target.Host("/etc/zehos/.zehos-security.db")), Accounts());

        var marker = target.Marker();
        Assert.AreEqual(new LayoutMarker(LayoutState.Ready, Disk, result.Value!.SourceHash, plan.Files.Count), marker);
        var operations = target.Operations;
        Assert.AreEqual("write " + LayoutMarker.Path, operations[^3], "ready als letzter Schreibzugriff");
        CollectionAssert.AreEqual(new[] { "sync", "sync" }, operations.TakeLast(2).ToArray());
        CollectionAssert.AreEqual(new[] { "sync", "sync" }, operations.Skip(operations.Count - 5).Take(2).ToArray(), "Sync-Schranke vor ready");
        Assert.IsTrue(operations[^6].StartsWith("journal F ", StringComparison.Ordinal), "davor nur Kopieren");
        Assert.AreEqual(1, operations.Count(operation => operation == "write " + LayoutMarker.Path) - 1, "genau ein Schreiben vor ready (migrating)");
        Assert.AreEqual(1, sourceLock.Taken);
        Assert.AreEqual(1, sourceLock.Released);
        Assert.IsFalse(target.Operations.Any(operation => operation.StartsWith("mkdir /etc", StringComparison.Ordinal)), "Installerverzeichnisse bleiben unberuehrt");
    }

    [TestMethod]
    public void T16_IntentIsJournaledBeforeEachFileAndDoneAfter()
    {
        StandardSource();
        var target = Target();
        Assert.IsTrue(Run(target).IsOK());
        var operations = target.Operations;
        int migrating = operations.IndexOf("write " + LayoutMarker.Path);
        foreach (var file in Plan().Files)
        {
            int intent = operations.IndexOf("journal W " + Encode(file.Target));
            int write = operations.IndexOf("write " + file.Target);
            int done = operations.FindIndex(operation => operation.StartsWith("journal F " + Encode(file.Target) + " ", StringComparison.Ordinal));
            Assert.IsTrue(migrating < intent && intent < write && write < done, $"{file.Target}: {intent} {write} {done}");
        }
        foreach (string directory in Plan().Directories.Where(directory => !LayoutMigration.InstallerEntries.Contains(directory)))
            Assert.IsTrue(operations.IndexOf("journal D " + Encode(directory)) < operations.IndexOf("mkdir " + directory), directory);
    }

    [TestMethod]
    public void T17_AbortAfterAnyOperationNeverMarksReadyAndReleasesTheLock()
    {
        StandardSource();
        var reference = Target();
        Assert.IsTrue(Run(reference).IsOK());
        // Nach dem Schreiben von "ready" folgen nur noch die beiden Sync-Aufrufe; ihr Fehlschlag meldet einen Fehler, der Marker
        // steht dann aber schon (Etappe 4 §2: Sync danach).
        int count = reference.Operations.LastIndexOf("write " + LayoutMarker.Path);

        for (int stop = 0; stop <= count; stop++)
        {
            var target = Target();
            int seen = 0;
            target.FailWhen = _ => seen++ == stop;
            var sourceLock = new Lock();
            var result = Run(target, sourceLock);
            Assert.IsFalse(result.IsOK(), $"Abbruch bei Operation {stop}");
            Assert.AreNotEqual(LayoutState.Ready, target.Marker().State, $"Abbruch bei {stop}");
            Assert.AreEqual(sourceLock.Taken, sourceLock.Released, $"Sperre bei {stop}");

            target.FailWhen = null;
            var repeated = Run(target);
            Assert.IsTrue(repeated.IsOK(), $"Wiederholung nach Abbruch bei {stop}: {repeated.Status.Message}");
            CollectionAssert.AreEqual(reference.Snapshot(), target.Snapshot(), $"Abbruch bei {stop}");
        }
    }

    [TestMethod]
    public void T17_AbortInEveryStepReleasesTheLock()
    {
        StandardSource();
        foreach (MigrationStep step in Enum.GetValues<MigrationStep>())
        {
            var target = Target();
            var sourceLock = new Lock();
            Assert.ThrowsException<InvalidOperationException>(() => Run(target, sourceLock, current =>
            {
                if (current == step) throw new InvalidOperationException("Abbruch");
            }));
            Assert.AreEqual(1, sourceLock.Released, step.ToString());
            if (step != MigrationStep.Ready) Assert.AreNotEqual(LayoutState.Ready, target.Marker().State, step.ToString());
        }
    }

    [TestMethod]
    public void T18_NoSpaceOrInodesWhileCopyingAbortsWithMigratingMarker()
    {
        StandardSource();
        foreach (string refused in new[] { "write /home/bob/brief.txt", "mkdir /srv/legacy-data" })
        {
            var target = Target();
            target.RefuseWhen = operation => operation == refused;
            var result = Run(target);
            Assert.IsFalse(result.IsOK());
            StringAssert.Contains(result.Status.Message, "NoSpace");
            Assert.AreEqual(LayoutState.Migrating, target.Marker().State);
        }
    }

    [TestMethod]
    public void T19_CrashAtAnyJournalBoundaryCanBeRepeated()
    {
        StandardSource();
        var reference = Target();
        Assert.IsTrue(Run(reference).IsOK());
        var expected = reference.Snapshot();

        string file = "/home/bob/brief.txt";
        var crashes = new Func<string, bool>[]
        {
            operation => operation == "write " + file,                                                      // zwischen W und Datei
            operation => operation.StartsWith("journal F " + Encode(file), StringComparison.Ordinal),           // zwischen Datei und F
            operation => operation == "mkdir /srv/legacy-data",                                              // zwischen D und mkdir
        };
        foreach (var crash in crashes)
        {
            var target = Target();
            target.FailWhen = crash;
            Assert.IsFalse(Run(target).IsOK());
            Assert.AreEqual(LayoutState.Migrating, target.Marker().State);
            target.FailWhen = null;
            var repeated = Run(target);
            Assert.IsTrue(repeated.IsOK(), repeated.Status.Message);
            CollectionAssert.AreEqual(expected, target.Snapshot());
        }

        // Zwischen mkdir und dem naechsten Eintrag.
        var afterDirectory = Target();
        bool created = false;
        afterDirectory.FailWhen = operation =>
        {
            if (created) return true;
            created = operation == "mkdir /srv";
            return false;
        };
        Assert.IsFalse(Run(afterDirectory).IsOK());
        afterDirectory.FailWhen = null;
        Assert.IsTrue(Run(afterDirectory).IsOK());
        CollectionAssert.AreEqual(expected, afterDirectory.Snapshot());
    }

    [TestMethod]
    public void T19_TemporaryFileOfAnInterruptedWriteIsCleanedUp()
    {
        StandardSource();
        var reference = Target();
        Assert.IsTrue(Run(reference).IsOK());
        // Datei, Marker "migrating" und Marker "ready": jeweils Absturz zwischen Temp und Rename.
        foreach (int markerWrite in new[] { -1, 0, 1 })
        {
            var target = Target();
            int markers = 0;
            target.CrashBeforeRenameWhen = operation => markerWrite < 0
                ? operation == "write /home/bob/brief.txt"
                : operation == "write " + LayoutMarker.Path && markers++ == markerWrite;
            Assert.IsFalse(Run(target).IsOK());
            Assert.IsTrue(target.List().Value!.Any(path => path.EndsWith(IMigrationTarget.TemporarySuffix, StringComparison.Ordinal)), "Temp-Rest liegt");
            target.CrashBeforeRenameWhen = null;
            var repeated = Run(target);
            Assert.IsTrue(repeated.IsOK(), $"{markerWrite}: {repeated.Status.Message}");
            CollectionAssert.AreEqual(reference.Snapshot(), target.Snapshot(), markerWrite.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public void T19_CrashWhileWritingTheJournalHeaderCanBeRepeated()
    {
        StandardSource();
        var reference = Target();
        Assert.IsTrue(Run(reference).IsOK());
        foreach (string partial in new[] { "", "ZEHOS-MIG", "\0\0\0\0", "ZEHOS-MIGRATION-1 abc\n" })
        {
            var target = Target();
            target.FailWhen = operation => operation.StartsWith("journal ZEHOS-MIGRATION-1", StringComparison.Ordinal);
            Assert.IsFalse(Run(target).IsOK());
            Assert.AreEqual(LayoutState.Migrating, target.Marker().State);
            File.WriteAllText(target.Host(MigrationJournal.Path), partial);
            target.FailWhen = null;
            var repeated = Run(target);
            Assert.IsTrue(repeated.IsOK(), $"Kopf '{partial}': {repeated.Status.Message}");
            CollectionAssert.AreEqual(reference.Snapshot(), target.Snapshot());
        }
    }

    [TestMethod]
    public void T19_CrashWhileCleaningUpCanBeRepeatedAgain()
    {
        StandardSource();
        var reference = Target();
        Assert.IsTrue(Run(reference).IsOK());
        var target = Target();
        target.FailWhen = operation => operation == "write /srv/legacy-data/notizen.txt";
        Assert.IsFalse(Run(target).IsOK());
        target.FailWhen = operation => operation == "delete /home/bob";
        Assert.IsFalse(Run(target).IsOK(), "Abbruch mitten im Aufraeumen");
        target.FailWhen = null;
        Assert.IsTrue(Run(target).IsOK());
        CollectionAssert.AreEqual(reference.Snapshot(), target.Snapshot());
    }

    [TestMethod]
    public void T20_ForeignEntryOnAFreshTargetIsRefused()
    {
        StandardSource();
        var target = Target();
        Directory.CreateDirectory(target.Host("/home/anna"));
        File.WriteAllText(target.Host("/home/anna/bild.bmp"), "alt");
        StringAssert.Contains(Run(target).Status.Message, "ForeignEntry");
        Assert.IsFalse(Writes(target));
        Assert.AreEqual("alt", File.ReadAllText(target.Host("/home/anna/bild.bmp")));
    }

    [TestMethod]
    public void T20_JournalNamingAnInstallerAreaIsRefused()
    {
        StandardSource();
        var target = Target();
        target.FailWhen = operation => operation == "write /home/bob/brief.txt";
        Assert.IsFalse(Run(target).IsOK());
        File.AppendAllText(target.Host(MigrationJournal.Path), MigrationJournal.WriteLine("/boot/efi/zehos.elf"));
        target.FailWhen = null;
        target.Operations.Clear();
        StringAssert.Contains(Run(target).Status.Message, "ForeignEntry");
        Assert.IsFalse(Writes(target));
    }

    [TestMethod]
    public void T19_CrashWithAPartiallyWrittenJournalLineCanBeRepeated()
    {
        StandardSource();
        var reference = Target();
        Assert.IsTrue(Run(reference).IsOK());
        var target = Target();
        target.FailWhen = operation => operation == "write /home/bob/brief.txt";
        Assert.IsFalse(Run(target).IsOK());
        File.AppendAllText(target.Host(MigrationJournal.Path), "W aGFs\0\0\0");
        target.FailWhen = null;
        Assert.IsTrue(Run(target).IsOK());
        CollectionAssert.AreEqual(reference.Snapshot(), target.Snapshot());
    }

    [TestMethod]
    public void T20_ForeignEntryRefusesTheRepetition()
    {
        StandardSource();
        var target = Target();
        target.FailWhen = operation => operation == "write /home/bob/brief.txt";
        Assert.IsFalse(Run(target).IsOK());
        File.WriteAllText(target.Host("/fremd.txt"), "x");
        target.FailWhen = null;
        target.Operations.Clear();
        var result = Run(target);
        StringAssert.Contains(result.Status.Message, "ForeignEntry");
        Assert.IsFalse(Writes(target), "nichts geloescht oder geschrieben");
        Assert.AreEqual(LayoutState.Migrating, target.Marker().State);
    }

    [TestMethod]
    public void T20_DamagedJournalRefusesTheRepetition()
    {
        StandardSource();
        var target = Target();
        target.FailWhen = operation => operation == "write /home/bob/brief.txt";
        Assert.IsFalse(Run(target).IsOK());
        string[] lines = File.ReadAllText(target.Host(MigrationJournal.Path)).Split('\n');
        lines[1] = "kaputt";
        File.WriteAllText(target.Host(MigrationJournal.Path), string.Join('\n', lines));
        target.FailWhen = null;
        StringAssert.Contains(Run(target).Status.Message, "JournalInvalid");
    }

    [TestMethod]
    public void T21_SourceChangeDuringCopyingAbortsBeforeReady()
    {
        StandardSource();
        var target = Target();
        var result = Run(target, progress: step =>
        {
            if (step != MigrationStep.Copied) return;
            string file = Path.Combine(source, "notizen.txt");
            File.WriteAllText(file, "NOTIZ");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        });
        StringAssert.Contains(result.Status.Message, "SourceChanged");
        Assert.AreEqual(LayoutState.Migrating, target.Marker().State);
    }

    [TestMethod]
    public void T21_SourceChangeAfterTheInventoryFailsTheReadBack()
    {
        StandardSource();
        var target = Target();
        var result = Run(target, progress: step =>
        {
            if (step == MigrationStep.Inventory) File.WriteAllText(Path.Combine(source, "notizen.txt"), "NOTIZ");
        });
        StringAssert.Contains(result.Status.Message, "VerifyFailed");
        Assert.AreNotEqual(LayoutState.Ready, target.Marker().State);
    }

    [TestMethod]
    public void T21a_TooManyEntriesAbortBeforeAnyWrite()
    {
        for (int i = 0; i < 6; i++) Write($"d/{i}.txt", "x");
        var inventory = MigrationInventory.Read(source, 5);
        Assert.IsFalse(inventory.IsOK());
        StringAssert.Contains(inventory.Status.Message, "InventoryTooLarge");
        Assert.IsTrue(MigrationInventory.Read(source, 7).IsOK(), "7 Eintraege (Ordner + 6 Dateien) passen genau");
    }

    [TestMethod]
    public void T22_SourceStaysByteIdentical()
    {
        StandardSource();
        string before = SourceHash();
        Assert.IsTrue(Run(Target()).IsOK());
        Assert.AreEqual(before, SourceHash());
    }

    [TestMethod]
    public void PathComponentHasAUserAndASystemVariant()
    {
        Assert.IsTrue(PathComponent.IsValid("brief.txt"));
        Assert.IsFalse(PathComponent.IsValid(".zehos-security.db"), "Nutzerpfade sperren .zehos-");
        Assert.IsTrue(PathComponent.IsValid(".zehos-security.db", allowSystemNames: true), "Systemziele erlauben .zehos-");
        foreach (string invalid in new[] { "", ".", "..", "a.", "a ", "a|b", "a:b", "a\\b", "a/b", "a\0b", "a\nb" })
        {
            Assert.IsFalse(PathComponent.IsValid(invalid), invalid);
            Assert.IsFalse(PathComponent.IsValid(invalid, allowSystemNames: true), invalid);
        }
    }

    [TestMethod]
    public void ReadyTargetIsRefusedWithoutWriting()
    {
        StandardSource();
        var target = Target();
        Assert.IsTrue(Run(target).IsOK());
        target.Operations.Clear();
        StringAssert.Contains(Run(target).Status.Message, "TargetReady");
        Assert.AreEqual(0, target.Operations.Count);
    }
}
