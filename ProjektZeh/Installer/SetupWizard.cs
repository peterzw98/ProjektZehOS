using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shared;
using ProjektZeh.UI;

namespace ProjektZeh.Installer;

internal enum SetupPage { Welcome, License, Disk, Confirm, Installing, Done, Error }

/// <summary>
/// Vollbild-Einrichtungsassistent im Stil von Windows Setup. Wird wie der Anmeldebildschirm von einer
/// kooperativen Schleife ueber Handle/Update/Render angetrieben.
/// </summary>
/// <remarks>
/// Die Cosmos-Standardschrift ordnet Zeichen ueber 127 nicht nach Latin-1 zu; Umlaute werden deshalb umschrieben.
/// </remarks>
internal sealed class SetupWizard
{
    internal const int Ink = unchecked((int)0xff1b1b1b);
    internal const int SecondaryInk = unchecked((int)0xff5c6670);
    internal const int DisabledInk = unchecked((int)0xffa3a3a3);
    internal const int Primary = unchecked((int)0xff0067c0);
    internal const int PrimaryPressed = unchecked((int)0xff004e93);
    internal const int Surface = unchecked((int)0xffffffff);
    internal const int ControlFill = unchecked((int)0xffe8e8e8);
    internal const int ControlBorder = unchecked((int)0xffa8a8a8);
    internal const int Selection = unchecked((int)0xffcce8ff);
    internal const int Warning = unchecked((int)0xfff2a900);
    private const int Heading = unchecked((int)0xff0c3c78);
    private const int Footer = unchecked((int)0xfff3f3f3);
    private const int Divider = unchecked((int)0xffdcdcdc);
    private const int Shadow = unchecked((int)0xff0a2550);
    private const int WarningBack = unchecked((int)0xfffff8e1);
    private const int WarningBorder = unchecked((int)0xffe8c66a);
    private const int ErrorRed = unchecked((int)0xffc42b1c);
    private const int Success = unchecked((int)0xff0f7b0f);
    private const int ProgressFill = unchecked((int)0xff06a82a);

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
    private readonly SetupButton start;
    private readonly SetupButton back;
    private readonly SetupButton next;
    private readonly SetupButton cancel;
    private readonly SetupButton refresh;
    private readonly SetupButton yes;
    private readonly SetupButton no;
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
        int glyph = painter.GlyphHeight;
        // 880x640 passt in 1024x768; kleinere Anzeigen behalten einen schmalen Rand zum Hintergrund.
        int dialogWidth = Math.Min(880, width - 24);
        int dialogHeight = Math.Min(640, height - 24);
        dialog = new((width - dialogWidth) / 2, (height - dialogHeight) / 2, dialogWidth, dialogHeight);
        titleHeight = glyph + 12;
        buttonHeight = glyph + 14;
        footerTop = dialog.Y + dialog.Height - buttonHeight - 28;
        contentX = dialog.X + 32;
        contentWidth = dialog.Width - 64;
        headingY = dialog.Y + titleHeight + 20;
        bodyTop = headingY + glyph + 16;
        bodyBottom = footerTop - 16;

        start = new("Jetzt installieren", () => Show(SetupPage.License), primary: true);
        back = new("< Zurueck", Back);
        next = new("Weiter >", Next, primary: true);
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
        start.Bounds = new(dialog.X + (dialog.Width - startWidth) / 2, 0, startWidth, buttonHeight + 6);
        int checkHeight = glyph + 8;
        accept.Bounds = new(contentX, bodyBottom - checkHeight, contentWidth, checkHeight);
        license.Bounds = new(contentX, bodyTop, contentWidth, bodyBottom - checkHeight - 16 - bodyTop);
        int listHeight = bodyBottom - bodyTop - buttonHeight - 2 * glyph - 32;
        disks.Bounds = new(contentX, bodyTop, contentWidth, listHeight);
        refresh.Bounds = new(contentX, bodyTop + listHeight + 12, ButtonWidth(refresh.Label), buttonHeight);

