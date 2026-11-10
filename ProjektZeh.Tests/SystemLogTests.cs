using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SystemLogTests
{
    [TestMethod]
    public void Write_RecordsTimestampComponentAndMessage()
    {
        var sink = new Mock<ISystemLogSink>(MockBehavior.Strict);
        sink.Setup(target => target.Write(It.IsAny<SystemLogEntry>()));
        var log = new SystemLog(2, () => 42, sink.Object);

        log.Information("Boot", "Initialisierung abgeschlossen.");

        SystemLogEntry entry = log.GetEntries()[0];
        Assert.AreEqual(42L, entry.ElapsedMilliseconds, "Der Eintrag muss die verstrichene Zeit enthalten.");
        Assert.AreEqual(LogLevel.Information, entry.Level, "Der Eintrag muss die Stufe enthalten.");
        Assert.AreEqual("Boot", entry.Component, "Der Eintrag muss die Komponente enthalten.");
        Assert.AreEqual("Initialisierung abgeschlossen.", entry.Message, "Der Eintrag muss die Meldung enthalten.");
        sink.Verify(target => target.Write(entry), Times.Once, "Der Eintrag muss seriell weitergegeben werden.");
        sink.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void Write_WhenCapacityIsReached_KeepsTheNewestEntriesInChronologicalOrder()
    {
        long milliseconds = 0;
        var log = new SystemLog(2, () => ++milliseconds, null);

        log.Information("Boot", "eins");
        log.Warning("Input", "zwei");
        log.Error("Display", "drei");

        SystemLogEntry[] entries = log.GetEntries();
        Assert.AreEqual(2, log.Count, "Der Puffer darf seine Kapazitaet nicht ueberschreiten.");
        Assert.AreEqual(2, entries.Length, "Die Momentaufnahme darf nur gepufferte Eintraege enthalten.");
        Assert.AreEqual("zwei", entries[0].Message, "Der aelteste verbliebene Eintrag muss zuerst kommen.");
        Assert.AreEqual("drei", entries[1].Message, "Der juengste Eintrag muss zuletzt kommen.");
        Assert.AreEqual(2L, entries[0].ElapsedMilliseconds, "Zeitstempel muessen erhalten bleiben.");
        Assert.AreEqual(3L, entries[1].ElapsedMilliseconds, "Zeitstempel muessen erhalten bleiben.");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Constructor_NonPositiveCapacity_RejectsTheConfiguration(int capacity)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SystemLog(capacity, () => 0, null),
            "Ein begrenzter Logpuffer benoetigt eine positive Kapazitaet.");
    }
}
