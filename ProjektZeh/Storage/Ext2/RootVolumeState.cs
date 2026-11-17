using System;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Storage.Ext2;

/// <summary>Root-Volume fuer <see cref="RootVolumeState"/>; der Cosmos-Adapter rechnet den Partitionsversatz um.</summary>
internal interface IRootVolume
{
    /// <summary>Sync des eingehaengten Dateisystems; false oder Ausnahme gelten als Fehler.</summary>
    bool Sync();
    /// <summary>Liest die 1024 Byte des Superblocks (Byte 1024 der Partition) direkt vom Geraet.</summary>
    void ReadSuperblock(Span<byte> superblock);
    /// <summary>Schreibt den geaenderten Superblock zurueck; der Adapter darf sich auf den Sektor mit s_state beschraenken.</summary>
    void WriteSuperblock(ReadOnlySpan<byte> superblock);
    /// <summary>Device.Flush: Schreibpuffer des Geraets auf die Platte.</summary>
    void Flush();
}

/// <summary>
/// s_state des Root (Spec 7a-S4 §6.4, Entscheidung der Storage-Leitung 17.11.): Open markiert vor Schreibzugriffen
/// "nicht sauber", Close nach zwei Syncs wieder "sauber" und belegt das durch Neulesen. Installer, Energieaktion (4.3) und
/// Updates (7b) nutzen denselben Baustein. Ein gesetztes Fehlerbit wird nie ueberschrieben.
/// Vorbedingung: nie nebenlaeufig zu Dateischreibern auf demselben Root (7b-Spec §3); die Sperre haelt der Aufrufer.
/// Annahme: der Treiber schreibt seinen gepufferten Superblock nicht ueber unser s_state zurueck. Das Neulesen faengt nur
/// einen sofortigen Rueckschreib; Wirksamkeit unter Last belegt erst die E4.0-Probe (Laeufe 3/4), nicht der Hosttest.
/// </summary>
internal static class RootVolumeState
{
    private const int SuperblockSize = 1024;

    public static ResultErr Open(IRootVolume volume) => Run(volume, () =>
    {
        var result = SetState(volume, 0);
        if (!result.IsOK()) return result;
        Do("flush", volume.Flush);
        // Ohne Nachweis saehe ein Absturz nach einem Treiber-Rueckschreib mit s_state 1 sauber aus.
        if (BitConverter.ToUInt16(Read(volume), Ext2FeatureGate.StateOffset) != 0) return Failed("Root nach dem Oeffnen nicht als offen markiert (s_state != 0).");
        return ResultErr.CreateSuccess();
    });

    public static ResultErr Close(IRootVolume volume) => Run(volume, () =>
    {
        // Zweimal wie bei sync; sync: der erste Lauf kann Metadaten erzeugen, die erst der zweite schreibt.
        for (int pass = 0; pass < 2; pass++)
            if (!Do("sync", volume.Sync)) return Failed("sync meldete einen Fehler.");
        ResultErr result;
        try { result = SetState(volume, 1); }
        catch (StepException exception) when (exception.Step == "write")
        {
            return RollBack(volume, Failed($"write fehlgeschlagen: {exception.InnerException!.Message}"));
        }
        if (!result.IsOK()) return result;
        var confirmed = Run(volume, () =>
        {
            Do("flush", volume.Flush);
            // Neulesen belegt wegen des Geraetecaches nur Sichtbarkeit; es faengt aber einen zurueckgeschriebenen alten Superblock.
            byte[] reread = Read(volume);
            if (BitConverter.ToUInt16(reread, Ext2FeatureGate.StateOffset) != 1) return Failed("Root nach dem Schliessen nicht sauber (s_state != 1).");
            if (InstalledIdentity.CheckRoot(reread) != RootCheck.Ok) return Failed("CheckRoot nach dem Schliessen nicht Ok.");
            return ResultErr.CreateSuccess();
        });
        return confirmed.IsOK() ? confirmed : RollBack(volume, confirmed);
    });

    private static ResultErr RollBack(IRootVolume volume, ResultErr failure)
    {
        // Auch ein fehlgeschlagenes Schreiben kann den Zustand bereits geaendert haben.
        var rollback = Run(volume, () =>
        {
            var reset = SetState(volume, 0);
            if (reset.IsOK()) Do("flush", volume.Flush);
            return reset;
        });
        return rollback.IsOK() ? failure : Failed(failure.Message + " Ruecknahme auf s_state 0 fehlgeschlagen: " + rollback.Message);
    }

    private static ResultErr SetState(IRootVolume volume, ushort state)
    {
        byte[] superblock = Read(volume);
        ushort current = BitConverter.ToUInt16(superblock, Ext2FeatureGate.StateOffset);
        if (current > 1) return Failed($"Root traegt das Fehlerbit (s_state {current}); offline e2fsck noetig.");
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2FeatureGate.StateOffset), state);
        Do("write", () => volume.WriteSuperblock(superblock));
        return ResultErr.CreateSuccess();
    }

    private static byte[] Read(IRootVolume volume)
    {
        var superblock = new byte[SuperblockSize];
        Do("read", () => volume.ReadSuperblock(superblock));
        return superblock;
    }

    // Jede Ausnahme behaelt den Schritt im Text, damit Protokoll und Abbruchmeldung die Fehlerstelle nennen.
    private static void Do(string step, Action action)
    {
        try { action(); }
        catch (Exception exception) { throw new StepException(step, exception); }
    }

    private static bool Do(string step, Func<bool> action)
    {
        try { return action(); }
        catch (Exception exception) { throw new StepException(step, exception); }
    }

    private static ResultErr Run(IRootVolume volume, Func<ResultErr> body)
    {
        ArgumentNullException.ThrowIfNull(volume);
        try { return body(); }
        catch (StepException exception) { return Failed($"{exception.Step} fehlgeschlagen: {exception.InnerException!.Message}"); }
    }

    private static ResultErr Failed(string message) => ResultErr.CreateFailure(ResultCode.Failure, message);

    private sealed class StepException(string step, Exception inner) : Exception(step, inner)
    {
        public string Step { get; } = step;
    }
}
