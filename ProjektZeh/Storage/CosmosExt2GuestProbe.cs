#if ZEHOS_STORAGE_PROBE
using System;
using System.IO;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using Cosmos.Kernel.System.Storage;
using Org.BouncyCastle.Crypto.Digests;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

/// <summary>
/// Gastprobe (-p:ZehOSStorageProbe=true) fuer ext2 auf der ganzen Testplatte ohne Partitionstabelle; Gegenstueck
/// ist scripts/test-storage-ext2-guest.py. Lauf 1 (erstes MiB null): Formatieren ueber VfsManager.TryFormat, damit der
/// Plug belegt ist, Dateioperationen, probe.bin bleibt. Lauf 2 (ext2 vorhanden): Featurevertrag,
/// schreibgeschuetzter Mount, Hashes von probe.bin und reuse.bin, Verzeichnis neu.
/// Jedes andere Geraet und jeder andere Inhalt bleibt unberuehrt.
/// </summary>
internal static class CosmosExt2GuestProbe
{
    // Testplatte = AHCI-Port 1 hinter der App-Platte an Port 0; Cosmos zaehlt sata{n} in Portreihenfolge.
    private const string ProbeDeviceName = "sata1";
    // Genau 72 MiB: die leere App-Platte hat 64 MiB und darf nie getroffen werden.
    private const ulong ProbeDiskSize = 72UL * 1024 * 1024;
    private const string FileSystemName = "zehos-ext2-probe";
    private const string MountPoint = "/ext2probe";
    private const int CheckedPrefix = 1024 * 1024;
    private const string ProbeFile = MountPoint + "/probe.bin";
    private const int BaseLength = 64 * 1024;
    private const int AppendLength = 8 * 1024;
    // Mitten im vierten Block; 72 KiB reichen ueber die 12 direkten Bloecke in den Einfach-Indirektblock.
    private const int OverwriteOffset = 3 * 4096 + 100;
    private const int OverwriteLength = 16;
    // Nach Delete/Rmdir neu angelegt: belegt die eben freigegebenen Inodes erneut (FreeInode-Plug laesst den Cache stehen).
    private const string ReuseFile = MountPoint + "/reuse.bin";
    private const string ReuseDirectory = MountPoint + "/neu";
    private const int ReuseLength = 6000;

    private static string step = "select";

