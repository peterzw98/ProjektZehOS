using System;
using System.IO;

namespace ProjektZeh.Storage.Migration;

/// <summary>
/// Quellschutz der Migration (Etappe 4.5, P3-g, Entscheidung storage-lead2 17.11.): Cosmos 3.0.97 ruft auch fuer nur
/// lesend geoeffnete FAT-Handles beim Release Fsync auf und schreibt dabei Verzeichniseintrag und Flush. Deshalb wird /data
/// fuer die Dauer der Migration unter dem exklusiven Quelllease ausgehaengt und dieselbe Partition ueber den Lese-Wrapper
/// eingehaengt; danach kommt der normale Schreibmount zurueck. Laesst sich der alte Zustand nicht wiederherstellen, bleibt
/// der Lease bewusst gehalten: /data bleibt bis zum Neustart fuer Schreiber gesperrt, nie halb eingehaengt und beschreibbar.
/// Die Schritte kommen als Callbacks, damit jede Fehlerstelle auf dem Host pruefbar ist.
/// </summary>
internal sealed class MigrationSourceSwap(Func<IDisposable?> tryLockSource, Func<string> busyDescription, Func<bool> unmountWritable,
    Func<bool> mountReadOnly, Func<bool> unmountReadOnly, Func<bool> mountWritable, Action<string> log)
{
    /// <summary>Gesetzt, wenn /data nach der Migration nicht wieder normal eingehaengt werden konnte.</summary>
    public string? RestoreFailure { get; private set; }

    /// <summary>Fuer <see cref="LayoutMigration.Run"/>: Fehler werden als IOException gemeldet, die der Ablauf zum Ergebnis macht.</summary>
    public IDisposable Acquire()
    {
        IDisposable lease = tryLockSource()
            ?? throw new IOException($"cause='StorageBusy' message='{busyDescription()} laeuft; /data ist nicht exklusiv sperrbar.'");
        if (!Attempt(unmountWritable, "Aushaengen von /data"))
        {
            lease.Dispose();
            throw new IOException("cause='SourceUnmountFailed' message='/data liess sich nicht aushaengen (offene Dateien?); unveraendert eingehaengt.'");
        }

        if (!Attempt(mountReadOnly, "Lesemount der Quelle"))
        {
            if (Attempt(mountWritable, "Wiedereinhaengen von /data"))
            {
                lease.Dispose();
                throw new IOException("cause='SourceReadOnlyMountFailed' message='Quelle nicht lesend einhaengbar; /data wieder normal eingehaengt.'");
            }

            Fail("/data ist nicht eingehaengt und bleibt bis zum Neustart gesperrt.");
            throw new IOException("cause='SourceRestoreFailed' message='" + RestoreFailure + "'");
        }

        return new Restore(this, lease);
    }

    private void Release(IDisposable lease)
    {
        if (!Attempt(unmountReadOnly, "Aushaengen des Lesemounts"))
        {
            Fail("/data bleibt nur lesend eingehaengt und bis zum Neustart fuer Schreiber gesperrt.");
            return;
        }

        if (!Attempt(mountWritable, "Wiedereinhaengen von /data"))
        {
            Fail("/data ist nicht eingehaengt und bleibt bis zum Neustart gesperrt.");
            return;
        }

        lease.Dispose();
    }

    private void Fail(string message)
    {
        RestoreFailure = message;
        log("Fehler: " + message);
    }

    // Ein Ausnahmefehler eines Mount-Schritts zaehlt wie dessen Ablehnung; die Ursache bleibt im Protokoll.
    private bool Attempt(Func<bool> step, string name)
    {
        try
        {
            return step();
        }
        catch (Exception exception)
        {
            log($"{name}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private sealed class Restore(MigrationSourceSwap owner, IDisposable lease) : IDisposable
    {
        private MigrationSourceSwap? current = owner;

        public void Dispose()
        {
            var swap = current;
            current = null;
            swap?.Release(lease);
        }
    }
}
