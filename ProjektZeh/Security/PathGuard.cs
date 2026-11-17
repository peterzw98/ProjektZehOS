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
        Func<string, string, PathNodeKind> lookup, bool creating) => throw new NotImplementedException();

    /// <summary>
    /// Einhaengepunkt, der <paramref name="path"/> abdeckt, nach derselben Regel wie Cosmos VfsManager.FindMount/MountCovers
    /// (efcf20c VfsManager.cs:645/671): Ordinal, Segmentgrenze, laengster Treffer, "/" deckt alles; null ohne Treffer.
    /// </summary>
    public static string? MountOf(IReadOnlyList<string> mountPoints, string path) => throw new NotImplementedException();
}
