using System;
using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Processes;

/// <summary>
/// V30: Lesequelle für Seiten, die erst beim Zugriff aus einer Datei kommen (PT_LOAD des Programms, mmap einer Datei mit MAP_PRIVATE).
/// Sie hält die Datei offen, unabhängig vom Deskriptor (close nach mmap lässt die Abbildung gültig, wie bei Linux), und liest ohne
/// Dateiposition. <see cref="ReadAt"/> läuft im Seitenfehler unter <c>_memGate</c> des Prozesses und ohne <c>FileGate</c>: Die
/// Implementierung darf deshalb keine Sperre vor <c>_memGate</c> nehmen, nur eigene danach (die Platte sperrt mit <c>StorageLock</c>).
/// Zählung: Jeder Halter (Ausschnitt, Kopie bei fork) gibt seinen Verweis mit <see cref="Release"/> zurück; der letzte schließt.
/// </summary>
internal interface ILinuxPageSource
{
    /// <summary>Liest ab <paramref name="offset"/>; Anzahl (0 am Dateiende) oder -errno.</summary>
    long ReadAt(long offset, Span<byte> dst);

    /// <summary>Ein Verweis mehr.</summary>
    void AddRef();

    /// <summary>Ein Verweis weniger; beim letzten wird die Datei geschlossen.</summary>
    void Release();
}

/// <summary>V30: Eine offene Datei, die eine <see cref="ILinuxPageSource"/> liefern kann (reguläre Dateien des Speichers).</summary>
internal interface ILinuxPageSourceFile
{
    /// <summary>Eine Quelle mit einem eigenen Verweis (der Aufrufer gibt ihn mit Release zurück) oder null, wenn die Datei zu ist.</summary>
    ILinuxPageSource? OpenPageSource();
}

/// <summary>V30: Prozess, der Dateien mit Seiten beim Zugriff abbilden kann (<see cref="LinuxUserProcess"/>).</summary>
internal interface ILinuxFileMapping
{
    /// <summary>
    /// Belegt den Bereich wie MapAnonymous oder MapFixed (mit <paramref name="fixedAt"/>) mit <paramref name="prot"/> und trägt die Quelle ab
    /// <paramref name="offset"/> ein, beides unter einer Sperre. Übernimmt den Verweis auf <paramref name="source"/> (auch bei Fehler).
    /// Adresse oder 0 ohne Platz oder bei ungültigem Schutz.
    /// </summary>
    ulong MapFilePrivate(ulong addr, bool fixedAt, ulong len, int prot, ILinuxPageSource source, long offset);
}

/// <summary>
/// V30: Eine Speicherdatei mit Zählung, die Deskriptor und Abbildungen teilen. Der Deskriptor hält den ersten Verweis; jede Quelle einen
/// weiteren. Zählen mit Interlocked: Der Deskriptor schließt unter <c>FileGate</c>, Abbildungen geben ihren Verweis unter <c>_memGate</c>
/// zurück (munmap, Prozessende), ohne gemeinsame Sperre. Lesen nach dem letzten Verweis gibt es nicht, weil jeder Leser einen hält.
/// </summary>
internal sealed class LinuxSharedStoreFile : ILinuxPageSource
{
    private readonly ILinuxStoreFile _file;
    private int _refs = 1;

    public LinuxSharedStoreFile(ILinuxStoreFile file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <summary>Anzahl der Verweise (für Tests).</summary>
    public int References => Volatile.Read(ref _refs);

    public ILinuxStoreFile File => _file;

    /// <summary>V40: Seiten kommen über den Lese-Cache des Laufs, wenn die Datei einen hat (<see cref="LinuxCachedStoreFile"/>).</summary>
    public long ReadAt(long offset, Span<byte> dst) => _file is LinuxCachedStoreFile cached ? cached.ReadPages(offset, dst) : _file.ReadAt(offset, dst);

    /// <summary>V32: Länge der Datei oder -errno (Seitenablage bei MAP_SHARED); sperrt wie <see cref="ReadAt"/> im Speicher selbst.</summary>
    public long Length() => _file.Length();

    /// <summary>V32: Zurückschreiben der Seitenablage; Anzahl oder -errno.</summary>
    public long WriteAt(long offset, ReadOnlySpan<byte> src) => _file.WriteAt(offset, src);

    public void AddRef() => Interlocked.Increment(ref _refs);

    public void Release() => _ = ReleaseChecked();

    /// <summary>Wie <see cref="Release"/>; liefert den Fehler beim Schließen (0, wenn es nicht der letzte Verweis war).</summary>
    public long ReleaseChecked()
    {
        int left = Interlocked.Decrement(ref _refs);
        if (left < 0)
        {
            throw new InvalidOperationException("Verweis zu oft zurückgegeben.");
        }

        return left == 0 ? _file.Close() : 0;
    }
}

/// <summary>
/// V30: Datei-Ausschnitte eines Adressraums. Ein Ausschnitt sagt: Die Bytes [Start, End) des Benutzerraums kommen ab Dateioffset
/// <c>Offset</c> aus <c>Source</c>; hinter dem Dateiende Nullen. Die Grenzen sind byte-genau (Segmente beginnen und enden mitten in
/// Seiten). Eine Seite ohne Frame wird beim ersten Zugriff genullt und mit allen Ausschnitten gefüllt, die sie schneiden. Nur Buchführung
/// und Lesen; Sperren hält der Aufrufer (<c>_memGate</c>).
/// </summary>
internal sealed class LinuxFileExtents
{
    private readonly record struct Extent(ulong Start, ulong End, long Offset, ILinuxPageSource Source);

