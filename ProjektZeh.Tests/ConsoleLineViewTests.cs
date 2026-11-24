using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class ConsoleLineViewTests
{
    [DataTestMethod]
    [DataRow("echo short", "zehos> echo short")]
    [DataRow("", "zehos>")]
    public void Line_ShortenedBeforeRedraw_OverwritesOldDraft(string command, string expected)
    {
        char[] row = "zehos> echo abcdefghijklmnopqrstuvwxyz".ToCharArray();
        string written = ConsoleLineView.FormatLine(command, row.Length + 1);
        written.CopyTo(0, row, 0, written.Length);
        Assert.AreEqual(expected, new string(row).TrimEnd());
    }

    [TestMethod]
    public void Line_CommandWiderThanScreen_PreservesEntireCommand()
    {
        string written = ConsoleLineView.FormatLine("echo abcdefghijklmnopqrstuvwxyz", 16);
        Assert.AreEqual("zehos> echo abcdefghijklmnopqrstuvwxyz", written);
    }

    [TestMethod]
    public void ChangedSpan_UnchangedRow_WritesNothing()
    {
        Assert.IsFalse(ConsoleLineView.TryGetChangedSpan("ZehOS | Laufzeit: 12 s", "ZehOS | Laufzeit: 12 s", out _, out int length));
        Assert.AreEqual(0, length);
    }

    [TestMethod]
    public void ChangedSpan_UnknownOrResizedRow_WritesWholeRow()
    {
        Assert.IsTrue(ConsoleLineView.TryGetChangedSpan(null, "zehos> ", out int start, out int length));
        Assert.AreEqual((0, 7), (start, length));
        Assert.IsTrue(ConsoleLineView.TryGetChangedSpan("zehos>  ", "zehos> ", out start, out length));
        Assert.AreEqual((0, 7), (start, length));
    }

    [DataTestMethod]
    [DataRow("Laufzeit: 9 s  ", "Laufzeit: 10 s ", 10, 4)]
    [DataRow("Laufzeit: 19 s ", "Laufzeit: 20 s ", 10, 2)]
    [DataRow("zehos> ab      ", "zehos> abc     ", 9, 1)]
    [DataRow("zehos> abc     ", "zehos> ab      ", 9, 1)]
    [DataRow("a-------------z", "b-------------y", 0, 15)]
    public void ChangedSpan_ChangedColumns_CoversFirstToLastDifference(string shown, string text, int expectedStart, int expectedLength)
    {
        Assert.IsTrue(ConsoleLineView.TryGetChangedSpan(shown, text, out int start, out int length));
        Assert.AreEqual((expectedStart, expectedLength), (start, length));
        // Der geschriebene Ausschnitt ueber dem alten Stand ergibt genau die neue Zeile.
        Assert.AreEqual(text, shown[..start] + text.Substring(start, length) + shown[(start + length)..]);
    }
}
