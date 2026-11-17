using System;
using System.Collections.Generic;
using ProjektZeh.Shared;

namespace ProjektZeh.Security;

/// <summary>Art eines Verzeichniseintrags, wie ihn eine Abfrage OHNE Folgen von Symlinks liefert.</summary>
internal enum PathNodeKind
{
    Missing,
    File,
    Directory,
    Symlink,
}

/// <summary>
/// Etappe 3: Pruefung der Pfadidentitaet vor jedem Dateizugriff, ohne Cosmos-Abhaengigkeit. Cosmos 3.0.97 bietet keine
/// sichere Aufloesung (VfsManager.TryResolve privat, folgt Symlinks in jedem Segment, kein lstat), deshalb fail-closed:
/// jede Komponente wird ohne Folgen abgefragt, jeder Symlink und jede Komponente ausserhalb des Mounts der
/// Verwaltungswurzel fuehrt zur Ablehnung. Pfade sind VFS-Pfade mit '/' als Trenner.
/// </summary>
internal static class PathGuard
{
    /// <summary>
    /// Prueft <paramref name="path"/> unterhalb von <paramref name="root"/>. <paramref name="lookup"/> fragt einen Eintrag
    /// (Elternpfad, Name) ohne Folgen ab. Fehlende Komponenten: nur das Blatt und nur bei <paramref name="creating"/> erlaubt.
    /// </summary>
    public static ResultErr Check(string root, string path, IReadOnlyList<string> mountPoints,
        Func<string, string, PathNodeKind> lookup, bool creating)
    {
        // Die Wurzel muss selbst ein Einhaengepunkt sein: ein Mount-Wurzelverzeichnis ist nie ein Symlink.
        if (!root.StartsWith('/') || !string.Equals(MountOf(mountPoints, root), root, StringComparison.Ordinal))
            return Denied("RootNotMountPoint", "Die Verwaltungswurzel ist kein Einhaengepunkt.");
        if (!string.Equals(MountOf(mountPoints, path), root, StringComparison.Ordinal))
            return Denied("ForeignMount", "Der Pfad liegt in einem anderen Einhaengepunkt.");
        if (string.Equals(path, root, StringComparison.Ordinal)) return ResultErr.CreateSuccess();

        string prefix = root == "/" ? "/" : root + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return Denied("OutsideRoot", "Der Pfad liegt ausserhalb der Verwaltungswurzel.");
        string[] parts = path[prefix.Length..].Split('/');
        string parent = root;
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0 || part == "." || part == "..")
                return Denied("InvalidComponent", "Leere, '.'- oder '..'-Komponente.");
            bool leaf = index == parts.Length - 1;
            switch (lookup(parent, part))
            {
                case PathNodeKind.Symlink:
                    // Cosmos bietet keine sichere Aufloesung: jeder Symlink im Pfad wird verweigert.
                    return Denied("Symlink", $"Symlink im Pfad: {part}");
                case PathNodeKind.Missing when leaf && creating:
                    return ResultErr.CreateSuccess();
                case PathNodeKind.Missing:
                    return Denied("Missing", $"Pfadkomponente fehlt: {part}");
                case PathNodeKind.File when !leaf:
                    return Denied("NotDirectory", $"Keine Verzeichniskomponente: {part}");
            }

            parent = parent == "/" ? "/" + part : parent + "/" + part;
        }

        return ResultErr.CreateSuccess();
    }

    /// <summary>
    /// Einhaengepunkt, der <paramref name="path"/> abdeckt, nach derselben Regel wie Cosmos VfsManager.FindMount/MountCovers
    /// (efcf20c VfsManager.cs:645/671): Ordinal, Segmentgrenze, laengster Treffer, "/" deckt alles; null ohne Treffer.
    /// </summary>
    public static string? MountOf(IReadOnlyList<string> mountPoints, string path)
    {
        string? best = null;
        foreach (string mountPoint in mountPoints)
        {
            bool covers = mountPoint == "/" || string.Equals(path, mountPoint, StringComparison.Ordinal) ||
                path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
            if (covers && (best == null || mountPoint.Length > best.Length)) best = mountPoint;
        }

        return best;
    }

    private static ResultErr Denied(string cause, string message) =>
        ResultErr.CreateFailure(ResultCode.Failure, $"cause='{cause}' message='{message}'");
}
