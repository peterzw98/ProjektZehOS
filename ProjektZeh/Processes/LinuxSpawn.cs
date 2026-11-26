using System;
using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;

namespace ProjektZeh.Processes;

/// <summary>
/// V15: gemeinsame Vergabe von PIDs und TIDs aller Prozesse eines Laufs. Linux führt beide in einem Namensraum; eine TID darf mit
/// keiner PID eines anderen Prozesses zusammenfallen (kill, tgkill, wait4 und futex-Eigentümer meinen sonst den falschen).
/// Sperrreihenfolge: zuunterst, darin keine andere Sperre; <c>_threadGate</c> eines Prozesses darf davor gehalten werden.
/// </summary>
internal sealed class LinuxIds
{
    private readonly object _gate = new();
    private readonly HashSet<int> _used = new();
    private int _last;

    /// <param name="start">Die erste Vergabe liefert die nächste freie Nummer nach <paramref name="start"/> (wie bisher PID + 1).</param>
    public LinuxIds(int start) => _last = start;

    public void Add(int id)
    {
        lock (_gate)
        {
            _used.Add(id);
        }
    }

    public void Remove(int id)
    {
        lock (_gate)
        {
            _used.Remove(id);
        }
    }

    public bool InUse(int id)
    {
        lock (_gate)
        {
            return _used.Contains(id);
        }
    }

    /// <summary>Nächste freie Nummer (mit Umlauf nach <see cref="LinuxUserProcess.TidMax"/>), gleich als belegt eingetragen.</summary>
    public int Allocate()
    {
        lock (_gate)
        {
            _last = LinuxUserProcess.NextTid(_last, 0, _used.Contains);
            _used.Add(_last);
            return _last;
        }
    }
}

/// <summary>
/// V15: ein Kindprozess aus Sicht des Elternprozesses (für wait4, waitid, getppid). <see cref="Process"/> wechselt bei execve auf das
/// neue Abbild; die PID bleibt. Geschrieben unter <see cref="LinuxUserProcess.ChildGate"/>.
/// </summary>
internal sealed class LinuxChild
{
    public LinuxChild(int pid, LinuxUserProcess process)
    {
        Pid = pid;
        Process = process;
    }

    public int Pid { get; }

    public LinuxUserProcess Process { get; internal set; }

    /// <summary>Wartestatus wie Linux (<c>exit n</c>: n &lt;&lt; 8, Signal: Nummer), null, solange das Kind läuft.</summary>
    public int? WaitStatus { get; internal set; }
}

/// <summary>Optional neben <see cref="ILinuxProcess"/> (V15): execve. Ohne sie ist execve -ENOSYS.</summary>
internal interface ILinuxSpawn
{
    /// <summary>
    /// Lädt <paramref name="path"/> (absolut im Prozess, aufgelöst) in einen neuen Adressraum. Liefert -errno, solange das alte Abbild
    /// weiterläuft; 0 heißt: Das neue Abbild läuft, der aufrufende Thread kehrt nicht in das alte Programm zurück.
    /// </summary>
    long Exec(string path, string[] argv, string[] envp);
}

/// <summary>
/// V15 "Prozess startet Prozess" (Ladybird startet WebContent mit posix_spawn; musl: <c>clone(CLONE_VM|CLONE_VFORK|SIGCHLD)</c>,
/// dann execve im Kind). Kein fork mit eigenem Speicher: Das vfork-Kind ist ein eigener Prozess (PID, Deskriptortabelle, Signalhandler,
/// Arbeitsverzeichnis) auf dem Speicher des Elternprozesses, bis es execve oder exit ruft; so lange ist der aufrufende Thread des
/// Elternprozesses blockiert (im Kernel läuft das Kind auf genau diesem Kernelthread). execve lädt das Programm in einen neuen Adressraum
/// (eigene Seitentabelle) und gibt den Elternthread frei.
/// </summary>
internal sealed partial class LinuxUserProcess
{
    /// <summary>Größte Datei, die execve lädt (wie <c>CosmosLinuxRun.MaxFileSize</c>).</summary>
    public const int MaxExecFileSize = 128 << 20;

