using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Input;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MigrationInterruptTests
{
    [TestMethod]
    public void ControlCInterruptsButCopyAndResetDoNot()
    {
        int calls = 0;
        var shell = new ShellService { InterruptJob = _ => { calls++; return true; } };
        var terminal = new TerminalApp(shell, new byte[256 * 16], 8, 16, 80);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: 46, IsTextKey: true));
        Assert.AreEqual(1, calls);
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: 46));
        Assert.AreEqual(1, calls, "Strg+Shift+C bleibt Kopieren.");
        terminal.Handle(new(InputEventType.Reset));
        terminal.Handle(new(InputEventType.KeyDown, KeyCode: 46, IsTextKey: true));
        Assert.AreEqual(1, calls, "Reset verwirft den alten Modifierzustand.");
    }

    [TestMethod]
    public void SecretInputAndClosedShellDoNotForwardAnInterrupt()
    {
        int calls = 0;
        var shell = new ShellService { InterruptJob = _ => { calls++; return true; } };
        var output = new Output();
        shell.BeginSecretInput(_ => () => () => ResultErr.CreateSuccess());
        Assert.IsFalse(shell.TryInterrupt(output));
        shell.CancelSecretInput();
        Assert.IsTrue(shell.TryInterrupt(output));
        shell.Close();
        Assert.IsFalse(shell.TryInterrupt(output));
        Assert.AreEqual(1, calls);
    }

    private sealed class Output : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }
}
