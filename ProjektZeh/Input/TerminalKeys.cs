using System.Text;

namespace ProjektZeh.Input;

/// <summary>
/// Tasten als Bytes, wie ein Linux-Terminal (xterm) sie im Rohmodus an das Programm schickt (Ladybird V3 raw): Zeichen als UTF-8,
/// Enter als CR, Backspace als DEL (VERASE), Pfeile und Pos1/Ende als CSI-Folgen, Strg+Buchstabe als Steuerzeichen 1 bis 26.
/// </summary>
internal static class TerminalKeys
{
    // Scancode-Satz 1 der Buchstabenreihen (Q ab 16, A ab 30, Z ab 44), wie PhysicalKey.
    private const string RowQ = "qwertyuiop", RowA = "asdfghjkl", RowZ = "zxcvbnm";

    /// <summary>Der Buchstabe auf der Taste (klein), sonst '\0'.</summary>
    public static char Letter(byte code)
    {
        if (code >= 16 && code < 16 + RowQ.Length) return RowQ[code - 16];
        if (code >= 30 && code < 30 + RowA.Length) return RowA[code - 30];
        if (code >= 44 && code < 44 + RowZ.Length) return RowZ[code - 44];
        return '\0';
    }

    /// <summary>
    /// Bytes fuer das Ereignis; null, wenn es nichts sendet (Taste losgelassen, Text-Tastendruck vor seinem Textereignis, Taste ohne
    /// Terminalfolge). <paramref name="control"/>: Strg ist gedrueckt.
    /// </summary>
    public static byte[]? Encode(InputEvent value, bool control)
    {
        if (value.Type == InputEventType.Text)
            return control || char.IsControl(value.Character) ? null : Encoding.UTF8.GetBytes(value.Character.ToString());
        if (value.Type != InputEventType.KeyDown) return null;
        if (control)
        {
            char letter = Letter(value.KeyCode);
            return letter == '\0' ? null : new[] { (byte)(letter - 'a' + 1) };
        }
        if (value.IsTextKey) return null; // das Zeichen kommt mit dem folgenden Textereignis
        return (PhysicalKey)value.KeyCode switch
        {
            PhysicalKey.Enter => new[] { (byte)'\r' },
            PhysicalKey.Backspace => new byte[] { 0x7f },
            PhysicalKey.Tab => new[] { (byte)'\t' },
            PhysicalKey.Escape => new byte[] { 0x1b },
            PhysicalKey.Up => Csi('A'),
            PhysicalKey.Down => Csi('B'),
            PhysicalKey.Right => Csi('C'),
            PhysicalKey.Left => Csi('D'),
            PhysicalKey.Home => Csi('H'),
            PhysicalKey.End => Csi('F'),
            PhysicalKey.Delete => new byte[] { 0x1b, (byte)'[', (byte)'3', (byte)'~' },
            _ => null,
        };
    }

    private static byte[] Csi(char final) => new byte[] { 0x1b, (byte)'[', (byte)final };
}
