using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class BootServiceTests
{
    [TestMethod]
    public void RunStep_FailureAndException_LogsErrorsAndAllowsFollowingStep()
    {
        var log = new SystemLog(16, () => 0, null);
        var boot = new BootService(log);
        Assert.IsFalse(boot.RunStep("Storage", () => ResultErr.CreateFailure(ResultCode.Failure, "Kein Laufwerk")),
            "Fehler muessen erkennbar bleiben.");
        Assert.IsFalse(boot.RunStep("Display", () => throw new InvalidOperationException("Testfehler")),
            "Optionale Initialisierungsfehler muessen abgefangen werden.");
        Assert.IsTrue(boot.RunStep("Shell", ResultErr.CreateSuccess), "Nach Fehlern muss die Diagnose starten koennen.");
        SystemLogEntry[] entries = log.GetEntries();
        Assert.AreEqual(LogLevel.Error, entries[1].Level, "Ein Fehlerstatus muss protokolliert werden.");
        StringAssert.Contains(entries[3].Message, "Testfehler", "Exceptions muessen ihre Ursache behalten.");
        Assert.AreEqual("Shell", entries[5].Component, "Der folgende Schritt muss ausgefuehrt werden.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InitializeDisplay_FailedDisplayOrForcedRecovery_RestoresTextConsole(bool forceRecovery)
    {
        var boot = new BootService(new SystemLog(16, () => 0, null));
        int displayCalls = 0;
        int recoveryCalls = 0;
        bool ready = boot.InitializeDisplay(forceRecovery, () =>
        {
            displayCalls++;
            throw new InvalidOperationException("Grafikfehler");
        }, () =>
        {
            recoveryCalls++;
            return ResultErr.CreateSuccess();
        });
        Assert.IsTrue(ready, "Die Textshell muss nach der Wiederherstellung starten koennen.");
        Assert.IsTrue(boot.IsRecoveryMode, "Der Wiederherstellungsmodus muss erkennbar sein.");
        Assert.AreEqual(forceRecovery ? 0 : 1, displayCalls, "Recovery muss die normale Anzeige umgehen.");
        Assert.AreEqual(1, recoveryCalls, "Die Textkonsole muss genau einmal wiederhergestellt werden.");
    }

    [TestMethod]
    public void InitializeDisplay_Success_DoesNotRunRecovery()
    {
        var boot = new BootService(new SystemLog(16, () => 0, null));
        Assert.IsTrue(boot.InitializeDisplay(false, ResultErr.CreateSuccess,
            () => throw new InvalidOperationException("Recovery darf nicht laufen")), "Normale Anzeige muss starten.");
        Assert.IsFalse(boot.IsRecoveryMode, "Erfolg darf keinen Recovery-Modus aktivieren.");
    }

    [TestMethod]
    public void InitializeDisplay_RecoveryAlsoFails_ReportsUnavailableConsole()
    {
        var boot = new BootService(new SystemLog(16, () => 0, null));
        Assert.IsFalse(boot.InitializeDisplay(true, ResultErr.CreateSuccess,
            () => ResultErr.CreateFailure(ResultCode.Failure, "Kein Framebuffer")),
            "Ohne Textkonsole darf keine erreichbare Shell behauptet werden.");
    }
}
