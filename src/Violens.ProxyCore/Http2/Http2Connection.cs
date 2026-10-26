using System.Buffers.Binary;
using System.Threading.Channels;
using Violens.ProxyCore.Http2.Hpack;

namespace Violens.ProxyCore.Http2;

/// <summary>What this side tells its peer in SETTINGS, and the limits it enforces.</summary>
internal sealed record Http2Settings
{
    public int HeaderTableSize { get; init; } = 4096;

    public int MaxConcurrentStreams { get; init; } = 100;

    /// <summary>Each stream's receive window: how much of a body the peer may send before it's read.</summary>
    public int InitialWindowSize { get; init; } = 1 << 20;

    public int MaxFrameSize { get; init; } = Http2FrameCodec.MinMaxFrameSize;

    public int MaxHeaderListSize { get; init; } = 64 * 1024;

    /// <summary>The connection's receive window, shared by all streams.</summary>
    public int ConnectionWindowSize { get; init; } = 16 << 20;
}

internal enum Http2Role
{
    Client,
    Server,
}

/// <summary>
/// One HTTP/2 connection (RFC 9113) over an already-established transport,
/// as either the client or the server. A single loop reads frames and hands
/// them to their streams; any number of streams can be read and written at
/// once from other tasks. Protocol errors are answered as the RFC says: a
/// stream error resets that stream, a connection error sends GOAWAY and
/// closes the transport.
/// </summary>
internal sealed class Http2Connection : IAsyncDisposable
{
    private const int DefaultWindowSize = 65535;

    // How many recently reset stream ids to remember, so frames the peer
    // sent before it saw our RST_STREAM are ignored rather than treated as errors.
    private const int RememberedResets = 1024;

    private readonly Stream _transport;
    private readonly Http2Role _role;
    private readonly Http2Settings _local;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HpackEncoder _encoder = new();
    private readonly HpackDecoder _decoder;
    private readonly object _sync = new();
    private readonly Dictionary<int, Http2Stream> _streams = [];
    private readonly HashSet<int> _resetByUs = [];
    private readonly Queue<int> _resetOrder = new();
    private readonly Channel<Http2Stream> _accepted = Channel.CreateUnbounded<Http2Stream>();
    private readonly CancellationTokenSource _shutdown = new();

    // Set once the peer's first SETTINGS have been applied (or the connection
    // has ended). A client waits for it before opening streams, so it never
    // opens more than the server allows and has them refused.
    private readonly TaskCompletionSource _peerSettingsApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource _windowChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _readLoop = Task.CompletedTask;
    private int _lastPeerStreamId;
    private int _nextLocalStreamId;
    private int _activePeerStreams;
    private int _activeLocalStreams;
    private long _connectionSendWindow = DefaultWindowSize;
    private int _connectionReceiveWindow = DefaultWindowSize;
    private int _connectionPendingCredit;
    private int _peerInitialWindowSize = DefaultWindowSize;
    private int _peerMaxFrameSize = Http2FrameCodec.MinMaxFrameSize;
    private long _peerMaxConcurrentStreams = long.MaxValue;
    private bool _receivedFirstFrame;
    private int? _goAwayLastStreamId;
    private int? _goAwaySentLastStreamId;
    private bool _closed;
    private int _disposed;
    private Exception? _closedBecause;

    // A header block being assembled from HEADERS and CONTINUATION frames.
    private int _continuationStreamId;
    private List<byte>? _headerFragments;
    private bool _continuationEndStream;
    private bool _continuationSelfDependent;

    private Http2Connection(Stream transport, Http2Role role, Http2Settings local)
    {
        _transport = transport;
        _role = role;
        _local = local with { InitialWindowSize = Math.Max(local.InitialWindowSize, DefaultWindowSize) };
        _decoder = new HpackDecoder(local.HeaderTableSize);
        _nextLocalStreamId = role == Http2Role.Client ? 1 : 2;
    }

    /// <summary>Completes when the connection has ended, for whatever reason.</summary>
    public Task Completion => _readLoop;

    /// <summary>Why the connection ended, once it has; null for an orderly close.</summary>
    public Exception? ClosedBecause => _closedBecause;

    /// <summary>Starts the client side: sends the preface and SETTINGS, then reads frames in the background.</summary>
    public static async Task<Http2Connection> ConnectAsync(Stream transport, Http2Settings? settings, CancellationToken cancellationToken)
    {
        var connection = new Http2Connection(transport, Http2Role.Client, settings ?? new Http2Settings());
        var opening = new List<byte>(Http2FrameCodec.ClientPreface.ToArray());
        opening.AddRange(connection.OpeningFrames());
        await transport.WriteAsync(opening.ToArray(), cancellationToken);
        await transport.FlushAsync(cancellationToken);
        connection._readLoop = connection.ReadLoopAsync();
        return connection;
    }

