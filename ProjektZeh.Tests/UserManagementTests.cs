using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Runtime;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UserManagementTests
{
    private const string AdminPassword = "admin-password-123";
    private const string UserPassword = "alice-password-123";
    private string root = null!;
    private AccountService accounts = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), $"zehos-users-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        accounts = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, true);

    private UserSession SetupAdministrator()
    {
        Assert.AreEqual(ResultCode.NotFound, accounts.Load().Code);
        var admin = accounts.CreateSession();
        Assert.IsTrue(accounts.SetupWork(admin, "admin", AdminPassword)()().IsOK());
        return admin;
    }

    private ShellService CreateShell(UserSession identity)
    {
        var files = new FileService(root, access: accounts.Access, session: identity);
        ShellService? shell = null;
        shell = new ShellService(
            [.. new AccountShellCommands(accounts, identity, files, prepare => shell!.BeginSecretInput(prepare), false,
                name => shell!.HasCommand(name)).Commands,
             new ShellCommand("reboot", "reboot", "Testbefehl.", false, (_, output) => output.WriteLine("rebooted"))],
            authorize: command => accounts.Authorize(identity, command));
        return shell;
    }

    private static void WaitForPassword(Func<bool> poll)
    {
        var watch = Stopwatch.StartNew();
        while (!poll()) Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(30), "Kennwortpruefung endet nicht.");
    }

    [TestMethod]
    public void Setup_OnlyWhileDatabaseIsMissing()
    {
        var admin = SetupAdministrator();
        Assert.IsFalse(accounts.NeedsSetup);
        Assert.IsTrue(accounts.IsAdmin(admin));
        Assert.IsFalse(accounts.SetupWork(accounts.CreateSession(), "other", AdminPassword)()().IsOK());
        Assert.IsTrue(accounts.Load().IsOK());

        File.WriteAllText(Path.Combine(root, ".zehos-security.db"), "kaputt");
        Assert.AreEqual(ResultCode.Failure, accounts.Load().Code);
        Assert.IsFalse(accounts.NeedsSetup, "Beschaedigte Konten duerfen keine neue Einrichtung erlauben.");
        Assert.IsFalse(accounts.SetupWork(accounts.CreateSession(), "intruder", AdminPassword)()().IsOK());
        Assert.AreEqual("kaputt", File.ReadAllText(Path.Combine(root, ".zehos-security.db")));
    }

    [TestMethod]
    public void CommandTable_RestrictsNonAdministratorsAndPersists()
    {
        var admin = SetupAdministrator();
        Assert.IsTrue(accounts.PrepareCreateUser(admin, "alice", false, UserPassword)().IsOK());
        var alice = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareLogin(alice, "alice", UserPassword)().IsOK());

        Assert.IsFalse(accounts.Authorize(alice, "reboot").IsOK());
        Assert.IsFalse(accounts.Authorize(alice, "user").IsOK());
        Assert.IsTrue(accounts.Authorize(alice, "ls").IsOK());
        Assert.IsTrue(accounts.Authorize(admin, "reboot").IsOK());
        Assert.IsFalse(accounts.SetCommandAccess(alice, "ls", true).IsOK());
        Assert.IsFalse(accounts.SetCommandAccess(admin, "login", true).IsOK());
        Assert.IsTrue(accounts.SetCommandAccess(admin, "ls", true).IsOK());
        Assert.IsTrue(accounts.SetCommandAccess(admin, "reboot", false).IsOK());

        Assert.IsTrue(accounts.Load().IsOK());
        Assert.IsTrue(accounts.PrepareLogin(alice, "alice", UserPassword)().IsOK());
        Assert.IsFalse(accounts.Authorize(alice, "ls").IsOK());
        Assert.IsTrue(accounts.Authorize(alice, "reboot").IsOK());

        File.WriteAllText(Path.Combine(root, ".zehos-commands.db"), "kaputt");
        Assert.IsFalse(accounts.Load().IsOK());
        Assert.IsNull(alice.UserId);
    }

    [TestMethod]
    public void Shell_DeniedCommandDoesNotRunAndChpermChecksCommand()
    {
        var admin = SetupAdministrator();
        Assert.IsTrue(accounts.PrepareCreateUser(admin, "alice", false, UserPassword)().IsOK());
        var alice = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareLogin(alice, "alice", UserPassword)().IsOK());
        var output = new TerminalBuffer(120);

        Assert.IsFalse(CreateShell(alice).ExecuteLine("reboot", output).IsOK());
        Assert.AreEqual(0, output.Count);
        var adminShell = CreateShell(admin);
        Assert.AreEqual(ResultCode.NotFound, adminShell.ExecuteLine("chperm gibtsnicht *", output).Code);
        Assert.IsTrue(adminShell.ExecuteLine("chperm reboot *", output).IsOK());
        Assert.IsTrue(CreateShell(alice).ExecuteLine("reboot", output).IsOK());
        Assert.AreEqual("rebooted", output.GetLine(output.Count - 1));
    }

    [TestMethod]
    public void DeleteUser_LogsOutTransfersFilesAndKeepsDatabaseLoadable()
    {
        var admin = SetupAdministrator();
        Assert.IsTrue(accounts.PrepareCreateUser(admin, "alice", false, UserPassword)().IsOK());
        var alice = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareLogin(alice, "alice", UserPassword)().IsOK());
        var files = new FileService(alice.HomeDirectory!, access: accounts.Access, session: alice);
        Assert.IsTrue(files.WriteText("notiz.txt", "hallo").IsOK());

        Assert.IsFalse(accounts.DeleteUser(alice, "admin").IsOK());
        Assert.IsFalse(accounts.DeleteUser(admin, "admin").IsOK());
        Assert.IsTrue(accounts.DeleteUser(admin, "alice").IsOK());
        Assert.IsNull(alice.UserId);
        Assert.IsFalse(accounts.PrepareLogin(alice, "alice", UserPassword)().IsOK());
        Assert.IsTrue(accounts.Load().IsOK(), "Verwaiste Rechte duerfen die Kontodatei nicht sperren.");
        Assert.IsTrue(accounts.PrepareLogin(admin, "admin", AdminPassword)().IsOK());
        var adminFiles = new FileService(root, access: accounts.Access, session: admin);
        Assert.AreEqual("hallo", adminFiles.ReadText(Path.Combine(root, "home", "alice", "notiz.txt")).Value);
    }

    [TestMethod]
    public void Password_OwnChangeWorksForeignChangeNeedsAdministrator()
    {
        var admin = SetupAdministrator();
        Assert.IsTrue(accounts.PrepareCreateUser(admin, "alice", false, UserPassword)().IsOK());
        var alice = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareLogin(alice, "alice", UserPassword)().IsOK());

        Assert.IsFalse(accounts.PasswordWork(alice, "admin", "neues-kennwort-1")()().IsOK());
        Assert.IsTrue(accounts.PasswordWork(alice, "alice", "neues-kennwort-1")()().IsOK());
        Assert.IsTrue(accounts.Load().IsOK());
        Assert.IsFalse(accounts.PrepareLogin(alice, "alice", UserPassword)().IsOK());
        Assert.IsTrue(accounts.PrepareLogin(alice, "alice", "neues-kennwort-1")().IsOK());
        Assert.IsTrue(accounts.PrepareLogin(admin, "admin", AdminPassword)().IsOK());
        Assert.IsTrue(accounts.PasswordWork(admin, "alice", "admin-gesetzt-1")()().IsOK());
    }

    [TestMethod]
    public void LoginFlow_SetupRequiresMatchingPasswordsThenLogsIn()
    {
        Assert.AreEqual(ResultCode.NotFound, accounts.Load().Code);
        var identity = accounts.CreateSession();
        var shell = CreateShell(identity);
        var output = new TerminalBuffer(120);
        var login = new LoginFlow(accounts, shell, output);
        Assert.IsTrue(login.IsActive);
        Assert.AreEqual("Administrator anlegen", login.Title);

        login.Submit("admin");
        Assert.IsTrue(login.IsSecret);
        login.Submit(AdminPassword);
        Assert.IsFalse(login.Submit("anderes-kennwort").IsOK());
        // Anfuehrungszeichen im Namen duerfen die erzeugte Befehlszeile nicht veraendern.
        login.Submit("admin\" x");
        login.Submit(AdminPassword);
        Assert.IsFalse(login.Submit(AdminPassword).IsOK());
        Assert.IsFalse(shell.IsBusy);

        login.Submit("admin");
        login.Submit(AdminPassword);
        Assert.IsTrue(login.Submit(AdminPassword).IsOK());
        WaitForPassword(() => shell.Poll(output));
        Assert.AreEqual("admin", identity.UserName);
        Assert.AreEqual("Anmelden", login.Title);
    }

    [TestMethod]
    public void UserSession_LoginAsCopiesIdentityAndCountsChanges()
    {
        var admin = SetupAdministrator();
        var terminal = accounts.CreateSession();
        int generation = terminal.Generation;
        terminal.LoginAs(admin);
        Assert.AreEqual(admin.UserId, terminal.UserId);
        Assert.AreNotEqual(generation, terminal.Generation);
        admin.Logout();
        Assert.IsNotNull(terminal.UserId, "Terminals behalten nach der Uebernahme ihre eigene Identitaet.");
    }

    [TestMethod]
    public void Desktop_LoginScreenGatesInputAndIdentityChangeClosesWindows()
    {
        var admin = SetupAdministrator();
        var identity = accounts.CreateSession();
        var shell = CreateShell(identity);
        var desktop = new Desktop(1024, 768, new byte[4096], 8, 16, () => new ShellService(),
            () => new FileService(root), new DesktopSession(accounts, shell, identity));
        var surface = new PixelSurface(1024, 768, new int[1024 * 768]);
        desktop.Render(surface, 0, 10, 10, PointerButtons.None);

        // Die Terminal-Schaltflaeche der Leiste bleibt ohne Anmeldung wirkungslos.
        desktop.Handle(new(InputEventType.PointerDown, X: 240, Y: 740, Button: PointerButtons.Left));
        Assert.AreEqual(0, desktop.Windows.Count);
        foreach (string line in new[] { "admin", AdminPassword })
        {
            foreach (char character in line) desktop.Handle(new(InputEventType.Text, Character: character));
            desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Enter));
        }
        WaitForPassword(() => { desktop.Update(0); return identity.UserId != null; });
        Assert.AreEqual("admin", identity.UserName);

        desktop.OpenTerminal();
        Assert.AreEqual(1, desktop.Windows.Count);
        // Abmelden und erneutes Anmelden ausserhalb des Desktops, etwa in der Textshell.
        identity.Logout();
        identity.LoginAs(admin);
        desktop.Handle(new(InputEventType.Text, Character: 'x'));
        Assert.AreEqual(0, desktop.Windows.Count);
        desktop.Render(surface, 0, 10, 10, PointerButtons.None);
        Assert.IsNotNull(admin.UserId);
    }
}
