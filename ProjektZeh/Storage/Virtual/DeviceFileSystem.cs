using System;
using System.Collections.Generic;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Virtual;

/// <summary>
/// Reine Knotenlogik fuer /dev/null und /dev/zero (Etappe 6a); keine VFS-Anbindung, keine Aktivierung.
/// Jede Oeffnung ergibt ein eigenes Handle ohne gemeinsamen Zustand. Die Dateiposition fuehrt das VFS (VfsFileHandle), nicht der Knoten.
/// </summary>
internal static class DeviceFileSystem
{
    /// <summary>Die einzigen Geraete dieser Etappe, sortiert; Terminal- und Blockgeraete brauchen erst eigene Vertraege.</summary>
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[] { "null", "zero" });

    public static ResultObject<DeviceHandle> Open(string name) => name switch
    {
        "null" => ResultObjectFactory.CreateSuccess(new DeviceHandle(isZero: false)),
        "zero" => ResultObjectFactory.CreateSuccess(new DeviceHandle(isZero: true)),
        _ => ResultObjectFactory.CreateFailure<DeviceHandle>(ResultCode.NotFound, "Unbekanntes Geraet."),
    };
}

/// <remarks>
/// TODO E6b (nicht Teil von 6a): Oeffnungsmodus (/dev/null nur lesend geoeffnet: Write = EBADF wie unter Linux), /dev/zero liefert nie EOF
/// (Aufrufer wie copy, Hash oder fd_read brauchen ein eigenes Limit), ACL Read/Write fuer "*" ist noch zu entscheiden.
/// </remarks>
internal sealed class DeviceHandle
{
    /// <summary>Hoechstens so viele Bytes je Read auf /dev/zero; ein groesserer Puffer bekommt einen kurzen Read (Rest folgt im naechsten).</summary>
    public const int MaxReadBytes = 64 * 1024;

    private readonly bool isZero;

    internal DeviceHandle(bool isZero) => this.isZero = isZero;

    public bool IsClosed { get; private set; }

    /// <summary>/dev/null: sofort EOF (0). /dev/zero: fuellt den Puffer bis <see cref="MaxReadBytes"/>; bei Laenge 0 ergibt das 0.</summary>
    public ResultObject<int> Read(Span<byte> buffer)
    {
        if (IsClosed) return Closed();
        if (!isZero) return ResultObjectFactory.CreateSuccess(0);
        int count = Math.Min(buffer.Length, MaxReadBytes);
        buffer[..count].Clear();
        return ResultObjectFactory.CreateSuccess(count);
    }

    /// <summary>Beide Geraete verwerfen die Daten und melden die ganze Laenge als geschrieben (wie unter Linux).</summary>
    public ResultObject<int> Write(ReadOnlySpan<byte> data) =>
        IsClosed ? Closed() : ResultObjectFactory.CreateSuccess(data.Length);

    /// <summary>Wiederholtes Schliessen ist harmlos; andere Handles bleiben unberuehrt.</summary>
    public void Close() => IsClosed = true;

    private static ResultObject<int> Closed() => ResultObjectFactory.CreateFailure<int>(ResultCode.Failure, "Handle ist geschlossen.");
}
