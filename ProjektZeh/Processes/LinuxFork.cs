using System;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes;

/// <summary>
/// V21: fork, clone ohne CLONE_VM und clone3 wie fork. Das Kind ist ein eigener Prozess mit einer Kopie des Adressraums: dieselbe Karte,
/// je privater Seite mit Frame ein neuer Frame mit demselben Inhalt, geteilte Bereiche (MAP_SHARED einer memfd oder anonym) zeigen weiter
/// auf dieselben Frames der Datei. Deskriptoren werden wie bei vfork geerbt (Zählung in <see cref="LinuxOpenFiles"/>), Handler, Maske
/// und Alternativstack bleiben, anhängige Signale nicht. Nur der aufrufende Thread lebt im Kind weiter (Linux: ebenso), als Hauptthread
/// mit TID = PID des Kinds.
/// <para>
/// Abkürzung (shortcut): Kopiert wird sofort und vollständig, ohne Copy-on-Write. Das Ergebnis ist für das Programm dasselbe (privat getrennt,
/// geteilt sichtbar), kostet aber Zeit und Speicher proportional zum belegten Speicher des Elternprozesses statt zu den später geschriebenen
/// Seiten, und scheitert mit -ENOMEM, sobald der Rechner keine Frames mehr für die ganze Kopie hat, wo Linux noch forken könnte (die
/// Obergrenze je Prozess erbt das Kind und füllt sie mit der Kopie genau wie die Eltern). COW brauchte geteilte, schreibgeschützte Frames mit
/// Zählung und einen Seitenfehlerweg für Schreibzugriffe auf vorhandene Seiten; das folgt, wenn große Prozesse (Ladybird) forken.
/// </para>
/// </summary>
internal sealed partial class LinuxUserProcess
{
    // Was fork ohne CLONE_VM annimmt: TLS und TID-Zeiger wie bei Threads (glibc fork: CLONE_CHILD_SETTID|CLONE_CHILD_CLEARTID|SIGCHLD).
    // Geteilte Dateitabelle, Handler oder Arbeitsverzeichnis ohne geteilten Speicher (CLONE_FILES, CLONE_FS, CLONE_SIGHAND), CLONE_PARENT,
    // CLONE_PIDFD und Namensräume gibt es nicht; sie bleiben -ENOSYS, statt halb zu gelten.
    private const ulong CloneForkFlags = CloneSettls | CloneParentSettid | CloneChildCleartid | CloneChildSettid | 0xff;
    private const ulong ExitSignalChld = 17;

    /// <summary>
    /// Kernel: startet <paramref name="child"/> (eigene Seitentabelle, schon kopiert) auf einem eigenen Kernelthread mit den Registern und
    /// dem FPU-Zustand des aufrufenden Threads, rax 0, und dem Stack aus clone (0: derselbe). false ohne Mittel; dann räumt fork auf.
    /// </summary>
    public Func<LinuxUserProcess, ulong, bool>? StartFork { get; set; }

