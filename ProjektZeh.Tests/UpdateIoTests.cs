using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UpdateIoTests
{
    [TestMethod]
    public void BinaryReplace_PreservesPreviousKernelAndRotatesBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "boot"));
        try
        {
            var files = new FileService(root);
            string path = Path.Combine(root, "boot/ProjektZeh.elf");
            File.WriteAllBytes(path, [1]);
            File.WriteAllBytes(path + ".bak", [0]);
            var target = new FileUpdateTarget(root);
            Assert.IsTrue(target.ReplaceKernel([2]).IsOK());
            Assert.IsTrue(target.WriteVersion("0.2.0").IsOK());
            CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(path + ".bak"));
            CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(path));
            Assert.AreEqual("0.2.0\n", File.ReadAllText(Path.Combine(root, "boot/zehos-version.txt")));
            Assert.AreEqual(3, Directory.GetFiles(Path.Combine(root, "boot")).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BinaryReplace_RenameFailureRestoresKernelAndExistingBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "kernel");
            File.WriteAllBytes(path, [1]);
            File.WriteAllBytes(path + ".bak", [0]);
            var files = new FileService(root, (source, destination, overwrite) =>
            {
                if (source.EndsWith(".tmp", StringComparison.Ordinal)) throw new IOException("Testfehler");
                File.Move(source, destination, overwrite);
            });
            Assert.IsFalse(files.WriteBytes(path, [2], true).IsOK());
            CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(new byte[] { 0 }, File.ReadAllBytes(path + ".bak"));
            Assert.AreEqual(2, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void HttpResponse_RejectsTruncationAndUnboundedOrEncodedBodies()
    {
        using var valid = Response("HTTP/1.0 200 OK\r\nContent-Length: 3\r\n\r\nabc");
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("abc"), HttpUpdateResponse.Read(valid, 3));
        foreach (string text in new[]
        {
            "HTTP/1.0 404 Missing\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.0 200 OK\r\n\r\n",
            "HTTP/1.0 200 OK\r\nContent-Length: 4\r\n\r\nabc",
            "HTTP/1.0 200 OK\r\nContent-Length: 5\r\n\r\nabcde",
            "HTTP/1.0 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\na",
            "HTTP/1.0 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"
        })
        {
            using var stream = Response(text);
            try
            {
                HttpUpdateResponse.Read(stream, 4);
                Assert.Fail("Ungueltige HTTP-Antwort wurde akzeptiert.");
            }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public void Endpoint_RejectsRequestsAndInvalidPorts()
    {
        Assert.IsTrue(UpdateEndpoint.TryParse("mirror.example:8080", out var endpoint));
        Assert.AreEqual("mirror.example:8080", endpoint!.ToString());
        foreach (string invalid in new[] { "http://mirror", "mirror/path", "mirror:0", "mirror:65536", "mirror\r\nHost:x", "-mirror", "mirror:", "a..b" })
            Assert.IsFalse(UpdateEndpoint.TryParse(invalid, out _));
    }

    private static MemoryStream Response(string text) => new(Encoding.ASCII.GetBytes(text));
}
