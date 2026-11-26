using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Zugriff auf den Speicher eines Linux-Benutzerprozesses. Jede Methode prüft Zeiger und Länge selbst
/// (kein Überlauf von addr+len, nur zugeordnete Seiten mit passendem Schutz) und liefert false statt zu werfen;
/// der Verteiler macht daraus -EFAULT. Ein teilweiser Zugriff gilt als Fehler (nichts oder alles).
/// </summary>
internal interface IUserMemory
{
    bool TryRead(ulong addr, Span<byte> dst);
    bool TryWrite(ulong addr, ReadOnlySpan<byte> src);

    /// <summary>
    /// true, wenn <see cref="TryWrite"/> auf [addr, addr+length) die Seiten beschreiben dürfte; schreibt nichts. Für Aufrufe, die vor dem
    /// Warten -EFAULT melden müssen, ohne den Benutzerpuffer bei -EAGAIN, -EINTR oder EOF zu verändern (V13-Review P2-2).
    /// </summary>
    bool CanWrite(ulong addr, ulong length);

    /// <summary>Liest eine NUL-terminierte Zeichenkette (UTF-8) von höchstens <paramref name="max"/> Bytes ohne NUL.</summary>
    bool TryReadCString(ulong addr, int max, out string s);
}

/// <summary>Überlaufsichere Bereichsprüfung für Implementierungen von <see cref="IUserMemory"/>.</summary>
internal static class UserRange
{
    /// <summary>true, wenn [addr, addr+length) vollständig in [start, end) liegt, ohne dass addr+length überläuft.</summary>
    public static bool Contains(ulong start, ulong end, ulong addr, ulong length)
    {
        if (addr < start || addr > end)
        {
            return false;
        }

        return length <= end - addr;
    }
}
