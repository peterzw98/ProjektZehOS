using System;
using System.Collections.Generic;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Macht aus einem Pfad des Programms (Zeichenkette im Benutzerspeicher, relativ zu cwd oder einem Verzeichnis-fd) den absoluten,
/// normalisierten Pfad, den <see cref="ILinuxFileSystem"/> erwartet. Gemeinsam für alle Dateiaufrufe, damit open, stat, mkdir und
/// die übrigen Pfade gleich behandeln.
/// </summary>
/// <remarks>
/// ".." wird hier rein textuell aufgelöst, bevor das Dateisystem Symlinks sieht (Abweichung von Linux, steht in docs/LINUX_ABI.md):
/// "link/.." ergibt das Verzeichnis, in dem der Link liegt, nicht den Elternordner des Linkziels. Dafür kann kein Pfad des Programms
/// über die Wurzel hinaus; ".." an der Wurzel bleibt die Wurzel wie bei chroot.
/// Ein abschließendes "/" (auch aus "." oder ".." am Ende) bleibt erhalten: Das Dateisystem verlangt dann ein Verzeichnis.
/// </remarks>
internal static class LinuxPath
{
    /// <summary>AT_FDCWD: relativ zum Arbeitsverzeichnis.</summary>
    public const int AtFdcwd = -100;

    /// <summary>PATH_MAX einschließlich NUL.</summary>
    public const int PathMax = 4096;

    private const long ENOENT = 2, EBADF = 9, EFAULT = 14, ENOTDIR = 20, ENAMETOOLONG = 36;

    /// <summary>Liest den Pfad bei <paramref name="ptr"/> und löst ihn auf. 0 oder -errno (EFAULT, ENAMETOOLONG, ENOENT, EBADF, ENOTDIR).</summary>
    public static long Resolve(ILinuxProcess p, ILinuxFiles f, int dirfd, ulong ptr, out string path)
    {
        path = string.Empty;
        long r = ReadPath(p, ptr, out string raw);
        return r < 0 ? r : Resolve(p, f, dirfd, raw, out path);
    }

    /// <summary>Wie oben, mit schon gelesener Zeichenkette.</summary>
    public static long Resolve(ILinuxProcess p, ILinuxFiles f, int dirfd, string raw, out string path)
    {
        path = string.Empty;
        if (raw.Length == 0)
        {
            return -ENOENT; // ohne AT_EMPTY_PATH; das prüft der Aufrufer vorher
        }

        string dir = f.Cwd;
        if (raw[0] != '/' && dirfd != AtFdcwd)
        {
            // Wie Linux: dirfd zählt nur bei relativen Pfaden, ein ungültiger dirfd bei absolutem Pfad stört nicht.
            ILinuxDescriptor? d = p.GetDescriptor(dirfd);
            if (d is null)
            {
                return -EBADF;
            }

            if (d is not ILinuxFile file || !file.IsDirectory)
            {
                return -ENOTDIR;
            }

            dir = file.Path;
        }

        path = Normalize(raw[0] == '/' ? raw : dir + "/" + raw);
        return 0;
    }

    /// <summary>
    /// Pfad eines Kernelpfads im Prozess mit der Wurzel <paramref name="root"/> (V12, /proc/self/exe): "/data/app/bin/js" mit Wurzel
    /// "/data/app" wird "/bin/js". null, wenn der Pfad nicht unter der Wurzel liegt oder keine Wurzel auf der Platte besteht. Die
    /// Datenplatte ist FAT, Groß-/Kleinschreibung zählt beim Vergleich deshalb nicht.
    /// </summary>
    public static string? InsideRoot(string kernelPath, string? root)
    {
        if (root is null || kernelPath.Length == 0)
        {
            return null;
        }

        string r = root.Replace('\\', '/').TrimEnd('/');
        string k = kernelPath.Replace('\\', '/');
        if (r.Length == 0 || k.Length <= r.Length + 1 || k[r.Length] != '/' || !k.StartsWith(r, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Normalize(k.Substring(r.Length));
    }

    /// <summary>Liest eine Pfadzeichenkette; -ENAMETOOLONG, wenn in PATH_MAX Bytes kein NUL steht, sonst -EFAULT.</summary>
    public static long ReadPath(ILinuxProcess p, ulong ptr, out string raw)
    {
        if (p.Memory.TryReadCString(ptr, PathMax - 1, out raw))
        {
            return 0;
        }

        // Lesbar, aber zu lang? Die ersten PATH_MAX Bytes lesen; gelingt das, fehlt nur das NUL.
        raw = string.Empty;
        byte[] probe = new byte[PathMax];
        return p.Memory.TryRead(ptr, probe) && Array.IndexOf(probe, (byte)0) < 0 ? -ENAMETOOLONG : -EFAULT;
    }

    /// <summary>
    /// Entfernt leere Teile und ".", löst ".." textuell auf (nie über "/" hinaus). Endet der Eingabepfad mit "/", "." oder "..",
    /// endet das Ergebnis mit "/" (außer bei der Wurzel selbst).
    /// </summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        bool trailingDir = false;
        foreach (string part in path.Split('/'))
        {
            trailingDir = part.Length == 0 || part == "." || part == "..";
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            return "/";
        }

        string joined = "/" + string.Join('/', parts);
        return trailingDir ? joined + "/" : joined;
    }
}
