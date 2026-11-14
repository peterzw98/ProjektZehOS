using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shared;
using ProjektZeh.UI;

namespace ProjektZeh.Installer;

internal enum SetupPage { Welcome, License, Disk, Confirm, Installing, Done, Error }

/// <summary>
/// Vollbild-Einrichtungsassistent mit dem Ablauf von Windows Setup, aber in der Gestaltung des Desktops:
/// Standardhintergrund, Fensterrahmen und gemeinsame Steuerelemente im Standardthema. Wird wie der
/// Anmeldebildschirm von einer kooperativen Schleife ueber Handle/Update/Render angetrieben.
/// </summary>
/// <remarks>
/// Die Cosmos-Standardschrift ordnet Zeichen ueber 127 nicht nach Latin-1 zu; Umlaute werden deshalb umschrieben.
/// </remarks>
internal sealed class SetupWizard
{
    private const int Black = unchecked((int)0xff000000);

    private const string LicenseText =
        "ZEHOS - EXPERIMENTELLES BETRIEBSSYSTEM\n\n" +
        "ZehOS ist ein Lern- und Experimentierprojekt. Es wird ohne jede Gewaehrleistung bereitgestellt " +
        "und ist nicht fuer den produktiven Einsatz bestimmt.\n\n" +
        "Die Installation legt auf der gewaehlten Zielplatte eine neue Partitionstabelle mit einer einzigen " +
        "EFI-Systempartition (FAT32) an. Alle vorhandenen Daten auf dieser Platte gehen dabei verloren.\n\n" +
        "ZehOS startet nur ueber UEFI. Ein Start ueber BIOS wird nicht unterstuetzt.\n\n" +
        "Sichern Sie vorher alle wichtigen Daten. Verwenden Sie nach Moeglichkeit eine virtuelle Maschine " +
        "oder eine leere Testplatte.\n\n" +
        "Mit der Installation bestaetigen Sie, dass Sie diese Hinweise gelesen haben und ZehOS auf eigenes " +
        "Risiko verwenden.";

    private static readonly string[] StageTexts =
    [
        "Platte wird partitioniert",
        "Dateisystem wird erstellt",
        "Dateien werden kopiert",
        "Startkonfiguration wird geschrieben"
    ];

    private readonly IInstallService service;
    private readonly UiPainter painter;
    private readonly int width;
    private readonly int height;
    private readonly int[] wallpaper;
    private readonly UiRect dialog;
    private readonly int titleHeight;
    private readonly int footerTop;
    private readonly int contentX;
    private readonly int contentWidth;
    private readonly int headingY;
    private readonly int bodyTop;
    private readonly int bodyBottom;
    private readonly int buttonHeight;
    private readonly UiRect prompt;
    private readonly UiButton start;
    private readonly UiButton back;
    private readonly UiButton next;
    private readonly UiButton cancel;
    private readonly UiButton refresh;
    private readonly UiButton yes;
    private readonly UiButton no;
    private readonly SetupCheckBox accept;
    private readonly SetupCheckBox erase;
    private readonly SetupTextView license;
    private readonly SetupDiskList disks;
    private readonly UiPanel promptPanel = new();
    private UiPanel panel = new();
    private bool cancelShown;
    private IInstallJob? job;
    private InstallDisk? target;
    private ResultErr error = ResultErr.CreateSuccess();
    private SetupPage errorReturn;

    public SetupPage Page { get; private set; }
    /// <summary>Nur nach Eingaben, Fortschritt oder Seitenwechseln muss neu gezeichnet werden.</summary>
    public bool IsDirty { get; private set; } = true;
    public bool IsCancelPromptOpen { get; private set; }
    public UiControl? FocusedControl => IsCancelPromptOpen ? promptPanel.Focused : panel.Focused;

