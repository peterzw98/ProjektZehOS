using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Apps.Settings;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Apps.TextEditor;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

/// <summary>
/// Bedienablaeufe Ende zu Ende: Eingaben nur ueber Desktop.Handle, Pruefung des fachlichen Ergebnisses und eines Referenzbilds.
/// </summary>
[TestClass]
public sealed class DesktopE2ETests
{
    private static string[] Lines(TerminalApp terminal) =>
        [.. Enumerable.Range(0, terminal.Buffer.Count).Select(terminal.Buffer.GetLine)];

    private static TerminalWorkspaceApp ActiveTerminal(DesktopScenario scenario) =>
        scenario.Desktop.Windows.ActiveWindow?.Application as TerminalWorkspaceApp ??
        throw new AssertFailedException("Aktives Fenster ist kein Terminal.");

    // Die Statuszeile der Einstellungen nennt den Hostpfad der Konfigurationsdatei; 320 ist die Seitenleiste der App.
    private const int Sidebar = 320;

    private static UiRect SettingsStatus(DesktopScenario scenario)
    {
        var window = scenario.Desktop.Windows.ActiveWindow!;
        return new UiRect(window.X + Sidebar, window.Y + window.Height - 80, window.Width - Sidebar, 80);
    }

    private static void Launch(DesktopScenario scenario, string search)
    {
        scenario.Chord(PhysicalKey.Alt, PhysicalKey.F1);
        scenario.Text(search);
        scenario.Key(PhysicalKey.Enter);
    }

    [TestMethod]
    public void Login_WrongThenRightPassword_ShowsDesktop()
    {
        using var scenario = new DesktopScenario(withLogin: true);
        scenario.Line("admin");
        scenario.Line("falsch-falsch-123");
        scenario.WaitForPassword();
        Assert.IsNull(scenario.Identity!.UserId);
        scenario.Chord(PhysicalKey.Alt, PhysicalKey.F1);
        Assert.IsFalse(scenario.Desktop.CloseMenu(), "Ohne Anmeldung oeffnet sich das Startmenue nicht.");
        scenario.Check("login-fehler");

        scenario.Line("admin");
        scenario.Line(DesktopScenario.AdminPassword);
        scenario.WaitForPassword();
        Assert.AreEqual("admin", scenario.Identity.UserName);
        scenario.Check("desktop-leer");
        scenario.Chord(PhysicalKey.Alt, PhysicalKey.F1);
        Assert.IsTrue(scenario.Desktop.CloseMenu(), "Nach der Anmeldung oeffnet Alt+F1 das Startmenue.");
    }

    [TestMethod]
    public void StartMenu_SearchAndEnter_OpensTerminalAndRunsEcho()
    {
        using var scenario = new DesktopScenario();
        Launch(scenario, "term");
        var terminal = ActiveTerminal(scenario);
        scenario.Line("echo hallo");
        CollectionAssert.Contains(Lines(terminal.ActiveTerminal), "hallo");
        scenario.Check("terminal-echo");
    }

    [TestMethod]
    public void Terminal_WriteFile_AppearsInFileManagerAndOpensInEditor()
    {
        using var scenario = new DesktopScenario();
        Launch(scenario, "term");
        scenario.Line("write notiz.txt eins");
        Launch(scenario, "dateien");
        var window = scenario.Desktop.Windows.ActiveWindow!;
        Assert.IsInstanceOfType<FileManagerApp>(window.Application, "Startmenue oeffnet die Dateien-App.");
        // Die Dateiliste zeigt den Hostpfad der Testplatte und bekommt deshalb kein Referenzbild; die erste Zeile ist die Datei.
        scenario.Click(window.X + 400, window.Y + 216);
        scenario.Key(PhysicalKey.Enter);
        var editor = scenario.Desktop.Windows.ActiveWindow!.Application as TextEditorApp;
        Assert.IsNotNull(editor, "Enter auf der Datei oeffnet den Texteditor.");
        scenario.Text("zwei");
        Assert.IsTrue(editor.IsDirty);
        scenario.Key(PhysicalKey.F2);
        Assert.IsFalse(editor.IsDirty, "F2 speichert.");
        string saved = File.ReadAllText(Path.Combine(scenario.Root, "notiz.txt"));
        StringAssert.Contains(saved, "eins");
        StringAssert.Contains(saved, "zwei");
        scenario.Check("editor-notiz", DesktopScenario.PathRow(window), DesktopScenario.PathRow(scenario.Desktop.Windows.ActiveWindow!));
    }

