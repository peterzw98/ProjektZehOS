using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class VirtualFileServiceTests
{
    private static readonly UserSession User = new("2", "bob", false, "/home/bob");
    private static readonly UserSession Admin = new("1", "alice", true, "/home/alice");

    private static FileService Service(UserSession? session, Func<string, ResultObject<byte[]>>? source = null) =>
        new(Path.GetTempPath(), session: session, virtualSource: source ?? (path => ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetBytes("aus " + path + "\n"))));

    [TestMethod]
    public void ReadVirtualReadsFromTheSourceForEveryNormalizedSpelling()
    {
        var files = Service(User);
        foreach (string path in new[] { "/proc/version", "/proc/./version", "//proc/version", "/proc/../proc/version" })
            Assert.AreEqual("aus /proc/version\n", files.ReadVirtual(path).Value, path);
    }

    [TestMethod]
    public void ReadVirtualTakesEachCallAsFreshSnapshot()
    {
        int calls = 0;
        var files = Service(User, _ => ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetBytes((++calls).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.AreEqual("1", files.ReadVirtual("/proc/uptime").Value);
        Assert.AreEqual("2", files.ReadVirtual("/proc/uptime").Value);
    }

    [TestMethod]
    public void ReadVirtualIgnoresAFileWithTheSameNameOnDisk()
    {
        // Mount fehlgeschlagen: unter dem Arbeitsverzeichnis liegt ein gleichnamiger Pfad, gelesen wird trotzdem der Erzeuger.
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "proc"));
        try
        {
            File.WriteAllText(Path.Combine(root, "proc", "version"), "falsch");
            var files = new FileService(root, session: User, virtualSource: _ => ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetBytes("echt")));
            Assert.AreEqual("echt", files.ReadVirtual("/proc/version").Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void LogIsForAdministratorsAndNothingWithoutLogin()
    {
        Assert.IsFalse(Service(User).ReadVirtual("/proc/log").IsOK());
        Assert.IsTrue(Service(Admin).ReadVirtual("/proc/log").IsOK());
        Assert.IsFalse(Service(null).ReadVirtual("/proc/version").IsOK());
        Assert.IsFalse(Service(new UserSession()).ReadVirtual("/proc/version").IsOK());
    }

    [TestMethod]
    public void SourceFailuresBecomeResultsNotExceptions()
    {
        Assert.IsFalse(Service(User, _ => throw new InvalidOperationException("kaputt")).ReadVirtual("/proc/meminfo").IsOK());
        Assert.IsFalse(Service(User, _ => ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, "x")).ReadVirtual("/proc/meminfo").IsOK());
        Assert.IsFalse(new FileService(Path.GetTempPath(), session: User).ReadVirtual("/proc/meminfo").IsOK());
    }

    [TestMethod]
    public void DevicesReadEmptyOrHitTheLimitAndUnknownOrDirectoriesFail()
    {
        var files = Service(User);
        Assert.AreEqual(string.Empty, files.ReadVirtual("/dev/null").Value);
        var zero = files.ReadVirtual("/dev/zero");
        Assert.IsFalse(zero.IsOK());
        StringAssert.Contains(zero.Status.Message, "TextTooLarge");
        Assert.AreEqual(ResultCode.NotFound, files.ReadVirtual("/proc/threads").Status.Code);
        Assert.IsFalse(files.ReadVirtual("/proc").IsOK());
        Assert.IsFalse(files.ReadVirtual("/proc/../etc/x").IsOK());
    }

    [TestMethod]
    public void OversizedSnapshotIsAnErrorNotTruncated()
    {
        var files = Service(User, _ => ResultObjectFactory.CreateSuccess(new byte[FileService.MaximumTextLength + 1]));
        Assert.IsFalse(files.ReadVirtual("/proc/mounts").IsOK());
    }

    [TestMethod]
    public void ListVirtualListsDirectoriesFirstAndRejectsFilesAndAnonymous()
    {
        var sys = Service(User).ListVirtual("/sys");
        CollectionAssert.AreEqual(new[] { new FileEntry("/sys/kernel", true), new FileEntry("/sys/block", false), new FileEntry("/sys/devices", false) }, sys.Value);
        Assert.AreEqual(2, Service(User).ListVirtual("/dev/.").Value!.Length);
        Assert.IsFalse(Service(User).ListVirtual("/proc/version").IsOK());
        Assert.IsFalse(Service(null).ListVirtual("/proc").IsOK());
    }

    [TestMethod]
    public void WriteNullAcceptsOnlyDevNull()
    {
        Assert.IsTrue(Service(User).WriteNull("/dev/null").IsOK());
        Assert.IsTrue(Service(User).WriteNull("/dev/./null").IsOK());
        foreach (string path in new[] { "/dev/zero", "/proc/version", "/sys/block", "/proc/none" })
            Assert.IsFalse(Service(Admin).WriteNull(path).IsOK(), path);
        Assert.IsFalse(Service(null).WriteNull("/dev/null").IsOK());
    }

    [TestMethod]
    public void IsVirtualPathUsesTheSameNormalization()
    {
        var files = Service(User);
        Assert.IsTrue(files.IsVirtualPath("/proc/./version"));
        Assert.IsTrue(files.IsVirtualPath("/dev/none"));
        Assert.IsFalse(files.IsVirtualPath("/proc/../etc/x"));
        Assert.IsFalse(files.IsVirtualPath(""));
        Assert.IsFalse(files.IsVirtualPath(null));
    }

    [TestMethod]
    public void ReadBytesReadsToEofWhenLengthIsZeroAndKeepsEmptyFilesEmpty()
    {
        using (var stream = new MemoryStream(new byte[10 * 1024]))
            Assert.AreEqual(10 * 1024, FileService.ReadUntilEnd(stream, 64 * 1024).Value!.Length);
        using (var stream = new MemoryStream(new byte[100]))
            Assert.IsFalse(FileService.ReadUntilEnd(stream, 99).IsOK());
        string empty = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(empty, []);
        try
        {
            Assert.AreEqual(0, new FileService(Path.GetTempPath()).ReadBytes(empty, 16).Value!.Length);
        }
        finally
        {
            File.Delete(empty);
        }
    }
}
