using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>Ein offener Deskriptor eines Linux-Prozesses (S1: nur Schreiben, etwa das Terminal).</summary>
internal interface ILinuxDescriptor
{
    /// <summary>Schreibt die Bytes; liefert die geschriebene Anzahl oder -errno.</summary>
    long Write(ReadOnlySpan<byte> data);
}

/// <summary>Was der Verteiler vom Prozess braucht. Die Brücke zu echten Prozessen (V3) implementiert das.</summary>
internal interface ILinuxProcess
{
    IUserMemory Memory { get; }

    /// <summary>Deskriptor zu <paramref name="fd"/> oder null (dann -EBADF).</summary>
    ILinuxDescriptor? GetDescriptor(int fd);

    /// <summary>Aktuelles Programmende (brk).</summary>
    ulong Brk { get; }

    /// <summary>Benutzernummer des Prozesses (dieselbe wie AT_UID), für getuid und geteuid.</summary>
    ulong Uid { get; }

    /// <summary>Gruppennummer des Prozesses (dieselbe wie AT_GID), für getgid und getegid.</summary>
    ulong Gid { get; }

    /// <summary>Setzt brk; false, wenn die Adresse unter dem Anfang liegt oder nicht belegbar ist (Brk bleibt dann unverändert).</summary>
    bool TrySetBrk(ulong newBrk);

    /// <summary>Belegt anonymen Speicher; 0 bei Fehler. <paramref name="prot"/> sind die PROT_*-Bits.</summary>
    ulong MapAnonymous(ulong len, int prot);

    /// <summary>Belegt anonymen Speicher genau bei <paramref name="addr"/> und ersetzt dort Vorhandenes (MAP_FIXED); false bei Fehler.</summary>
    bool MapFixed(ulong addr, ulong len, int prot);

    /// <summary>Verwirft den Inhalt der Seiten (madvise DONTNEED/FREE): der Bereich bleibt belegt und liest sich danach als Nullen; false bei Fehler.</summary>
    bool Discard(ulong addr, ulong len);

    /// <summary>Gibt einen Bereich frei; false bei Fehler.</summary>
    bool Unmap(ulong addr, ulong len);

    /// <summary>Ändert den Schutz eines Bereichs; false bei Fehler.</summary>
    bool Protect(ulong addr, ulong len, int prot);

    /// <summary>FS-Basis des Threads (TLS), gesetzt über arch_prctl.</summary>
    ulong FsBase { get; set; }

    /// <summary>Adresse für set_tid_address (wird beim Threadende gelöscht; S1 merkt sie sich nur).</summary>
    ulong ClearChildTid { get; set; }

    int Pid { get; }

    /// <summary>Signalzustand des Prozesses (Handler, Maske, Alternativstack); der Prozess hält ihn, damit er mit ihm endet.</summary>
    LinuxSignalState Signals { get; }

    /// <summary>Beendet den Prozess mit dem Code; kehrt im Test zurück, im Kernel nicht.</summary>
    void Exit(int code);
}

/// <summary>
/// Optional neben <see cref="ILinuxProcess"/>: true, sobald das Programm abgebrochen ist (Strg+C, Zeitlimit, exit_group).
/// Ohne sie laufen Kopierschleifen bis zum Ende (Hosttests mit Ersatzprozessen).
/// </summary>
internal interface ILinuxAbort
{
    bool IsAborted { get; }
}

/// <summary>
/// V18: Optional neben <see cref="ILinuxAbort"/>: true, solange ein Signal mit Handler anhängig und nicht blockiert ist. Wartende Aufrufe
/// (wait4, poll, Pipes, Sockets, futex, pause, nanosleep) kehren dann mit -EINTR zurück, damit die Brücke den Handler beim Rücksprung anspringt.
/// Getrennt von <see cref="ILinuxAbort.IsAborted"/>: Kopierschleifen über reguläre Dateien unterbricht ein Signal unter Linux nicht.
/// </summary>
internal interface ILinuxSignalWake
{
    bool SignalWaiting { get; }
}

/// <summary>V18: Abbruch oder wartendes Signal des Aufrufers; die gemeinsame Prüfung aller Warteschleifen.</summary>
internal static class LinuxInterrupt
{
    public static bool Check(ILinuxAbort? caller) => caller != null && (caller.IsAborted || caller is ILinuxSignalWake { SignalWaiting: true });
}

/// <summary>
/// Optional neben <see cref="ILinuxProcess"/>: Pfad des laufenden Programms, wie der Prozess ihn sieht (V12, readlink "/proc/self/exe").
/// null, wenn das Programm außerhalb der Prozesswurzel liegt; dann gibt es den Link nicht (-ENOENT).
/// </summary>
internal interface ILinuxExe
{
    string? ExePath { get; }
}
