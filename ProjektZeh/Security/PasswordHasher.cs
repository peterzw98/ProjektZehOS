using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace ProjektZeh.Security;

internal sealed class PasswordHasher(Action<byte[]> fillSalt)
{
    public const int Iterations = 600000;

    public string Hash(string password)
    {
        if (password.Length is < 12 or > 128) throw new ArgumentException("Kennwort muss 12 bis 128 Zeichen enthalten.");
        var salt = new byte[16];
        fillSalt(salt);
        byte[] derived = Derive(password, salt, Iterations);
        string encoded = $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}";
        CryptographicOperations.ZeroMemory(derived);
        return encoded;
    }

    public static bool Verify(string password, string encoded)
    {
        if (password.Length > 128 || !IsValid(encoded)) return false;
        string[] parts = encoded.Split('$');
        byte[] expected = Convert.FromBase64String(parts[3]);
        byte[] actual = Derive(password, Convert.FromBase64String(parts[2]), Iterations);
        bool matches = CryptographicOperations.FixedTimeEquals(expected, actual);
        CryptographicOperations.ZeroMemory(actual);
        return matches;
    }

    public static bool IsValid(string encoded)
    {
        try
        {
            string[] parts = encoded.Split('$');
            return parts.Length == 4 && parts[0] == "pbkdf2-sha256" && parts[1] == "600000" &&
                Convert.FromBase64String(parts[2]).Length == 16 && Convert.FromBase64String(parts[3]).Length == 32;
        }
        catch (FormatException) { return false; }
    }

    internal static byte[] Derive(string password, byte[] salt, int iterations) =>
        DeriveAndClear(Encoding.UTF8.GetBytes(password), salt, iterations);

    internal static byte[] DeriveAndClear(byte[] bytes, byte[] salt, int iterations)
    {
        try
        {
            var generator = new Pkcs5S2ParametersGenerator(new Sha256Digest());
            generator.Init(bytes, salt, iterations);
            byte[] result = ((KeyParameter)generator.GenerateDerivedMacParameters(256)).GetKey();
            CryptographicOperations.ZeroMemory(bytes);
            return result;
        }
        catch
        {
            // Cosmos 3.0.94 fuehrt verschachteltes finally bei Ausnahmen nicht zuverlaessig aus.
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }
}
