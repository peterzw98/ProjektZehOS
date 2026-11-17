using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProjektZeh.Tests;

/// <summary>
/// Quellpruefung zur Speicher-Sperre (Diagnoselauf 13, 18.11.): Jeder Plattenzugriff im Kernel steht entweder im Argument
/// eines <c>StorageLock.Run(...)</c> oder in einer Methode mit Endung <c>Locked</c>, und solche Methoden werden nur aus
/// gesperrten Bereichen gerufen. Neue ungesperrte Aufrufer machen den Test rot, nicht nur die heute bekannten.
/// </summary>
/// <remarks>
/// Grenze: Die Pruefung erkennt Zugriffe an API-Namen (File, Directory, FileStream, VfsManager, ...). Lesen und Schreiben
/// auf einem schon geoeffneten Stream ausserhalb dieser Methoden erkennt sie nicht; offene Streams ueber mehrere Aufrufe
/// (WASI-Dateihandles) sind deshalb in <c>WasmRuntime</c> von Hand gesperrt und hier als Muster mitgeprueft.
/// </remarks>
[TestClass]
public sealed class StorageLockCoverageTests
{
    private static readonly Regex Access = new(
        @"\b(?:File|Directory)\.[A-Z]\w*\s*\(|\bnew\s+(?:FileStream|FileInfo|DirectoryInfo|StreamReader|StreamWriter)\s*\(|\bVfsManager\.[A-Z]\w*|\bDriveInfo\b",
        RegexOptions.Compiled);

    // Aufruf (nicht Deklaration) einer gesperrten Hilfsmethode.
    private static readonly Regex LockedCall = new(@"\b(\w+Locked)\s*\(", RegexOptions.Compiled);

