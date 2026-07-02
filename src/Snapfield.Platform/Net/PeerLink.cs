using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Snapfield.Core.Net;

namespace Snapfield.Platform.Net;

/// <summary>
/// A single TCP connection between two Snapfield peers. One side calls
/// <see cref="Listen"/>, the other <see cref="Connect"/>; after that the API is
/// symmetric. Messages are length-prefixed JSON frames. Nagle is disabled so
/// cursor updates go out immediately.
/// </summary>
public sealed class PeerLink : IDisposable
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _reader;
    private readonly object _writeLock = new();
    private volatile bool _closed;

    public event Action? Connected;
    public event Action<NetMessage>? MessageReceived;
    public event Action<string>? Disconnected;

    public bool IsConnected => _stream is not null && !_closed;

    /// <summary>Listen for a single incoming peer (runs the accept in the background).</summary>
    public void Listen(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        var t = new Thread(() =>
        {
            try
            {
                var c = _listener.AcceptTcpClient();
                Attach(c);
            }
            // Catch-all: an exception filter of `when (!_closed)` would leave the
            // dispose-triggered abort UNHANDLED on this background thread and kill
            // the whole process. Catch everything; only report when still open.
            catch (Exception ex) { if (!_closed) Disconnected?.Invoke(ex.Message); }
        }) { IsBackground = true, Name = "Snapfield.Listen" };
        t.Start();
    }

    /// <summary>Connect to a listening peer.</summary>
    public void Connect(string host, int port)
    {
        var t = new Thread(() =>
        {
            try
            {
                var c = new TcpClient();
                c.Connect(host, port);
                Attach(c);
            }
            catch (Exception ex) when (!_closed) { Disconnected?.Invoke(ex.Message); }
        }) { IsBackground = true, Name = "Snapfield.Connect" };
        t.Start();
    }

    private void Attach(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = _client.GetStream();
        Connected?.Invoke();

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Snapfield.Reader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var stream = _stream!;
        var lenBuf = new byte[4];
        try
        {
            while (!_closed)
            {
                if (!ReadExact(stream, lenBuf, 4)) break;
                var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (len <= 0 || len > 16 * 1024 * 1024) break; // sanity guard
                var body = new byte[len];
                if (!ReadExact(stream, body, len)) break;

                var msg = NetMessage.FromBody(body);
                if (msg is not null) MessageReceived?.Invoke(msg);
            }
        }
        catch (Exception ex) { if (!_closed) Disconnected?.Invoke(ex.Message); return; }
        if (!_closed) Disconnected?.Invoke("peer closed the connection");
    }

    private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    public void Send(NetMessage message)
    {
        var stream = _stream;
        if (stream is null || _closed) return;
        var frame = message.ToFrame();
        try
        {
            lock (_writeLock)
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
        }
        catch (Exception ex) { if (!_closed) Disconnected?.Invoke(ex.Message); }
    }

    public void Dispose()
    {
        _closed = true;
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}
