using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shell;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class CommandLineParserTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  \t ")]
    public void Parse_EmptyInput_ReturnsNoTokens(string? input)
    {
        var result = CommandLineParser.Parse(input);

        Assert.IsTrue(result.IsOK(), "Leere Eingabe ist kein Fehler.");
        Assert.AreEqual(0, result.Value!.Length, "Leere Eingabe darf keine Argumente erzeugen.");
    }

    [TestMethod]
    public void Parse_QuotedAndUnquotedArguments_PreservesTheirContents()
    {
        var result = CommandLineParser.Parse(" \techo  \"Hallo ZehOS\" \"\" /mnt/test.txt ");

        Assert.IsTrue(result.IsOK(), "Geschlossene Anfuehrungszeichen muessen erlaubt sein.");
        CollectionAssert.AreEqual(new[] { "echo", "Hallo ZehOS", "", "/mnt/test.txt" }, result.Value,
            "Leerzeichen in Anfuehrungszeichen und leere Argumente muessen erhalten bleiben.");
    }

    [TestMethod]
    public void Parse_AdjacentQuotedParts_FormsOneArgument()
    {
        var result = CommandLineParser.Parse("echo vor\"mit Leerzeichen\"nach");

        Assert.IsTrue(result.IsOK(), "Zusammenhaengende Teile sollen ein Argument bilden.");
        CollectionAssert.AreEqual(new[] { "echo", "vormit Leerzeichennach" }, result.Value,
            "Nur Leerraum ausserhalb von Anfuehrungszeichen trennt Argumente.");
    }

    [TestMethod]
    public void Parse_Backslashes_PreservesThemLiterally()
    {
        var result = CommandLineParser.Parse("echo \"C:\\games\\doom\"");

        Assert.IsTrue(result.IsOK(), "Backslashes sind normale Zeichen.");
        CollectionAssert.AreEqual(new[] { "echo", "C:\\games\\doom" }, result.Value,
            "Der Parser darf Pfadzeichen nicht als Escape-Sequenzen verschlucken.");
    }

    [DataTestMethod]
    [DataRow("echo \"offen")]
    [DataRow("echo test\"")]
    public void Parse_UnclosedQuotation_ReturnsAnInputError(string input)
    {
        var result = CommandLineParser.Parse(input);

        Assert.IsFalse(result.IsOK(), "Nicht geschlossene Anfuehrungszeichen sind ungueltig.");
        Assert.AreEqual(ResultCode.InvalidArgument, result.Status.Code, "Der Fehler muss eine ungueltige Eingabe kennzeichnen.");
        Assert.IsNull(result.Value, "Ein fehlerhafter Befehl darf kein ausfuehrbares Teilergebnis liefern.");
        StringAssert.Contains(result.Status.Message, "Anfuehrungszeichen", "Die Meldung muss die Ursache nennen.");
    }
}