        int promptWidth = Math.Min(600, dialog.Width - 64);
        int promptLines = Paragraph(null, painter, PromptText, 0, 0, promptWidth - 84, 0);
        int promptHeight = titleHeight + 20 + promptLines * glyph + 24 + buttonHeight + 24;
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
        if (press && value.KeyCode == (byte)PhysicalKey.Enter && panel.Focused is not SetupButton)
        {
            if (Page == SetupPage.Welcome) start.Activate();
            else if (next.Enabled && Page is not SetupPage.Installing and not SetupPage.Error) next.Activate();
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
                if (target!.PartitionCount > 0)
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
    private void Buttons(SetupButton? previous, string? nextLabel, SetupButton? abort)
    {
        cancelShown = abort != null;
        int right = dialog.X + dialog.Width - 24;
        int y = footerTop + 14;
        if (abort != null) right = Place(abort, right, y) - 12;
        if (nextLabel != null) { next.Label = nextLabel; right = Place(next, right, y) - 12; }
        // Zwischen Zurueck und Weiter bleibt wie bei Windows Setup weniger Abstand als zu Abbrechen.
        if (previous != null) Place(previous, right + 4, y);
        if (previous != null) panel.Add(previous);
        if (nextLabel != null) panel.Add(next);
        if (abort != null) panel.Add(abort);
    }

    private int Place(SetupButton button, int right, int y)
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
        SetupPage.Confirm => target!.PartitionCount == 0 || erase.Checked,
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
        var result = service.Begin(target!.Name, target.PartitionCount > 0 && erase.Checked);
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
        Backdrop(surface);
        DrawDialogFrame(surface, dialog, "ZehOS Setup");
        surface.FillRectangle(dialog.X + 1, footerTop, dialog.Width - 2, dialog.Y + dialog.Height - 1 - footerTop, Footer);
        surface.FillRectangle(dialog.X + 1, footerTop, dialog.Width - 2, 1, Divider);
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
            DrawDialogFrame(surface, prompt, "ZehOS Setup");
            WarningIcon(surface, prompt.X + 20, prompt.Y + titleHeight + 20 + (painter.GlyphHeight - 24) / 2);
            Paragraph(surface, painter, PromptText, prompt.X + 60, prompt.Y + titleHeight + 20, prompt.Width - 84, Ink);
            promptPanel.Draw(surface, painter, 0, 0, true);
        }
        Desktop.DrawPointer(surface, pointerX, pointerY, buttons);
    }

    private void Backdrop(PixelSurface surface)
    {
        // Senkrechter Verlauf zeilenweise; kein zusaetzlicher Vollbildpuffer.
        for (int row = 0; row < height; row++)
        {
            int blend = row * 255 / Math.Max(1, height - 1);
            int red = 0x24 + (0x08 - 0x24) * blend / 255;
            int green = 0x5e + (0x2a - 0x5e) * blend / 255;
            int blue = 0xb8 + (0x66 - 0xb8) * blend / 255;
            surface.FillRectangle(0, row, width, 1, unchecked((int)0xff000000) | red << 16 | green << 8 | blue);
        }
    }

    private void DrawDialogFrame(PixelSurface surface, UiRect bounds, string title)
    {
        surface.FillRectangle(bounds.X + 8, bounds.Y + 8, bounds.Width, bounds.Height, Shadow);
        surface.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, unchecked((int)0xff6f86a6));
        surface.FillRectangle(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2, Surface);
        surface.FillRectangle(bounds.X + 1, bounds.Y + titleHeight, bounds.Width - 2, 1, Divider);
        int mark = titleHeight - 16;
        DrawLogo(surface, bounds.X + 12, bounds.Y + 8, mark);
        painter.Text(surface, title, bounds.X + 24 + mark, bounds.Y + (titleHeight - painter.GlyphHeight) / 2, bounds.Width - 40 - mark, Ink);
    }

    private void Title(PixelSurface surface, string text)
    {
        // Die Bitmapschrift kennt keinen Fettschnitt; doppelt versetztes Zeichnen hebt die Ueberschrift hervor.
        painter.Text(surface, text, contentX, headingY, contentWidth, Heading);
        painter.Text(surface, text, contentX + 1, headingY, contentWidth - 1, Heading);
    }

    private int WelcomeTextY() => WelcomeTop() + 72 + 20 + 2 * painter.GlyphHeight + 32;

    private int WelcomeButtonY() => WelcomeTextY() + Paragraph(null, painter, WelcomeText, 0, 0, contentWidth, 0) * painter.GlyphHeight + 32;

    /// <summary>Logo, Text und Startschaltflaeche stehen als Gruppe mittig zwischen Titelleiste und Fussbereich.</summary>
    private int WelcomeTop()
    {
        int glyph = painter.GlyphHeight;
        int group = 72 + 20 + 2 * glyph + 32 + Paragraph(null, painter, WelcomeText, 0, 0, contentWidth, 0) * glyph + 32 + buttonHeight + 6;
        int top = dialog.Y + titleHeight;
        return top + Math.Max(16, (footerTop - top - group) / 2);
    }

    private const string WelcomeText = "Dieser Assistent richtet ZehOS auf einer Festplatte dieses Computers ein.";

    private void DrawWelcome(PixelSurface surface)
    {
        int glyph = painter.GlyphHeight;
        int y = WelcomeTop();
        DrawLogo(surface, dialog.X + (dialog.Width - 72) / 2, y, 72);
        y += 72 + 20;
        Centered(surface, "ZehOS", y, Heading);
        Centered(surface, "ZehOS", y, Heading, 1);
        y += glyph + 8;
        Centered(surface, $"Version {service.ProductVersion}", y, SecondaryInk);
        Paragraph(surface, painter, WelcomeText, contentX, WelcomeTextY(), contentWidth, Ink, center: true);
        painter.Text(surface, "Experimentelles Betriebssystem", contentX - 8, footerTop + 14 + (buttonHeight - glyph) / 2,
            cancel.Bounds.X - contentX - 8, SecondaryInk);
    }

    private void Centered(PixelSurface surface, string text, int y, int color, int offset = 0)
    {
        int textWidth = Math.Min(contentWidth, text.Length * painter.GlyphWidth);
        painter.Text(surface, text, contentX + (contentWidth - textWidth) / 2 + offset, y, contentWidth, color);
    }

    /// <summary>Eigenes ZehOS-Zeichen statt eines fremden Logos: blaue Kachel mit hellem Z.</summary>
    private static void DrawLogo(PixelSurface surface, int x, int y, int size)
    {
        int margin = size * 2 / 9;
        int bar = Math.Max(2, size / 10);
        surface.FillRectangle(x, y, size, size, Primary);
        surface.FillRectangle(x + margin, y + margin, size - 2 * margin, bar, Surface);
        surface.FillRectangle(x + margin, y + size - margin - bar, size - 2 * margin, bar, Surface);
        // Diagonale aus parallelen Linien, damit sie so kraeftig wie die Balken wirkt.
        for (int offset = 0; offset < bar + 2; offset++)
            surface.DrawLine(x + size - margin - bar - 1 + offset, y + margin + bar, x + margin - 1 + offset, y + size - margin - bar, Surface);
    }

    private void DrawDisk(PixelSurface surface)
    {
        Title(surface, "Wo soll ZehOS installiert werden?");
        var selected = disks.Selected;
        int y = refresh.Bounds.Y + refresh.Bounds.Height + 12;
        if (disks.Count == 0)
        {
            Paragraph(surface, painter, "Keine geeignete Platte gefunden. Schliessen Sie eine Platte an und waehlen Sie Aktualisieren.",
                contentX, y, contentWidth, SecondaryInk, max: 2);
            return;
        }
        if (selected == null || selected.PartitionCount == 0) return;
        WarningIcon(surface, contentX, y + 4);
        Paragraph(surface, painter,
            $"{PartitionText(selected.PartitionCount)} vorhanden. Alle Daten auf dieser Platte werden geloescht.",
            contentX + 36, y, contentWidth - 36, Ink, max: 2);
    }

    private int ConfirmWarningY() => bodyTop + 4 * (painter.GlyphHeight + 6) + 16;

    private int WarningHeight() => Paragraph(null, painter, ConfirmWarningText(), 0, 0, contentWidth - 68, 0) * painter.GlyphHeight + 28;

    private string ConfirmWarningText() => target!.PartitionCount > 0
        ? $"{PartitionText(target.PartitionCount)} vorhanden. Bei der Installation werden alle Daten auf dieser Platte unwiderruflich geloescht."
        : "Die Platte enthaelt keine Partitionen. Es werden keine vorhandenen Daten geloescht.";

    private void DrawConfirm(PixelSurface surface)
    {
        Title(surface, "Bereit zur Installation");
        int line = painter.GlyphHeight + 6;
        int label = 13 * painter.GlyphWidth;
        Summary(surface, 0, "Zielplatte", $"{target!.Name} ({SetupDiskList.FormatSize(target.SizeBytes)})", label, line);
        Summary(surface, 1, "Version", $"ZehOS {service.ProductVersion}", label, line);
        Summary(surface, 2, "Startmodus", "UEFI", label, line);
        Summary(surface, 3, "Aufteilung", "1 EFI-Systempartition (FAT32)", label, line);
        bool destructive = target.PartitionCount > 0;
        var box = new UiRect(contentX, ConfirmWarningY(), contentWidth, WarningHeight());
        surface.FillRectangle(box.X, box.Y, box.Width, box.Height, destructive ? WarningBorder : Divider);
        surface.FillRectangle(box.X + 1, box.Y + 1, box.Width - 2, box.Height - 2, destructive ? WarningBack : Footer);
        if (destructive) WarningIcon(surface, box.X + 16, box.Y + 14 + (painter.GlyphHeight - 24) / 2);
        else CheckMark(surface, box.X + 18, box.Y + 14 + (painter.GlyphHeight - 24) / 2, Success);
        Paragraph(surface, painter, ConfirmWarningText(), box.X + 52, box.Y + 14, box.Width - 68, Ink);
    }

    private void Summary(PixelSurface surface, int row, string label, string value, int labelWidth, int line)
    {
        int y = bodyTop + row * line;
        painter.Text(surface, label, contentX, y, labelWidth, SecondaryInk);
        painter.Text(surface, value, contentX + labelWidth, y, contentWidth - labelWidth, Ink);
    }

    private void DrawInstalling(PixelSurface surface)
    {
        Title(surface, "ZehOS wird installiert");
        int glyph = painter.GlyphHeight;
        int y = bodyTop + Paragraph(surface, painter, "Bitte warten. Dies kann einige Minuten dauern.", contentX, bodyTop, contentWidth, SecondaryInk) * glyph + 16;
        var stage = job?.Stage ?? InstallStage.Completed;
        for (int index = 0; index < StageTexts.Length; index++)
        {
            int rowY = y + index * (glyph + 8);
            int iconY = rowY + (glyph - 24) / 2;
            if (index < (int)stage) CheckMark(surface, contentX + 2, iconY, Success);
            else if (index == (int)stage)
            {
                // Pfeil auf die laufende Phase wie in der Statusliste von Windows Setup.
                for (int row = 0; row < 16; row++)
                    surface.FillRectangle(contentX + 6, iconY + 4 + row, 8 - Math.Abs(row - 8) + 1, 1, Primary);
            }
            int color = index == (int)stage ? Ink : index < (int)stage ? SecondaryInk : DisabledInk;
            painter.Text(surface, StageTexts[index], contentX + 36, rowY, contentWidth - 36, color);
            if (index == (int)stage) painter.Text(surface, StageTexts[index], contentX + 37, rowY, contentWidth - 37, color);
        }
        int percent = Math.Clamp(job?.Percent ?? 100, 0, 100);
        int barY = bodyBottom - 20;
        painter.Text(surface, $"Gesamtfortschritt: {percent} %", contentX, barY - glyph - 8, contentWidth, Ink);
        surface.FillRectangle(contentX, barY, contentWidth, 20, ControlBorder);
        surface.FillRectangle(contentX + 1, barY + 1, contentWidth - 2, 18, unchecked((int)0xffe6e6e6));
        surface.FillRectangle(contentX + 1, barY + 1, (contentWidth - 2) * percent / 100, 18, ProgressFill);
        painter.Text(surface, "Schalten Sie den Computer nicht aus.", contentX - 8, footerTop + 14 + (buttonHeight - glyph) / 2,
            contentWidth, SecondaryInk);
    }

    private void DrawDone(PixelSurface surface)
    {
        Title(surface, "Installation abgeschlossen");
        CheckMark(surface, contentX + 2, bodyTop + (painter.GlyphHeight - 24) / 2, Success);
        Paragraph(surface, painter,
            $"ZehOS {service.ProductVersion} wurde auf {target?.Name} installiert.\n\nEntfernen Sie das Installationsmedium und starten Sie den Computer neu. Im Bootmenue erscheint der Eintrag ZehOS.",
            contentX + 40, bodyTop, contentWidth - 40, Ink);
    }

    private void DrawError(PixelSurface surface)
    {
        Title(surface, "Setup ist auf einen Fehler gestossen");
        int iconY = bodyTop + (painter.GlyphHeight - 24) / 2;
        surface.FillRectangle(contentX, iconY, 24, 24, ErrorRed);
        for (int offset = 0; offset < 3; offset++)
        {
            surface.DrawLine(contentX + 6 + offset, iconY + 6, contentX + 15 + offset, iconY + 17, Surface);
            surface.DrawLine(contentX + 17 - offset, iconY + 6, contentX + 8 - offset, iconY + 17, Surface);
        }
        int glyph = painter.GlyphHeight;
        int lines = Paragraph(surface, painter, error.Message.Length > 0 ? error.Message : "Unbekannter Fehler.",
            contentX + 40, bodyTop, contentWidth - 40, Ink, max: Math.Max(1, (bodyBottom - bodyTop) / glyph - 3));
        int y = bodyTop + (lines + 1) * glyph;
        painter.Text(surface, $"Fehlercode: {error.Code}", contentX + 40, y, contentWidth - 40, SecondaryInk);
        Paragraph(surface, painter, "Mit Zurueck kehren Sie zum vorherigen Schritt zurueck.", contentX + 40, y + glyph + 8, contentWidth - 40, SecondaryInk);
    }

    internal static void WarningIcon(PixelSurface surface, int x, int y)
    {
        for (int row = 0; row < 24; row++) surface.FillRectangle(x + 13 - row / 2 - 1, y + row, row + 2, 1, Warning);
        surface.FillRectangle(x + 12, y + 8, 2, 9, Ink);
        surface.FillRectangle(x + 12, y + 19, 2, 2, Ink);
    }

    internal static void CheckMark(PixelSurface surface, int x, int y, int color)
    {
        for (int offset = 0; offset < 3; offset++)
        {
            surface.DrawLine(x + 2, y + 12 + offset, x + 8, y + 18 + offset, color);
            surface.DrawLine(x + 8, y + 18 + offset, x + 20, y + 4 + offset, color);
        }
    }

    internal static string PartitionText(int count) => count == 1 ? "1 Partition" : $"{count} Partitionen";

    /// <summary>
    /// Bricht Text an Leerzeichen um und zeichnet die Zeilen ab first, hoechstens max; ohne Flaeche wird nur gezaehlt.
    /// Arbeitet auf Ausschnitten, damit beim Zeichnen keine Zeilenobjekte entstehen.
    /// </summary>
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

