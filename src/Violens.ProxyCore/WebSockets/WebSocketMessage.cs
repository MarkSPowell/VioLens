using System.Buffers.Binary;
using System.Text;

namespace Violens.ProxyCore.WebSockets;

/// <summary>Which way a WebSocket message went.</summary>
public enum WebSocketDirection
{
    /// <summary>From the client (the browser or app) to the server.</summary>
    Sent,

    /// <summary>From the server to the client.</summary>
    Received,
}

/// <summary>What kind of WebSocket message it is (RFC 6455 opcodes).</summary>
public enum WebSocketMessageKind
{
    Text = 1,
    Binary = 2,
    Close = 8,
    Ping = 9,
    Pong = 10,
}

/// <summary>
/// One WebSocket message as VioLens saw it go by: a whole text or binary
/// message (put back together if it was sent in fragments, and
/// decompressed if permessage-deflate was in use), or a control message.
/// </summary>
/// <param name="Number">1 for the connection's first message, counting both directions.</param>
/// <param name="Length">The message's size on the wire, before decompression.</param>
/// <param name="Payload">The message's content, decompressed. Cut short when <see cref="Truncated"/>.</param>
/// <param name="Compressed">Whether it was sent compressed (permessage-deflate).</param>
/// <param name="Truncated">Whether <see cref="Payload"/> holds only the start of a very large message.</param>
/// <param name="Note">Something worth knowing about this message, such as that it couldn't be decompressed.</param>
public sealed record WebSocketMessage(
    int Number,
    WebSocketDirection Direction,
    DateTimeOffset Time,
    WebSocketMessageKind Kind,
    long Length,
    byte[] Payload,
    bool Compressed = false,
    bool Truncated = false,
    string? Note = null)
{
    /// <summary>The payload as UTF-8 text (invalid bytes become U+FFFD).</summary>
    public string Text => Encoding.UTF8.GetString(Payload);

    /// <summary>A Close message's status code, if it has one.</summary>
    public int? CloseCode =>
        Kind == WebSocketMessageKind.Close && Payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(Payload) : null;

    /// <summary>What a Close status code means (RFC 6455 section 7.4.1), or null for one it doesn't define.</summary>
    public static string? CloseCodeName(int code) => code switch
    {
        1000 => "normal closure",
        1001 => "going away",
        1002 => "protocol error",
        1003 => "unsupported data",
        1005 => "no status given",
        1006 => "closed abnormally",
        1007 => "invalid data",
        1008 => "policy violation",
        1009 => "message too big",
        1010 => "extension required",
        1011 => "server error",
        1012 => "service restart",
        1013 => "try again later",
        1014 => "bad gateway",
        1015 => "TLS handshake failed",
        _ => null,
    };

    /// <summary>A Close message's reason text, if it has one.</summary>
    public string? CloseReason =>
        Kind == WebSocketMessageKind.Close && Payload.Length > 2 ? Encoding.UTF8.GetString(Payload, 2, Payload.Length - 2) : null;
}

/// <summary>
/// The messages of one WebSocket connection, added as they pass through
/// (from the capture threads) and read by the UI. Attached to the session
/// whose request opened the connection (status 101).
/// </summary>
public sealed class WebSocketLog
{
    /// <summary>
    /// Message content kept in total before only sizes are kept, so a busy,
    /// long-lived connection can't use up memory.
    /// </summary>
    public const long MaxStoredBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Messages recorded before later ones are only counted, so a stream
    /// of tiny messages can't use up memory either.
    /// </summary>
    internal const int MaxStoredMessages = 100_000;

    private readonly object _gate = new();
    private readonly List<WebSocketMessage> _messages = [];
    private long _storedBytes;
    private long _notRecorded;
    private DateTimeOffset? _closedAt;
    private string? _problem;

    public WebSocketLog(DateTimeOffset openedAt, string? extensions = null)
    {
        OpenedAt = openedAt;
        Extensions = extensions;
    }

    /// <summary>When the connection switched to WebSocket.</summary>
    public DateTimeOffset OpenedAt { get; }

    /// <summary>The extensions the server agreed to (Sec-WebSocket-Extensions), if any.</summary>
    public string? Extensions { get; }

    /// <summary>Raised, on the capture thread, after each message is added.</summary>
    public event Action<WebSocketMessage>? MessageAdded;

    /// <summary>Raised, on the capture thread, when the connection ends.</summary>
    public event Action? Closed;

    /// <summary>The messages so far, oldest first.</summary>
    public IReadOnlyList<WebSocketMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    /// <summary>When the connection ended, or null while it's still open.</summary>
    public DateTimeOffset? ClosedAt
    {
        get
        {
            lock (_gate)
            {
                return _closedAt;
            }
        }
    }

    public bool IsOpen => ClosedAt is null;

    /// <summary>
    /// Why the messages stopped being followed before the connection ended
    /// (the bytes no longer looked like WebSocket frames), or null.
    /// Traffic still passes through either way.
    /// </summary>
    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                return _problem;
            }
        }
    }

    /// <summary>Adds a message, numbering it. Its content is dropped (only its size kept) once <see cref="MaxStoredBytes"/> is reached.</summary>
    public WebSocketMessage Add(
        WebSocketDirection direction,
        WebSocketMessageKind kind,
        long length,
        byte[] payload,
        bool compressed = false,
        bool truncated = false,
        string? note = null,
        DateTimeOffset? time = null)
    {
        WebSocketMessage message;
        lock (_gate)
        {
            if (_messages.Count >= MaxStoredMessages)
            {
                // Still numbered and returned, so callers carry on as usual,
                // but not kept or announced.
                _notRecorded++;
                _problem ??= $"Only the first {MaxStoredMessages:N0} messages were recorded; the connection carried on, and later messages passed through unrecorded.";
                return new WebSocketMessage(
                    (int)Math.Min(_messages.Count + _notRecorded, int.MaxValue), direction, time ?? DateTimeOffset.Now,
                    kind, length, [], compressed, payload.Length > 0, note);
            }

            if (_storedBytes + payload.Length > MaxStoredBytes && payload.Length > 0)
            {
                note ??= "Not kept: this connection's messages reached the size kept for each connection.";
                payload = [];
                truncated = true;
            }

            _storedBytes += payload.Length;
            message = new WebSocketMessage(
                _messages.Count + 1, direction, time ?? DateTimeOffset.Now, kind, length, payload, compressed, truncated, note);
            _messages.Add(message);
        }

        MessageAdded?.Invoke(message);
        return message;
    }

    /// <summary>Messages that passed through after <see cref="MaxStoredMessages"/> were recorded.</summary>
    internal long NotRecorded
    {
        get
        {
            lock (_gate)
            {
                return _notRecorded;
            }
        }
    }

    /// <summary>Records that the messages can't be followed any more (see <see cref="Problem"/>).</summary>
    public void ReportProblem(string problem)
    {
        lock (_gate)
        {
            _problem ??= problem;
        }
    }

    /// <summary>Marks the connection as ended.</summary>
    public void Close(DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            if (_closedAt is not null)
            {
                return;
            }

            _closedAt = at ?? DateTimeOffset.Now;
        }

        Closed?.Invoke();
    }
}
