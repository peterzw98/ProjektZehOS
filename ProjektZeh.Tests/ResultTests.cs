using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class ResultTests
{
    [TestMethod]
    public void DefaultResults_WithoutAnOperation_DoNotReportSuccess()
    {
        var status = new ResultErr();
        var result = new ResultObject<string>();

        Assert.IsFalse(status.IsOK(), "Ein noch nicht gesetztes Ergebnis darf keinen Erfolg melden.");
        Assert.AreEqual(ResultCode.Uninitialized, status.Code, "Der Ausgangszustand muss erkennbar sein.");
        Assert.AreEqual(string.Empty, status.Message, "Ohne Fehler gibt es keinen erfundenen Meldungstext.");
        Assert.IsFalse(result.IsOK(), "Auch ein Wertergebnis beginnt ohne Erfolg.");
        Assert.IsNull(result.Value, "Ohne Operation gibt es keinen Wert.");
    }

    [TestMethod]
    public void CreateSuccess_WithoutAValue_ReportsSuccess()
    {
        var status = ResultErr.CreateSuccess();

        Assert.IsTrue(status.IsOK(), "Ausdruecklich gesetzter Erfolg muss erkennbar sein.");
        Assert.AreEqual(ResultCode.Success, status.Code, "Erfolg muss den passenden Status setzen.");
        Assert.AreEqual(string.Empty, status.Message, "Erfolg braucht keine Fehlermeldung.");
        Assert.IsNull(status.ExceptionDetails, "Erfolg darf keine Exceptiondetails enthalten.");
    }

    [DataTestMethod]
    [DataRow(ResultCode.InvalidArgument)]
    [DataRow(ResultCode.MissingArgument)]
    [DataRow(ResultCode.NotFound)]
    [DataRow(ResultCode.Failure)]
    [DataRow(ResultCode.Exception)]
    public void CreateFailure_ProvidedDetails_ArePreserved(ResultCode code)
    {
        var status = ResultErr.CreateFailure(code, "Die Operation ist fehlgeschlagen.");

        Assert.IsFalse(status.IsOK(), "Ein Fehlercode darf keinen Erfolg ergeben.");
        Assert.AreEqual(code, status.Code, "Die Ursache muss beim Aufrufer ankommen.");
        Assert.AreEqual("Die Operation ist fehlgeschlagen.", status.Message, "Der echte Meldungstext muss erhalten bleiben.");
    }

    [TestMethod]
    public void CreateFailure_SuccessCode_ReportsInvalidArguments()
    {
        var status = ResultErr.CreateFailure(ResultCode.Success, "Fehler");
        var result = ResultObjectFactory.CreateFailure<string>(ResultCode.Success, "Fehler");

        Assert.AreEqual(ResultCode.InvalidArgument, status.Code, "Eine widerspruechliche Fehleranforderung muss auffallen.");
        Assert.IsFalse(result.IsOK(), "Auch das Wertergebnis darf den Widerspruch nicht als Erfolg behandeln.");
    }

    [TestMethod]
    public void CreateFailure_MissingMessage_UsesAnEmptyMessage()
    {
        var status = ResultErr.CreateFailure(ResultCode.Failure, null);

        Assert.IsFalse(status.IsOK(), "Eine fehlende Meldung aendert den Fehlerstatus nicht.");
        Assert.AreEqual(string.Empty, status.Message, "Aufrufer sollen einen verwendbaren Meldungstext erhalten.");
    }

    [DataTestMethod]
    [DataRow("Ergebnis")]
    [DataRow(null)]
    public void CreateSuccess_WithAValue_PreservesTheValue(string? value)
    {
        var result = ResultObjectFactory.CreateSuccess<string>(value);

        Assert.IsTrue(result.IsOK(), "Ein ausdruecklich erfolgreiches Ergebnis muss als Erfolg gelten.");
        Assert.AreEqual(value, result.Value, "Auch ein erlaubter null-Wert muss unveraendert bleiben.");
    }

    [TestMethod]
    public void CreateFailure_WithAValueType_DoesNotTurnTheDefaultValueIntoSuccess()
    {
        var result = ResultObjectFactory.CreateFailure<int>(ResultCode.NotFound, "Kein Wert gefunden.");

        Assert.IsFalse(result.IsOK(), "Ein Standardwert darf keinen Erfolg vortaeuschen.");
        Assert.AreEqual(ResultCode.NotFound, result.Status.Code, "Die Ursache muss vom Wert getrennt bleiben.");
        Assert.AreEqual(0, result.Value, "Ohne Wert bleibt der Standardwert des Typs erhalten.");
    }

    [TestMethod]
    public void CreateFailure_ExistingStatus_PreservesTheFailure()
    {
        var status = ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Eingabe.");
        var result = ResultObjectFactory.CreateFailure<string>(status);

        Assert.AreSame(status, result.Status, "Ein unveraenderlicher Fehlerstatus kann direkt weitergereicht werden.");
        Assert.IsFalse(result.IsOK(), "Der weitergereichte Fehler darf nicht verloren gehen.");
        Assert.IsNull(result.Value, "Ein Fehler liefert keinen Ersatzwert.");
    }

    [TestMethod]
    public void CreateFailure_MissingStatus_ReportsTheMissingArgument()
    {
        var result = ResultObjectFactory.CreateFailure<string>(null);

        Assert.AreEqual(ResultCode.MissingArgument, result.Status.Code, "Ein fehlender Status muss eine klare Ursache haben.");
        Assert.IsFalse(result.IsOK(), "Ohne Fehlerstatus darf die Operation keinen Erfolg vortaeuschen.");
    }

    [TestMethod]
    public void CreateFailure_SuccessfulStatus_RejectsTheContradiction()
    {
        var result = ResultObjectFactory.CreateFailure<string>(ResultErr.CreateSuccess());

        Assert.AreEqual(ResultCode.InvalidArgument, result.Status.Code, "Erfolg ist kein weiterreichbarer Fehler.");
        Assert.IsFalse(result.IsOK(), "Eine ungueltige Fehleranforderung darf keinen Erfolg liefern.");
    }

    [TestMethod]
    public void CreateFromException_PreservesTheActualDiagnosticDetails()
    {
        var exception = new InvalidOperationException("Die Operation ist fehlgeschlagen.");
        var status = ResultErr.CreateFromException(exception);
        var result = ResultObjectFactory.CreateFromException<string>(exception);

        Assert.AreEqual(ResultCode.Exception, status.Code, "Der Fehler muss als Exception erkennbar sein.");
        Assert.AreEqual(exception.Message, status.Message, "Die wirkliche Exceptionmeldung muss erhalten bleiben.");
        Assert.AreEqual(exception.ToString(), status.ExceptionDetails, "Die Diagnose muss die vollstaendige Exception enthalten.");
        Assert.AreEqual(status.ExceptionDetails, result.Status.ExceptionDetails, "Wertergebnisse muessen dieselbe Diagnose weiterreichen.");
        Assert.IsFalse(result.IsOK(), "Eine Exception darf keinen Erfolg melden.");
        Assert.IsNull(result.Value, "Eine Exception liefert keinen Ergebniswert.");
    }

    [TestMethod]
    public void CreateFromException_Null_ReportsTheMissingException()
    {
        var result = ResultObjectFactory.CreateFromException<string>(null);

        Assert.AreEqual(ResultCode.MissingArgument, result.Status.Code, "Fehlende Exceptiondaten duerfen keine weitere Exception ausloesen.");
        Assert.IsNull(result.Status.ExceptionDetails, "Ohne Exception duerfen keine Details erfunden werden.");
    }

    [TestMethod]
    public void ToString_FailureWithException_IncludesTheCauseAndDetails()
    {
        var exception = new InvalidOperationException("Diagnose");
        var result = ResultObjectFactory.CreateFromException<string>(exception);

        StringAssert.Contains(result.ToString(), "Exception: Diagnose", "Die Diagnose soll Status und Meldung enthalten.");
        StringAssert.Contains(result.ToString(), exception.ToString(), "Die Diagnose soll die eigentlichen Exceptiondetails enthalten.");
    }
}
