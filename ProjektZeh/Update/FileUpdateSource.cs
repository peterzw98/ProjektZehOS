using System;
using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Update;

/// <summary>Offlinepakete enthalten Manifest, Signatur und Kernel im selben Verzeichnis.</summary>
internal sealed class FileUpdateSource(string directory) : IUpdateSource
{
    public ResultObject<byte[]> Read(string name, int maxBytes)
    {
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || name is "." or "..")
            return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Ungueltiger Update-Dateiname.");
        try
        {
            using var stream = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > maxBytes)
                return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Update-Datei ist leer oder zu gross: " + name);
            byte[] data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
            return ResultObjectFactory.CreateSuccess(data);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<byte[]>(exception); }
    }
}
