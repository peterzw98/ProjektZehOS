using System;
using System.IO;
using ProjektZeh.Shared;

namespace ProjektZeh.Update;

/// <summary>Offlinepakete enthalten Manifest, Signatur und Kernel im selben Verzeichnis.</summary>
internal sealed class FileUpdateSource(string directory) : IUpdateSource
{
    public ResultObject<byte[]> Read(string name)
    {
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || name is "." or "..")
            return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Ungueltiger Update-Dateiname.");
        int limit = name == "update-manifest.txt" ? 4096 : name == "update-manifest.sig" ? 64 : 64 * 1024 * 1024;
        try
        {
            using var stream = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > limit)
                return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Update-Datei ist leer oder zu gross: " + name);
            byte[] data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
            return ResultObjectFactory.CreateSuccess(data);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ResultObjectFactory.CreateFromException<byte[]>(exception); }
    }
}
