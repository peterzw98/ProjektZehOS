using System;

namespace ProjektZeh.Storage.Migration;

/// <summary>
/// Kernelpfade mit '/' unabhaengig vom Host. Bewusst ohne Path.GetFullPath und Hosttrenner, damit Plan, ACL-Umschreibung und
/// Journal auf Linux- und Windows-Hosts gleich getestet werden (Spec E4.4 §1).
/// </summary>
internal static class KernelPath
{
    public static string Combine(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    /// <summary>Elternpfad; fuer "/" selbst <see langword="null"/>.</summary>
    public static string? Parent(string path)
    {
        if (path == "/") return null;
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>Absolut, ohne doppelte, Punkt- oder abschliessende Trenner; "/" ist zulaessig.</summary>
    public static bool IsAbsoluteNormalized(string path)
    {
        if (path == "/") return true;
        if (!path.StartsWith('/') || path.EndsWith('/')) return false;
        foreach (string part in path[1..].Split('/'))
            if (part.Length == 0 || part == "." || part == "..") return false;
        return true;
    }

    /// <summary>Ob <paramref name="path"/> gleich <paramref name="directory"/> ist oder darunter liegt.</summary>
    public static bool IsAtOrBelow(string path, string directory, StringComparison comparison) =>
        string.Equals(path, directory, comparison) ||
        path.StartsWith(directory == "/" ? "/" : directory + "/", comparison);
}
