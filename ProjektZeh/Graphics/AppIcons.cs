namespace ProjektZeh.Graphics;

internal enum AppIcon { Menu, Files, Terminal, Editor, Settings, Tasks, Pong }

/// <summary>Einfache Liniensymbole, direkt mit den vorhandenen Zeichenfunktionen gezeichnet.</summary>
internal static class AppIcons
{
    public const int Size = 20;

    public static void Draw(PixelSurface surface, AppIcon icon, int x, int y, int color)
    {
        switch (icon)
        {
            case AppIcon.Menu:
                foreach ((int left, int top) in new[] { (0, 0), (12, 0), (0, 12), (12, 12) }) Outline(surface, x + left, y + top, 8, 8, color);
                break;
            case AppIcon.Files:
                surface.FillRectangle(x + 1, y + 2, 8, 3, color);
                Outline(surface, x, y + 5, 20, 13, color);
                break;
            case AppIcon.Terminal:
                Outline(surface, x, y + 2, 20, 16, color);
                Line(surface, x + 4, y + 7, x + 7, y + 10, color);
                Line(surface, x + 7, y + 10, x + 4, y + 13, color);
                surface.FillRectangle(x + 9, y + 12, 6, 2, color);
                break;
            case AppIcon.Editor:
                Outline(surface, x + 3, y, 14, 20, color);
                for (int row = 0; row < 3; row++) surface.FillRectangle(x + 6, y + 5 + row * 4, row == 2 ? 5 : 8, 2, color);
                break;
            case AppIcon.Settings:
                for (int row = 0; row < 3; row++)
                {
                    surface.FillRectangle(x, y + 3 + row * 6, 20, 2, color);
                    surface.FillRectangle(x + (row == 1 ? 4 : row == 0 ? 12 : 14), y + 1 + row * 6, 5, 6, color);
                }
                break;
            case AppIcon.Tasks:
                Line(surface, x, y + 10, x + 5, y + 10, color);
                Line(surface, x + 5, y + 10, x + 8, y + 2, color);
                Line(surface, x + 8, y + 2, x + 12, y + 18, color);
                Line(surface, x + 12, y + 18, x + 15, y + 10, color);
                Line(surface, x + 15, y + 10, x + 19, y + 10, color);
                break;
            case AppIcon.Pong:
                surface.FillRectangle(x + 1, y + 3, 3, 13, color);
                surface.FillRectangle(x + 16, y + 5, 3, 13, color);
                surface.FillRectangle(x + 9, y + 9, 3, 3, color);
                break;
        }
    }

    private static void Outline(PixelSurface surface, int x, int y, int width, int height, int color)
    {
        surface.FillRectangle(x, y, width, 2, color);
        surface.FillRectangle(x, y + height - 2, width, 2, color);
        surface.FillRectangle(x, y, 2, height, color);
        surface.FillRectangle(x + width - 2, y, 2, height, color);
    }

    private static void Line(PixelSurface surface, int x1, int y1, int x2, int y2, int color)
    {
        surface.DrawLine(x1, y1, x2, y2, color);
        surface.DrawLine(x1 + 1, y1, x2 + 1, y2, color);
    }
}
