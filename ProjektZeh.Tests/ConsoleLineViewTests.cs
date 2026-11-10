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
}