    /// <summary>
    /// Starts the server side: reads the client's preface, sends SETTINGS,
    /// then reads frames in the background. Throws <see cref="IOException"/>
    /// if the client didn't send the HTTP/2 preface.
    /// </summary>
    public static async Task<Http2Connection> AcceptAsync(Stream transport, Http2Settings? settings, CancellationToken cancellationToken)
    {
        var preface = new byte[Http2FrameCodec.ClientPreface.Length];
        var read = 0;
        while (read < preface.Length)
        {
            var n = await transport.ReadAsync(preface.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read < preface.Length || !preface.AsSpan().SequenceEqual(Http2FrameCodec.ClientPreface))
        {
            throw new IOException("The client didn't send the HTTP/2 connection preface.");
        }

        var connection = new Http2Connection(transport, Http2Role.Server, settings ?? new Http2Settings());
        await transport.WriteAsync(connection.OpeningFrames(), cancellationToken);
        await transport.FlushAsync(cancellationToken);
        connection._readLoop = connection.ReadLoopAsync();
        return connection;
    }

    /// <summary>The next stream the client opened, once its headers have arrived; null when the connection ends.</summary>
    public async Task<Http2Stream?> AcceptStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _accepted.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a stream by sending its request headers, waiting first if the
    /// server's SETTINGS_MAX_CONCURRENT_STREAMS has been reached.
    /// </summary>
    public async Task<Http2Stream> OpenStreamAsync(
        IReadOnlyList<(string Name, string Value)> headers, bool endStream, CancellationToken cancellationToken)
    {
        if (_role != Http2Role.Client)
        {
            throw new InvalidOperationException("Only the client opens streams.");
        }

        await _peerSettingsApplied.Task.WaitAsync(cancellationToken);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                ThrowIfUnusable();
                if (_activeLocalStreams < _peerMaxConcurrentStreams)
                {
                    _activeLocalStreams++;
                    break;
                }

                changed = _windowChanged.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }

        Http2Stream? stream = null;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Stream ids must go out in increasing order, so the id is taken
            // under the same lock as the write.
            lock (_sync)
            {
                ThrowIfUnusable();
                stream = new Http2Stream(this, _nextLocalStreamId, _peerInitialWindowSize, _local.InitialWindowSize)
                {
                    LocalClosed = endStream,
                };
                _nextLocalStreamId += 2;
                _streams[stream.Id] = stream;
            }

            var block = _encoder.Encode(headers);
            await WriteUnlockedAsync(Http2FrameCodec.Headers(stream.Id, block, endStream, _peerMaxFrameSize), cancellationToken);
            return stream;
        }
        catch
        {
            lock (_sync)
            {
                if (stream is null)
                {
                    _activeLocalStreams--;
                }
                else
                {
                    RemoveStream(stream);
                }
            }

            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Sends GOAWAY (NO_ERROR): no new streams; those already open carry on.</summary>
    public async Task GoAwayAsync(CancellationToken cancellationToken)
    {
        int lastStreamId;
        lock (_sync)
        {
            lastStreamId = _lastPeerStreamId;
            _goAwaySentLastStreamId = lastStreamId;
        }

        await WriteAsync(Http2FrameCodec.GoAway(lastStreamId, Http2ErrorCode.NoError, string.Empty), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync();
        await _transport.DisposeAsync();
        try
        {
            await _readLoop;
        }
        catch (Exception)
        {
            // Already recorded in ClosedBecause.
        }

        _writeLock.Dispose();
        _shutdown.Dispose();
    }

    internal async Task SendHeadersAsync(
        Http2Stream stream, IReadOnlyList<(string Name, string Value)> headers, bool endStream, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
            {
                ThrowIfUnusable();
                ThrowIfCantSend(stream);
                if (endStream)
                {
                    EndLocalSide(stream);
                }
            }

            var block = _encoder.Encode(headers);
            await WriteUnlockedAsync(Http2FrameCodec.Headers(stream.Id, block, endStream, _peerMaxFrameSize), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task SendDataAsync(Http2Stream stream, ReadOnlyMemory<byte> data, bool endStream, CancellationToken cancellationToken)
    {
        var remaining = data;
        while (true)
        {
            int chunkLength;
            Task? waitForCredit = null;
            lock (_sync)
            {
                ThrowIfUnusable();
                ThrowIfCantSend(stream);
                var available = Math.Min(Math.Min(_connectionSendWindow, stream.SendWindow), _peerMaxFrameSize);
                if (remaining.IsEmpty)
                {
                    chunkLength = 0;
                }
                else if (available <= 0)
                {
                    chunkLength = -1;
                    waitForCredit = _windowChanged.Task;
                }
                else
                {
                    chunkLength = (int)Math.Min(available, remaining.Length);
                    _connectionSendWindow -= chunkLength;
                    stream.SendWindow -= chunkLength;
                }
            }

            if (waitForCredit is not null)
            {
                await waitForCredit.WaitAsync(cancellationToken);
                continue;
            }

            if (chunkLength == 0 && !endStream)
            {
                return;
            }

            var isLast = chunkLength == remaining.Length;
            var flags = isLast && endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                if (flags == Http2FrameFlags.EndStream)
                {
                    lock (_sync)
                    {
                        ThrowIfCantSend(stream);
                        EndLocalSide(stream);
                    }
                }

                await WriteUnlockedAsync(
                    Http2FrameCodec.Build(Http2FrameType.Data, flags, stream.Id, remaining.Span[..chunkLength]), cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            remaining = remaining[chunkLength..];
            if (isLast)
            {
                return;
            }
        }
    }

    internal async Task ResetStreamAsync(Http2Stream stream, Http2ErrorCode code)
    {
        lock (_sync)
        {
            if (stream.IsReset || !_streams.ContainsKey(stream.Id))
            {
                return;
            }

            MarkResetByUs(stream, new Http2StreamClosedException($"Stream {stream.Id} was reset.", code));
        }

        await TryWriteAsync(Http2FrameCodec.RstStream(stream.Id, code));
    }

    /// <summary>
    /// The application has read <paramref name="count"/> body octets: give the
    /// peer that much stream credit back, in batches of half a window.
    /// </summary>
    internal async ValueTask ReturnCreditAsync(Http2Stream stream, int count, CancellationToken cancellationToken)
    {
        int increment;
        lock (_sync)
        {
            if (stream.RemoteClosed || stream.IsReset || _closed)
            {
                return;
            }

            stream.PendingCredit += count;
            if (stream.PendingCredit < _local.InitialWindowSize / 2)
            {
                return;
            }

            increment = stream.PendingCredit;
            stream.PendingCredit = 0;
            stream.ReceiveWindow += increment;
        }

        await TryWriteAsync(Http2FrameCodec.WindowUpdate(stream.Id, increment), cancellationToken);
    }

    private byte[] OpeningFrames()
    {
        var settings = new List<(Http2SettingId, uint)>();
        if (_local.HeaderTableSize != 4096)
        {
            settings.Add((Http2SettingId.HeaderTableSize, (uint)_local.HeaderTableSize));
        }

        if (_role == Http2Role.Client)
        {
            settings.Add((Http2SettingId.EnablePush, 0));
        }

        settings.Add((Http2SettingId.MaxConcurrentStreams, (uint)_local.MaxConcurrentStreams));
        settings.Add((Http2SettingId.InitialWindowSize, (uint)_local.InitialWindowSize));
        if (_local.MaxFrameSize != Http2FrameCodec.MinMaxFrameSize)
        {
            settings.Add((Http2SettingId.MaxFrameSize, (uint)_local.MaxFrameSize));
        }

        settings.Add((Http2SettingId.MaxHeaderListSize, (uint)_local.MaxHeaderListSize));

        var frames = new List<byte>(Http2FrameCodec.Settings(settings));
        if (_local.ConnectionWindowSize > DefaultWindowSize)
        {
            frames.AddRange(Http2FrameCodec.WindowUpdate(0, _local.ConnectionWindowSize - DefaultWindowSize));
            _connectionReceiveWindow = _local.ConnectionWindowSize;
        }

        return frames.ToArray();
    }

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (true)
            {
                var frame = await Http2FrameCodec.ReadAsync(_transport, _local.MaxFrameSize, _shutdown.Token);
                if (frame is null)
                {
                    break;
                }

                try
                {
                    await HandleFrameAsync(frame);
                }
                catch (Http2StreamException ex)
                {
                    await ResetFromErrorAsync(ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                reason = ex;
            }
        }
        catch (Exception ex)
        {
            // A protocol violation, or a fault of our own: either way GOAWAY says why.
            reason = ex;
            var code = ex is Http2ConnectionException connectionError ? connectionError.Code : Http2ErrorCode.InternalError;
            int lastStreamId;
            lock (_sync)
            {
                lastStreamId = _lastPeerStreamId;
            }

            await TryWriteAsync(Http2FrameCodec.GoAway(lastStreamId, code, ex.Message));
            await DrainBrieflyAsync();
            await _transport.DisposeAsync();
        }
        finally
        {
            CloseAll(reason);
        }
    }

    /// <summary>
    /// Reads and discards what the peer is still sending, for a moment, before
    /// closing. Closing a socket with unread input resets the connection, which
    /// can discard the GOAWAY before the peer reads it.
    /// </summary>
    private async Task DrainBrieflyAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        var buffer = new byte[16384];
        try
        {
            while (await _transport.ReadAsync(buffer, timeout.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task HandleFrameAsync(Http2Frame frame)
    {
        if (!_receivedFirstFrame)
        {
            if (frame.Type != Http2FrameType.Settings || frame.Has(Http2FrameFlags.Ack))
            {
                throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "The first frame wasn't SETTINGS.");
            }

            _receivedFirstFrame = true;
        }

        if (_headerFragments is not null &&
            (frame.Type != Http2FrameType.Continuation || frame.StreamId != _continuationStreamId))
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError, $"A {frame.Type} frame arrived in the middle of a header block.");
        }

        switch (frame.Type)
        {
            case Http2FrameType.Data:
                await HandleDataAsync(frame);
                break;
            case Http2FrameType.Headers:
                HandleHeaders(frame);
                break;
            case Http2FrameType.Continuation:
                HandleContinuation(frame);
                break;
            case Http2FrameType.Priority:
                HandlePriority(frame);
                break;
            case Http2FrameType.RstStream:
                HandleRstStream(frame);
                break;
            case Http2FrameType.Settings:
                await HandleSettingsAsync(frame);
                break;
            case Http2FrameType.PushPromise:
                throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "PUSH_PROMISE isn't allowed.");
            case Http2FrameType.Ping:
                await HandlePingAsync(frame);
                break;
            case Http2FrameType.GoAway:
                HandleGoAway(frame);
                break;
            case Http2FrameType.WindowUpdate:
                HandleWindowUpdate(frame);
                break;
            default:
                // Unknown frame types are ignored (RFC 9113 section 4.1).
                break;
        }
    }

    private async Task HandleDataAsync(Http2Frame frame)
    {
        if (frame.StreamId == 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "DATA on stream 0.");
        }

        var data = Http2FrameCodec.Unpad(frame);
        var length = frame.Payload.Length;
        var endStream = frame.Has(Http2FrameFlags.EndStream);
        int connectionIncrement = 0;
        lock (_sync)
        {
            // The connection window counts every DATA frame, whatever becomes of its stream.
            _connectionReceiveWindow -= length;
            if (_connectionReceiveWindow < 0)
            {
                throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, "The peer sent more than the connection window allows.");
            }

            _connectionPendingCredit += length;
            if (_connectionPendingCredit >= _local.ConnectionWindowSize / 2)
            {
                connectionIncrement = _connectionPendingCredit;
                _connectionPendingCredit = 0;
                _connectionReceiveWindow += connectionIncrement;
            }
        }

        if (connectionIncrement > 0)
        {
            await TryWriteAsync(Http2FrameCodec.WindowUpdate(0, connectionIncrement));
        }

        lock (_sync)
        {
            var stream = FindActiveStream(frame.StreamId, frame.Type);
            if (stream is null)
            {
                return;
            }

            if (stream.RemoteClosed)
            {
                throw new Http2StreamException(stream.Id, Http2ErrorCode.StreamClosed, $"DATA on stream {stream.Id} after its END_STREAM.");
            }

            if (!stream.ReceivedFinalHeaders)
            {
                throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"DATA on stream {stream.Id} before its final headers.");
            }

            stream.ReceiveWindow -= length;
            if (stream.ReceiveWindow < 0)
            {
                throw new Http2StreamException(stream.Id, Http2ErrorCode.FlowControlError, $"Stream {stream.Id} sent more than its window allows.");
            }

            // Padding is never read by the application, so its credit comes back now.
            stream.PendingCredit += length - data.Length;
            stream.ReceivedDataLength += data.Length;
            if (stream.ExpectedContentLength is { } expected &&
                (stream.ReceivedDataLength > expected || (endStream && stream.ReceivedDataLength != expected)))
            {
                throw new Http2StreamException(
                    stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}'s body doesn't match its content-length ({expected}).");
            }

            stream.Deliver(new Http2Stream.DataItem(data, endStream));
            if (endStream)
            {
                stream.RemoteClosed = true;
                stream.EndInbound();
                RemoveIfDone(stream);
            }
        }
    }

    private void HandleHeaders(Http2Frame frame)
    {
        if (frame.StreamId == 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "HEADERS on stream 0.");
        }

        var fragment = Http2FrameCodec.Unpad(frame);
        var selfDependent = false;
        if (frame.Has(Http2FrameFlags.Priority))
        {
            if (fragment.Length < 5)
            {
                throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "HEADERS is too short for its priority fields.");
            }

            var dependency = BinaryPrimitives.ReadInt32BigEndian(fragment.Span) & int.MaxValue;
            fragment = fragment[5..];
            selfDependent = dependency == frame.StreamId;
        }

