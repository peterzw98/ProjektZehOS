using System;
using System.IO;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>Plattformunabhaengiger Teil des lesenden Paketzugriffs; die nativen Hostfunktionen in WasmRuntime rufen ihn auf.</summary>
internal static class WasmPackagePaths
{
    internal const int Success = 0;
    internal const int AccessDenied = 2; // WASI EACCES
    internal const int BadFile = 8; // WASI EBADF
    internal const int Invalid = 28; // WASI EINVAL
    internal const int Io = 29; // WASI EIO
    internal const int TooManyFiles = 33; // WASI ENFILE
    internal const int NameTooLong = 37; // WASI ENAMETOOLONG
    internal const int NotFound = 44; // WASI ENOENT
    internal const int NotDirectory = 54; // WASI ENOTDIR
    internal const int MaximumNameBytes = 255;
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

    /// <summary>
    /// Wie <see cref="Resolve"/>, prueft zusaetzlich jede vorhandene Pfadkomponente: Verweise und ReparsePoints (auch in der Mitte des
    /// Pfads) werden abgewiesen, Zwischenglieder muessen Ordner sein, Namen und der zusammengesetzte Pfad sind hoechstens 255 Byte lang. Wirft
    /// <see cref="UnauthorizedAccessException"/>, <see cref="FileNotFoundException"/> oder <see cref="PathTooLongException"/>.
    /// </summary>
    internal static string ResolveChecked(string root, string relative)
    {
        string full = Resolve(root, relative);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full == root) return full;
        string relativePath = full[(root.Length + 1)..];
        // Der zusammengesetzte Pfad (Ordnerpfad + / + Name) darf ebenfalls hoechstens 255 Byte lang sein.
        if (System.Text.Encoding.UTF8.GetByteCount(relativePath) > MaximumNameBytes) throw new PathTooLongException();
        string[] parts = relativePath.Split(Path.DirectorySeparatorChar);
        string current = root;
        for (int index = 0; index < parts.Length; index++)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(parts[index]) > MaximumNameBytes) throw new PathTooLongException();
            current = Path.Combine(current, parts[index]);
            FileAttributes attributes = StorageLock.Run(() => File.GetAttributes(current));
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Verweis im Pfad.");
            if (index < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0) throw new NotADirectoryException();
        }
        return full;
    }

    /// <summary>Ein Zwischenglied des Pfads ist kein Ordner (WASI ENOTDIR).</summary>
    internal sealed class NotADirectoryException : IOException;

    /// <summary>Haengt <paramref name="path"/> an den Ordnerpfad (relativ zur Wurzel); absolute Pfade sind unzulaessig.</summary>
    internal static string Join(string directory, string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path)) throw new UnauthorizedAccessException("Absoluter Pfad.");
        return directory.Length == 0 ? path : directory + "/" + path;
    }

    private static int Map(Exception exception) => exception switch
    {
        NotADirectoryException => NotDirectory,
        PathTooLongException => NameTooLong,
        FileNotFoundException or DirectoryNotFoundException => NotFound,
        UnauthorizedAccessException => AccessDenied,
        _ => Io,
    };

    /// <summary>
    /// Listet den Ordner <paramref name="relative"/> (Eintraege nach Ordinal sortiert, ohne ReparsePoints). Liefert einen
    /// WASI-Fehlercode; wirft nie.
    /// </summary>
    internal static int List(string? root, string relative, out (string Name, bool Directory)[] entries)
    {
        entries = [];
        try
        {
            if (root == null) return AccessDenied;
            string full = ResolveChecked(root, relative);
            var result = new System.Collections.Generic.List<(string Name, bool Directory)>();
            int status = StorageLock.Run(() =>
            {
                if ((File.GetAttributes(full) & FileAttributes.Directory) == 0) return NotDirectory;
                foreach (FileSystemInfo info in new DirectoryInfo(full).EnumerateFileSystemInfos())
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    result.Add((info.Name, (info.Attributes & FileAttributes.Directory) != 0));
                }
                return Success;
            });
            if (status != Success) return status;
            result.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            entries = result.ToArray();
            return Success;
        }
        catch (Exception exception) { return Map(exception); }
    }

    /// <summary>Liefert einen WASI-Fehlercode; wirft nie, weil der Aufrufer an der nativen Grenze steht.</summary>
    internal static int Stat(string? root, string relative, out ulong size, out int type)
    {
        size = 0;
        type = 0;
        try
        {
            if (root == null) return AccessDenied;
            string full = ResolveChecked(root, relative);
            FileAttributes attributes = StorageLock.Run(() => File.GetAttributes(full));
            if ((attributes & FileAttributes.ReparsePoint) != 0) return AccessDenied;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            size = directory ? 0 : (ulong)StorageLock.Run(() => new FileInfo(full).Length);
            type = directory ? TypeDirectory : TypeFile;
            return Success;
        }
        catch (Exception exception) { return Map(exception); }
    }
}
