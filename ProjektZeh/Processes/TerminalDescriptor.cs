using System;
using System.Text;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shell;

namespace ProjektZeh.Processes;

/// <summary>Empfänger roher Ausgabebytes (etwa ein Spiegel ins serielle Protokoll).</summary>
internal delegate void ByteSink(ReadOnlySpan<byte> data);

/// <summary>
/// Deskriptor 0 bis 2 eines Linux-Prozesses (V3): Bytes gehen zeilenweise an das Terminal. <see cref="ITextOutput"/> kennt nur
/// ganze Zeilen; eine Zeile ohne Zeilenende bleibt deshalb bis zum nächsten <c>\n</c>, bis <see cref="MaxLine"/> Bytes oder bis
/// <see cref="Flush"/> (Prozessende) stehen. Bytes werden erst beim Ausgeben als UTF-8 gelesen, damit ein Zeichen, das über zwei
/// <c>write</c>-Aufrufe verteilt ist, ganz bleibt.
/// </summary>
internal sealed class TerminalDescriptor : ILinuxDescriptor
{
    /// <summary>Höchstens so viele Bytes ohne Zeilenende werden gesammelt, dann als Zeile ausgegeben.</summary>
    public const int MaxLine = 4096;

    private readonly ITextOutput _output;
    private readonly byte[] _line = new byte[MaxLine];
    private int _length;
    private volatile bool _closed;

    public TerminalDescriptor(ITextOutput output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>Bekommt jeden Schreibaufruf unverändert, bevor er in Zeilen zerlegt wird (Prüfskripte vergleichen Bytes).</summary>
    public ByteSink? Mirror { get; set; }

    public long Write(ReadOnlySpan<byte> data)
    {
        if (_closed)
        {
            // Die Shell ist schon weiter (Programm nach dem Zeitlimit nicht beendet): Ausgaben verwerfen, wie ein geschlossenes Terminal.
            return data.Length;
        }

        Mirror?.Invoke(data);
        foreach (byte b in data)
        {
            if (b == (byte)'\n')
            {
                Emit();
                continue;
            }

            if (_length == MaxLine)
            {
                Emit();
            }

            _line[_length++] = b;
        }

        return data.Length;
    }

    /// <summary>
    /// Danach nimmt der Deskriptor nichts mehr an (Schreiben meldet Erfolg und verwirft). Darf von einem anderen Thread als dem
    /// schreibenden kommen; eine Zeile, die gerade ausgegeben wird, erscheint noch.
    /// </summary>
    public void Close() => _closed = true;

    /// <summary>Gibt eine angefangene Zeile aus (Prozessende).</summary>
    public void Flush()
    {
        if (_length > 0)
        {
            Emit();
        }
    }

    private void Emit()
    {
        _output.WriteLine(Printable(Encoding.UTF8.GetString(_line, 0, _length)));
        _length = 0;
    }

    /// <summary>
    /// Was ein Zeilenterminal von einer Zeile zeigen kann (V3-Review P3 c): Die Terminals von ZehOS kennen keine Steuerfolgen und
    /// zeigten ESC und andere Steuerzeichen als Zeichen. ANSI-Folgen (CSI <c>ESC [ … Endbyte</c>, OSC <c>ESC ] … BEL|ST</c>, sonst
    /// ESC und ein Zeichen) fallen weg, Backspace löscht das Zeichen davor, ein <c>\r</c> mitten in der Zeile beginnt sie neu
    /// (Fortschrittsanzeigen), eines am Ende gehört zum Zeilenende. Tab bleibt, die übrigen Steuerzeichen fallen weg.
    /// </summary>
    internal static string Printable(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\x1b')
            {
                i = SkipEscape(text, i);
                continue;
            }

            if (c == '\r')
            {
                if (i + 1 < text.Length)
                {
                    result.Clear();
                }

                continue;
            }

            if (c == '\b')
            {
                if (result.Length > 0)
                {
                    result.Length--;
                }

                continue;
            }

            if ((c < ' ' && c != '\t') || c == '\x7f' || (c >= '\x80' && c < '\xa0'))
            {
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>Index des letzten Zeichens der Steuerfolge ab dem ESC bei <paramref name="at"/>; eine abgeschnittene Folge reicht bis zum Ende.</summary>
    private static int SkipEscape(string text, int at)
    {
        if (at + 1 >= text.Length)
        {
            return at;
        }

        char kind = text[at + 1];
        if (kind == '[')
        {
            // CSI: Parameter und Zwischenbytes (0x20 bis 0x3F), dann ein Endbyte 0x40 bis 0x7E.
            int i = at + 2;
            while (i < text.Length && text[i] >= ' ' && text[i] <= '?')
            {
                i++;
            }

            return Math.Min(i, text.Length - 1);
        }

        if (kind == ']')
        {
            // OSC (etwa Fenstertitel): bis BEL oder ESC \.
            for (int i = at + 2; i < text.Length; i++)
            {
                if (text[i] == '\a')
                {
                    return i;
                }

                if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                {
                    return i + 1;
                }
            }

            return text.Length - 1;
        }

        return at + 1;
    }
}