    private const int ORdOnly = 0;
    private const long E2Big = 7, ENoExec = 8;

    // Speicherbesitzer im vfork-Kind (bis execve oder exit); null in jedem Prozess mit eigenem Adressraum.
    private readonly LinuxUserProcess? _vforkOf;
    private readonly LinuxIds _ids;

    // Kinder dieses Prozesses; die Liste ist zugleich die Sperre (ChildGate), zuunterst in der Reihenfolge wie _threadGate.
    private readonly List<LinuxChild> _children = new();

    /// <summary>vfork-Kind: eigener Prozess auf dem Speicher von <paramref name="parent"/> (V15).</summary>
    private LinuxUserProcess(LinuxUserProcess parent)
        : this(parent._pages, parent._terminal, parent._ids.Allocate(), parent._maxFrames * Page, parent._host, parent.FileSystem, parent._fileGate,
            parent._ids)
    {
        _vforkOf = parent;
        Parent = parent;
        _uid = parent._uid;
        _gid = parent._gid;
        ExePath = parent.ExePath;
        HwCap = parent.HwCap;
        CopySignals(parent.Signals, Signals, resetHandlers: false);
        InheritFiles(parent);
    }

    /// <summary>Elternprozess (getppid); null für den ersten Prozess eines Laufs.</summary>
    public LinuxUserProcess? Parent { get; private set; }

    /// <summary>True im vfork-Kind vor execve oder exit: Es teilt den Speicher des Elternprozesses.</summary>
    public bool InVfork => _vforkOf != null;

    /// <summary>Sperre für <see cref="Children"/> und <see cref="LinuxChild.WaitStatus"/>; wer wartet, nutzt Monitor.Wait auf ihr.</summary>
    public object ChildGate => _children;

    /// <summary>Schnappschuss der Kinder (laufend und beendet, bis wait4 sie austrägt).</summary>
    public LinuxChild[] Children
    {
        get
        {
            lock (_children)
            {
                return _children.ToArray();
            }
        }
    }

    /// <summary>AT_HWCAP für execve (Kernel: CPUID 1 EDX); erbt jedes Kind.</summary>
    public uint HwCap { get; set; }

    /// <summary>Speicherobergrenze in Bytes; erbt das Abbild nach execve.</summary>
    public ulong MaxBytes => _maxFrames * Page;

    /// <summary>Seiten dieses Prozesses (die Brücke des Kernels braucht die Tabelle für den Kernelthread des neuen Abbilds).</summary>
    public IUserPages Pages => _pages;

    /// <summary>
    /// Kernel: der aufrufende Kernelthread läuft ab dem Rücksprung als <paramref name="child"/> weiter, mit dem Stack aus clone (0: der
    /// bisherige) und rax 0; die Register des Elternthreads sichert er bis zum Ende der vfork-Phase. false ohne Mittel.
    /// </summary>
    public Func<LinuxUserProcess, ulong, bool>? StartVfork { get; set; }

    /// <summary>Kernel: neue, leere Seitentabelle für execve; null ohne Speicher.</summary>
    public Func<IUserPages?>? CreatePages { get; set; }

    /// <summary>
    /// Kernel: startet <paramref name="next"/> (nach execve, eigener Adressraum) auf einem eigenen Kernelthread am Einsprung des Abbilds
    /// und gibt den Elternthread frei. false, wenn kein Thread startet; der Elternthread wird trotzdem frei.
    /// </summary>
    public Func<LinuxUserProcess, UserImage, bool>? StartExec { get; set; }

    private long CloneVforkProcess(ulong flags, ulong stack)
    {
        // Nur das Austrittssignal (SIGCHLD) neben CLONE_VM|CLONE_VFORK; geteilte Dateien, Handler oder TLS im neuen Prozess gibt es nicht.
        if ((flags & ~(CloneVm | CloneVfork | 0xffUL)) != 0)
        {
            return -LinuxSyscalls.ENOSYS;
        }

        if (InVfork || StartVfork == null)
        {
            return -LinuxSyscalls.ENOSYS;
        }

        var child = new LinuxUserProcess(this);
        var entry = new LinuxChild(child.Pid, child);
        lock (_children)
        {
            _children.Add(entry);
        }

        if (!StartVfork(child, stack))
        {
            lock (_children)
            {
                _children.Remove(entry);
            }

            child.Release();
            return -LinuxSyscalls.EAGAIN;
        }

        // Der Thread läuft jetzt als Kind weiter (Rückgabe 0); die PID bekommt der Elternthread, wenn die vfork-Phase endet.
        return 0;
    }

