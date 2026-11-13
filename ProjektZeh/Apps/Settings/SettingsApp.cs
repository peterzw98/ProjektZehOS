using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.Settings;

internal sealed class SettingsApp : IApplication
{
    public const int DisplayPage = 0;
    public const int PersonalizationPage = 1;
    private const int SidebarWidth = 260;
    private const int PreviewWidth = 192;
    private const int PreviewHeight = 108;
    private readonly UiPainter painter;
    private readonly DesktopPreferences preferences;
    private readonly DisplaySettings? display;
    private readonly Func<long> clock;
    private readonly UiPanel panel = new();
    private readonly UiList pages = new() { RowPadding = 12 };
    private readonly UiList modes = new();
    private readonly UiButton apply;
    private readonly UiButton keep;
    private readonly UiButton revert;
    private readonly UiButton light;
    private readonly UiButton dark;
    private readonly UiButton[] accents = new UiButton[UiPainter.AccentNames.Length];
    private readonly UiButton[] wallpapers = new UiButton[WallpaperImage.BuiltInNames.Length];
    private readonly UiTextBox imagePath;
    private readonly UiButton loadImage;
    private readonly int[][] previews = new int[WallpaperImage.BuiltInNames.Length][];
    private int previewRevision = -1;
    private int previewWidth;
    private int previewHeight;
    private int width = 1198;
    private int height = 780;
    private string displayError = "";

    public SettingsApp(UiPainter painter, DesktopPreferences preferences, DisplaySettings? display, Func<long> clock)
    {
        this.painter = painter;
        this.preferences = preferences;
        this.display = display;
        this.clock = clock;
        pages.SetItems(["Anzeige", "Personalisierung"]);
        apply = new("Uebernehmen", ApplyMode) { Primary = true };
        keep = new("Beibehalten", KeepMode) { Primary = true };
        revert = new("Zuruecknehmen", () => display?.Revert());
        light = new("Hell", () => Change(preferences.Settings with { Dark = false }));
        dark = new("Dunkel", () => Change(preferences.Settings with { Dark = true }));
        for (int index = 0; index < accents.Length; index++)
        {
            int accent = index;
            accents[index] = new(UiPainter.AccentNames[index], () => Change(preferences.Settings with { Accent = accent }));
        }
        for (int index = 0; index < wallpapers.Length; index++)
        {
            int wallpaper = index;
            wallpapers[index] = new(WallpaperImage.BuiltInNames[index], () => Change(preferences.Settings with { Wallpaper = wallpaper }));
        }
        imagePath = new UiTextBox(240, painter: painter);
        imagePath.SetText(preferences.Settings.Image.Length > 0 ? preferences.Settings.Image : "/data/hintergrund.bmp");
        loadImage = new("Laden", () => preferences.UseImage(imagePath.Text));
        imagePath.Submitted = () => preferences.UseImage(imagePath.Text);
        if (display != null)
        {
            var labels = new string[display.Modes.Length];
            for (int index = 0; index < labels.Length; index++) labels[index] = display.Modes[index].ToString();
            modes.SetItems(labels);
        }
        panel.Add(pages);
        foreach (var control in new UiControl[] { modes, apply, keep, revert, light, dark }) panel.Add(control);
        foreach (var control in accents) panel.Add(control);
        foreach (var control in wallpapers) panel.Add(control);
        panel.Add(imagePath);
        panel.Add(loadImage);
        Layout(width, height);
        UpdateState();
    }

    public int Page => pages.SelectedIndex;

    public void Start() { }
    public void Update(long milliseconds) { }
    public void Resize(int newWidth, int newHeight) => Layout(newWidth, newHeight);

    public void Handle(InputEvent value)
    {
        UpdateState();
        panel.Handle(value);
        UpdateState();
    }

    private void Change(DesktopSettings settings) => preferences.Apply(settings);

    private void ApplyMode()
    {
        if (display == null || modes.SelectedIndex < 0) return;
        var result = display.Apply(display.Modes[modes.SelectedIndex], clock());
        displayError = result.IsOK() ? "" : result.Message;
    }

    private void KeepMode()
    {
        if (display is not { Pending: true }) return;
        display.Confirm();
        Change(preferences.Settings with { Width = display.Current.Width, Height = display.Current.Height });
    }

