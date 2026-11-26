using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Metadaten einer Datei in Linux-Begriffen; der Verteiler schreibt daraus struct stat (x86-64, 144 Bytes).
/// Zeiten in Nanosekunden seit 1970.
/// </summary>
internal struct LinuxStat
{
    public ulong Dev { get; set; }
    public ulong Ino { get; set; }
    public uint Mode { get; set; } // S_IF* | Rechte
    public uint Nlink { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public ulong Rdev { get; set; }
    public long Size { get; set; }
    public long BlockSize { get; set; }
    public long Blocks { get; set; }
    public long AtimeNanos { get; set; }
    public long MtimeNanos { get; set; }
    public long CtimeNanos { get; set; }
}

/// <summary>Ein Verzeichniseintrag für getdents64.</summary>
internal readonly struct LinuxDirEntry(ulong ino, byte type, string name)
{
    public ulong Ino { get; } = ino;
    public byte Type { get; } = type; // DT_*
    public string Name { get; } = name;
}

/// <summary>
/// Ein offener Deskriptor auf eine Datei oder ein Verzeichnis. Erweitert <see cref="ILinuxDescriptor"/>, damit vorhandene
/// Deskriptoren (Terminal) unverändert bleiben; der Verteiler prüft mit <c>is ILinuxFile</c>.
/// Alle Methoden liefern -errno statt zu werfen.
/// </summary>
internal interface ILinuxFile : ILinuxDescriptor
{
    /// <summary>Absoluter, normalisierter Pfad (für openat mit Verzeichnis-fd und fchdir).</summary>
    string Path { get; }

    bool IsDirectory { get; }

    /// <summary>Liest ab der aktuellen Position und rückt sie vor; 0 am Ende, -EISDIR bei Verzeichnissen.</summary>
    long Read(Span<byte> dst);

    /// <summary>Setzt die Position (whence 0/1/2); liefert die neue Position oder -errno.</summary>
    long Seek(long offset, int whence);

    long Stat(out LinuxStat st);

    /// <summary>
    /// Liefert den Verzeichniseintrag an Position <paramref name="index"/> (fortlaufend ab 0, inklusive "." und "..");
    /// 0 bei Erfolg, 1 am Ende, -ENOTDIR oder andere -errno. Die Position verwaltet der Verteiler (getdents64).
    /// </summary>
    long ReadDir(long index, out LinuxDirEntry entry);

    /// <summary>Position des nächsten getdents64-Eintrags; gehört zum offenen Deskriptor wie bei Linux.</summary>
    long DirIndex { get; set; }

    /// <summary>
    /// Gibt Puffer frei; der Verteiler ruft das beim letzten close. 0 oder -errno (etwa -EIO beim Zurückschreiben); die Datei ist danach
    /// in jedem Fall zu, wie <c>close</c> unter Linux.
    /// </summary>
    long Close();
}

/// <summary>
/// Pfadbasierte Dateioperationen. Pfade sind immer absolut und normalisiert (kein ".", "..", doppelter "/");
/// das erledigt der Verteiler mit dem Arbeitsverzeichnis. Symbolische Links löst die Implementierung selbst auf
/// (<paramref name="follow"/> nur für die letzte Komponente). Rückgabe 0 oder -errno.
/// </summary>
internal interface ILinuxFileSystem
{
    /// <summary>open/openat: <paramref name="flags"/> und <paramref name="mode"/> wie Linux (O_*); nicht unterstützte Flags -EINVAL.</summary>
    long Open(string path, int flags, uint mode, out ILinuxFile? file);

    long Stat(string path, bool follow, out LinuxStat st);

    /// <summary>access/faccessat: <paramref name="mode"/> ist F_OK oder eine Kombination aus R_OK, W_OK, X_OK.</summary>
    long Access(string path, int mode);

    long ReadLink(string path, out string target);

    long Mkdir(string path, uint mode);

    long Unlink(string path);

    long Rmdir(string path);

    long Rename(string oldPath, string newPath);

    long Symlink(string target, string linkPath);
}

/// <summary>
/// Optionale Dateifähigkeit eines Prozesses (wie <see cref="ILinuxHost"/>): Ohne sie liefern alle Dateiaufrufe -ENOSYS.
/// Die Deskriptortabelle bleibt beim Prozess; <see cref="ILinuxProcess.GetDescriptor"/> liefert auch Dateien.
/// </summary>
internal interface ILinuxFiles
{
    ILinuxFileSystem FileSystem { get; }

    /// <summary>Arbeitsverzeichnis, absolut und normalisiert; Start "/".</summary>
    string Cwd { get; set; }

    /// <summary>Trägt den Deskriptor unter der kleinsten freien Nummer ab <paramref name="min"/> ein; -EMFILE, wenn voll.</summary>
    int AddDescriptor(ILinuxDescriptor d, int min);

    /// <summary>Entfernt die Nummer aus der Tabelle; false, wenn sie nicht belegt war.</summary>
    bool RemoveDescriptor(int fd);
}
