using System;
using System.Collections.Generic;
using System.Text;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V16: Name eines Unix-Sockets aus einer <c>sockaddr_un</c> (Familie AF_UNIX, dann bis zu 108 Bytes Name). Ein führendes Nullbyte macht
/// den Namen abstrakt (Linux: die übrigen Bytes bis zur angegebenen Länge, Nullbytes eingeschlossen, ohne Datei); sonst ist es ein Pfad
/// bis zum ersten Nullbyte. Eine Adresse nur aus der Familie (Länge 2) ist bei bind die Bitte um einen automatischen abstrakten Namen.
/// </summary>
internal readonly struct LinuxUnixAddress
{
    public const int AfUnix = 1;

    /// <summary><c>sizeof(struct sockaddr_un)</c>: 2 Bytes Familie, 108 Bytes Name.</summary>
    public const int MaxLength = 110;

    private const int FamilyLength = 2;
    private const long EINVAL = 22;

    private LinuxUnixAddress(bool isAbstract, byte[] name)
    {
        IsAbstract = isAbstract;
        Name = name;
    }

    /// <summary>Abstrakter Name; <see cref="Name"/> beginnt dann mit dem Nullbyte.</summary>
    public bool IsAbstract { get; }

    /// <summary>Rohe Namensbytes wie bei bind angegeben (Pfad ohne abschließendes Nullbyte); leer bei einem unbenannten Socket.</summary>
    public byte[] Name { get; }

    public bool IsUnnamed => Name.Length == 0;

    /// <summary>Schlüssel in der Namenstabelle; Bytes eins zu eins in Zeichen, damit abstrakte Namen mit Nullbytes eindeutig bleiben.</summary>
    public string Key => LinuxUnixNames.KeyOf(Name);

    /// <summary>Pfad als Zeichenkette (UTF-8 wie die übrigen Pfadaufrufe); nur für Pfadnamen sinnvoll.</summary>
    public string Path => Encoding.UTF8.GetString(Name);

    public static LinuxUnixAddress Unnamed => new(false, []);

    public static LinuxUnixAddress Abstract(byte[] nameWithLeadingZero) => new(true, nameWithLeadingZero);

    /// <summary>
    /// Liest eine Adresse wie Linux (<c>unix_validate_addr</c>, <c>unix_mkname</c>): Länge 2 bis 110, Familie AF_UNIX, sonst -EINVAL.
    /// Länge 2 liefert einen unbenannten Namen; ein Pfad endet am ersten Nullbyte oder an der Länge.
    /// </summary>
    public static long Parse(ReadOnlySpan<byte> raw, out LinuxUnixAddress address)
    {
        address = Unnamed;
        if (raw.Length < FamilyLength || raw.Length > MaxLength || BitConverter.ToUInt16(raw.Slice(0, FamilyLength)) != AfUnix)
        {
            return -EINVAL;
        }

        ReadOnlySpan<byte> name = raw.Slice(FamilyLength);
        if (name.IsEmpty)
        {
            return 0;
        }

        if (name[0] == 0)
        {
            address = new LinuxUnixAddress(true, name.ToArray());
            return 0;
        }

        int end = name.IndexOf((byte)0);
        address = new LinuxUnixAddress(false, (end < 0 ? name : name.Slice(0, end)).ToArray());
        return 0;
    }

    /// <summary>
    /// Länge, die getsockname, getpeername und accept melden: Familie plus Name, bei einem Pfad mit Nullbyte (wie Linux), unbenannt nur
    /// die Familie.
    /// </summary>
    public int Length => FamilyLength + Name.Length + (IsUnnamed || IsAbstract ? 0 : 1);

    /// <summary>Schreibt die Adresse nach <paramref name="dst"/> (höchstens <see cref="Length"/> Bytes, gekürzt wie Linux).</summary>
    public int Format(Span<byte> dst)
    {
        Span<byte> full = stackalloc byte[MaxLength + 1];
        full.Clear();
        BitConverter.TryWriteBytes(full.Slice(0, FamilyLength), (ushort)AfUnix);
        Name.AsSpan().CopyTo(full.Slice(FamilyLength));
        int n = Math.Min(dst.Length, Length);
        full.Slice(0, n).CopyTo(dst);
        return n;
    }
}

