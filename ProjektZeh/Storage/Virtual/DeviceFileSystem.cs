using System;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Virtual;

/// <summary>
/// Reine Knotenlogik fuer /dev/null und /dev/zero (Etappe 6a); keine VFS-Anbindung, keine Aktivierung.
/// Jede Oeffnung ergibt ein eigenes Handle ohne gemeinsamen Zustand. Die Dateiposition fuehrt das VFS (VfsFileHandle), nicht der Knoten.
/// </summary>
internal static class DeviceFileSystem
{
    /// <summary>Die einzigen Geraete dieser Etappe, sortiert; Terminal- und Blockgeraete brauchen erst eigene Vertraege.</summary>
    public static readonly string[] Names = ["null", "zero"];

    public static ResultObject<DeviceHandle> Open(string name) => name switch
    {
        "null" => ResultObjectFactory.CreateSuccess(new DeviceHandle(isZero: false)),
        "zero" => ResultObjectFactory.CreateSuccess(new DeviceHandle(isZero: true)),
        _ => ResultObjectFactory.CreateFailure<DeviceHandle>(ResultCode.NotFound, "Unbekanntes Geraet."),
    };
}

internal sealed class DeviceHandle
{
    private readonly bool isZero;

    internal DeviceHandle(bool isZero) => this.isZero = isZero;

    public bool IsClosed { get; private set; }

    /// <summary>/dev/null: sofort EOF (0). /dev/zero: fuellt genau den angeforderten Puffer, bei Laenge 0 ergibt das 0.</summary>
    public ResultObject<int> Read(Span<byte> buffer)
    {
        if (IsClosed) return Closed();
        if (!isZero) return ResultObjectFactory.CreateSuccess(0);
        buffer.Clear();
        return ResultObjectFactory.CreateSuccess(buffer.Length);
    }

    /// <summary>Beide Geraete verwerfen die Daten und melden die ganze Laenge als geschrieben (wie unter Linux).</summary>
    public ResultObject<int> Write(ReadOnlySpan<byte> data) =>
        IsClosed ? Closed() : ResultObjectFactory.CreateSuccess(data.Length);

    /// <summary>Wiederholtes Schliessen ist harmlos; andere Handles bleiben unberuehrt.</summary>
    public void Close() => IsClosed = true;

    private static ResultObject<int> Closed() => ResultObjectFactory.CreateFailure<int>(ResultCode.Failure, "Handle ist geschlossen.");
}
