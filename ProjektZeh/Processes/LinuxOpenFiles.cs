using System.Collections.Generic;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes;

/// <summary>
/// Optional neben <see cref="ILinuxFiles"/> (V15): das Flag <c>FD_CLOEXEC</c> je Nummer. Es gehört zur Nummer, nicht zur offenen Datei
/// (dup löscht es, wie Linux); execve schließt die markierten Nummern.
/// </summary>
internal interface ILinuxFdFlags
{
    bool GetCloseOnExec(int fd);

    void SetCloseOnExec(int fd, bool on);
}

/// <summary>
/// Optional neben <see cref="ILinuxFiles"/> (V15): Nummern auf eine offene Datei werden über alle Prozesse eines Laufs gezählt. Ohne sie
/// zählt close nur die eigene Tabelle (Hosttests mit Ersatzprozessen).
/// </summary>
internal interface ILinuxOpenFileCount
{
    /// <summary>true, solange irgendeine Nummer irgendeines Prozesses des Laufs auf <paramref name="d"/> zeigt. Der Aufrufer hält FileGate.</summary>
    bool IsOpenAnywhere(ILinuxDescriptor d);
}

/// <summary>
/// V15: Referenzzähler der offenen Dateien und Pipe-Enden aller Prozesse eines Laufs (Linux: <c>struct file.f_count</c>). Nach vfork zeigen
/// Kind und Eltern auf dieselben Objekte; close im Kind darf die Datei der Eltern nicht schließen, und das Ende des letzten Schreibers einer Pipe
/// muss beim Leser EOF auslösen. Gezählt wird jede Nummer, nicht jede Tabelle (dup zählt zweimal).
/// <para>
/// Das Objekt ist zugleich die Dateisperre (<see cref="ILinuxFiles.FileGate"/>), die alle Prozesse eines Laufs teilen; alle Methoden setzen
/// voraus, dass der Aufrufer sie hält. Darin keine weitere Sperre außer den Sperren der Dateien (Platte), wie bisher unter FileGate.
/// Das Terminal zählt nicht: Es gehört dem Prozess und wird nie geschlossen.
/// </para>
/// <para>
/// Eine Liste statt eines Wörterbuchs: Gleichheit soll Referenzgleichheit sein, und Dateien eines Laufs sind wenige (die Suche ist billig
/// gegenüber einer Datei-I/O, wie die Tabellensuche in close).
/// </para>
/// </summary>
internal sealed class LinuxOpenFiles
{
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public Entry(ILinuxDescriptor descriptor) => Descriptor = descriptor;

        public ILinuxDescriptor Descriptor { get; }

        public int Count { get; set; }
    }

    /// <summary>Anzahl verschiedener gezählter Objekte (Tests).</summary>
    public int Distinct => _entries.Count;

    /// <summary>Eine Nummer mehr zeigt auf <paramref name="d"/>.</summary>
    public void Add(ILinuxDescriptor d)
    {
        if (!Counted(d))
        {
            return;
        }

        Entry? e = Find(d);
        if (e == null)
        {
            e = new Entry(d);
            _entries.Add(e);
        }

        e.Count++;
    }

    /// <summary>Eine Nummer weniger; unbekannte Objekte (Terminal, nie gezählt) bleiben ohne Wirkung.</summary>
    public void Remove(ILinuxDescriptor d)
    {
        Entry? e = Find(d);
        if (e == null)
        {
            return;
        }

        e.Count--;
        if (e.Count <= 0)
        {
            _entries.Remove(e);
        }
    }

    /// <summary>Anzahl der Nummern auf <paramref name="d"/> im ganzen Lauf.</summary>
    public int CountOf(ILinuxDescriptor d) => Find(d)?.Count ?? 0;

    public bool IsReferenced(ILinuxDescriptor d) => CountOf(d) > 0;

    // Wie ReleaseIfUnused: nur Dateien, Pipe-Enden, Unix-Sockets (V16), eventfd und inotify (V20) werden geschlossen, also nur sie gezählt.
    private static bool Counted(ILinuxDescriptor d) => d is ILinuxFile or LinuxPipeEnd or LinuxUnixSocket or ILinuxAnonFile;

    private Entry? Find(ILinuxDescriptor d)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i].Descriptor, d))
            {
                return _entries[i];
            }
        }

        return null;
    }
}
