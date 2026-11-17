using System;

namespace ProjektZeh.Security;

/// <summary>
/// Regel fuer einen einzelnen Pfadteil, gemeinsam fuer die Rechteverwaltung und die Migration (Etappe 4.4). Reine
/// Zeichenpruefung ohne Dateisystem; Symlinks und Mountgrenzen prueft erst der Pfadwaechter beim Zugriff.
/// </summary>
internal static class PathComponent
{
    /// <summary>
    /// Nutzerpfade sperren zusaetzlich das .zehos--Praefix (Systemdateien); <paramref name="allowSystemNames"/> erlaubt es fuer
    /// Ziele, die nur Systemdienste schreiben, etwa /etc/zehos/.zehos-security.db.
    /// </summary>
    public static bool IsValid(string part, bool allowSystemNames = false) =>
        part.Length != 0 && part != "." && part != ".." && !part.EndsWith('.') && !part.EndsWith(' ') &&
        (allowSystemNames || !part.StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase)) &&
        part.IndexOfAny(['|', '\r', '\n', ':', '\\', '/', '\0']) < 0;
}