/// <summary>Helle Schaltflaeche im Windows-Stil; die dunkle UiButton-Gestaltung passt nicht zum Setupdialog.</summary>
internal sealed class SetupButton(string label, Action activate, bool primary = false) : UiControl
{
    private bool pressed;
    public string Label { get; set; } = label;

    public void Activate()
    {
        if (Enabled) activate();
    }

    public override void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { pressed = false; return; }
        if (!Enabled) return;
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
            pressed = Bounds.Contains(value.X, value.Y);
        if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left)
        {
            bool invoke = pressed && Bounds.Contains(value.X, value.Y);
            pressed = false;
            if (invoke) activate();
        }
        if (value.Type == InputEventType.KeyDown && !value.IsRepeat &&
            value.KeyCode is (byte)PhysicalKey.Enter or (byte)PhysicalKey.Space) activate();
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        var bounds = Bounds;
        bool focused = active && Focused && Enabled;
        bool filled = primary && Enabled;
        int border = !Enabled ? unchecked((int)0xffd2d2d2) : focused ? SetupWizard.Primary : filled ? SetupWizard.Primary : SetupWizard.ControlBorder;
        int fill = !Enabled ? unchecked((int)0xfff0f0f0) : filled ? pressed ? SetupWizard.PrimaryPressed : SetupWizard.Primary
            : pressed ? unchecked((int)0xffcce4f7) : SetupWizard.ControlFill;
        surface.FillRectangle(x + bounds.X, y + bounds.Y, bounds.Width, bounds.Height, border);
        surface.FillRectangle(x + bounds.X + 1, y + bounds.Y + 1, bounds.Width - 2, bounds.Height - 2, fill);
        // Fokusrahmen innen als zweite Linie, damit er auch auf der gefuellten Hauptschaltflaeche sichtbar bleibt.
        if (focused)
        {
            int ring = filled ? SetupWizard.Surface : SetupWizard.Primary;
            surface.FillRectangle(x + bounds.X + 3, y + bounds.Y + 3, bounds.Width - 6, 1, ring);
            surface.FillRectangle(x + bounds.X + 3, y + bounds.Y + bounds.Height - 4, bounds.Width - 6, 1, ring);
            surface.FillRectangle(x + bounds.X + 3, y + bounds.Y + 3, 1, bounds.Height - 6, ring);
            surface.FillRectangle(x + bounds.X + bounds.Width - 4, y + bounds.Y + 3, 1, bounds.Height - 6, ring);
        }
        int textWidth = Math.Min(bounds.Width - 16, Label.Length * painter.GlyphWidth);
        painter.Text(surface, Label, x + bounds.X + (bounds.Width - textWidth) / 2, y + bounds.Y + (bounds.Height - painter.GlyphHeight) / 2,
            bounds.Width - 16, !Enabled ? SetupWizard.DisabledInk : filled ? SetupWizard.Surface : SetupWizard.Ink);
    }
}

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
        int left = x + Bounds.X;
        int box = y + Bounds.Y + (Bounds.Height - 22) / 2;
        surface.FillRectangle(left + 2, box, 22, 22, Checked ? SetupWizard.Primary : unchecked((int)0xff5c5c5c));
        if (!Checked) surface.FillRectangle(left + 4, box + 2, 18, 18, SetupWizard.Surface);
        else SetupWizard.CheckMark(surface, left + 2, box, SetupWizard.Surface);
        int textX = left + 36;
        int textY = y + Bounds.Y + (Bounds.Height - painter.GlyphHeight) / 2;
        painter.Text(surface, Label, textX, textY, Bounds.Width - 40, SetupWizard.Ink);
        if (active && Focused)
        {
            // Fokus markiert wie in Windows die Beschriftung, nicht das Kaestchen.
            int textWidth = Math.Min(Bounds.Width - 36, Label.Length * painter.GlyphWidth + 8);
            surface.FillRectangle(textX - 4, y + Bounds.Y + Bounds.Height - 2, textWidth, 2, SetupWizard.Primary);
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
        surface.FillRectangle(x + bounds.X, y + bounds.Y, bounds.Width, bounds.Height, active && Focused ? SetupWizard.Primary : SetupWizard.ControlBorder);
        surface.FillRectangle(x + bounds.X + 1, y + bounds.Y + 1, bounds.Width - 2, bounds.Height - 2, SetupWizard.Surface);
        int textWidth = bounds.Width - 48;
        lines = SetupWizard.Paragraph(null, painter, text, 0, 0, textWidth, 0);
        visible = Math.Max(1, (bounds.Height - 24) / painter.GlyphHeight);
        FirstLine = Math.Clamp(FirstLine, 0, Math.Max(0, lines - visible));
        SetupWizard.Paragraph(surface, painter, text, x + bounds.X + 16, y + bounds.Y + 12, textWidth, SetupWizard.Ink, FirstLine, visible);
        int trackX = x + bounds.X + bounds.Width - 16;
        surface.FillRectangle(trackX, y + bounds.Y + 1, 15, bounds.Height - 2, unchecked((int)0xfff0f0f0));
        if (lines <= visible) return;
        int track = bounds.Height - 8;
        int thumb = Math.Max(24, track * visible / lines);
        int thumbY = (track - thumb) * FirstLine / Math.Max(1, lines - visible);
        surface.FillRectangle(trackX + 4, y + bounds.Y + 4 + thumbY, 8, thumb, unchecked((int)0xff8a8a8a));
    }
}

