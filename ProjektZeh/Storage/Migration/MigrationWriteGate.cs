using System;
using System.Threading;

namespace ProjektZeh.Storage.Migration;

/// <summary>Schliesst laufende Quellschreiber und eine Migration gegenseitig aus, ohne einen Aufrufer warten zu lassen.</summary>
internal sealed class MigrationWriteGate
{
    private readonly object gate = new();
    private int writers;
    private bool migrating;

    public bool IsLocked { get { lock (gate) return migrating; } }

    public IDisposable? TryBeginWrite()
    {
        lock (gate)
        {
            if (migrating) return null;
            writers++;
            return new Lease(this, migration: false);
        }
    }

    public IDisposable? TryLockSource()
    {
        lock (gate)
        {
            if (migrating || writers != 0) return null;
            migrating = true;
            return new Lease(this, migration: true);
        }
    }

    private void Release(bool migration)
    {
        lock (gate)
        {
            if (migration) migrating = false;
            else writers--;
        }
    }

    private sealed class Lease(MigrationWriteGate owner, bool migration) : IDisposable
    {
        private MigrationWriteGate? current = owner;

        // Ein zweites Dispose darf weder einen fremden Schreiber freigeben noch dessen Zaehler vermindern.
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release(migration);
    }
}