    /// <summary>Schreibt alle Schritte seriell und endet immer mit STORAGE_PROBE DONE; true nur ohne FAIL.</summary>
    public static bool Run()
    {
        bool mounted = false;
        try
        {
            Begin("select");
            IBlockDevice device = FindProbeDevice();
            byte[] prefix = ReadPrefix(device);
            bool blank = Array.TrueForAll(prefix, value => value == 0);
            bool hasExt2 = BitConverter.ToUInt16(prefix, CosmosExt2SuperblockLayout.SuperblockOffset + CosmosExt2SuperblockLayout.MagicOffset) == CosmosExt2SuperblockLayout.Magic;
            Check(blank || hasExt2, $"{device.Name}: erstes MiB weder leer noch ext2; nichts geschrieben");
            Pass($"{device.Name} Lauf {(blank ? 1 : 2)}");

            Begin("register");
            Check(VfsManager.RegisterFileSystem(FileSystemName, new Ext2FileSystemType(device)), "Registrierung abgelehnt");
            Pass(FileSystemName);

            if (blank)
            {
                // Bewusst der allgemeine VFS-Weg: ohne Optionen formatiert der Paketformatter mit 1024 Byte,
                // 4096 Byte und sparse_super belegen also den Plug auf FormatForKernel.
                Begin("format");
                Check(VfsManager.TryFormat(FileSystemName, "", null), "TryFormat abgelehnt");
                prefix = ReadPrefix(device);
                uint logBlockSize = BitConverter.ToUInt32(prefix, CosmosExt2SuperblockLayout.SuperblockOffset + CosmosExt2SuperblockLayout.LogBlockSizeOffset);
                uint roCompat = BitConverter.ToUInt32(prefix, CosmosExt2SuperblockLayout.SuperblockOffset + CosmosExt2SuperblockLayout.FeatureRoCompatOffset);
                Check(logBlockSize == 2 && (roCompat & 1) == 1, $"log_block_size={logBlockSize} ro_compat=0x{roCompat:X}: Plug nicht aktiv");
                Pass("4096 Byte, sparse_super (Plug aktiv)");
            }

            Begin("gate");
            var decision = Ext2FeatureGate.Evaluate(prefix.AsSpan(CosmosExt2SuperblockLayout.SuperblockOffset, CosmosExt2SuperblockLayout.SuperblockSize));
            Check(decision == Ext2MountDecision.Ok, $"Entscheidung {decision}");
            Pass("Ok");

            // Lauf 2 prueft nur und mountet deshalb schreibgeschuetzt.
            Begin("mount");
            MountFlags flags = blank ? MountFlags.None : MountFlags.ReadOnly;
            Check(VfsManager.TryMount(FileSystemName, "", flags, MountPoint, out _), "TryMount abgelehnt");
            mounted = true;
            Pass(blank ? MountPoint : MountPoint + " nur lesend");

            byte[] expected = ExpectedContent();
            if (blank) FileOperations(device, expected);

            Begin("hash");
            string expectedHash = Hash(expected);
            string actualHash = Hash(File.ReadAllBytes(ProbeFile));
            Check(actualHash == expectedHash, $"{actualHash} statt {expectedHash}");
            Pass(expectedHash);

            Begin("reuse-check");
            Check(Directory.Exists(ReuseDirectory), "Verzeichnis neu fehlt");
            string expectedReuse = Hash(ReuseContent());
            string actualReuse = Hash(File.ReadAllBytes(ReuseFile));
            Check(actualReuse == expectedReuse, $"reuse.bin {actualReuse} statt {expectedReuse}");
            Pass(expectedReuse);

            Begin("unmount");
            mounted = false;
            Check(VfsManager.TryUnmount(MountPoint), "TryUnmount abgelehnt");
            device.Flush();
            Pass("Puffer geschrieben");
            Write("DONE");
            return true;
        }
        catch (ProbeFailure)
        {
            return Finish(mounted);
        }
        catch (Exception exception)
        {
            // Auch Ausnahmen aus Format oder Geraete-I/O werden dem laufenden Schritt zugeordnet.
            Write($"{step} FAIL {exception.GetType().Name}: {exception.Message}");
            return Finish(mounted);
        }
    }

    private static IBlockDevice FindProbeDevice()
    {
        foreach (IBlockDevice device in StorageManager.Devices)
        {
            if (!string.Equals(device.Name, ProbeDeviceName, StringComparison.Ordinal)) continue;
            Check(device.BlockSize != 0 && device.BlockCount * device.BlockSize == ProbeDiskSize && CheckedPrefix % (int)device.BlockSize == 0,
                $"{device.Name} ist nicht genau 72 MiB ({device.BlockCount} x {device.BlockSize}); nichts geschrieben");
            return device;
        }

        Check(false, $"{ProbeDeviceName} fehlt; nichts geschrieben");
        throw new ProbeFailure();
    }

    private static byte[] ReadPrefix(IBlockDevice device)
    {
        byte[] prefix = new byte[CheckedPrefix];
        device.ReadBlock(0, (ulong)CheckedPrefix / device.BlockSize, prefix);
        return prefix;
    }

