using System.Security.Cryptography;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using ProjektZeh.Storage.Ext2;

internal static class ReadOnlySelfTest
{
    internal static int Run()
    {
        int checks = 0;
        void Expect(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("ok   " + name);
        }

        try
        {
            var device = new MemoryDevice();
            Expect(CosmosExt2Formatter.FormatForKernel(device, null), "4-KiB-Volume formatieren");
            Expect(BitConverter.ToUInt16(device.Bytes.AsSpan(1024 + 58, 2)) == 1, "Formatter setzt s_state=clean");
            var native = new Ext2FileSystemType(device);
            Expect(native.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out var seeded), "native Testdaten einbinden");
            IVfsInode root = seeded!.Root;
            Expect(root.InodeOperations.Mkdir(root, "ordner", VfsMode.OwnerRead | VfsMode.OwnerExecute, out _), "Testordner anlegen");
            Expect(root.InodeOperations.Create(root, "probe.bin", VfsMode.OwnerRead | VfsMode.OwnerWrite, out var seedFile), "Testdatei anlegen");
            byte[] content = new byte[6000];
            for (int i = 0; i < content.Length; i++) content[i] = (byte)(i % 251);
            var writer = new OpenFile(seedFile!);
            Expect(writer.Operations.Write(writer, content) == content.Length, "Testdatei ueber Blockgrenze schreiben");
            writer.Operations.Release(writer);
            Expect(root.InodeOperations.Symlink(root, "verweis", "probe.bin", out _), "Testverweis anlegen");
            seeded.SuperOperations.Drop(seeded);

            byte[] before = SHA256.HashData(device.Bytes);
            int writes = device.Writes;
            int flushes = device.Flushes;
            var readOnly = new CosmosReadOnlyExt2FileSystem(device);
            Expect(!readOnly.TryMount("0", MountFlags.ReadOnly, out _), "Source kann Lesegeraet nicht umgehen");
            Expect(!readOnly.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out _), "Schreibmount wird abgelehnt");
            Expect(!readOnly.TryFormat(ReadOnlySpan<char>.Empty, null), "Formatieren wird abgelehnt");
            Expect(!readOnly.TryDestroy(ReadOnlySpan<char>.Empty), "Zerstoeren wird abgelehnt");
            Expect(readOnly.TryMount(ReadOnlySpan<char>.Empty, MountFlags.ReadOnly, out var mounted), "Lesemount akzeptiert sauberes Volume");
            Expect(mounted!.IsReadOnly && mounted.BlockSize == 4096 && mounted.MaxNameLength == 255, "Superblock meldet schreibgeschuetzt und Geometrie");
            Expect(mounted.SuperOperations.StatFs(mounted, out var statFs) && statFs.BlockSize == 4096, "StatFs delegiert auf nativen Superblock");
            root = mounted.Root;
            Expect(root.InodeOperations.Lookup(root, "probe.bin", out var file), "Lookup liefert lesbaren Datei-Inode");
            Expect(file!.InodeOperations.GetAttr(file, out var stat) && stat.Size == (ulong)content.Length, "Dateiattribute bleiben erhalten");
            var reader = new OpenFile(file);
            byte[] buffer = new byte[content.Length];
            long count = reader.Operations.Read(reader, buffer);
            reader.Position += count;
            Expect(count == content.Length && content.AsSpan().SequenceEqual(buffer), "Dateilesen ueber Blockgrenze liefert Inhalt");
            Expect(reader.Operations.Read(reader, buffer) == 0, "Position am Dateiende liefert EOF");
            Expect(reader.Operations.Seek(reader, 4090, SeekWhence.Set, out long position) && position == 4090 && reader.Position == 4090, "Seek aktualisiert aeussere Position");
            Expect(reader.Operations.Read(reader, buffer.AsSpan(0, 20)) == 20 && content.AsSpan(4090, 20).SequenceEqual(buffer.AsSpan(0, 20)), "Lesen nach Seek ueber Blockgrenze");
            Expect(ThrowsUnauthorized(() => reader.Operations.Write(reader, content)), "Dateischreiben wird sichtbar abgelehnt");
            Expect(reader.Operations.Fsync(reader), "Fsync einer Lesedatei gelingt ohne Schreiben");
            reader.Operations.Release(reader);
            Expect(root.InodeOperations.ReadDir(root, out var entries) && entries.Count == 3, "ReadDir liefert alle Testeintraege");
            foreach (IVfsInode entry in entries)
            {
                Expect(!entry.InodeOperations.SetAttr(entry, (SetAttrFlags)0, in stat), "ReadDir-Inode sperrt SetAttr: " + entry.Name);
            }
            Expect(root.InodeOperations.Lookup(root, "verweis", out var link) && link.InodeOperations.TryReadLink(link, out string? target) && target == "probe.bin", "Symlinkziel bleibt lesbar");
            Expect(root.InodeOperations.Lookup(root, "ordner", out var directory) && directory.InodeOperations.ReadDir(directory, out var empty) && empty.Count == 0, "Unterordner bleibt lesbar");
            Expect(!root.InodeOperations.Create(root, "neu", VfsMode.OwnerWrite, out _), "Create ist gesperrt");
            Expect(!root.InodeOperations.Mkdir(root, "neu", VfsMode.OwnerWrite, out _), "Mkdir ist gesperrt");
            Expect(!root.InodeOperations.Symlink(root, "neu", "probe.bin", out _), "Symlink ist gesperrt");
            Expect(!root.InodeOperations.Unlink(root, "probe.bin"), "Unlink ist gesperrt");
            Expect(!root.InodeOperations.Rmdir(root, "ordner"), "Rmdir ist gesperrt");
            Expect(!root.InodeOperations.Rename(root, "probe.bin", root, "neu"), "Rename ist gesperrt");
            Expect(mounted.SuperOperations.Sync(mounted), "Sync des Lesemounts gelingt");
            mounted.SuperOperations.Drop(mounted);