    public SetupWizard(IInstallService service, UiPainter painter, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(painter);
        this.service = service;
        this.painter = painter;
        this.width = width;
        this.height = height;
        // Derselbe Standardhintergrund wie auf dem Desktop vor eigenen Einstellungen; einmal berechnet, pro Bild nur kopiert.
        var defaults = new DesktopSettings();
        wallpaper = new int[width * height];
        WallpaperImage.Create(wallpaper, width, height, defaults.Wallpaper, defaults.Dark, UiPainter.Accent);
        int glyph = painter.GlyphHeight;
        // 880x640 passt in 1024x768; groessere Anzeigen erhalten einen mitwachsenden Dialog ohne ueberlange Textzeilen,
        // kleinere behalten einen schmalen Rand zum Hintergrund.
        int dialogWidth = Math.Min(width - 24, Math.Max(880, width * 3 / 5));
        int dialogHeight = Math.Min(height - 24, Math.Max(640, height * 7 / 10));
        dialog = new((width - dialogWidth) / 2, (height - dialogHeight) / 2, dialogWidth, dialogHeight);
        titleHeight = Math.Max(Window.TitleHeight, glyph + 8);
        buttonHeight = glyph + 16;
        footerTop = dialog.Y + dialog.Height - buttonHeight - 32;
        contentX = dialog.X + 32;
        contentWidth = dialog.Width - 64;
        headingY = dialog.Y + titleHeight + 24;
        bodyTop = headingY + glyph + 20;
        bodyBottom = footerTop - 20;

        start = new("Jetzt installieren", () => Show(SetupPage.License)) { Primary = true };
        back = new("< Zurueck", Back);
        next = new("Weiter >", Next) { Primary = true };
        cancel = new("Abbrechen", OpenCancelPrompt);
        refresh = new("Aktualisieren", Refresh);
        // Die Rueckfrage hebt keine Antwort farbig hervor; der Fokus liegt auf der sicheren Antwort.
        yes = new("Ja", Cancel);
        no = new("Nein", CloseCancelPrompt);
        accept = new("Ich akzeptiere die Hinweise");
        erase = new("");
        license = new(LicenseText);
        disks = new();

        int startWidth = ButtonWidth(start.Label) + 48;
        start.Bounds = new(dialog.X + (dialog.Width - startWidth) / 2, 0, startWidth, buttonHeight);
        int checkHeight = glyph + 8;
        accept.Bounds = new(contentX, bodyBottom - checkHeight, contentWidth, checkHeight);
        license.Bounds = new(contentX, bodyTop, contentWidth, bodyBottom - checkHeight - 16 - bodyTop);
        int listHeight = bodyBottom - bodyTop - buttonHeight - 2 * glyph - 32;
        disks.Bounds = new(contentX, bodyTop, contentWidth, listHeight);
        refresh.Bounds = new(contentX, bodyTop + listHeight + 12, ButtonWidth(refresh.Label), buttonHeight);

        int promptWidth = Math.Min(Math.Max(600, dialogWidth * 5 / 8), dialog.Width - 64);
        int promptLines = Paragraph(null, painter, PromptText, 0, 0, promptWidth - 84, 0);
        int promptHeight = titleHeight + 24 + promptLines * glyph + 24 + buttonHeight + 24;
        prompt = new((width - promptWidth) / 2, (height - promptHeight) / 2, promptWidth, promptHeight);
        int promptButtons = prompt.Y + prompt.Height - buttonHeight - 20;
        no.Bounds = new(prompt.X + prompt.Width - 24 - ButtonWidth(no.Label), promptButtons, ButtonWidth(no.Label), buttonHeight);
        yes.Bounds = new(no.Bounds.X - 12 - ButtonWidth(yes.Label), promptButtons, ButtonWidth(yes.Label), buttonHeight);
        promptPanel.Add(yes);
        promptPanel.Add(no);

        Show(SetupPage.Welcome);
    }

    private const string PromptText =
        "Moechten Sie Setup wirklich abbrechen? ZehOS wird nicht installiert und der Computer wird neu gestartet.";

    public void Handle(InputEvent value)
    {
        IsDirty = true;
        bool press = value.Type == InputEventType.KeyDown && !value.IsRepeat;
        if (IsCancelPromptOpen)
        {
            if (press && value.KeyCode == (byte)PhysicalKey.Escape) CloseCancelPrompt();
            else promptPanel.Handle(value);
            return;
        }
        if (press && value.KeyCode == (byte)PhysicalKey.Escape)
        {
            if (cancelShown) OpenCancelPrompt();
            return;
        }
        // Wie in Windows Setup loest Enter ausserhalb einer Schaltflaeche die Standardschaltflaeche aus.
        if (press && value.KeyCode == (byte)PhysicalKey.Enter && panel.Focused is not UiButton)
        {
            if (Page == SetupPage.Welcome) Show(SetupPage.License);
            else if (next.Enabled && Page is not SetupPage.Installing and not SetupPage.Error) Next();
            return;
        }
        panel.Handle(value);
        UpdateNext();
    }

    /// <summary>Fuehrt hoechstens einen Installationsschritt aus, damit Eingaben und Zeichnen zwischendurch drankommen.</summary>
    public void Update()
    {
        if (job == null) return;
        IsDirty = true;
        if (!job.IsCompleted)
        {
            var status = job.Step();
            if (!status.IsOK()) { Fail(status); return; }
        }
        if (job.IsCompleted)
        {
            job = null;
            Show(SetupPage.Done);
        }
    }

    /// <summary>Zeigt einen Fehler an, der ausserhalb des Assistenten aufgetreten ist, etwa eine Ausnahme der Installation.</summary>
    public void Fail(ResultErr status)
    {
        job = null;
        IsCancelPromptOpen = false;
        error = status;
        errorReturn = Page switch { SetupPage.Installing => SetupPage.Disk, SetupPage.Error => errorReturn, _ => Page };
        Show(SetupPage.Error);
    }

    private void Show(SetupPage page)
    {
        IsDirty = true;
        if (page == SetupPage.Disk && !RefreshDisks()) return;
        panel.Focus(null);
        panel = new();
        Page = page;
        UiControl? focus = null;
        switch (page)
        {
            case SetupPage.Welcome:
                start.Bounds = start.Bounds with { Y = WelcomeButtonY() };
                panel.Add(start);
                Buttons(null, null, cancel);
                focus = start;
                break;
            case SetupPage.License:
                panel.Add(license);
                panel.Add(accept);
                Buttons(back, "Weiter >", cancel);
                focus = accept;
                break;
            case SetupPage.Disk:
                panel.Add(disks);
                panel.Add(refresh);
                Buttons(back, "Weiter >", cancel);
                focus = disks;
                break;
            case SetupPage.Confirm:
                target = disks.Selected;
                erase.Checked = false;
                if (target!.RequiresErase)
                {
                    erase.Label = $"Alle Daten auf {target.Name} loeschen";
                    erase.Bounds = new(contentX, ConfirmWarningY() + WarningHeight() + 16, contentWidth, painter.GlyphHeight + 8);
                    panel.Add(erase);
                    focus = erase;
                }
                Buttons(back, "Installieren", cancel);
                focus ??= next;
                break;
            case SetupPage.Installing:
                Buttons(null, null, null);
                break;
            case SetupPage.Done:
                Buttons(null, "Neu starten", null);
                focus = next;
                break;
            case SetupPage.Error:
                Buttons(back, null, cancel);
                focus = back;
                break;
        }
        UpdateNext();
        panel.Focus(focus);
    }

