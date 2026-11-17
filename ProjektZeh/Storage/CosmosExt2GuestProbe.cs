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
/// Plug belegt ist, Dateioperationen, probe.bin bleibt. Lauf 2 (ext2 vorhanden): nur die
/// Lesekomponente CosmosReadOnlyExt2FileSystem unter /mnt/ext2-ro, verweigerte Mutationen, Imagehash vor/nach unveraendert.
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
    // Einhaengepunkt, den Finish nach einem Fehler noch aushaengen muss; null = nichts eingebunden.
    private static string? mountedAt;

    /// <summary>Schreibt alle Schritte seriell und endet immer mit STORAGE_PROBE DONE; true nur ohne FAIL.</summary>
    public static bool Run()
    {
        try
        {
            Begin("select");
            IBlockDevice device = FindProbeDevice();
            byte[] prefix = ReadPrefix(device);
            bool blank = Array.TrueForAll(prefix, value => value == 0);
            bool hasExt2 = BitConverter.ToUInt16(prefix, CosmosExt2SuperblockLayout.SuperblockOffset + CosmosExt2SuperblockLayout.MagicOffset) == CosmosExt2SuperblockLayout.Magic;
            Check(blank || hasExt2, $"{device.Name}: erstes MiB weder leer noch ext2; nichts geschrieben");
            Pass($"{device.Name} Lauf {(blank ? 1 : 2)}");

            if (!blank)
            {
                ReadOnlyRun(device, prefix);
                Write("DONE");
                return true;
            }

            Begin("register");
            Check(VfsManager.RegisterFileSystem(FileSystemName, new Ext2FileSystemType(device)), "Registrierung abgelehnt");
            Pass(FileSystemName);

            // Bewusst der allgemeine VFS-Weg: ohne Optionen formatiert der Paketformatter mit 1024 Byte,
            // 4096 Byte und sparse_super belegen also den Plug auf FormatForKernel.
            Begin("format");
            Check(VfsManager.TryFormat(FileSystemName, "", null), "TryFormat abgelehnt");
            prefix = ReadPrefix(device);
            uint logBlockSize = BitConverter.ToUInt32(prefix, CosmosExt2SuperblockLayout.SuperblockOffset + CosmosExt2SuperblockLayout.LogBlockSizeOffset);
            uint roCompat = BitConverter.ToUInt32(prefix, CosmosExt2SuperblockLayout.SuperblockOffset + CosmosExt2SuperblockLayout.FeatureRoCompatOffset);
            Check(logBlockSize == 2 && (roCompat & 1) == 1, $"log_block_size={logBlockSize} ro_compat=0x{roCompat:X}: Plug nicht aktiv");
            Pass("4096 Byte, sparse_super (Plug aktiv)");

            Begin("gate");
            var decision = Ext2FeatureGate.Evaluate(prefix.AsSpan(CosmosExt2SuperblockLayout.SuperblockOffset, CosmosExt2SuperblockLayout.SuperblockSize));
            Check(decision == Ext2MountDecision.Ok, $"Entscheidung {decision}");
            Pass("Ok");

            Begin("mount");
            Check(VfsManager.TryMount(FileSystemName, "", MountFlags.None, MountPoint, out _), "TryMount abgelehnt");
            mountedAt = MountPoint;
            Pass(MountPoint);

            byte[] expected = ExpectedContent();
            FileOperations(device, expected);

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
            mountedAt = null;
            Check(VfsManager.TryUnmount(MountPoint), "TryUnmount abgelehnt");
            device.Flush();
            Pass("Puffer geschrieben");
            Write("DONE");
            return true;
        }
        catch (ProbeFailure)
        {
            return Finish();
        }
        catch (Exception exception)
        {
            // Auch Ausnahmen aus Format oder Geraete-I/O werden dem laufenden Schritt zugeordnet.
            Write($"{step} FAIL {exception.GetType().Name}: {exception.Message}");
            return Finish();
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

    /// <summary>
    /// Lauf 2: nur die Lesekomponente CosmosReadOnlyExt2FileSystem unter /mnt/ext2-ro. Der native Cosmos-Lesemount faellt weg,
    /// weil er bei Sync/Drop schreibt; der SHA-256 ueber die ganze Platte vor und nach dem Lauf belegt, dass nichts geschrieben wurde.
    /// </summary>
    private static void ReadOnlyRun(IBlockDevice device, byte[] prefix)
    {
        Begin("ro-imagehash-before");
        string before = DeviceHash(device);
        Pass(before);

        Begin("ro-gate");
        var decision = Ext2FeatureGate.Evaluate(prefix.AsSpan(CosmosExt2SuperblockLayout.SuperblockOffset, CosmosExt2SuperblockLayout.SuperblockSize));
        Check(decision != Ext2MountDecision.Reject, $"Entscheidung {decision}");
        Pass(decision == Ext2MountDecision.Ok ? "Ok" : "ReadOnly");

        Begin("ro-mount");
        var component = new CosmosReadOnlyExt2FileSystem(device);
        Check(component.TryMount(out VfsMount? mount), "TryMount abgelehnt");
        mountedAt = CosmosReadOnlyExt2FileSystem.MountPoint;
        Check(mount!.IsReadOnly, "Mount nicht als schreibgeschuetzt gefuehrt");
        Pass(mountedAt + " nur lesend");
        // Nur Doku (keine Bewertung): VfsManager.TryMount verlangt kein vorhandenes /mnt; ob der Elternpfad sichtbar ist, steht hier.
        Write($"ro-info /mnt existiert={Directory.Exists("/mnt")}");

        string root = CosmosReadOnlyExt2FileSystem.MountPoint;
        string probe = root + "/probe.bin";
        string expectedHash = Hash(ExpectedContent());

        Begin("ro-read");
        Check(Hash(File.ReadAllBytes(probe)) == expectedHash, "probe.bin Hash weicht ab");
        Check(Hash(File.ReadAllBytes(root + "/reuse.bin")) == Hash(ReuseContent()), "reuse.bin Hash weicht ab");
        Check(Directory.Exists(root + "/neu"), "Verzeichnis neu fehlt");
        Check(Array.Exists(Directory.GetFileSystemEntries(root), entry => entry.EndsWith("/probe.bin", StringComparison.Ordinal)), "Wurzelliste ohne probe.bin");
        Pass("probe.bin, reuse.bin, neu, Wurzelliste");

        Begin("ro-handle");
        Check(VfsManager.TryOpenFile(probe, out IVfsFileHandle? opened), "TryOpenFile abgelehnt");
        IVfsFileHandle handle = opened!;
        // Ausdruecklich schliessen statt using: der Handle wird auch nach einem Check-Fehler geschlossen, und eine
        // Ausnahme beim Schliessen verdeckt die primaere nicht (beide werden gemeldet).
        Exception? failure = null;
        try
        {
            byte[] content = new byte[BaseLength + AppendLength];
            int total = 0;
            long read;
            while (total < content.Length && (read = handle.Read(content.AsSpan(total))) > 0) total += (int)read;
            Check(total == content.Length && Hash(content) == expectedHash, $"Handle las {total} Byte oder falschen Inhalt");
            Check(handle.TryFlush(), "Sync (TryFlush) abgelehnt");
            Check(!handle.TrySetAttr(SetAttrFlags.Size, default), "SetAttr nicht verweigert");
            long written = -1;
            bool threw = Refused(() => written = handle.Write([0x55]));
            Check(threw || written <= 0, $"Write ueber Handle nicht verweigert ({written} Byte)");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            handle.Dispose();
        }
        catch (Exception exception)
        {
            Write($"ro-handle FAIL Dispose {exception.GetType().Name}: {exception.Message}");
            failure ??= new ProbeFailure();
        }

        if (failure != null) throw failure;
        Pass("Lesen, Sync, SetAttr/Write verweigert, Schliessen");

        Begin("ro-write-denied");
        string created = root + "/neu.bin";
        string moved = root + "/verschoben.bin";
        string directory = root + "/ordner2";
        // Ausgangslage nachweisen: Ziel vorhanden, neue Namen fehlen; sonst bewiese das Ausbleiben der Wirkung nichts.
        Check(File.Exists(probe) && !File.Exists(created) && !Directory.Exists(directory) && !File.Exists(moved), "Ausgangslage falsch");
        Refused(() => File.WriteAllBytes(created, [1, 2, 3]));
        Refused(() => File.Delete(probe));
        Refused(() => Directory.CreateDirectory(directory));
        Refused(() => File.Move(probe, moved));
        Check(!File.Exists(created) && !Directory.Exists(directory) && !File.Exists(moved), "Mutation sichtbar");
        Check(File.Exists(probe) && Hash(File.ReadAllBytes(probe)) == expectedHash, "probe.bin veraendert oder weg");
        Pass("Create, Delete, Mkdir, Rename ohne Wirkung");

        Begin("ro-unmount");
        mountedAt = null;
        Check(VfsManager.TryUnmount(root), "TryUnmount abgelehnt");
        Pass(root);

        Begin("ro-imagehash-after");
        string after = DeviceHash(device);
        Check(after == before, $"{after} statt {before}");
        Pass("Image unveraendert");
    }

    // Jede Ausnahme gilt als Verweigerung; ob wirklich nichts geschrieben wurde, pruefen Dateizustand und Imagehash.
    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string DeviceHash(IBlockDevice device)
    {
        var digest = new Sha256Digest();
        byte[] chunk = new byte[CheckedPrefix];
        ulong blocksPerChunk = (ulong)CheckedPrefix / device.BlockSize;
        for (ulong block = 0; block < device.BlockCount; block += blocksPerChunk)
        {
            device.ReadBlock(block, blocksPerChunk, chunk);
            digest.BlockUpdate(chunk, 0, chunk.Length);
        }

        byte[] hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return Convert.ToHexString(hash);
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

    private static bool Finish()
    {
        // Auch nach einem Fehler aushaengen, damit der Treiber seine Puffer schreibt; das Ergebnis bleibt FAIL.
        try
        {
            if (mountedAt != null && !VfsManager.TryUnmount(mountedAt)) Write("unmount FAIL nach Fehler");
            mountedAt = null;
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
