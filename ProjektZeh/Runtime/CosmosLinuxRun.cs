#if ZEHOS_LINUX_RUN
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Memory;
using ProjektZeh.Processes;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // UserMode und UserPageTable gelten als Vorschau.

/// <summary>
/// Brücke von Ring 3 zu <see cref="LinuxUserProcess"/> (Ladybird V3, nur Testbau <c>-p:ZehOSLinuxRun=true</c>): legt Seitentabelle und
/// Frames an, lädt das Abbild, betritt Ring 3 in einem eigenen Thread und leitet Systemaufrufe und Seitenfehler an den Prozess.
/// Es läuft immer nur ein Programm (die Handler sind prozessweit).
/// </summary>
internal static unsafe class CosmosLinuxRun
{
    private const ulong PageSize = 4096;
    private const int TimeoutSeconds = 120;

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "VirtualToPhysical")]
    private static extern ulong ToPhysical([UnsafeAccessorType("Cosmos.Kernel.Core.Memory.PageAllocator, Cosmos.Kernel.Core")] object? owner, ulong address);

    private static LinuxUserProcess? current;
    private static ulong lastFsBase;
    private static int busy;
    private static ulong hhdm;
    private static bool initialized;
    private static ulong faultVector, faultError, faultAddress, faultRip;

    /// <summary>Seiten eines Prozesses über <see cref="UserPageTable"/>; Frames gehören dem Prozess und gehen mit <see cref="Dispose"/> zurück.</summary>
    private sealed class KernelPages : IUserPages
    {
        private readonly UserPageTable _table;
        private readonly Dictionary<ulong, ulong> _frames = new();

        public KernelPages(UserPageTable table) => _table = table;

        public bool Map(ulong page, MapAccess access)
        {
            bool writable = access == MapAccess.ReadWrite, executable = access == MapAccess.ReadExecute;
            if (_frames.ContainsKey(page))
            {
                return _table.TryProtect(page, writable, executable);
            }

            ulong frame = UserPageTable.AllocateFrame();
            if (frame == 0)
            {
                return false;
            }

            if (!_table.TryMap(page, frame, writable, executable))
            {
                UserPageTable.FreeFrame(frame);
                return false;
            }

            _frames[page] = frame;
            return true;
        }

        public bool IsMapped(ulong page) => _frames.ContainsKey(page);

        public void Protect(ulong page, MapAccess access)
        {
            if (_frames.ContainsKey(page))
            {
                _table.TryProtect(page, access == MapAccess.ReadWrite, access == MapAccess.ReadExecute);
            }
        }

        public void Unmap(ulong page)
        {
            if (_frames.TryGetValue(page, out ulong frame) && _table.Unmap(page) != 0)
            {
                _frames.Remove(page);
                UserPageTable.FreeFrame(frame);
            }
        }

        public void Read(ulong page, int offset, Span<byte> dst) => new ReadOnlySpan<byte>((void*)(_frames[page] + hhdm + (ulong)offset), dst.Length).CopyTo(dst);

        public void Write(ulong page, int offset, ReadOnlySpan<byte> src) => src.CopyTo(new Span<byte>((void*)(_frames[page] + hhdm + (ulong)offset), src.Length));

        /// <summary>Erst die Tabelle (wartet auf CPUs, die sie noch halten), dann die Frames.</summary>
        public void Dispose()
        {
            _table.Dispose();
            foreach (ulong frame in _frames.Values)
            {
                UserPageTable.FreeFrame(frame);
            }

            _frames.Clear();
        }
    }

    /// <summary>Führt ein statisches Linux-Programm aus und wartet auf sein Ende. Die Ausgabe geht zeilenweise nach <paramref name="output"/>.</summary>
    public static ResultErr Run(byte[] elf, string[] argv, string[] envp, ITextOutput output)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Es läuft schon ein Linux-Programm.");
        }

        try
        {
            if (!Prepare())
            {
                return ResultErr.CreateFailure(ResultCode.Failure, "Ring 3 ist nicht verfügbar.");
            }

            UserImage image;
            try
            {
                image = ElfLoader.Load(elf, argv, envp);
            }
            catch (ElfLoadException e)
            {
                return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Kein ladbares Programm: " + e.Message);
            }

            ulong result = ulong.MaxValue;
            string? failure = null;
            int finished = 0;
            var thread = new Thread(() =>
            {
                try
                {
                    result = Execute(elf, image, output, out failure);
                }
                finally
                {
                    Volatile.Write(ref finished, 1);
                }
            });
            thread.Start();
            if (!thread.Join(TimeoutSeconds * 1000))
            {
                return ResultErr.CreateFailure(ResultCode.Failure, "Zeitüberschreitung nach " + TimeoutSeconds + " s; das Programm läuft weiter.");
            }

            if (failure != null)
            {
                return ResultErr.CreateFailure(ResultCode.Failure, failure);
            }

            output.WriteLine("[Programm beendet, Code " + (long)result + "]");
            return result == 0 ? ResultErr.CreateSuccess() : ResultErr.CreateFailure(ResultCode.Failure, "Beendet mit Code " + (long)result);
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }

    private static bool Prepare()
    {
        if (initialized)
        {
            return true;
        }

        if (!UserPageTable.ShareKernelHalf())
        {
            return false;
        }

        // Versatz des Kernel-Direktabbilds: aus einer Heapseite bestimmt wie in den Proben.
        ulong raw = (ulong)NativeMemory.AllocZeroed((nuint)(2 * PageSize));
        ulong aligned = (raw + PageSize - 1) & ~(PageSize - 1);
        hhdm = aligned - ToPhysical(null, aligned);
        NativeMemory.Free((void*)raw);
        UserMode.SyscallHandler = &OnSyscall;
        UserMode.FaultHandler = &OnFault;
        initialized = UserMode.Initialize();
        return initialized;
    }

    private static ulong Execute(byte[] elf, UserImage image, ITextOutput output, out string? failure)
    {
        failure = null;
        UserPageTable? table = UserPageTable.TryCreate();
        if (table == null)
        {
            failure = "Keine Seitentabelle (Speicher).";
            return ulong.MaxValue;
        }

        var pages = new KernelPages(table);
        var process = new LinuxUserProcess(image, pages, output, 1, code => UserMode.LeaveCurrent((ulong)code));
        try
        {
            ResultErr loaded = process.Load(elf);
            if (!loaded.IsOK())
            {
                failure = "Laden fehlgeschlagen: " + loaded.Message;
                return ulong.MaxValue;
            }

            faultVector = 0;
            lastFsBase = 0;
            table.BindToCurrentThread();
            current = process;
            ulong result;
            try
            {
                result = UserMode.Enter(image.Entry, image.StackPointer);
            }
            finally
            {
                current = null;
                UserPageTable.UnbindCurrentThread();
            }

            if (result == UserMode.Faulted)
            {
                failure = "Abbruch durch Fehler: Vektor " + faultVector + " Fehlercode 0x" + faultError.ToString("X") + " Adresse 0x" + faultAddress.ToString("X") + " rip 0x" + faultRip.ToString("X");
            }
            else if (!process.Exited)
            {
                failure = "Ring 3 verlassen ohne Programmende (Code 0x" + result.ToString("X") + ").";
            }

            return result;
        }
        finally
        {
            pages.Dispose();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnSyscall(UserFrame* frame)
    {
        LinuxUserProcess? process = current;
        if (process == null)
        {
            frame->Rax = unchecked((ulong)-38L);
            return;
        }

        long result = process.Syscall((long)frame->Rax, frame->Rdi, frame->Rsi, frame->Rdx, frame->R10, frame->R8, frame->R9);
        frame->Rax = unchecked((ulong)result);
        if (process.FsBase != lastFsBase)
        {
            lastFsBase = process.FsBase;
            UserMode.SetFsBase(lastFsBase);
        }
    }

    [UnmanagedCallersOnly]
    private static int OnFault(UserFrame* frame, ulong vector, ulong error, ulong address)
    {
        LinuxUserProcess? process = current;
        // Seite nicht vorhanden (Bit 0 aus): in einem belegten Bereich nachladen, sonst abbrechen.
        if (process != null && vector == 14 && (error & 1) == 0 && process.HandlePageFault(address, (error & 2) != 0, (error & 16) != 0))
        {
            return 1;
        }

        faultVector = vector;
        faultError = error;
        faultAddress = address;
        faultRip = frame->Rip;
        return 0;
    }
}
#endif
