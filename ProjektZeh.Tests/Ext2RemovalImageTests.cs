using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

/// <summary>
/// Regression fuer den Cosmos-RemoveEntry-Fehler an einem echten ext2-Image (4 KiB Bloecke, mke2fs/debugfs) mit e2fsck als
/// unabhaengigem Pruefer: Das Original verschmolz den ersten Record eines Folgeblocks mit dem letzten Record des Vorgaengerblocks
/// (Befund codex-upper-test: rec_len 24 -> 40, e2fsck 0 -> 12). <see cref="Ext2DirectoryRemoval"/> muss e2fsck sauber lassen.
/// Ohne e2fsprogs (etwa unter Windows) sind die Tests inconclusive.
/// </summary>
[TestClass]
public sealed class Ext2RemovalImageTests
{
    private const int BlockSize = 4096;
    private const int Entries = 600;
    private string directory = "";
    private string image = "";

    [TestInitialize]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "zehos-ext2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        image = Path.Combine(directory, "data.img");
        string empty = Path.Combine(directory, "leer");
        File.WriteAllBytes(empty, []);
        Tool("mke2fs", $"-q -t ext2 -b {BlockSize} -O ^dir_index -F \"{image}\" 8M");
        var commands = new StringBuilder("mkdir d\ncd d\n");
        for (int index = 0; index < Entries; index++) commands.Append(CultureInfo.InvariantCulture, $"write {empty} n{index:D4}\n");
        // Platz fuer die Ersatzlinks entfernter Eintraege, damit deren Inodes nicht verwaist zurueckbleiben.
        commands.Append("cd /\nmkdir k\nexpand_dir k\nexpand_dir k\nexpand_dir k\n");
        Tool("debugfs", $"-w -f - \"{image}\"", commands.ToString());
        Assert.AreEqual(0, Check().ExitCode, "frisches Image ist sauber");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void FirstEntryOfSecondBlock_RemovedByPlug_KeepsE2fsckClean()
    {
        var (blocks, data) = ReadDirectory();
        Assert.IsTrue(blocks.Count >= 3, "Verzeichnis ueber mehrere Bloecke");
        var (name, inode) = RecordAt(data, BlockSize);

        KeepInode(inode);
        Assert.AreEqual(1, Ext2DirectoryRemoval.Remove(data, BlockSize, Encoding.UTF8.GetBytes(name)));
        WriteDirectory(blocks, data);

        var check = Check();
        Assert.AreEqual(0, check.ExitCode, check.Output);
        Assert.IsFalse(List().Contains(name), $"{name} entfernt");
        Assert.AreEqual(Entries - 1, List().Count(entry => entry.StartsWith('n')));
    }

    [TestMethod]
    public void FirstEntryOfSecondBlock_MergedAcrossBlock_IsReportedByE2fsck()
    {
        // Gegenprobe mit der Regel des Originals: der letzte Record von Block 0 schluckt den ersten von Block 1. So sieht
        // e2fsck den Fehler; ohne diese Gegenprobe koennte der obige Test auch mit einem blinden Pruefer gruen sein.
        var (blocks, data) = ReadDirectory();
        var (_, inode) = RecordAt(data, BlockSize);
        int last = LastRecord(data, 0);
        ushort recLen = BitConverter.ToUInt16(data, last + 4);
        ushort firstLen = BitConverter.ToUInt16(data, BlockSize + 4);
        KeepInode(inode);
        BitConverter.TryWriteBytes(data.AsSpan(last + 4, 2), (ushort)(recLen + firstLen));
        WriteDirectory(blocks, data);

        var check = Check();
        Assert.AreNotEqual(0, check.ExitCode, "e2fsck muss den Record ueber die Blockgrenze melden");
        StringAssert.Contains(check.Output, "directory corrupted");
    }

    [TestMethod]
    public void WholeSecondBlockAndMore_RemovedByPlug_KeepsE2fsckClean()
    {
        // Lauf 9/10: viele Loeschungen hintereinander, darunter jeder erste Record eines Blocks.
        var (blocks, data) = ReadDirectory();
        var victims = new List<(string Name, uint Inode)>();
        for (int index = 200; index < 520; index++)
        {
            string name = $"n{index:D4}";
            victims.Add((name, InodeOf(data, name)));
        }

        foreach (var (_, inode) in victims) KeepInode(inode);
        foreach (var (name, _) in victims)
            Assert.IsTrue(Ext2DirectoryRemoval.Remove(data, BlockSize, Encoding.UTF8.GetBytes(name)) >= 0, name);
        WriteDirectory(blocks, data);

        var check = Check();
        Assert.AreEqual(0, check.ExitCode, check.Output);
        var live = List();
        Assert.AreEqual(Entries - victims.Count, live.Count(entry => entry.StartsWith('n')));
        Assert.IsFalse(live.Contains("n0200") || live.Contains("n0519"));
        Assert.IsTrue(live.Contains("n0199") && live.Contains("n0520"));
    }

