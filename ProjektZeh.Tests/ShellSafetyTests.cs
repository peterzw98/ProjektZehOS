using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.Shared;
namespace ProjektZeh.Tests;

[TestClass]
public sealed class ShellSafetyTests
{
    private sealed class Output : ITextOutput
    {
        public string Text { get; private set; } = "";
        public void WriteLine(string text) => Text += text + "\n";
        public void Clear() => Text = "";
    }

    [TestMethod]
    public void Redirect_ReplaceAppendAndFailureKeepOriginal()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var shell = new ShellService(files: new FileService(directory)); var output = new Output();
            Assert.IsTrue(shell.ExecuteLine("echo one > \"test file\"", output).IsOK());
            Assert.IsTrue(shell.ExecuteLine("echo two >> \"test file\"", output).IsOK());
            Assert.AreEqual("one\ntwo\n", File.ReadAllText(Path.Combine(directory,"test file")));
            Assert.IsFalse(shell.ExecuteLine("notfound > \"test file\"", output).IsOK());
            Assert.AreEqual("one\ntwo\n", File.ReadAllText(Path.Combine(directory,"test file")));
            Assert.IsTrue(shell.ExecuteLine("grep two \"test file\" | wc -w", output).IsOK());
            Assert.AreEqual("1\n", output.Text);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void SecretCommands_CannotUsePipesOrRedirectEvenViaAlias()
    {
        bool called = false;
        var shell = new ShellService([new ShellCommand("login","login", "",true, (_,_)=>called=true)]);
        var output = new Output();
        Assert.IsFalse(shell.ExecuteLine("login > x", output).IsOK());
        Assert.IsFalse(shell.ExecuteLine("echo password | login", output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("alias l=login", output).IsOK());
        Assert.IsFalse(shell.ExecuteLine("l | wc", output).IsOK());
        Assert.IsFalse(called);
        Assert.AreEqual(0, shell.History.Count);
    }

    [TestMethod]
    public void Alias_AuthorizesExpandedCommand()
    {
        var shell = new ShellService([], authorize: n=>n=="echo" ? ResultErr.CreateFailure(ResultCode.Failure,"denied") : ResultErr.CreateSuccess());
        var output = new Output();
        Assert.IsTrue(shell.ExecuteLine("alias e=echo",output).IsOK());
        Assert.IsFalse(shell.ExecuteLine("e hello",output).IsOK());
        Assert.AreEqual("",output.Text);
    }

    [TestMethod]
    public void OutputAndStages_AreBounded()
    {
        var shell = new ShellService(); var output = new Output();
        Assert.IsFalse(shell.ExecuteLine("echo " + new string('x', 65536) + " | wc",output).IsOK());
        Assert.AreEqual("", output.Text);
        Assert.IsFalse(shell.ExecuteLine("echo a" + string.Concat(System.Linq.Enumerable.Repeat(" | wc",16)),output).IsOK());
    }

    [TestMethod]
    public void Filters_PreserveBlankLinesAndApplyCounts()
    {
        var shell = new ShellService(); var output = new Output();
        Assert.IsTrue(shell.ExecuteLine("echo \"b\na\na\n\" | sort | uniq | tail -n 2",output).IsOK());
        Assert.AreEqual("a\nb\n",output.Text);
        output.Clear();
        Assert.IsTrue(shell.ExecuteLine("echo \"a\n\" | wc -l",output).IsOK());
        Assert.AreEqual("2\n",output.Text);
    }

    [TestMethod]
    public void History_IsBoundedAndClearedOnIdentityChange()
    {
        int generation = 0;
        var shell = new ShellService(getSessionGeneration:()=>generation); var output = new Output();
        for(int i=0;i<40;i++) Assert.IsTrue(shell.ExecuteLine("echo "+i,output).IsOK());
        Assert.AreEqual(32,shell.History.Count);
        shell.ExecuteLine("alias e=echo",output);
        shell.ExecuteLine("echo page | less",output);
        generation++;
        Assert.AreEqual(1,shell.SessionGeneration);
        Assert.AreEqual(0,shell.History.Count);
        Assert.IsNull(shell.PagerText);
        Assert.IsFalse(shell.ExecuteLine("e",output).IsOK());
    }

    [TestMethod]
    public void Completion_CommandsQuotedPathsAndSecretInput()
    {
        string directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory,"some file.txt"),"a");
            var shell=new ShellService(files:new FileService(directory));
            Assert.AreEqual("echo",shell.Complete("ech",3));
            Assert.AreEqual("cat \"some file.txt\"",shell.Complete("cat \"some fi",12));
            Assert.AreEqual("cat \"some file.txt\"", shell.Complete("cat \"some fi\"", 12));
            Assert.AreEqual("cat \"some file.txt\"", shell.Complete("cat \"some fi\"", 13));
            shell.BeginSecretInput(_=>()=>()=>ResultErr.CreateSuccess());
            Assert.AreEqual("ech",shell.Complete("ech",3));
        }
        finally { Directory.Delete(directory,true); }
    }

    [TestMethod]
    public void Editor_SearchAndSecrets()
    {
        var editor=new LineEditor();
        editor.SetText("echo older"); editor.Handle(new(InputEventType.KeyDown,(byte)PhysicalKey.Enter),out _);
        editor.SetText("echo newer"); editor.Handle(new(InputEventType.KeyDown,(byte)PhysicalKey.Enter),out _);
        editor.SetText("echo"); Assert.IsTrue(editor.SearchHistory()); Assert.AreEqual("echo newer",editor.Text);
        Assert.IsTrue(editor.SearchHistory()); Assert.AreEqual("echo older",editor.Text);
        editor.ResetHistory(); editor.SetText("passwd secret"); editor.Handle(new(InputEventType.KeyDown,(byte)PhysicalKey.Enter),out _);
        Assert.IsFalse(editor.SearchHistory());
    }
    [TestMethod]
    public void Alias_PreservesQuotedArgumentGroupsAndOperators()
    {
        var shell=new ShellService(); var output=new Output();
        Assert.IsTrue(shell.ExecuteLine("alias g=grep \"a b\"",output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("echo \"a b\" | g",output).IsOK());
        Assert.AreEqual("a b\n",output.Text);
        output.Clear();
        Assert.IsTrue(shell.ExecuteLine("alias e=echo \"a|b > c\"",output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("e",output).IsOK());
        Assert.AreEqual("a|b > c\n",output.Text);
    }

    [TestMethod]
    public void Editor_ShellHistoryFilterProtectsSecretAliasesAndReset()
    {
        var shell=new ShellService(); var output=new Output();
        shell.ExecuteLine("alias p=passwd",output);
        var editor=new LineEditor { HistoryFilter=shell.CanRemember };
        editor.SetText("p do-not-record"); editor.Handle(new(InputEventType.KeyDown,(byte)PhysicalKey.Enter),out _);
        Assert.IsFalse(editor.SearchHistory());
        shell.BeginSecretInput(_=>()=>()=>ResultErr.CreateSuccess());
        editor.IsSecret=true; editor.SetText("secret"); editor.Complete(shell);
        Assert.AreEqual("secret",editor.Text);
        Assert.IsFalse(editor.SearchHistory());
    }

    [TestMethod]
    public void Close_IsIdempotentAndClearsPrivateState()
    {
        var shell=new ShellService(); var output=new Output(); int calls=0;
        shell.OnClose=()=>calls++;
        shell.ExecuteLine("echo private | less",output);
        shell.Close(); shell.Close();
        Assert.AreEqual(1,calls); Assert.IsNull(shell.PagerText); Assert.AreEqual(0,shell.History.Count);
        Assert.IsFalse(shell.ExecuteLine("echo",output).IsOK());
    }
}