    /// <summary>Legt die sichtbaren Fussschaltflaechen rechtsbuendig und in Windows-Reihenfolge an.</summary>
    private void Buttons(UiButton? previous, string? nextLabel, UiButton? abort)
    {
        cancelShown = abort != null;
        int right = dialog.X + dialog.Width - 24;
        int y = footerTop + 16;
        if (abort != null) right = Place(abort, right, y) - 12;
        if (nextLabel != null) { next.Label = nextLabel; right = Place(next, right, y) - 12; }
        // Zwischen Zurueck und Weiter bleibt wie bei Windows Setup weniger Abstand als zu Abbrechen.
        if (previous != null) Place(previous, right + 4, y);
        if (previous != null) panel.Add(previous);
        if (nextLabel != null) panel.Add(next);
        if (abort != null) panel.Add(abort);
    }

    private int Place(UiButton button, int right, int y)
    {
        int buttonWidth = ButtonWidth(button.Label);
        button.Bounds = new(right - buttonWidth, y, buttonWidth, buttonHeight);
        return right - buttonWidth;
    }

    private int ButtonWidth(string label) => Math.Max(112, label.Length * painter.GlyphWidth + 32);

    private void UpdateNext() => next.Enabled = Page switch
    {
        SetupPage.License => accept.Checked,
        SetupPage.Disk => disks.Selected != null,
        SetupPage.Confirm => !target!.RequiresErase || erase.Checked,
        _ => true
    };

    private void Next()
    {
        switch (Page)
        {
            case SetupPage.License: Show(SetupPage.Disk); break;
            case SetupPage.Disk: Show(SetupPage.Confirm); break;
            case SetupPage.Confirm: Begin(); break;
            case SetupPage.Done: Restart(); break;
        }
    }

    private void Back()
    {
        switch (Page)
        {
            case SetupPage.License: Show(SetupPage.Welcome); break;
            case SetupPage.Disk: Show(SetupPage.License); break;
            case SetupPage.Confirm: Show(SetupPage.Disk); break;
            case SetupPage.Error: Show(errorReturn); break;
        }
    }

    private void Begin()
    {
        // Loeschen wird nur nach der ausdruecklichen Bestaetigung fuer genau diese Platte angefordert.
        var result = service.Begin(target!.Name, target.RequiresErase && erase.Checked);
        if (!result.IsOK()) { Fail(result.Status); return; }
        if (result.Value == null)
        {
            Fail(ResultErr.CreateFailure(ResultCode.Failure, "Setup hat keinen Installationsauftrag erhalten."));
            return;
        }
        job = result.Value;
        Show(SetupPage.Installing);
    }

    private void Refresh()
    {
        if (RefreshDisks()) UpdateNext();
    }

    /// <summary>Liest die Platten neu ein; nach einer fehlgeschlagenen Installation kann sich die Partitionierung geaendert haben.</summary>
    private bool RefreshDisks()
    {
        var result = service.GetDisks();
        if (!result.IsOK()) { Fail(result.Status); return false; }
        disks.SetItems(result.Value ?? []);
        return true;
    }

    private void Restart()
    {
        var status = service.Restart();
        if (!status.IsOK()) Fail(status);
    }

    private void OpenCancelPrompt()
    {
        if (!cancelShown) return;
        panel.Handle(new(InputEventType.Reset));
        IsCancelPromptOpen = true;
        // Wie bei Windows ist die sichere Antwort vorausgewaehlt.
        promptPanel.Focus(no);
    }

    private void CloseCancelPrompt()
    {
        promptPanel.Handle(new(InputEventType.Reset));
        IsCancelPromptOpen = false;
    }

    private void Cancel()
    {
        CloseCancelPrompt();
        Restart();
    }

    public void Render(PixelSurface surface, int pointerX, int pointerY, PointerButtons buttons)
    {
        ArgumentNullException.ThrowIfNull(surface);
        IsDirty = false;
        surface.ResetClip();
        surface.DrawPixels(wallpaper, 0, 0, width, height);
        DrawWindow(surface, dialog, "ZehOS Setup", !IsCancelPromptOpen);
        // Fussbereich wie Menue- und Taskleiste in Panel mit Trennlinie.
        surface.FillRectangle(dialog.X + 1, footerTop, dialog.Width - 2, dialog.Y + dialog.Height - 1 - footerTop, UiPainter.Panel);
        surface.FillRectangle(dialog.X + 1, footerTop, dialog.Width - 2, 1, UiPainter.Border);
        switch (Page)
        {
            case SetupPage.Welcome: DrawWelcome(surface); break;
            case SetupPage.License: Title(surface, "Wichtige Hinweise"); break;
            case SetupPage.Disk: DrawDisk(surface); break;
            case SetupPage.Confirm: DrawConfirm(surface); break;
            case SetupPage.Installing: DrawInstalling(surface); break;
            case SetupPage.Done: DrawDone(surface); break;
            case SetupPage.Error: DrawError(surface); break;
        }
        panel.Draw(surface, painter, 0, 0, !IsCancelPromptOpen);
        if (IsCancelPromptOpen)
        {
            DrawWindow(surface, prompt, "ZehOS Setup", true);
            WarningIcon(surface, prompt.X + 20, prompt.Y + titleHeight + 24 + (painter.GlyphHeight - 24) / 2);
            Paragraph(surface, painter, PromptText, prompt.X + 60, prompt.Y + titleHeight + 24, prompt.Width - 84, UiPainter.Foreground);
            promptPanel.Draw(surface, painter, 0, 0, true);
        }
        // UiButton zeigt den Fokus als Akzentrahmen, der auf einer Akzentflaeche verschwindet; innen deshalb ein Kontrastring.
        if (FocusedControl is UiButton { Primary: true, Enabled: true } focused)
            Ring(surface, focused.Bounds, 4, UiPainter.AccentText);
        Desktop.DrawPointer(surface, pointerX, pointerY, buttons);
    }

