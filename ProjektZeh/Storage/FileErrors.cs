using System;
using System.IO;

namespace ProjektZeh.Storage;

/// <summary>Erwartete Dateifehler, die Aufrufer als Ergebnis melden statt durchzulassen.</summary>
internal static class FileErrors
{
    // Ein gemeinsamer Filter: Liefert eine Cosmos-Version einen weiteren Ausnahmetyp fuer Dateifehler, reicht eine Stelle.
    internal static bool IsFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
