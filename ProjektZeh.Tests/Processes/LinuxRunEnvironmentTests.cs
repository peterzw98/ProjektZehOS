using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V37: Umgebung von run (Standardwerte, NAME=WERT vor dem Programm, Grenzen, anzulegende Ordner).</summary>
[TestClass]
public sealed class LinuxRunEnvironmentTests
{
    [TestMethod]
    public void DefaultsKeepOldOrderAndAddWritableDirectories()
    {
        string[] expected =
        [
            "ZEHOS_ABI=1", "LANG=C", "PATH=/usr/bin:/bin", "HOME=/",
            "TMPDIR=/tmp", "XDG_RUNTIME_DIR=/tmp/xdg-runtime", "XDG_CACHE_HOME=/.cache",
        ];
        CollectionAssert.AreEqual(expected, LinuxRunEnvironment.Defaults);
    }

    [TestMethod]
    public void WithoutAssignmentsArgumentsAndDefaultsPassUnchanged()
    {
        Assert.IsTrue(LinuxRunEnvironment.Split(["prog", "a=1", "b"], out string[] env, out string[] argv, out string error), error);
        CollectionAssert.AreEqual(LinuxRunEnvironment.Defaults, env);

        // Zuweisungen nach dem Programm sind Argumente wie bei env.
        string[] expected = ["prog", "a=1", "b"];
        CollectionAssert.AreEqual(expected, argv);
    }

    [TestMethod]
    public void AssignmentsReplaceInPlaceOrAppendAndLastWins()
    {
        Assert.IsTrue(LinuxRunEnvironment.Split(["LANG=de_DE.UTF-8", "FOO=1", "FOO=2", "EMPTY=", "HOME_X=y", "./p", "x"],
            out string[] env, out string[] argv, out string error), error);
        Assert.AreEqual("LANG=de_DE.UTF-8", env[1]);
        Assert.AreEqual("HOME=/", env[3]);
        string[] added = ["FOO=2", "EMPTY=", "HOME_X=y"];
        string[] program = ["./p", "x"];
        CollectionAssert.AreEqual(added, env.Skip(LinuxRunEnvironment.Defaults.Length).ToArray());
        CollectionAssert.AreEqual(program, argv);
    }

    [TestMethod]
    public void ValueMayContainEqualsAndSpaces()
    {
        Assert.IsTrue(LinuxRunEnvironment.Split(["OPTS=a=b c", "p"], out string[] env, out _, out string error), error);
        Assert.AreEqual("OPTS=a=b c", env[^1]);
    }

    [TestMethod]
    public void WordsWithoutShellNameAreThePrograms()
    {
        string[] programs = ["./a=b", "=x", "1A=x", "A-B=x", "/bin/x=1", "plain"];
        foreach (string word in programs)
        {
            Assert.IsFalse(LinuxRunEnvironment.IsAssignment(word), word);
            Assert.IsTrue(LinuxRunEnvironment.Split([word, "arg"], out string[] env, out string[] argv, out string error), error);
            Assert.AreEqual(word, argv[0]);
            CollectionAssert.AreEqual(LinuxRunEnvironment.Defaults, env);
        }

        Assert.IsTrue(LinuxRunEnvironment.IsAssignment("_a1=x"));
        Assert.IsTrue(LinuxRunEnvironment.IsAssignment("A="));
    }

    [TestMethod]
    public void MissingProgramFails()
    {
        Assert.IsFalse(LinuxRunEnvironment.Split(["A=1"], out string[] env, out string[] argv, out string error));
        StringAssert.Contains(error, "Programm fehlt");
        Assert.AreEqual(0, env.Length);
        Assert.AreEqual(0, argv.Length);
        Assert.IsFalse(LinuxRunEnvironment.Split([], out _, out _, out _));
    }

