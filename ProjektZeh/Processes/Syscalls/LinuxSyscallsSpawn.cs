using System;
using System.Collections.Generic;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V15: vfork (58) und execve (59); posix_spawn von musl nimmt clone mit CLONE_VM|CLONE_VFORK (siehe <see cref="LinuxUserProcess"/>).
/// V21: fork (57) und clone3 (435) ohne CLONE_VM werden ein Prozess mit kopiertem Adressraum.
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long Fork = 57, Vfork = 58, Execve = 59, Clone3 = 435;

    // clone3: Größe von clone_args VER0 (64 Bytes) und größte Signalnummer (Linux _NSIG).
    private const ulong CloneArgsSizeVer0 = 64, CloneArgsMax = 4096;
    private const ulong MaxSignal = 64;

    /// <summary>Linux: MAX_ARG_STRLEN (32 Seiten) je Zeichenkette.</summary>
    private const int MaxArgString = 32 * 4096;

    /// <summary>Höchstzahl Einträge in argv und envp zusammen; Linux begrenzt über den Platz (ein Viertel des Stacks, 2 MiB).</summary>
    private const int MaxArgCount = 32768;

    /// <summary>Gesamtgröße aller Zeichenketten aus argv und envp (wie das Linux-Standardlimit bei 8 MiB Stack).</summary>
    private const long MaxArgBytes = 2L << 20;

    private const long E2BIG = 7;

    private static long DispatchSpawn(ref SyscallArgs a, ILinuxProcess p)
    {
        switch (a.Number)
        {
            case Vfork:
                // vfork ist clone(CLONE_VM|CLONE_VFORK|SIGCHLD) ohne eigenen Stack.
                return p is ILinuxThreads cloner
                    ? cloner.CloneThread(LinuxUserProcess.CloneVm | LinuxUserProcess.CloneVfork | 17, 0, 0, 0, 0)
                    : -ENOSYS;
            case Fork:
                // fork ist clone(SIGCHLD) ohne Stack: neuer Prozess mit eigener Kopie des Adressraums (V21).
                return p is ILinuxThreads forker ? forker.CloneThread(17, 0, 0, 0, 0) : -ENOSYS;
            case Clone3:
                return DoClone3(p, a.A0, a.A1);
            case Execve:
                return DoExecve(p, a.A0, a.A1, a.A2);
            default:
                return -ENOSYS;
        }
    }

    /// <summary>
    /// V21 clone3: liest clone_args (VER0 und länger; Bytes hinter VER0 müssen 0 sein, wie Linux für unbekannte Felder) und führt auf
    /// clone zurück. pidfd, set_tid und cgroup gibt es nicht (CLONE_PIDFD bleibt -ENOSYS über die Flagprüfung von clone); exit_signal steht
    /// bei clone3 getrennt von den Flags und muss eine gültige Signalnummer sein (sonst -EINVAL). stack_size zählt wie bei Linux:
    /// clone3 übergibt stack + stack_size als Stackzeiger.
    /// </summary>
    private static long DoClone3(ILinuxProcess p, ulong argsPtr, ulong size)
    {
        if (p is not ILinuxThreads cloner)
        {
            return -ENOSYS;
        }

        if (size < CloneArgsSizeVer0 || size > CloneArgsMax)
        {
            return size > CloneArgsMax ? -E2BIG : -EINVAL;
        }

        Span<byte> raw = stackalloc byte[(int)size];
        if (!p.Memory.TryRead(argsPtr, raw))
        {
            return -EFAULT;
        }

        foreach (byte b in raw[(int)CloneArgsSizeVer0..])
        {
            if (b != 0)
            {
                return -E2BIG; // Linux copy_struct_from_user: unbekannte, gesetzte Felder
            }
        }

        ulong flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw);
        ulong pidfd = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]);
        ulong childTid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[16..]);
        ulong parentTid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[24..]);
        ulong exitSignal = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[32..]);
        ulong stack = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[40..]);
        ulong stackSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[48..]);
        ulong tls = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[56..]);

        // Linux clone3_args_valid: Signal nur im eigenen Feld, nie in den unteren Flagbits; Stack und Größe nur zusammen.
        if ((flags & 0xff) != 0 || exitSignal > MaxSignal || ((stack == 0) != (stackSize == 0)) || stack > ulong.MaxValue - stackSize)
        {
            return -EINVAL;
        }

        _ = pidfd; // nur mit CLONE_PIDFD gelesen, das clone ablehnt
        return cloner.CloneThread(flags | exitSignal, stack == 0 ? 0 : stack + stackSize, parentTid, childTid, tls);
    }

    private static long DoExecve(ILinuxProcess p, ulong pathPtr, ulong argvPtr, ulong envpPtr)
    {
        if (p is not ILinuxSpawn spawn || p is not ILinuxFiles f)
        {
            return -ENOSYS;
        }

        string path;
        lock (f.FileGate)
        {
            long r = LinuxPath.Resolve(p, f, LinuxPath.AtFdcwd, pathPtr, out path);
            if (r < 0)
            {
                return r;
            }
        }

        long total = 0;
        long got = ReadStringArray(p, argvPtr, ref total, out string[] argv);
        if (got < 0)
        {
            return got;
        }

        got = ReadStringArray(p, envpPtr, ref total, out string[] envp);
        return got < 0 ? got : spawn.Exec(path, argv, envp);
    }

    /// <summary>Liest ein nullterminiertes Zeigerfeld aus dem Benutzerspeicher; 0 als Feld gilt wie unter Linux als leer.</summary>
    private static long ReadStringArray(ILinuxProcess p, ulong array, ref long total, out string[] values)
    {
        values = [];
        if (array == 0)
        {
            return 0;
        }

        var list = new List<string>();
        Span<byte> slot = stackalloc byte[8];
        for (ulong i = 0; ; i++)
        {
            if (i >= MaxArgCount || total > MaxArgBytes)
            {
                return -E2BIG;
            }

            if (array + i * 8 < array || !p.Memory.TryRead(array + i * 8, slot))
            {
                return -EFAULT;
            }

            ulong ptr = BitConverter.ToUInt64(slot);
            if (ptr == 0)
            {
                break;
            }

            if (!p.Memory.TryReadCString(ptr, MaxArgString, out string s))
            {
                return -EFAULT;
            }

            total += s.Length + 1;
            list.Add(s);
        }

        values = list.ToArray();
        return total > MaxArgBytes ? -E2BIG : 0;
    }
}
