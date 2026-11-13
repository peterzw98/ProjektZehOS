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

    [TestMethod]
    public void FailedCreationLeavesNoOrphanedRightsEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var access = new FileAccessControl(root);
            access.Initialize();
            access.Assign(root, "1");
            access.Grant(root, "*", FilePermission.Read | FilePermission.Write);
            int saves = 0;
            access.Persist = () => { saves++; return ResultErr.CreateSuccess(); };
            var bob = new FileService(root, (_, _, _) => throw new IOException("Datentraeger voll"),
                access, new UserSession("2", "bob", false, root));
            var alice = new FileService(root, access: access, session: new UserSession("3", "alice", false, root));
            string target = Path.Combine(root, "notiz.txt");

            Assert.IsFalse(bob.WriteText("notiz.txt", "x").IsOK());
            Assert.IsFalse(access.Contains(target), "Der Eintrag muss nach dem Schreibfehler zurueckgenommen sein.");
            Assert.AreEqual(2, saves, "Anlegen und Zuruecknehmen muessen beide gespeichert werden.");
            Assert.IsTrue(alice.WriteText("notiz.txt", "y").IsOK(), "Ein anderer Benutzer muss den Pfad danach anlegen duerfen.");
            Assert.IsTrue(access.IsOwner(target, "3"));
        }
        finally { Directory.Delete(root, true); }
    }
}
