using System;
using System.Text;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shell;

namespace ProjektZeh.Processes;

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

    public TerminalDescriptor(ITextOutput output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public long Write(ReadOnlySpan<byte> data)
    {
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
        // Ein \r vor dem Zeilenende gehört zum Terminalprotokoll, nicht zum Text.
        int length = _length > 0 && _line[_length - 1] == (byte)'\r' ? _length - 1 : _length;
        _output.WriteLine(Encoding.UTF8.GetString(_line, 0, length));
        _length = 0;
    }
}
