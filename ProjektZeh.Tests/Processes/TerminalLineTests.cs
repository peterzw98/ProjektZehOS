using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
using ProjektZeh.Processes;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V3 raw: die Zeile eines Zeileneditors wie ein VT100 sie zeigt, und Tasten als Terminalbytes.</summary>
[TestClass]
public sealed class TerminalLineTests
{
    private static (string Text, int Cursor) Render(string raw)
    {
        string text = TerminalLine.Render(raw, out int cursor);
        return (text, cursor);
    }

    [TestMethod]
    public void UeberschreibenLoeschenUndBewegen()
    {
        Assert.AreEqual(("> abc", 5), Render("> abc"));
        Assert.AreEqual(("Xbc", 1), Render("abc\rX"), "CR setzt nur den Cursor zurueck");
        Assert.AreEqual(("> a", 3), Render("> abc\x1b[2D\x1b[K"));
        Assert.AreEqual(("   d", 2), Render("abcd\x1b[2D\x1b[1K"), "K 1 bis einschliesslich Cursor");
        Assert.AreEqual(("", 3), Render("abc\x1b[2K"));
        Assert.AreEqual(("ab", 1), Render("ab\b"), "Backspace bewegt nur");
        Assert.AreEqual(("aXc", 2), Render("abc\x1b[2GX"));
        Assert.AreEqual(("aXc", 2), Render("abc\x1b[30;2HX"), "H: nur die Spalte zaehlt");
        Assert.AreEqual(("ab  Z", 5), Render("ab\x1b[2CZ"), "hinter dem Text wird aufgefuellt");
        Assert.AreEqual(("abc", 1), Render("a\x1b[sbc\x1b[u"));
        Assert.AreEqual(("abc", 1), Render("a\x1b" + "7bc\x1b" + "8"));
        Assert.AreEqual(("a       b", 9), Render("a\tb"));
    }

    [TestMethod]
    public void FarbenPrivateFolgenUndTitelFallenWeg()
    {
        Assert.AreEqual(("> 1+1", 5), Render("\x1b[?25l\x1b[32m> \x1b[0m1+1\x1b[?25h"));
        Assert.AreEqual(("ab", 2), Render("a\x1b]0;Titel\ab"));
        Assert.AreEqual(("ab", 2), Render("a\x1b]0;Titel\x1b\\b"));
        Assert.AreEqual(("ab", 2), Render("ab\x1b[1A\x1b[1B"), "Zeilenwechsel gibt es nicht");
        Assert.AreEqual(("ab", 2), Render("ab\x1b[3"), "angefangene Folge am Ende");
        Assert.AreEqual(("ab", 2), Render("ab\x1b"));
    }

    [TestMethod]
    public void GrosseSprungweitenSindBegrenzt()
    {
        var (text, cursor) = Render("a\x1b[999999999CZ");
        Assert.AreEqual(TerminalLine.MaxColumn, cursor);
        Assert.AreEqual("a", text, "jenseits der Grenze wird nichts geschrieben");
        Assert.AreEqual(0, Render("\x1b[99D").Cursor);
    }

    [TestMethod]
    public void TastenAlsTerminalbytes()
    {
        static string Bytes(byte[]? b) => b == null ? "null" : System.BitConverter.ToString(b);
        Assert.AreEqual("0D", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, (byte)PhysicalKey.Enter), false)));
        Assert.AreEqual("7F", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, (byte)PhysicalKey.Backspace), false)));
        Assert.AreEqual("1B", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, (byte)PhysicalKey.Escape), false)));
        Assert.AreEqual("1B-5B-41", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, (byte)PhysicalKey.Up), false)));
        Assert.AreEqual("1B-5B-44", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, (byte)PhysicalKey.Left), false)));
        Assert.AreEqual("1B-5B-33-7E", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, (byte)PhysicalKey.Delete), false)));
        Assert.AreEqual("null", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, 30, IsTextKey: true), false)), "Zeichen kommt als Text");
        Assert.AreEqual("C3-A4", Bytes(TerminalKeys.Encode(new(InputEventType.Text, Character: 'ä'), false)));
        Assert.AreEqual("01", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, 30, IsTextKey: true), true)), "Strg+A");
        Assert.AreEqual("03", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, 46, IsTextKey: true), true)), "Strg+C");
        Assert.AreEqual("1A", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, 44, IsTextKey: true), true)), "Strg+Z");
        Assert.AreEqual("null", Bytes(TerminalKeys.Encode(new(InputEventType.Text, Character: 'a'), true)), "Text bei Strg nicht doppelt");
        Assert.AreEqual("null", Bytes(TerminalKeys.Encode(new(InputEventType.KeyUp, (byte)PhysicalKey.Enter), false)));
        Assert.AreEqual("null", Bytes(TerminalKeys.Encode(new(InputEventType.KeyDown, (byte)PhysicalKey.F1), false)));
    }
}
