using System;
using System.IO;
using System.Threading;
using ProjektZeh.Network;
using ProjektZeh.Shared;
using ProjektZeh.Update;

namespace ProjektZeh.Packages;

/// <summary>
/// HTTPS-Repository mit vorhandener TLS-Pruefung. Jede Datei ist ein eigener GET ohne Umleitungen; Inhalte prueft
/// erst PackageStore ueber Signatur und SHA-256.
/// </summary>
internal sealed class CosmosHttpsPackageSource(string baseUrl) : IPackageSource
{
    private const int BufferBytes = 64 * 1024;

    public ResultObject<byte[]> Read(string path, int maximumBytes, CancellationToken cancellation)
    {
        try
        {
            return ResultObjectFactory.CreateSuccess(CosmosNetworkService.Download(baseUrl + path, Roots(),
                stream => HttpUpdateResponse.Read(stream, maximumBytes), cancellation));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure, path + ": " + exception.Message);
        }
    }

    public ResultErr Copy(string path, long size, Stream target, CancellationToken cancellation)
    {
        try
        {
            CosmosNetworkService.Download(baseUrl + path, Roots(), stream =>
            {
                if (HttpUpdateResponse.ReadLength(stream, size) != size) throw new IOException("Groesse passt nicht zum Manifest.");
                byte[] buffer = new byte[BufferBytes];
                for (long left = size; left > 0;)
                {
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) throw new EndOfStreamException("Download unvollstaendig.");
                    target.Write(buffer, 0, read);
                    left -= read;
                }
                return true;
            }, cancellation);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, path + ": " + exception.Message);
        }
    }

    // Eine Test-CA gelangt nur ueber -p:ZehOSPackageTestCaPath in einen Build und ergaenzt die normalen Wurzeln.
    private static byte[] Roots()
    {
        byte[] roots = TlsRoots.Load();
        using (Stream? probe = typeof(TlsRoots).Assembly.GetManifestResourceStream("ZehOS.PackageTestCa"))
            if (probe == null) return roots;
        byte[] test = TlsRoots.Read("ZehOS.PackageTestCa");
        byte[] combined = new byte[roots.Length + 1 + test.Length];
        roots.CopyTo(combined, 0);
        combined[roots.Length] = (byte)'\n';
        test.CopyTo(combined, roots.Length + 1);
        return combined;
    }
}
