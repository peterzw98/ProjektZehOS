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

    /// <param name="sleep">Schläft so viele Millisekunden (Kernel: Thread.Sleep; Tests: zählt oder liefert Eingabe nach).</param>
    public LinuxTerminalSession(Action<int> sleep)
    {
        _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
    }

    /// <summary>Wird bei <see cref="Interrupt"/> aufgerufen (auf der Ereignisschleife): verlässt den Ring 3 des Programms.</summary>
    public Action? OnInterrupt { get; set; }

    // ---- Programmseite ----

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

                // Nach dem Abbruch nicht mehr warten: das Programm soll den Aufruf schnell verlassen.
                if (_lines.Count < MaxQueuedLines || _interrupted)
                {
                    _lines.Enqueue(text);
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
                return _prompt == null ? string.Empty : TerminalDescriptor.Printable(Encoding.UTF8.GetString(_prompt));
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