    [TestMethod]
    public void CountLimitIsInclusive()
    {
        string[] max = Enumerable.Range(0, LinuxRunEnvironment.MaxAssignments).Select(i => "V" + i + "=1").Append("p").ToArray();
        Assert.IsTrue(LinuxRunEnvironment.Split(max, out _, out _, out string error), error);

        string[] over = Enumerable.Range(0, LinuxRunEnvironment.MaxAssignments + 1).Select(i => "V" + i + "=1").Append("p").ToArray();
        Assert.IsFalse(LinuxRunEnvironment.Split(over, out _, out _, out error));
        StringAssert.Contains(error, "Zuweisungen");
    }

    [TestMethod]
    public void LengthLimitCountsUtf8Bytes()
    {
        string fits = "A=" + new string('x', LinuxRunEnvironment.MaxAssignmentBytes - 2);
        Assert.IsTrue(LinuxRunEnvironment.Split([fits, "p"], out _, out _, out string error), error);

        // Ein Umlaut sind zwei Bytes: gleiche Zeichenzahl, aber über der Grenze.
        string over = "A=" + new string('x', LinuxRunEnvironment.MaxAssignmentBytes - 3) + "ä";
        Assert.IsFalse(LinuxRunEnvironment.Split([over, "p"], out _, out _, out error));
        StringAssert.Contains(error, "Bytes");
    }

    [TestMethod]
    public void TotalLimitAndNulAreRejected()
    {
        string big = new string('x', LinuxRunEnvironment.MaxAssignmentBytes - 4);
        string[] words = Enumerable.Range(0, 9).Select(i => "B" + i + "=" + big).Append("p").ToArray();
        Assert.IsFalse(LinuxRunEnvironment.Split(words, out _, out _, out string error));
        StringAssert.Contains(error, "Umgebung");

        Assert.IsFalse(LinuxRunEnvironment.Split(["A=x\0y", "p"], out _, out _, out error));
        StringAssert.Contains(error, "NUL");
    }

    [TestMethod]
    public void DirectoriesOnlyForDefaultValuesParentsFirst()
    {
        CollectionAssert.AreEqual(
            new[] { ("/tmp", 0x1FFu), ("/tmp/xdg-runtime", 0x1C0u), ("/.cache", 0x1EDu) },
            LinuxRunEnvironment.DirectoriesFor(LinuxRunEnvironment.Defaults).ToArray());

        Assert.IsTrue(LinuxRunEnvironment.Split(["TMPDIR=/data/t", "XDG_CACHE_HOME=/c", "p"], out string[] env, out _, out string error), error);
        // /tmp bleibt als Elternordner des Laufzeitverzeichnisses; selbst gesetzte Pfade legt run nicht an.
        CollectionAssert.AreEqual(new[] { ("/tmp", 0x1FFu), ("/tmp/xdg-runtime", 0x1C0u) }, LinuxRunEnvironment.DirectoriesFor(env).ToArray());

        Assert.IsTrue(LinuxRunEnvironment.Split(["TMPDIR=/t", "XDG_RUNTIME_DIR=/r", "XDG_CACHE_HOME=/c", "p"], out env, out _, out error), error);
        Assert.AreEqual(0, LinuxRunEnvironment.DirectoriesFor(env).Count);
    }

    [TestMethod]
    public void DirectoriesCacheAloneAndTmpAloneAreIndependent()
    {
        Assert.IsTrue(LinuxRunEnvironment.Split(["TMPDIR=/tmp", "XDG_RUNTIME_DIR=/r", "XDG_CACHE_HOME=/c", "p"], out string[] env, out _, out string error), error);
        CollectionAssert.AreEqual(new[] { ("/tmp", 0x1FFu) }, LinuxRunEnvironment.DirectoriesFor(env).ToArray());

        Assert.IsTrue(LinuxRunEnvironment.Split(["TMPDIR=/t", "XDG_RUNTIME_DIR=/r", "p"], out env, out _, out error), error);
        CollectionAssert.AreEqual(new[] { ("/.cache", 0x1EDu) }, LinuxRunEnvironment.DirectoriesFor(env).ToArray());
    }
}
