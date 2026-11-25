using System;
using System.Collections.Generic;
using ProjektZeh.Shared;

namespace ProjektZeh.Processes;

internal enum MapAccess { None, Read, ReadWrite, ReadExecute }

/// <summary>Platzierung wie bei mmap: ohne Flag, MAP_FIXED, MAP_FIXED_NOREPLACE.</summary>
internal enum Placement { Hint, FixedReplace, FixedNoReplace }

/// <summary>
/// Verwaltet die belegten Bereiche eines Benutzeradressraums [start, end) als sortierte, überlappungsfreie Liste.
/// Reine Buchführung ohne Seitentabellen, damit sie auf dem Host prüfbar ist. Ein Bereich mit
/// <see cref="MapAccess.None"/> bleibt belegt (wie PROT_NONE). Nicht threadsicher; der Aufrufer serialisiert.
/// </summary>
internal sealed class AddressSpaceMap
{
    public const ulong PageSize = 4096;

    private readonly record struct Region(ulong Start, ulong End, MapAccess Access);

    private readonly ulong _start;
    private readonly ulong _end;
    private readonly List<Region> _regions = new();

    public AddressSpaceMap(ulong start, ulong end)
    {
        if (!IsAligned(start) || !IsAligned(end) || start >= end)
        {
            throw new ArgumentException("Der Adressraum muss seitenausgerichtet und nicht leer sein.");
        }

        _start = start;
        _end = end;
    }

    /// <summary>
    /// Belegt <paramref name="length"/> Bytes. Bei <see cref="Placement.Hint"/> gilt <paramref name="hint"/> als Wunsch:
    /// der erste freie Platz ab dem Hinweis, danach vom Anfang bis zum Hinweis; Hinweis 0 beginnt am Anfang. Bei den festen
    /// Arten ist <paramref name="hint"/> die Adresse; FixedReplace ersetzt Überlappungen, FixedNoReplace scheitert ohne Änderung.
    /// </summary>
    public ResultObject<ulong> Reserve(ulong hint, ulong length, MapAccess access, Placement placement)
    {
        ResultErr check = ValidateRange(hint, length, placement != Placement.Hint);
        if (!check.IsOK())
        {
            return ResultObjectFactory.CreateFailure<ulong>(check);
        }

        if (placement == Placement.Hint)
        {
            if (length > _end - _start)
            {
                return ResultObjectFactory.CreateFailure<ulong>(ResultCode.Failure, "Der Adressraum ist für die Länge zu klein.");
            }

            ulong from = Math.Clamp(hint, _start, _end);
            if (TryFindFree(from, _end, length, out ulong found) || TryFindFree(_start, _end, length, out found))
            {
                Insert(found, found + length, access);
                return ResultObjectFactory.CreateSuccess(found);
            }

            return ResultObjectFactory.CreateFailure<ulong>(ResultCode.Failure, "Kein freier Platz im Adressraum.");
        }

        ulong endAddress = hint + length;
        if (placement == Placement.FixedNoReplace && Overlaps(hint, endAddress))
        {
            return ResultObjectFactory.CreateFailure<ulong>(ResultCode.Failure, "Der Bereich ist bereits belegt.");
        }

        Cut(hint, endAddress);
        Insert(hint, endAddress, access);
        return ResultObjectFactory.CreateSuccess(hint);
    }

    /// <summary>Gibt den Bereich frei; belegte Bereiche werden geteilt, Lücken sind erlaubt.</summary>
    public ResultErr Release(ulong address, ulong length)
    {
        ResultErr check = ValidateRange(address, length, true);
        if (!check.IsOK())
        {
            return check;
        }

        Cut(address, address + length);
        return ResultErr.CreateSuccess();
    }

