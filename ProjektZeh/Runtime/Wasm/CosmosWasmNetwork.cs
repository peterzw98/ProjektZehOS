#if ZEHOS_WASM || ZEHOS_NETWORK_TEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ProjektZeh.Network;
using ProjektZeh.Update;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>Ein langlebiger Worker kapselt die in Cosmos teilweise blockierenden Netzwerkaufrufe.</summary>
internal sealed class CosmosWasmNetworkConnector : IWasmNetworkConnector
{
    private static readonly object Gate = new();
    private static readonly List<Connection> Connections = [];
    private static Thread? worker;
#if ZEHOS_NETWORK_TEST
    internal static Action? BeforeConnect;
    internal static Action? AfterConnect;
#endif

    public IWasmNetworkConnection Open(string host, int port)
    {
        var connection = new Connection(host, port);
        Exception? failure = null;
        lock (Gate)
        {
            if (Connections.Count >= WasmNetworkSession.MaximumConnections) failure = new WasmNetworkCapacityException();
            else
            {
                Connections.Add(connection);
                if (worker == null)
                {
                    try
                    {
                        worker = new Thread(Work);
#if ZEHOS_NETWORK_TEST
                        worker.IsBackground = true;
#endif
                        worker.Start();
                    }
                    catch (Exception exception)
                    {
                        worker = null;
                        Connections.Remove(connection);
                        failure = exception;
                    }
                }
                Monitor.Pulse(Gate);
            }
        }
        if (failure != null) throw failure;
        return connection;
    }

    private static void Work()
    {
        while (true)
        {
            Connection[] snapshot;
            lock (Gate)
            {
                while (Connections.Count == 0) Monitor.Wait(Gate);
                snapshot = Connections.ToArray();
            }
            foreach (Connection connection in snapshot)
            {
                try { connection.Step(); }
                catch (Exception) { connection.Fail(); }
                if (connection.Finished) lock (Gate) Connections.Remove(connection);
            }
            Thread.Sleep(10);
        }
    }

    private sealed class DnsFailureException(Exception inner) : Exception("DNS-Aufloesung fehlgeschlagen.", inner);

    private sealed class Connection(string host, int port) : IWasmNetworkConnection
    {
        private const int Limit = WasmNetworkSession.MaximumTransferBytes;
        private const int TimeoutMs = 10000;
        private readonly object gate = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<byte> input = new();
        private readonly Queue<byte> output = new();
        private readonly byte[] scratch = new byte[4096];
        private Socket? socket;
        private int error = WasmNetworkError.Again;
        private bool closing;
        private bool finished;
        private bool eof;
        public bool Finished { get { lock (gate) return finished; } }

        public int Status()
        {
            lock (gate)
            {
                if (error == WasmNetworkError.Again && clock.ElapsedMilliseconds >= TimeoutMs)
                {
                    error = WasmNetworkError.TimedOut;
                    closing = true;
                }
                return error;
            }
        }

        public int Send(ReadOnlySpan<byte> data, out int written)
        {
            lock (gate)
            {
                written = 0;
                if (error != WasmNetworkError.Success) return error;
                int count = Math.Min(data.Length, Limit - output.Count);
                if (count == 0 && data.Length != 0) return WasmNetworkError.Again;
                for (int i = 0; i < count; i++) output.Enqueue(data[i]);
                written = count;
                return WasmNetworkError.Success;
            }
        }

        public int Receive(Span<byte> data, out int read)
        {
            lock (gate)
            {
                read = 0;
                while (read < data.Length && input.Count > 0) data[read++] = input.Dequeue();
                if (read > 0 || data.Length == 0 || eof) return WasmNetworkError.Success;
                return error == WasmNetworkError.Success ? WasmNetworkError.Again : error;
            }
        }

        public void Close() { lock (gate) closing = true; }

        public void Fail() { lock (gate) { error = WasmNetworkError.Io; closing = true; } }

        public void Step()
        {
            bool shouldClose;
            lock (gate) shouldClose = closing;
            if (shouldClose) { Finish(); return; }
            if (socket == null) { Connect(); return; }
            if (!Monitor.TryEnter(CosmosNetworkService.Gate)) return;
            Exception? failure = null;
            try { Transfer(); }
            catch (Exception exception) { failure = exception; }
            Monitor.Exit(CosmosNetworkService.Gate);
            if (failure != null) { lock (gate) { error = MapError(failure); closing = true; } Finish(); }
        }