    /// <summary>
    /// Zeichnet einen Dialog in der Fenstersprache von Window.Render: Versatzschatten, Rahmen mit Akzentanteil und
    /// Titelleiste in Panel. Window selbst passt nicht, weil es Fensterknoepfe und eine IApplication voraussetzt.
    /// </summary>
    private void DrawWindow(PixelSurface surface, UiRect bounds, string title, bool active)
    {
        // Window haelt seine Schattenfarben privat; aus dem Thema abgeleitete Toene treffen sie im dunklen Thema.
        int outer = UiPainter.Mix(UiPainter.Panel, Black, 45);
        int inner = UiPainter.Mix(UiPainter.Panel, Black, 65);
        surface.FillRectangle(bounds.X + bounds.Width, bounds.Y + 8, 6, bounds.Height, outer);
        surface.FillRectangle(bounds.X + 6, bounds.Y + bounds.Height, bounds.Width, 8, outer);
        surface.FillRectangle(bounds.X + bounds.Width, bounds.Y + 4, 3, bounds.Height, inner);
        surface.FillRectangle(bounds.X + 3, bounds.Y + bounds.Height, bounds.Width, 4, inner);
        surface.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, active ? UiPainter.Mix(UiPainter.Accent, UiPainter.Border, 45) : UiPainter.Border);
        surface.FillRectangle(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2, UiPainter.Background);
        surface.FillRectangle(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, titleHeight - 1, UiPainter.Panel);
        surface.FillRectangle(bounds.X + 1, bounds.Y + titleHeight - 1, bounds.Width - 2, 1, UiPainter.Border);
        DrawLogo(surface, bounds.X + 12, bounds.Y + (titleHeight - AppIcons.Size) / 2, AppIcons.Size);
        int left = bounds.X + 22 + AppIcons.Size;
        painter.Text(surface, title, left, bounds.Y + (titleHeight - painter.GlyphHeight) / 2, bounds.X + bounds.Width - 12 - left,
            active ? UiPainter.Foreground : UiPainter.Secondary);
    }

    private static void Ring(PixelSurface surface, UiRect bounds, int inset, int color)
    {
        int left = bounds.X + inset, top = bounds.Y + inset, right = bounds.X + bounds.Width - inset, bottom = bounds.Y + bounds.Height - inset;
        surface.FillRectangle(left, top, right - left, 2, color);
        surface.FillRectangle(left, bottom - 2, right - left, 2, color);
        surface.FillRectangle(left, top, 2, bottom - top, color);
        surface.FillRectangle(right - 2, top, 2, bottom - top, color);
    }

    /// <summary>Seitenueberschrift in Akzentfarbe wie die Abschnittstitel der Einstellungen.</summary>
    private void Title(PixelSurface surface, string text) => painter.Text(surface, text, contentX, headingY, contentWidth, UiPainter.Accent);

    private int LogoSize => Math.Max(72, 3 * painter.GlyphHeight);

    private int WelcomeTextY() => WelcomeTop() + LogoSize + 20 + 2 * painter.GlyphHeight + 32;

    private int WelcomeButtonY() => WelcomeTextY() + Paragraph(null, painter, WelcomeText, 0, 0, WelcomeWidth, 0) * painter.GlyphHeight + 32;

    /// <summary>Begrenzt die Begruessung auf eine ruhige Zeilenlaenge, damit sie im breiten Full-HD-Dialog nicht ausfranst.</summary>
    private int WelcomeWidth => Math.Min(contentWidth, 48 * painter.GlyphWidth);

    /// <summary>Logo, Text und Startschaltflaeche stehen als Gruppe mittig zwischen Titelleiste und Fussbereich.</summary>
    private int WelcomeTop()
    {
        int glyph = painter.GlyphHeight;
        int group = LogoSize + 20 + 2 * glyph + 32 + Paragraph(null, painter, WelcomeText, 0, 0, WelcomeWidth, 0) * glyph + 32 + buttonHeight;
        int top = dialog.Y + titleHeight;
        return top + Math.Max(16, (footerTop - top - group) / 2);
    }

    private const string WelcomeText = "Dieser Assistent richtet ZehOS auf einer Festplatte dieses Computers ein.";

    private void DrawWelcome(PixelSurface surface)
    {
        int glyph = painter.GlyphHeight;
        int y = WelcomeTop();
        DrawLogo(surface, dialog.X + (dialog.Width - LogoSize) / 2, y, LogoSize);
        y += LogoSize + 20;
        Centered(surface, "ZehOS", y, UiPainter.Foreground);
        y += glyph + 8;
        Centered(surface, $"Version {service.ProductVersion}", y, UiPainter.Secondary);
        Paragraph(surface, painter, WelcomeText, contentX + (contentWidth - WelcomeWidth) / 2, WelcomeTextY(), WelcomeWidth, UiPainter.Foreground, center: true);
        FooterNote(surface, "Experimentelles Betriebssystem", cancel.Bounds.X - contentX - 8);
    }

    private void FooterNote(PixelSurface surface, string text, int noteWidth) =>
        painter.Text(surface, text, dialog.X + 24, footerTop + 16 + (buttonHeight - painter.GlyphHeight) / 2, noteWidth, UiPainter.Secondary);

    private void Centered(PixelSurface surface, string text, int y, int color)
    {
        int textWidth = Math.Min(contentWidth, text.Length * painter.GlyphWidth);
        painter.Text(surface, text, contentX + (contentWidth - textWidth) / 2, y, contentWidth, color);
    }

    /// <summary>Eigenes ZehOS-Zeichen statt eines fremden Logos: Akzentkachel mit Z in der Schriftfarbe von Akzentflaechen.</summary>
    private static void DrawLogo(PixelSurface surface, int x, int y, int size)
    {
        int margin = size * 2 / 9;
        int bar = Math.Max(2, size / 10);
        surface.FillRectangle(x, y, size, size, UiPainter.Accent);
        surface.FillRectangle(x + margin, y + margin, size - 2 * margin, bar, UiPainter.AccentText);
        surface.FillRectangle(x + margin, y + size - margin - bar, size - 2 * margin, bar, UiPainter.AccentText);
        // Diagonale aus parallelen Linien, damit sie so kraeftig wie die Balken wirkt.
        for (int offset = 0; offset < bar + 2; offset++)
            surface.DrawLine(x + size - margin - bar - 1 + offset, y + margin + bar, x + margin - 1 + offset, y + size - margin - bar, UiPainter.AccentText);
    }

    private void DrawDisk(PixelSurface surface)
    {
        Title(surface, "Wo soll ZehOS installiert werden?");
        var selected = disks.Selected;
        int y = refresh.Bounds.Y + refresh.Bounds.Height + 12;
        if (disks.Count == 0)
        {
            Paragraph(surface, painter, "Keine geeignete Platte gefunden. Schliessen Sie eine Platte an und waehlen Sie Aktualisieren.",
                contentX, y, contentWidth, UiPainter.Secondary, max: 2);
            return;
        }
        if (selected == null || !selected.RequiresErase) return;
        WarningIcon(surface, contentX, y + 4);
        Paragraph(surface, painter,
            $"{Capitalize(selected.ContentText)} vorhanden. Alle Daten auf dieser Platte werden geloescht.",
            contentX + 36, y, contentWidth - 36, UiPainter.Foreground, max: 2);
    }

    private int ConfirmWarningY() => bodyTop + 4 * (painter.GlyphHeight + 6) + 16;

    private int WarningHeight() => Paragraph(null, painter, ConfirmWarningText(), 0, 0, contentWidth - 68, 0) * painter.GlyphHeight + 28;

    private string ConfirmWarningText() => target!.RequiresErase
        ? $"{Capitalize(target.ContentText)} vorhanden. Bei der Installation werden alle Daten auf dieser Platte unwiderruflich geloescht."
        : "Die Platte ist leer. Es werden keine vorhandenen Daten geloescht.";

    private void DrawConfirm(PixelSurface surface)
    {
        Title(surface, "Bereit zur Installation");
        int line = painter.GlyphHeight + 6;
        int label = 13 * painter.GlyphWidth;
        Summary(surface, 0, "Zielplatte", $"{target!.Name} ({SetupDiskList.FormatSize(target.SizeBytes)})", label, line);
        Summary(surface, 1, "Version", $"ZehOS {service.ProductVersion}", label, line);
        Summary(surface, 2, "Startmodus", "UEFI", label, line);
        Summary(surface, 3, "Aufteilung", "1 EFI-Systempartition (FAT32)", label, line);
        bool destructive = target.RequiresErase;
        var box = new UiRect(contentX, ConfirmWarningY(), contentWidth, WarningHeight());
        // Karten wie im Taskmanager; die Loeschwarnung erhaelt einen Rahmen und Schimmer in der Warnfarbe.
        surface.FillRectangle(box.X, box.Y, box.Width, box.Height, destructive ? UiPainter.Warning : UiPainter.Border);
        surface.FillRectangle(box.X + 1, box.Y + 1, box.Width - 2, box.Height - 2,
            destructive ? UiPainter.Mix(UiPainter.Warning, UiPainter.Background, 12) : UiPainter.Muted);
        if (destructive) WarningIcon(surface, box.X + 16, box.Y + 14 + (painter.GlyphHeight - 24) / 2);
        else CheckMark(surface, box.X + 18, box.Y + 14 + (painter.GlyphHeight - 24) / 2, UiPainter.Success);
        Paragraph(surface, painter, ConfirmWarningText(), box.X + 52, box.Y + 14, box.Width - 68, UiPainter.Foreground);
    }

    private void Summary(PixelSurface surface, int row, string label, string value, int labelWidth, int line)
    {
        int y = bodyTop + row * line;
        painter.Text(surface, label, contentX, y, labelWidth, UiPainter.Secondary);
        painter.Text(surface, value, contentX + labelWidth, y, contentWidth - labelWidth, UiPainter.Foreground);
    }

    private void DrawInstalling(PixelSurface surface)
    {
        Title(surface, "ZehOS wird installiert");
        int glyph = painter.GlyphHeight;
        int y = bodyTop + Paragraph(surface, painter, "Bitte warten. Dies kann einige Minuten dauern.", contentX, bodyTop, contentWidth, UiPainter.Secondary) * glyph + 16;
        var stage = job?.Stage ?? InstallStage.Completed;
        for (int index = 0; index < StageTexts.Length; index++)
        {
            int rowY = y + index * (glyph + 8);
            int iconY = rowY + (glyph - 24) / 2;
            if (index < (int)stage) CheckMark(surface, contentX + 2, iconY, UiPainter.Success);
            else if (index == (int)stage)
            {
                // Pfeil auf die laufende Phase wie in der Statusliste von Windows Setup.
                for (int row = 0; row < 16; row++)
                    surface.FillRectangle(contentX + 6, iconY + 4 + row, 8 - Math.Abs(row - 8) + 1, 1, UiPainter.Accent);
            }
            // Kommende Phasen gedaempft; das Thema kennt nur Secondary, daher zum Hintergrund hin gemischt.
            int color = index == (int)stage ? UiPainter.Foreground : index < (int)stage ? UiPainter.Secondary
                : UiPainter.Mix(UiPainter.Secondary, UiPainter.Background, 55);
            painter.Text(surface, StageTexts[index], contentX + 36, rowY, contentWidth - 36, color);
        }
        int percent = Math.Clamp(job?.Percent ?? 100, 0, 100);
        // Fortschritt direkt unter den Phasen, damit die Seite im hohen Full-HD-Dialog als Gruppe zusammenbleibt.
        int barY = y + StageTexts.Length * (glyph + 8) + 24 + glyph + 8;
        painter.Text(surface, $"Gesamtfortschritt: {percent} %", contentX, barY - glyph - 8, contentWidth, UiPainter.Foreground);
        surface.FillRectangle(contentX, barY, contentWidth, 16, UiPainter.Border);
        surface.FillRectangle(contentX + 1, barY + 1, contentWidth - 2, 14, UiPainter.Muted);
        surface.FillRectangle(contentX + 1, barY + 1, (contentWidth - 2) * percent / 100, 14, UiPainter.Accent);
        FooterNote(surface, "Schalten Sie den Computer nicht aus.", contentWidth);
    }

    private void DrawDone(PixelSurface surface)
    {
        Title(surface, "Installation abgeschlossen");
        CheckMark(surface, contentX + 2, bodyTop + (painter.GlyphHeight - 24) / 2, UiPainter.Success);
        Paragraph(surface, painter,
            $"ZehOS {service.ProductVersion} wurde auf {target?.Name} installiert.\n\nEntfernen Sie das Installationsmedium und starten Sie den Computer neu. Im Bootmenue erscheint der Eintrag ZehOS.",
            contentX + 40, bodyTop, contentWidth - 40, UiPainter.Foreground);
    }

    private void DrawError(PixelSurface surface)
    {
        Title(surface, "Setup ist auf einen Fehler gestossen");
        int iconY = bodyTop + (painter.GlyphHeight - 24) / 2;
        surface.FillRectangle(contentX, iconY, 24, 24, UiPainter.Danger);
        for (int offset = 0; offset < 3; offset++)
        {
            surface.DrawLine(contentX + 6 + offset, iconY + 6, contentX + 15 + offset, iconY + 17, UiPainter.Background);
            surface.DrawLine(contentX + 17 - offset, iconY + 6, contentX + 8 - offset, iconY + 17, UiPainter.Background);
        }
        int glyph = painter.GlyphHeight;
        int lines = Paragraph(surface, painter, error.Message.Length > 0 ? error.Message : "Unbekannter Fehler.",
            contentX + 40, bodyTop, contentWidth - 40, UiPainter.Foreground, max: Math.Max(1, (bodyBottom - bodyTop) / glyph - 3));
        int y = bodyTop + (lines + 1) * glyph;
        painter.Text(surface, $"Fehlercode: {error.Code}", contentX + 40, y, contentWidth - 40, UiPainter.Secondary);
        Paragraph(surface, painter, "Mit Zurueck kehren Sie zum vorherigen Schritt zurueck.", contentX + 40, y + glyph + 8, contentWidth - 40, UiPainter.Secondary);
    }

    internal static void WarningIcon(PixelSurface surface, int x, int y)
    {
        for (int row = 0; row < 24; row++) surface.FillRectangle(x + 13 - row / 2 - 1, y + row, row + 2, 1, UiPainter.Warning);
        surface.FillRectangle(x + 12, y + 8, 2, 9, UiPainter.Background);
        surface.FillRectangle(x + 12, y + 19, 2, 2, UiPainter.Background);
    }

    internal static void CheckMark(PixelSurface surface, int x, int y, int color)
    {
        for (int offset = 0; offset < 3; offset++)
        {
            surface.DrawLine(x + 2, y + 12 + offset, x + 8, y + 18 + offset, color);
            surface.DrawLine(x + 8, y + 18 + offset, x + 20, y + 4 + offset, color);
        }
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Bricht Text an Leerzeichen um und zeichnet die Zeilen ab first, hoechstens max; ohne Flaeche wird nur gezaehlt.
    /// Arbeitet auf Ausschnitten, damit beim Zeichnen keine Zeilenobjekte entstehen.
    /// </summary>
    /// <remarks>
    /// UiPainter.Paragraph genuegt hier nicht: Der Assistent braucht Absaetze, Zaehlen ohne Zeichnen fuer das Layout,
    /// Bildlauf ab einer Zeile und zentrierte Zeilen.
    /// </remarks>
    internal static int Paragraph(PixelSurface? surface, UiPainter painter, string text, int x, int y, int width, int color,
        int first = 0, int max = int.MaxValue, bool center = false)
    {
        int columns = Math.Max(1, width / painter.GlyphWidth);
        int line = 0;
        int start = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            do
            {
                int length = end - start;
                if (length > columns)
                {
                    int space = text.LastIndexOf(' ', start + columns, columns + 1);
                    length = space > start ? space - start : columns;
                }
                if (surface != null && line >= first && line - first < max)
                {
                    int offset = center ? (width - length * painter.GlyphWidth) / 2 : 0;
                    painter.Text(surface, text.AsSpan(start, length), x + offset, y + (line - first) * painter.GlyphHeight, width, color);
                }
                line++;
                start += length;
                if (start < end && text[start] == ' ') start++;
            } while (start < end);
            start = end + 1;
        }
        return line;
    }
}

