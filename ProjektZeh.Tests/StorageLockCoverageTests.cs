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
/// eines <c>StorageLock.Run(...)</c> oder in einer Methode mit Endung <c>StorageLocked</c>, und solche Methoden werden nur
/// aus gesperrten Bereichen gerufen oder referenziert. Neue ungesperrte Aufrufer machen den Test rot, nicht nur die heute
/// bekannten. Eigene Endung, weil <c>Locked</c> im Projekt schon fuer andere Sperren steht (Review blue-lockscan).
/// </summary>
/// <remarks>
/// Grenzen: Die Pruefung erkennt Zugriffe an API-Namen (File, Directory, FileStream, VfsManager, InodeOperations, ...).
/// Lesen und Schreiben auf einem schon geoeffneten Stream ausserhalb dieser Bereiche erkennt sie nicht; offene Streams ueber
/// mehrere Aufrufe (WASI-Dateihandles) sind in <c>WasmRuntime</c> von Hand gesperrt. Ausdruecke in interpolierten
/// Zeichenketten werden mit der Zeichenkette ausgeblendet.
/// </remarks>
[TestClass]
public sealed class StorageLockCoverageTests
{
    private static readonly Regex Access = new(
        @"\b(?:File|Directory)\.[A-Z]\w*\s*\(|\bnew\s+(?:FileStream|FileInfo|DirectoryInfo|StreamReader|StreamWriter)\s*\(|\b(?:VfsManager|FileSystemManager|InodeOperations)\.[A-Z]\w*|\bDriveInfo\b|\.(?:EnumerateFileSystemInfos|GetFileSystemInfos|EnumerateFiles|EnumerateDirectories)\s*\(",
        RegexOptions.Compiled);

    // Aufruf oder Methodengruppe (nicht Deklaration) einer gesperrten Hilfsmethode.
    private static readonly Regex LockedCall = new(@"\b(\w+StorageLocked)\b", RegexOptions.Compiled);

    // Faule Aufzaehlung: im Run-Ausdruck ohne Block liefe die Aufzaehlung erst ausserhalb der Sperre (Review blue-lockscan).
    private static readonly Regex LazyEnumeration = new(@"\bEnumerate\w*\s*\(", RegexOptions.Compiled);

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
        ["Runtime/CosmosSecurityProbe.cs"] = "Probe",
        // VFS-Schicht: Cosmos ruft diese Inode-Operationen nur innerhalb eines Zugriffs, den der Aufrufer schon unter der
        // Speicher-Sperre ausfuehrt; die eigenen Mount-Aufrufe der Datei sind gesperrt. Span-Parameter liessen sich hier
        // ohnehin nicht in ein Run-Lambda fassen.
        ["Storage/CosmosReadOnlyVfs.cs"] = "VFS-Schicht",
    };

    [TestMethod]
    public void EveryDiskAccessRunsUnderStorageLock()
    {
        string root = Path.Combine(Golden.RepositoryRoot(), "ProjektZeh");
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
                public void Save(string p) => StorageLock.Run(() => SaveStorageLocked(p));
                public string[] Names(string p) => StorageLock.Run(() => Directory.EnumerateFiles(p).ToArray());
                private void SaveStorageLocked(string p)
                {
                    using var s = new FileStream(p, FileMode.Create);
                    HelperStorageLocked();
                    foreach (string n in Directory.EnumerateFiles(p)) { }
                }
                private static void HelperStorageLocked() { Directory.CreateDirectory("d"); }
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
                public void Save(string p) { SaveStorageLocked(p); }
                public void All(List<string> l) { l.ForEach(SaveStorageLocked); }
                private void SaveStorageLocked(string p) { File.Delete(p); }
            }
            """;
        var findings = Check(source);
        Assert.AreEqual(2, findings.Count, string.Join("\n", findings));
        Assert.IsTrue(findings.All(finding => finding.Contains("SaveStorageLocked", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Detects_LazyEnumerationAndInstanceApis()
    {
        const string source = """
            class A
            {
                public IEnumerable<string> Names(string p) => StorageLock.Run(() => Directory.EnumerateFiles(p));
                public void Infos(DirectoryInfo d) { foreach (var i in d.EnumerateFileSystemInfos()) { } }
                public bool Look(IVfsInode n) => n.InodeOperations.Lookup(n, "x", out _);
                public string Old() => RunLocked();
            }
            """;
        var findings = Check(source);
        Assert.AreEqual(3, findings.Count, string.Join("\n", findings));
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

            foreach (Match match in LazyEnumeration.Matches(line))
                if (regions.InLazyRun(offset + match.Index)) findings.Add($"{index + 1}: {match.Value.Trim()} im Run-Ausdruck ohne ToArray/ToList (Aufzaehlung ausserhalb der Sperre)");

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
        // Run-Argumente als Ausdruckslambda ohne Block und ohne ToArray/ToList.
        public readonly List<(int Start, int End)> LazyRuns = [];

        public bool Covered(int position) => Ranges.Any(range => position >= range.Start && position < range.End);

        public bool InLazyRun(int position) => LazyRuns.Any(range => position >= range.Start && position < range.End);
    }

    /// <summary>
    /// Gesperrte Bereiche: das Klammerargument jedes <c>StorageLock.Run(</c> und der Rumpf jeder Methode mit Endung StorageLocked
    /// (Block bis zur schliessenden Klammer oder Ausdruckskoerper bis zum Semikolon).
    /// </summary>
    private static Coverage Regions(string code)
    {
        var coverage = new Coverage();
        foreach (Match match in Regex.Matches(code, @"\bStorageLock\.Run\s*\("))
        {
            int open = match.Index + match.Length - 1;
            int close = Matching(code, open, '(', ')');
            coverage.Ranges.Add((open, close));
            string argument = code[open..close];
            if (!argument.Contains('{') && !argument.Contains(".ToArray()", StringComparison.Ordinal)
                && !argument.Contains(".ToList()", StringComparison.Ordinal))
                coverage.LazyRuns.Add((open, close));
        }

        foreach (Match match in Regex.Matches(code, @"\b\w+StorageLocked\s*(?:<[^>]*>)?\s*\(", RegexOptions.Multiline))
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
}
