using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SecretInputTests
{
    [TestMethod]
    public void SecretTextIsMaskedAndNeverAddedToHistory()
    {
        var editor = new LineEditor { IsSecret = true };
        editor.Handle(new InputEvent(InputEventType.Text, Character: 's'), out _);
        Assert.AreEqual("*", editor.DisplayText);
        editor.Handle(new InputEvent(InputEventType.KeyDown, (byte)PhysicalKey.Enter), out string? secret);
        Assert.AreEqual("s", secret);
        editor.IsSecret = false;
        editor.Handle(new InputEvent(InputEventType.KeyDown, (byte)PhysicalKey.Up), out _);
        Assert.AreEqual("", editor.Text);
    }

    [TestMethod]
    public void PasswordWorkDoesNotBlockAndAppliesOnlyDuringForegroundPoll()
    {
        var shell = new ShellService();
        var output = new TerminalBuffer(80);
        using var release = new ManualResetEvent(false);
        using var done = new ManualResetEvent(false);
        bool applied = false;
        shell.BeginSecretInput(_ => () =>
        {
            release.WaitOne();
            done.Set();
            return () => { applied = true; return ResultErr.CreateSuccess(); };
        });
        Assert.IsTrue(shell.IsSecretInput);
        Assert.IsTrue(shell.ExecuteLine("secret", output).IsOK());
        Assert.IsTrue(shell.IsBusy);
        Assert.IsFalse(applied);
        release.Set();
        Assert.IsTrue(done.WaitOne(5000));
        Assert.IsTrue(SpinWait.SpinUntil(() => { shell.Poll(output); return !shell.IsBusy; }, 5000));
        Assert.IsTrue(applied);
    }
    [TestMethod]
    public void ClosedPasswordWorkersRemainGloballyBoundedAndNeverApplyTheirResults()
    {
        using var release = new ManualResetEvent(false);
        var output = new TerminalBuffer(80);
        var first = new ShellService();
        var second = new ShellService();
        var third = new ShellService();
        bool applied = false;
        System.Func<string, System.Func<System.Func<ResultErr>>> prepare = _ => () =>
        {
            release.WaitOne();
            return () => { applied = true; return ResultErr.CreateSuccess(); };
        };
        try
        {
            first.BeginSecretInput(prepare); second.BeginSecretInput(prepare); third.BeginSecretInput(prepare);
            Assert.IsTrue(first.ExecuteLine("secret", output).IsOK());
            Assert.IsTrue(second.ExecuteLine("secret", output).IsOK());
            first.Close(); second.Close();
            Assert.IsTrue(ShellService.HasRunningPasswordOperations);
            Assert.IsFalse(third.ExecuteLine("secret", output).IsOK());
            Assert.IsTrue(third.IsSecretInput);
            Assert.IsFalse(third.IsBusy);
            release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => !ShellService.HasRunningPasswordOperations, 5000));
            Assert.IsTrue(SpinWait.SpinUntil(() => { first.Poll(output); second.Poll(output); return !first.IsBusy && !second.IsBusy; }, 5000));
            Assert.IsFalse(applied);
            Assert.IsTrue(third.ExecuteLine("secret", output).IsOK());
            Assert.IsTrue(SpinWait.SpinUntil(() => { third.Poll(output); return !third.IsBusy; }, 5000));
            Assert.IsTrue(applied);
        }
        finally
        {
            release.Set();
            SpinWait.SpinUntil(() => !ShellService.HasRunningPasswordOperations, 5000);
            first.Close(); second.Close(); third.Close();
        }
    }

}