        if (frame.Has(Http2FrameFlags.EndHeaders))
        {
            OnHeaderBlock(frame.StreamId, fragment.Span, frame.Has(Http2FrameFlags.EndStream), selfDependent);
            return;
        }

        _continuationStreamId = frame.StreamId;
        _continuationEndStream = frame.Has(Http2FrameFlags.EndStream);
        _continuationSelfDependent = selfDependent;
        _headerFragments = [.. fragment.Span];
    }

    private void HandleContinuation(Http2Frame frame)
    {
        if (_headerFragments is null)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "CONTINUATION without a header block in progress.");
        }

        _headerFragments.AddRange(frame.Payload);
        if (_headerFragments.Count > (_local.MaxHeaderListSize * 2) + _local.MaxFrameSize)
        {
            throw new Http2ConnectionException(Http2ErrorCode.EnhanceYourCalm, "The header block is too large.");
        }

        if (frame.Has(Http2FrameFlags.EndHeaders))
        {
            var block = _headerFragments.ToArray();
            _headerFragments = null;
            OnHeaderBlock(_continuationStreamId, block, _continuationEndStream, _continuationSelfDependent);
        }
    }

    private void OnHeaderBlock(int streamId, ReadOnlySpan<byte> block, bool endStream, bool selfDependent)
    {
        // Always decoded, even when the stream is about to be refused, to keep
        // the HPACK state in step with the peer's.
        var headers = DecodeBlock(block);
        if (selfDependent)
        {
            lock (_sync)
            {
                _lastPeerStreamId = Math.Max(_lastPeerStreamId, IsPeerInitiated(streamId) ? streamId : 0);
                RememberReset(streamId);
            }

            throw new Http2StreamException(streamId, Http2ErrorCode.ProtocolError, $"Stream {streamId} depends on itself.");
        }

        lock (_sync)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                OnHeadersForOpenStream(stream, headers, endStream);
                return;
            }

            if (_role == Http2Role.Server && IsPeerInitiated(streamId) && streamId > _lastPeerStreamId)
            {
                OnNewPeerStream(streamId, headers, endStream);
                return;
            }

            if (_resetByUs.Contains(streamId))
            {
                return;
            }

            if (!IsIdle(streamId))
            {
                throw new Http2ConnectionException(Http2ErrorCode.StreamClosed, $"HEADERS on closed stream {streamId}.");
            }

            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"HEADERS with an unexpected stream id {streamId}.");
        }
    }

    private List<(string Name, string Value)> DecodeBlock(ReadOnlySpan<byte> block)
    {
        try
        {
            return _decoder.Decode(block);
        }
        catch (HpackException ex)
        {
            throw new Http2ConnectionException(Http2ErrorCode.CompressionError, ex.Message);
        }
    }

    private void OnNewPeerStream(int streamId, List<(string Name, string Value)> headers, bool endStream)
    {
        _lastPeerStreamId = streamId;
        var stream = new Http2Stream(this, streamId, _peerInitialWindowSize, _local.InitialWindowSize)
        {
            ReceivedFinalHeaders = true,
        };

        if (streamId > _goAwaySentLastStreamId || _activePeerStreams >= _local.MaxConcurrentStreams)
        {
            RememberReset(streamId);
            throw new Http2StreamException(streamId, Http2ErrorCode.RefusedStream, $"Stream {streamId} is over the concurrent stream limit.");
        }

        if (HeaderListSize(headers) > _local.MaxHeaderListSize)
        {
            RememberReset(streamId);
            throw new Http2StreamException(streamId, Http2ErrorCode.ProtocolError, $"Stream {streamId}'s headers are larger than allowed.");
        }

        var problem = Http2HeaderRules.CheckRequest(headers);
        long? contentLength = null;
        if (problem is null && !Http2HeaderRules.TryGetContentLength(headers, out contentLength))
        {
            problem = "The content-length isn't valid.";
        }

        if (problem is not null)
        {
            RememberReset(streamId);
            throw new Http2StreamException(streamId, Http2ErrorCode.ProtocolError, $"Stream {streamId}: {problem}");
        }

        if (endStream && contentLength is > 0)
        {
            RememberReset(streamId);
            throw new Http2StreamException(streamId, Http2ErrorCode.ProtocolError, $"Stream {streamId} ended with a content-length of {contentLength} and no body.");
        }

        stream.ExpectedContentLength = contentLength;
        stream.RemoteClosed = endStream;
        _streams[streamId] = stream;
        _activePeerStreams++;
        stream.Deliver(new Http2Stream.HeadersItem(headers, endStream));
        if (endStream)
        {
            stream.EndInbound();
        }

        _accepted.Writer.TryWrite(stream);
    }

    private void OnHeadersForOpenStream(Http2Stream stream, List<(string Name, string Value)> headers, bool endStream)
    {
        if (stream.RemoteClosed)
        {
            throw new Http2StreamException(stream.Id, Http2ErrorCode.StreamClosed, $"HEADERS on stream {stream.Id} after its END_STREAM.");
        }

        if (HeaderListSize(headers) > _local.MaxHeaderListSize)
        {
            throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}'s headers are larger than allowed.");
        }

        if (stream.ReceivedFinalHeaders)
        {
            // Trailers: the last thing on the stream.
            if (!endStream)
            {
                throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}'s trailers don't end the stream.");
            }

            if (Http2HeaderRules.CheckTrailers(headers) is { } trailerProblem)
            {
                throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}: {trailerProblem}");
            }

            if (stream.ExpectedContentLength is { } expected && stream.ReceivedDataLength != expected)
            {
                throw new Http2StreamException(
                    stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}'s body doesn't match its content-length ({expected}).");
            }
        }
        else
        {
            // A response: any number of interim (1xx) blocks, then the final one.
            if (Http2HeaderRules.CheckResponse(headers) is { } problem)
            {
                throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}: {problem}");
            }

            var status = Http2HeaderRules.Status(headers);
            if (status is >= 100 and < 200)
            {
                if (endStream)
                {
                    throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}'s interim response ends the stream.");
                }
            }
            else
            {
                if (!Http2HeaderRules.TryGetContentLength(headers, out var contentLength))
                {
                    throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"Stream {stream.Id}'s content-length isn't valid.");
                }

                stream.ReceivedFinalHeaders = true;
                stream.ExpectedContentLength = contentLength;
            }
        }

        stream.Deliver(new Http2Stream.HeadersItem(headers, endStream));
        if (endStream)
        {
            stream.RemoteClosed = true;
            stream.EndInbound();
            RemoveIfDone(stream);
        }
    }

    private void HandlePriority(Http2Frame frame)
    {
        if (frame.StreamId == 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "PRIORITY on stream 0.");
        }

        if (frame.Payload.Length != 5)
        {
            throw new Http2StreamException(frame.StreamId, Http2ErrorCode.FrameSizeError, "PRIORITY isn't 5 octets.");
        }

        var dependency = BinaryPrimitives.ReadInt32BigEndian(frame.Payload) & int.MaxValue;
        if (dependency == frame.StreamId)
        {
            throw new Http2StreamException(frame.StreamId, Http2ErrorCode.ProtocolError, $"Stream {frame.StreamId} depends on itself.");
        }

        // Prioritisation is advisory, and RFC 9113 deprecates this scheme; nothing else to do.
    }

    private void HandleRstStream(Http2Frame frame)
    {
        if (frame.StreamId == 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "RST_STREAM on stream 0.");
        }

        if (frame.Payload.Length != 4)
        {
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "RST_STREAM isn't 4 octets.");
        }

        var code = (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(frame.Payload);
        lock (_sync)
        {
            if (IsIdle(frame.StreamId))
            {
                throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"RST_STREAM on idle stream {frame.StreamId}.");
            }

            if (_streams.TryGetValue(frame.StreamId, out var stream))
            {
                stream.IsReset = true;
                stream.Fail(new Http2StreamClosedException($"The peer reset stream {stream.Id} ({code}).", code));
                RemoveStream(stream);
                SignalWindows();
            }
        }
    }

    private async Task HandleSettingsAsync(Http2Frame frame)
    {
        if (frame.StreamId != 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "SETTINGS on a stream.");
        }

        if (frame.Has(Http2FrameFlags.Ack))
        {
            if (frame.Payload.Length != 0)
            {
                throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A SETTINGS acknowledgement has a payload.");
            }

            return;
        }

        if (frame.Payload.Length % 6 != 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "SETTINGS isn't a multiple of 6 octets.");
        }

        // Under the write lock: the HPACK encoder's table size changes here,
        // and the acknowledgement has to follow anything encoded before it.
        await _writeLock.WaitAsync(_shutdown.Token);
        try
        {
            foreach (var (id, value) in Http2FrameCodec.ParseSettings(frame.Payload))
            {
                ApplySetting(id, value);
            }

            await WriteUnlockedAsync(Http2FrameCodec.Build(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, []), _shutdown.Token);
        }
        finally
        {
            _writeLock.Release();
        }

        _peerSettingsApplied.TrySetResult();

    }

    private void ApplySetting(Http2SettingId id, uint value)
    {
        switch (id)
        {
            case Http2SettingId.HeaderTableSize:
                _encoder.SetPeerMaxTableSize((int)Math.Min(value, int.MaxValue));
                break;
            case Http2SettingId.EnablePush:
                if (value > 1 || (_role == Http2Role.Client && value != 0))
                {
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"SETTINGS_ENABLE_PUSH of {value} isn't allowed.");
                }

                break;
            case Http2SettingId.MaxConcurrentStreams:
                lock (_sync)
                {
                    _peerMaxConcurrentStreams = value;
                    SignalWindows();
                }

                break;
            case Http2SettingId.InitialWindowSize:
                if (value > Http2FrameCodec.MaxWindowSize)
                {
                    throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, $"SETTINGS_INITIAL_WINDOW_SIZE of {value} is too large.");
                }

                lock (_sync)
                {
                    var delta = (long)value - _peerInitialWindowSize;
                    _peerInitialWindowSize = (int)value;
                    foreach (var stream in _streams.Values)
                    {
                        stream.SendWindow += delta;
                        if (stream.SendWindow > Http2FrameCodec.MaxWindowSize)
                        {
                            throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, $"Stream {stream.Id}'s window overflowed.");
                        }
                    }

                    SignalWindows();
                }

                break;
            case Http2SettingId.MaxFrameSize:
                if (value < Http2FrameCodec.MinMaxFrameSize || value > Http2FrameCodec.MaxMaxFrameSize)
                {
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"SETTINGS_MAX_FRAME_SIZE of {value} is out of range.");
                }

                _peerMaxFrameSize = (int)value;
                break;
            default:
                // SETTINGS_MAX_HEADER_LIST_SIZE is advisory; unknown settings are ignored.
                break;
        }
    }

    private async Task HandlePingAsync(Http2Frame frame)
    {
        if (frame.StreamId != 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "PING on a stream.");
        }

        if (frame.Payload.Length != 8)
        {
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "PING isn't 8 octets.");
        }

        if (!frame.Has(Http2FrameFlags.Ack))
        {
            await TryWriteAsync(Http2FrameCodec.Ping(frame.Payload, ack: true));
        }
    }

    private void HandleGoAway(Http2Frame frame)
    {
        if (frame.StreamId != 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "GOAWAY on a stream.");
        }

        if (frame.Payload.Length < 8)
        {
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "GOAWAY is too short.");
        }

        var lastStreamId = BinaryPrimitives.ReadInt32BigEndian(frame.Payload) & int.MaxValue;
        lock (_sync)
        {
            _goAwayLastStreamId = lastStreamId;

            // Streams we opened that the peer won't process can be retried elsewhere.
            foreach (var stream in _streams.Values.Where(s => !IsPeerInitiated(s.Id) && s.Id > lastStreamId).ToList())
            {
                stream.IsReset = true;
                stream.Fail(new Http2StreamClosedException($"The server won't process stream {stream.Id}.", Http2ErrorCode.RefusedStream));
                RemoveStream(stream);
            }

            SignalWindows();
        }
    }

    private void HandleWindowUpdate(Http2Frame frame)
    {
        if (frame.Payload.Length != 4)
        {
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "WINDOW_UPDATE isn't 4 octets.");
        }

        var increment = BinaryPrimitives.ReadInt32BigEndian(frame.Payload) & int.MaxValue;
        lock (_sync)
        {
            if (frame.StreamId == 0)
            {
                if (increment == 0)
                {
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "A connection WINDOW_UPDATE of 0.");
                }

                _connectionSendWindow += increment;
                if (_connectionSendWindow > Http2FrameCodec.MaxWindowSize)
                {
                    throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, "The connection window overflowed.");
                }
            }
            else
            {
                var stream = FindActiveStream(frame.StreamId, frame.Type);
                if (stream is null)
                {
                    return;
                }

                if (increment == 0)
                {
                    throw new Http2StreamException(stream.Id, Http2ErrorCode.ProtocolError, $"A WINDOW_UPDATE of 0 on stream {stream.Id}.");
                }

                stream.SendWindow += increment;
                if (stream.SendWindow > Http2FrameCodec.MaxWindowSize)
                {
                    throw new Http2StreamException(stream.Id, Http2ErrorCode.FlowControlError, $"Stream {stream.Id}'s window overflowed.");
                }
            }

            SignalWindows();
        }
    }

    /// <summary>
    /// The active stream a frame is for; null when the frame should be
    /// ignored (the stream was reset by us, or is closed and the frame is one
    /// that may still arrive after closing). Throws for frames the RFC
    /// forbids on idle or closed streams. Call under the lock.
    /// </summary>
    private Http2Stream? FindActiveStream(int streamId, Http2FrameType type)
    {
        if (_streams.TryGetValue(streamId, out var stream))
        {
            return stream;
        }

        if (IsIdle(streamId))
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"{type} on idle stream {streamId}.");
        }

        if (_resetByUs.Contains(streamId) || type == Http2FrameType.WindowUpdate)
        {
            return null;
        }

        throw new Http2ConnectionException(Http2ErrorCode.StreamClosed, $"{type} on closed stream {streamId}.");
    }

    private bool IsPeerInitiated(int streamId) => _role == Http2Role.Server ? streamId % 2 == 1 : streamId % 2 == 0;

    private bool IsIdle(int streamId) => IsPeerInitiated(streamId) ? streamId > _lastPeerStreamId : streamId >= _nextLocalStreamId;

    private async Task ResetFromErrorAsync(Http2StreamException ex)
    {
        lock (_sync)
        {
            if (_streams.TryGetValue(ex.StreamId, out var stream))
            {
                MarkResetByUs(stream, new Http2StreamClosedException(ex.Message, ex.Code));
            }
            else
            {
                RememberReset(ex.StreamId);
            }
        }

        await TryWriteAsync(Http2FrameCodec.RstStream(ex.StreamId, ex.Code));
    }

    private void MarkResetByUs(Http2Stream stream, Exception reason)
    {
        stream.IsReset = true;
        stream.Fail(reason);
        RemoveStream(stream);
        RememberReset(stream.Id);
        SignalWindows();
    }

    private void RememberReset(int streamId)
    {
        if (_resetByUs.Add(streamId))
        {
            _resetOrder.Enqueue(streamId);
            if (_resetOrder.Count > RememberedResets)
            {
                _resetByUs.Remove(_resetOrder.Dequeue());
            }
        }
    }

    /// <summary>
    /// Marks our side ended, just before the frame that ends it is written.
    /// A stream that's then done frees its slot now rather than after the
    /// write: once the frame is out, the peer may open another stream
    /// straight away, and has to find the slot free. Call under the lock.
    /// </summary>
    private void EndLocalSide(Http2Stream stream)
    {
        stream.LocalClosed = true;
        RemoveIfDone(stream);
    }

    private void RemoveIfDone(Http2Stream stream)
    {
        if (stream.LocalClosed && stream.RemoteClosed)
        {
            RemoveStream(stream);
        }
    }

    private void RemoveStream(Http2Stream stream)
    {
        if (!_streams.Remove(stream.Id))
        {
            return;
        }

        if (IsPeerInitiated(stream.Id))
        {
            _activePeerStreams--;
        }
        else
        {
            _activeLocalStreams--;
            SignalWindows();
        }
    }

    private void SignalWindows()
    {
        var changed = _windowChanged;
        _windowChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private void ThrowIfUnusable()
    {
        if (_closed || _shutdown.IsCancellationRequested)
        {
            throw new Http2StreamClosedException("The HTTP/2 connection has closed.");
        }

        if (_goAwayLastStreamId is not null && _role == Http2Role.Client)
        {
            throw new Http2StreamClosedException("The server is closing the connection.", Http2ErrorCode.RefusedStream);
        }
    }

    private static void ThrowIfCantSend(Http2Stream stream)
    {
        if (stream.IsReset)
        {
            throw new Http2StreamClosedException($"Stream {stream.Id} was reset.");
        }

        if (stream.LocalClosed)
        {
            throw new InvalidOperationException($"Stream {stream.Id} has already ended its side.");
        }
    }

    private static int HeaderListSize(List<(string Name, string Value)> headers) =>
        headers.Sum(h => HpackText.EntrySize(h.Name, h.Value));

    private void CloseAll(Exception? reason)
    {
        lock (_sync)
        {
            _closed = true;
            _closedBecause = reason;
            foreach (var stream in _streams.Values.ToList())
            {
                stream.IsReset = true;
                stream.Fail(new Http2StreamClosedException($"The connection closed before stream {stream.Id} finished."));
            }

            _streams.Clear();
            SignalWindows();
        }

        _accepted.Writer.TryComplete();
        _peerSettingsApplied.TrySetResult();
    }

    private async Task WriteAsync(byte[] frames, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await WriteUnlockedAsync(frames, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes, ignoring a transport that has already gone: the read loop notices that on its own.</summary>
    private async Task TryWriteAsync(byte[] frames, CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteAsync(frames, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task WriteUnlockedAsync(byte[] frames, CancellationToken cancellationToken)
    {
        await _transport.WriteAsync(frames, cancellationToken);
        await _transport.FlushAsync(cancellationToken);
    }
}
