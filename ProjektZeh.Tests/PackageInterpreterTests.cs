using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Tests;

/// <summary>
/// N3-E: Der Interpreter (PT_INTERP) des ersten Programms von <c>run --pkg</c> kommt aus der gebundenen Paketwurzel, mit denselben
/// Regeln wie ein spaeteres execve (Links, Ausfuehrrecht, Inhaltspruefung) und aus dem gebundenen Indexstand.
/// </summary>
public sealed partial class PackageIndex2Tests
{
    private static string Interpreter(PackageRootStore store, string path)
    {
        long r = LinuxInterpreterFile.Read(store, path, 1000, 1000, out byte[] content);
        return r < 0 ? "errno " + -r : Encoding.ASCII.GetString(content);
    }

    [TestMethod]
    public void InterpreterComesFromTheBoundPackageRootThroughLinks()
    {
        var (_, service) = Installed();
        using var lease = service.Bind().Value!;
        RootOf(lease.Index, out PackageRootStore store);
        Assert.AreEqual("musl r1", Interpreter(store, "/lib/ld-musl-x86_64.so.1"));
        // Datei- und Ordnerlink ueber Paketgrenzen (usr/lib64 -> lib, libc.so -> ../../lib/ld-musl…) wie beim execve.
        Assert.AreEqual("musl r1", Interpreter(store, "/usr/lib64/libc.so"));
    }

    [TestMethod]
    public void InterpreterOfARunningStartStaysAcrossUpgradeAndARestartTakesTheNewOne()
    {
        var (_, service) = Installed();
        var lease = service.Bind().Value!;
        RootOf(lease.Index, out PackageRootStore running);
        UpgradeMusl(service);

        // Laufender Start liest weiter seinen Stand (etwa ein spaeteres execve), ein Neustart bindet den neuen.
        Assert.AreEqual("musl r1", Interpreter(running, "/lib/ld-musl-x86_64.so.1"));
        using var restart = service.Bind().Value!;
        RootOf(restart.Index, out PackageRootStore next);
        Assert.AreEqual("musl r2 neu", Interpreter(next, "/lib/ld-musl-x86_64.so.1"));
        lease.Dispose();
    }

    [TestMethod]
    public void MissingOrUnusableInterpreterIsAnErrnoNotAnImage()
    {
        var (_, service) = Installed();
        using var lease = service.Bind().Value!;
        RootOf(lease.Index, out PackageRootStore store);
        Assert.AreEqual("errno " + LinuxErrno.ENOENT, Interpreter(store, "/lib/ld-linux-x86-64.so.2"));
        Assert.AreEqual("errno " + LinuxErrno.EACCES, Interpreter(store, "/usr/share/app/readme")); // kein Ausfuehrrecht
        Assert.AreEqual("errno " + LinuxErrno.EACCES, Interpreter(store, "/usr/lib")); // Verzeichnis

        // Veraenderter Inhalt bei gleicher Laenge: die Inhaltspruefung der Wurzel verweigert den Interpreter.
        string location = lease.Index.Resolve("/lib/ld-musl-x86_64.so.1").Value!.Location!;
        File.WriteAllText(location, "musl XX", Encoding.ASCII);
        RootOf(lease.Index, out PackageRootStore fresh);
        Assert.AreEqual("errno " + LinuxErrno.EIO, Interpreter(fresh, "/lib/ld-musl-x86_64.so.1"));
        Assert.IsTrue(service.BoundUsers().Single().Users == 1);
    }
}
