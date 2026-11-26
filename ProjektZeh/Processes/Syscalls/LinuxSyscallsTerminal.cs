using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Ein Deskriptor, der ein Terminal ist (Ladybird V3 termios): <c>isatty</c> ist wahr, musl puffert stdout zeilenweise, REPLs
/// erkennen ein Terminal. Der Zustand gehört zum Terminal, nicht zum Deskriptor: fd 0 bis 2 teilen ihn.
/// Die Einstellungen werden nur gespeichert. Das Terminal arbeitet immer kanonisch mit Echo (die Shell zeigt und editiert die
/// Eingabezeile); ein Programm, das den Rohmodus setzt, liest ihn zurück, bekommt aber weiter ganze Zeilen.
/// </summary>
internal interface ILinuxTerminal : ILinuxDescriptor
{
    /// <summary>false, wenn der Deskriptor zwar Zeilen ausgibt, aber an keinem Fenster hängt (serielle Prüfläufe): dann -ENOTTY.</summary>
    bool IsTerminal { get; }

    /// <summary>Die Kernelstruktur <c>struct termios</c> (<see cref="LinuxSyscalls.TermiosSize"/> Bytes), wie TCGETS sie liefert.</summary>
    byte[] Termios { get; set; }

    /// <summary>Fenstergröße in Zeichen (Zeilen, Spalten); nie 0.</summary>
    (ushort Rows, ushort Columns) WindowSize { get; }

    /// <summary>Vordergrund-Prozessgruppe; 0, solange niemand sie gesetzt hat (dann gilt der Prozess selbst).</summary>
    int ForegroundGroup { get; set; }
}

internal static partial class LinuxSyscalls
{
    // asm-generic/ioctls.h (x86-64)
    public const ulong TCGETS = 0x5401, TCSETS = 0x5402, TCSETSW = 0x5403, TCSETSF = 0x5404, TIOCGPGRP = 0x540F,
        TIOCSPGRP = 0x5410, TIOCGWINSZ = 0x5413, TIOCSWINSZ = 0x5414;

    public const long ESRCH = 3;

    /// <summary>Größe von <c>struct termios</c> im Kernel: vier tcflag_t, c_line, 19 c_cc (musl hängt danach noch Geschwindigkeiten an, die der Kernel nicht kennt).</summary>
    public const int TermiosSize = 36;

    /// <summary>
    /// Was ein frisches Linux-Terminal meldet (<c>stty -a</c> nach dem Öffnen): ICRNL IXON IUTF8, OPOST ONLCR, B38400 CS8 CREAD HUPCL,
    /// ISIG ICANON ECHO ECHOE ECHOK ECHOCTL ECHOKE IEXTEN; Steuerzeichen ^C ^\ ^? ^U ^D, VMIN 1.
    /// </summary>
    public static byte[] DefaultTermios()
    {
        var t = new byte[TermiosSize];
        PutU32(t, 0, 0x4500);
        PutU32(t, 4, 0x0005);
        PutU32(t, 8, 0x04bf);
        PutU32(t, 12, 0x8a3b);
        // t[16] ist c_line (N_TTY = 0), c_cc beginnt bei 17.
        byte[] cc = { 0x03, 0x1c, 0x7f, 0x15, 0x04, 0x00, 0x01, 0x00, 0x11, 0x13, 0x1a, 0x00, 0x12, 0x0f, 0x17, 0x16, 0x00 };
        cc.CopyTo(t, 17);
        return t;
    }

    private static void PutU32(byte[] b, int at, uint value)
    {
        b[at] = (byte)value;
        b[at + 1] = (byte)(value >> 8);
        b[at + 2] = (byte)(value >> 16);
        b[at + 3] = (byte)(value >> 24);
    }

    private static long DoIoctl(ILinuxProcess p, int fd, ulong request, ulong arg)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not ILinuxTerminal tty || !tty.IsTerminal)
        {
            return -ENOTTY;
        }

        switch (request)
        {
            case TCGETS:
                return p.Memory.TryWrite(arg, tty.Termios) ? 0 : -EFAULT;
            case TCSETS:
            case TCSETSW:
            case TCSETSF:
            {
                // Nur speichern (Rohmodus wird nicht umgesetzt, siehe ILinuxTerminal). TCSETSW wartet bei Linux auf die Ausgabe,
                // TCSETSF verwirft zusätzlich ungelesene Eingabe; hier ist die Ausgabe nach write schon beim Terminal, und
                // getippte Zeilen bleiben erhalten (Grenze in docs/LINUX_ABI.md).
                var t = new byte[TermiosSize];
                if (!p.Memory.TryRead(arg, t))
                {
                    return -EFAULT;
                }

                tty.Termios = t;
                return 0;
            }

            case TIOCGWINSZ:
            {
                (ushort rows, ushort columns) = tty.WindowSize;
                var w = new byte[8];
                w[0] = (byte)rows;
                w[1] = (byte)(rows >> 8);
                w[2] = (byte)columns;
                w[3] = (byte)(columns >> 8);
                // ws_xpixel, ws_ypixel bleiben 0 wie bei den meisten Terminals.
                return p.Memory.TryWrite(arg, w) ? 0 : -EFAULT;
            }

            case TIOCSWINSZ:
                // Die Größe bestimmt das Fenster; ein Programm kann sie nicht ändern. Linux nimmt den Aufruf an, hier ohne Wirkung.
                return p.Memory.TryRead(arg, new byte[8]) ? 0 : -EFAULT;
            case TIOCGPGRP:
            {
                int group = tty.ForegroundGroup != 0 ? tty.ForegroundGroup : p.Pid;
                var g = BitConverter.GetBytes(group);
                return p.Memory.TryWrite(arg, g) ? 0 : -EFAULT;
            }

            case TIOCSPGRP:
            {
                var g = new byte[4];
                if (!p.Memory.TryRead(arg, g))
                {
                    return -EFAULT;
                }

                // Es gibt nur den einen Prozess und seine Gruppe; eine andere Gruppe existiert nicht (Linux: ESRCH), negativ ist EINVAL.
                int group = BitConverter.ToInt32(g, 0);
                if (group < 0)
                {
                    return -EINVAL;
                }

                if (group != p.Pid)
                {
                    return -ESRCH;
                }

                tty.ForegroundGroup = group;
                return 0;
            }

            default:
                // Linux meldet für unbekannte Terminal-ioctls ENOTTY ("passt nicht zu diesem Gerät"), nicht EINVAL.
                return -ENOTTY;
        }
    }
}