    private static void FileOperations(IBlockDevice device, byte[] expected)
    {
        Begin("create-write");
        using (var stream = new FileStream(ProbeFile, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(expected, 0, BaseLength);
        }
        Pass($"{BaseLength} Byte");

        Begin("append");
        using (var stream = new FileStream(ProbeFile, FileMode.Append, FileAccess.Write))
        {
            stream.Write(expected, BaseLength, AppendLength);
        }
        long length = new FileInfo(ProbeFile).Length;
        Check(length == BaseLength + AppendLength, $"Laenge {length}");
        Pass($"{AppendLength} Byte");

        Begin("seek-write");
        using (var stream = new FileStream(ProbeFile, FileMode.Open, FileAccess.ReadWrite))
        {
            Check(stream.Seek(OverwriteOffset, SeekOrigin.Begin) == OverwriteOffset, "Position falsch");
            stream.Write(expected, OverwriteOffset, OverwriteLength);
        }
        Pass($"{OverwriteLength} Byte bei {OverwriteOffset}");

        Begin("read");
        byte[] read = File.ReadAllBytes(ProbeFile);
        Check(read.AsSpan().SequenceEqual(expected), $"Inhalt weicht ab ({read.Length} Byte)");
        Pass($"{read.Length} Byte gleich");

        Begin("rename");
        string before = MountPoint + "/moved.bin";
        string after = MountPoint + "/renamed.bin";
        File.WriteAllBytes(before, [1, 2, 3]);
        File.Move(before, after);
        Check(!File.Exists(before) && File.Exists(after), "Zielzustand falsch");
        Pass("moved.bin -> renamed.bin");

        string directory = MountPoint + "/ordner";
        Begin("mkdir");
        Directory.CreateDirectory(directory);
        Check(Directory.Exists(directory), "Verzeichnis fehlt");
        Pass(directory);

        Begin("rmdir");
        Directory.Delete(directory);
        Check(!Directory.Exists(directory), "Verzeichnis noch vorhanden");
        Pass(directory);

        Begin("delete");
        File.Delete(after);
        Check(!File.Exists(after), "Datei noch vorhanden");
        Pass("renamed.bin");

        Begin("reuse-create");
        File.WriteAllBytes(ReuseFile, ReuseContent());
        Pass($"{ReuseLength} Byte");

        Begin("reuse-mkdir");
        Directory.CreateDirectory(ReuseDirectory);
        Check(Directory.Exists(ReuseDirectory), "Verzeichnis fehlt");
        Pass(ReuseDirectory);

        Begin("sync");
        device.Flush();
        Pass("Geraetepuffer geschrieben");
    }

    // Endinhalt von probe.bin nach Schreiben, Anhaengen und Ueberschreiben; Lauf 2 vergleicht dagegen.
    private static byte[] ExpectedContent()
    {
        byte[] content = new byte[BaseLength + AppendLength];
        for (int i = 0; i < BaseLength; i++) content[i] = (byte)((i * 131) ^ (i >> 9));
        for (int i = BaseLength; i < content.Length; i++) content[i] = (byte)(0xA5 ^ i);
        for (int i = OverwriteOffset; i < OverwriteOffset + OverwriteLength; i++) content[i] = 0xEE;
        return content;
    }

    private static byte[] ReuseContent()
    {
        byte[] content = new byte[ReuseLength];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 7 + 3);
        return content;
    }

    private static string Hash(byte[] data)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(data, 0, data.Length);
        byte[] hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return Convert.ToHexString(hash);
    }

    private static bool Finish(bool mounted)
    {
        // Auch nach einem Fehler aushaengen, damit der Treiber seine Puffer schreibt; das Ergebnis bleibt FAIL.
        try
        {
            if (mounted && !VfsManager.TryUnmount(MountPoint)) Write("unmount FAIL nach Fehler");
        }
        catch (Exception exception)
        {
            Write($"unmount FAIL nach Fehler: {exception.GetType().Name}: {exception.Message}");
        }

        Write("DONE");
        return false;
    }

    private static void Begin(string name) => step = name;

    private static void Check(bool condition, string detail)
    {
        if (condition) return;
        Write($"{step} FAIL {detail}");
        throw new ProbeFailure();
    }

    private static void Pass(string detail) => Write($"{step} PASS {detail}");

    private static void Write(string message) => Log.WriteString($"STORAGE_PROBE {message}\n");

    private sealed class ProbeFailure : Exception
    {
    }
}
#endif
