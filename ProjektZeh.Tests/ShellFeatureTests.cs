using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
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
    // Schickt die Zeile ueber den Editor ab und prueft am Verlauf, ob Pfeil hoch sie zurueckholt.
    private static bool Remembers(ShellService shell, string line)
    {
        var editor = new LineEditor { HistoryFilter = shell.CanRemember };
        editor.SetText(line);
        editor.Handle(new(InputEventType.KeyDown, (byte)PhysicalKey.Enter), out _);
        editor.Handle(new(InputEventType.KeyDown, (byte)PhysicalKey.Up), out _);
        return editor.Text == line;
    }
    [TestMethod]
    public void History_NormalCommand_IsRemembered()
    {
        Assert.IsTrue(Remembers(new ShellService(), "echo hallo"));
    }
    [TestMethod]
    public void History_DuringSecretInput_RemembersNothing()
    {
        var shell = new ShellService();
        shell.BeginSecretInput(_ => () => () => ResultErr.CreateSuccess());
        Assert.IsTrue(shell.IsSecretInput);
        Assert.IsFalse(shell.IsBusy);
        Assert.IsFalse(Remembers(shell, "echo hallo"));
    }
    [TestMethod]
    public void History_WhilePasswordWorkIsBusy_RemembersNothing()
    {
        var shell = new ShellService(); var output = new Output();
        using var release = new ManualResetEvent(false);
        shell.BeginSecretInput(_ => () => { release.WaitOne(); return () => ResultErr.CreateSuccess(); });
        try
        {
            Assert.IsTrue(shell.ExecuteLine("secret", output).IsOK());
            Assert.IsTrue(shell.IsBusy);
            Assert.IsFalse(shell.IsSecretInput);
            Assert.IsFalse(Remembers(shell, "echo hallo"));
        }
        finally
        {
            release.Set();
            SpinWait.SpinUntil(() => { shell.Poll(output); return !shell.IsBusy; }, 5000);
        }
    }
    [DataTestMethod]
    [DataRow("passwd")]
    [DataRow("login gast")]
    [DataRow("echo x | user list")]
    public void History_SensitiveCommand_IsNotRemembered(string line)
    {
        Assert.IsFalse(Remembers(new ShellService(), line));
    }
    [TestMethod]
    public void History_AliasDefinition_IsNotRemembered()
    {
        Assert.IsFalse(Remembers(new ShellService(), "alias g=echo geheim"));
    }
}