        private void Connect()
        {
            if (!Monitor.TryEnter(CosmosNetworkService.Gate)) return;
            bool cancelled;
            lock (gate) cancelled = closing;
            if (cancelled)
            {
                Monitor.Exit(CosmosNetworkService.Gate);
                Finish();
                return;
            }
            Exception? failure = null;
            Socket? next = null;
            try
            {
                CosmosNetworkService.EnsureAddress();
                string address;
                try { address = CosmosNetworkService.Resolve(host, CancellationToken.None).ToString()!; }
                catch (Exception exception) { throw new DnsFailureException(exception); }
                lock (gate)
                {
                    if (clock.ElapsedMilliseconds >= TimeoutMs) { error = WasmNetworkError.TimedOut; closing = true; }
                    cancelled = closing;
                }
                if (!cancelled)
                {
                    CosmosHttpUpdateSource.RandomizeTcpStart();
                    next = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
#if ZEHOS_NETWORK_TEST
                    BeforeConnect?.Invoke();
#endif
                    lock (gate) cancelled = closing;
                    if (!cancelled)
                    {
                        next.Connect(IPAddress.Parse(address), port);
#if ZEHOS_NETWORK_TEST
                        AfterConnect?.Invoke();
#endif
                    }
                    else { next.Close(0); next = null; }
                }
            }
            catch (Exception exception) { failure = exception; }
            if (failure != null && next != null)
            {
                try { next.Close(0); }
                catch (Exception) { failure = new InvalidOperationException("TCP-Bereinigung fehlgeschlagen.", failure); }
            }
            Monitor.Exit(CosmosNetworkService.Gate);
            if (failure != null)
            {
                lock (gate) error = MapError(failure);
                Finish();
                return;
            }
            if (cancelled) { Finish(); return; }
            socket = next;
            lock (gate)
            {
                if (clock.ElapsedMilliseconds >= TimeoutMs) { error = WasmNetworkError.TimedOut; closing = true; }
                else error = WasmNetworkError.Success;
            }
        }

        private void Transfer()
        {
            Socket current = socket!;
            int count = 0;
            lock (gate)
            {
                foreach (byte value in output)
                {
                    scratch[count++] = value;
                    if (count == scratch.Length) break;
                }
            }
            if (count > 0 && current.Poll(0, SelectMode.SelectWrite))
            {
                int sent = current.Send(scratch, 0, count, SocketFlags.None);
                if (sent <= 0) throw new SocketException();
                lock (gate) for (int i = 0; i < sent; i++) output.Dequeue();
            }
            lock (gate) count = eof ? 0 : Math.Min(scratch.Length, Limit - input.Count);
            if (count > 0 && current.Poll(0, SelectMode.SelectRead))
            {
                int received = current.Receive(scratch, 0, count, SocketFlags.None);
                lock (gate)
                {
                    if (received == 0) eof = true;
                    for (int i = 0; i < received; i++) input.Enqueue(scratch[i]);
                }
            }
        }

        private void Finish()
        {
            if (socket != null)
            {
                if (!Monitor.TryEnter(CosmosNetworkService.Gate)) return;
                try { socket.Close(0); } catch (Exception) { lock (gate) error = WasmNetworkError.Io; }
                Monitor.Exit(CosmosNetworkService.Gate);
                socket = null;
            }
            lock (gate) finished = true;
        }

        // Cosmos 3.0.94 meldet einen gescheiterten Connect nur als allgemeine Exception und einen Abbruch durch RST wie ein
        // Verbindungsende; die eigenen Werte entstehen deshalb nur mit Socketfehlern des Hosts, im Kernel bleibt es bei EIO.
        private static int MapError(Exception exception) => exception is DnsFailureException ? WasmNetworkError.NameNotFound : exception is SocketException socketError ? socketError.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => WasmNetworkError.ConnectionRefused,
            SocketError.ConnectionReset => WasmNetworkError.ConnectionReset,
            SocketError.HostUnreachable => WasmNetworkError.HostUnreachable,
            SocketError.TimedOut => WasmNetworkError.TimedOut,
            _ => WasmNetworkError.Io
        } : WasmNetworkError.Io;
    }
}
#endif