/// <summary>
/// V16: gebundene Namen der Unix-Sockets eines Laufs (alle Prozesse eines <c>run</c> teilen sie wie die PIDs, <see cref="LinuxIds"/>).
/// Abstrakte Namen leben nur hier und werden mit dem Socket frei. Pfadnamen legen zusätzlich eine leere Datei in der Prozesswurzel an,
/// damit bind wie bei Linux an einem vorhandenen Pfad mit EADDRINUSE scheitert, unlink den Namen löst und ein Verbindungsversuch auf eine
/// gewöhnliche Datei ECONNREFUSED liefert; die Datei bleibt nach dem Schließen stehen wie bei Linux. Die Inode-Nummern der Prozesswurzel
/// hängen am Pfad; deshalb melden unlink und rename ihre Änderung hier (<see cref="Unlinked"/>, <see cref="Renamed"/>), damit eine neue
/// Datei gleichen Namens den alten Socket nicht erreicht. Grenzen: Umbenennen eines Verzeichnisses mit Sockets darin löst deren Namen
/// nicht mit; Pfade werden genau verglichen (auf einer FAT-Wurzel ohne Groß-/Kleinschreibung ergibt eine andere Schreibweise ECONNREFUSED).
/// <para>
/// Sperren: <see cref="_gate"/> ist eine Blattsperre; darin kein Dateisystemaufruf. Pfadaufrufe macht der Verteiler unter der Dateisperre
/// des Prozesses davor oder danach. Zwei Prozesse, die denselben Pfad binden, entscheidet O_EXCL beim Anlegen der Datei.
/// </para>
/// </summary>
internal sealed class LinuxUnixNames
{
    private const long EADDRINUSE = 98;

    private readonly object _gate = new();
    private readonly Dictionary<string, object> _abstract = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _paths = new(StringComparer.Ordinal);
    private int _autobind;

    /// <summary>V27: Ports von AF_INET-Sockets (nur Loopback) desselben Laufs.</summary>
    public LinuxInetPorts Ports { get; } = new();

    /// <summary>Bytes eins zu eins in Zeichen (ohne Encoding.Latin1, das im Kernel nicht belegt ist).</summary>
    public static string KeyOf(ReadOnlySpan<byte> name)
    {
        var chars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            chars[i] = (char)name[i];
        }

