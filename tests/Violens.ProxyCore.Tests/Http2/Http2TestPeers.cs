using System.Net;
using System.Net.Sockets;
using System.Text;
using Violens.ProxyCore.Http2;
using Violens.ProxyCore.Http2.Hpack;
using Xunit;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary>A loopback HTTP/2 server built on <see cref="Http2Connection"/>, running a handler per stream.</summary>
internal sealed class Http2TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Http2Connection> _connections = [];
    private readonly Task _accepting;

    public Http2TestServer(Func<Http2Stream, Task> handler, Http2Settings? settings = null)
    {
        _listener.Start();
        _accepting = AcceptAsync(handler, settings);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Handler exceptions, which tests check are absent.</summary>
    public List<Exception> Errors { get; } = [];

    public IReadOnlyList<Http2Connection> Connections
    {
        get
        {
            lock (_connections)
            {
                return [.. _connections];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var connection in Connections)
        {
            await connection.DisposeAsync();
        }

        try
        {
            await _accepting;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Reads the request, then answers 200 with its body echoed and an x-length trailer.</summary>
    public static async Task EchoAsync(Http2Stream stream)
    {
        var request = await stream.ReadHeadersAsync(CancellationToken.None);
        var body = await stream.ReadAllDataAsync(CancellationToken.None);
        var path = request.First(h => h.Name == ":path").Value;
        await stream.WriteHeadersAsync([(":status", "200"), ("content-type", "application/octet-stream"), ("x-path", path)], endStream: false, CancellationToken.None);
        await stream.WriteDataAsync(body, endStream: false, CancellationToken.None);
        await stream.WriteTrailersAsync([("x-length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))], CancellationToken.None);
    }

    private async Task AcceptAsync(Func<Http2Stream, Task> handler, Http2Settings? settings)
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            client.NoDelay = true;
            _ = ServeAsync(client, handler, settings);
        }
    }

    private async Task ServeAsync(TcpClient client, Func<Http2Stream, Task> handler, Http2Settings? settings)
    {
        using var socket = client;
        try
        {
            await using var connection = await Http2Connection.AcceptAsync(client.GetStream(), settings, _stop.Token);
            lock (_connections)
            {
                _connections.Add(connection);
            }

            while (await connection.AcceptStreamAsync(_stop.Token) is { } stream)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler(stream);
                    }
                    catch (Exception ex) when (ex is not Http2StreamClosedException)
                    {
                        lock (Errors)
                        {
                            Errors.Add(ex);
                        }
                    }
                });
            }

            await connection.Completion;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>A hand-driven client that writes raw frames, for checking how a server answers protocol errors.</summary>
internal sealed class RawHttp2Client : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly HpackEncoder _encoder = new();
    private Stream _stream = Stream.Null;

    public static async Task<RawHttp2Client> ConnectAsync(int port, bool sendSettings = true)
    {
        var raw = new RawHttp2Client();
        await raw._client.ConnectAsync(IPAddress.Loopback, port);
        raw._stream = raw._client.GetStream();
        await raw.WriteAsync(Http2FrameCodec.ClientPreface.ToArray());
        if (sendSettings)
        {
            await raw.WriteAsync(Http2FrameCodec.Settings([]));
        }

        return raw;
    }

    public Task WriteAsync(byte[] bytes) => _stream.WriteAsync(bytes).AsTask();

    public Task WriteFrameAsync(Http2FrameType type, Http2FrameFlags flags, int streamId, byte[] payload) =>
        WriteAsync(Http2FrameCodec.Build(type, flags, streamId, payload));

    public byte[] Encode(params (string, string)[] headers) => _encoder.Encode(headers);

    public static (string, string)[] Get(string path = "/") =>
        [(":method", "GET"), (":scheme", "https"), (":path", path), (":authority", "example.test")];

    /// <summary>Reads frames until one matches, skipping SETTINGS and the like; null if the connection ends first.</summary>
    public async Task<Http2Frame?> ReadUntilAsync(Func<Http2Frame, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            Http2Frame? frame;
            try
            {
                frame = await Http2FrameCodec.ReadAsync(_stream, Http2FrameCodec.MaxMaxFrameSize, timeout.Token);
            }
            catch (IOException)
            {
                return null;
            }

            if (frame is null || match(frame))
            {
                return frame;
            }
        }
    }

    /// <summary>The error code of the GOAWAY the server sends; fails if it never does.</summary>
    public async Task<Http2ErrorCode> ReadGoAwayAsync()
    {
        var frame = await ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
        Assert.NotNull(frame);
        return (Http2ErrorCode)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(4));
    }

    /// <summary>The error code of the RST_STREAM the server sends for <paramref name="streamId"/>.</summary>
    public async Task<Http2ErrorCode> ReadResetAsync(int streamId)
    {
        var frame = await ReadUntilAsync(f => (f.Type == Http2FrameType.RstStream && f.StreamId == streamId) || f.Type == Http2FrameType.GoAway);
        Assert.NotNull(frame);
        Assert.Equal(Http2FrameType.RstStream, frame.Type);
        return (Http2ErrorCode)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Payload);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class Http2TestClient
{
    /// <summary>Connects a <see cref="Http2Connection"/> client to a loopback port.</summary>
    public static async Task<(Http2Connection Connection, TcpClient Socket)> ConnectAsync(int port, Http2Settings? settings = null)
    {
        var socket = new TcpClient { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, port);
        return (await Http2Connection.ConnectAsync(socket.GetStream(), settings, CancellationToken.None), socket);
    }

    public static (string, string)[] Request(string method, string path, int port) =>
        [(":method", method), (":scheme", "http"), (":path", path), (":authority", $"127.0.0.1:{port}")];

    public static string Text(byte[] body) => Encoding.UTF8.GetString(body);
}
