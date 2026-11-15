using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Shell;

internal sealed partial class ShellService
{
    internal const int MaximumOutputLength = FileService.MaximumTextLength;
    private readonly FileService? files;
    private readonly Func<string>? getDate;
    private readonly Func<string>? getMemory;
    private readonly Dictionary<string, string[]> aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> history = [];
    private string? pipelineInput;
    private readonly Func<int>? getSessionGeneration;
    private int sessionGeneration;
    public int SessionGeneration { get { SyncIdentity(); return sessionGeneration; } }
    private void SyncIdentity()
    {
        int current = getSessionGeneration?.Invoke() ?? sessionGeneration;
        if (current == sessionGeneration) return;
        sessionGeneration = current; history.Clear(); aliases.Clear(); DismissPager(); CancelSecretInput();
    }
    public IReadOnlyList<string> History { get { SyncIdentity(); return history.AsReadOnly(); } }

    private ShellCommand[] CreateFeatureCommands() =>
    [
        new("head", "head [-n Anzahl] [Datei]", "Zeigt die ersten Zeilen (Standard: 10).", true, (a,o) => Filter("head",a,o)),
        new("tail", "tail [-n Anzahl] [Datei]", "Zeigt die letzten Zeilen (Standard: 10).", true, (a,o) => Filter("tail",a,o)),
        new("grep", "grep [-i] <Text> [Datei]", "Sucht Zeilen mit einem festen Teiltext.", true, (a,o) => Filter("grep",a,o)),
        new("wc", "wc [-l|-w|-c] [Datei]", "Zaehlt Zeilen, Woerter und Zeichen (keine Bytes).", true, (a,o) => Filter("wc",a,o)),
        new("sort", "sort [Datei]", "Sortiert Zeilen ordinal.", true, (a,o) => Filter("sort",a,o)),
        new("uniq", "uniq [Datei]", "Entfernt benachbarte gleiche Zeilen.", true, (a,o) => Filter("uniq",a,o)),
        new("less", "less [Datei]", "Pager: Pfeile/Bildtasten, Leertaste, q zum Schliessen.", true, (a,o) => Filter("less",a,o)),
        new("history", "history", "Zeigt den sitzungslokalen Verlauf (32 Befehle).", false, WriteHistory),
        new("alias", "alias [Name=Befehl ...]", "Definiert einen sitzungslokalen Argumentalias (32 Aliase).", true, SetAlias),
        new("date", "date", "Zeigt Datum/Uhrzeit der Hardwareuhr.", false, (_,o) => o.WriteLine(getDate?.Invoke() ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))),
        new("uname", "uname", "Zeigt Kernel und Architektur.", false, (_,o) => o.WriteLine("ZehOS x64 Cosmos Gen3")),
        new("free", "free", "Zeigt die verfuegbaren Speicherangaben.", false, (_,o) => o.WriteLine(getMemory?.Invoke() ?? "Speicherangaben sind nicht verfuegbar.")),
        new("exit", "exit", "Schliesst diese Terminalsitzung; Textshell bleibt bedienbar.", false, (_,_) => ExitRequested = true)
    ];

    private ResultErr ExecuteFeatures(string? line, ITextOutput? output)
    {
        if (output == null) return ResultErr.CreateFailure(ResultCode.MissingArgument, "Das Ausgabeziel fehlt.");
        if (string.IsNullOrWhiteSpace(line)) return ResultErr.CreateSuccess();
        if (line.Length > 66560) return Invalid("Befehlszeile ist zu lang.");
        var stages = new List<string>();
        int start = 0; bool quoted = false; string? target = null; bool append = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted || c is not ('|' or '>')) continue;
            stages.Add(line[start..i]);
            if (c == '|') { start = i + 1; continue; }
            append = i + 1 < line.Length && line[i + 1] == '>';
            var parsedTarget = CommandLineParser.Parse(line[(i + (append ? 2 : 1))..]);
            if (!parsedTarget.IsOK()) return parsedTarget.Status;
            if (parsedTarget.Value!.Length != 1) return Invalid("Umleitung braucht genau einen Dateipfad.");
            target = parsedTarget.Value[0];
            // Operatoren im Ziel muessen zitiert sein; weitere Umleitungen sind nicht unterstuetzt.
            bool targetQuoted = false;
            foreach (char tc in line[(i + (append ? 2 : 1))..])
            {
                if (tc == '"') targetQuoted = !targetQuoted;
                else if (!targetQuoted && tc is '|' or '>') return Invalid("Nur eine abschliessende Umleitung ist erlaubt.");
            }
            start = line.Length;
            break;
        }
        if (target == null) stages.Add(line[start..]);
        if (stages.Count > 16) return Invalid("Eine Pipeline darf hoechstens 16 Stufen enthalten.");
        var expanded = new List<string[]>(); bool sensitive = false;
        foreach (string stage in stages)
        {
            var parsed = CommandLineParser.Parse(stage);
            if (!parsed.IsOK()) return parsed.Status;
            if (parsed.Value!.Length == 0) return Invalid("Leere Pipelinestufe.");
            var tokens = parsed.Value;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (aliases.TryGetValue(tokens[0], out var replacement))
            {
                if (!visited.Add(tokens[0]) || visited.Count > 16) return Invalid("Aliaszyklus oder zu tiefe Verschachtelung.");
                var next = new string[replacement.Length + tokens.Length - 1];
                Array.Copy(replacement, next, replacement.Length);
                Array.Copy(tokens, 1, next, replacement.Length, tokens.Length - 1);
                tokens = next;
            }
            sensitive |= IsSensitiveCommand(tokens[0]);
            expanded.Add(tokens);
        }
        if (sensitive && (expanded.Count != 1 || target != null)) return Invalid("Kontobefehle sind nur ohne Pipe oder Umleitung erlaubt.");
        for (int i = 0; i < expanded.Count; i++)
            if (expanded[i][0].Equals("less", StringComparison.OrdinalIgnoreCase) && (i != expanded.Count - 1 || target != null))
                return Invalid("less muss die letzte Stufe ohne Umleitung sein.");
        if (expanded.Count > 1 || target != null)
            foreach (var tokens in expanded)
                if (!IsPipelineCommand(tokens[0])) return Invalid("Dieser Befehl kann nicht in einer Pipeline ausgefuehrt werden: " + tokens[0]);
        if (target != null && files == null) return Invalid("Dateidienst ist nicht verfuegbar.");
        if (!sensitive && !string.Equals(expanded[0][0], "alias", StringComparison.OrdinalIgnoreCase))
        {
            if (history.Count == 0 || history[^1] != line)
            {
                if (history.Count == 32) history.RemoveAt(0);
                history.Add(line);
            }
        }
        string? input = null;
        for (int index = 0; index < expanded.Count; index++)
        {
            var tokens = expanded[index];
            ShellCommand? found = null;
            foreach (ShellCommand command in commands)
                if (string.Equals(command.Name, tokens[0], StringComparison.OrdinalIgnoreCase)) { found = command; break; }
            if (found == null) return ResultErr.CreateFailure(ResultCode.NotFound,
                $"cause='UnknownCommand' message='Unbekannter Befehl: {tokens[0]}. Hilfe: help.'");
            var allowed = authorize?.Invoke(found.Name) ?? ResultErr.CreateSuccess();
            if (!allowed.IsOK()) return allowed;
            var args = new string[tokens.Length - 1]; Array.Copy(tokens, 1, args, 0, args.Length);
            // Kennwortdialoge muessen ihr urspruengliches Ausgabeziel behalten.
            if (sensitive) return found.Execute(args, output);
            var captured = new BoundedOutput(expanded.Count == 1 && target == null ? output : null);
            pipelineInput = input;
            ResultErr result;
            try { result = found.Execute(args, captured); }
            finally { pipelineInput = null; }
            if (!result.IsOK()) return result;
            if (captured.Overflow) { DismissPager(); return Invalid("Ausgabe ist auf 65536 Zeichen begrenzt."); }
            input = captured.Text;
            if (index != expanded.Count - 1) continue;
            if (target != null)
            {
                if (PagerText != null) { DismissPager(); return Invalid("less kann nicht umgeleitet werden."); }
                if (append)
                {
                    var existing = files!.ReadText(target, true);
                    if (!existing.IsOK()) return existing.Status;
                    if ((existing.Value?.Length ?? 0) + input.Length > MaximumOutputLength) return Invalid("Datei ist zu gross.");
                    input = existing.Value + input;
                }
                return files!.WriteText(target, input);
            }
            if (expanded.Count == 1 && target == null) return result;
            if (captured.Cleared) output.Clear();
            foreach (string item in captured.Lines) output.WriteLine(item);
        }
        return ResultErr.CreateSuccess();
    }

    public bool CanRemember(string line)
    {
        SyncIdentity();
        if (IsSecretInput || IsBusy) return false;
        var remembered = new StringBuilder(line.Length);
        bool quoted = false;
        foreach (char character in line)
        {
            if (character == '"') quoted = !quoted;
            remembered.Append(!quoted && character is '|' or '>' ? ' ' : character);
        }
        var parsed = CommandLineParser.Parse(remembered.ToString());
        if (!parsed.IsOK()) return false;
        foreach (string token in parsed.Value!)
        {
            if (IsSensitiveCommand(token) || token.Equals("alias", StringComparison.OrdinalIgnoreCase)) return false;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string name = token;
            while (aliases.TryGetValue(name, out var replacement))
            {
                if (!visited.Add(name)) return false;
                name = replacement[0];
                if (IsSensitiveCommand(name)) return false;
            }
        }
        return true;
    }

    internal static bool IsSensitiveCommand(string name) =>
        name.Equals("login", StringComparison.OrdinalIgnoreCase) || name.Equals("logout", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("passwd", StringComparison.OrdinalIgnoreCase) || name.Equals("user", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("security", StringComparison.OrdinalIgnoreCase);

    private static bool IsPipelineCommand(string name)
    {
        foreach (string supported in new[] { "echo", "cat", "pwd", "ls", "dir", "head", "tail", "grep", "wc", "sort", "uniq", "less", "history", "date", "uname", "free", "help", "about", "version", "uptime", "log", "find", "df", "du", "whoami" })
            if (name.Equals(supported, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private sealed class BoundedOutput(ITextOutput? forward = null) : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public bool Overflow { get; private set; }
        public bool Cleared { get; private set; }
        private int length;
        public string Text => Lines.Count == 0 ? "" : string.Join("\n", Lines) + "\n";
        public void WriteLine(string text)
        {
            if (Overflow) return;
            if (text.Length + 1 > MaximumOutputLength - length) { Overflow = true; return; }
            length += text.Length + 1;
            if (forward != null) forward.WriteLine(text); else Lines.Add(text);
        }
        public void Clear() { Cleared = true; Lines.Clear(); length = 0; forward?.Clear(); }
    }

    private ResultErr SetAlias(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 0)
        {
            foreach (var alias in aliases) output.WriteLine(alias.Key + "=" + string.Join(" ", alias.Value));
            return ResultErr.CreateSuccess();
        }
        string assignment = arguments[0];
        int equals = assignment.IndexOf('=');
        if (equals < 1) return Invalid("Verwendung: alias Name=Befehl [Argumente ...]");
        string name = assignment[..equals];
        foreach (char character in name)
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') return Invalid("Ungueltiger Aliasname.");
        var parsed = CommandLineParser.Parse(assignment[(equals + 1)..]);
        if (!parsed.IsOK()) return parsed.Status;
        if (parsed.Value!.Length == 0) return Invalid("Aliasbefehl fehlt.");
        if (aliases.Count == 32 && !aliases.ContainsKey(name)) return Invalid("Hoechstens 32 Aliase pro Sitzung.");
        var replacement = new string[parsed.Value.Length + arguments.Length - 1];
        Array.Copy(parsed.Value, replacement, parsed.Value.Length);
        Array.Copy(arguments, 1, replacement, parsed.Value.Length, arguments.Length - 1);
        aliases[name] = replacement;
        return ResultErr.CreateSuccess();
    }

    private void WriteHistory(string[] arguments, ITextOutput output)
    {
        for (int i = 0; i < history.Count; i++) output.WriteLine($"{i + 1}  {history[i]}");
    }

    private ResultErr Filter(string command, string[] arguments, ITextOutput output)
    {
        int count = 10, position = 0; bool ignoreCase = false; string mode = ""; string pattern = "";
        if (command is "head" or "tail" && arguments.Length > 0 && arguments[0] == "-n")
        {
            if (arguments.Length < 2 || !int.TryParse(arguments[1], out count) || count < 0 || count > MaximumOutputLength) return Invalid("Verwendung: " + command + " [-n 0..65536] [Datei]");
            position = 2;
        }
        if (command == "grep")
        {
            if (arguments.Length > 0 && arguments[0] == "-i") { ignoreCase = true; position++; }
            if (position >= arguments.Length) return Invalid("grep braucht einen Suchtext.");
            pattern = arguments[position++];
        }
        if (command == "wc" && arguments.Length > 0 && arguments[0].StartsWith('-'))
        {
            mode = arguments[position++];
            if (mode is not ("-l" or "-w" or "-c")) return Invalid("wc kennt nur -l, -w und -c.");
        }
        if (arguments.Length - position > 1) return Invalid("Genau eine Datei oder Pipe als Eingabe verwenden.");
        string text;
        if (position < arguments.Length)
        {
            if (pipelineInput != null) return Invalid("Datei und Pipe nicht gleichzeitig verwenden.");
            if (files == null) return Invalid("Dateidienst ist nicht verfuegbar.");
            var read = files.ReadText(arguments[position]);
            if (!read.IsOK()) return read.Status;
            text = read.Value!;
        }
        else if (pipelineInput != null) text = pipelineInput;
        else return Invalid("Eine Datei oder Pipeline als Eingabe fehlt.");
        if (text.Length > MaximumOutputLength) return Invalid("Eingabe ist zu gross.");
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        // Nur den letzten Zeilentrenner entfernen: weitere leere Zeilen sind Nutzdaten.
        string[] lines = normalized.Length == 0 ? [] : (normalized.EndsWith('\n') ? normalized[..^1] : normalized).Split('\n');
        if (command == "less") { PagerText = normalized; return ResultErr.CreateSuccess(); }
        if (command == "wc")
        {
            int words = 0; bool inWord = false;
            int lineCount = 0;
            foreach (char c in normalized) if (c == '\n') lineCount++;
            foreach (char c in text) { if (char.IsWhiteSpace(c)) inWord = false; else if (!inWord) { words++; inWord = true; } }
            output.WriteLine(mode switch { "-l" => lineCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "-w" => words.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c" => text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => $"{lineCount} {words} {text.Length}" });
            return ResultErr.CreateSuccess();
        }
        if (command == "sort") Array.Sort(lines, StringComparer.Ordinal);
        int begin = command == "tail" ? Math.Max(0, lines.Length - count) : 0;
        int end = command == "head" ? Math.Min(count, lines.Length) : lines.Length;
        for (int i = begin; i < end; i++)
        {
            if (command == "grep" && !lines[i].Contains(pattern, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            if (command == "uniq" && i > 0 && lines[i] == lines[i - 1]) continue;
            output.WriteLine(lines[i]);
        }
        return ResultErr.CreateSuccess();
    }

    public string Complete(string line, int cursor)
    {
        SyncIdentity();
        if (IsSecretInput || IsBusy || cursor < 0 || cursor > line.Length || line.Length > 66560) return line;
        string prefix = line[..cursor];
        var parsed = CommandLineParser.Parse(prefix);
        // Unvollstaendige Quotes werden nur fuer die Vervollstaendigung geschlossen.
        bool insideQuote = false;
        foreach (char c in prefix) if (c == '"') insideQuote = !insideQuote;
        if (insideQuote) parsed = CommandLineParser.Parse(prefix + '"');
        if (!parsed.IsOK()) return line;
        if (parsed.Value!.Length > 0)
        {
            string name = parsed.Value[0];
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (aliases.TryGetValue(name, out var replacement))
            {
                if (!visited.Add(name)) return line;
                name = replacement[0];
            }
            if (IsSensitiveCommand(name)) return line;
        }
        int start = 0; bool tokenQuote = false;
        for (int i = 0; i < cursor; i++)
        {
            if (line[i] == '"') tokenQuote = !tokenQuote;
            else if (!tokenQuote && (char.IsWhiteSpace(line[i]) || line[i] is '|' or '>')) start = i + 1;
        }
        bool quoted = start < cursor && line[start] == '"';
        if (quoted) start++;
        string fragment = line[start..cursor];
        bool hadClosingQuote = quoted && !insideQuote && fragment.EndsWith('"');
        if (hadClosingQuote) fragment = fragment[..^1];
        var candidates = new List<string>();
        bool commandPosition = start == 0 || (quoted && start == 1) || prefix[..Math.Max(0, start - (quoted ? 1 : 0))].TrimEnd().EndsWith('|');
        if (commandPosition)
        {
            foreach (var command in commands)
                if (command.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase) &&
                    (authorize?.Invoke(command.Name).IsOK() ?? true)) candidates.Add(command.Name);
            foreach (string alias in aliases.Keys)
                if (alias.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)) candidates.Add(alias);
        }
        else if (files != null)
        {
            int slash = Math.Max(fragment.LastIndexOf('/'), fragment.LastIndexOf('\\'));
            string directory = slash >= 0 ? fragment[..(slash + 1)] : "";
            string name = fragment[(slash + 1)..];
            var listed = files.ListDirectory(directory.Length == 0 ? "." : directory);
            if (!listed.IsOK() || listed.Value!.Length > 4096) return line;
            foreach (var entry in listed.Value)
            {
                string entryName = Path.GetFileName(entry.Path.TrimEnd('/', '\\'));
                if (entryName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) candidates.Add(directory + entryName + (entry.IsDirectory ? "/" : ""));
            }
        }
        if (candidates.Count == 0) return line;
        string common = candidates[0];
        foreach (string candidate in candidates)
        {
            int length = 0;
            while (length < common.Length && length < candidate.Length && char.ToUpperInvariant(common[length]) == char.ToUpperInvariant(candidate[length])) length++;
            common = common[..length];
        }
        if (common.Length <= fragment.Length) return line;
        if (!quoted && common.Contains(' ')) common = '"' + common + '"';
        if (hadClosingQuote || (quoted && insideQuote && candidates.Count == 1 && !common.EndsWith('/') &&
            (cursor == line.Length || line[cursor] != '"'))) common += '"';
        string completed = line[..start] + common + line[cursor..];
        return completed.Length <= 66560 ? completed : line;
    }

    private static ResultErr Invalid(string message) => ResultErr.CreateFailure(ResultCode.InvalidArgument, message);
}