    // Steuerelemente der verborgenen Seite sind deaktiviert und erhalten weder Fokus noch Klicks.
    private void UpdateState()
    {
        bool displayPage = Page == DisplayPage, personal = Page == PersonalizationPage;
        bool pending = display?.Pending == true;
        modes.Enabled = displayPage && display?.CanSwitch == true && !pending;
        apply.Enabled = modes.Enabled;
        keep.Enabled = revert.Enabled = displayPage && pending;
        var settings = preferences.Settings;
        light.Enabled = dark.Enabled = imagePath.Enabled = loadImage.Enabled = personal;
        light.Selected = !settings.Dark;
        dark.Selected = settings.Dark;
        for (int index = 0; index < accents.Length; index++)
        {
            accents[index].Enabled = personal;
            accents[index].Selected = settings.Accent == index;
            accents[index].Swatch = UiPainter.AccentPreview(index);
        }
        for (int index = 0; index < wallpapers.Length; index++)
        {
            wallpapers[index].Enabled = personal;
            wallpapers[index].Selected = settings.Wallpaper == index;
        }
    }

    public void Draw(PixelSurface surface, int x, int y, int newWidth, int newHeight, bool active)
    {
        Layout(newWidth, newHeight);
        UpdateState();
        surface.FillRectangle(x, y, SidebarWidth, height, UiPainter.Panel);
        surface.FillRectangle(x + SidebarWidth, y, 1, height, UiPainter.Border);
        surface.FillRectangle(x + SidebarWidth + 1, y, width - SidebarWidth - 1, height, UiPainter.Background);
        pages.Draw(surface, painter, x, y, active);
        int left = x + SidebarWidth + 24, inner = width - SidebarWidth - 48;
        painter.Text(surface, Page == DisplayPage ? "Anzeige" : "Personalisierung", left, y + 20, inner, UiPainter.Accent);
        if (Page == DisplayPage) DrawDisplay(surface, x, y, left, inner, active);
        else DrawPersonalization(surface, x, y, left, inner, active);
        bool error = preferences.StatusIsError;
        painter.Paragraph(surface, preferences.Status, left, y + height - 2 * (painter.GlyphHeight + 4) - 12, inner,
            error ? UiPainter.Danger : UiPainter.Success);
    }

    private void DrawDisplay(PixelSurface surface, int x, int y, int left, int inner, bool active)
    {
        int line = painter.GlyphHeight + 8;
        if (display == null)
        {
            painter.Paragraph(surface, "Anzeigeinformationen sind in dieser Umgebung nicht verfuegbar.", left, y + modes.Bounds.Y, inner, UiPainter.Secondary);
            return;
        }
        painter.Text(surface, "Aufloesung", left, y + modes.Bounds.Y - line, inner, UiPainter.Secondary);
        if (display.CanSwitch)
        {
            modes.Draw(surface, painter, x, y, active);
            apply.Draw(surface, painter, x, y, active);
        }
        else
        {
            painter.Text(surface, display.Current.ToString(), left, y + modes.Bounds.Y, inner);
            painter.Paragraph(surface, "Der Anzeigetreiber meldet nur diese Aufloesung und kann sie nicht zur Laufzeit wechseln. " +
                "Eine andere Aufloesung beim Start im Bootmenue waehlen (Full HD oder 1024 x 768).",
                left, y + modes.Bounds.Y + line, inner, UiPainter.Secondary);
        }
        int top = y + apply.Bounds.Y + apply.Bounds.Height + 20;
        painter.Text(surface, display.Pending ? "Aktiv: " + display.Current + " - Bestaetigung ausstehend" : "Aktiv: " + display.Current,
            left, top, inner, display.Pending ? UiPainter.Warning : UiPainter.Foreground);
        if (display.Pending)
        {
            keep.Draw(surface, painter, x, y, active);
            revert.Draw(surface, painter, x, y, active);
            painter.Text(surface, $"Ohne Bestaetigung Rueckkehr in {display.SecondsLeft(clock())} s", left,
                y + keep.Bounds.Y + keep.Bounds.Height + 12, inner, UiPainter.Secondary);
        }
        else if (displayError.Length > 0 || display.Status.Length > 0)
            painter.Paragraph(surface, displayError.Length > 0 ? displayError : display.Status, left, top + line, inner,
                displayError.Length > 0 ? UiPainter.Danger : UiPainter.Secondary);
    }