    [TestMethod]
    public void Settings_SwitchTheme_PersistsAndRepaints()
    {
        using var scenario = new DesktopScenario();
        Launch(scenario, "einst");
        Assert.IsInstanceOfType<SettingsApp>(scenario.Desktop.Windows.ActiveWindow!.Application);
        scenario.Key(PhysicalKey.Down);
        scenario.Check("einstellungen-dunkel", SettingsStatus(scenario));
        scenario.Key(PhysicalKey.Tab);
        scenario.Key(PhysicalKey.Enter);
        StringAssert.Contains(File.ReadAllText(Path.Combine(scenario.Root, "zehos-desktop.conf")), "dark=False");
        scenario.Check("einstellungen-hell", SettingsStatus(scenario));
    }
    private static int Count<T>(DesktopScenario scenario) =>
        Enumerable.Range(0, scenario.Desktop.Windows.Count).Count(index => scenario.Desktop.Windows.GetWindow(index).Application is T);

    [TestMethod]
    public void Window_DragCloseAndLimit()
    {
        using var scenario = new DesktopScenario();
        Launch(scenario, "term");
        var window = scenario.Desktop.Windows.ActiveWindow!;
        int x = window.X, y = window.Y;
        scenario.Drag(x + 100, y + 20, x + 160, y + 70);
        Assert.AreEqual((x + 60, y + 50), (window.X, window.Y), "Ziehen an der Titelleiste verschiebt das Fenster.");
        scenario.Click(window.X + window.Width - 20, window.Y + 20);
        Assert.AreEqual(0, scenario.Desktop.Windows.Count, "Das Schliessfeld schliesst das Fenster.");
        for (int index = 0; index < Desktop.MaximumWindows + 1; index++) Launch(scenario, "term");
        Assert.AreEqual(Desktop.MaximumWindows, scenario.Desktop.Windows.Count);
    }

