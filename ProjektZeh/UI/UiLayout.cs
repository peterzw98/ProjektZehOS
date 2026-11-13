using System;

namespace ProjektZeh.UI;

internal static class UiLayout
{
    public const int Gap = 8;
    public const int ToolbarHeight = 44;

    public static void Row(int x, int y, int width, UiControl first, UiControl second, UiControl third)
    {
        int cell = Math.Max(1, (width - 2 * Gap) / 3);
        first.Bounds = new(x, y, cell, ToolbarHeight);
        second.Bounds = new(x + cell + Gap, y, cell, ToolbarHeight);
        third.Bounds = new(x + 2 * (cell + Gap), y, cell, ToolbarHeight);
    }
}
