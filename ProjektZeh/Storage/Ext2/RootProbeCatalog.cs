#if ZEHOS_STORAGE_ROOT_PROBE || ZEHOS_HOST_TESTS
using System;
using System.Collections.Generic;

namespace ProjektZeh.Storage.Ext2;

/// <summary>Eine Integritaetsdatei der E4.0-Probe: Inhalt = Generate(Seed, Source, Length), abgelegt unter Path.</summary>
/// <param name="Source">Pfad, unter dem der Inhalt erzeugt wurde; nach einem Umbenennen weicht er von Path ab.</param>
internal sealed record RootProbeEntry(string Path, string Source, string Seed, long Length);

/// <summary>
/// Sollzustand der Integritaetsdateien der E4.0-Probe (Spec docs/superpowers/specs/2026-11-17-storage-e4-0-rw-qualification.md,
/// Abschnitt 3) als reine Funktion. Die Erwartung wird in jedem Lauf neu berechnet, nie aus dem geprueften Dateisystem
/// gelesen. Stufe 1 = nach Lauf 1, Stufe 2 = nach Lauf 2 (g und h je 500 weitere Aenderungen).
/// </summary>
internal static class RootProbeCatalog
{
    // Cosmos-Inode-Cache (Befund 17.11.): je Mount bleiben Lauf 1 und 2 unter ~15 000 verschiedenen Inodes; bei 17 519
    // scheitert das Wachstum des Cache-Dictionary an der GC-Fragmentierung (COSMOS_SUPPORT, Diagnoselauf 12).
    internal const int DFiles = 4000;
    internal const int DDirectories = 10;
    // 2-GiB-Root fuer die Laeufe 1-5 (Leitungsentscheid 19:02): Groessen nur bis 64 KiB, zusammen ~0,2 GB.
    internal static readonly long[] DSizes = [0, 1, 4095, 4096, 4097, 65536];
    // Schritt e bleibt klein (Inode-Cache); grosse Verzeichnisse pruefen die Diagnoselaeufe 9 und 16 (Schritt e-limit).
    internal const int EEntries = 3000;
    internal const int EChanged = 300;
    /// <summary>Alte Cosmos-Grenze vor cosmos #345 (MaxParseIters 8192 inkl. '.' und '..'); Lauf 9 legt einen mehr an.</summary>
    internal const int LegacyDirectoryLimit = 8190;
    /// <summary>Eintraege im Diagnoselauf 16 (E2-P7, Plan Schritt 4).</summary>
    // 10.000 statt 20.000 (Koordinator 19.11.): Anlegen kostet in Cosmos O(n^2), 20.000 liefen nicht in 3600 s durch.
    internal const int ScaleEntries = 10000;
    internal const int FCount = 1000;
    internal const int FOverwrite = 200;
    internal const int GCount = 1000;
    internal const int GRun2 = 500;
    internal const int HCount = 2000;
    internal const int HRun2 = 500;
    internal const int JReadFiles = 50;
    /// <summary>2 GiB - 1 Byte: groesste Datei ohne large_file (Schritt k, nur im Hochgruppenlauf 32/128 GiB).</summary>
    internal const long BigLength = int.MaxValue;
    internal const string BigPath = "/k/big";
    /// <summary>Ziel des Ersetzens bei vollem Datentraeger (l2): eine 64-KiB-Datei aus d.</summary>
    internal const string FullReplaceTarget = "/d/d00/f0005";
    internal const string Utf8Name = "äß x";

    public static string DPath(int index) => $"/d/d{index / (DFiles / DDirectories):D2}/f{index:D4}";

    public static long DLength(int index) => DSizes[index % DSizes.Length];

    public static string EName(int index) => $"/e/e{index:D5}";

    public static long ELength(int index) => 16 + index % 64;

    public static long GInitialLength(int index) => 1000 + 37L * index;

    /// <summary>Ersetzen in Lauf 1: wachsend, schrumpfend oder auf Laenge 0.</summary>
    public static long GReplacedLength(int index) => (index % 3) switch
    {
        0 => 2 * GInitialLength(index),
        1 => GInitialLength(index) / 2,
        _ => 0,
    };

    public static long GRun2Length(int index) => GInitialLength(index) + index;

    /// <summary>Gerade Indizes ersetzen mit keepBackup; die .bak traegt dann den vorigen Inhalt.</summary>
    public static bool GKeepsBackup(int index) => index % 2 == 0;

    public static string LongName => new('n', 255);

    public static IEnumerable<RootProbeEntry> Expected(int stage)
    {
        if (stage is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(stage));

        yield return Same("/home/test/notes.bin", "c", 3000);
        yield return Same("/home/test/.config/zehos/t1/t2/t3/t4/t5/t6/t7/t8/leaf.bin", "c", 777);

        for (int index = 0; index < DFiles; index++) yield return Same(DPath(index), "d", DLength(index));

        for (int index = 0; index < EEntries; index++)
        {
            switch (index % 10)
            {
                case 1: yield return new($"/e/r{index:D5}", EName(index), "e", ELength(index)); break;
                case 2: break;
                default: yield return Same(EName(index), "e", ELength(index)); break;
            }
        }

        for (int index = 0; index < EChanged; index++) yield return Same($"/e/x{index:D5}", "e", ELength(index));

        for (int index = 0; index < FCount; index++)
        {
            yield return new($"/f/a/b{index:D4}", $"/f/a/a{index:D4}", "f", 200 + index);
            yield return new($"/f/y/m{index:D4}", $"/f/x/m{index:D4}", "f", 300 + index);
            yield return new($"/f/dirmoved/e{index:D4}", $"/f/dir/e{index:D4}", "f", 100 + index % 50);
        }

        for (int index = 0; index < FOverwrite; index++) yield return new($"/f/o/dst{index:D3}", $"/f/o/src{index:D3}", "f", 400 + index);

        for (int index = 0; index < GCount; index++)
        {
            string path = $"/g/r{index:D4}";
            bool second = stage == 2 && index < GRun2;
            yield return second ? Same(path, "g2", GRun2Length(index)) : Same(path, "g1", GReplacedLength(index));
            if (!GKeepsBackup(index)) continue;
            yield return second ? new(path + ".bak", path, "g1", GReplacedLength(index)) : new(path + ".bak", path, "g0", GInitialLength(index));
        }

        for (int index = 0; index < HCount; index++)
        {
            if (stage == 2 && index < HRun2) continue;
            yield return Same($"/h/new{index:D4}", "h1", 512);
        }

        if (stage == 2)
            for (int index = 0; index < HRun2; index++) yield return Same($"/h/run2_{index:D3}", "h2", 512);

        yield return Same("/i/" + LongName, "i", 10);
        yield return Same("/i/" + Utf8Name, "i", 11);
        yield return Same("/i/A", "i", 12);
        yield return Same("/i/a", "i", 13);

        for (int index = 0; index < JReadFiles; index++) yield return Same($"/j/c/r{index:D2}", "j", 4096);
    }

    private static RootProbeEntry Same(string path, string seed, long length) => new(path, path, seed, length);
}
#endif
