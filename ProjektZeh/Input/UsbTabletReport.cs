using System;

namespace ProjektZeh.Input;

internal static class UsbTabletReport
{
    internal static bool TryDecode(ReadOnlySpan<byte> report, int width, int height,
        out int x, out int y, out byte buttons, out int wheel)
    {
        x = y = 0;
        buttons = 0;
        wheel = 0;
        if (report.Length < 6) return false;

        int rawX = report[1] | (report[2] << 8);
        int rawY = report[3] | (report[4] << 8);
        x = (int)((long)rawX * width / 0x8000);
        y = (int)((long)rawY * height / 0x8000);
        buttons = (byte)(report[0] & 7);
        wheel = unchecked((sbyte)report[5]);
        return true;
    }
}