/// <summary>Plattenliste mit Spaltenkopf wie in der Partitionsauswahl von Windows Setup.</summary>
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
            partitions[index] = values[index].PartitionCount == 0 ? "keine" : $"{values[index].PartitionCount}";
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
        surface.FillRectangle(x + bounds.X, y + bounds.Y, bounds.Width, bounds.Height, active && Focused ? SetupWizard.Primary : SetupWizard.ControlBorder);
        surface.FillRectangle(x + bounds.X + 1, y + bounds.Y + 1, bounds.Width - 2, bounds.Height - 2, SetupWizard.Surface);
        int left = x + bounds.X + 1;
        int top = y + bounds.Y + 1;
        int inner = bounds.Width - 2;
        int partitionColumn = left + inner - 12 * painter.GlyphWidth;
        int sizeColumn = partitionColumn - 11 * painter.GlyphWidth;
        int nameColumn = left + 44;
        surface.FillRectangle(left, top, inner, headerHeight, unchecked((int)0xfff3f3f3));
        surface.FillRectangle(left, top + headerHeight - 1, inner, 1, unchecked((int)0xffdcdcdc));
        int headerText = top + (headerHeight - glyph) / 2;
        painter.Text(surface, "Name", nameColumn, headerText, sizeColumn - nameColumn - 8, SetupWizard.SecondaryInk);
        painter.Text(surface, "Groesse", sizeColumn, headerText, partitionColumn - sizeColumn, SetupWizard.SecondaryInk);
        painter.Text(surface, "Partitionen", partitionColumn, headerText, left + inner - partitionColumn, SetupWizard.SecondaryInk);
        visible = Math.Max(1, (bounds.Height - 2 - headerHeight) / rowHeight);
        if (SelectedIndex >= 0) first = Math.Clamp(first, Math.Max(0, SelectedIndex - visible + 1), SelectedIndex);
        for (int row = 0; row < Math.Min(visible, items.Length - first); row++)
        {
            int index = first + row;
            int rowTop = top + headerHeight + row * rowHeight;
            if (index == SelectedIndex)
            {
                surface.FillRectangle(left + 2, rowTop + 2, inner - 4, rowHeight - 4, active && Focused ? SetupWizard.Primary : SetupWizard.ControlBorder);
                surface.FillRectangle(left + 3, rowTop + 3, inner - 6, rowHeight - 6, SetupWizard.Selection);
            }
            int textTop = rowTop + (rowHeight - glyph) / 2;
            // Platten mit Partitionen tragen dauerhaft ein Warnzeichen; das Loeschen wird spaeter einzeln bestaetigt.
            if (items[index].PartitionCount > 0) SetupWizard.WarningIcon(surface, left + 10, rowTop + (rowHeight - 24) / 2);
            else DiskIcon(surface, left + 10, rowTop + (rowHeight - 20) / 2);
            painter.Text(surface, items[index].Name, nameColumn, textTop, sizeColumn - nameColumn - 8, SetupWizard.Ink);
            painter.Text(surface, sizes[index], sizeColumn, textTop, partitionColumn - sizeColumn, SetupWizard.Ink);
            painter.Text(surface, partitions[index], partitionColumn, textTop, left + inner - partitionColumn, SetupWizard.Ink);
        }
    }

    private static void DiskIcon(PixelSurface surface, int x, int y)
    {
        surface.FillRectangle(x, y + 4, 26, 14, unchecked((int)0xff6d7782));
        surface.FillRectangle(x + 2, y + 6, 22, 10, unchecked((int)0xffc9d1d9));
        surface.FillRectangle(x + 18, y + 13, 3, 2, unchecked((int)0xff06a82a));
    }
}
