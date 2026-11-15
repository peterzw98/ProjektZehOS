using System;
using System.IO;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace ProjektZeh.Packages;

/// <summary>
/// Eigener Ed25519-Vertrauensschluessel fuer Pakete, getrennt vom Kernelupdate-Schluessel. Ein normaler Build enthaelt nur
/// dann einen Schluessel, wenn er ausdruecklich mit -p:ZehOSPackageKeyPath gebaut wurde.
/// </summary>
internal sealed class PackageTrust
{
    internal const int SignatureBytes = 64;
    private readonly Ed25519PublicKeyParameters key;

    public PackageTrust(byte[] publicKey)
    {
        if (publicKey.Length != Ed25519PublicKeyParameters.KeySize) throw new ArgumentException("Paketschluessel muss 32 Bytes haben.");
        key = new Ed25519PublicKeyParameters(publicKey, 0);
    }

    public bool Verify(byte[] content, byte[] signature)
    {
        if (signature.Length != SignatureBytes) return false;
        var verifier = new Ed25519Signer();
        verifier.Init(false, key);
        verifier.BlockUpdate(content, 0, content.Length);
        return verifier.VerifySignature(signature);
    }

    /// <summary>Liest den eingebetteten oeffentlichen Schluessel (Base64-Text); ohne Ressource gibt es keine Paketinstallation.</summary>
    public static PackageTrust? FromBuild()
    {
        using Stream? stream = typeof(PackageTrust).Assembly.GetManifestResourceStream("ZehOS.PackageKey");
        if (stream == null || stream.Length is <= 0 or > 256) return null;
        byte[] text = new byte[stream.Length];
        stream.ReadExactly(text);
        // Ein falsch eingebetteter Schluessel deaktiviert Pakete, statt den Boot abzubrechen.
        try { return new PackageTrust(Convert.FromBase64String(Encoding.ASCII.GetString(text).Trim())); }
        catch (Exception exception) when (exception is FormatException or ArgumentException) { return null; }
    }

    public static string Sha256Hex(byte[] bytes)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(bytes, 0, bytes.Length);
        byte[] hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return Convert.ToHexStringLower(hash);
    }
}