    private static readonly Regex Member = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|internal|protected|static|override|async|unsafe|readonly|sealed|virtual|extern|new|partial)\s+)+[\w<>\[\]?,\s.()]*?\b(\w+)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>Dateien ohne Pflicht zur Sperre, je mit Grund.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // Gastproben laufen allein im eigenen Build (-p:ZehOS...Probe=true); ihre Nebenlaeufigkeit ist das Pruefobjekt.
        ["Storage/CosmosExt2RootProbe.cs"] = "Probe",
        ["Storage/CosmosExt2GuestProbe.cs"] = "Probe",
        ["Runtime/CosmosThreadProbe.cs"] = "Probe",
        ["Runtime/CosmosWasmProbe.cs"] = "Probe",
        ["Runtime/CosmosAotProbe.cs"] = "Probe",
        ["Runtime/CosmosSimdProbe.cs"] = "Probe",
        ["Messaging/MessageGuestProbe.cs"] = "Probe",
        ["Network/CosmosNetworkProbe.cs"] = "Probe",
    };

    [TestMethod]
    public void EveryDiskAccessRunsUnderStorageLock()
    {
        string root = SourceRoot();
        var findings = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
            if (relative.StartsWith("ThirdParty/", StringComparison.Ordinal) || Exempt.ContainsKey(relative)) continue;
            findings.AddRange(Check(File.ReadAllText(path)).Select(finding => $"{relative}:{finding}"));
        }

        Assert.AreEqual(0, findings.Count, "Plattenzugriff ohne StorageLock:\n" + string.Join("\n", findings));
    }

    [TestMethod]
    public void Detects_UnlockedAccess()
    {
        const string source = """
            class A
            {
                public void Save() { File.WriteAllText("x", "y"); }
            }
            """;
        Assert.AreEqual(1, Check(source).Count);
    }

    [TestMethod]
    public void Accepts_RunArgumentAndLockedMethod()
    {
        const string source = """
            class A
            {
                public bool Exists(string p) => StorageLock.Run(() =>
                    File.Exists(p));
                public void Save(string p) => StorageLock.Run(() => SaveLocked(p));
                private void SaveLocked(string p)
                {
                    using var s = new FileStream(p, FileMode.Create);
                    HelperLocked();
                }
                private static void HelperLocked() { Directory.CreateDirectory("d"); }
            }
            """;
        Assert.AreEqual(0, Check(source).Count, string.Join("\n", Check(source)));
    }

    [TestMethod]
    public void Detects_LockedMethodCalledWithoutLock()
    {
        const string source = """
            class A
            {
                public void Save(string p) { SaveLocked(p); }
                private void SaveLocked(string p) { File.Delete(p); }
            }
            """;
        var findings = Check(source);
        Assert.AreEqual(1, findings.Count);
        StringAssert.Contains(findings[0], "SaveLocked");
    }

    [TestMethod]
    public void IgnoresCommentsAndStrings()
    {
        const string source = """
            class A
            {
                // File.Delete(p) im Kommentar
                public string Text() => "File.Delete(p)";
            }
            """;
        Assert.AreEqual(0, Check(source).Count);
    }

    /// <summary>Befunde "Zeile: Text" fuer eine Quelldatei.</summary>
    internal static List<string> Check(string source)
    {
        string code = StripCommentsAndStrings(source);
        string[] lines = code.Split('\n');
        var findings = new List<string>();
        var regions = Regions(code);
        int offset = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            foreach (Match match in Access.Matches(line))
                if (!regions.Covered(offset + match.Index)) findings.Add($"{index + 1}: {match.Value.Trim()}");
            foreach (Match match in LockedCall.Matches(line))
            {
                if (IsDeclaration(line, match)) continue;
                if (!regions.Covered(offset + match.Index)) findings.Add($"{index + 1}: {match.Groups[1].Value} ohne Sperre gerufen");
            }

            offset += line.Length + 1;
        }

        return findings;
    }

    private static bool IsDeclaration(string line, Match match)
    {
        var member = Member.Match(line);
        return member.Success && member.Groups[1].Value == match.Groups[1].Value && member.Groups[1].Index == match.Groups[1].Index;
    }

    private sealed class Coverage
    {
        public readonly List<(int Start, int End)> Ranges = [];

        public bool Covered(int position) => Ranges.Any(range => position >= range.Start && position < range.End);
    }

    /// <summary>
    /// Gesperrte Bereiche: das Klammerargument jedes <c>StorageLock.Run(</c> und der Rumpf jeder Methode mit Endung Locked
    /// (Block bis zur schliessenden Klammer oder Ausdruckskoerper bis zum Semikolon).
    /// </summary>
    private static Coverage Regions(string code)
    {
        var coverage = new Coverage();
        foreach (Match match in Regex.Matches(code, @"\bStorageLock\.Run\s*\("))
        {
            int open = match.Index + match.Length - 1;
            coverage.Ranges.Add((open, Matching(code, open, '(', ')')));
        }

        foreach (Match match in Regex.Matches(code, @"\b\w+Locked\s*(?:<[^>]*>)?\s*\(", RegexOptions.Multiline))
        {
            int lineStart = code.LastIndexOf('\n', match.Index) + 1;
            int lineEnd = code.IndexOf('\n', match.Index);
            string line = code[lineStart..(lineEnd < 0 ? code.Length : lineEnd)];
            var member = Member.Match(line);
            if (!member.Success || lineStart + member.Groups[1].Index != match.Index) continue;
            int parameters = Matching(code, match.Index + match.Length - 1, '(', ')');
            int cursor = parameters;
            while (cursor < code.Length && char.IsWhiteSpace(code[cursor])) cursor++;
            if (cursor < code.Length && code[cursor] == '{') coverage.Ranges.Add((cursor, Matching(code, cursor, '{', '}')));
            else if (code.AsSpan(cursor).StartsWith("=>")) coverage.Ranges.Add((cursor, ExpressionEnd(code, cursor)));
        }

        return coverage;
    }

    private static int Matching(string code, int open, char opening, char closing)
    {
        int depth = 0;
        for (int index = open; index < code.Length; index++)
        {
            if (code[index] == opening) depth++;
            else if (code[index] == closing && --depth == 0) return index + 1;
        }

        return code.Length;
    }

    private static int ExpressionEnd(string code, int start)
    {
        int depth = 0;
        for (int index = start; index < code.Length; index++)
        {
            char current = code[index];
            if (current is '(' or '{' or '[') depth++;
            else if (current is ')' or '}' or ']') depth--;
            else if (current == ';' && depth <= 0) return index + 1;
        }

        return code.Length;
    }

    /// <summary>Ersetzt Kommentare und Zeichenketten durch Leerzeichen (Zeilenumbrueche bleiben), damit Klammern darin nicht zaehlen.</summary>
    internal static string StripCommentsAndStrings(string source)
    {
        var result = new StringBuilder(source.Length);
        int index = 0;
        while (index < source.Length)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (current == '/' && next == '/')
            {
                while (index < source.Length && source[index] != '\n') { result.Append(' '); index++; }
            }
            else if (current == '/' && next == '*')
            {
                int end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                Blank(source, index, end, result);
                index = end;
            }
            else if (current == '"' && index + 2 < source.Length && next == '"' && source[index + 2] == '"')
            {
                int end = source.IndexOf("\"\"\"", index + 3, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 3;
                Blank(source, index, end, result);
                index = end;
            }
            else if (current == '"' || (current is '@' or '$' && next == '"') || (current is '$' or '@' && next is '@' or '$' && index + 2 < source.Length && source[index + 2] == '"'))
            {
                int start = index;
                bool verbatim = false;
                while (source[index] != '"') { verbatim |= source[index] == '@'; index++; }
                index++;
                while (index < source.Length)
                {
                    if (verbatim && source[index] == '"' && index + 1 < source.Length && source[index + 1] == '"') { index += 2; continue; }
                    if (!verbatim && source[index] == '\\') { index += 2; continue; }
                    if (source[index] == '"') { index++; break; }
                    if (!verbatim && source[index] == '\n') break;
                    index++;
                }

                Blank(source, start, Math.Min(index, source.Length), result);
            }
            else if (current == '\'' && index + 2 < source.Length)
            {
                int end = source[index + 1] == '\\' ? source.IndexOf('\'', index + 3) : index + 2;
                end = end < 0 ? index + 1 : end + 1;
                Blank(source, index, end, result);
                index = end;
            }
            else
            {
                result.Append(current);
                index++;
            }
        }

        return result.ToString();
    }

    private static void Blank(string source, int start, int end, StringBuilder result)
    {
        for (int index = start; index < end; index++) result.Append(source[index] == '\n' ? '\n' : ' ');
    }

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "ProjektZeh", "ProjektZeh.csproj");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        }

        throw new AssertFailedException("ProjektZeh-Quellordner nicht gefunden");
    }
}
