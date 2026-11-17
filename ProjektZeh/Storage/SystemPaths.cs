using System;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

internal enum PathProfile { Legacy, Root }

/// <summary>
/// Etappe 4.1: Alle festen Systempfade je Profil an einer Stelle (Spec 2026-11-17-storage-etappe4-design.md, Abschnitt 1).
/// <see cref="Legacy"/> liefert exakt die heutigen Pfade unter /data; <see cref="Root"/> den Linux-aehnlichen Baum auf ext2.
/// Das Profil wird einmal beim Boot gewaehlt und nie gewechselt; es gibt keine Konfiguration und keinen globalen Zustand.
/// Pfade verwenden immer '/' (Kernelpfade), unabhaengig vom Host.
/// </summary>
internal sealed class SystemPaths
{
    public static SystemPaths Legacy { get; } = new(PathProfile.Legacy, "/data", StringComparison.OrdinalIgnoreCase, "/data", "/data/home",
        "/data/.zehos-packages", "/data/.zehos-update.conf", "/data", "/data", "/data", "/data/.zehos-apps");

    public static SystemPaths Root { get; } = new(PathProfile.Root, "/", StringComparison.Ordinal, "/etc/zehos", "/home",
        "/var/lib/zehos/packages", "/etc/zehos/update.conf", "/boot/efi", "/", "/mnt/install", "/var/lib/zehos/apps");

    private SystemPaths(PathProfile profile, string adminRoot, StringComparison comparison, string securityDirectory, string homeBase,
        string packagesDirectory, string updateConfigPath, string updateRoot, string startDirectory, string installMount, string appStateBase)
    {
        Profile = profile;
        AdminRoot = adminRoot;
        Comparison = comparison;
        SecurityDirectory = securityDirectory;
        HomeBase = homeBase;
        PackagesDirectory = packagesDirectory;
        UpdateConfigPath = updateConfigPath;
        UpdateRoot = updateRoot;
        StartDirectory = startDirectory;
        InstallMount = installMount;
        appStateBase = appStateBase.TrimEnd('/');
        this.appStateBase = appStateBase;
    }

    private readonly string appStateBase;

    public PathProfile Profile { get; }
    /// <summary>Verwaltungswurzel der ACLs.</summary>
    public string AdminRoot { get; }
    /// <summary>Pfadvergleich der Verwaltungswurzel: ext2 Ordinal, FAT OrdinalIgnoreCase.</summary>
    public StringComparison Comparison { get; }
    /// <summary>Ordner von Konten-, Befehlsrechte-Datei und Recovery-Kopien (Dateinamen bleiben beim AccountService).</summary>
    public string SecurityDirectory { get; }
    public string HomeBase { get; }
    public string PackagesDirectory { get; }
    public string UpdateConfigPath { get; }
    /// <summary>Wurzel der ESP-Pfade fuer Kernel, Version und Rueckfall (boot/...).</summary>
    public string UpdateRoot { get; }
    public string StartDirectory { get; }
    /// <summary>Vorlaeufiger Einhaengepunkt des Installationsziels.</summary>
    public string InstallMount { get; }

    public string Home(string userName) => HomeBase + "/" + userName;

    /// <summary>Datei der Desktopeinstellungen eines Kontos.</summary>
    public string DesktopSettings(string home) => Profile == PathProfile.Legacy ? home.TrimEnd('/') + "/zehos-desktop.conf" : home.TrimEnd('/') + "/.config/zehos/desktop.conf";

    /// <summary>Vorgeschlagener Hintergrundpfad im Einstellungsfeld (nur Vorschlagstext).</summary>
    public string DefaultBackground(string home) => Profile == PathProfile.Legacy ? "/data/hintergrund.bmp" : home.TrimEnd('/') + "/hintergrund.bmp";

    /// <summary>Orte im Dateimanager: Arbeitsverzeichnis und je Profil /data bzw. das Home.</summary>
    public string[] Places(string workingDirectory, string home) => [workingDirectory, Profile == PathProfile.Legacy ? "/data" : home];

    /// <summary>Zustandsordner einer App je Konto; Komponenten werden als einzelne Pfadteile geprueft.</summary>
    public ResultObject<string> AppState(string packageId, string accountId)
    {
        if (!IsComponent(packageId) || !IsComponent(accountId))
            return ResultObjectFactory.CreateFailure<string>(ResultCode.InvalidArgument, "Ungueltige Paket- oder Konto-ID fuer den App-Speicher.");
        return ResultObjectFactory.CreateSuccess(appStateBase + "/" + packageId + "/" + accountId);
    }

    // Keine Trenner, keine Punktkomponenten, keine Steuerzeichen; das .zehos--Praefix bleibt Systemdateien vorbehalten.
    private static bool IsComponent(string value)
    {
        if (string.IsNullOrEmpty(value) || value == "." || value == ".." || value.StartsWith(".zehos-", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (char c in value)
            if (c <= ' ' || c == '/' || c == '\\' || c == ':' || c == '|' || char.IsControl(c)) return false;
        return !value.EndsWith('.');
    }
}
