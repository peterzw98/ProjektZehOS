namespace ProjektZeh.Input;

internal static class GermanKeyboard
{
    internal static char Translate(byte scanCode, char character, bool shift, bool capsLock, bool control, bool altGr)
    {
        // Cosmos 3.0.94 verwechselt die ISO-Tasten 43/86 und hat unvollstaendige CapsLock-Spalten.
        if (scanCode == 43) return control || altGr ? '\0' : shift ? '\'' : '#';
        if (scanCode == 86) return altGr ? '|' : control ? '\0' : shift ? '>' : '<';
        if (!altGr && char.IsLetter(character) && scanCode != 12)
            return shift ^ capsLock ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character);
        return character;
    }
}
