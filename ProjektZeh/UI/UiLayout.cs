using System;

namespace ProjektZeh.UI;

internal static class UiLayout
{
    public const int Gap = 8;
    public const int ToolbarHeight = 44;

    public static void Row(int x, int y, int width, params ReadOnlySpan<UiControl> controls)
    {
        int cell = Math.Max(1, (width - (controls.Length - 1) * Gap) / Math.Max(1, controls.Length));
        for (int index = 0; index < controls.Length; index++)
            controls[index].Bounds = new(x + index * (cell + Gap), y, cell, ToolbarHeight);
    }
}
