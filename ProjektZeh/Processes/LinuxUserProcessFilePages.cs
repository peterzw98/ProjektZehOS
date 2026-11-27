using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shared;

namespace ProjektZeh.Processes;

/// <summary>
/// V30: Dateiabbildungen, deren Seiten erst beim Zugriff gelesen werden (<see cref="LinuxFileExtents"/>). Belegen und Eintragen der
/// Quelle geschehen unter einer <c>_memGate</c>-Sperre, damit kein anderer Thread die Seite dazwischen genullt holt.
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxFileMapping
{
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
    private void AddProgramExtents(UserImage image, ILinuxPageSource program)
    {
        foreach (UserSegment segment in image.Segments)
        {
            if (!segment.Interpreter && segment.FileSize > 0)
            {
                program.AddRef();
                _fileExtents.Add(segment.Address, segment.FileSize, (long)segment.FileOffset, program);
            }
        }
    }

    /// <summary>
    /// V30: wie <see cref="Load(UserImage, System.ReadOnlySpan{byte})"/>, aber die Programmsegmente kommen beim Zugriff aus
    /// <paramref name="program"/> (der Aufrufer behält seinen Verweis); <paramref name="head"/> ist nur der Kopfteil, den der Lader las.
    /// </summary>
    public ResultErr Load(UserImage image, System.ReadOnlySpan<byte> head, ILinuxPageSource program)
    {
        System.ArgumentNullException.ThrowIfNull(program);
        AddProgramExtents(image, program);
        return LoadCore(image, head, lazyProgram: true);
    }
}
