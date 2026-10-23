using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipFx.Share;

/// <summary>A protocol connection: JSON lines plus raw byte streams, with its own read buffer.</summary>
public sealed class Conn : IAsyncDisposable
{
    public const int MaxLine = 64 * 1024 * 1024;
    public const int SocketBuffer = 4 * 1024 * 1024;
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    readonly Socket _socket;
    readonly NetworkStream _stream;
    readonly byte[] _buf = new byte[256 * 1024];
    int _pos, _len;

    public IPEndPoint? Remote { get; }

    public Conn(Socket socket)
    {
        _socket = socket;
        Tune(socket);
        _stream = new NetworkStream(socket, ownsSocket: true);
        Remote = socket.RemoteEndPoint as IPEndPoint;
    }

    public static void Tune(Socket s)
    {
        s.NoDelay = true;
        try { s.SendBufferSize = SocketBuffer; s.ReceiveBufferSize = SocketBuffer; } catch { }
        try { s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
    }

    public static async Task<Conn> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct, Func<Socket, Task>? prepare = null)
    {
        var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            Tune(s);
            if (prepare != null) await prepare(s);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await s.ConnectAsync(new IPEndPoint(address, port), cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Couldn't reach the other device."); }
            return new Conn(s);
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    CancellationTokenSource Timeout(CancellationToken ct, TimeSpan? t = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(t ?? IdleTimeout);
        return cts;
    }

    async ValueTask<bool> Fill(CancellationToken ct)
    {
        if (_pos > 0 && _pos == _len) { _pos = _len = 0; }
        if (_len == _buf.Length)
        {
            Buffer.BlockCopy(_buf, _pos, _buf, 0, _len - _pos);
            _len -= _pos; _pos = 0;
        }
        int n = await _stream.ReadAsync(_buf.AsMemory(_len), ct);
        if (n == 0) return false;
        _len += n;
        return true;
    }

    /// <summary>Reads one JSON message line, skipping keep-alive blank lines. Null when the peer closed.</summary>
    public async Task<JsonObject?> ReadMessageAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = Timeout(ct, timeout);
        MemoryStream? big = null;
        while (true)
        {
            int nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
            if (nl >= 0)
            {
                int start = _pos;
                _pos = nl + 1;
                ArraySegment<byte> line;
                if (big != null) { big.Write(_buf, start, nl - start); line = big.ToArray(); big = null; }
                else line = new ArraySegment<byte>(_buf, start, nl - start);
                if (line.Count > 0 && line[^1] == '\r') line = line[..^1];
                if (line.Count == 0) { cts.CancelAfter(timeout ?? IdleTimeout); continue; }
                return JsonNode.Parse(line) as JsonObject ?? throw new IOException("Bad message.");
            }
            if (_len - _pos > 0 && (_len == _buf.Length))
            {
                // A long line: spill to a memory stream.
                big ??= new MemoryStream();
                big.Write(_buf, _pos, _len - _pos);
                if (big.Length > MaxLine) throw new IOException("Message too large.");
                _pos = _len = 0;
            }
            try
            {
                if (!await Fill(cts.Token)) return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Peer stopped responding."); }
        }
    }

    public async Task WriteMessageAsync(JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString(Json.Options) + "\n");
        using var cts = Timeout(ct);
        try { await _stream.WriteAsync(bytes, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Peer stopped reading."); }
    }

    public Task KeepAliveAsync(CancellationToken ct) => _stream.WriteAsync("\n"u8.ToArray(), ct).AsTask();

    /// <summary>Reads exactly dst.Length bytes.</summary>
    public async Task ReadExactAsync(Memory<byte> dst, CancellationToken ct)
    {
        using var cts = Timeout(ct);
        try
        {
            int got = 0;
            int buffered = Math.Min(_len - _pos, dst.Length);
            if (buffered > 0)
            {
                _buf.AsMemory(_pos, buffered).CopyTo(dst);
                _pos += buffered; got = buffered;
            }
            while (got < dst.Length)
            {
                int n = await _stream.ReadAsync(dst[got..], cts.Token);
                if (n == 0) throw new IOException("Connection closed.");
                got += n;
                cts.CancelAfter(IdleTimeout);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Peer stopped sending."); }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var cts = Timeout(ct);
        try { await _stream.WriteAsync(data, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Peer stopped reading."); }
    }

    public async Task<uint> ReadUInt32Async(CancellationToken ct)
    {
        var b = new byte[4];
        await ReadExactAsync(b, ct);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    public Task WriteUInt32Async(uint v, CancellationToken ct)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return WriteAsync(b, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        await _stream.DisposeAsync();
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static JsonObject Ok(JsonObject? extra = null)
    {
        var o = new JsonObject { ["ok"] = true };
        if (extra != null) foreach (var (k, v) in extra.ToList()) { extra.Remove(k); o[k] = v; }
        return o;
    }

    public static JsonObject Error(string error) => new() { ["ok"] = false, ["error"] = error };

    public static JsonNode? From<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
    public static T? To<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Options);

    public static string? Str(this JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static long Long(this JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
    public static bool Bool(this JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
