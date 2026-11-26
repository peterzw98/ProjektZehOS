using System;

namespace ProjektZeh.Processes.Vfs;

/// <summary>Art eines Eintrags im Speicher hinter der Prozesswurzel, abgefragt OHNE Folgen eines Symlinks (lstat).</summary>
internal enum LinuxNodeKind
{
    Missing,
    File,
    Directory,
    Symlink,
}

/// <summary>Metadaten eines Speichereintrags. <see cref="Mode"/> sind nur die Rechtebits (0777), der Typ steht in <see cref="Kind"/>.</summary>
internal readonly record struct LinuxStoreNode(LinuxNodeKind Kind, long Size, uint Mode, long MtimeNanos);

/// <summary>
/// Speicher, auf dem <see cref="LinuxFileSystem"/> die Prozesswurzel abbildet (Ordner der Datenplatte oder Arbeitsspeicher).
/// Pfade sind relativ zur Wurzel, mit '/' getrennt, ohne leere, "."- oder ".."-Teile; "" ist die Wurzel selbst. Der Speicher folgt
/// nie einem Symlink: Auflösung, Rechte und Typprüfungen macht das Dateisystem darüber, Komponente für Komponente. Rückgabe 0
/// oder -errno (<see cref="LinuxErrno"/>), keine Ausnahmen bei erwarteten Fehlern.
/// </summary>
internal interface ILinuxFileStore
{
    /// <summary>Vergleich der Namen, wie der Speicher sie unterscheidet (FAT ohne Groß-/Kleinschreibung).</summary>
    StringComparison NameComparison { get; }

    /// <summary>Eintrag ohne Folgen; ein fehlender Eintrag ist Erfolg mit <see cref="LinuxNodeKind.Missing"/>.</summary>
    long Lookup(string path, out LinuxStoreNode node);

    long ReadLink(string path, out string target);

    /// <summary>Namen der Einträge eines Verzeichnisses (ohne "." und ".."), ordinal sortiert.</summary>
    long List(string path, out string[] names);

    /// <summary>Öffnet eine vorhandene reguläre Datei.</summary>
    long OpenFile(string path, bool write, out ILinuxStoreFile? file);

    /// <summary>Legt eine neue reguläre Datei an (-EEXIST, wenn der Name belegt ist) und öffnet sie lesend und schreibend.</summary>
    long CreateFile(string path, uint mode, out ILinuxStoreFile? file);

    long CreateDirectory(string path, uint mode);

    long CreateSymlink(string path, string target);

    /// <summary>Entfernt eine Datei oder einen Symlink.</summary>
    long DeleteFile(string path);

    /// <summary>Entfernt ein leeres Verzeichnis.</summary>
    long DeleteDirectory(string path);

    /// <summary>
    /// Benennt um. Ein vorhandenes Ziel ersetzt der Speicher; das Dateisystem hat vorher geprüft, dass die Typen passen und ein
    /// Zielverzeichnis leer ist.
    /// </summary>
    long Rename(string from, string to);
}

/// <summary>Eine offene reguläre Datei des Speichers; Positionen verwaltet das Dateisystem.</summary>
internal interface ILinuxStoreFile : IDisposable
{
    /// <summary>Länge in Bytes oder -errno.</summary>
    long Length();

    /// <summary>Liest ab <paramref name="offset"/>; Anzahl (0 am Ende) oder -errno.</summary>
    long ReadAt(long offset, Span<byte> dst);

    /// <summary>Schreibt ab <paramref name="offset"/> (Lücken werden genullt); Anzahl oder -errno.</summary>
    long WriteAt(long offset, ReadOnlySpan<byte> src);

    long SetLength(long length);
}
