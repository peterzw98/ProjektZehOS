using System;
using System.IO;

namespace ProjektZeh.Network;

internal static class TlsRoots
{
    internal static byte[] Load() => Read("ZehOS.TlsRoots");

    internal static byte[] Read(string resourceName)
    {
        Stream? stream = typeof(TlsRoots).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) throw new InvalidOperationException("TLS-Zertifikatbundle fehlt: " + resourceName);
        Exception? failure = null;
        byte[]? result = null;
        try
        {
            if (stream.Length is <= 0 or > 262144) throw new InvalidOperationException("Ungueltige Groesse des TLS-Zertifikatbundles.");
            result = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < result.Length)
            {
                int count = stream.Read(result, offset, result.Length - offset);
                if (count <= 0) throw new EndOfStreamException("TLS-Zertifikatbundle unvollstaendig.");
                offset += count;
            }
        }
        catch (Exception exception) { failure = exception; }
        stream.Dispose();
        if (failure != null) throw failure;
        return result!;
    }
}
