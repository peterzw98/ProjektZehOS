#if ZEHOS_WASM || ZEHOS_NETWORK_TEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Org.BouncyCastle.Tls;
using ProjektZeh.Network;
using ProjektZeh.Update;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>Ein langlebiger Worker kapselt die in Cosmos teilweise blockierenden Netzwerkaufrufe.</summary>
/// <remarks>
/// <paramref name="createTls"/> erzeugt den TLS-Client im App-Thread und meldet fehlende Voraussetzungen als
/// <see cref="WasmNetworkUnavailableException"/>; ohne Fabrik ist TLS nicht verfuegbar.
/// </remarks>
internal sealed class CosmosWasmNetworkConnector(Func<string, TlsClient>? createTls = null) : IWasmNetworkConnector
{
    private static readonly object Gate = new();
    private static readonly List<Connection> Connections = [];
    private static Thread? worker;
#if ZEHOS_NETWORK_TEST
    internal static Action? BeforeConnect;
    internal static Action? AfterConnect;
#endif

    public IWasmNetworkConnection Open(string host, int port, bool tls)
    {
        TlsClient? client = null;
        if (tls)
        {
            if (createTls == null) throw new WasmNetworkUnavailableException(new NotSupportedException("TLS fehlt."));
            client = createTls(host);
        }
        var connection = new Connection(host, port, client);
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

    // Mit TLS laufen Handshake und Entschluesselung nur im Worker und ausserhalb beider Sperren; unter
    // CosmosNetworkService.Gate stehen ausschliesslich Socketaufrufe, damit die Shell nicht auf einen Handshake wartet.
    private sealed class Connection(string host, int port, TlsClient? tlsClient) : IWasmNetworkConnection
    {
        private const int Limit = WasmNetworkSession.MaximumTransferBytes;
        private const int TimeoutMs = 10000;
        private readonly object gate = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<byte> input = new();
        private readonly Queue<byte> output = new();
        private readonly byte[] scratch = new byte[4096];
        private readonly byte[] cipherOut = new byte[4096];
        private readonly byte[] cipherIn = new byte[4096];
        private int cipherOutCount;
        private int cipherInCount;
        private bool socketEof;
        private TlsClientProtocol? tls;
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
            Exception? failure = null;
            if (tls != null)
            {
                try { Encrypt(tls); }
                catch (Exception exception) { failure = exception; }
            }
            if (failure == null)
            {
                if (!Monitor.TryEnter(CosmosNetworkService.Gate)) return;
                try
                {
                    if (tls == null) Transfer();
                    else TransferCipher(tls);
                }
                catch (Exception exception) { failure = exception; }
                Monitor.Exit(CosmosNetworkService.Gate);
            }
            if (failure == null && tls != null)
            {
                try { Decrypt(tls); }
                catch (Exception exception) { failure = exception; }
            }
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
            if (tlsClient != null)
            {
                // ClientHello entsteht hier ausserhalb der Sperren; der Status bleibt EAGAIN bis zum Handshake-Ende.
                try
                {
                    tls = new TlsClientProtocol();
                    tls.Connect(tlsClient);
                }
                catch (Exception exception)
                {
                    tls = null;
                    lock (gate) { error = MapError(exception); closing = true; }
                    Finish();
                    return;
                }
            }
            lock (gate)
            {
                if (clock.ElapsedMilliseconds >= TimeoutMs) { error = WasmNetworkError.TimedOut; closing = true; }
                else if (tls == null) error = WasmNetworkError.Success;
            }
        }

        // Klartext der App erst nach dem Handshake und nur, wenn kein Chiffretext mehr aussteht (Gegendruck).
        private void Encrypt(TlsClientProtocol protocol)
        {
            if (protocol.IsConnected && cipherOutCount == 0 && protocol.GetAvailableOutputBytes() == 0)
            {
                int count = 0;
                lock (gate) while (count < scratch.Length && output.Count > 0) scratch[count++] = output.Dequeue();
                if (count > 0) protocol.WriteApplicationData(scratch, 0, count);
            }
            if (cipherOutCount < cipherOut.Length)
                cipherOutCount += protocol.ReadOutput(cipherOut, cipherOutCount, cipherOut.Length - cipherOutCount);
        }

        private void TransferCipher(TlsClientProtocol protocol)
        {
            Socket current = socket!;
            if (cipherOutCount > 0 && current.Poll(0, SelectMode.SelectWrite))
            {
                int sent = current.Send(cipherOut, 0, cipherOutCount, SocketFlags.None);
                if (sent <= 0) throw new SocketException();
                Buffer.BlockCopy(cipherOut, sent, cipherOut, 0, cipherOutCount - sent);
                cipherOutCount -= sent;
            }
            int waiting;
            lock (gate) waiting = input.Count;
            // Neue Records erst, wenn App-Puffer und noch nicht abgeholter Klartext zusammen unter der Grenze liegen.
            if (!socketEof && cipherInCount == 0 && waiting + protocol.GetAvailableInputBytes() < Limit &&
                current.Poll(0, SelectMode.SelectRead))
            {
                cipherInCount = current.Receive(cipherIn, 0, cipherIn.Length, SocketFlags.None);
                if (cipherInCount == 0) socketEof = true;
            }
        }

        private void Decrypt(TlsClientProtocol protocol)
        {
            if (cipherInCount > 0)
            {
                protocol.OfferInput(cipherIn, 0, cipherInCount);
                cipherInCount = 0;
            }
            int count;
            lock (gate) count = Math.Min(scratch.Length, Limit - input.Count);
            count = Math.Min(count, protocol.GetAvailableInputBytes());
            if (count > 0)
            {
                protocol.ReadInput(scratch, 0, count);
                lock (gate) for (int i = 0; i < count; i++) input.Enqueue(scratch[i]);
            }
            // Erst wenn aller Klartext abgeholt ist: ohne close_notify wirft CloseInput TlsNoCloseNotifyException.
            // Ein Ende im Handshake ist wie beim Pruefhost ein Abbruch, kein Protokollfehler.
            bool drained = protocol.GetAvailableInputBytes() == 0;
            if (socketEof && drained && !protocol.IsClosed)
            {
                if (!protocol.IsConnected) throw new TlsNoCloseNotifyException();
                protocol.CloseInput();
            }
            lock (gate)
            {
                if (error == WasmNetworkError.Again && protocol.IsConnected) error = WasmNetworkError.Success;
                if (protocol.IsClosed && drained) eof = true;
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
            if (tls != null)
            {
                // close_notify nach Moeglichkeit; ein Fehler dabei wird laut ABI nicht an die App gemeldet. Steht noch ein
                // Teilrecord aus, entfaellt es, sonst laege close_notify mitten in diesem Record.
                int length = 0;
                try
                {
                    if (cipherOutCount == 0 && tls.IsConnected && !tls.IsClosed)
                    {
                        tls.Close();
                        length = tls.ReadOutput(cipherOut, 0, cipherOut.Length);
                    }
                }
                catch (Exception) { length = 0; }
                cipherOutCount = length;
                tls = null;
            }
            if (socket != null)
            {
                if (!Monitor.TryEnter(CosmosNetworkService.Gate)) return;
                if (cipherOutCount > 0)
                {
                    // Nur ein sofort moeglicher Versuch, damit die Shell nicht auf einen vollen Sendepuffer wartet.
                    try { if (socket.Poll(0, SelectMode.SelectWrite)) socket.Send(cipherOut, 0, cipherOutCount, SocketFlags.None); }
                    catch (Exception) { }
                    cipherOutCount = 0;
                }
                try { socket.Close(0); } catch (Exception) { lock (gate) error = WasmNetworkError.Io; }
                Monitor.Exit(CosmosNetworkService.Gate);
                socket = null;
            }
            lock (gate) finished = true;
        }

        // Cosmos 3.0.94 meldet einen gescheiterten Connect nur als allgemeine Exception und einen Abbruch durch RST wie ein
        // Verbindungsende; die eigenen Werte entstehen deshalb nur mit Socketfehlern des Hosts, im Kernel bleibt es bei EIO.
        // EPERM nur fuer die eigene Zertifikatspruefung, nicht anhand der Alertnummer; Alerts des Servers sind EPROTO.
        // BouncyCastle kann die Ablehnung in eine eigene Ausnahme einpacken, daher die ganze Kette pruefen.
        private static int MapError(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
                if (current is TlsCertificateRejectedException) return WasmNetworkError.NotPermitted;
            return MapOuterError(exception);
        }

        private static int MapOuterError(Exception exception) => exception switch
        {
            DnsFailureException => WasmNetworkError.NameNotFound,
            TlsNoCloseNotifyException => WasmNetworkError.ConnectionReset,
            TlsException => WasmNetworkError.Protocol,
            SocketException socketError => socketError.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => WasmNetworkError.ConnectionRefused,
                SocketError.ConnectionReset => WasmNetworkError.ConnectionReset,
                SocketError.HostUnreachable => WasmNetworkError.HostUnreachable,
                SocketError.TimedOut => WasmNetworkError.TimedOut,
                _ => WasmNetworkError.Io
            },
            _ => WasmNetworkError.Io
        };
    }
}
#endif
