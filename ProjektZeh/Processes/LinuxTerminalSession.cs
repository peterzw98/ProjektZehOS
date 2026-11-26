using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shell;

namespace ProjektZeh.Processes;

/// <summary>
/// Verbindet ein laufendes Linux-Programm mit einem Shellfenster, ohne dass einer auf den anderen wartet (Ladybird V3 stdin).
/// Programmseite (eigener Kernelthread): <see cref="WriteLine"/> stellt Ausgabezeilen ein, <see cref="ReadInput"/> holt
/// Eingabebytes. Shellseite (Ereignisschleife, über <see cref="IShellForeground"/>): <see cref="Drain"/> schreibt die Zeilen
/// ins Fenster, <see cref="Submit"/> legt Enter-Zeilen in die Eingabe. Warten gibt es nur auf der Programmseite, in Scheiben
/// von <see cref="Slice"/> ms mit Abbruchprüfung; die Ereignisschleife blockiert nie.
/// </summary>
internal sealed class LinuxTerminalSession : ITextOutput, IShellForeground
{
    /// <summary>Längste Wartezeit am Stück auf der Programmseite; danach prüft sie Abbruch und Abmeldung.</summary>
    public const int Slice = 50;

    /// <summary>So viele Zeilen warten höchstens auf die Ereignisschleife; dann wartet das Programm beim Schreiben (wie eine volle Pipe).</summary>
    public const int MaxQueuedLines = 2048;

    /// <summary>So viele Eingabebytes werden höchstens gepuffert; Zeilen darüber hinaus fallen weg (das Programm liest nicht).</summary>
    public const int MaxInputBytes = 64 << 10;

    /// <summary>Platz über <see cref="MaxInputBytes"/> nur für Antworten des Terminals (eine <c>ESC [ Zeile ; Spalte R</c> ist höchstens 16 Byte).</summary>
    public const int ResponseReserve = 64;

    private readonly object _gate = new();
    private readonly Action<int> _sleep;
    private readonly Queue<string> _lines = new();
    private readonly Queue<byte> _input = new();
    private byte[]? _prompt;
    private bool _endOfInput;
    private bool _interrupted;
    private bool _detached;
    private bool _finished;
    private string? _status;
    private bool _done;

    // Terminalmodus aus termios (V3 raw), gesetzt vom Programmthread über den Deskriptor, gelesen von der Ereignisschleife.
    private bool _raw;
    private bool _signalKeys = true;
    private bool _crToNewline = true;

    /// <param name="sleep">Schläft so viele Millisekunden (Kernel: Thread.Sleep; Tests: zählt oder liefert Eingabe nach).</param>
    public LinuxTerminalSession(Action<int> sleep)
    {
        _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
    }

    /// <summary>Liefert die Fenstergröße (Zeilen, Spalten) für TIOCGWINSZ; ohne sie 24 x 80. Wird vom Programmthread gerufen.</summary>
    public Func<(ushort Rows, ushort Columns)>? SizeSource { get; set; }

    /// <summary>Fenstergröße in Zeichen, nie 0.</summary>
    public (ushort Rows, ushort Columns) WindowSize
    {
        get
        {
            (ushort rows, ushort columns) = SizeSource?.Invoke() ?? ((ushort)24, (ushort)80);
            return (rows == 0 ? (ushort)24 : rows, columns == 0 ? (ushort)80 : columns);
        }
    }

    /// <summary>Wird bei <see cref="Interrupt"/> aufgerufen (auf der Ereignisschleife): verlässt den Ring 3 des Programms.</summary>
    public Action? OnInterrupt { get; set; }

    // ---- Programmseite ----

    /// <summary>
    /// Übernimmt den Modus aus termios (TCSETS*): <paramref name="canonical"/> (ICANON) heißt ganze Zeilen aus dem Zeileneditor
    /// der Shell, sonst Tasten einzeln (Rohmodus). <paramref name="signalKeys"/> ist ISIG (Strg+C bricht ab),
    /// <paramref name="crToNewline"/> ist ICRNL (Enter liefert \n statt \r). ECHO wird nicht gebraucht: Im kanonischen Modus zeigt die
    /// Shell die Zeile immer, im Rohmodus zeigt sie nur, was das Programm zeichnet (ein Rohmodus mit Echo ist nicht umgesetzt).
    /// </summary>
    public void SetMode(bool canonical, bool signalKeys, bool crToNewline)
    {
        lock (_gate)
        {
            _raw = !canonical;
            _signalKeys = signalKeys;
            _crToNewline = crToNewline;
        }
    }

