using System;
using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Update;

/// <summary>Offlinepakete enthalten Manifest, Signatur und Kernel im selben Verzeichnis.</summary>
internal sealed class FileUpdateSource(string directory) : IUpdateSource
{
    private const int ChunkBytes = 1024 * 1024;

    public ResultObject<byte[]> Read(string name, int maxBytes)
    {
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || name is "." or "..")
            return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Ungueltiger Update-Dateiname.");
        try
        {
            var stream = StorageLock.Run(() => new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read));
            try
            {
                long length = StorageLock.Run(() => stream.Length);
                if (length <= 0 || length > maxBytes)
                    return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Update-Datei ist leer oder zu gross: " + name);
                byte[] data = new byte[(int)length];
                // Der Kernel ist mehrere MiB gross: je Block hoechstens 1 MiB unter der Sperre lesen, damit andere Threads
                // zwischen den Bloecken auf die Platte zugreifen koennen.
                for (int offset = 0; offset < data.Length;)
                {
                    int start = offset, count = Math.Min(ChunkBytes, data.Length - offset);
                    StorageLock.Run(() => stream.ReadExactly(data, start, count));
                    offset += count;
                }
                return ResultObjectFactory.CreateSuccess(data);
            }
            finally { StorageLock.Run(() => stream.Dispose()); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ResultObjectFactory.CreateFromException<byte[]>(exception); }
    }
}
