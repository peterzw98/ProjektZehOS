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

    /// <summary>
    /// true im Rohmodus (das Programm hat ICANON ausgeschaltet, etwa ein Zeileneditor): Tasten gehen einzeln als Bytes an das
    /// Programm (<see cref="SubmitRaw"/>), die Shell editiert und zeigt nichts selbst; <see cref="Prompt"/> ist dann die Zeile,
    /// die das Programm gezeichnet hat, mit dem Cursor bei <see cref="PromptCursor"/>.
    /// </summary>
    bool RawInput { get; }

    /// <summary>Cursorspalte in <see cref="Prompt"/> im Rohmodus; sonst -1 (der Cursor gehoert der Shell).</summary>
    int PromptCursor { get; }

    /// <summary>true, solange Strg+C das Programm abbricht (ISIG); sonst bekommt es das Byte 0x03.</summary>
    bool SignalKeys { get; }

    /// <summary>Gibt Tastenbytes an das Programm (Rohmodus); ein CR wird mit ICRNL zum Zeilenende.</summary>
    void SubmitRaw(byte[] bytes);

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
