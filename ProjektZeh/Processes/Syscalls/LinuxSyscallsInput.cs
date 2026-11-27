using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Ein Deskriptor, von dem gelesen werden kann, ohne Datei zu sein (Ladybird V3: Eingabe aus dem Terminal auf fd 0).
/// Lesen blockiert wie an einer Linux-Pipe: bis wenigstens ein Byte da ist, bis zum Eingabeende oder bis zum Abbruch des
/// Programms. Dateien (<see cref="ILinuxFile"/>, V2 Dateien) lesen über ihre eigene Schnittstelle.
/// </summary>
internal interface ILinuxInput : ILinuxDescriptor
{
    /// <summary>Liest höchstens <c>dst.Length</c> Bytes; 0 am Eingabeende (Strg+D), -EINTR beim Abbruch des Programms.</summary>
    long ReadInput(Span<byte> dst);

    /// <summary>true, wenn <see cref="ReadInput"/> nicht warten würde (Bytes, Eingabeende oder Abbruch).</summary>
    bool InputReady { get; }

    /// <summary>Wartet höchstens <paramref name="timeoutMs"/> (negativ: ohne Frist) auf <see cref="InputReady"/>; liefert es zurück.</summary>
    bool WaitInput(long timeoutMs);
}

internal static partial class LinuxSyscalls
{
    public const long Read = 0, Readv = 19;

    public const long EINTR = 4;

    private static long DoReadInput(ILinuxProcess p, int fd, ulong buf, ulong count)
    {
        var d = p.GetDescriptor(fd);
        if (d is not ILinuxInput input)
        {
            // Nicht zum Lesen offen (Linux: EBADF), etwa ein Deskriptor, der nur schreibt.
            return -EBADF;
        }

        if (count == 0)
        {
            // V20: eventfd und inotify prüfen die Länge selbst (Linux: unter 8 bzw. 16 Bytes -EINVAL, auch 0).
            return d is ILinuxAnonFile and ILinuxPollable anon ? anon.ReadAs([], p as ILinuxAbort) : 0;
        }

        // Erst prüfen, ob der Puffer beschreibbar ist, dann warten: sonst ginge eine Eingabezeile an einen ungültigen Zeiger verloren.
        // Nur prüfen, nicht schreiben: Bei -EAGAIN, -EINTR oder EOF bleibt der Puffer wie bei Linux unverändert (V13-Review P2-2).
        int n = (int)Math.Min(count, (ulong)Chunk);
        if (buf + (ulong)n < buf || !p.Memory.CanWrite(buf, (ulong)n))
        {
            return -EFAULT;
        }

        return ReadToUser(p, input, [buf], [(ulong)n]);
    }

    private static long DoReadvInput(ILinuxProcess p, int fd, ulong iov, ulong cnt)
    {
        var d = p.GetDescriptor(fd);
        if (d is not ILinuxInput input)
        {
            return -EBADF;
        }

        if (cnt > MaxIov)
        {
            return -EINVAL;
        }

        // musl liest stdin mit readv (Benutzerpuffer, dann der eigene Puffer von FILE); ein einziges Lesen verteilt auf die Einträge.
        var bases = new ulong[cnt];
        var lens = new ulong[cnt];
        ulong total = 0;
        Span<byte> entry = stackalloc byte[16];
        for (ulong i = 0; i < cnt; i++)
        {
            if (iov + i * 16 < iov || !p.Memory.TryRead(iov + i * 16, entry))
            {
                return -EFAULT;
            }

            bases[i] = BitConverter.ToUInt64(entry.Slice(0, 8));
            lens[i] = BitConverter.ToUInt64(entry.Slice(8, 8));
            if (lens[i] > (ulong)MaxRw || total + lens[i] > (ulong)MaxRw)
            {
                return -EINVAL;
            }

            total += lens[i];
        }

        if (total == 0)
        {
            return 0;
        }

        // Wie bei read: nur Einträge bis Chunk Bytes, und die müssen beschreibbar sein, bevor gewartet wird.
        ulong room = Math.Min(total, (ulong)Chunk);
        ulong left = room;
        // Ohne Abbruch bei left == 0: Restliche Eintraege werden 0, sonst behielten sie ihre OriginalLaenge (bis MaxRw) und
        // ReadToUser summierte sie zu einem Riesenpuffer und schriebe in ungepruefte Bereiche.
        for (ulong i = 0; i < cnt; i++)
        {
            ulong take = Math.Min(lens[i], left);
            lens[i] = take;
            if (take > 0 && (bases[i] + take < bases[i] || !p.Memory.CanWrite(bases[i], take)))
            {
                return -EFAULT;
            }

            left -= take;
        }

        return ReadToUser(p, input, bases, lens);
    }

    /// <summary>Ein Lesen aus der Eingabe, verteilt auf die Zielbereiche (deren Länge zusammen höchstens <see cref="Chunk"/> ist).</summary>
    private static long ReadToUser(ILinuxProcess p, ILinuxInput input, ulong[] bases, ulong[] lens)
    {
        ulong room = 0;
        foreach (ulong len in lens)
        {
            room += len;
        }

        byte[] tmp = new byte[(int)room];
        // V16: Pipes und Unix-Sockets warten mit dem Abbruch des lesenden Prozesses, nicht des Erzeugers (vfork, SCM_RIGHTS).
        long got = input is ILinuxPollable pollable ? pollable.ReadAs(tmp, p as ILinuxAbort) : input.ReadInput(tmp);
        if (got <= 0)
        {
            return got;
        }

        long at = 0;
        for (int i = 0; i < bases.Length && at < got; i++)
        {
            int n = (int)Math.Min(lens[i], (ulong)(got - at));
            if (n == 0)
            {
                continue;
            }

            // Der Bereich war vor dem Warten beschreibbar; ein anderer Thread desselben Prozesses gibt es (V3) nicht.
            if (!p.Memory.TryWrite(bases[i], tmp.AsSpan((int)at, n)))
            {
                return at > 0 ? at : -EFAULT;
            }

            at += n;
        }

        return at;
    }
}