        return new string(chars);
    }

    /// <summary>Trägt einen abstrakten Namen ein; -EADDRINUSE, wenn er schon vergeben ist.</summary>
    public long BindAbstract(string key, object owner)
    {
        lock (_gate)
        {
            if (_abstract.ContainsKey(key))
            {
                return -EADDRINUSE;
            }

            _abstract[key] = owner;
            return 0;
        }
    }

    /// <summary>
    /// Automatischer abstrakter Name wie Linux (Nullbyte und fünf Hexziffern) für bind mit Länge 2 und für connect eines unbenannten
    /// Sockets mit SO_PASSCRED; liefert die Namensbytes.
    /// </summary>
    public byte[] Autobind(object owner)
    {
        lock (_gate)
        {
            for (int tries = 0; tries <= 0xfffff; tries++)
            {
                _autobind = (_autobind + 1) & 0xfffff;
                byte[] name = new byte[6];
                for (int i = 0; i < 5; i++)
                {
                    int digit = (_autobind >> (4 * (4 - i))) & 0xf;
                    name[1 + i] = (byte)(digit < 10 ? '0' + digit : 'a' + digit - 10);
                }

                string key = KeyOf(name);
                if (!_abstract.ContainsKey(key))
                {
                    _abstract[key] = owner;
                    return name;
                }
            }

            return [];
        }
    }

    public object? FindAbstract(string key)
    {
        lock (_gate)
        {
            return _abstract.TryGetValue(key, out object? owner) ? owner : null;
        }
    }

    /// <summary>
    /// Bindet einen Pfad (absolut und normalisiert, <see cref="LinuxPath.Resolve(ILinuxProcess, ILinuxFiles, int, string, out string)"/>):
    /// legt die Datei mit O_EXCL an (EEXIST wird EADDRINUSE, übrige Fehler wie open) und trägt den Pfad ein.
    /// Nur unter der Dateisperre des Prozesses.
    /// </summary>
    public long BindPath(ILinuxFileSystem fs, string path, object owner)
    {
        const int OWrOnly = 1, OCreat = 0x40, OExcl = 0x80;
        const long EEXIST = 17;
        long r = fs.Open(path, OWrOnly | OCreat | OExcl, 0x1ed, out ILinuxFile? file);
        if (r < 0)
        {
            return r == -EEXIST ? -EADDRINUSE : r;
        }

        _ = file!.Close();
        lock (_gate)
        {
            _paths[path] = owner;
        }

        return 0;
    }

    /// <summary>
    /// Sucht den Socket hinter einem Pfad: Fehler von stat (ENOENT …) wie Linux; eine Datei ohne Eintrag (gewöhnliche Datei, nach unlink
    /// neu angelegt, Socket geschlossen) liefert null und der Aufrufer ECONNREFUSED. Nur unter der Dateisperre.
    /// </summary>
    public long FindPath(ILinuxFileSystem fs, string path, out object? owner)
    {
        owner = null;
        long r = fs.Stat(path, true, out _);
        if (r < 0)
        {
            return r;
        }

        lock (_gate)
        {
            _ = _paths.TryGetValue(path, out owner);
        }

        return 0;
    }

    /// <summary>
    /// Gibt alle Unix-Namen von <paramref name="owner"/> frei (letztes close des Sockets); die Datei eines Pfadnamens bleibt. Ports von AF_INET
    /// gibt das Socket selbst über <see cref="LinuxInetPorts.Release"/> frei (V27b, ohne Durchsuchen der Porttabellen).
    /// </summary>
    public void Release(object owner)
    {
        lock (_gate)
        {
            List<string>? drop = null;
            foreach (KeyValuePair<string, object> kv in _abstract)
            {
                if (ReferenceEquals(kv.Value, owner))
                {
                    (drop ??= new List<string>()).Add(kv.Key);
                }
            }

            foreach (string key in drop ?? [])
            {
                _abstract.Remove(key);
            }

            drop = null;
            foreach (KeyValuePair<string, object> kv in _paths)
            {
                if (ReferenceEquals(kv.Value, owner))
                {
                    (drop ??= new List<string>()).Add(kv.Key);
                }
            }

            foreach (string key in drop ?? [])
            {
                _paths.Remove(key);
            }
        }
    }

    /// <summary>Nach einem erfolgreichen unlink: der Pfad ist kein Socketname mehr.</summary>
    public void Unlinked(string path)
    {
        lock (_gate)
        {
            _paths.Remove(path);
        }
    }

    /// <summary>Nach einem erfolgreichen rename: ein Socketname zieht mit, ein überschriebener Name am Ziel verfällt.</summary>
    public void Renamed(string from, string to)
    {
        lock (_gate)
        {
            _paths.Remove(to);
            if (_paths.Remove(from, out object? owner))
            {
                _paths[to] = owner;
            }
        }
    }
}

/// <summary>Optional neben <see cref="ILinuxProcess"/>: die Namenstabelle der Unix-Sockets des Laufs (V16).</summary>
internal interface ILinuxUnixNames
{
    LinuxUnixNames UnixNames { get; }
}
