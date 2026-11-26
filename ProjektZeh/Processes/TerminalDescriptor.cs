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
/// Nur mit Sitzung ist der Deskriptor ein Terminal (<see cref="ILinuxTerminal"/>, V3 termios): Prüfläufe ohne Fenster behalten
/// voll gepuffertes stdout wie bei einer Umleitung in eine Datei, damit ihre erwarteten Ausgaben gleich bleiben.
/// </summary>
internal sealed class TerminalDescriptor : ILinuxInput, ILinuxTerminal
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

    // termios und Vordergrundgruppe liest und setzt nur der Programmthread (ioctl); fd 0 bis 2 sind dieses eine Objekt.
    private byte[] _termios = LinuxSyscalls.DefaultTermios();

    public bool IsTerminal => _session != null;

    // Kanonisch (ICANON): Zeilen sind Ausgaben eines Zeilenterminals; sonst (Rohmodus) zeichnet ein Zeileneditor die Zeile neu.
    private bool _canonical = true;

    public byte[] Termios
    {
        get => _termios;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            // ioctl liest immer TermiosSize Byte; ein kürzeres Feld (andere Aufrufer, Tests) wäre ein Programmfehler, nicht ein halber Zustand.
            if (value.Length < LinuxSyscalls.TermiosSize)
            {
                throw new ArgumentException($"termios braucht {LinuxSyscalls.TermiosSize} Byte, nicht {value.Length}", nameof(value));
            }

            _termios = value;
            uint iflag = BitConverter.ToUInt32(value, 0);
            uint lflag = BitConverter.ToUInt32(value, 12);
            _canonical = (lflag & LinuxSyscalls.ICANON) != 0;
            _session?.SetMode(_canonical, (lflag & LinuxSyscalls.ISIG) != 0, (iflag & LinuxSyscalls.ICRNL) != 0);
        }
    }

    public (ushort Rows, ushort Columns) WindowSize => _session?.WindowSize ?? ((ushort)24, (ushort)80);

    public int ForegroundGroup { get; set; }

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

            if (_length == MaxLine && (_canonical || !Compact()))
            {
                EmitFull();
            }

            _line[_length++] = b;
            if (b == (byte)'n' && IsStatusRequest())
            {
                AnswerStatusRequest();
            }
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

    /// <summary>true, wenn die Zeile gerade mit <c>ESC [ 6 n</c> endet (DSR, Frage nach der Cursorposition).</summary>
    private bool IsStatusRequest() =>
        _session != null && _length >= 4 && _line[_length - 4] == 0x1b && _line[_length - 3] == (byte)'[' && _line[_length - 2] == (byte)'6';

    /// <summary>
    /// Antwortet wie ein VT100 mit <c>ESC [ Zeile ; Spalte R</c> in die Eingabe (V3 raw): LibLine fragt so vor jeder Eingabezeile
    /// und liest, bis die Antwort kommt. Die angefangene Zeile steht im Fenster ganz unten, die Zeile ist deshalb die letzte; die
    /// Spalte ergibt sich aus dem, was das Programm bisher gezeichnet hat. Die Frage selbst wird nicht angezeigt.
    /// </summary>
    private void AnswerStatusRequest()
    {
        _length -= 4;
        TerminalLine.Render(Encoding.UTF8.GetString(_line, 0, _length), out int cursor);
        string answer = "\x1b[" + WindowSize.Rows + ";" + (cursor + 1) + "R";
        _session!.Respond(Encoding.ASCII.GetBytes(answer));
    }

    /// <summary>
    /// Rohmodus: Ein Zeileneditor zeichnet die Zeile bei jeder Taste neu und schreibt dabei lange kein Zeilenende. Statt die
    /// Neuzeichnungen als Zeile auszugeben, wird die volle Zeile durch ihren Stand ersetzt (Text, dann Cursor mit <c>ESC [ n G</c>).
    /// Eine angefangene Folge am Ende bleibt dahinter. false, wenn auch der Stand die Zeile füllt.
    /// </summary>
    private bool Compact()
    {
        int cut = IncompleteTail(_line, _length);
        int keep = cut < 0 ? _length : cut;
        string text = TerminalLine.Render(Encoding.UTF8.GetString(_line, 0, keep), out int cursor);
        byte[] state = Encoding.UTF8.GetBytes(text + "\x1b[" + (cursor + 1) + "G");
        int tail = _length - keep;
        if (state.Length + tail > MaxLine / 2)
        {
            return false;
        }

        Array.Copy(_line, keep, _line, state.Length, tail);
        state.CopyTo(_line, 0);
        _length = state.Length + tail;
        return true;
    }

    /// <summary>
    /// Gibt eine volle Zeile aus (V3-Review P3 b): Eine angefangene ESC-Folge oder ein angefangenes UTF-8-Zeichen am Ende wandert
    /// in die nächste Zeile, sonst bliebe vom Rest der Folge etwa "[31m" sichtbar. Ist die ganze Zeile eine einzige Folge, geht sie so.
    /// </summary>
    private void EmitFull()
    {
        int cut = IncompleteTail(_line, _length);
        if (cut <= 0)
        {
            Emit();
            return;
        }

        int rest = _length - cut;
        byte[] tail = new byte[rest];
        Array.Copy(_line, cut, tail, 0, rest);
        _length = cut;
        Emit();
        tail.CopyTo(_line, 0);
        _length = rest;
    }

    /// <summary>Anfang einer am Ende abgeschnittenen ESC-Folge oder UTF-8-Folge, sonst -1.</summary>
    internal static int IncompleteTail(byte[] line, int length)
    {
        // UTF-8: höchstens drei Folgebytes zurück bis zum Startbyte; reicht die Länge nicht, ist das Zeichen angefangen.
        for (int i = length - 1; i >= 0 && i >= length - 4; i--)
        {
            byte b = line[i];
            if ((b & 0xc0) == 0x80)
            {
                continue;
            }

            int need = b >= 0xf0 ? 4 : b >= 0xe0 ? 3 : b >= 0xc0 ? 2 : 1;
            if (length - i < need)
            {
                return i;
            }

            break;
        }

        // Letztes ESC suchen; ist seine Folge bis zum Ende nicht abgeschlossen, beginnt dort der Rest.
        int esc = Array.LastIndexOf(line, (byte)0x1b, length - 1, length);
        if (esc < 0)
        {
            return -1;
        }

        if (esc == length - 1)
        {
            return esc;
        }

        byte kind = line[esc + 1];
        if (kind == (byte)'[')
        {
            for (int i = esc + 2; i < length; i++)
            {
                if (line[i] < 0x20 || line[i] > 0x3f)
                {
                    return -1; // Endbyte (oder ein Zeichen, das die Folge abbricht) ist da.
                }
            }

            return esc;
        }

        if (kind == (byte)']')
        {
            for (int i = esc + 2; i < length; i++)
            {
                if (line[i] == 0x07)
                {
                    return -1;
                }
            }

            return esc;
        }

        return -1;
    }

    private void Emit()
    {
        string text = Encoding.UTF8.GetString(_line, 0, _length);
        // Im Rohmodus ist die Zeile das, was der Zeileneditor zuletzt gezeichnet hat (V3 raw), nicht die Folge seiner Neuzeichnungen.
        _output.WriteLine(_canonical ? Printable(text) : TerminalLine.Render(text, out _).TrimEnd(' '));
        _length = 0;
    }

    /// <summary>
    /// Was ein Zeilenterminal von einer Zeile zeigen kann (V3-Review P3 c): Die Terminals von ZehOS kennen keine Steuerfolgen und
    /// zeigten ESC und andere Steuerzeichen als Zeichen. ANSI-Folgen (CSI <c>ESC [ … Endbyte</c>, OSC <c>ESC ] … BEL|ST</c>, sonst
    /// ESC und ein Zeichen) fallen weg, Backspace löscht das Zeichen davor, ein <c>\r</c> mitten in der Zeile beginnt sie neu
    /// (Fortschrittsanzeigen), eines am Ende gehört zum Zeilenende. Tab bleibt, die übrigen Steuerzeichen fallen weg.
    /// Das Neubeginnen geschieht erst beim nächsten sichtbaren Zeichen (V3-Review P3 a): Nach "ab\r" nur noch Steuerfolgen
    /// (etwa "\x1b[0m") ist das <c>\r</c> noch Zeilenende, die Zeile bleibt "ab".
    /// </summary>
    internal static string Printable(string text)
    {
        var result = new StringBuilder(text.Length);
        bool restart = false;
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
                restart = true;
                continue;
            }

            if (c == '\b')
            {
                if (!restart && result.Length > 0)
                {
                    result.Length--;
                }

                continue;
            }

            if ((c < ' ' && c != '\t') || c == '\x7f' || (c >= '\x80' && c < '\xa0'))
            {
                continue;
            }

            if (restart)
            {
                result.Clear();
                restart = false;
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