/// <summary>Kontrollkaestchen; die gemeinsamen Steuerelemente kennen keines.</summary>
internal sealed class SetupCheckBox(string label) : UiControl
{
    private bool pressed;
    public string Label { get; set; } = label;
    public bool Checked { get; set; }

    public override void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { pressed = false; return; }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
            pressed = Bounds.Contains(value.X, value.Y);
        if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left)
        {
            if (pressed && Bounds.Contains(value.X, value.Y)) Checked = !Checked;
            pressed = false;
        }
        if (value.Type == InputEventType.KeyDown && !value.IsRepeat && value.KeyCode == (byte)PhysicalKey.Space) Checked = !Checked;
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        var box = new UiRect(Bounds.X + 2, Bounds.Y + (Bounds.Height - 24) / 2, 24, 24);
        // Secondary statt Border als Rahmen: ein leeres Kaestchen muss sich deutlich vom Hintergrund abheben.
        surface.FillRectangle(x + box.X, y + box.Y, 24, 24, UiPainter.Secondary);
        surface.FillRectangle(x + box.X + 1, y + box.Y + 1, 22, 22, UiPainter.Background);
        if (Checked)
        {
            surface.FillRectangle(x + box.X + 1, y + box.Y + 1, 22, 22, UiPainter.Accent);
            SetupWizard.CheckMark(surface, x + box.X + 1, y + box.Y, UiPainter.AccentText);
        }
        int textX = x + Bounds.X + 40;
        painter.Text(surface, Label, textX, y + Bounds.Y + (Bounds.Height - painter.GlyphHeight) / 2, Bounds.Width - 44, UiPainter.Foreground);
        if (active && Focused)
        {
            // Fokus markiert die Beschriftung, weil ein Akzentrahmen am angekreuzten Kaestchen nicht auffiele.
            int textWidth = Math.Min(Bounds.Width - 40, Label.Length * painter.GlyphWidth + 8);
            surface.FillRectangle(textX - 4, y + Bounds.Y + Bounds.Height - 2, textWidth, 2, UiPainter.Accent);
        }
    }
}

