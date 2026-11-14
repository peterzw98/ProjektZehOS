using System;
using System.IO;
using System.Text;
using System.Linq;
using ProjektZeh.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FileOperationTests
{
    private string root = null!;
    private FileService files = null!;
    private FileAccessControl access = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        access = new FileAccessControl(root);
        access.Initialize(); access.Assign(root, "alice");
        access.Grant(root, "*", FilePermission.Read);
        access.Persist = ResultErr.CreateSuccess;
        files = new FileService(root, access: access, session: new UserSession("alice", "alice", false, root), diskSpace: HostDiskSpace);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, true);

    [TestMethod]
    public void CopyMoveDeletePreserveBytesAndMovePermissions()
    {
        Assert.IsTrue(files.WriteText("source", "Grüße").IsOK());
        access.Grant(Path.Combine(root, "source"), "bob", FilePermission.Read);
        Assert.IsTrue(files.CopyFile("source", "copy").IsOK());
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(root, "source")), File.ReadAllBytes(Path.Combine(root, "copy")));
        Assert.IsTrue(files.MoveFile("source", "moved").IsOK());
        Assert.IsFalse(access.Contains(Path.Combine(root, "source")));
        var bob = new FileService(root, access: access, session: new UserSession("bob", "bob", false, root));
        Assert.AreEqual("Grüße", bob.ReadText("moved").Value);
        Assert.IsFalse(bob.ReadText("copy").IsOK());
        Assert.IsTrue(files.DeleteFile("moved").IsOK());
        Assert.IsFalse(access.Contains(Path.Combine(root, "moved")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "moved")));
    }

    [TestMethod]
    public void ExistingTargetsAndMissingSourcesDoNotChangeDataOrRights()
    {
        files.WriteText("source", "original"); files.WriteText("target", "kept");
        Assert.IsFalse(files.CopyFile("source", "target").IsOK());
        Assert.IsFalse(files.MoveFile("source", "target").IsOK());
        Assert.IsFalse(files.CopyFile("missing", "new").IsOK());
        Assert.IsFalse(files.MoveFile("missing", "new").IsOK());
        Assert.IsFalse(files.DeleteFile("missing").IsOK());
        Assert.IsFalse(access.Contains(Path.Combine(root, "new")));
        Assert.AreEqual("original", files.ReadText("source").Value);
        Assert.AreEqual("kept", files.ReadText("target").Value);
    }

    [TestMethod]
    public void UnauthorizedOperationsPreserveFiles()
    {
        files.WriteText("secret", "kept");
        var bob = new FileService(root, access: access, session: new UserSession("bob", "bob", false, root));
        Assert.IsFalse(bob.CopyFile("secret", "copy").IsOK());
        Assert.IsFalse(bob.MoveFile("secret", "moved").IsOK());
        Assert.IsFalse(bob.DeleteFile("secret").IsOK());
        Assert.IsFalse(bob.Touch("secret").IsOK());
        Assert.IsFalse(bob.GetFileSize("secret").IsOK());
        Assert.AreEqual("kept", files.ReadText("secret").Value);
    }

    [TestMethod]
    public void TouchPreservesContentAndRmdirRejectsNonemptyOrCurrentDirectory()
    {
        Assert.IsTrue(files.Touch("new").IsOK());
        Assert.AreEqual(0L, files.GetFileSize("new").Value);
        files.WriteText("new", "ä");
        Assert.IsTrue(files.Touch("new").IsOK());
        Assert.AreEqual("ä", files.ReadText("new").Value);
        Assert.AreEqual(2L, files.GetFileSize("new").Value);
        files.CreateDirectory("dir"); files.WriteText(Path.Combine("dir", "child"), "x");
        Assert.IsFalse(files.DeleteDirectory("dir").IsOK());
        Assert.IsTrue(files.DeleteFile(Path.Combine("dir", "child")).IsOK());
        Assert.IsTrue(files.DeleteDirectory("dir").IsOK());
        Assert.IsFalse(access.Contains(Path.Combine(root, "dir")));
        Assert.IsFalse(files.DeleteDirectory(".").IsOK());
    }

    [TestMethod]
    public void FailedMoveRestoresRightsAndPreservesSource()
    {
        files.WriteText("source", "kept");
        var failing = new FileService(root, (_, _, _) => throw new IOException("rename failed"), access,
            new UserSession("alice", "alice", false, root));
        Assert.IsFalse(failing.MoveFile("source", "new").IsOK());
        Assert.IsTrue(access.Contains(Path.Combine(root, "source")));
        Assert.IsFalse(access.Contains(Path.Combine(root, "new")));
        Assert.AreEqual("kept", files.ReadText("source").Value);
    }

    [TestMethod]
    public void TouchUpdatesHostTimestampWithoutTruncating()
    {
        files.WriteText("timestamp", "kept");
        string path = Path.Combine(root, "timestamp");
        var before = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, before);
        Assert.IsTrue(files.Touch("timestamp").IsOK());
        Assert.IsTrue(File.GetLastWriteTimeUtc(path) > before);
        Assert.AreEqual("kept", File.ReadAllText(path));
    }

    [TestMethod]
    public void SearchAndUsageExcludeUnreadableFilesAndEnforceLimit()
    {
        files.CreateDirectory("sub"); files.WriteText(Path.Combine("sub", "public.txt"), "abc");
        files.WriteText("private.txt", "secret");
        access.Grant(Path.Combine(root, "sub"), "bob", FilePermission.Read);
        access.Grant(Path.Combine(root, "sub", "public.txt"), "bob", FilePermission.Read);
        var bob = new FileService(root, access: access, session: new UserSession("bob", "bob", false, root));
        var found = bob.FindEntries(".", "txt");
        Assert.IsTrue(found.IsOK());
        Assert.AreEqual(1, found.Value!.Length);
        Assert.AreEqual(3L, bob.GetDirectorySize(".").Value);
        Assert.AreEqual(3L, bob.GetDirectorySize(Path.Combine("sub", "public.txt")).Value);
        for (int index = 0; index <= FileService.MaximumSearchEntries; index++)
            File.WriteAllText(Path.Combine(root, "entry" + index), "");
        Assert.IsFalse(new FileService(root).FindEntries(".").IsOK());
        Assert.IsFalse(files.FindEntries(".").IsOK(), "Auch ausgeblendete Eintraege verbrauchen das Arbeitsbudget.");
    }

    [TestMethod]
    public void MetadataSaveFailureStopsCreationAndLocksAccess()
    {
        files.WriteText("source", "kept");
        access.Persist = () => ResultErr.CreateFailure(ResultCode.Failure, "disk full");
        Assert.IsFalse(files.MoveFile("source", "target").IsOK());
        Assert.IsTrue(File.Exists(Path.Combine(root, "source")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "target")));
        Assert.IsFalse(access.IsReady);
    }

    [TestMethod]
    public void SourceParentAndDestinationRightsAreRequired()
    {
        files.CreateDirectory("restricted");
        files.WriteText(Path.Combine("restricted", "source"), "kept");
        access.Grant(Path.Combine(root, "restricted"), "bob", FilePermission.Read);
        access.Grant(Path.Combine(root, "restricted", "source"), "bob", FilePermission.Read | FilePermission.Write);
        access.Grant(root, "bob", FilePermission.Read | FilePermission.Write);
        var bob = new FileService(root, access: access, session: new UserSession("bob", "bob", false, root));
        Assert.IsFalse(bob.DeleteFile(Path.Combine("restricted", "source")).IsOK());
        Assert.IsFalse(bob.MoveFile(Path.Combine("restricted", "source"), "target").IsOK());
        Assert.IsFalse(bob.CopyFile(Path.Combine("restricted", "source"), Path.Combine("restricted", "copy")).IsOK());
        Assert.IsTrue(bob.CopyFile(Path.Combine("restricted", "source"), "copy").IsOK());
    }
    [TestMethod]
    public void DiskSpaceAdapterRunsAfterAuthorizationAndPropagatesFailure()
    {
        string? requested = null;
        var adapted = new FileService(root, access: access, session: new UserSession("alice", "alice", false, root),
            diskSpace: path => { requested = path; return ResultObjectFactory.CreateSuccess((100L, 25L)); });
        Assert.AreEqual((100L, 25L), adapted.GetDiskSpace(".").Value);
        Assert.AreEqual(root, requested);
        requested = null;
        Assert.IsFalse(adapted.GetDiskSpace("..").IsOK());
        Assert.IsNull(requested);
        var failing = new FileService(root, diskSpace: _ => ResultObjectFactory.CreateFailure<(long, long)>(ResultCode.Failure, "unmounted"));
        Assert.IsFalse(failing.GetDiskSpace(".").IsOK());
    }

    [TestMethod]
    public void ShellCommandsValidateArgumentsAndUseAuthorizedServices()
    {
        var commands = new FileShellCommands(files).Commands;
        var output = new Output();
        ShellCommand Command(string name) => commands.Single(command => command.Name == name);
        Assert.IsFalse(Command("cp").Execute(["only"], output).IsOK());
        Assert.IsFalse(Command("rm").Execute(["-r"], output).IsOK());
        Assert.IsTrue(Command("touch").Execute(["source"], output).IsOK());
        Assert.IsTrue(Command("cp").Execute(["source", "copy"], output).IsOK());
        Assert.IsFalse(Command("cp").Execute(["source", "copy"], output).IsOK());
        Assert.IsTrue(Command("mv").Execute(["copy", "moved"], output).IsOK());
        Assert.IsTrue(Command("find").Execute([".", "moved"], output).IsOK());
        StringAssert.Contains(output.Text.ToString(), "moved");
        Assert.IsTrue(Command("du").Execute(["."], output).IsOK());
        StringAssert.Contains(output.Text.ToString(), "0 Bytes");
        Assert.IsTrue(Command("df").Execute([], output).IsOK());
        StringAssert.Contains(output.Text.ToString(), "Gesamt:");
        Assert.IsTrue(Command("rm").Execute(["moved"], output).IsOK());
        files.CreateDirectory("empty");
        Assert.IsTrue(Command("rmdir").Execute(["empty"], output).IsOK());
    }

    private sealed class Output : ITextOutput
    {
        public StringBuilder Text { get; } = new();
        public void WriteLine(string text) => Text.AppendLine(text);
        public void Clear() => Text.Clear();
    }
    [TestMethod]
    public void TransferChecksDestinationRightsBeforeRevealingExistence()
    {
        files.WriteText("source", "public"); files.WriteText("private", "secret");
        access.Grant(Path.Combine(root, "source"), "bob", FilePermission.Read);
        access.Grant(root, "bob", FilePermission.Read | FilePermission.Write);
        var bob = new FileService(root, access: access, session: new UserSession("bob", "bob", false, root));
        var result = bob.CopyFile("source", "private");
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "Zugriff verweigert");
    }

    [TestMethod]
    public void FailedCopyRenameRemovesTemporaryFileAndRights()
    {
        files.WriteText("source", "kept");
        var failing = new FileService(root, (_, _, _) => throw new IOException("rename failed"), access,
            new UserSession("alice", "alice", false, root));
        Assert.IsFalse(failing.CopyFile("source", "copy").IsOK());
        Assert.IsFalse(access.Contains(Path.Combine(root, "copy")));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(root).Length);
        Assert.AreEqual("kept", files.ReadText("source").Value);
    }

    [TestMethod]
    public void MetadataFailureAfterMoveReportsFailureAndLocksCompletedDestination()
    {
        files.WriteText("source", "kept");
        int saves = 0;
        access.Persist = () => ++saves == 1 ? ResultErr.CreateSuccess() : ResultErr.CreateFailure(ResultCode.Failure, "disk full");
        Assert.IsFalse(files.MoveFile("source", "target").IsOK());
        Assert.IsFalse(File.Exists(Path.Combine(root, "source")));
        Assert.AreEqual("kept", File.ReadAllText(Path.Combine(root, "target")));
        Assert.IsFalse(access.IsReady);
    }
    private static ResultObject<(long TotalBytes, long AvailableBytes)> HostDiskSpace(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        return ResultObjectFactory.CreateSuccess((drive.TotalSize, drive.AvailableFreeSpace));
    }

    [TestMethod]
    public void DiskSpaceWithoutPlatformAdapterReturnsFailure()
    {
        Assert.IsFalse(new FileService(root).GetDiskSpace(".").IsOK());
    }
    [TestMethod]
    public void AppendReadMissingAuthorizedTargetDoesNotRegisterAndPreservesExistingContent()
    {
        var initial = files.ReadText("append", true);
        Assert.IsTrue(initial.IsOK(), initial.Status.ToString());
        Assert.IsNull(initial.Value);
        Assert.IsFalse(access.Contains(Path.Combine(root, "append")));
        Assert.IsTrue(files.WriteText("append", (initial.Value ?? "") + "first\n").IsOK());
        var existing = files.ReadText("append", true);
        Assert.IsTrue(existing.IsOK());
        Assert.IsTrue(files.WriteText("append", existing.Value + "second\n").IsOK());
        Assert.AreEqual("first\nsecond\n", files.ReadText("append").Value);
    }

    [TestMethod]
    public void AppendReadRequiresWritableParentAndCannotReadExistingUnregisteredFile()
    {
        var bob = new FileService(root, access: access, session: new UserSession("bob", "bob", false, root));
        Assert.IsFalse(bob.ReadText("missing", true).IsOK());
        Assert.IsFalse(access.Contains(Path.Combine(root, "missing")));
        File.WriteAllText(Path.Combine(root, "unregistered"), "secret");
        var existing = files.ReadText("unregistered", true);
        Assert.IsFalse(existing.IsOK());
        Assert.IsNull(existing.Value);
        Assert.IsFalse(access.Contains(Path.Combine(root, "unregistered")));
        Assert.AreEqual("secret", File.ReadAllText(Path.Combine(root, "unregistered")));
        Assert.IsFalse(files.ReadText(Path.Combine("absent", "file"), true).IsOK());
    }

    [TestMethod]
    public void AppendReadDoesNotBypassReadDeniedExistingFile()
    {
        files.WriteText("private", "secret");
        access.Grant(root, "bob", FilePermission.Read | FilePermission.Write);
        access.Grant(Path.Combine(root, "private"), "bob", FilePermission.Write);
        var bob = new FileService(root, access: access, session: new UserSession("bob", "bob", false, root));
        var result = bob.ReadText("private", true);
        Assert.IsFalse(result.IsOK());
        Assert.IsNull(result.Value);
        Assert.AreEqual("secret", files.ReadText("private").Value);
    }
    [TestMethod]
    public void DiskSpaceReportsActualDriveAndDeniesUnauthorizedPaths()
    {
        var result = files.GetDiskSpace(".");
        Assert.IsTrue(result.IsOK(), result.Status.ToString());
        Assert.AreEqual(new DriveInfo(Path.GetPathRoot(root)!).TotalSize, result.Value.TotalBytes);
        Assert.IsTrue(result.Value.AvailableBytes >= 0);
        Assert.IsFalse(files.GetDiskSpace("..").IsOK());
    }
}