    private (List<long> Blocks, byte[] Data) ReadDirectory()
    {
        var blocks = Tool("debugfs", $"-R \"blocks /d\" \"{image}\"").Output
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList();
        byte[] data = new byte[blocks.Count * BlockSize];
        using var stream = new FileStream(image, FileMode.Open, FileAccess.Read);
        for (int index = 0; index < blocks.Count; index++)
        {
            stream.Position = blocks[index] * BlockSize;
            stream.ReadExactly(data, index * BlockSize, BlockSize);
        }

        return (blocks, data);
    }

    private void WriteDirectory(List<long> blocks, byte[] data)
    {
        using var stream = new FileStream(image, FileMode.Open, FileAccess.Write);
        for (int index = 0; index < blocks.Count; index++)
        {
            stream.Position = blocks[index] * BlockSize;
            stream.Write(data, index * BlockSize, BlockSize);
        }
    }

    /// <summary>Zweiter Link unter /k: nach dem Entfernen aus /d stimmt links_count 1 wieder, die Inode ist nicht verwaist.</summary>
    private void KeepInode(uint inode)
    {
        var result = Run("debugfs", $"-w -R \"ln <{inode}> /k/i{inode}\" \"{image}\"");
        StringAssert.Contains(Tool("debugfs", $"-R \"ls /k\" \"{image}\"").Output, $"i{inode}", result.Error);
    }

    private static (string Name, uint Inode) RecordAt(byte[] data, int offset) =>
        (Encoding.ASCII.GetString(data, offset + 8, data[offset + 6]), BitConverter.ToUInt32(data, offset));

    private static int LastRecord(byte[] data, int block)
    {
        int pos = block;
        while (pos + BitConverter.ToUInt16(data, pos + 4) < block + BlockSize) pos += BitConverter.ToUInt16(data, pos + 4);
        return pos;
    }

    private static uint InodeOf(byte[] data, string name)
    {
        for (int block = 0; block < data.Length; block += BlockSize)
            for (int pos = block; pos < block + BlockSize; pos += BitConverter.ToUInt16(data, pos + 4))
            {
                var (found, inode) = RecordAt(data, pos);
                if (inode != 0 && found == name) return inode;
            }

        throw new AssertFailedException($"{name} fehlt im Verzeichnis");
    }

    /// <summary>Lebende Namen aus "debugfs ls" (Format "inode (rec_len) name"); freigegebene Records mit Inode 0 zaehlen nicht.</summary>
    private List<string> List() =>
        Regex.Matches(Tool("debugfs", $"-R \"ls /d\" \"{image}\"").Output, @"(\d+)\s+\(\d+\)\s+(\S+)")
            .Where(match => match.Groups[1].Value != "0" && match.Groups[2].Value.StartsWith('n'))
            .Select(match => match.Groups[2].Value).ToList();

    private (int ExitCode, string Output) Check()
    {
        var result = Run("e2fsck", $"-fn \"{image}\"");
        return (result.ExitCode, result.Output + result.Error);
    }

    private static (int ExitCode, string Output) Tool(string name, string arguments, string? input = null)
    {
        var result = Run(name, arguments, input);
        Assert.AreEqual(0, result.ExitCode, $"{name} {arguments}: {result.Output} {result.Error}");
        return (result.ExitCode, result.Output);
    }

    /// <summary>Ausgabe getrennt: debugfs schreibt sein Versionsbanner auf stderr, geparst wird nur stdout.</summary>
    private static (int ExitCode, string Output, string Error) Run(string name, string arguments, string? input = null)
    {
        string? path = new[] { "/sbin/" + name, "/usr/sbin/" + name, "/usr/bin/" + name }.FirstOrDefault(File.Exists);
        if (path is null) Assert.Inconclusive($"{name} (e2fsprogs) fehlt");
        var start = new ProcessStartInfo(path, arguments)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        try
        {
            using var process = Process.Start(start)!;
            // Erst beide Ausgaben abholen, dann stdin schreiben: debugfs spiegelt jede Befehlszeile, sonst laeuft die Pipe voll.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();
            process.WaitForExit();
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (Win32Exception exception)
        {
            Assert.Inconclusive($"{name} nicht startbar: {exception.Message}");
            throw;
        }
    }
}
