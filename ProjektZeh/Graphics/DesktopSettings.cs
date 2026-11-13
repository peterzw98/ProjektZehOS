using System;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Graphics;

internal sealed record DesktopSettings
{
    public const int CustomWallpaper = 4;
    public bool Dark { get; init; } = true;
    public int Accent { get; init; }
    public int Wallpaper { get; init; }
    public string Image { get; init; } = "";
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;

    public string Serialize() => $"version=1\ndark={Dark}\naccent={Accent}\nwallpaper={Wallpaper}\nimage={Image}\nwidth={Width}\nheight={Height}\n";

    public static DesktopSettings Parse(string text)
    {
        var value = new DesktopSettings();
        int width = value.Width, height = value.Height;
        foreach (string line in text.Split('\n'))
        {
            int separator = line.IndexOf('=');
            if (separator < 0) continue;
            string key = line[..separator].Trim(), data = line[(separator + 1)..].Trim();
            // Unbekannte oder ungueltige Werte behalten den sicheren Standard; der Desktop startet trotzdem.
            switch (key)
            {
                case "dark" when bool.TryParse(data, out bool dark): value = value with { Dark = dark }; break;
                case "accent" when int.TryParse(data, out int accent) && accent >= 0 && accent < UiPainter.AccentNames.Length:
                    value = value with { Accent = accent }; break;
                case "wallpaper" when int.TryParse(data, out int wallpaper) && wallpaper is >= 0 and <= CustomWallpaper:
                    value = value with { Wallpaper = wallpaper }; break;
                case "image" when data.Length <= 240: value = value with { Image = data }; break;
                case "width" when int.TryParse(data, out int parsed): width = parsed; break;
                case "height" when int.TryParse(data, out int parsed): height = parsed; break;
            }
        }
        if (value.Wallpaper == CustomWallpaper && value.Image.Length == 0) value = value with { Wallpaper = 0 };
        return DisplayMode.IsSupportedSize(width, height) ? value with { Width = width, Height = height } : value;
    }
}

internal sealed class DesktopPreferences
{
    public const int MaximumImageBytes = 16 * 1024 * 1024;
    private const string FileName = "zehos-desktop.conf";
    private readonly FileService files;
    public DesktopSettings Settings { get; private set; } = new();
    public WallpaperImage? CustomImage { get; private set; }
    public string Status { get; private set; } = "Noch nicht gespeichert";
    public bool StatusIsError { get; private set; }
    /// <summary>Steigt bei jeder sichtbaren Aenderung, damit der Desktop Vorberechnetes erneuert.</summary>
    public int Revision { get; private set; }

    public DesktopPreferences(FileService files)
    {
        this.files = files;
        var loaded = files.ReadText(FileName);
        if (loaded.IsOK())
        {
            Settings = DesktopSettings.Parse(loaded.Value!);
            Status = "Einstellungen geladen";
            if (Settings.Wallpaper == DesktopSettings.CustomWallpaper && !LoadImage(Settings.Image).IsOK())
            {
                Settings = Settings with { Wallpaper = 0 };
                SetStatus("Eigenes Hintergrundbild fehlt oder ist ungueltig; Standardbild aktiv.", true);
            }
        }
        UiPainter.SetTheme(Settings.Dark, Settings.Accent);
    }

    /// <summary>Uebernimmt die Werte sofort und versucht sie dauerhaft zu speichern.</summary>
    public ResultErr Apply(DesktopSettings settings)
    {
        Settings = settings;
        Revision++;
        UiPainter.SetTheme(settings.Dark, settings.Accent);
        return Save();
    }

    /// <summary>Laedt ein eigenes Bild; bei Fehlern bleibt der bisherige Hintergrund unveraendert.</summary>
    public ResultErr UseImage(string path)
    {
        var result = LoadImage(path);
        if (!result.IsOK())
        {
            SetStatus("Bild nicht uebernommen: " + result.Message + " Bisheriger Hintergrund bleibt.", true);
            return result;
        }
        return Apply(Settings with { Wallpaper = DesktopSettings.CustomWallpaper, Image = path });
    }

    private ResultErr LoadImage(string path)
    {
        var bytes = files.ReadBytes(path, MaximumImageBytes);
        if (!bytes.IsOK()) return bytes.Status;
        try { CustomImage = WallpaperImage.Decode(bytes.Value!); return ResultErr.CreateSuccess(); }
        catch (ArgumentException exception) { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr Save()
    {
        var result = files.WriteText(FileName, Settings.Serialize());
        if (result.IsOK()) SetStatus("Gespeichert in " + files.CurrentDirectory.TrimEnd('/') + "/" + FileName, false);
        // Die technische Ursache steht im Ergebnis; sichtbar bleibt eine kurze, verstaendliche Meldung.
        else SetStatus("Nur fuer diese Sitzung: Speichern fehlgeschlagen (Datenlaufwerk eingebunden?).", true);
        return result;
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }
}
