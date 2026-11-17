using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

/// <summary>Bedient einen echten Desktop nur ueber Eingabeereignisse, mit eigener Platte, fester Uhr und festem Takt.</summary>
// UiPainter haelt das Farbschema statisch; Szenarien duerfen deshalb nicht parallel laufen.
internal sealed class DesktopScenario : IDisposable
{
    public const string AdminPassword = "admin-password-123";
    public static readonly DateTime FixedUtc = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private readonly int width;
    private readonly int height;
    private readonly int[] pixels;
    private readonly PixelSurface surface;
    private long milliseconds;
    private int pointerX;
    private int pointerY;

    /// <param name="rootName">Ordnername der Testplatte unter dem Temp-Ordner; nur fuer Tests der Pfadmaskierung.</param>
    public DesktopScenario(int width = 1024, int height = 768, bool withLogin = false, string? rootName = null)
    {
        this.width = width;
        this.height = height;
        pixels = new int[width * height];
        surface = new PixelSurface(width, height, pixels);
        Root = Path.Combine(Path.GetTempPath(), rootName ?? $"zehos-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        pointerX = width / 2;
        pointerY = height / 2;
        var font = Golden.Font();
        DesktopSession? session = null;
        if (withLogin)
        {
            // Wie der Boot: eingebundenes /data mit einem eingerichteten Administrator, Anmeldung noch offen.
            var accounts = new AccountService(Root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes))) { IsDataMounted = () => true, SetupWindowOpen = true };
            Assert.AreEqual(ResultCode.NotFound, accounts.Load().Code);
            Assert.IsTrue(accounts.SetupWork(accounts.CreateSession(), "admin", AdminPassword)()().IsOK());
            Identity = accounts.CreateSession();
            var files = new FileService(Root, access: accounts.Access, session: Identity);
            ShellService? shell = null;
            shell = new ShellService(
                [.. new AccountShellCommands(accounts, Identity, files, prepare => shell!.BeginSecretInput(prepare), false,
                    name => shell!.HasCommand(name)).Commands],
                authorize: command => accounts.Authorize(Identity, command));
            LoginShell = shell;
            session = new DesktopSession(accounts, shell, Identity);
        }
        Desktop = new Desktop(width, height, font.Data, font.Width, font.Height,
            () => new ShellService(new FileShellCommands(new FileService(Root)).Commands), () => new FileService(Root),
            session: session, getUtcNow: () => FixedUtc);
        Frame();
    }

    public Desktop Desktop { get; }
    public string Root { get; }
    public UserSession? Identity { get; }
    public ShellService? LoginShell { get; }
    public int[] Pixels => pixels;

    public void Key(PhysicalKey key)
    {
        Desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)key));
        Desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)key));
        Frame();
    }

    public void Chord(PhysicalKey modifier, PhysicalKey key)
    {
        Desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)modifier));
        Key(key);
        Desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)modifier));
        Frame();
    }

    public void Text(string text)
    {
        foreach (char character in text) Desktop.Handle(new(InputEventType.Text, Character: character, IsTextKey: true));
        Frame();
    }

    /// <summary>Tippt eine Zeile und schliesst sie mit Enter ab.</summary>
    public void Line(string text)
    {
        Text(text);
        Key(PhysicalKey.Enter);
    }

    public void Click(int x, int y)
    {
        Move(x, y);
        Desktop.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        Desktop.Handle(new(InputEventType.PointerUp, X: x, Y: y, Button: PointerButtons.Left));
        Frame();
    }

    public void Drag(int x, int y, int toX, int toY)
    {
        Move(x, y);
        Desktop.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
        Frame();
        Move(toX, toY, PointerButtons.Left);
        Desktop.Handle(new(InputEventType.PointerUp, X: toX, Y: toY, Button: PointerButtons.Left));
        Frame();
    }

    private void Move(int x, int y, PointerButtons buttons = PointerButtons.None)
    {
        pointerX = x;
        pointerY = y;
        Desktop.Handle(new(InputEventType.PointerMove, X: x, Y: y, Button: buttons));
    }

    public void Frame(long step = 16)
    {
        milliseconds += step;
        Desktop.Update(milliseconds);
        Desktop.Render(surface, milliseconds, pointerX, pointerY, PointerButtons.None);
    }

    /// <summary>Wartet auf die Kennwortpruefung im Hintergrund; der Takt laeuft dabei weiter.</summary>
    public void WaitForPassword()
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(30), "Kennwortpruefung endet nicht.");
            Frame();
        }
        while (LoginShell!.IsBusy);
        Frame();
    }

    /// <summary>
    /// Vergleicht das aktuelle Bild; <paramref name="hidden"/> sind Bildschirmbereiche, die den Hostpfad der
    /// Testplatte zeigen und deshalb vor dem Vergleich einfarbig uebermalt werden.
    /// </summary>
    public void Check(string name, params UiRect[] hidden) => Golden.Check(name, width, height, Masked(hidden));

    /// <summary>Pruefsumme des aktuellen Bilds mit uebermalten Bereichen, ohne Referenzbild.</summary>
    public string Hash(params UiRect[] hidden) => Golden.Hash(width, height, Masked(hidden));

    private int[] Masked(UiRect[] hidden)
    {
        Frame();
        var copy = (int[])pixels.Clone();
        var target = new PixelSurface(width, height, copy);
        foreach (var area in hidden) target.FillRectangle(area.X, area.Y, area.Width, area.Height, unchecked((int)0xffff00ff));
        return copy;
    }

    /// <summary>Zeile unter der Titelleiste eines Fensters, in der Dateien und Texteditor den Pfad zeigen.</summary>
    public static UiRect PathRow(Window window) => new(window.X, window.ContentY, window.Width, 56);

    public void Dispose()
    {
        Desktop.Windows.CloseAll();
        Directory.Delete(Root, true);
        UiPainter.SetTheme(true, 0);
    }
}