/// <summary>Bildlauffaehiger Hinweistext; Pfeile und Bildtasten blaettern, ein Klick auf die Leiste blaettert seitenweise.</summary>
internal sealed class SetupTextView(string text) : UiControl
{
    private int visible = 1;
    private int lines = 1;
    public int FirstLine { get; private set; }

    public override void Handle(InputEvent value)
    {
        int delta = 0;
        if (value.Type == InputEventType.KeyDown && !value.IsTextKey)
        {
            delta = (PhysicalKey)value.KeyCode switch
            {
                PhysicalKey.Up => -1, PhysicalKey.Down => 1,
                PhysicalKey.PageUp => -visible, PhysicalKey.PageDown => visible,
                PhysicalKey.Home => -lines, PhysicalKey.End => lines,
                _ => 0
            };
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left && value.X >= Bounds.X + Bounds.Width - 16)
            delta = value.Y < Bounds.Y + Bounds.Height / 2 ? -visible : visible;
        FirstLine = Math.Clamp(FirstLine + delta, 0, Math.Max(0, lines - visible));
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        var bounds = Bounds;
        UiPainter.Frame(surface, bounds, x, y, active && Focused);
        int textWidth = bounds.Width - 48;
        lines = SetupWizard.Paragraph(null, painter, text, 0, 0, textWidth, 0);
        visible = Math.Max(1, (bounds.Height - 24) / painter.GlyphHeight);
        FirstLine = Math.Clamp(FirstLine, 0, Math.Max(0, lines - visible));
        SetupWizard.Paragraph(surface, painter, text, x + bounds.X + 16, y + bounds.Y + 12, textWidth, UiPainter.Foreground, FirstLine, visible);
        if (lines <= visible) return;
        int track = bounds.Height - 12;
        int thumb = Math.Max(24, track * visible / lines);
        int thumbY = (track - thumb) * FirstLine / Math.Max(1, lines - visible);
        int trackX = x + bounds.X + bounds.Width - 14;
        surface.FillRectangle(trackX, y + bounds.Y + 6, 6, track, UiPainter.Muted);
        surface.FillRectangle(trackX, y + bounds.Y + 6 + thumbY, 6, thumb, UiPainter.Secondary);
    }
}

