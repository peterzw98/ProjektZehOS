#if ZEHOS_STORAGE_ROOT_PROBE
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using ProjektZeh.Security;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

/// <summary>
/// E4.0-Gastprobe (-p:ZehOSStorageRootProbe=true): ext2 als schreibbares / unter Dauerlast, Spec
/// docs/superpowers/specs/2026-11-17-storage-e4-0-rw-qualification.md; Gegenstueck scripts/test-storage-ext2-root.py.
/// Geraete: sata0 genau 64 MiB (FAT fuer /boot/efi, formatiert die Probe nur leer), sata1 Root genau 32 oder 128 GiB,
/// sata2 genau 1 GiB (nur Lauf 1, Inode-Erschoepfung), sata3 genau 1 MiB Steuerplatte ("ZEHOS-ROOTPROBE run=N" in
/// Sektor 0, vom Host geschrieben, hier nur gelesen). Jedes andere Geraet und jeder fremde Inhalt bleibt unberuehrt.
/// Ausgabe: STORAGE_ROOT &lt;schritt&gt; PASS|FAIL|INFO ..., am Ende STORAGE_ROOT DONE.
/// </summary>
internal static class CosmosExt2RootProbe
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;
    private const string FatDeviceName = "sata0";
    private const string RootDeviceName = "sata1";
    private const string InodeDeviceName = "sata2";
    private const string ControlDeviceName = "sata3";
    private const string RootFileSystem = "zehos-rootprobe-ext2";
    private const string FatFileSystem = "zehos-rootprobe-fat";
    private const string InodeFileSystem = "zehos-rootprobe-inodes";
    private const string RootLabel = "zehos-rootprobe";
    private const string InodeLabel = "zehos-inodeprobe";
    private const string FatLabel = "ZEHOSPROBE";
    private const string EfiMount = "/boot/efi";
    private const string InodeMount = "/inodeprobe";
    private const string ControlPrefix = "ZEHOS-ROOTPROBE run=";
    private const int CheckedPrefix = 1024 * 1024;
    private const int IoChunk = 1024 * 1024;
    // Einmal angelegt statt je Datei: 1-MiB-Arrays pro Aufruf (10000 Dateien in d) liessen eine Allokation null liefern
    // (Lauf 10: Page Fault in Fill, CR2=0x10). Nur der Hauptablauf nutzt sie; JWorker hat eigene Puffer.
    private static readonly byte[] IoBuffer = new byte[IoChunk];
    private static readonly byte[] ExpectedBuffer = new byte[IoChunk];
    private const int FillFileMiB = 64;
    private const int SmallFill = 4096;
    // Superblock ab Byte 1024 = Sektor 2 und 3 bei 512-Byte-Sektoren.
    private const ulong SuperblockLba = 2;
    private const int ConcurrencySeconds = 60;

    private static string step = "select";
    private static readonly List<string> mounted = [];
    private static readonly byte[] zeros = new byte[IoChunk];

    /// <summary>Schreibt alle Schritte seriell und endet immer mit STORAGE_ROOT DONE; true nur ohne FAIL.</summary>
    public static bool Run()
    {
        try
        {
            Begin("select");
            int run = ReadControl();
            // Laeufe 1-5 auf 2 GiB, Hochgruppenlauf 6-8 auf 32/128 GiB (Leitungsentscheid 19:02, Vollfuellung zu langsam).
            IBlockDevice root = run is <= 5 or >= 9
                ? FindDevice(RootDeviceName, size => size == 2 * GiB, "2 GiB")
                : FindDevice(RootDeviceName, size => size == 32 * GiB || size == 128 * GiB, "32 oder 128 GiB");
            Pass($"Lauf {run}, {RootDeviceName} {root.BlockCount * root.BlockSize / GiB} GiB");
            switch (run)
            {
                case 1: RunFresh(root); break;
                case 2: RunSecond(root); break;
                case 3: RunHardAbort(root); break;
                case 4: RunAfterAbort(root); break;
                case 5: RunAfterRepair(root); break;
                case 6: RunFormatOnly(root); break;
                case 9: RunLimitOnly(root, renameWhenFull: false); break;
                case 10: RunDeleteSpan(root); break;
                case 11: RunScale(root); break;
                case 12: RunHeapFragmentation(root); break;
                case 13: RunConcurrency(root, lockedOnly: false); break;
                case 14: RunConcurrency(root, lockedOnly: true); break;
                case 15: RunConcurrency(root, lockedOnly: true, onFat: true); break;
                default: RunHighGroups(root, fullBig: run == 7); break;
            }

            Write("DONE");
            return true;
        }
        catch (ProbeFailure)
        {
            return Finish();
        }
        catch (Exception exception)
        {
            // Jeder Ausnahmetyp ausserhalb der erwarteten Fehlerwege ist FAIL und wird dem laufenden Schritt zugeordnet.
            Write($"{step} FAIL {exception.GetType().Name}: {exception.Message}");
            return Finish();
        }
    }

    // ---------------------------------------------------------------- Laeufe

    private static void RunFresh(IBlockDevice root)
    {
        Begin("a");
        Check(IsBlank(root), $"{RootDeviceName}: erstes MiB nicht leer; Lauf 1 nur auf leerer Platte, nichts geschrieben");
        IBlockDevice fat = FindDevice(FatDeviceName, size => size == 64 * MiB, "64 MiB");
        // 64 MiB = eine Gruppe mit 4096 Inodes: Erschoepfung in einem Verzeichnis unter der Cosmos-Grenze von 8190 Eintraegen.
        IBlockDevice inodes = FindDevice(InodeDeviceName, size => size == 64 * MiB, "64 MiB");
        Check(IsBlank(fat), $"{FatDeviceName}: nicht leer; nichts geschrieben");
        Check(IsBlank(inodes), $"{InodeDeviceName}: nicht leer; nichts geschrieben");
        Check(CosmosExt2Formatter.FormatForKernel(root, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = RootLabel }), "FormatForKernel abgelehnt");
        Check(GateOf(root) == Ext2MountDecision.Ok, $"Gate {GateOf(root)} nach Format");
        MountRoot(root);
        Check(VfsManager.RegisterFileSystem(FatFileSystem, new FatFileSystemType(fat)), "FAT-Registrierung abgelehnt");
        Check(VfsManager.TryFormat(FatFileSystem, "", new FatFormatOptions { Type = FatType.Fat32, VolumeLabel = FatLabel }), "FAT-Format abgelehnt");
        MountEfi();
        File.WriteAllBytes(EfiMount + "/probe.txt", Encoding.ASCII.GetBytes("efi"));
        Check(File.Exists(EfiMount + "/probe.txt") && Directory.Exists(EfiMount), "/boot/efi ueber / nicht erreichbar");
        var files = Files();
        Check(!files.WriteBytes(EfiMount + "/fremd.bin", [1]).IsOK(), "FileService schrieb in den fremden Mount /boot/efi");
        Pass("Format 4 KiB, Gate Ok, / und /boot/efi eingehaengt, FileService lehnt /boot/efi ab");

        Begin("b");
        ushort state = ReadState(root);
        Check(state == 1, $"s_state {state} nach Mount statt 1");
        WriteState(root, 0);
        Pass("s_state 1 -> 0, Device.Flush()");

        StepC(root, files);
        StepD(root);
        StepE(root);
        // e-limit (8190 Eintraege) laeuft nur noch im Diagnoselauf 9: in Lauf 1 truege er 8190 Inodes zum Cosmos-Inode-Cache bei.
        StepF(root);
        StepG(root, files, 1, RootProbeCatalog.GCount);
        StepH(root, files);
        StepI(root, files);
        StepJ(root);
        StepL(root, files, null);
        StepI2(inodes);
        Verify(root, 1, "verify1");
        StepM(root);
    }

    private static void RunSecond(IBlockDevice root)
    {
        OpenExisting(root, mountEfi: true);
        Verify(root, 1, "verify1");
        StepG(root, Files(), 2, RootProbeCatalog.GRun2);
        StepHRun2(root);
        Verify(root, 2, "verify2");
        StepM(root);
    }

    private static void RunHardAbort(IBlockDevice root)
    {
        OpenExisting(root, mountEfi: false);
        Write("FLUSHED-STATE");
        var files = Files();
        Directory.CreateDirectory("/qual3");
        Write("WRITING");
        // Laeuft, bis der Host QEMU beendet; kein DONE im Normalfall.
        for (int index = 0; ; index++)
        {
            Begin("qual3");
            string path = $"/qual3/w{index:D6}";
            File.WriteAllBytes(path, RootProbeContent.Create("q", path, 8192));
            if (index % 3 == 0) File.Move(path, $"/qual3/r{index:D6}");
            if (index % 5 == 0) Check(files.WriteBytes($"/qual3/p{index % 50:D2}", RootProbeContent.Create("q", path, 4096)).IsOK(), "Ersetzen abgelehnt");
        }
    }

    private static void RunAfterAbort(IBlockDevice root)
    {
        Begin("gate4");
        var decision = GateOf(root);
        ushort state = ReadState(root);
        Check(decision == Ext2MountDecision.ReadOnly && state == 0, $"Gate {decision}, s_state {state} statt ReadOnly/0");
        Pass("ReadOnly wegen s_state 0");

        Begin("ro-mount");
        var component = new CosmosReadOnlyExt2FileSystem(root);
        Check(component.TryMount(out VfsMount? mount), "TryMount abgelehnt");
        Check(mount!.IsReadOnly, "Mount nicht schreibgeschuetzt");
        string prefix = CosmosReadOnlyExt2FileSystem.MountPoint;
        mounted.Add(prefix);
        Pass(prefix);

        Begin("ro-read");
        int good = 0, bad = 0;
        foreach (var entry in RootProbeCatalog.Expected(2))
        {
            try
            {
                if (Matches(prefix + entry.Path, entry)) good++;
                else bad++;
            }
            catch (IOException)
            {
                bad++;
            }
        }

        // Lauf 4 bewertet die Dateien nicht (Spec Abschnitt 3); Kriterium ist der unveraenderte Imagehash am Host.
        Write($"ro-read INFO gleich={good} abweichend={bad}");

        Begin("ro-unmount");
        mounted.Remove(prefix);
        Check(VfsManager.TryUnmount(prefix), "TryUnmount abgelehnt");
        Pass(prefix);
    }

    private static void RunAfterRepair(IBlockDevice root)
    {
        OpenExisting(root, mountEfi: true);
        Begin("qual3-info");
        int remaining = Directory.Exists("/qual3") ? Directory.GetFileSystemEntries("/qual3").Length : -1;
        Write($"qual3-info INFO eintraege={remaining}");
        Verify(root, 2, "verify5");

        Begin("write5");
        Directory.CreateDirectory("/run5");
        for (int index = 0; index < 100; index++)
        {
            string path = $"/run5/f{index:D3}";
            File.WriteAllBytes(path, RootProbeContent.Create("r5", path, 3000));
            Check(Matches(path, new RootProbeEntry(path, path, "r5", 3000)), path + " weicht ab");
        }

        Pass("100 Dateien");
        AfterPhase(root);
        StepM(root);
    }

    /// <summary>Diagnoselauf 9 (2 GiB, leer): nur Format, Mount und Schritt e-limit, ohne die langen Schritte davor.</summary>
    private static void RunLimitOnly(IBlockDevice root, bool renameWhenFull)
    {
        Begin("format9");
        Check(IsBlank(root), $"{RootDeviceName}: erstes MiB nicht leer; nichts geschrieben");
        Check(CosmosExt2Formatter.FormatForKernel(root, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = RootLabel }), "FormatForKernel abgelehnt");
        MountRoot(root);
        WriteState(root, 0);
        Pass("Format, Mount /, s_state 0");
        StepELimit(root, renameWhenFull);
        StepM(root);
    }

    /// <summary>
    /// Diagnoselauf 10: 600 Dateien in einem Verzeichnis (mehr als zwei 4-KiB-Bloecke), die ersten 300 loeschen, kein Aufraeumen.
    /// Der Host prueft mit e2fsck, ob geloeschte Eintraege ueber Blockgrenzen verschmolzen wurden.
    /// </summary>
    private static void RunDeleteSpan(IBlockDevice root)
    {
        Begin("format10");
        Check(IsBlank(root), $"{RootDeviceName}: erstes MiB nicht leer; nichts geschrieben");
        Check(CosmosExt2Formatter.FormatForKernel(root, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = RootLabel }), "FormatForKernel abgelehnt");
        MountRoot(root);
        WriteState(root, 0);
        Pass("Format, Mount /, s_state 0");
        Begin("delete-span");
        Directory.CreateDirectory("/span");
        for (int index = 0; index < 600; index++) File.WriteAllBytes($"/span/n{index:D4}", [1]);
        for (int index = 0; index < 300; index++) File.Delete($"/span/n{index:D4}");
        int visible = Directory.GetFileSystemEntries("/span").Length;
        Write($"delete-span INFO sichtbar nach Loeschen: {visible} (erwartet 300)");
        Check(visible == 300, $"{visible} statt 300 Eintraege sichtbar");
        Pass("300 von 600 geloescht, 300 sichtbar");
        AfterPhase(root);

        // (c) Leitung 22:23: Verschieben in einen anderen Ordner genau am Blockanfang. Bei 16-Byte-Eintraegen fuellen '.',
        // '..' und n0000-n0253 Block 0, n0254 ist der erste Record von Block 1 (wie im Hostbeweis).
        Begin("move-blockstart");
        Directory.CreateDirectory("/quelle");
        Directory.CreateDirectory("/ziel");
        for (int index = 0; index < 600; index++) File.WriteAllBytes($"/quelle/n{index:D4}", [3]);
        foreach (int index in new[] { 254, 255, 509, 510 }) File.Move($"/quelle/n{index:D4}", $"/ziel/m{index:D4}");
        File.Move("/quelle/n0100", "/quelle/umbenannt");
        Check(Directory.GetFileSystemEntries("/quelle").Length == 596 && Directory.GetFileSystemEntries("/ziel").Length == 4,
            $"quelle {Directory.GetFileSystemEntries("/quelle").Length}, ziel {Directory.GetFileSystemEntries("/ziel").Length}");
        Check(File.Exists("/ziel/m0254") && !File.Exists("/quelle/n0254") && File.Exists("/quelle/umbenannt"), "Verschieben am Blockanfang falsch");
        Pass("4 Dateien an Blockgrenzen verschoben, 1 im Ordner umbenannt");
        AfterPhase(root);
        StepM(root);
    }

    /// <summary>
    /// Messlauf 11 (Leitung 21:45): Eintragszahl beim ersten Fehler in EINEM Verzeichnis (bis 8190, Fortschritt und
    /// GC-Heap je 512) und ob vier Verzeichnisse mit je 2048 Eintraegen Anlegen, Umbenennen und Loeschen stabil schaffen.
    /// Kein Aufraeumen des grossen Verzeichnisses: Loeschen vieler Eintraege ist selbst ein offener Befund (Lauf 9/10).
    /// </summary>
    /// <summary>
    /// Diagnoselauf 12 (Schritt e, 17.11.): Liefert der Cosmos-GC nach viel Umschlag mit verstreuten langlebigen kleinen
    /// Objekten fuer eine grosse Allokation null? Nachgebildet wird der Inode-Cache (Dictionary, das mit jedem neuen Inode
    /// waechst) neben einem vorab dimensionierten Dictionary gleicher Fuellung. Erwartung aus der Quelle (efcf20cc): Sammeln
    /// erst bei vollem Seitenspeicher, keine Verdichtung, Freilistenbloecke unter 8 KiB, grosse Allokationen brauchen einen
    /// zusammenhaengenden Seitenlauf.
    /// </summary>
    private static void RunHeapFragmentation(IBlockDevice root)
    {
        Begin("format12");
        Check(IsBlank(root), $"{RootDeviceName}: erstes MiB nicht leer; nichts geschrieben");
        Check(CosmosExt2Formatter.FormatForKernel(root, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = RootLabel }), "FormatForKernel abgelehnt");
        MountRoot(root);
        WriteState(root, 0);
        Pass("Format, Mount /, s_state 0");

        Begin("frag-start");
        Write($"frag-start INFO groesste Allokation vorher: {LargestAllocation()} KiB, GC-Heap {GC.GetTotalMemory(false) / 1024} KiB");
        const int Survivors = 40_000;
        // Vorab dimensioniert: das Eintragsfeld (~40000 x 24 Byte) entsteht jetzt, solange der Seitenspeicher noch frei ist.
        var sized = new Dictionary<uint, object>(Survivors);
        var growing = new Dictionary<uint, object>();
        Pass("Dictionaries angelegt");

        Begin("frag-churn");
        string growFailure = "keiner";
        int growCount = 0;
        long garbage = 0;
        byte[]? sink = null;
        for (uint key = 0; key < Survivors; key++)
        {
            // Je Ueberlebendem etwa 64 KiB Muell in 1-KiB-Stuecken: die Ueberlebenden verteilen sich so ueber den ganzen
            // Speicher, wie die Inodes zwischen den Puffern einer langen Dateisitzung.
            for (int index = 0; index < 64; index++) sink = new byte[1024];
            garbage += 64 * 1024;
            var survivor = new byte[96];
            sized.Add(key, survivor);
            if (growFailure != "keiner") continue;
            try
            {
                growing.Add(key, survivor);
                growCount++;
            }
            catch (Exception exception)
            {
                growFailure = $"{exception.GetType().Name}: {exception.Message}";
            }

            if ((key + 1) % 5000 == 0)
                Write($"frag-churn INFO {key + 1} Ueberlebende, Muell {garbage / (1024 * 1024)} MiB, GC-Heap {GC.GetTotalMemory(false) / 1024} KiB");
        }

        GC.KeepAlive(sink);
        Write($"frag-churn INFO wachsendes Dictionary: {growCount} Eintraege, erster Fehler: {growFailure}");
        Write($"frag-churn INFO vorab dimensioniert: {sized.Count} Eintraege");
        Write($"frag-churn INFO groesste Allokation danach: {LargestAllocation()} KiB, GC-Heap {GC.GetTotalMemory(false) / 1024} KiB");
        Pass($"Muell {garbage / (1024 * 1024)} MiB");
        AfterPhase(root);
    }

    /// <summary>Groesste der Stufen 32 KiB bis 16 MiB, die der Heap noch liefert (null zaehlt wie OutOfMemoryException).</summary>
    private static int LargestAllocation()
    {
        int largest = 0;
        for (int kib = 32; kib <= 16 * 1024; kib *= 2)
        {
            byte[]? buffer;
            try
            {
                buffer = new byte[kib * 1024];
            }
            catch (OutOfMemoryException)
            {
                buffer = null;
            }

            if (IsMissing(buffer)) break;
            largest = kib;
        }

        return largest;
    }

    // Nicht inlinen: ILC sieht newarr als nie null an und wuerde einen direkten Vergleich entfernen (vgl. BufferAllocation).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsMissing(object? value) => value is null;

    /// <summary>
    /// Diagnoselauf 13 (Schritt j, 18.11.): Liest Cosmos-ext2 unter Nebenlaeufigkeit falsch? Je Variante 30 s, dreimal:
    /// ein Leser allein (Kontrolle), zwei Leser, ein Leser neben einem Anleger (anderes Verzeichnis), dasselbe mit einer
    /// gemeinsamen Sperre um jede Operation (Abhilfe-Beleg). Gezaehlt werden Lesevorgaenge und Abweichungen.
    /// </summary>
    /// <param name="lockedOnly">Lauf 14: nur gesperrte Varianten, damit e2fsck danach allein die Sperre bewertet (Lauf 13 beschaedigte
    /// ohne Sperre Bitmaps und Inode-Tabelle).</param>
    /// <param name="onFat">Lauf 15: dieselbe Last auf der FAT-Platte (sata0 unter /boot/efi), Auflage Koordinator 00:32.</param>
    private static void RunConcurrency(IBlockDevice root, bool lockedOnly, bool onFat = false)
    {
        Begin("format13");
        Check(IsBlank(root), $"{RootDeviceName}: erstes MiB nicht leer; nichts geschrieben");
        Check(CosmosExt2Formatter.FormatForKernel(root, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = RootLabel }), "FormatForKernel abgelehnt");
        MountRoot(root);
        WriteState(root, 0);
        string bas = "";
        if (onFat)
        {
            IBlockDevice fat = FindDevice(FatDeviceName, size => size == 64 * MiB, "64 MiB");
            Check(IsBlank(fat), $"{FatDeviceName}: nicht leer; nichts geschrieben");
            Check(VfsManager.RegisterFileSystem(FatFileSystem, new FatFileSystemType(fat)), "FAT-Registrierung abgelehnt");
            Check(VfsManager.TryFormat(FatFileSystem, "", new FatFormatOptions { Type = FatType.Fat32, VolumeLabel = FatLabel }), "FAT-Format abgelehnt");
            MountEfi();
            bas = EfiMount;
        }

        Directory.CreateDirectory(bas + "/c");
        for (int index = 0; index < RootProbeCatalog.JReadFiles; index++) WriteGenerated($"{bas}/c/r{index:D2}", $"/c/r{index:D2}", "j", 4096);
        Pass(onFat ? "Format, Mount / und FAT unter /boot/efi, 50 Lesedateien auf FAT" : "Format, Mount /, 50 Lesedateien");

        var variants = lockedOnly
            ? new (string Name, int Readers, bool Creator, bool Locked, bool Renamer)[]
            {
                ("zwei-leser-sperre", 2, false, true, false), ("leser-anleger-sperre", 1, true, true, false),
                ("zwei-leser-anleger-sperre", 2, true, true, false), ("leser-anleger-umbenenner-sperre", 2, true, true, true),
            }
            : new (string Name, int Readers, bool Creator, bool Locked, bool Renamer)[]
            {
                ("ein-leser", 1, false, false, false), ("zwei-leser", 2, false, false, false),
                ("leser-anleger", 1, true, false, false), ("leser-anleger-sperre", 1, true, true, false),
            };
        int unlockedMismatches = 0, lockedMismatches = 0;
        int rounds = lockedOnly ? 5 : 3;
        for (int round = 0; round < rounds; round++)
        {
            foreach (var variant in variants)
            {
                Begin("conc-" + variant.Name);
                var worker = new ConcurrencyWorker(variant.Locked, $"{bas}/n{round}{variant.Name}", bas);
                Directory.CreateDirectory(worker.CreateDirectory);
                var threads = new List<Thread>();
                for (int reader = 0; reader < variant.Readers; reader++) threads.Add(new Thread(worker.Read));
                if (variant.Creator) threads.Add(new Thread(worker.Create));
                if (variant.Renamer) threads.Add(new Thread(worker.Rename));
                foreach (var thread in threads) thread.Start();
                Thread.Sleep(30_000);
                worker.Stop = true;
                foreach (var thread in threads) Check(thread.Join(30000), "Aufgabe endet nicht (Haenger)");
                Check(worker.Unexpected == null, "unerwartete Ausnahme: " + worker.Unexpected);
                if (variant.Locked) lockedMismatches += worker.Mismatches;
                else if (variant.Readers + (variant.Creator ? 1 : 0) > 1) unlockedMismatches += worker.Mismatches;
                Write($"conc-{variant.Name} INFO Runde {round}: gelesen {worker.Reads}, falsch {worker.Mismatches}, angelegt {worker.Created}, umbenannt {worker.Renamed}");
                if (variant.Readers == 1 && !variant.Creator) Check(worker.Mismatches == 0, $"{worker.Mismatches} Fehler schon mit einem Leser");
                // Aufraeumen haelt den Inode-Cache je Mount klein (Grenze ~17 500) und die 64-MiB-FAT frei.
                StorageLock.Run(() => DeleteTree(worker.CreateDirectory));
            }
        }

        Begin("conc");
        Write($"conc INFO ohne Sperre nebenlaeufig falsch: {unlockedMismatches}, mit Sperre falsch: {lockedMismatches}");
        Check(lockedMismatches == 0, $"{lockedMismatches} Fehler trotz Sperre");
        Pass("Kontrolle und Sperre fehlerfrei");
        AfterPhase(root);
    }

    private sealed class ConcurrencyWorker(bool locked, string createDirectory, string readBase)
    {
        public volatile bool Stop;
        public volatile string? Unexpected;
        public int Reads;
        public int Mismatches;
        public int Created;
        public string CreateDirectory { get; } = createDirectory;

        public void Read()
        {
            int local = 0;
            Loop(() =>
            {
                string name = $"/c/r{local++ % RootProbeCatalog.JReadFiles:D2}";
                byte[] data = Guard(() => File.ReadAllBytes(readBase + name));
                if (!data.AsSpan().SequenceEqual(RootProbeContent.Create("j", name, 4096))) Interlocked.Increment(ref Mismatches);
                Interlocked.Increment(ref Reads);
            });
        }

        public void Create() => Loop(() =>
        {
            string path = $"{CreateDirectory}/f{Created:D6}";
            byte[] content = RootProbeContent.Create("j", path, 1000);
            Guard(() => { File.WriteAllBytes(path, content); return content; });
            Created++;
        });

        public int Renamed;

        /// <summary>Benennt die angelegten Dateien um (Verzeichnisaenderung neben Lesern und Anleger).</summary>
        public void Rename() => Loop(() =>
        {
            int target = Renamed;
            if (target >= Created) { Thread.Sleep(1); return; }
            string source = $"{CreateDirectory}/f{target:D6}";
            Guard(() => { File.Move(source, $"{CreateDirectory}/m{target:D6}"); return true; });
            Renamed++;
        });

        // Gesperrt ueber dieselbe StorageLock wie das Produkt, nicht ueber eine eigene Probensperre.
        private T Guard<T>(Func<T> operation) => locked ? StorageLock.Run(operation) : operation();

        private void Loop(Action action)
        {
            while (!Stop && Unexpected == null)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Unexpected = $"{exception.GetType().Name}: {exception.Message}";
                }
            }
        }
    }

    private static void RunScale(IBlockDevice root)
    {
        Begin("format11");
        Check(IsBlank(root), $"{RootDeviceName}: erstes MiB nicht leer; nichts geschrieben");
        Check(CosmosExt2Formatter.FormatForKernel(root, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = RootLabel }), "FormatForKernel abgelehnt");
        MountRoot(root);
        WriteState(root, 0);
        Pass("Format, Mount /, s_state 0");

        Begin("scale-one");
        Directory.CreateDirectory("/one");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int created = 0;
        string failure = "keiner";
        for (; created < RootProbeCatalog.DirectoryLimit; created++)
        {
            try
            {
                File.WriteAllBytes($"/one/n{created:D4}", [1]);
            }
            catch (Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
                break;
            }

            if ((created + 1) % 512 == 0)
                Write($"scale-one INFO {created + 1} Eintraege, {clock.ElapsedMilliseconds} ms, GC-Heap {GC.GetTotalMemory(false) / 1024} KiB");
        }

        Write($"scale-one INFO Ende bei {created} Eintraegen, erster Fehler: {failure}, {clock.ElapsedMilliseconds} ms");
        Pass($"{created} Eintraege in einem Verzeichnis");
        AfterPhase(root);

        Begin("scale-multi");
        for (int folder = 0; folder < 4; folder++)
        {
            string directory = $"/multi{folder}";
            Directory.CreateDirectory(directory);
            for (int index = 0; index < 2048; index++) File.WriteAllBytes($"{directory}/n{index:D4}", [2]);
            for (int index = 0; index < 100; index++) File.Move($"{directory}/n{index:D4}", $"{directory}/r{index:D4}");
            for (int index = 100; index < 200; index++) File.Delete($"{directory}/n{index:D4}");
            int visible = Directory.GetFileSystemEntries(directory).Length;
            Check(visible == 1948, $"{directory}: {visible} statt 1948 Eintraege");
            Check(File.Exists($"{directory}/r0000") && !File.Exists($"{directory}/n0150"), $"{directory}: Umbenennen/Loeschen falsch");
            Write($"scale-multi INFO {directory} fertig, GC-Heap {GC.GetTotalMemory(false) / 1024} KiB");
        }

        Pass("4 x 2048 angelegt, je 100 umbenannt und 100 geloescht, Auflistung stimmt");
        AfterPhase(root);
        StepM(root);
    }

    /// <summary>Hochgruppenlauf, Teil 1: nur formatieren; danach belegt der Host die unteren Gruppen per debugfs fallocate.</summary>
    private static void RunFormatOnly(IBlockDevice root)
    {
        Begin("format6");
        Check(IsBlank(root), $"{RootDeviceName}: erstes MiB nicht leer; nichts geschrieben");
        Check(CosmosExt2Formatter.FormatForKernel(root, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = RootLabel }), "FormatForKernel abgelehnt");
        Check(GateOf(root) == Ext2MountDecision.Ok && ReadState(root) == 1, $"Gate {GateOf(root)}, s_state {ReadState(root)} nach Format");
        root.Flush();
        Pass("Format 4 KiB, Gate Ok, s_state 1");
    }

    /// <summary>
    /// Hochgruppenlauf, Teil 2: die unteren Gruppen hat der Host belegt (/fill); k schreibt 2 GiB - 1 in die oberen Gruppen
    /// (oder nur die Grenze per Seek, Lauf 8), danach l1/l2 bis ENOSPC und sauberes Herunterfahren.
    /// </summary>
    private static void RunHighGroups(IBlockDevice root, bool fullBig)
    {
        OpenExisting(root, mountEfi: false);
        // Der Host belegt in 1-GiB-Dateien: eine einzelne Datei ueber 2 GiB braeuchte large_file, das Gate lehnte es ab.
        Check(Directory.Exists("/fill"), "/fill der Host-Vorbelegung fehlt");
        long filled = 0;
        foreach (string entry in Directory.GetFiles("/fill")) filled += new FileInfo(entry).Length;
        Write($"open INFO /fill {filled} Byte vorbelegt");
        if (fullBig) StepK(root);
        else StepKBoundary(root);
        var files = Files();
        Directory.CreateDirectory("/l2ziel");
        var target = new RootProbeEntry("/l2ziel/datei", "/l2ziel/datei", "d", 65536);
        WriteGenerated(target.Path, target.Source, target.Seed, target.Length);
        StepL(root, files, target);
        StepM(root);
    }

    /// <summary>Gate Ok, Volumename, Mount /, s_state 0 mit Flush; optional /boot/efi.</summary>
    private static void OpenExisting(IBlockDevice root, bool mountEfi)
    {
        Begin("open");
        byte[] superblock = ReadSuperblock(root);
        string label = Encoding.ASCII.GetString(superblock, CosmosExt2SuperblockLayout.VolumeNameOffset, 16).TrimEnd('\0');
        Check(string.Equals(label, RootLabel, StringComparison.Ordinal), $"Volumename '{label}' statt {RootLabel}; nichts geschrieben");
        var decision = GateOf(root);
        Check(decision == Ext2MountDecision.Ok, $"Gate {decision} statt Ok");
        MountRoot(root);
        ushort state = ReadState(root);
        Check(state == 1, $"s_state {state} nach Mount statt 1");
        WriteState(root, 0);
        if (mountEfi)
        {
            IBlockDevice fat = FindDevice(FatDeviceName, size => size == 64 * MiB, "64 MiB");
            Check(VfsManager.RegisterFileSystem(FatFileSystem, new FatFileSystemType(fat)), "FAT-Registrierung abgelehnt");
            MountEfi();
            Check(File.Exists(EfiMount + "/probe.txt"), "/boot/efi/probe.txt fehlt");
        }

        Pass("Gate Ok, / eingehaengt, s_state 0 + Flush" + (mountEfi ? ", /boot/efi" : ""));
    }

    // ---------------------------------------------------------------- Schritte (Lauf 1)

    private static void StepC(IBlockDevice root, FileService files)
    {
        Begin("c");
        Check(files.CreateDirectory("/etc").IsOK() && files.CreateDirectory("/etc/zehos").IsOK(), "/etc/zehos");
        string directory = "/home";
        Check(files.CreateDirectory(directory).IsOK(), "CreateDirectory /home");
        foreach (string part in new[] { "test", ".config", "zehos", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8" })
        {
            directory += "/" + part;
            Check(files.CreateDirectory(directory).IsOK(), "CreateDirectory " + directory);
        }

        foreach (var entry in new[] { new RootProbeEntry("/home/test/notes.bin", "/home/test/notes.bin", "c", 3000), new RootProbeEntry(directory + "/leaf.bin", directory + "/leaf.bin", "c", 777) })
        {
            Check(files.WriteBytes(entry.Path, RootProbeContent.Create(entry.Seed, entry.Path, (int)entry.Length)).IsOK(), "WriteBytes " + entry.Path);
            var read = files.ReadBytes(entry.Path, (int)entry.Length);
            Check(read.IsOK() && read.Value!.AsSpan().SequenceEqual(RootProbeContent.Create(entry.Seed, entry.Path, (int)entry.Length)), "ReadBytes " + entry.Path);
        }

        Check(files.WriteBytes("/home/test/tmp.bin", [1, 2, 3]).IsOK(), "Schreiben tmp.bin");
        Check(files.MoveFile("/home/test/tmp.bin", "/home/test/tmp2.bin").IsOK(), "Umbenennen tmp.bin");
        Check(files.DeleteFile("/home/test/tmp2.bin").IsOK() && !File.Exists("/home/test/tmp2.bin"), "Loeschen tmp2.bin");
        Pass("FileService: Baum Tiefe 8, Schreiben, Lesen, Umbenennen, Loeschen");
        AfterPhase(root);
    }

    private static void StepD(IBlockDevice root)
    {
        Begin("d");
        for (int dir = 0; dir < RootProbeCatalog.DDirectories; dir++) Directory.CreateDirectory($"/d/d{dir:D2}");
        for (int index = 0; index < RootProbeCatalog.DFiles; index++)
        {
            string path = RootProbeCatalog.DPath(index);
            WriteGenerated(path, path, "d", RootProbeCatalog.DLength(index));
        }

        int checkedFiles = 0;
        for (int index = 0; index < RootProbeCatalog.DFiles; index++)
        {
            string path = RootProbeCatalog.DPath(index);
            Check(Matches(path, new RootProbeEntry(path, path, "d", RootProbeCatalog.DLength(index))), path + " weicht ab");
            checkedFiles++;
        }

        Pass($"System.IO: {checkedFiles} Dateien gelesen = Erwartung");
        AfterPhase(root);
    }

    private static void StepE(IBlockDevice root)
    {
        Begin("e");
        // Phasenmarken nur als INFO: der Schrittname bleibt "e" fuer die Auswertung, ein FAIL ist so trotzdem verortbar
        // (Lauf 1 am 17.11.: ArgumentNullException 'destinationArray' ohne Ort, mit 512M und 1G gleich).
        Directory.CreateDirectory("/e");
        Write("e INFO Phase anlegen");
        for (int index = 0; index < RootProbeCatalog.EEntries; index++)
            WriteGenerated(RootProbeCatalog.EName(index), RootProbeCatalog.EName(index), "e", RootProbeCatalog.ELength(index));
        Write("e INFO Phase umbenennen");
        for (int index = 1; index < RootProbeCatalog.EEntries; index += 10) File.Move(RootProbeCatalog.EName(index), $"/e/r{index:D5}");
        Write("e INFO Phase loeschen");
        for (int index = 2; index < RootProbeCatalog.EEntries; index += 10) File.Delete(RootProbeCatalog.EName(index));
        Write("e INFO Phase ersetzen");
        for (int index = 0; index < RootProbeCatalog.EChanged; index++) WriteGenerated($"/e/x{index:D5}", $"/e/x{index:D5}", "e", RootProbeCatalog.ELength(index));

        Write("e INFO Phase auflisten");
        var listed = Directory.GetFileSystemEntries("/e");
        var unique = new HashSet<string>(listed, StringComparer.Ordinal);
        Check(unique.Count == listed.Length, $"{listed.Length - unique.Count} Doppelnamen");
        Write($"e INFO Phase pruefen ({listed.Length} Eintraege)");
        int expected = 0;
        foreach (var entry in RootProbeCatalog.Expected(1))
        {
            if (!entry.Path.StartsWith("/e/", StringComparison.Ordinal)) continue;
            expected++;
            Check(unique.Contains(entry.Path), entry.Path + " fehlt in der Auflistung");
            Check(Matches(entry.Path, entry), entry.Path + " weicht ab");
        }

        Check(listed.Length == expected, $"{listed.Length} Eintraege statt {expected}");
        Pass($"{expected} Eintraege vollstaendig, ohne Doppelnamen, Inhalte gleich");
        AfterPhase(root);
    }

    /// <summary>
    /// Grenzfall zu e: Cosmos 3.0.97 fasst je Verzeichnis 8190 Eintraege neben '.' und '..'; der naechste muss mit IOException
    /// scheitern, ohne das Verzeichnis zu beschaedigen. Danach Umbenennen und Loeschen darin und ein Ersatzeintrag; am Ende geraeumt.
    /// </summary>
    /// <param name="renameWhenFull">
    /// Nur Diagnose: Umbenennen im vollen Verzeichnis. Danach sieht Cosmos nur noch einen Teil der Eintraege, und das
    /// Aufraeumen loescht das Verzeichnis samt unsichtbarer Eintraege (Lauf 9 am 17.11.: 4097 verwaiste Inodes). Ueber
    /// ZehOS ist der Fall wegen der Produktgrenze 8189 nicht erreichbar; die Laeufe 1-5 pruefen ihn deshalb nicht.
    /// </param>
    private static void StepELimit(IBlockDevice root, bool renameWhenFull = false)
    {
        // Teilschritte einzeln benannt: ein FAIL nennt so die Operation (Lauf 32 GiB: ArgumentNullException ohne Ort).
        Begin("e-limit-create");
        const string directory = "/elimit";
        Directory.CreateDirectory(directory);
        for (int index = 0; index < RootProbeCatalog.DirectoryLimit; index++) File.WriteAllBytes($"{directory}/n{index:D4}", [unchecked((byte)index)]);
        Begin("e-limit-over");
        bool refused = Refused(() => File.WriteAllBytes($"{directory}/over", [1]));
        Check(refused, $"Eintrag {RootProbeCatalog.DirectoryLimit + 1} ohne IOException angelegt");
        Begin("e-limit-list");
        Check(!File.Exists($"{directory}/over"), "abgelehnter Eintrag sichtbar");
        var listed = Directory.GetFileSystemEntries(directory);
        Check(listed.Length == RootProbeCatalog.DirectoryLimit, $"{listed.Length} statt {RootProbeCatalog.DirectoryLimit} Eintraege nach der Ablehnung");
        // Umbenennen im vollen Verzeichnis braucht kurz einen Record mehr. Cosmos meldet Erfolg, sieht danach in derselben
        // Sitzung aber keinen der beiden Namen (am Image korrekt, e2fsck sauber). Nur protokolliert (Leitung 19:26); die
        // Produktgrenze liegt deshalb einen Eintrag tiefer (8189).
        if (renameWhenFull)
        {
            Begin("e-limit-rename-full");
            string fullRename = "ohne Ausnahme";
            try { File.Move($"{directory}/n0002", $"{directory}/v0002"); }
            catch (Exception exception) { fullRename = exception.GetType().Name + ": " + exception.Message; }
            bool oldName = File.Exists($"{directory}/n0002"), newName = File.Exists($"{directory}/v0002");
            Write($"e-limit INFO Umbenennen im vollen Verzeichnis: {fullRename}; alt={oldName} neu={newName}");
        }

        Begin("e-limit-delete");
        File.Delete($"{directory}/n0001");
        Check(!File.Exists($"{directory}/n0001"), "n0001 nach Loeschen noch da");
        Begin("e-limit-rename");
        File.Move($"{directory}/n0000", $"{directory}/m0000");
        Check(File.Exists($"{directory}/m0000") && !File.Exists($"{directory}/n0000"), $"Umbenennen nach Loeschen: m0000={File.Exists($"{directory}/m0000")} n0000={File.Exists($"{directory}/n0000")}");
        Begin("e-limit-replace");
        File.WriteAllBytes($"{directory}/ersatz", [2]);
        Check(File.Exists($"{directory}/ersatz"), "Ersatzeintrag fehlt");
        Begin("e-limit");
        Check(File.ReadAllBytes($"{directory}/n{RootProbeCatalog.DirectoryLimit - 1:D4}")[0] == unchecked((byte)(RootProbeCatalog.DirectoryLimit - 1)), "letzter Eintrag unlesbar");
        Begin("e-limit-cleanup");
        // Der abgelehnte 8191. Eintrag liegt trotz IOException auf der Platte, aber hinter dem Lesefenster von 8192 Records;
        // er wird erst sichtbar, wenn Platz frei ist. Deshalb in Runden leeren und jeden Nachzuegler protokollieren.
        string[] rest = Directory.GetFileSystemEntries(directory);
        for (int round = 0; round < 3 && rest.Length > 0; round++)
        {
            if (round > 0) Write($"e-limit INFO nachtraeglich sichtbar: {string.Join(",", rest, 0, Math.Min(5, rest.Length))}");
            foreach (string entry in rest) File.Delete(entry);
            rest = Directory.GetFileSystemEntries(directory);
        }

        Write($"e-limit INFO nach dem Leeren sichtbar: {rest.Length}");
        string rmdir = "ok";
        try { Directory.Delete(directory); }
        catch (Exception exception) { rmdir = exception.GetType().Name + ": " + exception.Message; }
        Write($"e-limit INFO rmdir {directory}: {rmdir}");
        Check(!Directory.Exists(directory), $"/elimit nicht geraeumt (sichtbar {rest.Length}, rmdir {rmdir})");
        Pass($"{RootProbeCatalog.DirectoryLimit} Eintraege gehen, der naechste ergibt IOException, Verzeichnis bleibt bedienbar");
        AfterPhase(root);
    }

    private static void StepF(IBlockDevice root)
    {
        Begin("f");
        foreach (string directory in new[] { "/f/a", "/f/x", "/f/y", "/f/dir", "/f/o" }) Directory.CreateDirectory(directory);
        for (int index = 0; index < RootProbeCatalog.FCount; index++)
        {
            WriteGenerated($"/f/a/a{index:D4}", $"/f/a/a{index:D4}", "f", 200 + index);
            WriteGenerated($"/f/x/m{index:D4}", $"/f/x/m{index:D4}", "f", 300 + index);
            WriteGenerated($"/f/dir/e{index:D4}", $"/f/dir/e{index:D4}", "f", 100 + index % 50);
        }

        for (int index = 0; index < RootProbeCatalog.FOverwrite; index++)
        {
            WriteGenerated($"/f/o/src{index:D3}", $"/f/o/src{index:D3}", "f", 400 + index);
            WriteGenerated($"/f/o/dst{index:D3}", $"/f/o/dst{index:D3}", "f-alt", 50);
        }

        for (int index = 0; index < RootProbeCatalog.FCount; index++)
        {
            File.Move($"/f/a/a{index:D4}", $"/f/a/b{index:D4}");
            File.Move($"/f/x/m{index:D4}", $"/f/y/m{index:D4}");
        }

        Directory.Move("/f/dir", "/f/dirmoved");
        for (int index = 0; index < RootProbeCatalog.FOverwrite; index++) File.Move($"/f/o/src{index:D3}", $"/f/o/dst{index:D3}", true);

        Check(!Directory.Exists("/f/dir") && Directory.GetFileSystemEntries("/f/x").Length == 0, "alte Namen noch vorhanden");
        int checkedFiles = 0;
        foreach (var entry in RootProbeCatalog.Expected(1))
        {
            if (!entry.Path.StartsWith("/f/", StringComparison.Ordinal)) continue;
            Check(!File.Exists(entry.Source) || entry.Source == entry.Path, entry.Source + " noch vorhanden");
            Check(Matches(entry.Path, entry), entry.Path + " weicht ab");
            checkedFiles++;
        }

        Pass($"{checkedFiles} Ziele mit Quellinhalt (im Verzeichnis, verzeichnisuebergreifend, Verzeichnis, auf vorhandene Namen)");
        AfterPhase(root);
    }

    /// <summary>Ersetzen wie das Produkt (FileService.WriteBytes); Lauf 1 legt vorher an, Lauf 2 ersetzt die ersten 500 erneut.</summary>
    private static void StepG(IBlockDevice root, FileService files, int run, int count)
    {
        Begin(run == 1 ? "g" : "g-run2");
        if (run == 1)
        {
            Directory.CreateDirectory("/g");
            for (int index = 0; index < count; index++)
                WriteGenerated($"/g/r{index:D4}", $"/g/r{index:D4}", "g0", RootProbeCatalog.GInitialLength(index));
        }

        for (int index = 0; index < count; index++)
        {
            string path = $"/g/r{index:D4}";
            long length = run == 1 ? RootProbeCatalog.GReplacedLength(index) : RootProbeCatalog.GRun2Length(index);
            var written = files.WriteBytes(path, RootProbeContent.Create(run == 1 ? "g1" : "g2", path, (int)length), RootProbeCatalog.GKeepsBackup(index));
            Check(written.IsOK(), $"WriteBytes {path}: {written.Message}");
        }

        foreach (string entry in Directory.GetFileSystemEntries("/g"))
            Check(!Path.GetFileName(entry).StartsWith(".zehos-", StringComparison.Ordinal), "Rest " + entry);
        int stage = run;
        int checkedFiles = 0;
        foreach (var entry in RootProbeCatalog.Expected(stage))
        {
            if (!entry.Path.StartsWith("/g/", StringComparison.Ordinal)) continue;
            Check(Matches(entry.Path, entry), entry.Path + " weicht ab");
            checkedFiles++;
        }

        Check(Directory.GetFileSystemEntries("/g").Length == checkedFiles, $"/g hat {Directory.GetFileSystemEntries("/g").Length} statt {checkedFiles} Eintraege");
        Pass($"{count} Ersetzungen (wachsend, schrumpfend, 0), {checkedFiles} Dateien gleich, keine .zehos-Reste");
        AfterPhase(root);
    }

    private static void StepH(IBlockDevice root, FileService files)
    {
        Begin("h");
        Directory.CreateDirectory("/h");
        for (int index = 0; index < RootProbeCatalog.HCount; index++) WriteGenerated($"/h/del{index:D4}", $"/h/del{index:D4}", "h0", 512);
        for (int tree = 0; tree < 3; tree++)
        {
            Directory.CreateDirectory($"/h/tree{tree}/sub");
            for (int index = 0; index < 100; index++) WriteGenerated($"/h/tree{tree}/sub/f{index:D3}", $"/h/tree{tree}/f{index:D3}", "h0", 4096);
        }

        Sync();
        var before = StatRoot();
        for (int index = 0; index < RootProbeCatalog.HCount; index++) File.Delete($"/h/del{index:D4}");
        for (int tree = 0; tree < 3; tree++) DeleteTree($"/h/tree{tree}");
        Sync();
        var deleted = StatRoot();
        Check(deleted.FreeBlocks > before.FreeBlocks && deleted.FreeInodes >= before.FreeInodes + 2000 + 306,
            $"Zaehler nach Loeschen: Bloecke {before.FreeBlocks}->{deleted.FreeBlocks}, Inodes {before.FreeInodes}->{deleted.FreeInodes}");
        for (int index = 0; index < RootProbeCatalog.HCount; index++)
            Check(files.WriteBytes($"/h/new{index:D4}", RootProbeContent.Create("h1", $"/h/new{index:D4}", 512)).IsOK(), "WriteBytes /h/new");
        Sync();
        var created = StatRoot();
        Check(created.FreeInodes <= deleted.FreeInodes - 2000 && created.FreeBlocks < deleted.FreeBlocks,
            $"Zaehler nach Neuanlegen: Bloecke {deleted.FreeBlocks}->{created.FreeBlocks}, Inodes {deleted.FreeInodes}->{created.FreeInodes}");
        Pass($"frei Inodes {before.FreeInodes}->{deleted.FreeInodes}->{created.FreeInodes}, Bloecke {before.FreeBlocks}->{deleted.FreeBlocks}->{created.FreeBlocks}");
        AfterPhase(root);
    }

    private static void StepHRun2(IBlockDevice root)
    {
        Begin("h-run2");
        for (int index = 0; index < RootProbeCatalog.HRun2; index++) File.Delete($"/h/new{index:D4}");
        for (int index = 0; index < RootProbeCatalog.HRun2; index++) WriteGenerated($"/h/run2_{index:D3}", $"/h/run2_{index:D3}", "h2", 512);
        Pass($"{RootProbeCatalog.HRun2} geloescht, {RootProbeCatalog.HRun2} neu");
        AfterPhase(root);
    }

    private static void StepI(IBlockDevice root, FileService files)
    {
        Begin("i");
        Directory.CreateDirectory("/i");
        string[] names = [RootProbeCatalog.LongName, RootProbeCatalog.Utf8Name, "A", "a"];
        for (int index = 0; index < names.Length; index++)
        {
            string path = "/i/" + names[index];
            WriteGenerated(path, path, "i", 10 + index);
        }

        var listed = files.ListEntries("/i");
        Check(listed.IsOK(), "ListEntries /i: " + listed.Status.Message);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in listed.Value!) set.Add(Path.GetFileName(entry.TrimEnd('/')));
        foreach (string name in names) Check(set.Contains(name), $"'{name}' fehlt in der Auflistung");
        Check(set.Count == names.Length, $"{set.Count} Eintraege statt {names.Length}");
        for (int index = 0; index < names.Length; index++)
        {
            string path = "/i/" + names[index];
            var read = files.ReadBytes(path, 64);
            Check(read.IsOK() && read.Value!.AsSpan().SequenceEqual(RootProbeContent.Create("i", path, 10 + index)), "FileService liest " + path + " falsch");
        }

        Pass("255 Byte, UTF-8, A und a getrennt (Ordinal)");
        AfterPhase(root);
    }

    private static void StepJ(IBlockDevice root)
    {
        Begin("j");
        Directory.CreateDirectory("/j/a");
        Directory.CreateDirectory("/j/c");
        for (int index = 0; index < RootProbeCatalog.JReadFiles; index++) WriteGenerated($"/j/c/r{index:D2}", $"/j/c/r{index:D2}", "j", 4096);

        var worker = new JWorker();
        // Wie das Produkt ueber ThreadStarter: ohne Stack im nativen Heap ein Fehler statt Page Fault (1G-Lauf 18.11.).
        var threads = new List<Thread>();
        foreach (ThreadStart body in new ThreadStart[] { worker.Create, worker.RenameDelete, worker.Read })
        {
            var started = ProjektZeh.Scheduling.ThreadStarter.TryStart(body);
            Check(started.IsOK(), "Thread-Start: " + started.Status.Message);
            threads.Add(started.Value!);
        }
        Thread.Sleep(ConcurrencySeconds * 1000);
        worker.Stop = true;
        foreach (var thread in threads) Check(thread.Join(30000), "Aufgabe endet nicht (Haenger)");
        Check(worker.Unexpected == null, "unerwartete Ausnahme: " + worker.Unexpected);
        Check(worker.ReadMismatch == 0, $"{worker.ReadMismatch} Lesefehler in /j/c");

        int checkedFiles = 0;
        foreach (string entry in Directory.GetFileSystemEntries("/j/a"))
        {
            string name = Path.GetFileName(entry);
            string source = "/j/a/f" + name[1..];
            Check(Matches(entry, new RootProbeEntry(entry, source, "j", 1000)), entry + " weicht ab");
            checkedFiles++;
        }

        Pass($"60 s: angelegt {worker.Created}, umbenannt/geloescht {worker.Changed}, gelesen {worker.Reads}, erwartete IOException {worker.ExpectedIo}, {checkedFiles} Dateien gleich");
        AfterPhase(root);
    }

    private static void StepK(IBlockDevice root)
    {
        Begin("k");
        Directory.CreateDirectory("/k");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        WriteGenerated(RootProbeCatalog.BigPath, RootProbeCatalog.BigPath, "k", RootProbeCatalog.BigLength);
        Write($"k INFO 2 GiB - 1 geschrieben in {clock.Elapsed.TotalSeconds:F0} s");
        Check(Matches(RootProbeCatalog.BigPath, new RootProbeEntry(RootProbeCatalog.BigPath, RootProbeCatalog.BigPath, "k", RootProbeCatalog.BigLength)), "2 GiB - 1 weicht ab");
        Write($"k INFO geschrieben und gelesen in {clock.Elapsed.TotalSeconds:F0} s");
        FinishBig(root, "2 GiB - 1 lesbar");
    }

    /// <summary>k verkuerzt (Lauf 8, Leitungsentscheid 19:02): nur der letzte Block vor der 2-GiB-Grenze per Seek, nicht der ganze Inhalt.</summary>
    private static void StepKBoundary(IBlockDevice root)
    {
        Begin("k");
        Directory.CreateDirectory("/k");
        // Beginn eines 4-KiB-Abschnitts, damit RootProbeContent.Fill ab hier den Inhalt der vollen Datei liefert.
        long start = (RootProbeCatalog.BigLength / RootProbeContent.ChunkSize - 1) * RootProbeContent.ChunkSize;
        byte[] tail = new byte[RootProbeCatalog.BigLength - start];
        RootProbeContent.Fill("k", RootProbeCatalog.BigPath, start, tail);
        using (var stream = new FileStream(RootProbeCatalog.BigPath, FileMode.CreateNew, FileAccess.Write))
        {
            Check(stream.Seek(start, SeekOrigin.Begin) == start, "Seek vor die 2-GiB-Grenze gescheitert");
            stream.Write(tail, 0, tail.Length);
        }

        byte[] read = new byte[tail.Length];
        using (var stream = new FileStream(RootProbeCatalog.BigPath, FileMode.Open, FileAccess.Read))
        {
            Check(stream.Seek(start, SeekOrigin.Begin) == start, "Seek beim Lesen gescheitert");
            int total = 0, got;
            while (total < read.Length && (got = stream.Read(read, total, read.Length - total)) > 0) total += got;
            Check(total == read.Length && read.AsSpan().SequenceEqual(tail), "letzter Abschnitt vor 2 GiB weicht ab");
        }

        FinishBig(root, "letzter Abschnitt vor 2 GiB per Seek lesbar");
    }

    /// <summary>Gemeinsamer Abschluss von k: Laenge 2 GiB - 1, ein weiteres Byte abgelehnt, large_file bleibt aus.</summary>
    private static void FinishBig(IBlockDevice root, string what)
    {
        bool refused = false;
        try
        {
            using var stream = new FileStream(RootProbeCatalog.BigPath, FileMode.Append, FileAccess.Write);
            stream.Write([0x42], 0, 1);
        }
        catch (IOException)
        {
            refused = true;
        }

        long length = new FileInfo(RootProbeCatalog.BigPath).Length;
        Sync();
        uint roCompat = BitConverter.ToUInt32(ReadSuperblock(root), CosmosExt2SuperblockLayout.FeatureRoCompatOffset);
        Write($"k INFO Anhaengen {(refused ? "IOException" : "ohne Ausnahme")}, Laenge danach {length}, ro_compat 0x{roCompat:X}");
        Check(length == RootProbeCatalog.BigLength, $"Laenge {length}: 2 GiB - 1 still ueberschritten");
        Check((roCompat & 2) == 0 && GateOf(root) != Ext2MountDecision.Reject, $"large_file gesetzt (ro_compat 0x{roCompat:X})");
        Pass(what + ", Anhaengen abgelehnt, large_file aus");
        AfterPhase(root);
    }

    /// <param name="target">Datei fuer l2; null = Integritaetsdatei aus d (Lauf 1).</param>
    private static void StepL(IBlockDevice root, FileService files, RootProbeEntry? target)
    {
        Begin("l1");
        Directory.CreateDirectory("/l");
        int large = FillUntilFull("/l/z", FillFileMiB * IoChunk, 0);
        // Zwischenstufe 1 MiB: der Rest nach den 64-MiB-Dateien faende sonst bis zu 16 384 Kleindateien in einem Verzeichnis
        // (Cosmos-Grenze 8190 Eintraege, Inode-Cache) und meldete "Verzeichnis voll" statt "Datentraeger voll".
        int medium = FillUntilFull("/l/m", IoChunk, 0);
        int small = FillUntilFull("/l/s", SmallFill, 0);
        Check(!Refused(() => Directory.CreateDirectory("/l/dirfull")) || !Directory.Exists("/l/dirfull"), "Mkdir trotz vollem Datentraeger");
        Check(!files.WriteBytes("/l/fs-full.bin", new byte[SmallFill]).IsOK(), "FileService schrieb trotz vollem Datentraeger");
        Check(!files.CreateDirectory("/l/fs-dir").IsOK() || !Directory.Exists("/l/fs-dir"), "FileService-Mkdir trotz vollem Datentraeger");
        uint reserved = BitConverter.ToUInt32(ReadSuperblock(root), CosmosExt2SuperblockLayout.RBlocksCountOffset);
        for (int index = 0; index < large; index += 50) Check(IsZero($"/l/z{index:D5}"), $"/l/z{index:D5} nicht nur Nullen");
        File.Delete("/l/z00000");
        File.WriteAllBytes("/l/after.bin", new byte[SmallFill]);
        Pass($"{large} x 64 MiB, {medium} x 1 MiB, {small} x 4 KiB bis voll, Fehler ueber System.IO und FileService, s_r_blocks_count {reserved}, nach Loeschen schreibbar");
        AfterPhase(root);

        Begin("l2");
        FillUntilFull("/l/y", FillFileMiB * IoChunk, 0);
        FillUntilFull("/l/n", IoChunk, 0);
        FillUntilFull("/l/t", SmallFill, 0);
        target ??= new RootProbeEntry(RootProbeCatalog.FullReplaceTarget, RootProbeCatalog.FullReplaceTarget, "d", 65536);
        var replaced = files.WriteBytes(target.Path, new byte[2 * 65536]);
        Check(!replaced.IsOK(), "Ersetzen bei vollem Datentraeger gelang");
        Check(Matches(target.Path, target), "Original nach fehlgeschlagenem Ersetzen veraendert");
        DeleteTree("/l");
        Pass("Fehler, Original unveraendert; /l aufgeraeumt");
        AfterPhase(root);
    }

    private static void StepI2(IBlockDevice device)
    {
        Begin("i2");
        Check(CosmosExt2Formatter.FormatForKernel(device, new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = InodeLabel }), "FormatForKernel abgelehnt");
        Check(VfsManager.RegisterFileSystem(InodeFileSystem, new Ext2FileSystemType(device)), "Registrierung abgelehnt");
        Check(VfsManager.TryMount(InodeFileSystem, "", MountFlags.None, InodeMount, out _), "TryMount abgelehnt");
        mounted.Add(InodeMount);
        int created = 0;
        while (!Refused(() => { using var _ = new FileStream($"{InodeMount}/n{created:D6}", FileMode.CreateNew); })) created++;
        var files = new FileService(InodeMount, pathGuard: CosmosPathGuard.For(InodeMount));
        Check(!files.Touch(InodeMount + "/fs-full").IsOK(), "FileService legte trotz erschoepfter Inodes an");
        SyncMount(InodeMount);
        SyncMount(InodeMount);
        uint freeInodes = BitConverter.ToUInt32(ReadSuperblock(device), CosmosExt2SuperblockLayout.FreeInodesCountOffset);
        Check(freeInodes == 0, $"s_free_inodes_count {freeInodes} statt 0");
        for (int index = 0; index < 100; index++) File.Delete($"{InodeMount}/n{index:D6}");
        for (int index = 0; index < 100; index++) File.WriteAllBytes($"{InodeMount}/neu{index:D3}", []);
        mounted.Remove(InodeMount);
        Check(VfsManager.TryUnmount(InodeMount), "TryUnmount abgelehnt");
        device.Flush();
        Pass($"{created} Dateien bis zum Fehler, beide Wege, s_free_inodes_count 0, danach 100 geloescht und neu angelegt");
    }

    /// <summary>Herunterfahren wie das Produkt: /boot/efi aushaengen, Sync, Sync, s_state = 1, Flush; / bleibt eingehaengt.</summary>
    private static void StepM(IBlockDevice root)
    {
        Begin("m");
        if (mounted.Remove(EfiMount)) Check(VfsManager.TryUnmount(EfiMount), "TryUnmount /boot/efi abgelehnt");
        Sync();
        Sync();
        WriteState(root, 1);
        Counters(root, "m");
        Pass("Sync, Sync, s_state 1, Device.Flush(); / nicht ausgehaengt");
    }

    // ---------------------------------------------------------------- Pruefen

    private static void Verify(IBlockDevice root, int stage, string name)
    {
        Begin(name);
        int count = 0;
        foreach (var entry in RootProbeCatalog.Expected(stage))
        {
            Check(Matches(entry.Path, entry), entry.Path + " weicht von der neu berechneten Erwartung ab");
            count++;
        }

        Pass($"{count} Integritaetsdateien (Stufe {stage}) gleich");
        AfterPhase(root);
    }

    /// <summary>Nach jeder Phase: Sync, s_state muss 0 bleiben (b), Zaehler fuer den Hostvergleich.</summary>
    private static void AfterPhase(IBlockDevice root)
    {
        string phase = step;
        Sync();
        ushort state = ReadState(root);
        Begin(phase + "-state");
        Check(state == 0, $"s_state {state} nach Phase {phase}: der Treiber hat das Kennzeichen ueberschrieben");
        Pass("s_state 0");
        Counters(root, phase);
        Begin(phase);
    }

    private static void Counters(IBlockDevice root, string phase)
    {
        byte[] superblock = ReadSuperblock(root);
        uint freeBlocks = BitConverter.ToUInt32(superblock, CosmosExt2SuperblockLayout.FreeBlocksCountOffset);
        uint freeInodes = BitConverter.ToUInt32(superblock, CosmosExt2SuperblockLayout.FreeInodesCountOffset);
        uint reserved = BitConverter.ToUInt32(superblock, CosmosExt2SuperblockLayout.RBlocksCountOffset);
        ushort state = BitConverter.ToUInt16(superblock, CosmosExt2SuperblockLayout.StateOffset);
        Write($"COUNTERS {phase} free_blocks={freeBlocks} free_inodes={freeInodes} r_blocks={reserved} state={state}");
    }

    private static bool Matches(string path, RootProbeEntry entry)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        if (stream.Length != entry.Length) return false;
        byte[] actual = IoBuffer;
        byte[] expected = ExpectedBuffer;
        for (long offset = 0; offset < entry.Length; offset += IoChunk)
        {
            int length = (int)Math.Min(IoChunk, entry.Length - offset);
            int read = 0;
            while (read < length)
            {
                int got = stream.Read(actual, read, length - read);
                if (got <= 0) return false;
                read += got;
            }

            RootProbeContent.Fill(entry.Seed, entry.Source, offset, expected.AsSpan(0, length));
            if (!actual.AsSpan(0, length).SequenceEqual(expected.AsSpan(0, length))) return false;
        }

        return stream.Read(actual, 0, 1) == 0;
    }

    private static bool IsZero(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        byte[] buffer = IoBuffer;
        int got;
        while ((got = stream.Read(buffer, 0, buffer.Length)) > 0)
            if (buffer.AsSpan(0, got).IndexOfAnyExcept((byte)0) >= 0) return false;
        return true;
    }

    // ---------------------------------------------------------------- Schreiben

    private static void WriteGenerated(string path, string source, string seed, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        byte[] buffer = IoBuffer;
        for (long offset = 0; offset < length; offset += buffer.Length)
        {
            int chunk = (int)Math.Min(buffer.Length, length - offset);
            RootProbeContent.Fill(seed, source, offset, buffer.AsSpan(0, chunk));
            stream.Write(buffer, 0, chunk);
        }
    }

    /// <summary>Nullen-Dateien bis zur ersten IOException; liefert die Zahl vollstaendig geschriebener Dateien.</summary>
    private static int FillUntilFull(string prefix, int size, int start)
    {
        for (int index = start; ; index++)
        {
            string path = $"{prefix}{index:D5}";
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                for (int written = 0; written < size; written += Math.Min(IoChunk, size - written))
                    stream.Write(zeros, 0, Math.Min(IoChunk, size - written));
            }
            catch (IOException exception)
            {
                Write($"{step} INFO voll bei {path}: {exception.Message}");
                return index - start;
            }
        }
    }

    private static void DeleteTree(string path)
    {
        foreach (string entry in Directory.GetFileSystemEntries(path))
        {
            if (Directory.Exists(entry)) DeleteTree(entry);
            else File.Delete(entry);
        }

        Directory.Delete(path);
    }

    // ---------------------------------------------------------------- Geraete, Superblock, Mounts

    private static int ReadControl()
    {
        IBlockDevice control = FindDevice(ControlDeviceName, size => size == MiB, "1 MiB");
        byte[] sector = new byte[control.BlockSize];
        control.ReadBlock(0, 1, sector);
        string text = Encoding.ASCII.GetString(sector);
        Check(text.StartsWith(ControlPrefix, StringComparison.Ordinal), "Steuerplatte ohne Kennung; nichts geschrieben");
        int end = text.IndexOf('\n', ControlPrefix.Length);
        bool parsed = int.TryParse(text.AsSpan(ControlPrefix.Length, (end < 0 ? text.Length : end) - ControlPrefix.Length),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int run);
        Check(parsed && run is >= 1 and <= 15, "Lauf ausserhalb 1..15; nichts geschrieben");
        return run;
    }

    private static IBlockDevice FindDevice(string name, Func<ulong, bool> size, string expected)
    {
        foreach (IBlockDevice device in StorageManager.Devices)
        {
            if (!string.Equals(device.Name, name, StringComparison.Ordinal)) continue;
            Check(device.BlockSize == 512 && size(device.BlockCount * device.BlockSize),
                $"{name} ist nicht genau {expected} mit 512-Byte-Sektoren ({device.BlockCount} x {device.BlockSize}); nichts geschrieben");
            return device;
        }

        Check(false, $"{name} fehlt; nichts geschrieben");
        throw new ProbeFailure();
    }

    private static bool IsBlank(IBlockDevice device)
    {
        byte[] prefix = new byte[CheckedPrefix];
        device.ReadBlock(0, (ulong)CheckedPrefix / device.BlockSize, prefix);
        return prefix.AsSpan().IndexOfAnyExcept((byte)0) < 0;
    }

    private static byte[] ReadSuperblock(IBlockDevice device)
    {
        byte[] superblock = new byte[CosmosExt2SuperblockLayout.SuperblockSize];
        device.ReadBlock(SuperblockLba, (ulong)superblock.Length / device.BlockSize, superblock);
        return superblock;
    }

    private static Ext2MountDecision GateOf(IBlockDevice device) => Ext2FeatureGate.Evaluate(ReadSuperblock(device));

    private static ushort ReadState(IBlockDevice device) =>
        BitConverter.ToUInt16(ReadSuperblock(device), CosmosExt2SuperblockLayout.StateOffset);

    /// <summary>s_state per Lesen-Aendern-Schreiben am Treiber vorbei, danach Device.Flush() (Etappe 4, Abschnitt 2).</summary>
    private static void WriteState(IBlockDevice device, ushort state)
    {
        byte[] superblock = ReadSuperblock(device);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.StateOffset, 2), state);
        device.WriteBlock(SuperblockLba, (ulong)superblock.Length / device.BlockSize, superblock);
        device.Flush();
        Check(ReadState(device) == state, $"s_state {state} nicht zurueckgelesen");
    }

    private static void MountRoot(IBlockDevice root)
    {
        Check(VfsManager.RegisterFileSystem(RootFileSystem, new Ext2FileSystemType(root)), "Registrierung ext2 abgelehnt");
        Check(VfsManager.TryMount(RootFileSystem, "", MountFlags.None, "/", out _), "TryMount / abgelehnt");
        mounted.Add("/");
    }

    private static void MountEfi()
    {
        if (!Directory.Exists(EfiMount)) Directory.CreateDirectory(EfiMount);
        Check(VfsManager.TryMount(FatFileSystem, "", MountFlags.None, EfiMount, out _), "TryMount /boot/efi abgelehnt");
        mounted.Add(EfiMount);
    }

    private static FileService Files() => new("/", pathGuard: CosmosPathGuard.For("/"));

    private static void Sync() => SyncMount("/");

    private static void SyncMount(string mountPoint)
    {
        Check(VfsManager.TryGetMount(mountPoint, out VfsMount? mount), mountPoint + " nicht eingehaengt");
        Check(mount!.Superblock.SuperOperations.Sync(mount.Superblock), "Sync " + mountPoint + " abgelehnt");
    }

    private static VfsStatFs StatRoot()
    {
        Check(VfsManager.TryStatFs("/", out VfsStatFs stat), "StatFs / abgelehnt");
        return stat;
    }

    // Jede IOException gilt als Verweigerung; andere Ausnahmetypen sind FAIL (Spec Abschnitt 2).
    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool Finish()
    {
        // Nur Nebenmounts aushaengen; / bleibt wie im Produkt eingehaengt, der Host bewertet das Image. / wird einmal
        // synchronisiert, damit die Superblockzaehler am Image zum Stand beim Fehler passen (sonst meldet e2fsck sie als falsch).
        foreach (string mountPoint in mounted.ToArray())
        {
            if (mountPoint == "/")
            {
                try
                {
                    if (VfsManager.TryGetMount("/", out VfsMount? root) && !root!.Superblock.SuperOperations.Sync(root.Superblock)) Write("sync / FAIL nach Fehler");
                }
                catch (Exception exception)
                {
                    Write($"sync / FAIL nach Fehler: {exception.GetType().Name}: {exception.Message}");
                }

                continue;
            }
            try
            {
                if (!VfsManager.TryUnmount(mountPoint)) Write($"unmount {mountPoint} FAIL nach Fehler");
            }
            catch (Exception exception)
            {
                Write($"unmount {mountPoint} FAIL nach Fehler: {exception.GetType().Name}: {exception.Message}");
            }
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

    private static void Write(string message) => Log.WriteString($"STORAGE_ROOT {message}\n");

    /// <summary>Drei nebenlaeufige Aufgaben fuer Schritt j; erwartete IOException zaehlen, jede andere Ausnahme beendet.</summary>
    private sealed class JWorker
    {
        public volatile bool Stop;
        public volatile string? Unexpected;
        public int Created;
        public int Changed;
        public int Reads;
        public int ReadMismatch;
        public int ExpectedIo;

        // Wie das Produkt: jeder Plattenzugriff unter StorageLock (Diagnoselauf 13: ohne Sperre Metadatenkorruption).
        public void Create() => Loop(() =>
        {
            string path = $"/j/a/f{Created:D6}";
            byte[] content = RootProbeContent.Create("j", path, 1000);
            StorageLock.Run(() => File.WriteAllBytes(path, content));
            Created++;
        });

        public void RenameDelete() => Loop(() =>
        {
            int target = Changed;
            string source = $"/j/a/f{target:D6}";
            if (!StorageLock.Run(() => File.Exists(source))) { Thread.Sleep(1); return; }
            if (target % 2 == 0) StorageLock.Run(() => File.Move(source, $"/j/a/g{target:D6}"));
            else StorageLock.Run(() => File.Delete(source));
            Changed++;
        });

        public void Read() => Loop(() =>
        {
            int index = Reads % RootProbeCatalog.JReadFiles;
            string path = $"/j/c/r{index:D2}";
            if (!StorageLock.Run(() => File.ReadAllBytes(path)).AsSpan().SequenceEqual(RootProbeContent.Create("j", path, 4096))) Interlocked.Increment(ref ReadMismatch);
            Reads++;
        });

        private void Loop(Action action)
        {
            while (!Stop && Unexpected == null)
            {
                try
                {
                    action();
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref ExpectedIo);
                }
                catch (Exception exception)
                {
                    Unexpected = $"{exception.GetType().Name}: {exception.Message}";
                }
            }
        }
    }

    private sealed class ProbeFailure : Exception
    {
    }
}
#endif
