using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>V16: sockaddr_un lesen und schreiben, Namenstabelle der Unix-Sockets (abstrakt und Pfad).</summary>
[TestClass]
public sealed class LinuxUnixNamesTests
{
    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text)
        {
        }

        public void Clear()
        {
        }
    }

    private static byte[] Addr(string name, bool isAbstract, int? length = null)
    {
        byte[] n = Encoding.UTF8.GetBytes(name);
        var b = new byte[LinuxUnixAddress.MaxLength];
        b[0] = 1;
        n.CopyTo(b, isAbstract ? 3 : 2);
        int len = length ?? (isAbstract ? 3 + n.Length : LinuxUnixAddress.MaxLength);
        return b.AsSpan(0, len).ToArray();
    }

    private static LinuxFileSystem NewFs() => new(new MemoryFileStore(), new TerminalDescriptor(new Lines()));

    [TestMethod]
    public void ParseErkenntPfadAbstraktUndUnbenannt()
    {
        Assert.AreEqual(0, LinuxUnixAddress.Parse(Addr("sock", false), out LinuxUnixAddress path));
        Assert.IsFalse(path.IsAbstract);
        Assert.AreEqual("sock", path.Path);
        Assert.AreEqual(7, path.Length); // Linux: Familie + Name + Nullbyte

        Assert.AreEqual(0, LinuxUnixAddress.Parse(Addr("zehos-v16", true), out LinuxUnixAddress abs));
        Assert.IsTrue(abs.IsAbstract);
        Assert.AreEqual(12, abs.Length);
        Assert.AreEqual("\0zehos-v16", abs.Key);

        Assert.AreEqual(0, LinuxUnixAddress.Parse(Addr("", false, 2), out LinuxUnixAddress none));
        Assert.IsTrue(none.IsUnnamed);
        Assert.AreEqual(2, none.Length);
    }

    [TestMethod]
    public void ParseAbstraktBehaeltNullbytesBisZurLaenge()
    {
        // Abstrakte Namen sind binär: "a\0b" und "a" sind verschiedene Namen.
        byte[] raw = Addr("a", true, 6);
        Assert.AreEqual(0, LinuxUnixAddress.Parse(raw, out LinuxUnixAddress a));
        Assert.AreEqual(4, a.Name.Length);
        Assert.AreNotEqual(Addr("a", true).Length, raw.Length);
    }

    [TestMethod]
    public void ParseLehntFalscheFamilieUndLaengeAb()
    {
        byte[] bad = Addr("sock", false);
        bad[0] = 2; // AF_INET
        Assert.AreEqual(-22, LinuxUnixAddress.Parse(bad, out _));
        Assert.AreEqual(-22, LinuxUnixAddress.Parse(new byte[1], out _));
        Assert.AreEqual(-22, LinuxUnixAddress.Parse(new byte[LinuxUnixAddress.MaxLength + 1], out _));
    }

    [TestMethod]
    public void FormatKuerztAufPufferlaenge()
    {
        Assert.AreEqual(0, LinuxUnixAddress.Parse(Addr("sock", false), out LinuxUnixAddress a));
        var full = new byte[16];
        Assert.AreEqual(7, a.Format(full));
        CollectionAssert.AreEqual(new byte[] { 1, 0, (byte)'s', (byte)'o', (byte)'c', (byte)'k', 0 }, full.AsSpan(0, 7).ToArray());
        var small = new byte[4];
        Assert.AreEqual(4, a.Format(small));
        CollectionAssert.AreEqual(new byte[] { 1, 0, (byte)'s', (byte)'o' }, small);
    }

    [TestMethod]
    public void AbstrakterNameEinmalVergebenUndNachReleaseFrei()
    {
        var names = new LinuxUnixNames();
        object s1 = new(), s2 = new();
        Assert.AreEqual(0, names.BindAbstract("\0x", s1));
        Assert.AreEqual(-98, names.BindAbstract("\0x", s2));
        Assert.AreSame(s1, names.FindAbstract("\0x"));
        names.Release(s1);
        Assert.IsNull(names.FindAbstract("\0x"));
        Assert.AreEqual(0, names.BindAbstract("\0x", s2));
    }

    [TestMethod]
    public void AutobindVergibtVerschiedeneNamenMitNullbyte()
    {
        var names = new LinuxUnixNames();
        byte[] a = names.Autobind(new object());
        byte[] b = names.Autobind(new object());
        Assert.AreEqual(6, a.Length);
        Assert.AreEqual(0, a[0]);
        Assert.AreNotEqual(LinuxUnixNames.KeyOf(a), LinuxUnixNames.KeyOf(b));
        Assert.AreEqual(-98, names.BindAbstract(LinuxUnixNames.KeyOf(a), new object()));
    }

    [TestMethod]
    public void PfadnameLegtDateiAnUndBelegtDenPfad()
    {
        LinuxFileSystem fs = NewFs();
        var names = new LinuxUnixNames();
        object s1 = new();
        Assert.AreEqual(0, names.BindPath(fs, "/sock", s1));
        Assert.AreEqual(0, fs.Access("/sock", 0));
        Assert.AreEqual(-98, names.BindPath(fs, "/sock", new object())); // EADDRINUSE auch für einen zweiten Prozess
        Assert.AreEqual(0, names.FindPath(fs, "/sock", out object? found));
        Assert.AreSame(s1, found);
    }

    [TestMethod]
    public void PfadnameGewoehnlicheDateiUnlinkUndNeueDatei()
    {
        LinuxFileSystem fs = NewFs();
        var names = new LinuxUnixNames();
        Assert.AreEqual(0, fs.Open("/datei", 0x41, 0x1a4, out ILinuxFile? f));
        _ = f!.Close();
        Assert.AreEqual(-98, names.BindPath(fs, "/datei", new object()));
        Assert.AreEqual(0, names.FindPath(fs, "/datei", out object? none));
        Assert.IsNull(none); // Aufrufer meldet ECONNREFUSED

        Assert.AreEqual(-2, names.FindPath(fs, "/fehlt", out _)); // ENOENT

        object s1 = new();
        Assert.AreEqual(0, names.BindPath(fs, "/sock", s1));
        Assert.AreEqual(0, fs.Unlink("/sock"));
        names.Unlinked("/sock"); // wie DoUnlink nach Erfolg
        Assert.AreEqual(-2, names.FindPath(fs, "/sock", out _));

        // Neue gewöhnliche Datei gleichen Namens erreicht den alten Socket nicht.
        Assert.AreEqual(0, fs.Open("/sock", 0x41, 0x1a4, out f));
        _ = f!.Close();
        Assert.AreEqual(0, names.FindPath(fs, "/sock", out object? stale));
        Assert.IsNull(stale);
    }

    [TestMethod]
    public void ReleaseLaesstDateiStehenUndLoestDenNamen()
    {
        LinuxFileSystem fs = NewFs();
        var names = new LinuxUnixNames();
        object s1 = new();
        Assert.AreEqual(0, names.BindPath(fs, "/sock", s1));
        names.Release(s1);
        Assert.AreEqual(0, fs.Access("/sock", 0)); // wie Linux: die Datei bleibt
        Assert.AreEqual(0, names.FindPath(fs, "/sock", out object? gone));
        Assert.IsNull(gone);
    }

    [TestMethod]
    public void RenameNimmtDenNamenMitUndUeberschreibtDasZiel()
    {
        LinuxFileSystem fs = NewFs();
        var names = new LinuxUnixNames();
        object s1 = new(), s2 = new();
        Assert.AreEqual(0, names.BindPath(fs, "/a", s1));
        Assert.AreEqual(0, names.BindPath(fs, "/b", s2));
        Assert.AreEqual(0, fs.Rename("/a", "/b"));
        names.Renamed("/a", "/b");
        Assert.AreEqual(0, names.FindPath(fs, "/b", out object? found));
        Assert.AreSame(s1, found);
        Assert.AreEqual(-2, names.FindPath(fs, "/a", out _));
    }
}
