using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>V34: stat meldet als Eigentümer den Benutzer des Prozesses (Ladybird vergleicht st_uid mit getuid).</summary>
[TestClass]
public sealed class LinuxFileOwnerTests
{
    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    [TestMethod]
    public void StatMeldetDieBenutzernummerDesDateisystems()
    {
        var term = new TerminalDescriptor(new Lines());
        var fs = new LinuxFileSystem(new MemoryFileStore(), term, uid: 1000, gid: 1000);
        Assert.AreEqual(0L, fs.Stat("/", true, out LinuxStat st));
        Assert.AreEqual(1000u, st.Uid);
        Assert.AreEqual(1000u, st.Gid);
    }
}
