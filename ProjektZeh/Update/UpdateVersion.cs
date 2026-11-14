using System;
using System.Globalization;

namespace ProjektZeh.Update;

internal readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (text == null) return false;
        string[] parts = text.Split('.');
        if (parts.Length != 3 || !ParsePart(parts[0], out int major) ||
            !ParsePart(parts[1], out int minor) || !ParsePart(parts[2], out int patch)) return false;
        version = new UpdateVersion(major, minor, patch);
        return true;
    }

    private static bool ParsePart(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) return false;
        foreach (char c in text) if (c < '0' || c > '9') return false;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(UpdateVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
