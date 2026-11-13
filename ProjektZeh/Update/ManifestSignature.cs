using System;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace ProjektZeh.Update;

internal static class ManifestSignature
{
    private static readonly byte[] PublicKey = Convert.FromBase64String("eebr5IjF0GjyGoAUrBhwUDUx5STOMTsAhfrq2af5s+Y=");

    public static bool Verify(byte[] manifest, byte[] signature)
    {
        if (signature.Length != 64) return false;
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(PublicKey, 0));
        verifier.BlockUpdate(manifest, 0, manifest.Length);
        return verifier.VerifySignature(signature);
    }
}
