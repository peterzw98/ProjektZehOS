using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FileMigrationTargetTests
{
    private string root = null!;

    [TestInitialize]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "ZehOS-MigrationTarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "etc", "zehos"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, true);

    private FileMigrationTarget Target(Func<ResultErr>? sync = null, Action<string, string, bool>? move = null,
        Func<string, bool, ResultErr>? guard = null) => new(root,
            () => ResultObjectFactory.CreateSuccess(12345L), () => ResultObjectFactory.CreateSuccess(321L),
            sync ?? ResultErr.CreateSuccess, guard ?? ((_, _) => ResultErr.CreateSuccess()), move);

    [TestMethod]
    public void FilesDirectoriesAndStatisticsUseTheTargetRoot()
    {
        var target = Target();
        Assert.IsTrue(target.CreateDirectory("/home").IsOK());
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("inhalt"));
        Assert.IsTrue(target.WriteFile("/home/note", content).IsOK());
        var read = target.ReadBack("/home/note");
        Assert.IsTrue(read.IsOK());
        using (var reader = new StreamReader(read.Value!)) Assert.AreEqual("inhalt", reader.ReadToEnd());
        CollectionAssert.AreEquivalent(new[] { "/etc", "/etc/zehos", "/home", "/home/note" }, target.List().Value!);
        Assert.AreEqual(12345L, target.FreeBytes().Value);
        Assert.AreEqual(321L, target.FreeInodes().Value);
        Assert.IsTrue(target.Delete("/home/note").IsOK());
        Assert.IsTrue(target.Delete("/home").IsOK());
        Assert.AreEqual(ResultCode.NotFound, target.ReadBack("/home/note").Status.Code);
    }

    [TestMethod]
    [DataRow("/../escape")]
    [DataRow("/C:/escape")]
    [DataRow("/bad\\escape")]
    [DataRow("relative")]
    [DataRow("//double")]
    [DataRow("/name.")]
    [DataRow("/")]
    public void InvalidFilePathsNeverEscapeOrChangeTheTarget(string path)
    {
        using var content = new MemoryStream([1]);
        Assert.IsFalse(Target().WriteFile(path, content).IsOK());
        Assert.AreEqual(0, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void RootCannotBeDeletedAndDeniedGuardPreventsMutation()
    {
        var target = Target(guard: (_, _) => ResultErr.CreateFailure(ResultCode.Failure, "cause='ForeignMount'"));
        Assert.IsFalse(target.CreateDirectory("/denied").IsOK());
        Assert.IsFalse(Target().Delete("/").IsOK());
        Assert.IsTrue(Directory.Exists(root));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "denied")));
    }

    [TestMethod]
    public void FailedCopyKeepsTheOriginalAndOnlyTheKnownTemporaryFile()
    {
        File.WriteAllText(Path.Combine(root, "note"), "original");
        using var content = new FailingInput();
        var result = Target().WriteFile("/note", content);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "input failed");
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "note")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "note" + IMigrationTarget.TemporarySuffix)));
        Assert.AreEqual(2, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void FailedRenameRestoresTheOriginal()
    {
        File.WriteAllText(Path.Combine(root, "note"), "original");
        var target = Target(move: (from, to, overwrite) =>
        {
            if (from.EndsWith(IMigrationTarget.TemporarySuffix, StringComparison.Ordinal)) throw new IOException("rename failed");
            File.Move(from, to, overwrite);
        });
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("changed"));
        var result = target.WriteFile("/note", content);
        Assert.IsFalse(result.IsOK());
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "note")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "note" + IMigrationTarget.BackupSuffix)));
    }

    [TestMethod]
    public void MissingMarkerCanBeReadFromItsInterruptedReplacementWithoutWriting()
    {
        string backup = Path.Combine(root, "etc", "zehos", ".zehos-layout" + IMigrationTarget.BackupSuffix);
        File.WriteAllText(backup, "old marker");
        var read = Target().ReadBack(LayoutMarker.Path);
        Assert.IsTrue(read.IsOK());
        using (var reader = new StreamReader(read.Value!)) Assert.AreEqual("old marker", reader.ReadToEnd());
        Assert.IsTrue(File.Exists(backup));
        Assert.IsFalse(File.Exists(Path.Combine(root, "etc", "zehos", ".zehos-layout")));
    }

    [TestMethod]
    public void JournalIntentIsWrittenBeforeBothSyncsAndTheirFailureIsReturned()
    {
        int calls = 0;
        var target = Target(sync: () =>
        {
            Assert.AreEqual("intent\n", File.ReadAllText(Path.Combine(root, "etc", "zehos", ".zehos-migration-journal")));
            return ++calls == 2 ? ResultErr.CreateFailure(ResultCode.Failure, "second sync failed") : ResultErr.CreateSuccess();
        });
        var result = target.AppendJournal("intent\n", flush: true);
        Assert.IsFalse(result.IsOK());
        Assert.AreEqual("second sync failed", result.Message);
        Assert.AreEqual(2, calls);
    }

    private sealed class FailingInput : Stream
    {
        private bool read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (read) throw new IOException("input failed");
            read = true;
            buffer[offset] = 42;
            return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
