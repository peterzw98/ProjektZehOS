using System;
using System.Globalization;

namespace ProjektZeh.Network;

internal static class Ipv4Configuration
{
    public static byte[] ParseAddress(string text)
    {
        string[] parts = text.Split('.');
        if (parts.Length != 4) throw new ArgumentException("IPv4 muss aus vier dezimalen Oktetten bestehen: " + text);
        byte[] bytes = new byte[4];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length is < 1 or > 3 || !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
                throw new ArgumentException("Ungueltige IPv4-Adresse: " + text);
        return bytes;
    }

    public static void Validate(string[] arguments)
    {
        if (arguments.Length != 4) throw new ArgumentException("Vier IPv4-Adressen erforderlich.");
        byte[][] addresses = new byte[4][];
        for (int i = 0; i < addresses.Length; i++) addresses[i] = ParseAddress(arguments[i]);
        uint mask = ((uint)addresses[1][0] << 24) | ((uint)addresses[1][1] << 16) | ((uint)addresses[1][2] << 8) | addresses[1][3];
        uint inverted = ~mask;
        if (mask == 0 || (inverted & (inverted + 1)) != 0) throw new ArgumentException("Subnetzmaske muss zusammenhaengend sein.");
        for (int i = 0; i < addresses.Length; i++)
        {
            if (i == 1) continue;
            var address = addresses[i];
            if (address[0] == 0 || address[0] >= 224 || address[0] == 127)
                throw new ArgumentException("IPv4, Gateway und DNS muessen Unicast-Adressen sein.");
        }
    }
}
