#if ZEHOS_SECURITY_PROBE
using System;
using System.Security.Cryptography;
using Cosmos.Kernel.System.Diagnostics;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using ProjektZeh.Security;

namespace ProjektZeh.Runtime;

internal static class CosmosSecurityProbe
{
    public static bool Run()
    {
        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(32);
            byte[] other = RandomNumberGenerator.GetBytes(32);
            CosmosSaltSource.Fill(salt);
            CosmosSaltSource.Fill(other);
            if (CryptographicOperations.FixedTimeEquals(salt, other)) throw new InvalidOperationException("Zufallswerte identisch.");
            Log.WriteString("SECURITY_PROBE RNG completed\n");
            var generator = new Pkcs5S2ParametersGenerator(new Sha256Digest());
            generator.Init("password"u8.ToArray(), "salt"u8.ToArray(), 1);
            byte[] hash = ((KeyParameter)generator.GenerateDerivedMacParameters(256)).GetKey();
            if (Convert.ToHexString(hash) != "120FB6CFFCF8B32C43E7225256C4F837A86548C92CCC35480805987CB70BE17B") throw new InvalidOperationException("PBKDF2 Testvektor falsch.");
            Log.WriteString("SECURITY_PROBE PASS PBKDF2-SHA256 vector and RNG\n");
            return true;
        }
        catch (Exception exception)
        {
            Log.WriteString($"SECURITY_PROBE FAIL {exception}\n");
            return false;
        }
    }
}
#endif
