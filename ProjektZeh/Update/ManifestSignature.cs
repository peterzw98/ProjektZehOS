using System;
using System.IO;
using System.Text;
using ProjektZeh.Packages;

namespace ProjektZeh.Update;

internal static class ManifestSignature
{
    internal const int SignatureBytes = 64;
    private static readonly byte[] ProductionKey = Convert.FromBase64String("eebr5IjF0GjyGoAUrBhwUDUx5STOMTsAhfrq2af5s+Y=");
#if ZEHOS_UPDATE_TESTKEY
    internal const string BuildLabel = " [UPDATE-TESTSCHLUESSEL]";
    private static readonly PackageTrust? Trust = FromTestBuild();

    private static PackageTrust? FromTestBuild()
    {
        using Stream? stream = typeof(ManifestSignature).Assembly.GetManifestResourceStream("ZehOS.UpdateTestKey");
        return ReadTestTrust(stream);
    }
#else
    internal const string BuildLabel = "";
    private static readonly PackageTrust Trust = new(ProductionKey);
#endif

    public static bool Verify(byte[] manifest, byte[] signature) => Trust?.Verify(manifest, signature) == true;

#if ZEHOS_UPDATE_TESTKEY || ZEHOS_UPDATE_TESTS
    internal static PackageTrust? ReadTestTrust(Stream? stream)
    {
        // Ein kaputter Testschluessel darf niemals auf das Produktionsvertrauen zurueckfallen.
        try
        {
            if (stream == null || stream.Length is <= 0 or > 256) return null;
            byte[] text = new byte[stream.Length];
            stream.ReadExactly(text);
            byte[] key = Convert.FromBase64String(Encoding.ASCII.GetString(text).Trim());
            if (key.AsSpan().SequenceEqual(ProductionKey)) return null;
            return new PackageTrust(key);
        }
        catch (Exception exception) when (exception is IOException or FormatException or ArgumentException) { return null; }
    }
#endif
}
