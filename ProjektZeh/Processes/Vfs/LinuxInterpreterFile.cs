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
/// (<c>LinuxUserProcess.ReadProgram</c>): nur regulaere Dateien mit einem Ausfuehrrecht, vom ladbaren Teil (<see cref="ElfFile.LoadedLength"/>)
/// hoechstens <see cref="LinuxUserProcess.MaxExecFileSize"/>. Ob der Inhalt ein brauchbares ELF ist, prueft danach der <see cref="ElfLoader"/>.
/// </remarks>
internal static class LinuxInterpreterFile
{
    /// <summary>
    /// Ladeoptionen des ersten Programms eines Laufs, dessen Interpreter aus <paramref name="store"/> kommt, mit den Rechten der
    /// Nutzerkennung genau dieser Optionen (die bekommt danach auch der Prozess).
    /// </summary>
    public static ElfLoadOptions LoadOptions(ILinuxFileStore store, byte[] random, ulong hwCap)
    {
        // Der Leser liest Uid/Gid der Optionen, in deren Initialisierer er steht; ein Lambda darf die eigene Variable dort nicht lesen
        // (CS0165), deshalb erst null. Der Lader ruft den Leser erst nach dem Initialisierer.
        ElfLoadOptions? options = null;
        options = new ElfLoadOptions
        {
            Random = random,
            HwCap = hwCap,
            ReadInterpreter = (string path, out byte[] content) => Read(store, path, (uint)options!.Uid, (uint)options.Gid, out content),
        };
        return options;
    }

    /// <summary>Liest den ladbaren Teil von <paramref name="path"/> wie <c>execve</c>; 0 oder -errno (ENOENT, EACCES, ENOMEM, EIO …).</summary>
    public static long Read(ILinuxFileStore store, string path, uint uid, uint gid, out byte[] content) =>
        Read(store, path, uid, gid, LinuxUserProcess.MaxExecFileSize, out content);

    /// <summary>Wie oben mit eigener Grenze fuer den ladbaren Teil (Tests ohne 128-MiB-Dateien).</summary>
    internal static long Read(ILinuxFileStore store, string path, uint uid, uint gid, long maxLoaded, out byte[] content)
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

            if (st.Size < 0)
            {
                return -LinuxSyscalls.ENOMEM;
            }

            // P3-1 (Review N3-E): wie execve (LinuxUserProcess.ReadProgram) zaehlt nur der ladbare Teil; Debugabschnitte dahinter liest
            // niemand, ein Interpreter mit grossen Symboltabellen startet sonst hier nicht, per execve aber schon.
            ILinuxFile f = file;
            long ReadAt(long offset, Span<byte> dst)
            {
                long at = f.Seek(offset, 0);
                return at < 0 ? at : f.Read(dst);
            }

            r = ElfFile.LoadedLength(st.Size, ReadAt, out long needed);
            if (r < 0)
            {
                return r;
            }

            if (needed > maxLoaded)
            {
                return -LinuxSyscalls.ENOMEM;
            }

            byte[] bytes = new byte[needed];
            r = ElfFile.ReadFull(ReadAt, 0, bytes);
            if (r < 0)
            {
                return r;
            }

            if (r < bytes.Length)
            {
                // Kuerzer als gemeldet: lieber kein Abbild als ein abgeschnittenes.
                return -LinuxErrno.EIO;
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
