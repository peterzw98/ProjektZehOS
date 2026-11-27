using System;
using System.Collections.Generic;
using System.Text;

namespace ProjektZeh.Processes;

/// <summary>
/// Ladybird V37: Umgebung für <c>run</c>. Standardwerte wie <c>experiments/LinuxAbi/run-linux.py</c> und Zuweisungen
/// <c>NAME=WERT</c> vor dem Programm wie bei <c>env</c> oder einer POSIX-Shell (<c>run LANG=de_DE.UTF-8 ./prog a</c>).
/// Plattformunabhängig, damit die Hosttests Zerlegung und Grenzen prüfen.
/// </summary>
internal static class LinuxRunEnvironment
{
    /// <summary>Höchstens so viele Zuweisungen je Aufruf; mehr ist ein Tippfehler oder ein Skriptfehler, kein Bedarf.</summary>
    public const int MaxAssignments = 32;

    /// <summary>Längste Zuweisung in UTF-8-Bytes (<c>NAME=WERT</c>, ohne NUL); Linux erlaubt 128 KiB, Pfade und Schalter brauchen weit weniger.</summary>
    public const int MaxAssignmentBytes = 4096;

    /// <summary>
    /// Ganze Umgebung (Standardwerte und Zuweisungen, je Eintrag mit NUL) in Bytes. Sie liegt mit argv auf dem ersten Stack des
    /// Programms; die Grenze hält den Platz dafür klein und vorhersagbar.
    /// </summary>
    public const int MaxTotalBytes = 32 * 1024;

    /// <summary>
    /// Schreibbares temporäres Verzeichnis (fontconfig, Ladybird, mkstemp-Aufrufer). <c>/tmp</c> liegt in der Prozesswurzel: auf der
    /// Platte ein Unterordner der Wurzel, sonst im Arbeitsspeicher.
    /// </summary>
    public const string TmpDir = "/tmp";

    /// <summary>
    /// XDG-Laufzeitverzeichnis (Sockets, Sperrdateien); die Spezifikation verlangt Eigentum des Nutzers und Modus 0700. Unter
    /// <see cref="TmpDir"/>, damit es nie neben Nutzerdaten in der Wurzel landet.
    /// </summary>
    public const string RuntimeDir = "/tmp/xdg-runtime";

    /// <summary>XDG-Cache (fontconfig-Cache); wie der Standard <c>$HOME/.cache</c> bei <c>HOME=/</c>, also dauerhaft in der Wurzel.</summary>
    public const string CacheDir = "/.cache";

    /// <summary>
    /// Standardumgebung. Die ersten vier Einträge sind seit V28b unverändert; die drei neuen hängen hinten an, damit Programme, die
    /// nur nach Namen suchen, und der Vergleich mit <c>expected/s1/args_env_auxv.txt</c> stabil bleiben.
    /// </summary>
    public static readonly string[] Defaults =
    [
        "ZEHOS_ABI=1", "LANG=C", "PATH=/usr/bin:/bin", "HOME=/",
        "TMPDIR=" + TmpDir, "XDG_RUNTIME_DIR=" + RuntimeDir, "XDG_CACHE_HOME=" + CacheDir,
    ];

    /// <summary>
    /// Trennt führende Zuweisungen vom Programmaufruf. <paramref name="environment"/> ist <see cref="Defaults"/> mit den Zuweisungen:
    /// ein vorhandener Name wird an seiner Stelle ersetzt, ein neuer hinten angehängt, eine spätere Zuweisung desselben Namens gewinnt.
    /// Ein Wort zählt als Zuweisung, wenn vor dem ersten '=' ein gültiger Name steht (Buchstabe oder '_', dann auch Ziffern); so
    /// bleibt <c>./a=b</c> ein Programmpfad wie in der Shell.
    /// </summary>
    /// <returns>false mit <paramref name="error"/> bei fehlendem Programm, verbotenem Zeichen oder überschrittener Grenze.</returns>
    public static bool Split(string[] arguments, out string[] environment, out string[] argv, out string error)
    {
        var env = new List<string>(Defaults);
        int count = 0;
        while (count < arguments.Length && IsAssignment(arguments[count]))
        {
            string assignment = arguments[count];
            count++;
            if (count > MaxAssignments)
            {
                return Fail("höchstens " + MaxAssignments + " Zuweisungen", out environment, out argv, out error);
            }

            // Ein NUL schnitte den Eintrag auf dem Stack still ab; das Programm sähe einen anderen Wert als angegeben.
            if (assignment.Contains('\0'))
            {
                return Fail("Zuweisung mit NUL-Zeichen", out environment, out argv, out error);
            }

            if (Encoding.UTF8.GetByteCount(assignment) > MaxAssignmentBytes)
            {
                return Fail("Zuweisung länger als " + MaxAssignmentBytes + " Bytes", out environment, out argv, out error);
            }

            int nameLength = assignment.IndexOf('=') + 1;
            int existing = env.FindIndex(e => e.Length >= nameLength && string.CompareOrdinal(e, 0, assignment, 0, nameLength) == 0);
            if (existing >= 0)
            {
                env[existing] = assignment;
            }
            else
            {
                env.Add(assignment);
            }
        }

        if (count == arguments.Length)
        {
            return Fail("Programm fehlt nach den Zuweisungen", out environment, out argv, out error);
        }

        int total = 0;
        foreach (string entry in env)
        {
            total += Encoding.UTF8.GetByteCount(entry) + 1;
        }

        if (total > MaxTotalBytes)
        {
            return Fail("Umgebung größer als " + MaxTotalBytes + " Bytes", out environment, out argv, out error);
        }

        environment = env.ToArray();
        argv = arguments.AsSpan(count).ToArray();
        error = string.Empty;
        return true;
    }

    /// <summary>true, wenn <paramref name="word"/> die Form <c>NAME=WERT</c> mit einem Shell-Namen hat.</summary>
    public static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        for (int i = 0; i < equals; i++)
        {
            char c = word[i];
            bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
            if (!letter && (i == 0 || c < '0' || c > '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verzeichnisse, die <c>run</c> vor dem Start in der Prozesswurzel anlegt (Eltern zuerst, Modus ohne umask), damit die
    /// Standardwerte beschreibbar sind. Nur für Variablen mit ihrem Standardwert: Einen selbst gesetzten Pfad legt <c>run</c> wie
    /// <c>env</c> nicht an.
    /// </summary>
    public static List<(string Path, uint Mode)> DirectoriesFor(string[] environment)
    {
        bool tmp = Array.IndexOf(environment, "TMPDIR=" + TmpDir) >= 0;
        bool runtime = Array.IndexOf(environment, "XDG_RUNTIME_DIR=" + RuntimeDir) >= 0;
        bool cache = Array.IndexOf(environment, "XDG_CACHE_HOME=" + CacheDir) >= 0;
        var result = new List<(string, uint)>();
        if (tmp || runtime)
        {
            // 0777: Das Sticky-Bit von /tmp speichert LinuxFileSystem nicht (LinuxMode.PermissionMask), und es gibt nur einen Nutzer.
            result.Add((TmpDir, 0x1FF));
        }

        if (runtime)
        {
            result.Add((RuntimeDir, 0x1C0));
        }

        if (cache)
        {
            result.Add((CacheDir, 0x1ED));
        }

        return result;
    }

    private static bool Fail(string why, out string[] environment, out string[] argv, out string error)
    {
        environment = [];
        argv = [];
        error = why;
        return false;
    }
}