/// <summary>
/// Plattenliste mit Spaltenkopf wie in der Partitionsauswahl von Windows Setup; gezeichnet wie UiList, das aber weder
/// Spalten noch Warnzeichen je Eintrag kennt.
/// </summary>
internal sealed class SetupDiskList : UiControl
{
    private InstallDisk[] items = [];
    private string[] sizes = [];
    private string[] partitions = [];
    private int first;
    private int visible = 1;
    private int rowHeight = 28;
    private int headerHeight = 24;
    public int SelectedIndex { get; private set; } = -1;
    public int Count => items.Length;
    public InstallDisk? Selected => SelectedIndex >= 0 ? items[SelectedIndex] : null;

    /// <summary>Uebernimmt neue Platten und behaelt die Auswahl, solange dieselbe Platte noch vorhanden ist.</summary>
    public void SetItems(InstallDisk[] values)
    {
        string? previous = Selected?.Name;
        items = values;
        sizes = new string[values.Length];
        partitions = new string[values.Length];
        SelectedIndex = values.Length == 0 ? -1 : 0;
        for (int index = 0; index < values.Length; index++)
        {
            sizes[index] = FormatSize(values[index].SizeBytes);
            partitions[index] = values[index].PartitionCount > 0 ? $"{values[index].PartitionCount}" : values[index].IsBlank ? "keine" : "unklar";
            if (string.Equals(values[index].Name, previous, StringComparison.Ordinal)) SelectedIndex = index;
        }
        first = 0;
    }

