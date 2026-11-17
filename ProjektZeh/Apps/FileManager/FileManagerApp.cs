using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Virtual;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.FileManager;

/// <summary>Datei-Explorer im Stil von Dolphin/Windows 11: Verlauf, Orte, Detailspalten, Vorschau und einfache Dateiaktionen.</summary>
internal sealed class FileManagerApp : IApplication
{
    private const int MaximumHistory = 32;
    // Groessen werden je Eintrag einzeln abgefragt; grosse Ordner zeigen deshalb nur Namen.
    private const int MaximumSizedEntries = 256;
    private const string ReadOnlyMessage = "/proc, /sys und /dev sind nur lesbar.";
    private enum NameMode { None, NewFolder, Rename }
    private readonly FileService files;
    private readonly Func<string, bool> edit;
    private readonly UiPainter painter;
    private readonly UiPanel panel = new();
    private readonly UiTextBox path;
    private readonly UiList places = new() { Framed = false, RowPadding = 12 };
    private readonly UiList list = new() { DoubleClick = true, RowPadding = 8 };
    private readonly UiTextBox preview;
    private readonly UiTextBox name;
    private readonly UiButton back;
    private readonly UiButton forward;
    private readonly UiButton up;
    private readonly UiButton open;
    private readonly UiButton editor;
    private readonly UiButton newFolder;
    private readonly UiButton rename;
    private readonly UiButton delete;
    private readonly string[] placePaths;
    private readonly List<string> history = [];
    private int historyIndex = -1;
    private FileEntry[] entries = [];
    private long[] sizes = [];
    private string status = "";
    private string summary = "";
    private string? pendingDelete;
    private string? renameSource;
    private NameMode naming;
    private int listColumns;
    // Ort in /proc, /sys oder /dev: liegt ausserhalb von FileService.CurrentDirectory, das unveraendert der letzte normale Ort bleibt.
    private string? virtualLocation;
    private string header = "Name";
    private long now;
    private int width = 796;
    private int height = 526;

    public FileManagerApp(FileService files, UiPainter painter, Func<string, bool> edit)
    {
        this.files = files;
        this.painter = painter;
        this.edit = edit;
        path = new UiTextBox(1024, painter: painter);
        preview = new UiTextBox(FileService.MaximumTextLength, true, true, painter);
        name = new UiTextBox(255, painter: painter) { Enabled = false };
        back = new("<", () => Navigate(-1));
        forward = new(">", () => Navigate(1));
        up = new("^", () => LoadDirectory("..", true));
        open = new("Oeffnen", OpenSelected) { Primary = true };
        editor = new("Bearbeiten", EditSelected);
        newFolder = new("Ordner neu", () => BeginNaming(NameMode.NewFolder));
        rename = new("Umbenennen", () => BeginNaming(NameMode.Rename));
        delete = new("Loeschen", DeleteSelected);
        placePaths = [files.CurrentDirectory, "/data", "/proc", "/sys", "/dev"];
        places.SetItems(["Persoenlich", "Laufwerk", "System (/proc)", "System (/sys)", "Geraete (/dev)"]);
        places.Icons = [AppIcon.Files, AppIcon.Files, AppIcon.Files, AppIcon.Files, AppIcon.Files];
        places.Activated = () => { if (places.SelectedIndex >= 0) LoadDirectory(placePaths[places.SelectedIndex], true); };
        path.Submitted = () => LoadDirectory(path.Text, true);
        name.Submitted = CommitName;
        list.Activated = OpenSelected;
        list.GetMilliseconds = () => now;
        foreach (var item in new UiControl[] { path, back, forward, up, open, editor, newFolder, rename, delete, places, list, preview, name })
            panel.Add(item);
        Layout(width, height);
    }

