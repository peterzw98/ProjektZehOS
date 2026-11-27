using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Ein Deskriptor, der ein Terminal ist (Ladybird V3 termios): <c>isatty</c> ist wahr, musl puffert stdout zeilenweise, REPLs
/// erkennen ein Terminal. Der Zustand gehört zum Terminal, nicht zum Deskriptor: fd 0 bis 2 teilen ihn.
/// Von den Einstellungen wirken ICANON, ISIG und ICRNL (V3 raw): kanonisch zeigt und editiert die Shell die Eingabezeile und
/// liefert ganze Zeilen; ohne ICANON (Rohmodus, Zeileneditoren wie LibLine) geht jede Taste sofort als Bytes an das Programm, die
/// Shell zeigt nur, was das Programm zeichnet. Alles andere (VMIN/VTIME, Echo im Rohmodus, OPOST …) wird nur gespeichert.
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

    // V33: FIONREAD ist dieselbe Nummer wie TIOCINQ und SIOCINQ; FIONBIO setzt oder löscht O_NONBLOCK.
    public const ulong FIONREAD = 0x541B, FIONBIO = 0x5421;

    public const long ESRCH = 3;

    // asm-generic/termbits.h: c_iflag ICRNL, c_lflag ISIG und ICANON
    public const uint ICRNL = 0x100, ISIG = 0x1, ICANON = 0x2;

    public const long Setpgid = 109, Getpgrp = 111, Setsid = 112, Getpgid = 121, Getsid = 124;

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

        if (request == FIONBIO)
        {
            return DoIoctlNonBlocking(p, d, arg);
        }

        if (request == FIONREAD && ReadableBytes(d) is long readable)
        {
            if (readable < 0)
            {
                return readable;
            }

            return p.Memory.TryWrite(arg, BitConverter.GetBytes((int)readable)) ? 0 : -EFAULT;
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
                // ICANON, ISIG, ICRNL wirken (siehe ILinuxTerminal), der Rest wird gespeichert. TCSETSW wartet bei Linux auf die Ausgabe,
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

    /// <summary>
    /// ioctl FIONBIO (V33): Linux behandelt es für jede Datei vor dem Treiber (do_vfs_ioctl) und setzt O_NONBLOCK der offenen Datei wie
    /// F_SETFL, gesteuert vom int an <paramref name="arg"/> (ungleich 0 setzt). LibCore schaltet so seine IPC-Sockets um. Wirkung haben nur
    /// Deskriptoren mit Statusflags (Pipe, Socket, eventfd, timerfd, inotify); auf den übrigen wird es wie F_SETFL still angenommen.
    /// </summary>
    private static long DoIoctlNonBlocking(ILinuxProcess p, ILinuxDescriptor d, ulong arg)
    {
        var v = new byte[4];
        if (!p.Memory.TryRead(arg, v))
        {
            return -EFAULT;
        }

        if (d is ILinuxStatusFlags flagged)
        {
            flagged.NonBlocking = BitConverter.ToInt32(v, 0) != 0;
        }

        return 0;
    }

    /// <summary>
    /// ioctl FIONREAD (V33): ungelesene Bytes für Pipe-Enden (beide, wie Linux' pipe_ioctl), Sockets und inotify; null, wenn der
    /// Deskriptor es nicht kennt. eventfd und timerfd kennen es bei Linux nicht (-ENOTTY), Terminals und Dateien hier noch nicht.
    /// </summary>
    private static long? ReadableBytes(ILinuxDescriptor d) => d switch
    {
        LinuxPipeEnd pipe => pipe.Pipe.Buffered,
        LinuxUnixSocket socket => socket.ReadableBytes(),
        LinuxInotify inotify => inotify.PendingBytes,
        _ => null,
    };

    /// <summary>
    /// Prozessgruppe und Sitzung (V3 raw): Ein Programm aus <c>run</c> ist allein in seiner Gruppe und Sitzung und führt beide
    /// (Gruppe und Sitzung tragen seine PID), wie ein Programm, das Linux direkt in einem neuen Pseudoterminal startet. Andere
    /// Prozesse gibt es nicht (-ESRCH). Wie bei Linux scheitert <c>setsid</c> für einen Gruppenführer und <c>setpgid</c> für einen
    /// Sitzungsführer mit -EPERM, auch wenn sich nichts ändern würde (Linux prüft den Sitzungsführer vor allem anderen).
    /// V35: Ein Prozess mit <see cref="ILinuxProcessGroups"/> (Kinder per fork, vfork) hat echte Gruppen und Sitzungen.
    /// </summary>
    private static long DoProcessGroup(ILinuxProcess p, ref SyscallArgs a)
    {
        int pid = (int)a.A0;
        if (p is ILinuxProcessGroups groups)
        {
            return a.Number switch
            {
                Getpgrp => groups.ProcessGroup,
                Getpgid => groups.GetProcessGroup(pid),
                Getsid => groups.GetSession(pid),
                Setpgid => groups.SetProcessGroup(pid, (int)a.A1),
                _ => groups.CreateSession(), // Setsid
            };
        }

        switch (a.Number)
        {
            case Getpgrp:
                return p.Pid;
            case Getpgid:
            case Getsid:
                // Linux sucht auch eine negative PID nur nach und findet sie nicht: ESRCH (EINVAL gibt es nur für setpgid mit pgid < 0).
                return pid == 0 || pid == p.Pid ? p.Pid : -ESRCH;
            case Setpgid:
            {
                if ((int)a.A1 < 0)
                {
                    return -EINVAL;
                }

                // Der Prozess ist Sitzungsführer: Linux lehnt jede Änderung seiner Gruppe ab.
                return pid != 0 && pid != p.Pid ? -ESRCH : -EPERM;
            }

            default: // Setsid
                return -EPERM;
        }
    }
}
