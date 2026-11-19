using System;
namespace ProjektZeh.Runtime;

/// <summary>Schalter der Kernel-Kommandozeile (Limine <c>cmdline:</c>), ohne Cosmos-Typen.</summary>
internal readonly record struct BootOptions(int? Cpus, bool SmtOff)
{
    private const string CpusPrefix = "zehos.cpus=";
    private const string SmtOffWord = "zehos.smt=off";

    /// <summary>
    /// Liest "zehos.cpus=&lt;n&gt;" (n &gt;= 1) und "zehos.smt=off" aus der Kernel-Kommandozeile. Unbekanntes wird ignoriert,
    /// ein ungültiger Wert gilt als nicht angegeben. Es zählen nur ganze, durch Leerzeichen getrennte Wörter.
    /// </summary>
    public static BootOptions Parse(string? commandLine)
    {
        int? cpus = null;
        bool smtOff = false;
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
        }
        return new BootOptions(cpus, smtOff);
    }
}
