using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

/// <summary>
/// Ladybird V8, Review P1: <c>run</c> gibt dem Programm Schreibzugriff auf den ganzen Unterbaum des Shellverzeichnisses. Die
/// Prozesswurzel braucht deshalb Schreib- und Verwaltungsrecht; fremde Homes, die Verwaltungswurzel und Systempfade sind gesperrt.
/// </summary>
[TestClass]
public sealed class ProcessRootAccessTests
{
    [TestMethod]
    public void ForeignHomeReadableByAllIsNoProcessRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string alice = Path.Combine(home, "alice");
        Directory.CreateDirectory(alice);
        try
        {
            var access = NewAccess(root);
            access.Assign(home, "0");
            access.Grant(home, "*", FilePermission.Read);
            access.Assign(alice, "1");
            // Wie /data/home: Lesen für alle, damit cd gelingt; das darf kein Schreibzugriff für run werden.
            access.Grant(alice, "*", FilePermission.Read);
            var bob = new FileService(root, access: access, session: new UserSession("2", "bob", false, root));

            Assert.IsTrue(bob.ChangeDirectory(alice).IsOK(), "Vorbedingung: cd prüft nur Lesen.");
            ResultErr denied = bob.AuthorizeProcessRoot(alice);
            Assert.IsFalse(denied.IsOK());
            StringAssert.Contains(denied.Message, "ProcessRootDenied");

            access.Grant(alice, "2", FilePermission.Write);
            Assert.IsFalse(bob.AuthorizeProcessRoot(alice).IsOK(), "Schreibfreigabe ohne Eigentum ist kein Verwaltungsrecht.");

            var owner = new FileService(alice, access: access, session: new UserSession("1", "alice", false, alice));
            Assert.IsTrue(owner.AuthorizeProcessRoot(alice).IsOK(), owner.AuthorizeProcessRoot(alice).Message);
            var admin = new FileService(root, access: access, session: new UserSession("0", "admin", true, root));
            Assert.IsTrue(admin.AuthorizeProcessRoot(alice).IsOK());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ForeignEntryBelowOwnRootBlocksIt()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string alice = Path.Combine(root, "alice");
        string sub = Path.Combine(alice, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "bob.txt"), "b");
        File.WriteAllText(Path.Combine(sub, "unzugeordnet.txt"), "x");
        try
        {
            var access = NewAccess(root);
            access.Assign(alice, "1");
            access.Assign(sub, "1");
            var files = new FileService(alice, access: access, session: new UserSession("1", "alice", false, alice));
            Assert.IsTrue(files.AuthorizeProcessRoot(alice).IsOK(), "Einträge ohne Zuordnung (frühere Läufe) bleiben erlaubt.");

            access.Assign(Path.Combine(sub, "bob.txt"), "2");
            ResultErr denied = files.AuthorizeProcessRoot(alice);
            Assert.IsFalse(denied.IsOK());
            StringAssert.Contains(denied.Message, "bob.txt");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ManagedRootAndLegacySystemDataAreNoProcessRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string apps = Path.Combine(root, ".zehos-apps");
        Directory.CreateDirectory(Path.Combine(apps, "spiel"));
        try
        {
            var access = NewAccess(root);
            // .zehos-Namen sind schon keine verwalteten Pfade (keine Zuordnung möglich); die Sperre gilt trotzdem für Administratoren.
            var admin = new FileService(root, access: access, session: new UserSession("0", "admin", true, root));
            Assert.IsFalse(admin.AuthorizeProcessRoot(root).IsOK(), "Die Verwaltungswurzel (/data) ist nie Prozesswurzel.");
            Assert.IsFalse(admin.AuthorizeProcessRoot(apps).IsOK());
            Assert.IsFalse(admin.AuthorizeProcessRoot(Path.Combine(apps, "spiel")).IsOK());
            Assert.IsFalse(new FileService(root, access: access, session: new UserSession()).AuthorizeProcessRoot(apps).IsOK(),
                "Ohne Anmeldung gibt es keine Prozesswurzel.");
            Assert.IsFalse(new FileService(root).AuthorizeProcessRoot(apps).IsOK(), "Ohne Rechteverwaltung gibt es keine Prozesswurzel.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RootProfileSystemDirectoriesAreNoProcessRoot()
    {
        // Root-Profil: Verwaltungswurzel ist das Dateisystem selbst. Abgelehnt wird vor jedem Zugriff auf die Platte.
        // Wurzel des Hostlaufwerks statt "/": Unter Windows ist "/" kein voll qualifizierter Pfad, ResolvePath scheiterte dort
        // schon an Path.GetFullPath (Muster wie SystemDataPathTests); im Kernel ist die Wurzel "/".
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        var access = new FileAccessControl(root, StringComparison.Ordinal);
        access.Initialize();
        var admin = new FileService(root, access: access, session: new UserSession("0", "admin", true, Path.Combine(root, "root")));
        foreach (string relative in new[] { "", "etc", "etc/zehos", "etc/ssl", "var/lib", "var/lib/zehos/apps", "boot/efi" })
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            ResultErr result = admin.AuthorizeProcessRoot(path);
            Assert.IsFalse(result.IsOK(), path);
            StringAssert.Contains(result.Message, "ProcessRootDenied", path);
        }
    }

    private static FileAccessControl NewAccess(string root)
    {
        var access = new FileAccessControl(root);
        access.Initialize();
        access.Assign(root, "0");
        access.Grant(root, "*", FilePermission.Read);
        access.Persist = ResultErr.CreateSuccess;
        return access;
    }
}
