using System;
using System.Globalization;

namespace ProjektZeh.Update;

internal sealed record UpdateEndpoint(string Host, int Port)
{
    public const string DefaultSource = "10.0.2.2:8080";

    public static bool TryParse(string text, out UpdateEndpoint? endpoint)
    {
        endpoint = null;
        string[] parts = text.Split(':');
        if (parts.Length is < 1 or > 2 || parts[0].Length is < 1 or > 253) return false;
        foreach (string label in parts[0].Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            foreach (char c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        }
        int port = 80;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) return false;
        endpoint = new(parts[0], port);
        return true;
    }

    public override string ToString() => Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
}
