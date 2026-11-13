using System;
using System.IO;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.FileManager;

internal sealed class FileManagerApp : IApplication
{
    private readonly FileService files;
    private readonly Func<string, bool> edit;
    private readonly UiPainter painter;
    private readonly UiPanel panel = new();
    private readonly UiTextBox path;
    private readonly UiList list = new();
    private readonly UiTextBox preview;
    private readonly UiButton open;
    private readonly UiButton parent;
    private readonly UiButton editor;
    private FileEntry[] entries = [];
    private string status = "";

    public FileManagerApp(FileService files, UiPainter painter, Func<string, bool> edit)
    {
        this.files = files;
        this.painter = painter;
        path = new UiTextBox(1024, painter: painter);
        preview = new UiTextBox(FileService.MaximumTextLength, true, true, painter);
        this.edit = edit;
        open = new("Oeffnen", OpenSelected);
        parent = new("Eltern", () => LoadDirectory(".."));
        editor = new("Bearbeiten", EditSelected);
        path.Submitted = () => LoadDirectory(path.Text);
        list.Activated = OpenSelected;
        foreach (var item in new UiControl[] { path, open, parent, editor, list, preview }) panel.Add(item);
        Layout(796, 526);
    }

    public void Start() => LoadDirectory(files.CurrentDirectory);
    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.F5) LoadDirectory(files.CurrentDirectory);
        else panel.Handle(value);
    }
    public void Update(long milliseconds) { }

    private void LoadDirectory(string destination)
    {
        var changed = files.ChangeDirectory(destination);
        if (!changed.IsOK()) { status = changed.Message; return; }
        path.SetText(files.CurrentDirectory);
        var listed = files.ListDirectory(".");
        entries = listed.IsOK() ? listed.Value! : [];
        var labels = new string[entries.Length];
        for (int index = 0; index < entries.Length; index++)
            labels[index] = (entries[index].IsDirectory ? "/ " : "  ") + Path.GetFileName(entries[index].Path);
        list.SetItems(labels);
        preview.SetText("");
        status = listed.IsOK() ? $"{entries.Length} Eintraege | Enter: oeffnen | F5: neu laden" : listed.Status.Message;
    }

    private void OpenSelected()
    {
        if (list.SelectedIndex < 0) { status = "Kein Eintrag ausgewaehlt."; return; }
        FileEntry entry = entries[list.SelectedIndex];
        if (entry.IsDirectory) { LoadDirectory(entry.Path); return; }
        var result = files.ReadText(entry.Path);
        if (!result.IsOK()) { status = result.Status.Message; return; }
        preview.SetText(result.Value!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        status = Path.GetFileName(entry.Path) + " | Vorschau: PageUp/PageDown";
    }

    private void EditSelected()
    {
        if (list.SelectedIndex < 0 || entries[list.SelectedIndex].IsDirectory)
        { status = "Eine Textdatei auswaehlen."; return; }
        if (!edit(entries[list.SelectedIndex].Path)) status = "Kein weiteres Fenster moeglich.";
    }

    public void Draw(PixelSurface surface, int x, int y, int width, int height, bool active)
    {
        Layout(width, height);
        surface.FillRectangle(x, y, width, height, UiPainter.Background);
        panel.Draw(surface, painter, x, y, active);
        painter.Text(surface, status, x + 8, y + height - painter.GlyphHeight - 8, width - 16);
    }

    private void Layout(int width, int height)
    {
        path.Bounds = new(8, 8, width - 16, 44);
        UiLayout.Row(8, 60, width - 16, open, parent, editor);
        int body = Math.Max(48, height - 120 - painter.GlyphHeight);
        int left = (width - 24) / 2;
        list.Bounds = new(8, 112, left, body);
        preview.Bounds = new(16 + left, 112, width - left - 24, body);
    }

    public void Close()
    {
        panel.Handle(new(InputEventType.Reset));
        entries = [];
        list.SetItems([]);
        preview.SetText("");
        path.SetText("");
    }
}
