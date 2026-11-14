using System;

namespace ProjektZeh.Network;

internal static class NetworkTarget
{
    public static void ValidateHost(string host)
    {
        if (host.Length is < 1 or > 253) throw new ArgumentException("Ungueltiger Hostname.");
        bool numeric = true;
        foreach (char c in host)
            if (!char.IsAsciiDigit(c) && c != '.') { numeric = false; break; }
        if (numeric)
        {
            Ipv4Configuration.ParseAddress(host);
            return;
        }
        foreach (string label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-')
                throw new ArgumentException("Ungueltiger Hostname.");
            foreach (char c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') throw new ArgumentException("Hostname muss aus ASCII-DNS-Zeichen bestehen.");
        }
    }
}
