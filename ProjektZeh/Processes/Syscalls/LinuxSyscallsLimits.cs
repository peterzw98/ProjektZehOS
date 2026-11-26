using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Was der Verteiler über den Adressraum wissen muss (V9, js-Vorbereitung). Optional neben <see cref="ILinuxProcess"/>:
/// ohne sie liefert <c>mremap</c> -ENOSYS und <c>RLIMIT_STACK</c> den Linux-Standard 8 MiB.
/// </summary>
internal interface ILinuxAddressSpace
{
    /// <summary>true, wenn jede Seite von [addr, addr+len) zu einer Abbildung gehört (auch PROT_NONE).</summary>
    bool IsMapped(ulong addr, ulong len);

    /// <summary>Größe des Hauptstacks (die Abbildung, nicht der benutzte Teil).</summary>
    ulong StackSize { get; }
}

/// <summary>
/// V9: Aufrufe, die Ladybirds js (statisch, musl, Rust-Laufzeit) vor dem ersten Skript braucht, siehe docs/LINUX_ABI.md, Abschnitt V9.
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long Mremap = 25, Getrlimit = 97, Prctl = 157, Setrlimit = 160, SchedGetaffinity = 204, Prlimit64 = 302;

    private const ulong RlimInfinity = ulong.MaxValue;
    private const int RlimitStack = 3, RlimitCore = 4, RlimitNofile = 7, RlimitCount = 16;
    private const ulong DefaultStackSize = 8UL << 20;
    private const ulong MremapMaymove = 1, MremapFixed = 2, MremapDontunmap = 4;
    private const int PrSetName = 15, PrGetName = 16;

    private static long DispatchLimits(ref SyscallArgs a, ILinuxProcess p)
    {
        switch (a.Number)
        {
            case Mremap: return DoMremap(p, a.A0, a.A1, a.A2, a.A3);
            case Prlimit64: return DoPrlimit(p, (long)a.A0, (int)a.A1, a.A2, a.A3);
            // Anders als prlimit64 (beide Zeiger optional) verlangen getrlimit/setrlimit ihren Zeiger: 0 ist -EFAULT.
            case Getrlimit: return a.A1 == 0 ? -EFAULT : DoPrlimit(p, 0, (int)a.A0, 0, a.A1);
            case Setrlimit: return a.A1 == 0 ? -EFAULT : DoPrlimit(p, 0, (int)a.A0, a.A1, 0);
            case SchedGetaffinity: return DoSchedGetaffinity(p, (long)a.A0, a.A1, a.A2);
            case Prctl: return DoPrctl(p, (int)a.A0, a.A1);
            default: return -ENOSYS;
        }
    }

    /// <summary>
    /// Nur, was ohne Verschieben geht: Verkleinern gibt das Ende frei, Vergrößern an Ort und Stelle und jedes Verschieben
    /// liefern -ENOMEM (Abweichung; musl-realloc und mimalloc fallen dann auf neu belegen und kopieren zurück).
    /// Wichtig ist die Fehlerunterscheidung: musls pthread_getattr_np misst den Hauptstack, indem es Seite um Seite unterhalb des
    /// Stackanfangs mremap versucht; -ENOMEM heißt „belegt, weiter“, -EFAULT „hier endet der Stack“. Ohne mremap (-ENOSYS) meldet
    /// es eine Seite, und Ladybirds Rust-Stackgrenze (AK/Rust/src/stack.rs) läge über dem Stackzeiger: jeder Aufruf wäre ein Überlauf.
    /// </summary>
    private static long DoMremap(ILinuxProcess p, ulong oldAddr, ulong oldSize, ulong newSize, ulong flags)
    {
        if (p is not ILinuxAddressSpace space)
        {
            return -ENOSYS;
        }

        if ((oldAddr & 0xfff) != 0 || (flags & ~(MremapMaymove | MremapFixed | MremapDontunmap)) != 0
            || ((flags & (MremapFixed | MremapDontunmap)) != 0 && (flags & MremapMaymove) == 0))
        {
            return -EINVAL;
        }

        // oldSize 0 heißt bei Linux „gemeinsame Abbildung duplizieren“; private Abbildungen (die einzigen hier) liefern -EINVAL.
        if (newSize == 0 || oldSize == 0 || newSize > MaxUserSpan || oldSize > MaxUserSpan || oldAddr > MaxUserSpan)
        {
            return -EINVAL;
        }

        ulong oldLen = (oldSize + 0xfff) & ~0xfffUL, newLen = (newSize + 0xfff) & ~0xfffUL;

        if (!space.IsMapped(oldAddr, oldLen))
        {
            return -EFAULT;
        }

        if ((flags & (MremapFixed | MremapDontunmap)) != 0)
        {
            return -ENOMEM; // Abweichung: Verschieben an eine feste Adresse fehlt
        }

        if (newLen == oldLen)
        {
            return unchecked((long)oldAddr);
        }

        if (newLen < oldLen)
        {
            return p.Unmap(oldAddr + newLen, oldLen - newLen) ? unchecked((long)oldAddr) : -ENOMEM;
        }

        return -ENOMEM; // Vergrößern (auch mit MAYMOVE): Abweichung, siehe oben
    }

    /// <summary>
    /// Grenzen nur lesen: RLIMIT_STACK ist der Hauptstack (musl meldet sonst für den Hauptthread nur die erste Größe, und Ladybirds
    /// AK::StackInfo rechnet mit RLIMIT_STACK), NOFILE 1024 (Deskriptortabelle), CORE 0, alle übrigen unbegrenzt.
    /// Setzen auf den geltenden Wert (Soft = Hard = Wert, so setzen Programme „Soft auf Hard“) gelingt ohne Wirkung; jeder andere Wert
    /// liefert -EPERM (Abweichung: Linux erlaubt Senken). Wie Linux (kernel/sys.c, prlimit64) wird old nur bei Erfolg geschrieben.
    /// </summary>
    private static long DoPrlimit(ILinuxProcess p, long pid, int resource, ulong newPtr, ulong oldPtr)
    {
        if (pid != 0 && pid != p.Pid)
        {
            return -ESRCH;
        }

        if ((uint)resource >= RlimitCount)
        {
            return -EINVAL;
        }

        ulong value = resource switch
        {
            RlimitStack => p is ILinuxAddressSpace space ? space.StackSize : DefaultStackSize,
            RlimitNofile => 1024,
            RlimitCore => 0,
            _ => RlimInfinity,
        };

        if (newPtr != 0)
        {
            Span<byte> wanted = stackalloc byte[16];
            if (!p.Memory.TryRead(newPtr, wanted))
            {
                return -EFAULT;
            }

            ulong cur = BitConverter.ToUInt64(wanted), max = BitConverter.ToUInt64(wanted[8..]);
            if (cur > max)
            {
                return -EINVAL;
            }

            if (cur != value || max != value)
            {
                return -EPERM;
            }
        }

        if (oldPtr != 0)
        {
            Span<byte> limit = stackalloc byte[16];
            BitConverter.TryWriteBytes(limit, value);
            BitConverter.TryWriteBytes(limit[8..], value);
            if (!p.Memory.TryWrite(oldPtr, limit))
            {
                return -EFAULT;
            }
        }

        return 0;
    }

    /// <summary>Eine CPU (der Prozess hat einen Thread); Rückgabe wie Linux die geschriebene Länge (8 Bytes).</summary>
    private static long DoSchedGetaffinity(ILinuxProcess p, long pid, ulong len, ulong maskPtr)
    {
        if (pid != 0 && pid != p.Pid)
        {
            return -ESRCH;
        }

        if (len < 8 || (len & 7) != 0)
        {
            return -EINVAL;
        }

        Span<byte> mask = stackalloc byte[8];
        mask[0] = 1;
        return p.Memory.TryWrite(maskPtr, mask) ? 8 : -EFAULT;
    }

    /// <summary>
    /// PR_SET_NAME (musl pthread_setname_np für den eigenen Thread) prüft nur den Zeiger und wirkt nicht; PR_GET_NAME liefert
    /// einen leeren Namen (Abweichung). Alles andere -EINVAL wie ein Linux ohne die Funktion (mimalloc: PR_SET_VMA).
    /// </summary>
    private static long DoPrctl(ILinuxProcess p, int option, ulong arg)
    {
        Span<byte> name = stackalloc byte[16];
        switch (option)
        {
            case PrSetName:
                return p.Memory.TryRead(arg, name[..1]) ? 0 : -EFAULT;
            case PrGetName:
                name.Clear();
                return p.Memory.TryWrite(arg, name) ? 0 : -EFAULT;
            default:
                return -EINVAL;
        }
    }
}