    /// <summary>
    /// Bytes, die das Terminal selbst in die Eingabe legt (Antwort auf <c>ESC [ 6 n</c>), ohne Umwandlung. Sie dürfen
    /// <see cref="MaxInputBytes"/> um <see cref="ResponseReserve"/> überschreiten, damit ein Programm, das bei voller Eingabe nach
    /// der Cursorposition fragt, trotzdem eine Antwort bekommt. Darüber verwirft die Sitzung die Antwort wie ein volles Linux-tty:
    /// Ein Programm, das fragt und nie liest, darf den Kernelspeicher nicht füllen.
    /// </summary>
    public void Respond(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_detached || _input.Count + bytes.Length > MaxInputBytes + ResponseReserve)
            {
                return;
            }

            foreach (byte b in bytes)
            {
                _input.Enqueue(b);
            }
        }
    }

    /// <summary>Eine fertige Ausgabezeile (vom <see cref="TerminalDescriptor"/>). Wartet, solange die Warteschlange voll ist.</summary>
    public void WriteLine(string text)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_detached)
                {
                    return;
                }

                if (_lines.Count < MaxQueuedLines)
                {
                    _lines.Enqueue(text);
                    return;
                }

                // Nach dem Abbruch nicht mehr warten, damit das Programm den Aufruf schnell verlässt, aber die Grenze halten
                // (Issue #735 P2-2): Ein Aufruf bis 2 GiB füllte sonst den Kernelheap. Was die Shell nicht mehr anzeigt, fällt weg.
                if (_interrupted)
                {
                    return;
                }
            }

            _sleep(Slice);
        }
    }

    /// <summary>Ein Programm kann das Fenster nicht leeren; ohne Wirkung.</summary>
    public void Clear()
    {
    }

    /// <summary>
    /// Die angefangene Zeile, mit der das Programm auf Eingabe wartet (Rohbytes). Die Shell zeigt sie als Eingabeaufforderung;
    /// schreibt das Programm vorher weiter, holt der Deskriptor sie mit <see cref="TakePrompt"/> zurück.
    /// </summary>
    public void SetPrompt(byte[] raw)
    {
        lock (_gate)
        {
            _prompt = raw;
        }
    }

    /// <summary>Die noch nicht beantwortete Eingabeaufforderung zurück an den Deskriptor; null, wenn die Shell sie schon ausgab.</summary>
    public byte[]? TakePrompt()
    {
        lock (_gate)
        {
            byte[]? raw = _prompt;
            _prompt = null;
            return raw;
        }
    }

    /// <summary>
    /// Liest wie von einer Pipe: wartet, bis Bytes da sind, liefert 0 einmal je Strg+D (bei leerer Eingabe) und -EINTR nach dem
    /// Abbruch oder der Abmeldung.
    /// </summary>
    public long ReadInput(Span<byte> dst)
    {
        if (dst.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            lock (_gate)
            {
                if (_interrupted || _detached)
                {
                    return -LinuxSyscalls.EINTR;
                }

                if (_input.Count > 0)
                {
                    int n = 0;
                    while (n < dst.Length && _input.Count > 0)
                    {
                        dst[n++] = _input.Dequeue();
                    }

                    return n;
                }

                if (_endOfInput)
                {
                    // Wie Strg+D an einem Terminal: einmal Ende, danach liest das Programm wieder Zeilen.
                    _endOfInput = false;
                    return 0;
                }
            }

            _sleep(Slice);
        }
    }

    public bool InputReady
    {
        get
        {
            lock (_gate)
            {
                return _input.Count > 0 || _endOfInput || _interrupted || _detached;
            }
        }
    }

    /// <summary>Wartet in Scheiben auf <see cref="InputReady"/>; <paramref name="timeoutMs"/> negativ heißt ohne Frist.</summary>
    public bool WaitInput(long timeoutMs)
    {
        long waited = 0;
        while (!InputReady)
        {
            if (timeoutMs >= 0 && waited >= timeoutMs)
            {
                return false;
            }

            int step = timeoutMs < 0 ? Slice : (int)Math.Min(Slice, timeoutMs - waited);
            _sleep(step);
            waited += step;
        }

        return true;
    }

    /// <summary>Das Programm ist beendet; <paramref name="status"/> erscheint nach der letzten Ausgabezeile.</summary>
    public void Finish(string status)
    {
        lock (_gate)
        {
            _finished = true;
            _status = status;
        }
    }

    /// <summary>true nach Strg+C oder Esc (das Programm fragt das an Stellen ohne eigene Abbruchprüfung ab).</summary>
    public bool IsInterrupted
    {
        get
        {
            lock (_gate)
            {
                return _interrupted && !_finished;
            }
        }
    }

    // ---- Shellseite (IShellForeground) ----

    public string Prompt
    {
        get
        {
            lock (_gate)
            {
                if (_prompt == null)
                {
                    return string.Empty;
                }

                string text = Encoding.UTF8.GetString(_prompt);
                return _raw ? TerminalLine.Render(text, out _) : TerminalDescriptor.Printable(text);
            }
        }
    }

    public bool RawInput
    {
        get
        {
            lock (_gate)
            {
                return _raw;
            }
        }
    }

    public int PromptCursor
    {
        get
        {
            lock (_gate)
            {
                if (!_raw)
                {
                    return -1;
                }

                if (_prompt == null)
                {
                    return 0;
                }

                TerminalLine.Render(Encoding.UTF8.GetString(_prompt), out int cursor);
                return cursor;
            }
        }
    }

    public bool SignalKeys
    {
        get
        {
            lock (_gate)
            {
                return _signalKeys;
            }
        }
    }

    public void SubmitRaw(byte[] bytes)
    {
        lock (_gate)
        {
            // Die Eingabezeile bleibt stehen: Im Rohmodus zeichnet das Programm sie nach der Taste selbst neu.
            if (_finished || _input.Count + bytes.Length > MaxInputBytes)
            {
                return;
            }

            foreach (byte b in bytes)
            {
                _input.Enqueue(b == (byte)'\r' && _crToNewline ? (byte)'\n' : b);
            }
        }
    }

    public void Submit(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_gate)
        {
            // Die Shell hat die Aufforderung mit der Zeile ausgegeben; sie gehört nicht mehr zur nächsten Ausgabezeile.
            _prompt = null;
            if (_finished || _input.Count + bytes.Length > MaxInputBytes)
            {
                return;
            }

            foreach (byte b in bytes)
            {
                _input.Enqueue(b);
            }
        }
    }

    public void EndInput()
    {
        lock (_gate)
        {
            _endOfInput = true;
        }
    }

    public void Interrupt()
    {
        Action? interrupt;
        lock (_gate)
        {
            if (_finished || _interrupted)
            {
                return;
            }

            _interrupted = true;
            interrupt = OnInterrupt;
        }

        // Außerhalb der Sperre: der Rückruf greift auf den Programmthread zu (UserMode.RequestLeave).
        interrupt?.Invoke();
    }

    public void Detach()
    {
        lock (_gate)
        {
            _detached = true;
            _lines.Clear();
            _prompt = null;
        }
    }

    public bool Drain(ITextOutput output)
    {
        bool written = false;
        while (true)
        {
            string? line;
            lock (_gate)
            {
                if (_lines.Count > 0)
                {
                    line = _lines.Dequeue();
                }
                else if (_finished && !_done)
                {
                    // Erst nach der letzten Zeile: sonst stünde der Status vor Ausgaben, die noch in der Warteschlange waren.
                    _done = true;
                    line = _status;
                }
                else
                {
                    return written;
                }
            }

            // Außerhalb der Sperre schreiben: das Fenster kann umbrechen und neu zeichnen, das Programm soll dabei weiterlaufen.
            if (line != null)
            {
                output.WriteLine(line);
                written = true;
            }
        }
    }

    public bool IsDone
    {
        get
        {
            lock (_gate)
            {
                return _done;
            }
        }
    }
}