    [TestMethod]
    public void Workspaces_SwitchAndMove_KeepWindowsSeparate()
    {
        using var scenario = new DesktopScenario();
        Launch(scenario, "term");
        Launch(scenario, "einst");
        // Strg+Alt+Umschalt+Rechts verschiebt das aktive Fenster auf die naechste Arbeitsflaeche.
        scenario.Desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        scenario.Desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Alt));
        scenario.Desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        scenario.Key(PhysicalKey.Right);
        scenario.Desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.LeftShift));
        scenario.Desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Alt));
        scenario.Desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Control));
        Assert.AreEqual((1, 1), (scenario.Desktop.Windows.CountOn(0), scenario.Desktop.Windows.CountOn(1)));
        scenario.Click(scenario.Desktop.PagerBounds(1).X + 4, scenario.Desktop.PagerBounds(1).Y + 18);
        Assert.AreEqual(1, scenario.Desktop.Windows.CurrentWorkspace);
        Assert.IsInstanceOfType<SettingsApp>(scenario.Desktop.Windows.ActiveWindow!.Application);
        scenario.Check("arbeitsflaeche-2", SettingsStatus(scenario));
    }

    [TestMethod]
    public void TaskManager_EndSelectedWindow_ClosesIt()
    {
        using var scenario = new DesktopScenario();
        Launch(scenario, "term");
        Launch(scenario, "task");
        Assert.IsInstanceOfType<TaskManagerApp>(scenario.Desktop.Windows.ActiveWindow!.Application);
        // Seitenleiste "Anwendungen", dann Liste mit dem Terminal in der ersten Zeile und die Schaltflaeche "Schliessen...".
        scenario.Key(PhysicalKey.Down);
        scenario.Key(PhysicalKey.Tab);
        scenario.Key(PhysicalKey.Home);
        scenario.Key(PhysicalKey.Tab);
        scenario.Key(PhysicalKey.Tab);
        scenario.Key(PhysicalKey.Enter);
        scenario.Frame();
        Assert.AreEqual(0, Count<TerminalWorkspaceApp>(scenario), "Das gewaehlte Terminal ist beendet.");
        Assert.AreEqual(1, Count<TaskManagerApp>(scenario), "Der Taskmanager bleibt offen.");
    }

    [TestMethod]
    public void EscapeAndSuspend_KeepTerminalState()
    {
        using var scenario = new DesktopScenario();
        Directory.CreateDirectory(Path.Combine(scenario.Root, "unter"));
        Launch(scenario, "term");
        var terminal = ActiveTerminal(scenario).ActiveTerminal;
        scenario.Line("cd unter");
        scenario.Text("halb");
        string[] before = Lines(terminal);
        // Wie F4/Esc: der Desktop wird angehalten und spaeter wieder gezeichnet.
        scenario.Desktop.Suspend();
        scenario.Frame();
        CollectionAssert.AreEqual(before, Lines(terminal));
        Assert.AreEqual("halb", terminal.Editor.Text);
        Assert.AreSame(terminal, ActiveTerminal(scenario).ActiveTerminal);
        scenario.Line("");
        scenario.Line("pwd");
        Assert.IsTrue(Lines(terminal).Any(line => line.EndsWith("unter", StringComparison.Ordinal)), "Arbeitsverzeichnis bleibt erhalten.");
    }

    [TestMethod]
    [DataRow(800, 600)]
    [DataRow(1024, 768)]
    [DataRow(1920, 1080)]
    public void Layout_AtSupportedResolutions(int width, int height)
    {
        using var scenario = new DesktopScenario(width, height);
        scenario.Chord(PhysicalKey.Alt, PhysicalKey.F1);
        scenario.Check($"menue-{width}x{height}");
    }
    // Strg+Umschalt+Taste wie auf der Tastatur: Modifikatoren halten, Taste druecken, in umgekehrter Reihenfolge loesen.
    private static void ControlShift(DesktopScenario scenario, byte key)
    {
        scenario.Desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.Control));
        scenario.Desktop.Handle(new(InputEventType.KeyDown, KeyCode: (byte)PhysicalKey.LeftShift));
        scenario.Desktop.Handle(new(InputEventType.KeyDown, KeyCode: key));
        scenario.Desktop.Handle(new(InputEventType.KeyUp, KeyCode: key));
        scenario.Desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.LeftShift));
        scenario.Desktop.Handle(new(InputEventType.KeyUp, KeyCode: (byte)PhysicalKey.Control));
        scenario.Frame();
    }

    [TestMethod]
    public void Terminal_TabsSplitSearchMenuAndExit()
    {
        using var scenario = new DesktopScenario();
        Launch(scenario, "term");
        var workspace = ActiveTerminal(scenario);
        scenario.Line("echo erster");
        ControlShift(scenario, 20);
        Assert.AreEqual((2, 1), (workspace.TabCount, workspace.ActiveTabIndex), "Strg+Umschalt+T oeffnet einen zweiten Tab.");
        ControlShift(scenario, 18);
        Assert.AreEqual(2, workspace.ViewCount, "Strg+Umschalt+E teilt nebeneinander.");
        var right = workspace.ActiveTerminal;
        ControlShift(scenario, 49);
        Assert.AreNotSame(right, workspace.ActiveTerminal, "Strg+Umschalt+N wechselt die Ansicht.");
        scenario.Line("echo links");
        ControlShift(scenario, 33);
        scenario.Line("links");
        scenario.Check("terminal-geteilt-suche");

        // Menue ueber Strg+Umschalt+M, mit Pfeiltasten auf "Uebereinander" und Enter.
        ControlShift(scenario, 50);
        scenario.Key(PhysicalKey.Down);
        scenario.Key(PhysicalKey.Down);
        scenario.Key(PhysicalKey.Enter);
        Assert.AreEqual(3, workspace.ViewCount, "Das Menue teilt die aktive Ansicht uebereinander.");
        ControlShift(scenario, 17);
        Assert.AreEqual(2, workspace.ViewCount, "Strg+Umschalt+W schliesst die aktive Ansicht.");

        // exit beendet nur die eigene Ansicht; der letzte Tab mit einer Ansicht schliesst danach das Fenster nicht.
        scenario.Line("exit");
        Assert.AreEqual(1, workspace.ViewCount);
        scenario.Line("exit");
        Assert.AreEqual(1, workspace.TabCount, "exit in der letzten Ansicht schliesst den Tab.");
        Assert.AreEqual(1, Count<TerminalWorkspaceApp>(scenario));
        CollectionAssert.Contains(Lines(workspace.ActiveTerminal), "erster", "Der erste Tab behaelt seine Ausgabe.");
    }
}