    public void Start() => LoadDirectory(files.CurrentDirectory, true);

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.KeyDown && !value.IsTextKey)
        {
            switch ((PhysicalKey)value.KeyCode)
            {
                case PhysicalKey.F5: LoadDirectory(virtualLocation ?? files.CurrentDirectory, false); return;
                case PhysicalKey.F2 when naming == NameMode.None && panel.Focused == list: BeginNaming(NameMode.Rename); return;
                case PhysicalKey.Delete when naming == NameMode.None && panel.Focused == list: DeleteSelected(); return;
                case PhysicalKey.Backspace when panel.Focused == list: LoadDirectory("..", true); return;
                case PhysicalKey.Escape when naming != NameMode.None: EndNaming("Abgebrochen."); return;
            }
        }
        int previous = list.SelectedIndex;
        panel.Handle(value);
        if (list.SelectedIndex == previous) return;
        // Eine geaenderte Auswahl hebt Loeschrueckfrage und offene Namenseingabe auf; Esc erreicht die App im Desktop nicht.
        pendingDelete = null;
        if (naming != NameMode.None) EndNaming("Abgebrochen.");
        ShowPreview();
    }

    public void Update(long milliseconds) => now = milliseconds;
    public void Resize(int newWidth, int newHeight) => Layout(newWidth, newHeight);

    private void Navigate(int direction)
    {
        int target = historyIndex + direction;
        if (target < 0 || target >= history.Count) return;
        // Vor dem Laden setzen, damit Zurueck/Vor den neuen Stand anzeigen; ein Fehler stellt den alten wieder her.
        int previous = historyIndex;
        historyIndex = target;
        if (!LoadDirectory(history[target], false)) historyIndex = previous;
    }

    /// <summary>Angezeigter Ort (virtuell oder normal) und Statuszeile, fuer Tests.</summary>
    internal string Location => virtualLocation ?? files.CurrentDirectory;
    internal string Status => status;

    internal bool LoadDirectory(string destination, bool remember)
    {
        if (virtualLocation != null || files.IsVirtualPath(destination))
        {
            // Hostpfade wie C:\... (Windows-Tests) sind nie virtuell und verlassen den Baum direkt.
            bool rooted = Path.IsPathRooted(destination);
            if (!rooted || destination.StartsWith('/'))
            {
                string target = VirtualTree.Normalize(destination, virtualLocation ?? files.CurrentDirectory);
                if (VirtualTree.IsVirtualPath(target)) return LoadVirtual(target, remember);
            }
            // Relativ aus einem Baum heraus (etwa "Hoch" an /proc): zurueck zum letzten normalen Ort.
            if (!rooted) destination = files.CurrentDirectory;
        }

        var changed = files.ChangeDirectory(destination);
        if (!changed.IsOK()) { status = changed.Message; return false; }
        SetVirtual(null, remember);
        var listed = files.ListDirectory(".");
        entries = listed.IsOK() ? listed.Value! : [];
        sizes = new long[entries.Length];
        int folders = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            if (entries[index].IsDirectory) { folders++; sizes[index] = -1; continue; }
            sizes[index] = -1;
            if (entries.Length > MaximumSizedEntries) continue;
            var size = files.GetFileSize(entries[index].Path);
            if (size.IsOK()) sizes[index] = size.Value;
        }
        pendingDelete = null;
        listColumns = -1;
        RefreshLabels();
        list.Select(entries.Length == 0 ? -1 : 0);
        ShowPreview();
        summary = listed.IsOK() ? $"{folders} Ordner, {entries.Length - folders} Dateien" + FreeSpace() : listed.Status.Message;
        status = "";
        return true;
    }

    /// <summary>Virtueller Ort: Eintraege per ListVirtual, keine Groessen (Snapshots melden 0), alle Schreibaktionen aus.</summary>
    private bool LoadVirtual(string target, bool remember)
    {
        var listed = files.ListVirtual(target);
        if (!listed.IsOK()) { status = listed.Status.Message; return false; }
        SetVirtual(target, remember);
        entries = listed.Value!;
        sizes = new long[entries.Length];
        Array.Fill(sizes, -1);
        int folders = Array.FindAll(entries, entry => entry.IsDirectory).Length;
        pendingDelete = null;
        listColumns = -1;
        RefreshLabels();
        list.Select(entries.Length == 0 ? -1 : 0);
        ShowPreview();
        summary = $"{folders} Ordner, {entries.Length - folders} Dateien | nur lesbar";
        status = "";
        return true;
    }

    /// <summary>Gemeinsamer Ortswechsel: Namenseingabe beenden, Verlauf, Pfadfeld und Schreibaktionen.</summary>
    private void SetVirtual(string? location, bool remember)
    {
        // Ein Ordnerwechsel beendet jede offene Namenseingabe, sonst landete Umbenennen ungefragt im neuen Ordner.
        if (naming != NameMode.None) EndNaming("");
        virtualLocation = location;
        string shown = location ?? files.CurrentDirectory;
        if (remember && (historyIndex < 0 || !string.Equals(history[historyIndex], shown, StringComparison.Ordinal)))
        {
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(shown);
            if (history.Count > MaximumHistory) history.RemoveAt(0);
            historyIndex = history.Count - 1;
        }
        back.Enabled = historyIndex > 0;
        forward.Enabled = historyIndex < history.Count - 1;
        path.SetText(shown);
        editor.Enabled = newFolder.Enabled = rename.Enabled = delete.Enabled = location == null;
    }

    private string FreeSpace()
    {
        var space = files.GetDiskSpace(".");
        return space.IsOK() ? $" | frei: {FormatSize(space.Value.AvailableBytes)}" : "";
    }

    // Feste Kultur, damit Host und Kernel dieselbe Darstellung liefern.
    internal static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB")
    };

    // Detailspalten als feste Zeichenbreiten, damit die vorhandene Bitmapschrift sauber ausgerichtet bleibt.
    private void RefreshLabels()
    {
        int columns = Math.Max(12, (list.Bounds.Width - 52) / painter.GlyphWidth);
        if (columns == listColumns) return;
        listColumns = columns;
        header = Columns("Name", "Groesse", "Typ", columns);
        var labels = new string[entries.Length];
        for (int index = 0; index < entries.Length; index++) labels[index] = Row(entries[index], sizes[index], columns);
        int selected = list.SelectedIndex;
        list.SetItems(labels);
        var icons = new AppIcon[entries.Length];
        for (int index = 0; index < entries.Length; index++) icons[index] = entries[index].IsDirectory ? AppIcon.Files : AppIcon.Editor;
        list.Icons = icons;
        if (selected < entries.Length) list.Select(selected);
    }

    internal static string Row(FileEntry entry, long size, int columns)
    {
        string title = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry.Path));
        string kind = entry.IsDirectory ? "Ordner" : Path.GetExtension(title).TrimStart('.').ToUpperInvariant();
        if (kind.Length == 0) kind = "Datei";
        return Columns(title, FormatSize(size), kind, columns);
    }

    // Kopfzeile und Eintraege teilen dieselbe Aufteilung: Name links, Groesse rechtsbuendig, Typ hoechstens sechs Zeichen.
    private static string Columns(string title, string size, string kind, int columns)
    {
        if (kind.Length > 6) kind = kind[..6];
        string detail = $"{size,9} {kind,-6}";
        int nameColumns = columns - detail.Length - 1;
        if (nameColumns < 4) return title;
        if (title.Length > nameColumns) title = title[..(nameColumns - 1)] + "~";
        return title.PadRight(nameColumns) + " " + detail;
    }

    private FileEntry? Selected => list.SelectedIndex >= 0 && list.SelectedIndex < entries.Length ? entries[list.SelectedIndex] : null;

    private void ShowPreview()
    {
        preview.SetText("");
        // Ohne sichtbare Vorschau keine Plattenarbeit in der gemeinsamen Ereignisschleife.
        if (!preview.Enabled || Selected is not { IsDirectory: false } entry || sizes[list.SelectedIndex] > FileService.MaximumTextLength) return;
        var result = files.ReadTextOrVirtual(entry.Path);
        if (result.IsOK()) preview.SetText(result.Value!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    }

    private void OpenSelected()
    {
        if (Selected is not { } entry) { status = "Kein Eintrag ausgewaehlt."; return; }
        if (entry.IsDirectory) { LoadDirectory(entry.Path, true); return; }
        if (!edit(entry.Path)) status = "Kein weiteres Fenster moeglich.";
    }

    private void EditSelected()
    {
        if (virtualLocation != null) { status = ReadOnlyMessage; return; }
        if (Selected is not { IsDirectory: false } entry) { status = "Eine Textdatei auswaehlen."; return; }
        if (!edit(entry.Path)) status = "Kein weiteres Fenster moeglich.";
    }

    private void BeginNaming(NameMode mode)
    {
        if (virtualLocation != null) { status = ReadOnlyMessage; return; }
        // Ein zweiter Klick auf dieselbe Schaltflaeche bricht ab.
        if (naming == mode) { EndNaming("Abgebrochen."); return; }
        if (mode == NameMode.Rename && Selected is not { IsDirectory: false }) { status = "Eine Datei zum Umbenennen auswaehlen."; return; }
        renameSource = mode == NameMode.Rename ? Selected!.Value.Path : null;
        naming = mode;
        name.Enabled = true;
        name.SetText(renameSource != null ? Path.GetFileName(renameSource) : "Neuer Ordner");
        name.SelectAll();
        panel.Focus(name);
        status = mode == NameMode.Rename ? "Neuer Name, Enter uebernimmt, erneuter Klick bricht ab." : "Ordnername, Enter legt an, erneuter Klick bricht ab.";
        Layout(width, height);
    }

    private void EndNaming(string message)
    {
        naming = NameMode.None;
        renameSource = null;
        name.Enabled = false;
        panel.Focus(list);
        Layout(width, height);
        status = message;
    }

    private void CommitName()
    {
        string value = name.Text.Trim();
        if (value.Length == 0 || value.IndexOfAny(['/', '\\']) >= 0) { status = "Ungueltiger Name."; return; }
        var result = naming == NameMode.NewFolder ? files.CreateDirectory(value)
            : files.MoveFile(renameSource, Path.Combine(files.CurrentDirectory, value));
        if (!result.IsOK()) { status = result.Message; return; }
        string done = naming == NameMode.NewFolder ? "Ordner angelegt: " + value : "Umbenannt in " + value;
        EndNaming("");
        LoadDirectory(files.CurrentDirectory, false);
        status = done;
    }

    // Zweistufig statt Dialogfenster: der erste Klick fragt nach, erst der zweite loescht denselben Eintrag.
    private void DeleteSelected()
    {
        if (virtualLocation != null) { status = ReadOnlyMessage; return; }
        if (Selected is not { } entry) { status = "Kein Eintrag ausgewaehlt."; return; }
        if (!string.Equals(pendingDelete, entry.Path, StringComparison.Ordinal))
        {
            pendingDelete = entry.Path;
            status = $"\"{Path.GetFileName(entry.Path)}\" wirklich loeschen? Erneut Loeschen waehlen.";
            return;
        }
        pendingDelete = null;
        var result = entry.IsDirectory ? files.DeleteDirectory(entry.Path) : files.DeleteFile(entry.Path);
        if (!result.IsOK()) { status = result.Message; return; }
        LoadDirectory(files.CurrentDirectory, false);
        status = "Geloescht: " + Path.GetFileName(entry.Path);
    }

    public void Draw(PixelSurface surface, int x, int y, int drawWidth, int drawHeight, bool active)
    {
        Layout(drawWidth, drawHeight);
        RefreshLabels();
        surface.FillRectangle(x, y, drawWidth, drawHeight, UiPainter.Background);
        int body = UiLayout.ToolbarHeight * 2 + 24;
        // Seitenleiste wie in Dolphin: eigene Flaeche mit Ueberschrift.
        surface.FillRectangle(x, y + body, places.Bounds.Width + 12, drawHeight - body, UiPainter.Panel);
        painter.Text(surface, "Orte", x + 12, y + body + 4, places.Bounds.Width, UiPainter.Secondary);
        painter.Text(surface, header, x + list.Bounds.X + 44, y + body + 4, list.Bounds.Width - 52, UiPainter.Secondary);
        panel.Draw(surface, painter, x, y, active);
        int statusTop = y + drawHeight - painter.GlyphHeight - 8;
        surface.FillRectangle(x, statusTop - 6, drawWidth, painter.GlyphHeight + 14, UiPainter.Panel);
        string line = status.Length > 0 ? status : summary;
        painter.Text(surface, line, x + 12, statusTop, drawWidth - 24, pendingDelete != null ? UiPainter.Danger : UiPainter.Secondary);
    }

    private void Layout(int newWidth, int newHeight)
    {
        width = newWidth;
        height = newHeight;
        int bar = UiLayout.ToolbarHeight;
        back.Bounds = new(8, 8, bar, bar);
        forward.Bounds = new(8 + bar + 6, 8, bar, bar);
        up.Bounds = new(8 + 2 * (bar + 6), 8, bar, bar);
        int pathLeft = 8 + 3 * (bar + 6) + 4;
        path.Bounds = new(pathLeft, 8, Math.Max(1, width - pathLeft - 8), bar);
        UiLayout.Row(8, 16 + bar, width - 16, open, editor, newFolder, rename, delete);
        int body = 2 * bar + 24;
        int header = painter.GlyphHeight + 8;
        int status = painter.GlyphHeight + 16;
        int naming = this.naming == NameMode.None ? 0 : bar + 8;
        int bodyHeight = Math.Max(48, height - body - header - status - naming - 8);
        int side = Math.Clamp(14 * painter.GlyphWidth + 44, 120, Math.Max(120, width / 4));
        places.Bounds = new(4, body + header, side, bodyHeight);
        int content = side + 20;
        // Die Vorschau erst bei breiten Fenstern, damit Dateinamen in der Gastschrift lesbar bleiben.
        int previewWidth = width >= 1400 ? (width - content) * 2 / 5 : 0;
        list.Bounds = new(content, body + header, Math.Max(48, width - content - previewWidth - (previewWidth > 0 ? 16 : 8)), bodyHeight);
        preview.Bounds = previewWidth > 0 ? new(width - previewWidth - 8, body + header, previewWidth, bodyHeight) : default;
        bool previewShown = preview.Enabled;
        preview.Enabled = previewWidth > 0;
        if (preview.Enabled && !previewShown) ShowPreview();
        name.Bounds = this.naming != NameMode.None ? new(content, body + header + bodyHeight + 8, width - content - 8, bar) : default;
    }

    public void Close()
    {
        panel.Handle(new(InputEventType.Reset));
        entries = [];
        sizes = [];
        list.SetItems([]);
        preview.SetText("");
        path.SetText("");
        history.Clear();
    }
}
