using System;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// N3-E: liest den Interpreter (<c>PT_INTERP</c>) des ersten Programms eines Laufs aus dessen Prozesswurzel, bevor es den Prozess gibt.
/// Bei <c>run --pkg</c> ist das die gebundene Paketwurzel (<see cref="PackageRootStore"/>): derselbe Indexstand, aus dem das Programm
/// kommt, mit derselben Inhaltspruefung und auch nach einem Upgrade waehrend des Laufs.
/// </summary>
/// <remarks>
/// Gelesen wird ueber ein eigenes <see cref="LinuxFileSystem"/> auf demselben Speicher, damit Verweise (etwa
/// <c>/lib/ld-musl-x86_64.so.1</c> als Link) und Rechte genau so gelten wie bei einem spaeteren <c>execve</c> des Prozesses
/// (<c>LinuxUserProcess.ReadProgram</c>): nur regulaere Dateien mit einem Ausfuehrrecht, hoechstens
/// <see cref="LinuxUserProcess.MaxExecFileSize"/>. Ob der Inhalt ein brauchbares ELF ist, prueft danach der <see cref="ElfLoader"/>.
/// </remarks>
internal static class LinuxInterpreterFile
{
    /// <summary>Liest <paramref name="path"/> ganz; 0 oder -errno (ENOENT, EACCES, ENOMEM, EIO …).</summary>
    public static long Read(ILinuxFileStore store, string path, uint uid, uint gid, out byte[] content)
    {
        content = [];
        // Das Dateisystem dient nur diesem einen Lesevorgang; sein Terminal schreibt nichts (niemand hat hier einen Deskriptor).
        var files = new LinuxFileSystem(store, NoTerminal.Instance, uid: uid, gid: gid);
        long r = files.Open(path, LinuxFileSystem.ORdOnly, 0, out ILinuxFile? file);
        if (r < 0 || file == null)
        {
            return r < 0 ? r : -LinuxErrno.ENOENT;
        }

        try
        {
            r = file.Stat(out LinuxStat st);
            if (r < 0)
            {
                return r;
            }

            if ((st.Mode & LinuxMode.TypeMask) != LinuxMode.Regular || (st.Mode & 0x49) == 0)
            {
                return -LinuxErrno.EACCES;
            }

            if (st.Size < 0 || st.Size > LinuxUserProcess.MaxExecFileSize)
            {
                return -LinuxSyscalls.ENOMEM;
            }

            byte[] bytes = new byte[st.Size];
            for (long at = 0; at < bytes.Length;)
            {
                long read = file.Read(bytes.AsSpan((int)at));
                if (read < 0)
                {
                    return read;
                }

                if (read == 0)
                {
                    // Kuerzer als gemeldet: lieber kein Abbild als ein abgeschnittenes.
                    return -LinuxErrno.EIO;
                }

                at += read;
            }

            content = bytes;
            return 0;
        }
        finally
        {
            _ = file.Close();
        }
    }

    private sealed class NoTerminal : ILinuxDescriptor
    {
        public static readonly NoTerminal Instance = new();

        public long Write(ReadOnlySpan<byte> data) => data.Length;
    }
}