    private void DrawPersonalization(PixelSurface surface, int x, int y, int left, int inner, bool active)
    {
        int line = painter.GlyphHeight + 8;
        painter.Text(surface, "Farbschema", left, y + light.Bounds.Y - line, inner, UiPainter.Secondary);
        light.Draw(surface, painter, x, y, active);
        dark.Draw(surface, painter, x, y, active);
        painter.Text(surface, "Akzentfarbe: " + UiPainter.AccentNames[preferences.Settings.Accent], left, y + accents[0].Bounds.Y - line, inner, UiPainter.Secondary);
        foreach (var button in accents) button.Draw(surface, painter, x, y, active);
        painter.Text(surface, "Hintergrund", left, y + wallpapers[0].Bounds.Y - previewHeight - 8 - line, inner, UiPainter.Secondary);
        PreparePreviews();
        for (int index = 0; index < wallpapers.Length; index++)
        {
            var bounds = wallpapers[index].Bounds;
            surface.DrawPixels(previews[index], x + bounds.X, y + bounds.Y - previewHeight - 8, previewWidth, previewHeight);
            wallpapers[index].Draw(surface, painter, x, y, active);
        }
        painter.Text(surface, "Eigenes Bild (BMP, 24/32 Bit, max. 4 Megapixel, 16 MiB)", left, y + imagePath.Bounds.Y - line, inner, UiPainter.Secondary);
        imagePath.Draw(surface, painter, x, y, active);
        loadImage.Draw(surface, painter, x, y, active);
    }

    // Vorschauen nur bei Theme-/Groessenwechsel neu berechnen, nicht pro Bild.
    private void PreparePreviews()
    {
        int targetWidth = Math.Min(PreviewWidth, wallpapers[0].Bounds.Width);
        int targetHeight = targetWidth * PreviewHeight / PreviewWidth;
        if (previewRevision == preferences.Revision && targetWidth == previewWidth && previews[0] != null) return;
        previewWidth = targetWidth;
        previewHeight = targetHeight;
        for (int index = 0; index < previews.Length; index++)
        {
            if (previews[index]?.Length != previewWidth * previewHeight) previews[index] = new int[previewWidth * previewHeight];
            WallpaperImage.Create(previews[index], previewWidth, previewHeight, index, preferences.Settings.Dark, UiPainter.Accent);
        }
        previewRevision = preferences.Revision;
    }

    private void Layout(int newWidth, int newHeight)
    {
        width = newWidth;
        height = newHeight;
        int line = painter.GlyphHeight + 8;
        int control = painter.GlyphHeight + 16;
        int left = SidebarWidth + 24, inner = Math.Max(1, width - SidebarWidth - 48);
        pages.Bounds = new(12, 12, SidebarWidth - 24, Math.Max(48, height - 24));
        int top = 20 + painter.GlyphHeight + 24 + line;
        modes.Bounds = new(left, top, Math.Min(inner, 420), 4 * (painter.GlyphHeight + 4) + 8);
        apply.Bounds = new(left, modes.Bounds.Y + modes.Bounds.Height + 12, Math.Min(inner, 260), control);
        int pendingTop = apply.Bounds.Y + apply.Bounds.Height + 20 + line;
        int half = Math.Min(260, (inner - 12) / 2);
        keep.Bounds = new(left, pendingTop, half, control);
        revert.Bounds = new(left + half + 12, pendingTop, half, control);

        light.Bounds = new(left, top, Math.Min(200, (inner - 12) / 2), control);
        dark.Bounds = new(left + light.Bounds.Width + 12, top, light.Bounds.Width, control);
        int swatchTop = top + control + 16 + line;
        int swatch = Math.Min(control, (inner - 5 * 12) / accents.Length);
        for (int index = 0; index < accents.Length; index++) accents[index].Bounds = new(left + index * (swatch + 12), swatchTop, swatch, swatch);
        int cell = Math.Min(PreviewWidth, (inner - 3 * 16) / wallpapers.Length);
        int buttonTop = swatchTop + swatch + 16 + line + cell * PreviewHeight / PreviewWidth + 8;
        for (int index = 0; index < wallpapers.Length; index++) wallpapers[index].Bounds = new(left + index * (cell + 16), buttonTop, cell, control);
        int imageTop = buttonTop + control + 16 + line;
        int button = Math.Min(160, inner / 3);
        imagePath.Bounds = new(left, imageTop, inner - button - 12, control);
        loadImage.Bounds = new(left + inner - button, imageTop, button, control);
    }

    public void Close() => panel.Handle(new(InputEventType.Reset));
}
