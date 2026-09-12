using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DeckLinkStreamStudio.Engines;

/// <summary>
/// High-performance local loopback TCP broadcast hub.
/// Distributes the Master FFmpeg MPEG-TS stream from port 19350 to independent
/// destination client ports (19351 for Facebook, 19352 for YouTube, 19353 for YouTube News).
/// Each client gets an independent bounded channel and write task, ensuring that
/// starting, stopping, or network stalling on any destination never interrupts or blocks
/// other active streams or the master capture/encoder.
/// </summary>
public sealed class TcpBroadcastHub : IDisposable
{
    public const int MasterPort = 19350;
    public const int BaseClientPort = 19351;
    public const int ClientCount = 3;

    private readonly TcpListener _masterListener;
    private readonly TcpListener[] _clientListeners = new TcpListener[ClientCount];
    private readonly ConcurrentDictionary<int, ClientSession> _activeClients = new();
    private CancellationTokenSource? _cts;
    private bool _isDisposed;
    private int _clientCounter;

    public bool IsRunning { get; private set; }

    private sealed class ClientSession : IDisposable
    {
        public int Id { get; }
        public int ClientPort { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public Channel<byte[]> Channel { get; }
        public CancellationTokenSource Cts { get; }

        public ClientSession(int id, int clientPort, TcpClient client)
        {
            Id = id;
            ClientPort = clientPort;
            Client = client;
            Stream = client.GetStream();
            Cts = new CancellationTokenSource();
            // Bounded queue: max 200 chunks (~6 MB). If queue is full, drop oldest to maintain real-time streaming
            Channel = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
        }

        public void Dispose()
        {
            try { Cts.Cancel(); } catch { }
            try { Stream.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
            try { Cts.Dispose(); } catch { }
        }
    }

    public TcpBroadcastHub()
    {
        _masterListener = new TcpListener(IPAddress.Loopback, MasterPort);
        for (int i = 0; i < ClientCount; i++)
        {
            _clientListeners[i] = new TcpListener(IPAddress.Loopback, BaseClientPort + i);
        }
    }

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _cts = new CancellationTokenSource();
            _masterListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _masterListener.Start();

            for (int i = 0; i < ClientCount; i++)
            {
                int port = BaseClientPort + i;
                var listener = _clientListeners[i];
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                Task.Run(() => AcceptClientsLoop(listener, port, _cts.Token));
            }

            Task.Run(() => AcceptMasterLoop(_cts.Token));
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TcpBroadcastHub] Start error: {ex.Message}");
        }
    }

    private async Task AcceptMasterLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isDisposed)
        {
            try
            {
                using var masterClient = await _masterListener.AcceptTcpClientAsync(ct);
                masterClient.NoDelay = true;
                using var stream = masterClient.GetStream();
                var buffer = new byte[32768];

                while (!ct.IsCancellationRequested && !_isDisposed)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (read <= 0) break;

                    if (_activeClients.Count > 0)
                    {
                        var chunk = new byte[read];
                        Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                        foreach (var kvp in _activeClients)
                        {
                            kvp.Value.Channel.Writer.TryWrite(chunk);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || _isDisposed) break;
                Debug.WriteLine($"[TcpBroadcastHub] Master connection error: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task AcceptClientsLoop(TcpListener listener, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isDisposed)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.SendBufferSize = 65536;

                // Close any existing session on the exact same destination port to prevent duplicates
                foreach (var kvp in _activeClients)
                {
                    if (kvp.Value.ClientPort == port)
                    {
                        if (_activeClients.TryRemove(kvp.Key, out var oldSession))
                        {
                            oldSession.Dispose();
                        }
                    }
                }

                int id = Interlocked.Increment(ref _clientCounter);
                var session = new ClientSession(id, port, client);
                _activeClients[id] = session;

                // Start dedicated non-blocking writer task for this client
                _ = Task.Run(() => RunClientWriterLoop(session, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                if (ct.IsCancellationRequested || _isDisposed) break;
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task RunClientWriterLoop(ClientSession session, CancellationToken globalCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token, globalCt);
        var ct = linkedCts.Token;

        try
        {
            var reader = session.Channel.Reader;
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var chunk))
                {
                    await session.Stream.WriteAsync(chunk, 0, chunk.Length, ct);
                }
            }
        }
        catch
        {
            // Client closed connection or encountered write error
        }
        finally
        {
            _activeClients.TryRemove(session.Id, out _);
            session.Dispose();
        }
    }

    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();

        try { _masterListener.Stop(); } catch { }
        for (int i = 0; i < ClientCount; i++)
        {
            try { _clientListeners[i].Stop(); } catch { }
        }

        foreach (var kvp in _activeClients)
        {
            kvp.Value.Dispose();
        }
        _activeClients.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _cts?.Dispose();
    }
}
