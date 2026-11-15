using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shell;
using ProjektZeh.Shared;
namespace ProjektZeh.Tests;
[TestClass]
public sealed class ShellFeatureTests
{
    private sealed class Output : ITextOutput
    {
        public readonly List<string> Lines = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
    [TestMethod]
    public void Pipeline_QuotedOperatorAndFilters()
    {
        var shell = new ShellService(); var output = new Output();
        Assert.IsTrue(shell.ExecuteLine("echo \"a|b > c\" | grep b | wc -l", output).IsOK());
        CollectionAssert.AreEqual(new[] { "1" }, output.Lines);
    }
    [TestMethod]
    public void Pipeline_StopsOnDeniedStage()
    {
        var shell = new ShellService([], authorize: name => name == "grep"
            ? ResultErr.CreateFailure(ResultCode.Failure, "denied") : ResultErr.CreateSuccess());
        var output = new Output();
        Assert.IsFalse(shell.ExecuteLine("echo a | grep a | wc", output).IsOK());
        Assert.AreEqual(0, output.Lines.Count);
    }
    [TestMethod]
    public void Alias_IsLocalAndCycleFails()
    {
        var shell = new ShellService(); var output = new Output();
        Assert.IsTrue(shell.ExecuteLine("alias a=echo hello", output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("a", output).IsOK());
        Assert.AreEqual("hello", output.Lines[0]);
        Assert.IsFalse(new ShellService().ExecuteLine("a", output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("alias a=b", output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("alias b=a", output).IsOK());
        Assert.IsFalse(shell.ExecuteLine("a", output).IsOK());
    }
}
