using System;
namespace ProjektZeh.Runtime;

/// <summary>Schalter der Kernel-Kommandozeile (Limine <c>cmdline:</c>), ohne Cosmos-Typen.</summary>
internal readonly record struct BootOptions(int? Cpus, bool SmtOff)
{
    private const string CpusPrefix = "zehos.cpus=";
    private const string SmtOffWord = "zehos.smt=off";
    private const string LinuxSmpPrefix = "zehos.linuxsmp=";
    private const string TlbTimeoutPrefix = "zehos.tlbtimeout=";

    /// <summary>
    /// N4-S2: "zehos.linuxsmp=1" laesst die Threads eines Linux-Laufs auf allen CPUs seiner Menge laufen; Vorgabe (fehlt, 0 oder ungueltig)
    /// ist die Bindung an eine CPU, bis die Pruefung S5 bestanden ist.
    /// </summary>
    public bool LinuxSmp { get; init; }

    /// <summary>N4-S1: "zehos.tlbtimeout=&lt;ms&gt;" (0 = nie) fuer die Panikgrenze des TLB-Abschusses; null laesst die Cosmos-Vorgabe (5000 ms).</summary>
    public int? TlbTimeoutMs { get; init; }

    /// <summary>
    /// Liest "zehos.cpus=&lt;n&gt;" (n &gt;= 1) und "zehos.smt=off" aus der Kernel-Kommandozeile. Unbekanntes wird ignoriert,
    /// ein ungültiger Wert gilt als nicht angegeben. Es zählen nur ganze, durch Leerzeichen getrennte Wörter.
    /// </summary>
    public static BootOptions Parse(string? commandLine)
    {
        int? cpus = null;
        bool smtOff = false;
        bool linuxSmp = false;
        int? tlbTimeout = null;
        if (string.IsNullOrWhiteSpace(commandLine))
            return new BootOptions(null, false);

        foreach (var word in commandLine.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith(CpusPrefix, StringComparison.Ordinal))
            {
                // Bei mehrfacher Angabe gilt die letzte gültige.
                if (int.TryParse(word.AsSpan(CpusPrefix.Length), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1)
                    cpus = n;
            }
            else if (string.Equals(word, SmtOffWord, StringComparison.Ordinal))
            {
                smtOff = true;
            }
            else if (word.StartsWith(LinuxSmpPrefix, StringComparison.Ordinal))
            {
                // Nur "1" schaltet ein; die letzte Angabe gilt, damit ein angehaengtes "=0" eine Vorgabe des Bootmenues aufhebt.
                linuxSmp = string.Equals(word, LinuxSmpPrefix + "1", StringComparison.Ordinal);
            }
            else if (word.StartsWith(TlbTimeoutPrefix, StringComparison.Ordinal))
            {
                if (int.TryParse(word.AsSpan(TlbTimeoutPrefix.Length), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var ms))
                    tlbTimeout = ms;
            }
        }
        return new BootOptions(cpus, smtOff) { LinuxSmp = linuxSmp, TlbTimeoutMs = tlbTimeout };
    }
}
