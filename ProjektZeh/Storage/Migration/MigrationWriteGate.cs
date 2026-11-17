using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ProjektZeh.Storage.Migration;

/// <summary>Gemeinsamer Leasekern; Try-Methoden warten nicht, die bisherige Datentraegersperre kann Schreiber abwarten.</summary>
internal sealed class MigrationWriteGate
{
    internal const string BusyMessage = "cause='StorageBusy' message='Quelllaufwerk ist fuer Schreibzugriffe gesperrt oder beschaeftigt.'";
    private readonly object gate = new();
    private readonly Dictionary<int, int> writers = new();
    private bool migrating;
    private bool blocked;

    public bool IsLocked { get { lock (gate) return migrating || blocked; } }
    public bool IsMigrationLocked { get { lock (gate) return migrating; } }

    public IDisposable? TryBeginWrite()
    {
        lock (gate)
        {
            int thread = Environment.CurrentManagedThreadId;
            writers.TryGetValue(thread, out int nested);
            // Ein wartendes Block darf die noch laufende Transaktion nicht vor ihrer Metadatensicherung abschneiden.
            if (migrating || (blocked && nested == 0)) return null;
            writers[thread] = nested + 1;
            return new Lease(this, migration: false, thread);
        }
    }

    public IDisposable? TryLockSource()
    {
        lock (gate)
        {
            if (migrating || blocked || writers.Count != 0) return null;
            migrating = true;
            return new Lease(this, migration: true, 0);
        }
    }

    public bool TryBlock()
    {
        lock (gate)
        {
            if (migrating || writers.Count != 0) return false;
            blocked = true;
            return true;
        }
    }

    public void Block()
    {
        lock (gate)
        {
            if (migrating) throw new IOException("cause='MigrationBusy' message='Migration sperrt die Quelle.'");
            blocked = true;
            while (writers.Count != 0) Monitor.Wait(gate);
        }
    }

    public void ReleaseBlock()
    {
        lock (gate) blocked = false;
    }

    private void Release(bool migration, int thread)
    {
        lock (gate)
        {
            if (migration) migrating = false;
            else
            {
                int remaining = writers[thread] - 1;
                if (remaining == 0) writers.Remove(thread);
                else writers[thread] = remaining;
            }
            Monitor.PulseAll(gate);
        }
    }

    private sealed class Lease(MigrationWriteGate owner, bool migration, int thread) : IDisposable
    {
        private MigrationWriteGate? current = owner;

        // Ein zweites Dispose darf weder einen fremden Schreiber freigeben noch dessen Zaehler vermindern.
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release(migration, thread);
    }
}
