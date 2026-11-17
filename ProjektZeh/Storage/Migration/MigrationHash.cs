using System;
using System.IO;
using Org.BouncyCastle.Crypto.Digests;

namespace ProjektZeh.Storage.Migration;

/// <summary>Wie beim Paketbestand BouncyCastle verwenden, ohne einen Betriebssystem-Hashanbieter vorauszusetzen.</summary>
internal static class MigrationHash
{
    public static string Hex(byte[] bytes)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(bytes, 0, bytes.Length);
        return Finish(digest);
    }

    public static string Hex(Stream stream)
    {
        var digest = new Sha256Digest();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) digest.BlockUpdate(buffer, 0, read);
        return Finish(digest);
    }

    private static string Finish(Sha256Digest digest)
    {
        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return Convert.ToHexStringLower(hash);
    }
}