            var guarded = new CosmosReadOnlyBlockDevice(device);
            byte[] sector = new byte[512];
            guarded.ReadBlock(2, 1, sector);
            Expect(guarded.BlockSize == device.BlockSize && guarded.BlockCount == device.BlockCount && guarded.Name == device.Name, "Lesegeraet erhaelt Eigenschaften");
            Expect(ThrowsUnauthorized(() => guarded.WriteBlock(0, 1, sector)), "Blockschreiben ist gesperrt");
            Expect(ThrowsUnauthorized(guarded.Flush), "Block-Flush ist gesperrt");
            Expect(device.Writes == writes && device.Flushes == flushes, "Lesen, Schliessen, Sync und Drop rufen keine Backend-Schreiboperation auf");
            Expect(before.AsSpan().SequenceEqual(SHA256.HashData(device.Bytes)), "Volume-Hash nach allen Leseoperationen unveraendert");

            foreach (ushort state in new ushort[] { 0, 2, 3, 4, ushort.MaxValue })
            {
                BitConverter.TryWriteBytes(device.Bytes.AsSpan(1024 + 58, 2), state);
                bool accepted = readOnly.TryMount(ReadOnlySpan<char>.Empty, MountFlags.ReadOnly, out var stateMount);
                Expect(accepted == (state is 0 or 2 or 3), "s_state=" + state + " wird passend behandelt");
                if (accepted)
                {
                    Expect(stateMount!.IsReadOnly, "unsauberes Volume bleibt schreibgeschuetzt");
                    stateMount.SuperOperations.Drop(stateMount);
                }
            }
            Expect(device.Writes == writes && device.Flushes == flushes, "Statuspruefungen bleiben ohne Backend-Schreiben");
            Console.WriteLine($"readonly-selftest: {checks} Pruefungen bestanden");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("readonly-selftest: " + exception);
            return 1;
        }
    }

    private static bool ThrowsUnauthorized(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return true; }
        return false;
    }

    private sealed class OpenFile(IVfsInode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.FileOperations!;
        public long Position { get; set; }
    }

    private sealed class MemoryDevice : IBlockDevice
    {
        internal byte[] Bytes { get; } = new byte[8 * 1024 * 1024];
        internal int Writes { get; private set; }
        internal int Flushes { get; private set; }
        public ulong BlockSize => 512;
        public ulong BlockCount => (ulong)Bytes.Length / BlockSize;
        public string Name => "readonly-test";
        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data) => Slice(blockNo, blockCount, data.Length).CopyTo(data);
        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
        {
            data[..checked((int)(blockCount * BlockSize))].CopyTo(Slice(blockNo, blockCount, data.Length));
            Writes++;
        }
        public void Flush() => Flushes++;
        private Span<byte> Slice(ulong blockNo, ulong blockCount, int length)
        {
            if (blockNo > BlockCount || blockCount > BlockCount - blockNo || (ulong)length < blockCount * BlockSize)
                throw new ArgumentOutOfRangeException(nameof(blockNo));
            return Bytes.AsSpan((int)(blockNo * BlockSize), (int)(blockCount * BlockSize));
        }
    }
}
