using System;
using System.IO;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Packages;

/// <summary>
/// Herkunft eines Pakets: lokales Verzeichnis oder HTTPS-Repository. Pfade sind relativ und bereits geprueft
/// (Kennung/Version/Manifestpfad); die Quelle prueft keine Inhalte, das uebernimmt PackageStore.
/// </summary>
internal interface IPackageSource
{
    /// <summary>Liest eine kleine Datei vollstaendig; groessere als <paramref name="maximumBytes"/> sind ein Fehler.</summary>
    ResultObject<byte[]> Read(string path, int maximumBytes, CancellationToken cancellation);

    /// <summary>Kopiert genau <paramref name="size"/> Bytes nach <paramref name="target"/>, ohne sie im Speicher zu sammeln.</summary>
    ResultErr Copy(string path, long size, Stream target, CancellationToken cancellation);
}

internal sealed class DirectoryPackageSource(string directory) : IPackageSource
{
    private const int BufferBytes = 64 * 1024;

    public ResultObject<byte[]> Read(string path, int maximumBytes, CancellationToken cancellation)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, path), FileMode.Open, FileAccess.Read);
            if (stream.Length > maximumBytes) return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, path + " ist zu gross.");
            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return ResultObjectFactory.CreateSuccess(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ResultObjectFactory.CreateFromException<byte[]>(exception);
        }
    }

    public ResultErr Copy(string path, long size, Stream target, CancellationToken cancellation)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, path), FileMode.Open, FileAccess.Read);
            return stream.Length == size ? CopyExactly(stream, path, size, target, cancellation)
                : ResultErr.CreateFailure(ResultCode.InvalidArgument, path + ": Groesse passt nicht zum Manifest.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, path + ": " + exception.Message);
        }
    }

    private static ResultErr CopyExactly(FileStream stream, string path, long size, Stream target, CancellationToken cancellation)
    {
        byte[] buffer = new byte[BufferBytes];
        for (long left = size; left > 0;)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) throw new EndOfStreamException(path + " ist unvollstaendig.");
            target.Write(buffer, 0, read);
            left -= read;
        }
        return ResultErr.CreateSuccess();
    }
}
