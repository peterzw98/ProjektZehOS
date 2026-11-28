using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shared;

namespace ProjektZeh.Processes;

/// <summary>
/// V30: Dateiabbildungen, deren Seiten erst beim Zugriff gelesen werden (<see cref="LinuxFileExtents"/>). Belegen und Eintragen der
/// Quelle geschehen unter einer <c>_memGate</c>-Sperre, damit kein anderer Thread die Seite dazwischen genullt holt.
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxFileMapping
{
    /// <summary>
    /// V38: Wo liegt <paramref name="address"/>? Für die Serial-Zeile einer Ausnahme im Ring 3 ohne Handler (sonst endet ein Kindprozess still).
    /// In einer Dateiabbildung Pfad und Dateioffset, dazu Ladeadresse und Versatz ab ihr (für llvm-symbolizer), wenn die Datei ab Offset 0 abgebildet ist;
    /// sonst <c>anon 0xBeginn-0xEnde</c> des Bereichs (Interpreter, Heap, Stack) oder <c>ungemappt</c>. Nur im Threadkontext rufen
    /// (allokiert, nimmt <c>_memGate</c>), nicht im Ausnahmehandler.
    /// </summary>
    public string DescribeAddress(ulong address)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.DescribeAddress(address);
        }

        lock (_memGate)
        {
            if (_fileExtents.TryLocate(address, out ILinuxPageSource? source, out long fileOffset, out ulong loadBase))
            {
                string name = (source as LinuxSharedStoreFile)?.Path ?? "datei";
                // Beides: Der Versatz ab Ladeadresse gilt nur, wenn die Bibliothek zusammenhängend ab Offset 0 liegt (ld-musl bildet erst den
                // ganzen Umfang ab und legt die Segmente mit MAP_FIXED darüber); der Dateioffset gilt immer.
                string text = name + " Dateioffset 0x" + fileOffset.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
                return loadBase != 0 && address >= loadBase ? text + " Basis 0x" + loadBase.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "+0x" + (address - loadBase).ToString("x", System.Globalization.CultureInfo.InvariantCulture) : text;
            }

            var regions = new System.Collections.Generic.List<(ulong Start, ulong End, MapAccess Access)>();
            _map.CopyRegionsTo(regions);
            foreach ((ulong start, ulong end, MapAccess access) in regions)
            {
                if (start <= address && address < end)
                {
                    // Rechte als Kürzel wie /proc/maps, nicht Enum.ToString (im Kernel ohne Reflexion nicht belegt).
                    string rights = access switch { MapAccess.Read => "r--", MapAccess.ReadWrite => "rw-", MapAccess.ReadExecute => "r-x", _ => "---" };
                    return "anon 0x" + start.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "-0x" + end.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + " " + rights;
                }
            }

            return "ungemappt";
        }
    }

    /// <summary>Anzahl der Datei-Ausschnitte (für Tests).</summary>
    public int FileExtentCount => _vforkOf?.FileExtentCount ?? _fileExtents.Count;

    public ulong MapFilePrivate(ulong addr, bool fixedAt, ulong len, int prot, ILinuxPageSource source, long offset)
    {
        if (_vforkOf != null)
        {
            return _vforkOf.MapFilePrivate(addr, fixedAt, len, prot, source, offset);
        }

        lock (_memGate)
        {
            ulong at = fixedAt ? (MapFixedLocked(addr, len, prot) ? addr : 0) : MapAnonymousLocked(len, prot);
            if (at == 0)
            {
                source.Release();
                return 0;
            }

            _fileExtents.Add(at, len, offset, source);
            return at;
        }
    }

    /// <summary>
    /// execve: trägt die Dateiteile der Programmsegmente (nicht die des Interpreters) als Ausschnitte ein; je Segment ein eigener Verweis.
    /// Vor dem Start, ohne Nebenläufigkeit.
    /// </summary>
    private void AddProgramExtents(UserImage image, ILinuxPageSource? program, ILinuxPageSource? interpreter)
    {
        foreach (UserSegment segment in image.Segments)
        {
            ILinuxPageSource? from = segment.Interpreter ? interpreter : program;
            if (from != null && segment.FileSize > 0)
            {
                from.AddRef();
                _fileExtents.Add(segment.Address, segment.FileSize, (long)segment.FileOffset, from);
            }
        }
    }

    /// <summary>
    /// V30: wie <see cref="Load(UserImage, System.ReadOnlySpan{byte})"/>, aber die Programmsegmente kommen beim Zugriff aus
    /// <paramref name="program"/> (der Aufrufer behält seinen Verweis); <paramref name="head"/> ist nur der Kopfteil, den der Lader las.
    /// </summary>
    public ResultErr Load(UserImage image, System.ReadOnlySpan<byte> head, ILinuxPageSource program) => Load(image, head, program, null);

    /// <summary>
    /// N2-Z: wie oben; mit <paramref name="interpreter"/> kommen auch die Segmente des Interpreters beim Zugriff aus dessen Datei
    /// (<see cref="UserImage.InterpreterLazy"/>), ohne den Interpreter ganz zu lesen und zu kopieren. <paramref name="program"/> darf
    /// dann null sein (Programm aus einem ganz gelesenen Puffer, <paramref name="head"/> ist die ganze Datei).
    /// </summary>
    public ResultErr Load(UserImage image, System.ReadOnlySpan<byte> head, ILinuxPageSource? program, ILinuxPageSource? interpreter)
    {
        if (image.InterpreterLazy != (interpreter != null))
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Quelle des Interpreters und Abbild passen nicht zusammen.");
        }

        AddProgramExtents(image, program, interpreter);
        return LoadCore(image, head, lazyProgram: program != null);
    }
}