    public long Exec(string path, string[] argv, string[] envp)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!InVfork || CreatePages == null || StartExec == null || Parent == null)
        {
            // execve ohne vfork bräuchte den Austausch des Adressraums unter dem laufenden Thread (und das Ende aller anderen Threads).
            return -LinuxSyscalls.ENOSYS;
        }

        long read = ReadExecutable(path, out byte[] elf);
        if (read < 0)
        {
            return read;
        }

        byte[] random = new byte[16];
        if (!TryFillRandom(random))
        {
            return -LinuxSyscalls.EAGAIN;
        }

        UserImage image;
        try
        {
            image = ElfLoader.Load(elf, argv, envp, new ElfLoadOptions { Random = random, HwCap = HwCap, ExecFn = path, Uid = _uid, Gid = _gid });
        }
        catch (ElfLoadException exception)
        {
            return exception.Kind switch
            {
                ElfLoadError.ArgumentsTooLong => -E2Big,
                ElfLoadError.InvalidArgument => -LinuxSyscalls.EINVAL,
                _ => -ENoExec,
            };
        }

        IUserPages? pages = CreatePages();
        if (pages == null)
        {
            return -LinuxSyscalls.ENOMEM;
        }

        var next = new LinuxUserProcess(pages, _terminal, Pid, MaxBytes, _host, FileSystem, _fileGate, _ids)
        {
            HwCap = HwCap,
            ExePath = path,
        };
        if (!next.Load(image, elf).IsOK())
        {
            next.DestroyMemory();
            return -LinuxSyscalls.ENOMEM;
        }

        // Ab hier kein Rückweg (Linux: point of no return): Dateien, Arbeitsverzeichnis und Signalmaske wandern ins neue Abbild.
        next.TakeOver(this);
        Parent.ReplaceChild(Pid, next);
        if (!StartExec(next, image))
        {
            next.ReportEnd(9);
            next.Destroy();
        }

        return 0;
    }

    /// <summary>Liest das Programm über das Dateisystem des Prozesses (nur reguläre Dateien bis <see cref="MaxExecFileSize"/>).</summary>
    private long ReadExecutable(string path, out byte[] elf)
    {
        elf = [];
        lock (_fileGate)
        {
            long r = FileSystem.Open(path, ORdOnly, 0, out ILinuxFile? file);
            if (r < 0 || file == null)
            {
                return r < 0 ? r : -LinuxErrno.ENOENT;
            }

            try
            {
                r = file.Stat(out LinuxStat st);
                if (r < 0)
                {
                    return r;
                }

                if ((st.Mode & LinuxMode.TypeMask) != LinuxMode.Regular)
                {
                    return -LinuxSyscalls.EACCES;
                }

                if (st.Size > MaxExecFileSize || st.Size < 0)
                {
                    return -LinuxSyscalls.ENOMEM;
                }

                byte[] bytes = new byte[st.Size];
                int done = 0;
                while (done < bytes.Length)
                {
                    long n = file.Read(bytes.AsSpan(done));
                    if (n < 0)
                    {
                        return n;
                    }

                    if (n == 0)
                    {
                        break;
                    }

                    done += (int)n;
                }

                elf = done == bytes.Length ? bytes : bytes.AsSpan(0, done).ToArray();
                return 0;
            }
            finally
            {
                _ = file.Close();
            }
        }
    }

    /// <summary>execve: übernimmt Deskriptoren (die Tabelle des vfork-Kinds wird leer), Arbeitsverzeichnis, Eltern und Signalzustand.</summary>
    private void TakeOver(LinuxUserProcess from)
    {
        Parent = from.Parent;
        lock (_fileGate)
        {
            Array.Copy(from._descriptors, _descriptors, MaxDescriptors);
            Array.Clear(from._descriptors);
            _cwd = from._cwd;
        }

        // Linux: Handler fallen auf SIG_DFL, SIG_IGN und die Signalmaske bleiben, der Alternativstack nicht.
        CopySignals(from.Signals, Signals, resetHandlers: true);
        CloseOnExec();
    }

    /// <summary>
    /// fork-Regel für die Deskriptortabelle (V15): eine Kopie der Nummern auf dieselben offenen Dateien, dazu das Arbeitsverzeichnis.
    /// Noch ohne Referenzzählung über Tabellen hinweg; die bringt senior2 (task/lb-v15-wait), bis dahin schließt Destroy eines
    /// vfork-Kinds nichts (siehe <see cref="Release"/>).
    /// </summary>
    private void InheritFiles(LinuxUserProcess parent)
    {
        lock (_fileGate)
        {
            Array.Copy(parent._descriptors, _descriptors, MaxDescriptors);
            _cwd = parent._cwd;
        }
    }

    /// <summary>execve: schließt Nummern mit FD_CLOEXEC. Noch leer (deshalb statisch), FD_CLOEXEC je Nummer führt senior2 ein (task/lb-v15-wait).</summary>
    private static void CloseOnExec()
    {
    }

    private static void CopySignals(LinuxSignalState from, LinuxSignalState to, bool resetHandlers)
    {
        for (int sig = 0; sig < from.Handler.Length; sig++)
        {
            bool keep = !resetHandlers || from.Handler[sig] == 1;
            to.Handler[sig] = keep ? from.Handler[sig] : 0;
            to.Flags[sig] = keep ? from.Flags[sig] : 0;
            to.Restorer[sig] = keep ? from.Restorer[sig] : 0;
            to.Mask[sig] = keep ? from.Mask[sig] : 0;
        }

        to.Blocked = from.Blocked;
        if (!resetHandlers)
        {
            to.AltSp = from.AltSp;
            to.AltSize = from.AltSize;
            to.AltFlags = from.AltFlags;
        }
    }

    /// <summary>
    /// Ende der vfork-Phase ohne eigenes Abbild (exit vor execve oder nach execve): Das Objekt wird nicht mehr gebraucht. Es schließt
    /// keine Dateien: Die Nummern zeigen ohne Referenzzählung auf die offenen Dateien des Elternprozesses (nach execve ist die Tabelle leer).
    /// </summary>
    public void Release()
    {
        if (_vforkOf == null)
        {
            throw new InvalidOperationException("Release nur für vfork-Kinder; ein Prozess mit eigenem Speicher endet mit Destroy.");
        }

        lock (_fileGate)
        {
            Array.Clear(_descriptors);
        }
    }

    /// <summary>Ein Thread endete mit einer Ausnahme im Ring 3 (Kernel): Linux beendet den Prozess mit dem Signal der Ausnahme.</summary>
    public void RecordFault(int signal)
    {
        lock (_threadGate)
        {
            TerminatedBySignal ??= signal;
        }
    }

    /// <summary>Meldet das Ende an den Elternprozess (einmal); weckt Wartende auf <see cref="ChildGate"/>.</summary>
    public void ReportEnd(int waitStatus) => Parent?.ChildEnded(Pid, waitStatus);

    /// <summary>Wartestatus aus dem Ende: Signal (tgkill, Abbruch) oder exit; <paramref name="fallbackSignal"/>, wenn keins von beiden.</summary>
    public int WaitStatusOf(int fallbackSignal) =>
        TerminatedBySignal is int signal ? signal & 0x7f : ExitCode is int code ? (code & 0xff) << 8 : fallbackSignal & 0x7f;

    private void ChildEnded(int pid, int waitStatus)
    {
        lock (_children)
        {
            LinuxChild? child = _children.Find(c => c.Pid == pid);
            if (child != null && child.WaitStatus == null)
            {
                child.WaitStatus = waitStatus;
            }

            Monitor.PulseAll(_children);
        }
    }

    private void ReplaceChild(int pid, LinuxUserProcess next)
    {
        lock (_children)
        {
            LinuxChild? child = _children.Find(c => c.Pid == pid);
            if (child != null)
            {
                child.Process = next;
            }
        }
    }
}
