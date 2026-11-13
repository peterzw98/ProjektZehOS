using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Storage;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SecuredFileServiceTests
{
    [TestMethod]
    public void CentralChecksProtectEveryOperationAndCanonicalPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var access = new FileAccessControl(root);
            access.Initialize();
            access.Assign(root, "1");
            access.Grant(root, "*", FilePermission.Read);
            access.Persist = ResultErr.CreateSuccess;
            var alice = new UserSession("1", "alice", false, root);
            var bob = new UserSession("2", "bob", false, root);
            var first = new FileService(root, access: access, session: alice);
            var second = new FileService(root, access: access, session: bob);
            Assert.IsTrue(first.WriteText("private.txt", "secret").IsOK());
            Assert.IsFalse(second.ReadText("./private.txt").IsOK());
            Assert.IsFalse(second.WriteText("private.txt", "evil").IsOK());
            Assert.IsFalse(second.WriteText("new.txt", "evil").IsOK());
            Assert.IsFalse(second.CreateDirectory("new").IsOK());
            Assert.IsFalse(second.ChangeDirectory("..").IsOK());
            Assert.AreEqual(0, second.ListEntries(".").Value!.Length);
            access.Grant(Path.Combine(root, "private.txt"), "2", FilePermission.Read);
            Assert.AreEqual("secret", second.ReadText("private.txt").Value);
            Assert.IsFalse(second.WriteText("private.txt", "evil").IsOK());
            Assert.IsTrue(first.CreateDirectory("own").IsOK());
            Assert.IsTrue(first.ChangeDirectory("own").IsOK());
            Assert.AreEqual(root, second.CurrentDirectory);
            Assert.IsFalse(second.ChangeDirectory("own").IsOK());
            access.Persist = () => ResultErr.CreateFailure(ResultCode.Failure, "disk full");
            Assert.IsFalse(first.WriteText("new.txt", "secret").IsOK());
            Assert.IsFalse(File.Exists(Path.Combine(root, "own", "new.txt")));
            Assert.IsFalse(second.ReadText("private.txt").IsOK());
        }
        finally { Directory.Delete(root, true); }
    }
}
