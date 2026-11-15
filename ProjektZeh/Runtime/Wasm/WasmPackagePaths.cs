using System;
using System.IO;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>Plattformunabhaengiger Teil des lesenden Paketzugriffs; die nativen Hostfunktionen in WasmRuntime rufen ihn auf.</summary>
internal static class WasmPackagePaths
{
    internal const int Success = 0;
    internal const int AccessDenied = 2; // WASI EACCES
    internal const int Io = 29; // WASI EIO
    internal const int NotFound = 44; // WASI ENOENT
    internal const int TypeDirectory = 3; // WASI filetype directory
    internal const int TypeFile = 4; // WASI filetype regular_file

    // C prueft die relativen Komponenten; dieser gemeinsame Schritt sichert den Bezug zum Paketordner.
    internal static string Resolve(string root, string relative)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Pfad ausserhalb des Pakets.");
        return full;
    }

    /// <summary>Liefert einen WASI-Fehlercode; wirft nie, weil der Aufrufer an der nativen Grenze steht.</summary>
    internal static int Stat(string? root, string relative, out ulong size, out int type)
    {
        size = 0;
        type = 0;
        try
        {
            if (root == null) return AccessDenied;
            string full = Resolve(root, relative);
            FileAttributes attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return AccessDenied;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            size = directory ? 0 : (ulong)new FileInfo(full).Length;
            type = directory ? TypeDirectory : TypeFile;
            return Success;
        }
        catch (FileNotFoundException) { return NotFound; }
        catch (DirectoryNotFoundException) { return NotFound; }
        catch (UnauthorizedAccessException) { return AccessDenied; }
        catch (Exception) { return Io; }
    }
}