    private long CloneForkProcess(ulong flags, ulong stack, ulong parentTid, ulong childTid, ulong tls)
    {
        // Nur SIGCHLD als Austrittssignal: wait4 ohne __WCLONE sähe ein Kind mit anderem Signal bei Linux nicht; das gibt es hier nicht.
        if ((flags & ~CloneForkFlags) != 0 || (flags & 0xff) != ExitSignalChld)
        {
            return -LinuxSyscalls.ENOSYS;
        }

        if (InVfork || StartFork == null || CreatePages == null)
        {
            return -LinuxSyscalls.ENOSYS;
        }

        IUserPages? pages = CreatePages();
        if (pages == null)
        {
            return -LinuxSyscalls.ENOMEM;
        }

        LinuxThread me = CurrentThread;
        var child = new LinuxUserProcess(pages, _terminal, _ids.Allocate(), MaxBytes, _host, FileSystem, _openFiles, _ids)
        {
            HwCap = HwCap,
            ExePath = ExePath,
        };
        child.Parent = this;
        child._uid = _uid;
        child._gid = _gid;
        child.MainThread.FsBase = (flags & CloneSettls) != 0 ? tls : me.FsBase;
        child.MainThread.ClearChildTid = (flags & CloneChildCleartid) != 0 ? childTid : 0;

        // Vor dem Eintrag in die Kinder: Scheitert die Kopie, hat niemand das Kind gesehen; seine PID wird hier frei, nicht beim Ernten.
        if (!CopyAddressSpaceTo(child))
        {
            child._pidPassed = true;
            child.Destroy();
            _ids.Remove(child.Pid);
            return -LinuxSyscalls.ENOMEM;
        }

        CopySignals(Signals, child.Signals, resetHandlers: false);
        child.InheritFiles(this);

        // Linux schreibt die TID vor dem ersten Lauf: CLONE_CHILD_SETTID in den Speicher des Kinds, CLONE_PARENT_SETTID in den der Eltern
        // (nach der Kopie, das Kind sieht den Wert dort nicht). Fehler beim Schreiben übergeht Linux.
        Span<byte> tid = stackalloc byte[4];
        BitConverter.TryWriteBytes(tid, child.Pid);
        if ((flags & CloneChildSettid) != 0)
        {
            child.TryWrite(childTid, tid);
        }

        if ((flags & CloneParentSettid) != 0)
        {
            TryWrite(parentTid, tid);
        }

        var entry = new LinuxChild(child.Pid, child);
        lock (_children)
        {
            _children.Add(entry);
        }

        if (!StartFork(child, stack))
        {
            lock (_children)
            {
                _children.Remove(entry);
            }

            child._pidPassed = true;
            child.Destroy();
            _ids.Remove(child.Pid);
            return -LinuxSyscalls.EAGAIN;
        }

        return child.Pid;
    }

    /// <summary>
    /// Kopiert Karte, brk, Stackgröße, geteilte Bereiche und den Inhalt jeder privaten Seite mit Frame in den leeren Prozess
    /// <paramref name="child"/>. Unter <c>_memGate</c> der Eltern: Andere Threads ändern Karte und Frames nicht mitten in der Kopie; ihre
    /// Schreibzugriffe aus dem Ring 3 können wie bei Linux vor oder nach dem Schnitt liegen. Darin nur die Sperre der Datei (AddSplit) und
    /// die der Seitentabelle des Kinds, wie in der Reihenfolge an <c>_memGate</c>. false ohne Speicher; dann räumt Destroy des Kinds auf.
    /// </summary>
    private bool CopyAddressSpaceTo(LinuxUserProcess child)
    {
        lock (_memGate)
        {
            child._map.CopyFrom(_map);
            child._brkStart = _brkStart;
            child._brk = _brk;
            child._stackSize = _stackSize;
            child._stackTop = _stackTop;

            // Geteilte Bereiche: dieselbe Datei, ein Verweis mehr; Seiten holt sich das Kind beim ersten Zugriff aus der Datei (PopulateShared).
            foreach (SharedRegion r in _regions)
            {
                r.Mem.AddSplit(r.Writable);
                child._regions.Add(new SharedRegion(r.Mem, r.Start, r.End, r.FirstIndex, r.Writable));
            }

            foreach (System.Collections.Generic.KeyValuePair<ulong, ulong> held in _frames)
            {
                if (_shared.ContainsKey(held.Key))
                {
                    continue;
                }

                ulong frame = child.NewFrame();
                if (frame == 0)
                {
                    return false;
                }

                _pages.Frame(held.Value).CopyTo(child._pages.Frame(frame));
                child._frames[held.Key] = frame;

                // Sofort abbilden spart je Seite einen Fehler; lehnt die Tabelle ab oder ist die Seite PROT_NONE, holt HandlePageFault es
                // mit dem Zugriff aus der Karte nach (Frame vorhanden, Seite nicht abgebildet).
                if (_map.TryFind(held.Key, out MapAccess access) && access != MapAccess.None)
                {
                    _ = child._pages.Map(held.Key, frame, access);
                }
            }

            return true;
        }
    }
}
