using System;
using System.Collections.Generic;
using ProjektZeh.Network;

namespace ProjektZeh.Runtime.Wasm;

// Numerische Werte nach WASI Preview 1, damit Gastschnittstellen einheitliche Fehler melden.
internal static class WasmNetworkError
{
    public const int Success = 0;
    public const int Access = 2;
    public const int Again = 6;
    public const int BadHandle = 8;
    public const int ConnectionRefused = 14;
    public const int ConnectionReset = 15;
    public const int HostUnreachable = 23;
    public const int Invalid = 28;
    public const int Io = 29;
    public const int TooManyFiles = 33;
    public const int NameNotFound = 44;
    public const int NotSupported = 58;
    public const int TimedOut = 73;
}

internal sealed class WasmNetworkCapacityException : Exception;

internal interface IWasmNetworkConnection
{
    int Status();
    int Send(ReadOnlySpan<byte> data, out int written);
    int Receive(Span<byte> data, out int read);
    void Close();
}

internal interface IWasmNetworkConnector
{
    IWasmNetworkConnection Open(string host, int port);
}

/// <summary>Verwaltet begrenzte, sitzungsgebundene Netzwerkhandles; Transport und Gastadressen bleiben getrennt.</summary>
internal sealed class WasmNetworkSession(IWasmNetworkConnector connector, bool allowed)
{
    internal const int MaximumConnections = 4;
    internal const int MaximumTransferBytes = 64 * 1024;
    private readonly Dictionary<int, IWasmNetworkConnection> connections = [];
    private int nextHandle;
    private bool closed;

    public int Open(string host, int port, int flags, out int handle)
    {
        handle = 0;
        if (closed || !allowed) return WasmNetworkError.Access;
        if (flags != 0) return WasmNetworkError.NotSupported;
        if (port is < 1 or > 65535 || host == null) return WasmNetworkError.Invalid;
        try { NetworkTarget.ValidateHost(host); }
        catch (ArgumentException) { return WasmNetworkError.Invalid; }
        if (connections.Count >= MaximumConnections || nextHandle == int.MaxValue) return WasmNetworkError.TooManyFiles;
        try
        {
            IWasmNetworkConnection connection = connector.Open(host, port);
            handle = ++nextHandle;
            connections.Add(handle, connection);
            return WasmNetworkError.Success;
        }
        catch (WasmNetworkCapacityException) { return WasmNetworkError.TooManyFiles; }
        catch (Exception) { return WasmNetworkError.Io; }
    }

    public int Status(int handle) => connections.TryGetValue(handle, out var connection) ? connection.Status() : WasmNetworkError.BadHandle;

    public int Send(int handle, ReadOnlySpan<byte> data, out int written)
    {
        written = 0;
        if (!connections.TryGetValue(handle, out var connection)) return WasmNetworkError.BadHandle;
        if (data.Length > MaximumTransferBytes) return WasmNetworkError.Invalid;
        return connection.Send(data, out written);
    }

    public int Receive(int handle, Span<byte> data, out int read)
    {
        read = 0;
        if (!connections.TryGetValue(handle, out var connection)) return WasmNetworkError.BadHandle;
        if (data.Length > MaximumTransferBytes) return WasmNetworkError.Invalid;
        return connection.Receive(data, out read);
    }

    public int Close(int handle)
    {
        if (!connections.Remove(handle, out var connection)) return WasmNetworkError.BadHandle;
        try { connection.Close(); }
        catch (Exception) { return WasmNetworkError.Io; }
        return WasmNetworkError.Success;
    }

    public int CloseAll()
    {
        int result = WasmNetworkError.Success;
        closed = true;
        foreach (var connection in connections.Values)
        {
            try { connection.Close(); }
            catch (Exception) { result = WasmNetworkError.Io; }
        }
        connections.Clear();
        return result;
    }
}