    private readonly List<Extent> _extents = new();

    public int Count => _extents.Count;

    /// <summary>Übernimmt einen Verweis auf <paramref name="source"/> (der Aufrufer gibt ihn ab).</summary>
    public void Add(ulong start, ulong length, long offset, ILinuxPageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (length == 0 || start > ulong.MaxValue - length || offset < 0)
        {
            source.Release();
            return;
        }

        _extents.Add(new Extent(start, start + length, offset, source));
    }

    /// <summary>true, wenn ein Ausschnitt [page, page + 4096) schneidet.</summary>
    public bool Covers(ulong page)
    {
        ulong end = page + AddressSpaceMap.PageSize;
        foreach (Extent e in _extents)
        {
            if (e.Start < end && page < e.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Füllt die (schon genullte) Seite ab <paramref name="page"/> aus allen Ausschnitten. false bei einem Lesefehler oder wenn die Seite
    /// eines Ausschnitts ganz hinter dem Dateiende liegt (V30-P3, etwa nach ftruncate); dann ist der Inhalt unbrauchbar, der Aufrufer gibt den
    /// Frame zurück und der Seitenfehler wird SIGBUS wie bei Linux (<c>filemap_fault</c>: Seitenindex ab der aufgerundeten Dateigröße).
    /// Endet die Datei in der Seite, bleibt der Rest null wie bei Linux.
    /// </summary>
    public bool Fill(ulong page, Span<byte> frame)
    {
        ulong pageEnd = page + AddressSpaceMap.PageSize;
        foreach (Extent e in _extents)
        {
            ulong from = Math.Max(e.Start, page);
            ulong to = Math.Min(e.End, pageEnd);
            if (from >= to)
            {
                continue;
            }

            long offset = e.Offset + (long)(from - e.Start);
            Span<byte> dst = frame.Slice((int)(from - page), (int)(to - from));
            int done = 0;
            while (done < dst.Length)
            {
                long n = e.Source.ReadAt(offset + done, dst.Slice(done));
                if (n < 0)
                {
                    return false;
                }

                if (n == 0)
                {
                    if (done == 0 && (from == page || BeyondEnd(e.Source, offset - (long)(from - page))))
                    {
                        return false;
                    }

                    break;
                }

                done += (int)n;
            }
        }

        return true;
    }

    /// <summary>
    /// V30-P3: Liegt die Dateiseite, die bei <paramref name="pageOffset"/> beginnt, ganz hinter dem Dateiende? Gefragt erst, wenn das Lesen ab
    /// dem Ausschnittbeginn schon 0 lieferte; beginnt der Ausschnitt mitten in der Seite (Programmsegment), prüft ein Byte am Seitenanfang.
    /// Dateioffset und Adresse sind kongruent (mmap verlangt ausgerichtete Versätze, ELF p_offset ≡ p_vaddr); sonst zählt Offset 0.
    /// </summary>
    private static bool BeyondEnd(ILinuxPageSource source, long pageOffset)
    {
        Span<byte> one = stackalloc byte[1];
        return source.ReadAt(Math.Max(0, pageOffset), one) <= 0;
    }

    /// <summary>Entfernt [start, end) aus allen Ausschnitten; Randstücke bleiben mit eigenem Verweis.</summary>
    public void Cut(ulong start, ulong end)
    {
        for (int i = _extents.Count - 1; i >= 0; i--)
        {
            Extent e = _extents[i];
            if (e.End <= start || e.Start >= end)
            {
                continue;
            }

            _extents.RemoveAt(i);
            if (e.Start < start)
            {
                e.Source.AddRef();
                _extents.Add(e with { End = start });
            }

            if (e.End > end)
            {
                e.Source.AddRef();
                _extents.Add(e with { Start = end, Offset = e.Offset + (long)(end - e.Start) });
            }

            e.Source.Release();
        }
    }

    /// <summary>fork: <paramref name="into"/> bekommt alle Ausschnitte mit je einem eigenen Verweis.</summary>
    public void CopyTo(LinuxFileExtents into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (Extent e in _extents)
        {
            e.Source.AddRef();
            into._extents.Add(e);
        }
    }

    /// <summary>Gibt alle Verweise zurück (Prozessende).</summary>
    public void Clear()
    {
        foreach (Extent e in _extents)
        {
            e.Source.Release();
        }

        _extents.Clear();
    }
}
