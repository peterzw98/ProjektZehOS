using System;
using System.Text;

namespace ProjektZeh.Processes;

/// <summary>
/// Die angefangene Zeile eines Programms im Rohmodus (Ladybird V3 raw), wie ein VT100 sie zeigen würde. Zeileneditoren wie
/// LibLine zeichnen die Eingabezeile nach jeder Taste neu: <c>\r</c>, Text, <c>ESC [ K</c> und Cursorbewegungen. Die Fenster von
/// ZehOS haben nur fertige Zeilen und eine Eingabezeile; deshalb wird hier nur diese eine Zeile ausgewertet: Zeichen überschreiben
/// an der Cursorspalte, <c>\r</c>, Backspace, Tab, CSI <c>K J C D G H f s u</c> und <c>ESC 7</c>/<c>ESC 8</c>. Zeilenwechsel
/// (<c>A</c>, <c>B</c>, die Zeile von <c>H</c>), Farben und private Folgen (<c>ESC [ ? …</c>) fallen weg.
/// </summary>
internal static class TerminalLine
{
    /// <summary>Weiter rechts setzt keine Folge den Cursor (ein <c>ESC [ 99999 C</c> soll keine riesige Zeile auffüllen).</summary>
    public const int MaxColumn = 4096;

    /// <summary>Wertet die Zeile aus; <paramref name="cursor"/> ist die Spalte des Cursors (0 ganz links), auch hinter dem Text.</summary>
    public static string Render(string text, out int cursor)
    {
        var cells = new StringBuilder(text.Length);
        cursor = 0;
        int saved = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\x1b')
            {
                if (i + 1 >= text.Length)
                {
                    break; // angefangene Folge: kommt mit dem nächsten write
                }

                char kind = text[i + 1];
                if (kind == '[')
                {
                    int end = i + 2;
                    while (end < text.Length && text[end] >= ' ' && text[end] <= '?')
                    {
                        end++;
                    }

                    if (end >= text.Length)
                    {
                        break;
                    }

                    Csi(cells, ref cursor, ref saved, text.AsSpan(i + 2, end - i - 2), text[end]);
                    i = end;
                }
                else if (kind == ']')
                {
                    // OSC (Fenstertitel): bis BEL oder ESC \, ohne Wirkung auf die Zeile.
                    int end = i + 2;
                    while (end < text.Length && text[end] != '\a' && !(text[end] == '\x1b' && end + 1 < text.Length && text[end + 1] == '\\'))
                    {
                        end++;
                    }

                    i = end < text.Length && text[end] == '\x1b' ? end + 1 : end;
                }
                else
                {
                    if (kind == '7')
                    {
                        saved = cursor;
                    }
                    else if (kind == '8')
                    {
                        cursor = saved;
                    }

                    i++;
                }

                continue;
            }

            switch (c)
            {
                case '\r':
                    cursor = 0;
                    continue;
                case '\b':
                    cursor = Math.Max(0, cursor - 1);
                    continue;
                case '\t':
                    cursor = Math.Min(MaxColumn, (cursor / 8 + 1) * 8);
                    continue;
            }

            if (c < ' ' || c == '\x7f' || (c >= '\x80' && c < '\xa0'))
            {
                continue;
            }

            Put(cells, ref cursor, c);
        }

        return cells.ToString();
    }

    private static void Put(StringBuilder cells, ref int cursor, char c)
    {
        if (cursor >= MaxColumn)
        {
            return;
        }

        if (cursor > cells.Length)
        {
            cells.Append(' ', cursor - cells.Length);
        }

        if (cursor == cells.Length)
        {
            cells.Append(c);
        }
        else
        {
            cells[cursor] = c;
        }

        cursor++;
    }

    private static void Csi(StringBuilder cells, ref int cursor, ref int saved, ReadOnlySpan<char> parameters, char final)
    {
        if (parameters.Length > 0 && parameters[0] is '?' or '>' or '<' or '=')
        {
            return; // private Folgen (Cursor sichtbar, eingeklammertes Einfügen …)
        }

        int first = Parameter(parameters, 0, 0);
        switch (final)
        {
            case 'K':
            case 'J':
                // Nur diese Zeile: 0 bis zum Ende, 1 bis zum Cursor, 2 (und J 3) alles.
                if (first == 0)
                {
                    cells.Length = Math.Min(cells.Length, cursor);
                }
                else if (first == 1)
                {
                    for (int k = 0; k < Math.Min(cells.Length, cursor + 1); k++)
                    {
                        cells[k] = ' ';
                    }
                }
                else
                {
                    cells.Clear();
                }

                break;
            case 'C':
                cursor = Math.Min(MaxColumn, cursor + Math.Max(1, first));
                break;
            case 'D':
                cursor = Math.Max(0, cursor - Math.Max(1, first));
                break;
            case 'G':
                cursor = Math.Min(MaxColumn, Math.Max(1, first) - 1);
                break;
            case 'H':
            case 'f':
                // Zeile, Spalte: nur die Spalte zählt (es gibt nur diese eine Zeile).
                cursor = Math.Min(MaxColumn, Math.Max(1, Parameter(parameters, 1, 1)) - 1);
                break;
            case 's':
                saved = cursor;
                break;
            case 'u':
                cursor = saved;
                break;
        }
    }

    /// <summary>Der <paramref name="index"/>-te Zahlenparameter (durch <c>;</c> getrennt); leer oder fehlend: <paramref name="fallback"/>.</summary>
    private static int Parameter(ReadOnlySpan<char> parameters, int index, int fallback)
    {
        int at = 0;
        for (int n = 0; n < index; n++)
        {
            int sep = parameters.Slice(at).IndexOf(';');
            if (sep < 0)
            {
                return fallback;
            }

            at += sep + 1;
        }

        int value = 0;
        bool any = false;
        for (int k = at; k < parameters.Length && parameters[k] is >= '0' and <= '9'; k++)
        {
            value = Math.Min(MaxColumn * 10, value * 10 + (parameters[k] - '0'));
            any = true;
        }

        return any ? value : fallback;
    }
}