    /// <summary>Ändert den Zugriff; eine Lücke im Bereich ist ein Fehler, dann bleibt alles unverändert.</summary>
    public ResultErr Protect(ulong address, ulong length, MapAccess access)
    {
        ResultErr check = ValidateRange(address, length, true);
        if (!check.IsOK())
        {
            return check;
        }

        ulong endAddress = address + length;
        if (!IsFullyCovered(address, endAddress))
        {
            return ResultErr.CreateFailure(ResultCode.NotFound, "Der Bereich enthält unbelegte Adressen.");
        }

        Cut(address, endAddress);
        Insert(address, endAddress, access);
        return ResultErr.CreateSuccess();
    }

    public bool TryFind(ulong address, out MapAccess access)
    {
        int index = FirstEndingAfter(address);
        if (index < _regions.Count && _regions[index].Start <= address)
        {
            access = _regions[index].Access;
            return true;
        }

        access = MapAccess.None;
        return false;
    }

    private static bool IsAligned(ulong value) => (value & (PageSize - 1)) == 0;

    private ResultErr ValidateRange(ulong address, ulong length, bool mustFit)
    {
        if (!IsAligned(address) || !IsAligned(length))
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Adresse und Länge müssen seitenausgerichtet sein.");
        }

        if (length == 0)
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Die Länge darf nicht 0 sein.");
        }

        if (!mustFit)
        {
            return ResultErr.CreateSuccess();
        }

        if (address > ulong.MaxValue - length)
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Adresse plus Länge läuft über.");
        }

        if (address < _start || address + length > _end)
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Der Bereich liegt außerhalb des Adressraums.");
        }

        return ResultErr.CreateSuccess();
    }

    // Index der ersten Region, die über address hinausreicht (End > address); Count, wenn es keine gibt.
    private int FirstEndingAfter(ulong address)
    {
        int low = 0;
        int high = _regions.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_regions[mid].End > address)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }

    private bool Overlaps(ulong from, ulong to)
    {
        int index = FirstEndingAfter(from);
        return index < _regions.Count && _regions[index].Start < to;
    }

    private bool IsFullyCovered(ulong from, ulong to)
    {
        ulong next = from;
        for (int i = FirstEndingAfter(from); i < _regions.Count && next < to; i++)
        {
            if (_regions[i].Start > next)
            {
                return false;
            }

            next = _regions[i].End;
        }

        return next >= to;
    }

    // Erster freier Platz der Länge length in [from, limit).
    private bool TryFindFree(ulong from, ulong limit, ulong length, out ulong address)
    {
        ulong candidate = from;
        for (int i = FirstEndingAfter(from); ; i++)
        {
            ulong gapEnd = i < _regions.Count ? Math.Min(_regions[i].Start, limit) : limit;
            if (gapEnd > candidate && gapEnd - candidate >= length)
            {
                address = candidate;
                return true;
            }

            if (i >= _regions.Count || _regions[i].Start >= limit)
            {
                address = 0;
                return false;
            }

            candidate = Math.Max(candidate, _regions[i].End);
        }
    }

    // Entfernt [from, to) aus allen Regionen; Randstücke bleiben erhalten.
    private void Cut(ulong from, ulong to)
    {
        int i = FirstEndingAfter(from);
        while (i < _regions.Count && _regions[i].Start < to)
        {
            Region region = _regions[i];
            _regions.RemoveAt(i);
            if (region.Start < from)
            {
                _regions.Insert(i++, region with { End = from });
            }

            if (region.End > to)
            {
                _regions.Insert(i++, region with { Start = to });
            }
        }
    }

    // Setzt eine Region in die (nach Cut) freie Lücke und verschmilzt gleichartige Nachbarn.
    private void Insert(ulong from, ulong to, MapAccess access)
    {
        int i = FirstEndingAfter(from);
        if (i < _regions.Count && _regions[i].Start == to && _regions[i].Access == access)
        {
            to = _regions[i].End;
            _regions.RemoveAt(i);
        }

        if (i > 0 && _regions[i - 1].End == from && _regions[i - 1].Access == access)
        {
            from = _regions[i - 1].Start;
            _regions.RemoveAt(--i);
        }

        _regions.Insert(i, new Region(from, to, access));
    }
}
