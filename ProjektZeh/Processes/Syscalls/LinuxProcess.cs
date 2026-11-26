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

    /// <summary>Setzt brk; false, wenn die Adresse unter dem Anfang liegt oder nicht belegbar ist (Brk bleibt dann unverändert).</summary>
    bool TrySetBrk(ulong newBrk);

    /// <summary>Belegt anonymen Speicher; 0 bei Fehler. <paramref name="prot"/> sind die PROT_*-Bits.</summary>
    ulong MapAnonymous(ulong len, int prot);

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
