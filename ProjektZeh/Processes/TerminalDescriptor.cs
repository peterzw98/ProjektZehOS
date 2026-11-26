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
/// Mit einer <see cref="LinuxTerminalSession"/> liest fd 0 die Enter-Zeilen des Fensters (V3 stdin); ohne sie ist die Eingabe
/// leer wie <c>/dev/null</c> (Prüfläufe über die serielle Schnittstelle).
/// </summary>
internal sealed class TerminalDescriptor : ILinuxInput
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
        _session = output as LinuxTerminalSession;
    }

    private readonly LinuxTerminalSession? _session;

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
        Reclaim();
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
        Reclaim();
        if (_length > 0)
        {
            Emit();
        }
    }

    public long ReadInput(Span<byte> dst)
    {
        if (_session == null)
        {
            return 0;
        }

        PublishPrompt();
        return _session.ReadInput(dst);
    }

    public bool InputReady => _session?.InputReady ?? true;

    public bool WaitInput(long timeoutMs)
    {
        if (_session == null)
        {
            return true;
        }

        // poll wartet wie read auf eine Zeile (etwa REPLs, die erst pollen): die Aufforderung muss schon dabei sichtbar sein.
        PublishPrompt();
        return _session.WaitInput(timeoutMs);
    }

    /// <summary>
    /// Wie an einem Terminal steht eine angefangene Zeile ("> ") schon da, wenn das Programm auf Eingabe wartet: Die Shell zeigt
    /// sie als Eingabeaufforderung und gibt sie mit der Enter-Zeile aus.
    /// </summary>
    private void PublishPrompt()
    {
        if (_length > 0 && !_session!.InputReady)
        {
            _session.SetPrompt(_line.AsSpan(0, _length).ToArray());
            _length = 0;
        }
    }

    /// <summary>Holt eine nicht beantwortete Eingabeaufforderung zurück in die Zeile: Weitere Ausgaben setzen sie fort.</summary>
    private void Reclaim()
    {
        byte[]? prompt = _session?.TakePrompt();
        if (prompt == null)
        {
            return;
        }

        // Die Aufforderung war vor dem Lesen die ganze angefangene Zeile; was danach kam, hängt dahinter.
        int keep = Math.Min(_length, MaxLine - prompt.Length);
        Array.Copy(_line, 0, _line, prompt.Length, Math.Max(0, keep));
        prompt.CopyTo(_line, 0);
        _length = prompt.Length + Math.Max(0, keep);
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
