using System;
using System.Collections.Generic;
using ProjektZeh.Security;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Virtual;

internal enum VirtualKind
{
    Directory,
    File,
    CharacterDevice,
}

/// <summary>Ein fester Knoten der Baeume /proc, /sys und /dev (Spec E5b/E6b §1); <paramref name="Mode"/> sind die Rechtebits ohne Typ.</summary>
internal sealed record VirtualNode(string Path, VirtualKind Kind, int Mode, bool AdministratorOnly = false);

/// <summary>
/// Reine Pfadlogik der virtuellen Baeume (Etappe 5b/6b, Paket E5b.1); keine VFS-Anbindung.
/// Pfade werden selbst und ohne System.IO normalisiert, damit Host (auch Windows) und Kernel dasselbe Ergebnis liefern.
/// </summary>
internal static class VirtualTree
{
    public static IReadOnlyList<string> Roots { get; } = Array.AsReadOnly(new[] { "/dev", "/proc", "/sys" });

    // Modi oktal: 0x16D = 0555, 0x1B6 = 0666, 0x124 = 0444, 0x100 = 0400 (C# kennt keine Oktalliterale).
    private static readonly VirtualNode[] Nodes =
    [
        new("/dev", VirtualKind.Directory, 0x16D),
        new("/dev/null", VirtualKind.CharacterDevice, 0x1B6),
        new("/dev/zero", VirtualKind.CharacterDevice, 0x124),
        new("/proc", VirtualKind.Directory, 0x16D),
        new("/proc/log", VirtualKind.File, 0x100, AdministratorOnly: true),
        new("/proc/meminfo", VirtualKind.File, 0x124),
        new("/proc/mounts", VirtualKind.File, 0x124),
        new("/proc/uptime", VirtualKind.File, 0x124),
        new("/proc/version", VirtualKind.File, 0x124),
        new("/sys", VirtualKind.Directory, 0x16D),
        new("/sys/block", VirtualKind.File, 0x124),
        new("/sys/devices", VirtualKind.File, 0x124),
        new("/sys/kernel", VirtualKind.Directory, 0x16D),
        new("/sys/kernel/components", VirtualKind.File, 0x124),
    ];

    /// <summary>
    /// Absoluter Pfad mit "/" ohne ".", ".." und leere Teile; relative Pfade gelten ab <paramref name="currentDirectory"/>.
    /// ".." ueber der Wurzel bleibt an der Wurzel (wie unter Linux). Backslash ist ein normales Zeichen.
    /// </summary>
    public static string Normalize(string path, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        var parts = new List<string>();
        foreach (string part in ((path.StartsWith('/') ? "" : currentDirectory + "/") + path).Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    /// <summary>Liegt der normalisierte Pfad in einem der drei Baeume (auch ein dort unbekannter Name)?</summary>
    public static bool IsVirtualPath(string normalizedPath)
    {
        foreach (string root in Roots)
        {
            if (string.Equals(normalizedPath, root, StringComparison.Ordinal) ||
                normalizedPath.StartsWith(root + "/", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    public static VirtualNode? Find(string normalizedPath)
    {
        foreach (var node in Nodes)
        {
            if (string.Equals(node.Path, normalizedPath, StringComparison.Ordinal)) return node;
        }

        return null;
    }

    /// <summary>Namen der direkten Kinder, ordinal sortiert; Fehler fuer unbekannte Pfade und Dateien.</summary>
    public static ResultObject<IReadOnlyList<string>> List(string normalizedPath)
    {
        var directory = Find(normalizedPath);
        if (directory is null) return ResultObjectFactory.CreateFailure<IReadOnlyList<string>>(ResultCode.NotFound, "Nicht gefunden.");
        if (directory.Kind != VirtualKind.Directory)
            return ResultObjectFactory.CreateFailure<IReadOnlyList<string>>(ResultCode.InvalidArgument, "Kein Verzeichnis.");
        var names = new List<string>();
        string prefix = normalizedPath + "/";
        foreach (var node in Nodes)
        {
            if (node.Path.StartsWith(prefix, StringComparison.Ordinal) && node.Path.IndexOf('/', prefix.Length) < 0)
                names.Add(node.Path[prefix.Length..]);
        }

        names.Sort(StringComparer.Ordinal);
        return ResultObjectFactory.CreateSuccess<IReadOnlyList<string>>(names.AsReadOnly());
    }
}

/// <summary>Rechte der virtuellen Knoten (Spec §1); jede Mutation ausser Schreiben auf /dev/null wird abgelehnt.</summary>
internal static class VirtualAccess
{
    public static ResultErr Check(VirtualNode node, UserSession session, bool write)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(session);
        if (session.UserId == null) return FileAccessControl.Denied();
        if (write)
        {
            return string.Equals(node.Path, "/dev/null", StringComparison.Ordinal)
                ? ResultErr.CreateSuccess()
                : ResultErr.CreateFailure(ResultCode.Failure, "Nur lesbar.");
        }

        return node.AdministratorOnly && !session.IsAdministrator ? FileAccessControl.Denied() : ResultErr.CreateSuccess();
    }
}