    internal static string FormatSize(ulong bytes)
    {
        const ulong mebibyte = 1024UL * 1024;
        const ulong gibibyte = mebibyte * 1024;
        if (bytes < gibibyte) return $"{bytes / mebibyte} MB";
        ulong tenths = bytes / (gibibyte / 10);
        return $"{tenths / 10},{tenths % 10} GB";
    }

    public override void Handle(InputEvent value)
    {
        if (items.Length == 0) return;
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            int offset = value.Y - Bounds.Y - 1 - headerHeight;
            if (offset >= 0 && offset / rowHeight < visible && first + offset / rowHeight < items.Length)
                SelectedIndex = first + offset / rowHeight;
        }
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return;
        int delta = (PhysicalKey)value.KeyCode switch
        {
            PhysicalKey.Up => -1, PhysicalKey.Down => 1,
            PhysicalKey.PageUp => -visible, PhysicalKey.PageDown => visible,
            PhysicalKey.Home => -items.Length, PhysicalKey.End => items.Length,
            _ => 0
        };
        if (delta != 0) SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, items.Length - 1);
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        var bounds = Bounds;
        int glyph = painter.GlyphHeight;
        rowHeight = glyph + 12;
        headerHeight = glyph + 8;
        UiPainter.Frame(surface, bounds, x, y, active && Focused);
        int left = x + bounds.X + 2;
        int top = y + bounds.Y + 1;
        int inner = bounds.Width - 4;
        int partitionColumn = left + inner - 12 * painter.GlyphWidth;
        int sizeColumn = partitionColumn - 11 * painter.GlyphWidth;
        int nameColumn = left + 44;
        surface.FillRectangle(left, top + headerHeight - 1, inner, 1, UiPainter.Border);
        int headerText = top + (headerHeight - glyph) / 2;
        painter.Text(surface, "Name", nameColumn, headerText, sizeColumn - nameColumn - 8, UiPainter.Secondary);
        painter.Text(surface, "Groesse", sizeColumn, headerText, partitionColumn - sizeColumn, UiPainter.Secondary);
        painter.Text(surface, "Partitionen", partitionColumn, headerText, left + inner - partitionColumn, UiPainter.Secondary);
        visible = Math.Max(1, (bounds.Height - 2 - headerHeight) / rowHeight);
        if (SelectedIndex >= 0) first = Math.Clamp(first, Math.Max(0, SelectedIndex - visible + 1), SelectedIndex);
        for (int row = 0; row < Math.Min(visible, items.Length - first); row++)
        {
            int index = first + row;
            int rowTop = top + headerHeight + row * rowHeight;
            if (index == SelectedIndex) surface.FillRectangle(left + 2, rowTop + 2, inner - 4, rowHeight - 4, UiPainter.Selection);
            int textTop = rowTop + (rowHeight - glyph) / 2;
            // Platten mit Partitionen tragen dauerhaft ein Warnzeichen; das Loeschen wird spaeter einzeln bestaetigt.
            if (items[index].RequiresErase) SetupWizard.WarningIcon(surface, left + 10, rowTop + (rowHeight - 24) / 2);
            else DiskIcon(surface, left + 10, rowTop + (rowHeight - 20) / 2);
            painter.Text(surface, items[index].Name, nameColumn, textTop, sizeColumn - nameColumn - 8, UiPainter.Foreground);
            painter.Text(surface, sizes[index], sizeColumn, textTop, partitionColumn - sizeColumn, UiPainter.Foreground);
            painter.Text(surface, partitions[index], partitionColumn, textTop, left + inner - partitionColumn, UiPainter.Foreground);
        }
    }

    /// <summary>Liniensymbol einer Platte im Stil von AppIcons.</summary>
    private static void DiskIcon(PixelSurface surface, int x, int y)
    {
        surface.FillRectangle(x, y + 4, 26, 14, UiPainter.Secondary);
        surface.FillRectangle(x + 2, y + 6, 22, 10, UiPainter.Muted);
        surface.FillRectangle(x + 18, y + 13, 3, 2, UiPainter.Success);
    }
}
