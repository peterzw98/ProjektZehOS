using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.TextEditor;

internal sealed class TextEditorApp : IApplication
{
    private readonly FileService files;
    private readonly UiPainter painter;
    private readonly UiPanel panel = new();
    private readonly UiPanel confirmation = new();
    private readonly UiTextBox path;
    private readonly UiTextBox document;
    private readonly UiButton load;
    private readonly UiButton save;
    private readonly UiButton clear;
    private readonly UiButton keep;
    private readonly UiButton discard;
    private readonly UiButton saveClose;
    private string savedText = "";
    private string newline = "\n";
    private string status = "F3: oeffnen | F2: speichern | Tab: Fokus";
    private bool closing;
    private bool allowClose;
    public bool IsCloseRequested => allowClose;
    public bool IsDirty => !string.Equals(document.Text, savedText, StringComparison.Ordinal);
    public bool CloseConfirmationVisible => closing;

    public TextEditorApp(FileService files, UiPainter painter, string initialPath = "")
    {
        this.files = files;
        this.painter = painter;
        path = new UiTextBox(1024, painter: painter);
        document = new UiTextBox(FileService.MaximumTextLength, true, painter: painter);
        path.SetText(initialPath);
        load = new("Oeffnen", Load);
        save = new("Speichern", Save);
        clear = new("Neu", New);
        keep = new("Zurueck", () => { closing = false; panel.Focus(document); });
        discard = new("Verwerfen", () => { allowClose = true; closing = false; });
        saveClose = new("Speichern", () => { if (SaveDocument()) { allowClose = true; closing = false; } });
        foreach (var item in new UiControl[] { path, load, save, clear, document }) panel.Add(item);
        foreach (var item in new UiControl[] { keep, discard, saveClose }) confirmation.Add(item);
        path.Submitted = Load;
        Layout(796, 526);
    }

    public void Start()
    {
        if (!string.IsNullOrEmpty(path.Text)) Load();
        else path.SetText("/data/neu.txt");
    }

    public void Handle(InputEvent value)
    {
        if (closing) { confirmation.Handle(value); return; }
        if (value.Type == InputEventType.KeyDown && !value.IsRepeat && value.KeyCode == (byte)PhysicalKey.F2) Save();
        else if (value.Type == InputEventType.KeyDown && !value.IsRepeat && value.KeyCode == (byte)PhysicalKey.F3) Load();
        else panel.Handle(value);
    }
    public void Update(long milliseconds) { }

    private void Load()
    {
        if (IsDirty) { status = "Ungespeichert: zuerst speichern oder Fenster schliessen."; return; }
        var result = files.ReadText(path.Text);
        if (!result.IsOK()) { status = result.Status.Message; return; }
        string text = result.Value!;
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        document.SetText(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        savedText = document.Text;
        status = "Datei geoeffnet. F2: speichern";
        panel.Focus(document);
    }

    private void New()
    {
        if (IsDirty) { status = "Ungespeichert: zuerst speichern oder Fenster schliessen."; return; }
        document.SetText("");
        savedText = "";
        newline = "\n";
        path.SetText("/data/neu.txt");
        status = "Neue Datei. Pfad vor dem Speichern pruefen.";
        panel.Focus(document);
    }

    private void Save() => SaveDocument();
    private bool SaveDocument()
    {
        string text = newline == "\n" ? document.Text : document.Text.Replace("\n", newline, StringComparison.Ordinal);
        var result = files.WriteText(path.Text, text);
        if (!result.IsOK()) { status = result.Message; return false; }
        savedText = document.Text;
        status = "Gespeichert. F3: erneut oeffnen";
        return true;
    }

    public bool RequestClose()
    {
        if (allowClose || !IsDirty) return true;
        panel.Handle(new(InputEventType.Reset));
        closing = true;
        confirmation.Focus(keep);
        return false;
    }

    public void Draw(PixelSurface surface, int x, int y, int width, int height, bool active)
    {
        Layout(width, height);
        surface.FillRectangle(x, y, width, height, UiPainter.Background);
        if (closing)
        {
            painter.Text(surface, "Ungespeicherte Aenderungen", x + 16, y + 24, width - 32, UiPainter.Accent);
            painter.Text(surface, "Speichern oder verwerfen?", x + 16, y + 24 + painter.GlyphHeight + 8, width - 32);
            confirmation.Draw(surface, painter, x, y, active);
            painter.Text(surface, status, x + 16, y + 200, width - 32);
            return;
        }
        panel.Draw(surface, painter, x, y, active);
        painter.Text(surface, IsDirty ? "* Ungespeichert" : "Gespeicherter Stand", x + 8, y + height - 2 * painter.GlyphHeight - 12, width - 16, UiPainter.Accent);
        painter.Text(surface, status, x + 8, y + height - painter.GlyphHeight - 6, width - 16);
    }

    private void Layout(int width, int height)
    {
        path.Bounds = new(8, 8, width - 16, 44);
        UiLayout.Row(8, 60, width - 16, load, save, clear);
        document.Bounds = new(8, 112, width - 16, Math.Max(48, height - 128 - 2 * painter.GlyphHeight));
        UiLayout.Row(16, 132, width - 32, keep, discard, saveClose);
    }

    public void Close()
    {
        panel.Handle(new(InputEventType.Reset));
        confirmation.Handle(new(InputEventType.Reset));
        document.SetText("");
        path.SetText("");
        savedText = "";
    }
}
