namespace ProjektZeh.Shell;

/// <summary>
/// Ein Programm, das im Vordergrund einer Shell laeuft (Ladybird V3: run). Solange es angemeldet ist, gehen Eingabezeilen an das
/// Programm statt an die Shell, und die Ereignisschleife holt seine Ausgabe mit <see cref="Drain"/> ab. Alle Methoden laufen auf
/// der Ereignisschleife und blockieren nie.
/// </summary>
internal interface IShellForeground
{
    /// <summary>Angefangene Ausgabezeile, auf deren Fortsetzung das Programm mit einer Eingabe wartet (etwa "> "); sonst leer.</summary>
    string Prompt { get; }

    /// <summary>true nach <see cref="Interrupt"/>, solange das Programm noch nicht beendet ist.</summary>
    bool IsInterrupted { get; }

    /// <summary>Gibt eine Eingabezeile an das Programm (mit Zeilenende).</summary>
    void Submit(string line);

    /// <summary>Eingabeende (Strg+D): das naechste Lesen ohne Bytes liefert 0.</summary>
    void EndInput();

    /// <summary>Bricht das Programm ab (Strg+C, Esc).</summary>
    void Interrupt();

    /// <summary>Die Shell gibt das Programm auf (Fenster zu, zweiter Abbruch): weitere Ausgaben verfallen, Schreiben wartet nicht mehr.</summary>
    void Detach();

    /// <summary>Schreibt die bisher gesammelte Ausgabe; nach dem Ende zuletzt die Statuszeile. true, wenn etwas geschrieben wurde.</summary>
    bool Drain(ITextOutput output);

    /// <summary>true, wenn das Programm beendet ist und <see cref="Drain"/> alles samt Statuszeile ausgegeben hat.</summary>
    bool IsDone { get; }
}
