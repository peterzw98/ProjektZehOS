using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Virtual;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SystemInfoSnapshotTests
{
    private static string Text(ResultObject<byte[]> result)
    {
        Assert.IsTrue(result.IsOK(), result.Status.ToString());
        return new UTF8Encoding(false, true).GetString(result.Value!);
    }

    [TestMethod]
    public void UptimeShowsSecondsAndMarksMissingTimer()
    {
        Assert.AreEqual("12.345\n", Text(SystemInfoSnapshot.Uptime(12345)));
        Assert.AreEqual("0.007\n", Text(SystemInfoSnapshot.Uptime(7)));
        Assert.AreEqual("unavailable\n", Text(SystemInfoSnapshot.Uptime(null)));
        Assert.AreEqual("unavailable\n", Text(SystemInfoSnapshot.Uptime(-1)));
    }

    [TestMethod]
    public void MemoryComputesBytesAndRejectsInconsistentCounters()
    {
        Assert.AreEqual("MemTotal: 40960 B\nMemFree: 8192 B\nPageSize: 4096 B\n", Text(SystemInfoSnapshot.Memory(4096, 10, 2)));
        Assert.IsFalse(SystemInfoSnapshot.Memory(4096, 10, 11).IsOK());
        Assert.IsFalse(SystemInfoSnapshot.Memory(0, 10, 2).IsOK());
        Assert.IsFalse(SystemInfoSnapshot.Memory(4096, ulong.MaxValue, 0).IsOK());
    }

    [TestMethod]
    public void FieldsAreMaskedToOneTokenAndStayValidUtf8()
    {
        var result = SystemInfoSnapshot.Mounts([new SystemInfoMount("a b", "src\nx", "/da\\ta", true), new SystemInfoMount("n\ud800", "\u0085", "/", false)]);
        string text = Text(result);
        Assert.AreEqual("src\\u000ax /da\\u005cta a\\u0020b ro\n\\u0085 / n\\ud800 rw\n", text);
        Assert.AreEqual(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.AreEqual("😀", SystemInfoSnapshot.Field("😀"));
    }

    [TestMethod]
    public void LogShowsOnlyTimeLevelAndAllowlistedCategory()
    {
        var entries = new[]
        {
            new SystemLogEntry(5, LogLevel.Information, "Boot", "MAC 52:54:00:12:34:56"),
            new SystemLogEntry(6, LogLevel.Error, "Storage", "Befehl: disk init sata0 --yes"),
            new SystemLogEntry(7, LogLevel.Warning, "Desktop", "System.IO.IOException: /data/geheim.txt"),
        };
        string text = Text(SystemInfoSnapshot.Log(entries));
        Assert.AreEqual("5 INFORMATION Boot\n6 ERROR other\n7 WARNING Desktop\n", text);
        foreach (string forbidden in new[] { "MAC", "52:54", "disk", "geheim", "IOException", "/data" })
            Assert.IsFalse(text.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    public void RecordLimitIs128AndOneMoreIsAnErrorNotATruncation()
    {
        var logs = Enumerable.Range(0, 128).Select(i => new SystemLogEntry(i, LogLevel.Information, "Boot", "x")).ToArray();
        Assert.AreEqual(128, Text(SystemInfoSnapshot.Log(logs)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var tooMany = logs.Append(new SystemLogEntry(128, LogLevel.Information, "Boot", "x")).ToArray();
        Assert.IsFalse(SystemInfoSnapshot.Log(tooMany).IsOK());
        var features = Enumerable.Range(0, 100).Select(i => new SystemInfoComponent("f" + i, true)).ToArray();
        var drivers = Enumerable.Range(0, 29).Select(i => new SystemInfoDriver("d" + i, i)).ToArray();
        Assert.IsFalse(SystemInfoSnapshot.Components(features, drivers).IsOK());
        Assert.IsTrue(SystemInfoSnapshot.Components(features, drivers.Take(28).ToArray()).IsOK());
    }

    [TestMethod]
    public void FieldLimitIs256BytesAfterMasking()
    {
        Assert.IsTrue(SystemInfoSnapshot.Blocks([new SystemInfoBlock(new string('a', 256), 1, 0)]).IsOK());
        Assert.IsFalse(SystemInfoSnapshot.Blocks([new SystemInfoBlock(new string('a', 257), 1, 0)]).IsOK());
        // Je Leerzeichen entstehen 6 Zeichen: 43 * 6 = 258 > 256.
        Assert.IsFalse(SystemInfoSnapshot.Blocks([new SystemInfoBlock(new string(' ', 43), 1, 0)]).IsOK());
        // Umlaute zaehlen mit zwei Byte: 129 * 2 = 258.
        Assert.IsFalse(SystemInfoSnapshot.Blocks([new SystemInfoBlock(new string('ä', 129), 1, 0)]).IsOK());
    }

    [TestMethod]
    public void TotalSizeLimitIs64KiB()
    {
        var devices = Enumerable.Range(0, 128).Select(i => new SystemInfoDevice(new string('p', 200), new string('b', 200), new string('d', 200), "ok")).ToArray();
        var result = SystemInfoSnapshot.Devices(devices);
        Assert.IsFalse(result.IsOK());
        Assert.IsTrue(SystemInfoSnapshot.Devices(devices.Take(60).ToArray()).IsOK());
    }

    [TestMethod]
    public void ComponentsAndBlocksFormat()
    {
        Assert.AreEqual("feature Pci compiled\nfeature Usb absent\ndriver Ahci 10\n",
            Text(SystemInfoSnapshot.Components([new("Pci", true), new("Usb", false)], [new("Ahci", 10)])));
        Assert.AreEqual("sata0 8589934592 2\n", Text(SystemInfoSnapshot.Blocks([new("sata0", 8589934592UL, 2)])));
        Assert.AreEqual(1024UL, SystemInfoSnapshot.SizeBytes(2, 512).Value);
        Assert.IsFalse(SystemInfoSnapshot.SizeBytes(ulong.MaxValue, 512).IsOK());
        Assert.IsFalse(SystemInfoSnapshot.SizeBytes(1, 0).IsOK());
    }

    [TestMethod]
    public void OpenedFileGivesConsistentPartialReadsAndStableEof()
    {
        int version = 0;
        var open = SystemInfoFile.Open(() => { version++; return SystemInfoSnapshot.Mounts([new SystemInfoMount("data" + version, "sata0", "/data", false)]); });
        Assert.IsTrue(open.IsOK());
        var file = open.Value!;
        Assert.AreNotEqual(0L, file.Length);
        var all = new byte[(int)file.Length];
        var first = new byte[5];
        var second = new byte[all.Length - 5];
        Assert.AreEqual(5, file.Read(0, first).Value);
        Assert.AreEqual(second.Length, file.Read(5, second).Value);
        first.CopyTo(all, 0);
        second.CopyTo(all, 5);
        Assert.AreEqual("sata0 /data data1 rw\n", Encoding.UTF8.GetString(all));
        Assert.AreEqual(0, file.Read(file.Length, new byte[4]).Value);
        Assert.AreEqual(0, file.Read(file.Length + 100, new byte[4]).Value);
        Assert.AreEqual(0, file.Read(file.Length, new byte[4]).Value);
        Assert.AreEqual(1, version, "Erzeugung nur einmal je Open");
        Assert.IsFalse(file.Read(-1, new byte[4]).IsOK());
        Assert.IsTrue(SystemInfoFile.Open(() => { version++; return SystemInfoSnapshot.Mounts([]); }).IsOK());
        Assert.AreEqual(2, version, "erneutes Open erzeugt neu");
    }

    [TestMethod]
    public void OpenPassesProducerFailureThrough()
    {
        var open = SystemInfoFile.Open(() => SystemInfoSnapshot.Memory(4096, 1, 2));
        Assert.IsFalse(open.IsOK());
        Assert.IsNull(open.Value);
    }

    [TestMethod]
    public void EverySeparatorAndControlClassIsMasked()
    {
        var expected = new Dictionary<string, string>
        {
            ["\u0001"] = "\\u0001",
            ["\u2028"] = "\\u2028",
            ["\u2029"] = "\\u2029",
            ["\u00a0"] = "\\u00a0",
            ["\u3000"] = "\\u3000",
            ["\u200b"] = "\\u200b",
            ["\u202e"] = "\\u202e",
            ["\ufeff"] = "\\ufeff",
            ["\ue000"] = "\\ue000",
            ["\u0378"] = "\\u0378",
            ["\udc00"] = "\\udc00",
            ["\ud800\ud800"] = "\\ud800\\ud800",
            ["\udb40\udc01"] = "\\U000e0001",
            ["\udb80\udc00"] = "\\U000f0000",
            ["\ud83d\ude00"] = "\ud83d\ude00",
            ["\u00e4"] = "\u00e4",
        };
        foreach (var (input, output) in expected)
            Assert.AreEqual(output, SystemInfoSnapshot.Field(input), $"U+{(int)input[0]:X4}");
    }

    [TestMethod]
    public void EmptyAndNullFieldsBecomeDashSoColumnsDoNotShift()
    {
        Assert.AreEqual("- /x - rw\n", Text(SystemInfoSnapshot.Mounts([new SystemInfoMount("", null!, "/x", false)])));
    }

    [TestMethod]
    public void ExactLimitsAreAcceptedAndOneMoreIsRejected()
    {
        SystemInfoMount[] Mounts(int lastNameLength) => Enumerable.Range(0, 128)
            .Select(i => new SystemInfoMount(new string('c', i == 127 ? lastNameLength : 106), new string('a', 200), new string('b', 200), false)).ToArray();
        var exact = SystemInfoSnapshot.Mounts(Mounts(106));
        Assert.IsTrue(exact.IsOK());
        Assert.AreEqual(65536, exact.Value!.Length);
        Assert.IsFalse(SystemInfoSnapshot.Mounts(Mounts(107)).IsOK());
        var mounts = Enumerable.Range(0, 129).Select(i => new SystemInfoMount("n", "s", "/m", false)).ToArray();
        Assert.IsTrue(SystemInfoSnapshot.Mounts(mounts.Take(128).ToArray()).IsOK());
        Assert.IsFalse(SystemInfoSnapshot.Mounts(mounts).IsOK());
        var devices = Enumerable.Range(0, 129).Select(i => new SystemInfoDevice("p", "b", "d", "ok")).ToArray();
        Assert.IsTrue(SystemInfoSnapshot.Devices(devices.Take(128).ToArray()).IsOK());
        Assert.IsFalse(SystemInfoSnapshot.Devices(devices).IsOK());
    }

    [TestMethod]
    public void OpenTurnsProducerExceptionIntoFailure()
    {
        var open = SystemInfoFile.Open(() => throw new InvalidOperationException("/data/geheim.txt"));
        Assert.IsFalse(open.IsOK());
        Assert.IsFalse(open.Status.ToString().Contains("geheim", StringComparison.Ordinal));
        Assert.IsNull(open.Value);
    }

    [TestMethod]
    public void VersionKeepsSpacesButMasksControlCharacters()
    {
        Assert.AreEqual("version: ZehOS 0.5.20 (Cosmos 3.0.97)\n", Text(SystemInfoSnapshot.Version("ZehOS 0.5.20 (Cosmos 3.0.97)")));
        Assert.AreEqual("version: a\\u000ab\\u005c \\u00a0\n", Text(SystemInfoSnapshot.Version("a\nb\\ \u00a0")));
    }
}
