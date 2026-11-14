using ProjektZeh.Input;

namespace ProjektZeh.Apps.Terminal;

internal static class TerminalClipboard
{
    private static string text = "";
    public static string Text
    {
        get => text;
        set => text = value.Length > LineEditor.MaximumLength ? value[..LineEditor.MaximumLength] : value;
    }
    public static void Clear() => text = "";
}
